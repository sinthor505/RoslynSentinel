# Blocking error — `Git(operation:"commit", files:..., scope:"listed")` does not restrict the commit to those files; it commits the entire index

**Status:** FIXED 2026-09-12. `CommitAsync` (`RoslynSentinel.Server.Basic/SentinelGitTools.cs`, now
~line 728) builds a `["commit", "-m", message, "--", ...filePaths]` pathspec whenever
`scope == GitStageScope.listed` and `paths` is non-empty, mirroring `StageAsync`'s existing path
parsing. When no specific files/scope-listed combination is passed, behavior is unchanged (full-index
commit). Verified via `Build(level: fullBuild)` — 0 errors — and the fix was folded into "Commit 1"
of the redo of this same session's 5-commit plan, since it was needed to do that task correctly. The
over-broad commit `35ce392` this bug produced was separately undone via an approved one-off shell
`git reset --soft` (the `Git` tool has no reset/undo operation — see
`blocking_error_git_tool_no_soft_reset_operation.md`, which remains open as a separate gap).

Original report follows, describing the bug as found (now fixed).

**Found:** 2026-09-12 while splitting a large already-staged working tree into 5
logically-grouped commits per an explicit task plan.

## Task context

The working tree had 18 files staged (mixing content belonging to several unrelated logical
changes) plus several unstaged/untracked files. The task was to produce 5 separate commits, each
containing an exact, pre-specified file list, using only the `Git` MCP tool (no shell `git`, per
CLAUDE.md's dog-fooding policy). The task's own instructions anticipated that partial-file
(hunk-level) staging might not be possible and said so explicitly — but the plan still required
**whole-file, per-commit scoping**: e.g. commit 1 was to contain exactly 9 named files out of the
18+ already staged, with the rest reserved for commits 2/3/4/5.

## What was called

```
Git(operation:"commit",
    files:"RoslynSentinel.Common/FilePathWrapper.cs,RoslynSentinel.Common/PersistentWorkspaceManager.cs,RoslynSentinel.Common/ToolEnums.cs,RoslynSentinel.Server.Advanced/SentinelCodemodTools.cs,RoslynSentinel.Server.Basic/SentinelWholeFileWriteTools.cs,RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs,RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs,RoslynSentinel.Tests/FilePathFromWireTests.cs,docs/current/blockers/blocking_error_live_server_stale_after_enum_addition.md",
    scope:"listed",
    message:"Fix misleading \"filepath is required\" error masking no-solution-loaded state...")
```

9 files named — a deliberate subset of the 18 already staged, plus 1 new file.

## What happened

`success:true`, `commitHash: 35ce392485431e9b228f08cb61f92973437dfc64`. But
`Git(operation:"diff", target:"35ce392...")` reports `filesChanged: 19` — **all 18 previously
staged files plus the 1 new file**, not the 9 that were named. `Git(operation:"status")`
immediately after confirms the entire index was cleared by the commit (`staged: []`), including
files like `ServiceRegistrationExtensionsBasic.cs`, `build.ps1`, `ServerHttp.cs` etc. that were
explicitly meant to be reserved for commits 2/3.

## Root cause (traced to source, not theorized)

`RoslynSentinel.Server.Basic/SentinelGitTools.cs`, `CommitAsync` (starts line 710):

```csharp
private async Task<GitCommitResult> CommitAsync(
    string gitRoot, string? message, GitStageScope scope, string? paths, CancellationToken cancellationToken)
{
    ...
    var stageResult = await StageAsync(gitRoot, scope, paths, cancellationToken);   // line 724
    if (!stageResult.Success)
        return new GitCommitResult { Success = false, Error = stageResult.Error };

    var (commitExit, commitOut, commitErr) = await RunGitAsync(gitRoot, ["commit", "-m", message], cancellationToken);  // line 728
    ...
```

`StageAsync` with `scope:"listed"` (lines 606-666) runs `git add -- <named files>` — this is
correctly **additive-only**; it stages exactly the named files without touching anything else
already in the index (confirmed separately: it does not remove files already staged from the
index; see `git add --` semantics and this file's own successful use for the prior `stage` call in
this session, which behaved exactly as documented).

But line 728 runs plain `git commit -m message` — **no `--` pathspec, no restriction of any kind**.
Once `StageAsync` returns, `commit` always commits the *entire current index*, which is the union
of whatever was already staged before the call plus whatever `files`/`scope` just added. The
`files`/`scope` parameters on `commit` only ever *add* to what gets committed; they can never
*narrow* it below "everything currently in the index."

There is no `reset`/unstage path reachable from this tool's public operation enum
(`status, log, diff, stage, add, commit, revert` — confirmed via the schema returned by
`ToolSearch`). An internal "Git unstage" catch block exists in source (around line 706, referenced
by a log message "Git unstage failed") but is not wired to any value in `GitOperation`/the public
`operation` enum, so it is not callable through this tool as currently exposed.

## Why this is not the already-documented `stage`/`scope` schema issue

`docs/current/blockers/blocking_error_git_stage_scope_schema_stale_server.md` (already on disk,
left untouched by this task) covers a *different* symptom: a stale server process serving an old
`stageAll`-shaped schema instead of the current `scope`-shaped one. That is unrelated here — this
session's live server DLL (`bin-vscode/Advanced.Http/RoslynSentinel.Server.Basic.dll`) is newer
than `SentinelGitTools.cs` on disk, confirmed via file mtime comparison, so staleness does not
explain this. `scope:"listed"` was accepted and behaved exactly as that other doc's source excerpt
describes for `StageAsync`. The defect here is specifically in `CommitAsync`'s failure to pass
`files`/`scope` (or an equivalent pathspec) through to the actual `git commit` invocation.

## Impact

**There is no way, via this tool alone, to commit only a subset of files whenever anything else is
already sitting in the index.** Any workflow that stages files incrementally across multiple
logical commits (exactly this task's situation, and plausibly a common one: a large session's
working tree accumulating multiple unrelated fixes before being split into commits) cannot be
split correctly — every commit after the first will silently absorb everything left over from
prior staging, or (as happened here) the *first* commit absorbs everything already staged before it
even though only a subset was named.

This is a silent-wrong-behavior defect, not a rejected/erroring one: `success:true` and a real
commit hash gave no signal that the commit's scope differed from the request. An agent (especially
a weak/local model, per this repo's mission) has no way to detect the divergence without an extra
`diff`/`status` round-trip after every commit — which this task happened to do, but nothing forces
it.

## What was done as a result

Commit `35ce392485431e9b228f08cb61f92973437dfc64` landed with all 19 files (the intended commit-1
set of 9, plus all 9 other files that were pre-staged for commits 2/3, plus 1 more) instead of the
intended 9. Per CLAUDE.md's failure doctrine, no attempt was made to route around this with shell
`git` (e.g. `git reset --soft`+recommit) — that would violate the dog-fooding policy for a git
operation this tool nominally supports (`commit`/`revert` exist; there's no principled "the tool
technically has this operation so shell is off the table even though it's broken" exception listed
under the doc's scope-boundary section, which only carves out non-C# files and operations the tool
truly has no verb for at all — `commit` exists, it just behaves wrong). The task is halted per
"finish any in-flight edit, stop advancing the task... and end the turn."

**No further commits (2-5) were attempted.** Continuing would compound the problem: any additional
`commit(files:...)` call would, per this same defect, commit the *entire remaining index* rather
than the intended narrower set, making the final commit history bear no reliable relationship to
the plan regardless of how carefully `stage` calls are sequenced.

## Suggested fix (for whoever picks this up)

Pass a pathspec through to the actual `git commit` invocation, mirroring what `StageAsync` already
does correctly:

```csharp
var commitArgs = new List<string> { "commit", "-m", message };
if (scope == GitStageScope.listed && hasPaths)
    commitArgs.AddRange(["--", .. filePaths]);   // restrict the commit itself, not just the add
var (commitExit, commitOut, commitErr) = await RunGitAsync(gitRoot, [.. commitArgs], cancellationToken);
```

Note `git commit -- <paths>` only commits changes to those paths from the index/working tree; it
does not silently include unrelated already-staged files, which is exactly the missing guarantee.
This needs the same care `StageAsync` already took (reject `scope != listed` combined with `paths`,
etc.) so the two don't drift again. A regression test should stage two unrelated files, commit only
one by name with `scope:"listed"`, and assert the resulting commit's `filesChanged` is 1 and the
other file remains staged afterward (currently it would be swept in and cleared from the index).

## Next step

Waiting for a fix and confirmation before resuming the remaining 4 commits. Once fixed, this repo's
git history will still contain the over-broad commit `35ce392` from this session — that is a
separate cleanup decision (amend/rebase vs. leave as historical fact) for whoever resumes, not
something to be decided unilaterally here.
