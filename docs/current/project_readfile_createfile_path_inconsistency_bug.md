---
name: project_readfile_createfile_path_inconsistency_bug
description: "ReadFile's existsOnDisk check and CreateFile/ApplyDiff's Roslyn-compilation collision check disagree about the same wrong (missing-subfolder) file path, sending the model contradictory signals ('file doesn't exist' vs 'file already exists') and burning its whole turn budget; found in run 1 of the 20-run PlanImplementVerify batch"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-02T00:44:22.099Z
---

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
the pre-fix scope doc, `docs/obsolete/new-file-validation-gap-scope.md` (moved to `obsolete/`
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
mechanisms and different fixes. This doc's proposed fix (name the colliding file's real path in
the CS0101/CS0111 message) is still open.
