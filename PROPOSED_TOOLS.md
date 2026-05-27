# Proposed New Tools / Enhancements for RoslynSentinel

Spec generated 2026-05-26. Paste this into a fresh chat along with the RoslynSentinel workspace context.

---

## Context

These proposals come from hands-on use during the **Avaal Express async migration** (Phase 11 — adding `CancellationToken` to 152 methods across 52 files).
Three pain points drove this list:

1. `add_cancellation_token_to_method` returns raw source and processes one method at a time → required complex PS1 regex scripting for batch work.
2. After bulk file edits via PS1, the Roslyn workspace held stale in-memory state → `find_missing_cancellation_tokens` returned wrong counts until `load_solution` was called manually.
3. No way to query CS0618 call sites of `[Obsolete]` bridge wrappers → migration progress tracking required grep or per-method `vscode_listCodeUsages`.

---

## Proposal 1 — `apply_cancellation_token_to_file` (HIGH priority)

### Problem

`add_cancellation_token_to_method` (line 419 of `SentinelQualityTools.cs`):
- Processes **one method at a time**
- Returns the updated source as a **raw string** — does NOT write to disk
- Caller must manually call `apply_proposed_changes` with the returned string

For 52 files × avg 3 methods each = 156 individual calls + 52 disk writes via `apply_proposed_changes`. This was impractical, so PS1 regex scripts were used instead.

### Proposed tool signature

```csharp
[McpServerTool]
[Description("Adds 'CancellationToken cancellationToken = default' as the last parameter to ALL eligible async methods in a file and propagates it to async callees. Eligible methods are: async Task/ValueTask methods that lack a CancellationToken parameter and have at least one callee that accepts one. Automatically skips: event handler methods (object sender, XxxEventArgs e), methods that already have a CancellationToken, and abstract/interface methods. Writes the result directly to disk. Returns a summary of modified and skipped methods.")]
public async Task<ApplyCancellationTokenToFileResult> ApplyCancellationTokenToFile(
    string filePath,
    string[]? methodNames = null)   // optional: restrict to specific methods; null = all eligible
```

### Return type

```csharp
public class ApplyCancellationTokenToFileResult
{
    public List<string> ModifiedMethods { get; set; }   // methods that had CT added
    public List<string> SkippedMethods  { get; set; }   // methods skipped and why
    public int TotalModified            { get; set; }
    public bool WroteToFile             { get; set; }
}
```

### Implementation notes

- Reuse the existing `AsyncOptimizationEngine.AddCancellationTokenToMethodAsync` for the per-method transformation.
- Run `find_missing_cancellation_tokens` logic (from `AntiPatternEngine`) internally to enumerate eligible methods in the file — respect the same event-handler exclusion (`IsEventHandlerSignature`).
- Apply all transformations sequentially on the same `SyntaxTree` (re-parse after each or build a combined rewriter).
- Write the final source to disk via `File.WriteAllTextAsync`.
- After writing, call `workspace.TryApplyChanges(...)` to update the in-memory workspace so subsequent tool calls see the new state.
- The `methodNames` filter, when provided, restricts processing to only the named methods (case-sensitive match on `MethodDeclarationSyntax.Identifier.Text`).

### Files to modify

- `RoslynSentinel.Server/AsyncOptimizationEngine.cs` — add `ApplyCancellationTokenToFileAsync(Document doc, string[]? methodNames)` returning `(string updatedSource, List<string> modified, List<string> skipped)`
- `RoslynSentinel.Server/SentinelQualityTools.cs` — add the `[McpServerTool]` registration method that calls the engine method, writes to disk, and returns the result DTO

---

## Proposal 2 — Workspace staleness indicator on `get_workspace_health` (HIGH priority)

### Problem

After Phase 11 wrote 52 files to disk via PS1 scripts, the Roslyn workspace held pre-Phase-11 in-memory versions.
`find_missing_cancellation_tokens` returned **392** stale results when the correct answer was ~17.
There was no signal that the workspace was stale — we only discovered the problem because the numbers didn't make sense.

