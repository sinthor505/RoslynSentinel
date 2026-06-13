<!-- spec-symbol-handle-impl-v1.md -->
# Spec — SymbolHandle Wire Integration
**Version:** v1
**Date:** 2026-06-13
**Status:** Ready for implementation
**Gates:** All mutation tools that currently accept contextSnippet/lineBefore/lineAfter for symbol identification

---

## Background (do not re-research)

The current symbol identification approach passes `contextSnippet` + `lineBefore` + `lineAfter` + `filePath` + `symbolName` as tool parameters, then uses `IndexOf`/`FindIdentifierInSnippet` inside engine methods to locate the symbol in source. This is fragile, redundant across many methods, and requires the model to reconstruct and re-pass textual state it does not own.

The replacement: `locate_symbol` returns a `docCommentId` (e.g. `M:Avaal.Carriers.EDIOrdersListForm.searchAsync(System.Threading.CancellationToken)`) which is deterministic from source structure. Consuming tools accept `sessionId` + `projectName` + `docCommentId` as flat primitive parameters. A boundary helper collapses these into a typed `SymbolHandle`, resolves the live `ISymbol`, and returns a typed result. Engine methods receive a resolved `ISymbol` directly — the snippet chain is eliminated.

`contextSnippet` remains in `locate_symbol` input only — it plays to model strengths during initial discovery. It is not a continuation mechanism and must not be threaded into downstream tool parameters.

---

## Constraints

- `ISymbol` is never cached across compilation snapshots. Always resolve live from current compilation via `GetCompilationAsync`.
- `sessionId` is not load-bearing for `docCommentId` validity. `docCommentId` is deterministic from source structure. Null resolution from `GetFirstSymbolForDeclarationId` is the staleness detector — no additional mechanism needed.
- Do not hard-invalidate on session change. Emit a stale-session error only when `IsCurrentSession` returns false.
- `SymbolHandle` struct construction follows the same pattern as `FilePath`: flat primitive params at the MCP wire boundary, struct constructed as the first substantive line of the tool method body.
- `[Description]` attribute arguments must be compile-time constants. Use `const string` fields in a shared `ToolParams` static class. Do not use `static readonly`.
- `SymbolKey` is internal Roslyn API — do not use it.

---

## Deliverables

### 1. `SymbolHandle` struct

New file. Location: same assembly as `FilePath` (shared types layer).

```csharp
// v1
public readonly struct SymbolHandle
{
    public string SessionId { get; init; }
    public string ProjectName { get; init; }
    public string DocCommentId { get; init; }

    public SymbolHandle(string sessionId, string projectName, string docCommentId)
    {
        SessionId = sessionId;
        ProjectName = projectName;
        DocCommentId = docCommentId;
    }
}
```

No `default(SymbolHandle)` guard required at this stage — the tool-layer validation below catches empty strings before the struct is used.

---

### 2. `SymbolResolution` result type

New file, same assembly as `SymbolHandle`.

```csharp
// v1
public readonly struct SymbolResolution
{
    public ISymbol? Symbol { get; init; }
    public SymbolHandle Handle { get; init; }
    public EngineError? Error { get; init; }

    public bool Resolved
    {
        get { return this.Error is null; }
    }
}
```

Do not use a bool return from `ResolveFromWireAsync`. The error distinction (StaleSession vs. SymbolNotResolved vs. ProjectNotFound) is required for agent recovery.

---

### 3. `PersistentWorkspaceManager.ResolveSymbolAsync`

Add to `PersistentWorkspaceManager`. Resolves a `SymbolHandle` to a live `ISymbol` against the current compilation. Called only from `ResolveFromWireAsync` — not directly from tool methods.

```csharp
// v1
public async Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken ct)
{
    var solution = await GetCurrentSolutionAsync();
    var project = solution.Projects.FirstOrDefault(p => p.Name == handle.ProjectName);
    if (project is null)
    {
        return null;
    }
    var compilation = await project.GetCompilationAsync(ct);
    return DocumentationCommentId.GetFirstSymbolForDeclarationId(handle.DocCommentId, compilation!);
}
```

---

### 4. `PersistentWorkspaceManager.ResolveFromWireAsync`

Add to `PersistentWorkspaceManager`. This is the single integration point for all symbol-accepting tool methods. Does: session validation → handle construction → live resolution → typed result.

