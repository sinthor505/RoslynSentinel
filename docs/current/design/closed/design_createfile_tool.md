# Design: CreateFile tool

**Status:** Implemented 2026-09-09 — historical design record. For current usage policy (why
`WriteFile` is gated off and `CreateFile` is the replacement), see `CLOSED.md`'s "No tool for
creating or deleting a whole file" entry (2026-09-09 update).

## Why

`WriteFile`, `ApplyDiff`, and `ApplyUnifiedDiff` are intentionally disabled for model-eval
sessions — models struggle with whole-file rewrites/large diffs and frequently fail to reproduce a
file's full content correctly. But this left **no way to create a brand-new file at all**:
`Member`, `ModifyEnum`, `ReplaceSnippet`, etc. all require an already-existing Document in the
workspace.

Confirmed root cause via a real eval transcript: a plan step required creating a new test file. The
model tried `Member(add)` pointed at the nonexistent path → `DocumentNotFound`. It correctly
recalled that `WriteFile` was the right tool (it had seen `WriteFile(operation=ReplaceFile)`
mentioned in an unrelated `ReplaceSnippet` size-limit error message), reasoned "but I don't see it"
(not in its tool list), and degenerated into a ~29-iteration verbatim repetition loop retrying the
same failing `Member` call, never resolving. This is a real, load-bearing tool gap for any plan
step requiring new files, not a one-off eval quirk.

## Proposed fix (design)

A new `CreateFile` MCP tool that ONLY creates a new file with minimal/empty scaffold content —
never a whole-file replace. This closes the file-creation gap without reopening the large-rewrite
failure mode `WriteFile`/`ApplyDiff` were disabled to avoid.

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

### Body — forked from WriteFile (`SentinelWholeFileWriteTools.cs:31-106`)

1. Reuse the `exists` check from `WriteFile`'s `CreateFile` branch — fail if the file already
   exists, telling the model this tool is create-only (no `ReplaceFile` equivalent; that's the
   whole point).
2. Reuse the parent-directory creation (`Directory.CreateDirectory`).
3. **Content is NOT a parameter** — that's the entire safety property versus `WriteFile`. Built
   server-side:
   - `.cs` file + `namespaceName` given → `$"namespace {namespaceName};\n"` (file-scoped namespace,
     matching this codebase's own convention).
   - `.cs` file + no `namespaceName` → reject and require `namespaceName` for `.cs` files
     specifically.
   - non-`.cs` file → empty string unconditionally; `namespaceName` ignored.
4. Route through the same chokepoint as `WriteFile`:
   `_workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: ...)` — every `.cs`
   write must go through this, no exceptions, so this preserves drift-detection/undo-tracking
   automatically. Since content is just `namespace X;`, `validateChanges` can default to `true`
   with no practical downside (a bare namespace declaration can't introduce a compiler error).
5. Call the existing `WriteBlobForApplyAsync("create_file", result)` helper, same as `WriteFile`
   does, for operation-log/undo consistency.
6. Strip `PreImages` from the response, same as `WriteFile` — no pre-image exists for a brand-new
   file, but match the shape for consistency.

### Naming collision to be aware of

`WriteFileOperation.CreateFile` (`RoslynSentinel.Common/ToolEnums.cs:60`) is an existing enum value
name (one of `WriteFile`'s two operations). The new tool being named `CreateFile` is a different
namespace (MCP tool name vs. C# enum member) so there's no compile conflict — but don't let the
coincidence cause confusion when searching the codebase for "CreateFile."

### Where it lives

`SentinelWorkspaceTools.cs`, not `SentinelWholeFileWriteTools.cs` — keeps it off the disabled-by-
default whole-file-write surface (`WriteFile`/`ApplyDiff`/`ApplyUnifiedDiff`) both in class
grouping and in the runtime allowlist that keys off that class, consistent with `CreateFile`'s risk
profile being narrow/safe rather than large-blast-radius.

### Test plan

- `CreateFile` succeeds for a new path; file exists on disk afterward with expected seeded content.
- `CreateFile` fails (`InvalidArgument`-style) if the file already exists — does NOT overwrite.
- `CreateFile` + `namespaceName` on a `.cs` file produces a file that parses as a valid empty
  compilation unit (round-trip through `CSharpSyntaxTree.ParseText` with zero diagnostics).
- Follow-up `Member(add, containerName: null, newMemberSource: "public class Foo { }")` against the
  freshly created file succeeds — the critical integration test: `CreateFile` alone is useless if
  `Member` can't then populate it.
- Parent directory auto-creation.

### Not in scope

- No `ReplaceFile`-equivalent operation. No `content` parameter at all. A future "create with
  specific starter content" need is a deliberate design conversation, not an accidental reopening
  of the whole-file-rewrite footgun.
- No changes to `WriteFile`/`ApplyDiff`/`ApplyUnifiedDiff`'s disabled status — those stay off for
  evals; `CreateFile` is additive only.

## As-built deviations from the sketch

- **`typeKind`/`typeName` ended up mandatory for `.cs` files, not optional.** Rationale: an
  optional `typeKind` just relocates the failure mode this tool exists to close (model omits it →
  bare namespace file → needs a 2nd `Member(add)` round-trip to reach a populatable type — same
  friction as the original bug, one tool later). A `NewTypeKind` enum
  (`class`/`record`/`interface`/`enum`/`struct`) was added to `RoslynSentinel.Common/ToolEnums.cs`.
  A file needing a 2nd+ top-level type still uses `Member(add, containerName: null, ...)` for
  those.
- **`staticClass` added same day as a follow-up.** Extended the `NewTypeKind` enum directly
  (`class, record, interface, enum, struct, staticClass`) rather than adding a separate boolean
  parameter. `staticStruct` was considered and rejected — `static` is only valid on classes in C#
  (CS0106), so a `static struct` would never compile. `CreateFile`'s keyword-mapping special-cases
  `staticClass` → `"static class"` instead of the generic transform used for the other values.
- **Gating confirmed by reading source**: `SentinelWholeFileWriteTools`
  (`WriteFile`/`ApplyDiff`/`ApplyUnifiedDiff`/`DeleteFile`) is registered in DI only when
  `activeModes.Contains("Admin") || activeModes.Contains("WholeFileWrite")`
  (`ServiceRegistrationExtensionsBasic.cs:123-131`), deliberately excluded from the `AllModes`/
  `"all"` expansion. `SentinelWorkspaceTools` registers under the separate, near-always-on
  `"Workspace"` mode — confirming `CreateFile`'s placement there means it's visible in eval sessions
  regardless of whether `WholeFileWrite` is active.

Tests added/passing: 23/23 in `RoslynSentinel.Tests.Battery/CreateFileDeleteFileTests.cs`, including
the full `CreateFile` → `Member(add, populate)` → `Member(add, 2nd top-level type)` integration
scenario. Commit `e9cf707` (staticClass follow-up).