### Proposed change

Extend the **existing** `GetWorkspaceHealth` tool (in `SentinelWorkspaceTools.cs`) to add staleness fields to the response object.

```csharp
public class WorkspaceHealthReport   // existing class, add new fields
{
    // ... existing fields ...

    public DateTime?     LastLoadedAt        { get; set; }  // UTC timestamp when load_solution last ran
    public int           StaleDocumentCount  { get; set; }  // docs where File.GetLastWriteTimeUtc > LastLoadedAt
    public bool          RequiresReload      { get; set; }  // true if StaleDocumentCount > 0
    public List<string>  SampleStaleFiles    { get; set; }  // up to 5 example stale paths for diagnostics
}
```

### Implementation notes

- Record `LastLoadedAt = DateTime.UtcNow` whenever `LoadSolutionAsync` completes successfully. Store in a static/singleton field on `PersistentWorkspaceManager` (or wherever the workspace is managed).
- In `GetWorkspaceHealth`, iterate `workspace.CurrentSolution.Projects.SelectMany(p => p.Documents)` and count documents where `File.Exists(doc.FilePath) && File.GetLastWriteTimeUtc(doc.FilePath) > LastLoadedAt`.
- Cap `SampleStaleFiles` at 5 entries to keep the response small.
- `RequiresReload = StaleDocumentCount > 0`.

### Files to modify

- `RoslynSentinel.Server/PersistentWorkspaceManager.cs` (or wherever the workspace singleton lives) — add `LastLoadedAt` tracking
- `RoslynSentinel.Server/SentinelWorkspaceTools.cs` — read staleness data and populate new fields in the response

---

## Proposal 3 — `find_obsolete_callers` (MEDIUM priority)

### Problem

The Asyncify-bridge pattern marks sync wrapper methods as `[Obsolete("Asyncify-bridge: call XxxAsync instead.", false)]`. CS0618 warnings at call sites are the migration tracking mechanism — they show which callers still use the old sync bridge and need to be migrated to `await XxxAsync(...)`.

There is currently no MCP tool to query these call sites. Options are:
- `vscode_listCodeUsages` — requires one call per bridge method, no filtering
- Grep for `CS0618` — requires parsing compiler output

### Proposed tool signature

```csharp
[McpServerTool]
[Description("Finds all call sites of [Obsolete]-tagged methods in the solution. Optionally filters by an obsolete message substring (e.g. 'Asyncify-bridge') to scope to migration bridge wrappers. Returns each call site with the obsolete method name, the obsolete message, caller method, file path, and line number. Useful for tracking async migration progress — the CS0618 warnings these generate are the primary migration tracking mechanism.")]
public async Task<List<ObsoleteCallerFinding>> FindObsoleteCallers(
    string? messagePattern  = null,   // substring match on the [Obsolete] message text; null = all [Obsolete] methods
    string? filePath        = null,
    string? projectName     = null)
```

### Return type

```csharp
public class ObsoleteCallerFinding
{
    public string ObsoleteMethodName    { get; set; }   // e.g. "search"
    public string ObsoleteMessage       { get; set; }   // e.g. "Asyncify-bridge: call searchAsync instead."
    public string DeclaringType         { get; set; }   // e.g. "CommonSearch"
    public string CallerMethod          { get; set; }   // e.g. "GetTrips"
    public string CallerType            { get; set; }   // e.g. "TripService"
    public string FilePath              { get; set; }
    public int    Line                  { get; set; }
    public string CodeSnippet           { get; set; }   // the call-site line text
}
```

### Implementation notes

- Use `SymbolFinder.FindReferencesAsync(symbol, solution)` for each method decorated with `[ObsoleteAttribute]`.
- Filter by `messagePattern` by checking `obsoleteAttr.ConstructorArguments[0].Value?.ToString()?.Contains(messagePattern)`.
- Scope by `filePath` or `projectName` when provided.
- This is the same pattern used by `find_callers_safe` — reuse the reference-finding infrastructure.