```csharp
// v1
public async Task<SymbolResolution> ResolveFromWireAsync(
    string sessionId,
    string projectName,
    string docCommentId,
    CancellationToken ct)
{
    if (!this.IsCurrentSession(sessionId))
    {
        return new SymbolResolution
        {
            Error = new EngineError(
                EngineErrorCode.StaleSession,
                "Symbol handle is from a prior workspace session. Re-run locate_symbol.",
                DataTag.SymbolHandle)
        };
    }

    SymbolHandle handle = new SymbolHandle(sessionId, projectName, docCommentId);
    ISymbol? symbol = await this.ResolveSymbolAsync(handle, ct);

    if (symbol is null)
    {
        return new SymbolResolution
        {
            Handle = handle,
            Error = new EngineError(
                EngineErrorCode.SymbolNotResolved,
                $"Symbol '{docCommentId}' no longer resolves — may have been renamed, moved, or removed. Re-run locate_symbol.",
                DataTag.SymbolHandle)
        };
    }

    return new SymbolResolution { Symbol = symbol, Handle = handle };
}
```

`EngineErrorCode` must have `StaleSession` and `SymbolNotResolved` values. Add them if not present. Do not add `ProjectNotFound` as a separate code — null resolution from a missing project collapses into `SymbolNotResolved` at this boundary.

---

### 5. `SymbolLocation` record — add `ProjectName` and `DocCommentId`

`locate_symbol` returns `SymbolLocation` results. These two fields must be present for the consuming tool to construct a `SymbolHandle` without additional lookups.

Find the `SymbolLocation` record definition. Add:

```csharp
// v1 — additions to existing record
string ProjectName,
string? DocCommentId,
```

`DocCommentId` is nullable because `GetDocumentationCommentId()` can return null for anonymous types, lambdas, and certain compiler-generated symbols. When null, the result is still valid for display — the consuming tool must check for null before constructing a `SymbolHandle` and return a descriptive error if the agent attempts to use it as a handle.

Remove `ContextSnippet` from the `SymbolLocation` record if it is currently present as a return field. It was a disambiguation aid for the snippet-based approach and must not be emitted going forward.

---

### 6. `LocateSymbolAsync` — emit `docCommentId` and `projectName`

In `LocateSymbolAsync` (or wherever `SymbolLocation` results are constructed), make the following changes:

```csharp
// v1 — key additions; keep surrounding logic intact
// Call outside the location loop (once per symbol, not once per location):
var docCommentId = symbol.GetDocumentationCommentId();

// Add to SymbolLocation constructor call:
ProjectName: project.Name,
DocCommentId: docCommentId,
// Remove: ContextSnippet (if present)
```

`docCommentId` is per-symbol, not per-location. If the symbol has multiple source locations (partial classes, etc.), the same `docCommentId` value goes into every `SymbolLocation` result for that symbol.

---

### 7. Shared `ToolParams` constants

Add a `ToolParams` static class (or add to an existing one if it exists). These are `const string` values used in `[Description(...)]` attributes across all symbol-accepting tool signatures.

```csharp
// v1
internal static class ToolParams
{
    public const string SessionId =
        "Session ID returned by load_solution. Used to detect workspace reload. " +
        "Pass the value exactly as returned — do not construct or modify.";

    public const string ProjectName =
        "Project name returned by locate_symbol in the projectName field. " +
        "Must match exactly — case-sensitive.";

    public const string DocCommentId =
        "Documentation comment ID returned by locate_symbol in the docCommentId field. " +
        "Uniquely identifies the symbol across tool calls. " +
        "Do not construct this value — pass it exactly as returned by locate_symbol.";
}
```

These constants replace bespoke `[Description]` prose in every tool method. If the class already exists under a different name, add to it rather than creating a duplicate.

---

### 8. Pilot tool — `rename_symbol`

Migrate `rename_symbol` as the pilot. Do not migrate other tools until this one builds, runs, and resolves a symbol correctly.

**Tool method signature (after migration):**

```csharp
// v1
[McpServerTool]
[Description("Renames a symbol and all its references across the solution. " +
             "Requires a symbol handle from locate_symbol (sessionId, projectName, docCommentId).")]
public async Task<string> RenameSymbol(
    [Description(ToolParams.SessionId)] string sessionId,
    [Description(ToolParams.ProjectName)] string projectName,
    [Description(ToolParams.DocCommentId)] string docCommentId,
    [Description("New name for the symbol. Must be a valid C# identifier.")] string newName,
    CancellationToken ct = default)
{
    SymbolResolution resolution = await _workspaceManager.ResolveFromWireAsync(
        sessionId, projectName, docCommentId, ct);
    if (!resolution.Resolved)
    {
        return resolution.Error!.ToToolResponse();
    }

    RenameSymbolResult result = await _engine.RenameSymbolAsync(
        resolution.Handle, resolution.Symbol!, newName, ct);
    return result.ToToolResponse();
}
```

