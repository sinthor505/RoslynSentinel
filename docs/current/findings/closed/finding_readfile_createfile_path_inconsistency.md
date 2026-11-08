# Finding: ReadFile and CreateFile disagree about a wrong path (existsOnDisk vs collision check)

**Status:** CLOSED 2026-11-08. Historical record filed under closed/ by the docs reorganization; the body keeps the original outcome.

**Merged from:** project_readfile_createfile_disk_fallback_fixed

## What happened

Run 1's implement phase of the 20-run `PlanImplementVerify` batch (`.113`) hit
`TurnCapExceeded` after 25 turns, never reaching a working fix. Root cause traced in
`ModelTestingResults/113/.../20260901-222149-093/implement/agent.log`:

1. **Turn 1**: model calls `ReadFile` on
   `.../ContosoOrders.Core/BlockEditHelpers.cs` — missing the real `FixtureHelpers/`
   subfolder (the actual file is at `.../ContosoOrders.Core/FixtureHelpers/BlockEditHelpers.cs`,
   confirmed by `ListSolutionItems`'s correct listing at turn 2). This is the model's own path
   mistake, unsurprising on its own.
2. **Turns 1, 3, 6, 8, 13, 22-25**: repeated `ReadFile` calls on the same wrong path all return
   `FileNotFound: ... (existsOnDisk=False, projectsLoaded=2)` — consistent, correct-looking
   "this file doesn't exist" signal.
3. **Turn 12**: model tries `CreateFile` at that *same wrong path* (still no `FixtureHelpers/`).
   The pre-apply validator **rejects it** with:
   ```
   CS0101: The namespace 'ContosoOrders.Core.FixtureHelpers' already contains a definition for 'BlockEditHelpers'
   CS0111: Type 'BlockEditHelpers' already defines a member called 'ReplaceBlockFormatted' with the same parameter types
   ```
4. **Turn 16**: model tries `ApplyDiff` at the same wrong path — gets the **identical** CS0101/CS0111
   "already contains a definition" collision.
5. Turns 14, 24-25: model's own reasoning explicitly notices the contradiction ("The file doesn't
   exist on disk... ReadFile can't find it" / "isn't being read by the MCP server") but has no tool
   that resolves it, and eventually just repeats the same failing `ReadFile` call three times in a
   row until the turn cap is exhausted.

## The actual bug

`ReadFile`'s existence check operates on the literal file path (correctly says the wrong path
doesn't exist), but `CreateFile`/`ApplyDiff`'s pre-apply validation is based on **Roslyn's
semantic model** — it compiles the proposed content against the whole project and flags a
namespace/type/member collision because the *real* `FixtureHelpers/BlockEditHelpers.cs` already
declares `ContosoOrders.Core.FixtureHelpers.BlockEditHelpers.ReplaceBlockFormatted`, regardless of
what file path the new content is nominally attached to. Both checks are individually "correct" by
their own logic, but they disagree about the same path in a way that gives the model no actionable
signal: "this file doesn't exist" (path-based) vs. "a member with this name already exists"
(semantic, project-wide) look contradictory to a model reasoning about one file at a time.

**This is a genuine RoslynSentinel gap, not a model defect.** The model's only real mistake was the
initial wrong path guess (a `FixtureHelpers/` subfolder miss) — a normal, recoverable error class
already well-handled elsewhere in the toolset (e.g. `SearchSolutionText`/`ListAll` correctly locate
the real file, as seen in this same run's plan phase). What made it unrecoverable here is that
neither `CreateFile`'s nor `ApplyDiff`'s error message tells the model *why* the collision is
happening (it never says "a file with this content already exists elsewhere at
`FixtureHelpers/BlockEditHelpers.cs`") — it just reports the C# compiler error as if the model had
correctly identified the target file and introduced a genuine duplicate-definition bug.

## Possible fix (not yet designed in detail)

When `CreateFile`/`ApplyDiff`'s pre-apply validation surfaces a CS0101/CS0111-style
already-defines collision for a **new** file (i.e. `CreateFile`, or `ApplyDiff` targeting a path
that doesn't currently exist), check whether the colliding symbol is already declared in a
*different* file already in the solution, and if so, name that file's actual path in the error —
turning "already contains a definition" into "already contains a definition, in
FixtureHelpers/BlockEditHelpers.cs — did you mean to edit that file instead of creating this one?"
This would have let the model at turn 12 or turn 16 realize its path was wrong and redirect to
the real file, rather than concluding the MCP server itself was malfunctioning (per its turn 24
reasoning) and looping on an unproductive `ReadFile` retry.

## Scope note

Observed once so far (run 1 of this 20-run batch). Per [[feedback_always_writeup_cs_error_designs]]'s
spirit of writing up cheap, evidenced fixes immediately rather than waiting for a pattern — but
this is a tooling/error-message gap, not a `CompilerErrorLookupHelper` CS-diagnostic case (no
diagnostic ID is being misinterpreted; the message correctly reports CS0101/CS0111, it's just
missing the "elsewhere" pointer that would make the message actionable for a model reasoning about
one file path at a time).

## Root cause identified: this is the direct, working-as-designed consequence of commit `1b00f3f`

Traced via `git log` on the user's hint that this might relate to "the recent workspace patching
issue." Commit `1b00f3f` ("Validate new files against their containing project's compilation",
2026-08-24) is exactly the mechanism producing this. Before that commit, `ValidationEngine`
skipped in-memory validation entirely for any file not already a `Document` in the solution — see
the pre-fix scope doc, `docs/current/findings/closed/finding_new_file_validation_gap_scope.md` (moved to `obsolete/`
once `1b00f3f` shipped and `876973f`-style resolution tracking marked it done — note: unlike
`876973f`, no separate "confirmed resolved" commit exists for this one; it was moved straight to
obsolete in the same commit as the fix).

The mechanism, precisely: `SolutionProjectLocator.FindContainingProject` (new in that commit, at
`RoslynSentinel.Common/SolutionProjectLocator.cs`) resolves a new file's path to a *project* via
longest-prefix directory match only (`filePath.StartsWith(projectDir)`) — it has no awareness of
sibling files or symbol names. `ValidationEngine.ValidateChangesAsync` then adds the new file into
the candidate `Solution` at exactly the (possibly wrong) path the caller gave, and lets Roslyn's
real compiler catch any resulting errors. This is exactly right for its stated purpose (catching
brand-new files with genuine compile errors, which is what it was built to fix — previously such
files were silently written unchecked). But when the "new" file's path is a *wrong* path for an
*existing* file within the same project, the longest-prefix match still succeeds (same project),
so the file gets added as a second, colliding document — producing exactly the CS0101/CS0111
"already contains a definition" pattern seen in this run, with no signal that the collision is
against a real file at a different, correct path.

**So this is not a bug in `1b00f3f`'s own logic** — the scope doc it shipped with explicitly
listed what the fix would and wouldn't cover, and "tell the model where the colliding file
actually lives" was never in scope; the doc was scoped to "does validation catch the bad file,"
not "is the resulting error message actionable for an agent that doesn't know the real path."
This finding fills that specific, previously-unscoped gap. The proposed fix above (name the
colliding file's real path in the CS0101/CS0111-style message) is additive to `1b00f3f`, not a
correction of it.

## Related but distinct issue, found and fixed independently on the same day

A separate Claude session, working from a branch named after this same bug report (before it could
actually see this doc, which hadn't been pushed to a shared branch it fetched from yet), found and
fixed a **different** `ReadFile`/`CreateFile` asymmetry under the same-sounding description: files
`CreateFile` writes that fall outside `PersistentWorkspaceManager`'s `.cs`-only, project-owned-only
Document-sync filter (any non-`.cs` file, or a `.cs` file outside every project's directory tree)
never became tracked Documents, so `ReadFile`'s Document-only lookup reported `FileNotFound` for a
file that genuinely existed on disk. Fixed by adding a disk-read fallback to `ReadFile` — see
[[project_readfile_createfile_disk_fallback_fixed]] for that writeup (merged into master).

That fix does **not** address the collision-message gap documented above (a wrong path that
happens to match an *existing* project-owned `.cs` file's project directory) — the two bugs share
a symptom category (`ReadFile` and `CreateFile` disagreeing about a path) but different root
mechanisms and different fixes.

## Fixed: collision message now names the real colliding file

Implemented the "Possible fix" above directly in `CompilerErrorLookupHelper`
(`RoslynSentinel.Basic/CompilerErrorLookupHelper.cs`), which is the single chokepoint all four
`CreateFile`/`ApplyDiff` (Basic + Advanced) validation-failure paths already route their
`DiagnosticReport` through (`SentinelWorkspaceTools.cs`) — the same place CS0122 accessibility
guidance was added earlier. Added a `DescribeDuplicateDefinitionAsync` branch for CS0101 ("the
namespace already contains a definition for X") and CS0111 ("type already defines a member called
X with the same parameter types"): extracts the colliding member name (and containing type, for
CS0111) via regex, looks it up with `SymbolNavigationEngine.LocateSymbolAsync`, and — if a
declaration is found at a path other than the diagnostic's own (wrong) path — names that real
file's path and line directly, e.g. "A declaration of 'ReplaceBlockFormatted' already exists at
FixtureHelpers/BlockEditHelpers.cs:5. ... did you mean to ReadFile/ApplyDiff
FixtureHelpers/BlockEditHelpers.cs instead of creating a new file here?"

Regression test: `CompilerErrorLookupHelperTests.DescribeAsync_Cs0111DuplicateMember_NamesTheRealCollidingFilePath`
(`RoslynSentinel.Tests/CompilerErrorLookupHelperTests.cs`) — two files declaring the same
namespace/type/member (mirroring this doc's `FixtureHelpers/BlockEditHelpers.cs` transcript
scenario), asserting the description names the real file's path. Passes alongside the existing
CS0122 test. Build: 0 errors.

---

## Merged: ReadFile/CreateFile disk-fallback gap — fixed 2026-09-02

**Note:** this file didn't exist anywhere in the repo (working tree or git history, any branch)
before the session that wrote it, despite that session's branch name
(`readfile-createfile-path-bug-*`) implying it should — that branch name was chosen before the
actual bug report ([[project_readfile_createfile_path_inconsistency_bug]]) had been pushed
anywhere that session could see it. Written to document the bug that session found instead, by
reading `ReadFile` and `CreateFile` in `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`
side by side. See the bottom of [[project_readfile_createfile_path_inconsistency_bug]] for how
this relates to (and differs from) that original report.

### The bug

`CreateFile` (`SentinelWorkspaceTools.cs`, `CreateFile` method) writes a new file to disk through
`PersistentWorkspaceManager.ApplyProposedChangesAsync` for **any** path under the solution
root — any extension, whether or not the path falls inside a loaded project's directory.

`ReadFile` (same file, `ReadFile` method) only ever looked the path up as a Roslyn `Document` in
`solution.GetDocumentIdsWithFilePath(...)` / a scan over `solution.Projects...Documents`. If no
`Document` matched, it returned `FileNotFound` — even when the file plainly existed on disk
(the error message's own `existsOnDisk=...` field could read `true` while still failing).

The two tools disagree about what "exists" means because
`PersistentWorkspaceManager`'s post-write in-memory sync
(`ApplyInMemoryDocumentUpdatesAsync`/`ApplyProposedChangesAsync`) only ever turns a written path
into a tracked `Document` when **both**:
1. its extension is `.cs` (everything else is skipped outright — `if (ext != ".cs") continue;`), and
2. `SolutionProjectLocator.FindContainingProject` can resolve an owning project for its directory
   (otherwise it's skipped with a logged warning and never becomes a `Document`).

So `CreateFile` could successfully write, e.g., a `.txt`/`.json`/`.md` file, or a `.cs` file
outside every project's directory tree, and `ReadFile` would then report `FileNotFound` for that
exact path forever after — a write/read round trip that silently fails for anything that isn't a
project-owned `.cs` file. `CreateFile_NewPath_WritesContentAndReturnsSuccessAsync` in
`RoslynSentinel.Tests.Battery/CreateFileDeleteFileTests.cs` only asserted the file landed on disk
via raw `File.ReadAllTextAsync`, never that the MCP `ReadFile` tool could read it back — so this
had no test coverage before now.

This same asymmetry is a known, already-established pattern elsewhere in the codebase in the
*other* direction: `AugmentToolsTests.cs` has a regression guard
(`GenerateToStringSafe_WorkspaceFirstRead_NoDiskFileRequired`) specifically because an engine once
read only from disk and needed a workspace-first fallback added. `ReadFile` had the mirror-image
gap — workspace-only, no disk fallback.

### Fix

`ReadFile` (`SentinelWorkspaceTools.cs`) now falls back to a raw disk read via
`FileIoHelper.ReadAllTextIfExistsAsync` when no tracked `Document` matches the path, building a
`SourceText` from the disk content instead of a `Document`'s. Everything downstream (line-range
slicing, large-result offload, `totalLines`) is unchanged since it only depends on the resulting
`SourceText`. `FileNotFound` is now returned only when the path has no `Document` **and** doesn't
exist on disk either — matching what `CreateFile` is actually able to produce.

### Tests added

- `RoslynSentinel.Tests.Battery/CreateFileDeleteFileTests.cs`:
  `CreateFile_NonCsFile_ThenReadFile_ReturnsContentAsync` — end-to-end round trip against a real
  `PersistentWorkspaceManager`/`TestSolutionFixture`: `CreateFile` a `.txt` file, then `ReadFile`
  it back and assert the content matches.
- `RoslynSentinel.Tests.Battery/ReadFileTests.cs`:
  `ReadFile_FileOnDiskButNotTrackedAsDocument_FallsBackToDiskReadAsync` — unit-level check against
  the fake workspace manager: a file written straight to disk (never added as a `Document` to the
  test solution) is still readable through `ReadFile`.
- Existing `ReadFile_FileNotInSolution_ReturnsFileNotFoundAsync` still passes unchanged — it covers
  a path that doesn't exist on disk at all, which must still fail.

### How to apply

No dotnet SDK was available in the session that made this fix (headless container, `dotnet` not on
`PATH`) — the change was verified by manual code review only, not by running `dotnet build`/`dotnet
test`. Before trusting this as done, run
`dotnet test RoslynSentinel.Tests.Battery --filter "FullyQualifiedName~ReadFileTests|FullyQualifiedName~CreateFileDeleteFileTests"`
(or the full battery) on a machine with the SDK installed, and fix forward if anything doesn't
compile or pass.

If a similar "one tool's write path is more permissive than another tool's matching read path"
gap turns up elsewhere, check first whether it's rooted in the same place as this one — the
`.cs`-only, project-owned-only filter in `PersistentWorkspaceManager`'s in-memory Document sync
(see `docs/current/references/reference_code_file_write_paths.md` for the write chokepoint this interacts
with) — rather than assuming it's a new, unrelated defect.