### Files to modify

- `RoslynSentinel.Server/AntiPatternEngine.cs` — add `FindObsoleteCallersAsync(Solution, string? messagePattern, string? filePath, string? projectName)`
- `RoslynSentinel.Server/SentinelQualityTools.cs` — add `[McpServerTool]` registration

---

## Proposal 4 — `add_cancellation_token_to_method` → add `autoStage` support (LOW priority)

### Problem

Every other refactoring tool (`add_field`, `rename_symbol`, `extract_method`, `add_modifier`, etc.) returns a `ChangeId` that can be:
- Inspected with `get_staged_changes`
- Validated with `validate_staged_changes`
- Applied with `apply_staged_changes`
- Discarded with `discard_staged_changes`

`add_cancellation_token_to_method` (line 419 of `SentinelQualityTools.cs`) returns the full updated source as a raw string. The caller must wrap it manually in `apply_proposed_changes({ filePath: source })`.

### Proposed change

Add `bool autoStage = true` parameter matching the pattern used by `add_field`, `add_property`, etc.

```csharp
// Current:
public async Task<string> AddCancellationTokenToMethod(string filePath, string methodName)

// Proposed:
public async Task<RefactoringResult> AddCancellationTokenToMethod(
    string filePath,
    string methodName,
    bool   autoStage = true)
```

When `autoStage = true`:
- Store the updated source in the staged-changes buffer (same mechanism as `rename_symbol` etc.)
- Return `{ changeId: "abc123", preview: "<first 10 changed lines>" }` instead of the raw source

When `autoStage = false`:
- Preserve the current behaviour — return the full updated source as a string in `{ source: "..." }`

### Files to modify

- `RoslynSentinel.Server/SentinelQualityTools.cs` — update `AddCancellationTokenToMethod` signature and return logic
- Reuse the existing staged-changes buffer infrastructure (already used by `rename_symbol`, `extract_method`, etc.)

---

## Bonus — `get_async_migration_progress` (informational)

A read-only stats dashboard to replace PS1 counting scripts.

```csharp
[McpServerTool]
[Description("Returns async migration progress statistics for the solution or a project: total async Task/ValueTask methods, how many have CancellationToken parameters, how many are Asyncify-bridge wrappers ([Obsolete(\"Asyncify-bridge:...\")] methods), remaining CS0618 obsolete call sites to migrate, and async void event handler count (informational). Use this to track overall migration progress without running individual analysis tools.")]
public async Task<AsyncMigrationProgressReport> GetAsyncMigrationProgress(
    string? projectName = null)
```

```csharp
public class AsyncMigrationProgressReport
{
    public int TotalAsyncMethods        { get; set; }   // async Task + async Task<T> + async ValueTask
    public int WithCancellationToken    { get; set; }
    public int WithoutCancellationToken { get; set; }
    public double CancellationTokenPct  { get; set; }   // WithCancellationToken / TotalAsyncMethods * 100
    public int BridgeWrappers           { get; set; }   // [Obsolete("Asyncify-bridge:...")] methods
    public int PendingObsoleteCallers   { get; set; }   // call sites of bridge wrappers (CS0618 sites)
    public int AsyncVoidEventHandlers   { get; set; }   // informational; not actionable
}
```

---

## Proposal 5 — Workspace-sync contract: add `WorkspaceInSync` to `ApplyChangesResult` (HIGH priority)

### Problem

After `apply_proposed_changes` or `apply_staged_changes` succeeds, the Roslyn in-memory workspace **is** updated (via `TryApplyChanges` inside `PersistentWorkspaceManager`). But `ApplyChangesResult` doesn't signal this:

```csharp
// Current:
public record ApplyChangesResult(
    bool Success,
    List<string> SucceededFiles,
    Dictionary<string, string> FailedFiles,
    string Summary
);
```

