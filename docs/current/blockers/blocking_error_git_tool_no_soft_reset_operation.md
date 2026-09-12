# Blocking error — `Git` tool has no operation equivalent to `git reset --soft/--mixed HEAD~1`

**Status:** OPEN. Found 2026-09-12 while attempting to undo commit `35ce392` (the over-broad commit
produced by the bug documented in
`docs/current/blockers/blocking_error_git_commit_files_param_does_not_scope_commit.md`) so its
19 files' content could return to the index/working tree exactly as it was pre-commit, and the
original 5-commit split could be redone correctly.

## Task context

The user approved: reset `35ce392` back to its pre-commit staged/unstaged state (soft/mixed reset,
no content loss), then redo the original 5-commit plan. The task explicitly named `git reset --soft
HEAD~1` / `git reset --mixed HEAD~1` as the required semantics, explicitly ruled out `revert` (makes
a new inverse commit, not a reset), and explicitly forbade falling back to shell `git` for this —
per CLAUDE.md's dog-fooding policy, enforced mechanically by the `enforce-dogfood.ps1` PreToolUse
hook.

## What was checked

1. **The tool's live schema**, fetched via `ToolSearch("select:Git")` (not the C# signature — per
   this repo's root-cause-discipline rule to check the emitted schema, not assume it matches
   source). The `operation` enum is:

   ```
   status, log, diff, stage, add, unstage, commit, revert, branch, checkout, push, fetch, pull
   ```

   There is no `reset` value and no ref/`commitHash`-bearing parameter usable to move HEAD
   backward — `commitHash` exists only for `operation:"revert"` ("the commit to revert").

2. **`RoslynSentinel.Server.Basic/SentinelGitTools.cs`**, read via `GetMethodSource`/
   `GetFileOutline` (solution loaded first — the tool requires it, consistent with
   `blocking_error_git_requires_loaded_solution.md`). The full dispatch surface (lines 437-976) is:

   `StatusAsync, LogAsync, DiffAsync, StageAsync, UnstageAsync, CommitAsync, RevertAsync,
   BranchAsync, CheckoutAsync, PushAsync, FetchAsync, PullAsync`

   Of these, the only one that touches the index/HEAD relationship at all is `UnstageAsync`
   (lines 674-709):

   ```csharp
   private async Task<GitStatusResult> UnstageAsync(
       string gitRoot, string? paths, CancellationToken cancellationToken)
   {
       ...
       string[] resetArgs;
       if (string.IsNullOrWhiteSpace(paths))
           resetArgs = ["reset"];                          // plain `git reset`, no ref
       else
           resetArgs = ["reset", "--", .. filePaths];       // `git reset -- <paths>`, no ref
       ...
   }
   ```

   Both branches call `git reset` **with no ref argument**, which resets the index against the
   *current* HEAD only (i.e., it can only undo staging, never move HEAD to a prior commit). There
   is no parameter on `Git(operation:"unstage")` — checked against the schema above, `commitHash`
   is documented as revert-only and `unstage` accepts only `paths` — that can be threaded through
   as a ref to make this `git reset --soft HEAD~1` or `git reset --mixed HEAD~1`.

3. **`RevertAsync`** (lines 751-789) is the only other operation that references a commit by hash,
   and by design creates a new inverse commit (`git revert`), which the task correctly identified
   as not equivalent to a reset — it adds history rather than removing the unwanted commit.

4. **`CheckoutAsync`/`BranchAsync`** operate on branches/refs for switching or creating branches,
   not on moving the current branch's HEAD pointer within history while preserving working-tree
   state.

## Root cause

`UnstageAsync`'s doc comment (line ~677) states its own scope precisely: "The tool could previously
stage but never un-stage, so any mis-stage forced a shell fallback." It was built to solve *mis-staging
within the current commit*, not to undo an already-made commit. No operation in the enum was ever
designed to move HEAD backward at all — this isn't a regression, it's a capability that was never
added. `git reset --soft/--mixed <ref>` needs two things `unstage` doesn't have: (a) a ref parameter
(defaults implicitly to `HEAD`, never `HEAD~1` or a hash), and (b) semantics that operate against a
target commit rather than always against current HEAD.

## Impact

There is currently no way, using only the `Git` MCP tool, to undo a bad commit non-destructively
(keeping its content staged/unstaged) once it has landed — the exact situation created by the
sibling bug in `blocking_error_git_commit_files_param_does_not_scope_commit.md`. The two bugs
compound: the commit-scoping bug makes over-broad commits more likely, and this gap means there is
no in-tool recovery path once one happens. An agent hitting this has to stop and ask for either a
shell exception or a tool fix — exactly what is happening now.

## Suggested fix

Add a `reset` value to the `operation` enum (or extend `unstage` with an optional ref parameter),
e.g.:

```csharp
private async Task<GitStatusResult> ResetAsync(
    string gitRoot, string? refName, GitResetMode mode, CancellationToken cancellationToken)
{
    var modeFlag = mode switch
    {
        GitResetMode.Soft  => "--soft",
        GitResetMode.Mixed => "--mixed",   // reset's own default, but pass explicitly for clarity
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    var target = string.IsNullOrWhiteSpace(refName) ? "HEAD~1" : refName;
    var (exit, _, err) = await RunGitAsync(gitRoot, ["reset", modeFlag, target], cancellationToken);
    ...
}
```

Deliberately exclude `--hard` from the mode enum (this repo's existing pattern of protecting against
destructive git ops by construction, as seen in `UnstageAsync`'s comment about never reaching
`reset --hard`-equivalent behavior). A regression test: commit a file, `reset(mode:"soft",
ref:"HEAD~1")`, assert `log` no longer shows the commit and `status`/`diff --staged` shows the
file's content fully present in the index exactly as before the commit.

## What was done as a result

No reset was attempted via any workaround. `Git(operation:"log")` and `Git(operation:"status")` were
both called read-only to confirm current state (5 commits deep, working tree already showing the
19 files as untracked/modified with nothing staged — i.e., `35ce392` has *not* yet been undone).
Per CLAUDE.md's failure doctrine and this task's explicit instruction, the task is halted here.
**No commits were reset, no files were staged, and Step 2 (the `CommitAsync` pathspec fix) and Step 3
(the 5-commit redo) were not started**, since Step 3 depends on Step 1 completing successfully first.

## Next step

Waiting for either: (a) the `Git` tool gaining a `reset` operation (or `unstage` gaining a ref
parameter) capable of `git reset --soft/--mixed HEAD~1`, or (b) explicit approval to use shell `git`
for this one operation as a named exception to the dog-fooding policy. Do not resume Steps 2/3 until
one of those happens.
