---
name: createfile-tool-design-sketch
description: Design sketch (now IMPLEMENTED) for the CreateFile MCP tool (skeleton file creation only) to replace WriteFile for eval sessions where WriteFile/ApplyDiff/ApplyUnifiedDiff are intentionally disabled
metadata:
  type: project
---

## STATUS: Implemented 2026-09-09

Implemented in `SentinelWorkspaceTools.cs` per the sketch below, with one deliberate deviation:
`typeKind`/`typeName` ended up **mandatory** for `.cs` files, not optional as originally sketched.
Decided live during implementation — the user's rationale: an optional typeKind just relocates the
failure mode this tool exists to close (a model omits it, gets a bare-namespace file, then needs a
second `Member(add)` round-trip to reach a populatable type — the same friction as the original
`WriteFile`-unavailable bug, just one tool later). Mandatory for `.cs` files closes that path
entirely; a file needing a 2nd+ top-level type still uses `Member(add, containerName: null, ...)`
for those additional types, same as before. A `NewTypeKind` enum
(`class`/`record`/`interface`/`enum`/`struct`) was added to `RoslynSentinel.Common/ToolEnums.cs` for
this. Tests added/passing (22/22) in `RoslynSentinel.Tests.Battery/CreateFileDeleteFileTests.cs`,
including the full CreateFile → Member(add, populate member) → Member(add, 2nd top-level type)
integration scenario.

The original sketch below (kept for history) proposed content-building rules that are now partially
superseded by the above — namespaceName alone is no longer a valid `.cs`-file call.

## Why

WriteFile, ApplyDiff, and ApplyUnifiedDiff are intentionally disabled for model-eval sessions —
models struggle with whole-file rewrites/large diffs and frequently fail to reproduce a file's
full content correctly. But this leaves **no way to create a brand-new file at all**: `Member`,
`ModifyEnum`, `ReplaceSnippet`, etc. all require an already-existing Document in the workspace.

Confirmed root cause via transcript `04-phase1-tests.md - 2026-09-09 08.18.md`: the plan step
required creating `RoslynSentinel.Tests.Battery/BuildEngineTests.cs` (a new file). The model tried
`Member(add)` pointed at the nonexistent path → `DocumentNotFound`. It correctly recalled that
`WriteFile` was the right tool (it had already seen `WriteFile(operation=ReplaceFile)` mentioned
verbatim in an unrelated `ReplaceSnippet` size-limit error message, twice), reasoned "but I don't
see it" (i.e. it's not in its tool list), and then degenerated into a ~29-iteration verbatim
repetition loop retrying the same failing `Member` call, never resolving, never reaching the
phase's Build/RunTest gate.

This is a real, load-bearing tool gap for any plan step that requires new files — not just this
one eval.

## Proposed fix

A new `CreateFile` MCP tool that ONLY creates a new file with minimal/empty scaffold content —
never a whole-file replace. This closes the file-creation gap without reopening the
large-rewrite failure mode WriteFile/ApplyDiff were disabled to avoid.

### Signature (sketch)

```csharp
[McpServerTool(Name = "CreateFile")]
[Produces(DataTag.ChangeId)]
[Description("Creates a new, empty (or minimal-skeleton) file. Fails if the file already exists — " +
    "use Member(add) to add types/members to it afterward, this tool never writes whole-file content. " +
    "For a .cs file, pass namespaceName to seed a valid compilation unit ('namespace X;' with no types) " +
    "so Member(add, containerName: null) can immediately add the first top-level type into it.")]
public async Task<ToolResult<object>> CreateFile(
    [Description(ToolParams.Reason)] string reason,
    [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
    [Description("Namespace to seed the file with (e.g. 'RoslynSentinel.Tests.Battery'). Only meaningful for .cs files; ignored otherwise.")] string? namespaceName = null,
    CancellationToken cancellationToken = default)
```

### Body — mostly forked from WriteFile (SentinelWholeFileWriteTools.cs:31-106)