The agent has no way to know the workspace is now in sync — so it may defensively call `load_solution` after every apply, or may fail to call it when `TryApplyChanges` silently failed.

### Proposed change

```csharp
// Proposed:
public record ApplyChangesResult(
    bool Success,
    List<string> SucceededFiles,
    Dictionary<string, string> FailedFiles,
    string Summary,
    bool WorkspaceInSync,    // true = TryApplyChanges succeeded; workspace matches disk
    int  WorkspaceVersion    // monotonically increasing; lets agent detect concurrent external changes
);
```

- `WorkspaceInSync = true` → in-memory workspace matches disk; no reload needed before running analysis tools.
- `WorkspaceInSync = false` → `TryApplyChanges` failed (workspace was modified concurrently); caller should call `load_solution` before running analysis.
- `WorkspaceVersion` → incremented on every successful `TryApplyChanges`; exposes the existing internal workspace version counter. An agent comparing workspace version before/after can detect concurrent external mutations.

### Files to modify

- `RoslynSentinel.Server/PersistentWorkspaceManager.cs` — add `WorkspaceInSync` and `WorkspaceVersion` fields to `ApplyChangesResult`; populate in `ApplyProposedChangesAsync`, `ApplyStagedChangesAsync`, `RetryFailedChangesAsync`

---

## Proposal 6 — Standardize raw-source tool descriptions and return types (MEDIUM priority)

### Problem

**8 tools return raw source strings** — they compute the updated code but do NOT write to disk and do NOT update the in-memory workspace:

| Tool | Line | Description problem |
|---|---|---|
| `add_configure_await_false` | 366 | Says "Adds .ConfigureAwait(false)..." — sounds like it modifies |
| `remove_configure_await_false` | 378 | Says "Removes all .ConfigureAwait(x) calls..." — sounds like it modifies |
| `convert_lock_to_semaphore_slim` | 390 | Says "Converts lock statements..." — sounds like it converts |
| `convert_to_async_enumerable` | 402 | Says "Converts a method..." — sounds like it converts |
| `add_cancellation_token_to_method` | 419 | Correctly says "Returns the updated source" ✅ |
| `make_method_thread_safe` | 431 | Says "Adds a private lock object field and wraps..." — sounds like it modifies |
| `add_guard_clauses` | 104 | Description doesn't mention return behaviour |
| `add_benchmark_stub` | 141 | Description doesn't mention return behaviour |

The first time an agent uses `add_configure_await_false` it assumes the file was modified and moves on — the change is silently lost.

### Proposed fix: add standard disclaimer to all 8 descriptions

Append to each tool description:

> "Returns the updated source as a string. **Does not write to disk or update the workspace.** Pass the result to `apply_proposed_changes` to save: `apply_proposed_changes({ \"<filePath>\": <result> })`."

### Proposed fix: consistent return type wrapper

Replace bare `Task<string>` with a thin wrapper that makes the contract explicit in the JSON shape:

```csharp
public record SourceTransformResult(
    string UpdatedSource,        // the new file content
    bool   WroteToFile,          // always false for these tools
    bool   WorkspaceUpdated,     // always false for these tools
    string FilePath              // echo the input path (makes apply_proposed_changes call trivial)
);
```

This eliminates the ambiguity — `WroteToFile: false` is an unambiguous signal.

### Files to modify

- `RoslynSentinel.Server/SentinelQualityTools.cs` — update descriptions and return types for all 8 methods
- Add `SourceTransformResult` record to a shared DTOs file or inline in `SentinelQualityTools.cs`

---

## Proposal 7 — Rename / fix `apply_proposed_diff` (MEDIUM priority)

### Problem

`apply_proposed_diff` (`SentinelWorkspaceTools.cs` line 198) has a deeply misleading name:

```csharp
public async Task<string> ApplyProposedDiff(string filePath, string unifiedDiff)
```