Remove: `contextSnippet`, `lineBefore`, `lineAfter`, `filePath`, `symbolName` parameters from this tool's signature. Remove `symbolName` from the engine method signature as well — the engine receives `SymbolHandle` + already-resolved `ISymbol`.

**Engine method `RenameSymbolAsync` signature (after migration):**

```csharp
// v1
public async Task<RenameSymbolResult> RenameSymbolAsync(
    SymbolHandle handle,
    ISymbol symbol,
    string newName,
    CancellationToken ct = default)
```

The engine no longer calls `ResolveSymbolAsync` — the tool layer already resolved and passed the `ISymbol`. The engine trusts the handle and the symbol. Remove any `FindIdentifierInSnippet`, `IndexOf`, or context-snippet logic from the engine method body.

**`UpdatedHandle` in `RenameSymbolResult`:**

After `Renamer.RenameSymbolAsync` completes, resolve the renamed symbol from the updated `Solution` snapshot to emit an `UpdatedHandle`. The agent uses this handle in subsequent tool calls without re-running `locate_symbol`.

The updated symbol must be resolved by location (original source span translated into the updated solution), not by naive string replacement on `docCommentId`. The rename changes the symbol name, so the old `docCommentId` will not resolve in the updated compilation.

Resolution approach: from the updated `Solution`, get the updated document for the file that contained the original symbol location, get the `SyntaxRoot`, find the renamed identifier node at the original character position (the span shifts predictably for in-file renames), call `GetDeclaredSymbol` via the updated semantic model, then call `GetDocumentationCommentId()` on the result.

If this resolution fails (e.g. multi-file partial class rename where the primary declaration location is ambiguous), emit `UpdatedHandle = null` in the result and include a note in the response that the agent must re-run `locate_symbol` before further operations on this symbol. Do not error — the rename succeeded.

```csharp
// v1 — UpdatedHandle field on RenameSymbolResult
public SymbolHandle? UpdatedHandle { get; init; }
```

---

## Sequencing

Execute in this order. Do not proceed to the next step until the current one compiles and (where indicated) produces verified output.

1. Add `EngineErrorCode.StaleSession` and `EngineErrorCode.SymbolNotResolved` if not present.
2. Create `SymbolHandle` struct.
3. Create `SymbolResolution` struct.
4. Add `ResolveSymbolAsync` to `PersistentWorkspaceManager`.
5. Add `ResolveFromWireAsync` to `PersistentWorkspaceManager`.
6. Add `ProjectName` and `DocCommentId` to `SymbolLocation` record. Remove `ContextSnippet` from record if present.
7. Update `LocateSymbolAsync` to emit `docCommentId` and `projectName`. Verify `locate_symbol` output contains both fields on a real symbol before proceeding.
8. Add `ToolParams` constants class.
9. Migrate `rename_symbol` tool and `RenameSymbolAsync` engine method per Deliverable 8. Build and verify resolution works end-to-end.
10. Migrate remaining symbol-accepting mutation tools following the same pattern as step 9.

---

## Do Not Do

- Do not use `SymbolKey` — it is internal Roslyn API.
- Do not cache `ISymbol` instances across compilation snapshots.
- Do not add `contextSnippet`, `lineBefore`, `lineAfter` to any new or migrated tool signature.
- Do not pass `filePath` to engine methods for the purpose of symbol resolution — `docCommentId` + `projectName` is sufficient.
- Do not hard-fail on session mismatch by throwing — return `EngineError.ToToolResponse()`.
- Do not migrate all tools before the pilot (`rename_symbol`) is verified working.
- Do not attempt to implement `UpdatedHandle` resolution via string manipulation on the old `docCommentId` string.
- Do not add `sessionId` hard-invalidation that prevents resolution — null from `GetFirstSymbolForDeclarationId` is the staleness check, not session comparison.
- Do not skip the build step between sequencing items.

---

## Open Question (deferred — do not implement)

`sessionId` hard-invalidation: current decision is soft (warn, don't block). This is confirmed policy for this spec. Do not add hard-invalidation logic. Flag in a `// TODO: revisit after empirical agent testing` comment on `IsCurrentSession` call site only.