1. Reuse the `exists` check from `WriteFile`'s `CreateFile` branch (lines 42-50) — fail if the
   file already exists, telling the model this tool is create-only (no ReplaceFile equivalent;
   that's the whole point).
2. Reuse the parent-directory creation (`Directory.CreateDirectory`, line 61-65).
3. **Content is NOT a parameter** — that's the entire safety property versus WriteFile. Build it
   server-side:
   - `.cs` file + `namespaceName` given → `$"namespace {namespaceName};\n"` (file-scoped namespace,
     matching this codebase's own convention — grep confirms `FileScopedNamespaceDeclarationSyntax`
     is the common case here).
   - `.cs` file + no `namespaceName` → just an empty string, or reject and require namespaceName
     for `.cs` files specifically (decide based on whether "no namespace" files are ever wanted —
     probably reject, since every real file in this repo has one).
   - non-`.cs` file → empty string unconditionally; `namespaceName` ignored.
4. Route through the SAME chokepoint as WriteFile: `_workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: ...)`
   — every .cs write must go through this, no exceptions, so this preserves
   drift-detection/undo-tracking automatically.
   - Since content is just `namespace X;`, `validateChanges` can likely default to `true` with no
     practical downside (a bare namespace declaration can't introduce a compiler error).
5. Call `_workspaceTools.WriteBlobForApplyAsync("create_file", result)` same as WriteFile does,
   for operation-log/undo consistency.
6. Strip `PreImages` from the response same as WriteFile (line 90) — no pre-image exists anyway
   for a brand-new file, but match the shape for consistency.

### Naming collision to be aware of

`WriteFileOperation.CreateFile` (RoslynSentinel.Common/ToolEnums.cs:60) is an existing ENUM VALUE
name (one of WriteFile's two operations). The new tool being named `CreateFile` is a different
namespace (MCP tool name vs C# enum member) so there's no actual compile conflict — but don't
let the coincidence cause confusion when searching the codebase for "CreateFile" during
implementation; you'll find both.

### Where to put it

Decided: goes in `SentinelWorkspaceTools.cs`, NOT `SentinelWholeFileWriteTools.cs`. Keeps it off
the disabled-by-default whole-file-write surface (WriteFile/ApplyDiff/ApplyUnifiedDiff) both in
class grouping and in whatever runtime allowlist keys off that file/class — consistent with
CreateFile's risk profile being narrow/safe rather than large-blast-radius. Check
`SentinelWorkspaceTools.cs`'s existing constructor DI params before adding — it likely already
has `IWorkspaceManager`/`_workspaceTools`-equivalent access, but confirm `ApplyProposedChangesAsync`
and `WriteBlobForApplyAsync` (or their equivalents in scope there) are reachable without pulling
in `SentinelWholeFileWriteTools` as a dependency.

### Test plan (mirror CodeEditingTests.cs / DocumentationToolsTests.cs conventions)

- CreateFile succeeds for a new path, file exists on disk afterward with expected seeded content.
- CreateFile fails (InvalidArgument-style) if the file already exists — does NOT overwrite.
- CreateFile + namespaceName on a .cs file produces a file that parses as a valid empty
  compilation unit (round-trip through `CSharpSyntaxTree.ParseText` with zero diagnostics).
- Follow-up `Member(add, containerName: null, newMemberSource: "public class Foo { }")` against
  the freshly created file succeeds — this is the actual end-to-end scenario that was blocked in
  the eval transcript. This is the critical integration test: CreateFile alone is useless if
  Member can't then populate it.
- Parent directory auto-creation (mirror WriteFile's behavior, line 61-65).

### NOT in scope for this tool

- No `ReplaceFile`-equivalent operation. No `content` parameter at all. If a future need for
  "create with specific starter content" emerges, that's a deliberate design conversation, not an
  accidental reopening of the whole-file-rewrite footgun.
- No changes to WriteFile/ApplyDiff/ApplyUnifiedDiff's disabled status — those stay off for
  evals; CreateFile is additive only.

## Gating mechanism — CONFIRMED by reading source (2026-09-09)

Verified directly in `ServiceRegistrationExtensionsBasic.cs:123-131`: `SentinelWholeFileWriteTools`
(WriteFile/ApplyDiff/ApplyUnifiedDiff/DeleteFile) is registered in DI only when
`activeModes.Contains("Admin") || activeModes.Contains("WholeFileWrite")` — i.e. gated by the
server's `--mode`/`--modes` startup arg (parsed in `ServerStartupHelpers.ParseArgs`), specifically
the `WholeFileWrite` toolset name. A code comment there confirms it's deliberately excluded from
the `AllModes`/`"all"` expansion, so it only activates via an explicit `--mode=WholeFileWrite` (or
`=Admin`).

`SentinelWorkspaceTools` registers under a separate, near-certainly-always-on mode name
(`"Workspace"`, same file ~line 103) — confirming `CreateFile`'s placement there means it will be
visible in eval sessions regardless of whether `WholeFileWrite` is active. Still worth a quick
sanity check at implementation time (start the eval server with `--mode` excluding `WholeFileWrite`
and confirm `CreateFile` still appears in the live tools/list dump) rather than assuming class
placement alone guarantees it.