It **does NOT write to disk**. It applies the diff to the in-memory source and returns the resulting string. Actual behaviour is identical to a preview/dry-run. But the name says "apply" — same verb used by `apply_proposed_changes` and `apply_staged_changes`, which **do** write to disk.

An agent seeing this tool will assume it writes. The result is either silently-lost changes or redundant work.

### Two options (pick one)

**Option A — Rename to `preview_proposed_diff`**
Make the name match the behaviour: returns the would-be content without writing.

```csharp
[Description("Applies a Unified Diff to a file in memory and returns the resulting full content as a string. Does NOT write to disk. Pass the returned string to apply_proposed_changes to save.")]
public async Task<SourceTransformResult> PreviewProposedDiff(string filePath, string unifiedDiff)
```

**Option B — Make it actually apply (write to disk)**
Change the implementation to call `ApplyProposedChangesAsync` with the result, returning `ApplyChangesResult` (consistent with the other apply tools). Rename kept as `apply_proposed_diff`.

```csharp
[Description("Applies a Unified Diff to a file and writes the result to disk. Returns ApplyChangesResult with SucceededFiles and WorkspaceInSync. To preview without writing, use validate_proposed_diff instead.")]
public async Task<ApplyChangesResult> ApplyProposedDiff(string filePath, string unifiedDiff)
```

**Recommendation**: Option B. The verb "apply" should always mean "write to disk". `validate_proposed_diff` (which already exists and returns diagnostics) covers the preview/dry-run use case.

### Files to modify

- `RoslynSentinel.Server/SentinelWorkspaceTools.cs` — change `ApplyProposedDiff` implementation and return type

---

## Implementation order recommendation

1. **Proposal 5** (workspace-sync contract) — zero new tools; touches 1 file; eliminates biggest source of silent failures
2. **Proposal 6** (description + return type standardization) — touching 8 descriptions; adding 1 record type; no logic changes
3. **Proposal 7** (`apply_proposed_diff` fix) — pick Option B; ~10 lines of logic change
4. **Proposal 2** (workspace staleness in `get_workspace_health`) — touches ~2 files; handles external-write detection
5. **Proposal 3** (`find_obsolete_callers`) — standalone new engine method, no existing code changes
6. **Proposal 4** (`autoStage` on existing method) — surgical signature change to one existing method
7. **Proposal 1** (`apply_cancellation_token_to_file`) — most complex; depends on existing per-method engine being solid
8. **Bonus** (`get_async_migration_progress`) — straightforward stats aggregation, implement last
2. **Proposal 3** (`find_obsolete_callers`) — standalone new engine method, no existing code changes
3. **Proposal 4** (`autoStage` on existing method) — surgical signature change to one existing method
4. **Proposal 1** (`apply_cancellation_token_to_file`) — most complex; depends on existing per-method engine being solid
5. **Bonus** (`get_async_migration_progress`) — straightforward stats aggregation, implement last

## Key files in RoslynSentinel

- `RoslynSentinel.Server/SentinelQualityTools.cs` — all `[McpServerTool]` registrations (83 tools)
- `RoslynSentinel.Server/AsyncOptimizationEngine.cs` — async transformation logic including `AddCancellationTokenToMethodAsync`
- `RoslynSentinel.Server/AntiPatternEngine.cs` — `FindMissingCancellationTokensAsync`, `IsEventHandlerSignature`
- `RoslynSentinel.Server/AnalysisEngine.cs` — `DetectMismatchedAwaitAsync`
- `RoslynSentinel.Server/PersistentWorkspaceManager.cs` — workspace singleton and load logic
- `RoslynSentinel.Server/SentinelWorkspaceTools.cs` — `GetWorkspaceHealth` and workspace management tools

## Build note

The MCP server process holds a file lock on `RoslynSentinel.Server.exe` while VS Code is running.
`dotnet build` will compile cleanly but fail on the final exe-copy step with MSB3027.
**Restart VS Code** before the final build to release the lock and deploy the new binary.
