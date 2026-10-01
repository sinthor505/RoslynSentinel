# Plan: Git tool - listed-scope fixes, shell-git parity, hook alignment, regression tests

## Status: IMPLEMENTED (2026-09-30)

Commits: Phase 0 `d8af128`; Phase 1 `b692dfc` + `d439eec`; Phase 2 `59e3599`; Phase 3 `c124b51`; Phase 4 `e3852f9`;
Phase 5 `0ecc527`; Phase 6 `002384a` (code) plus the docs close-out commit. Source blocker now lives in
`docs/current/blockers/resolved/`.

Source blocker: `docs/current/blockers/blocking_error_git_commit_listed_scope_rejects_staged_deletions_and_ignored_tracked_paths.md`.
This plan covers that blocker plus the parity gaps found in the follow-up review of `GitTools.cs`,
`GitImpl.cs` and `.claude/hooks/enforce-dogfood.ps1`.

## If any step below admits more than one reasonable reading

Stop before acting on it. State (1) the ambiguity, (2) which reading you chose, (3) why - as a
visible note in the final report or a code comment. Do not silently pick an interpretation.

## Execution rules (apply to every phase)

- **Hook disabled for this implementation (user decision, 2026-09-29):** the dog-food PreToolUse
  hook is off so the buggy Git tool cannot block the work. Shell `git add`/`git commit` is
  therefore permitted for THIS plan's commits only (stage explicit paths, never `-A`; commit only
  files changed in the phase). All C# edits and reads still go through the MCP tools. The
  ordering-constraint bullet below (restart and workaround) becomes optional: tests run via
  `dotnet test` against source, so a server restart is only needed to re-verify with the live tool.

- All C# reads/writes and git operations go through the RoslynSentinel MCP tools (`ReadFile`,
  `Member`, `ReplaceSnippet`, `ApplyDiff`, `Git`, `Build`, `RunTest`). `.ps1`/`.md` files use normal
  file tools.
- **Ordering constraint:** the Git tool is used to commit its own fixes. The server binary running
  the session is the OLD one until `McpServerControl(operation: stop)` and reconnect. Commit phases
  with the old tool (workaround: `Git(unstage)` with no paths, then `commit` with `scope: listed`
  listing only paths that exist in the index or on disk; leave `TestResults` deletions out). Restart
  the server after Phase 1 lands and re-verify with the fixed tool before starting Phase 2.
- Dispatch subagents **sequentially**, never in parallel, for C# edits (shared server process).
- One commit per phase. Build (0 errors) before each commit. Stage with `Git` scope `listed`,
  naming every file changed in the phase.
- Compare `RunTest` results to the known-failure baseline (`reference_known_failing_tests`);
  report only new failures.
- ASCII-only punctuation in code comments, docs and commit messages. `CancellationToken` stays the
  last parameter in any new or changed signature.
- Every new error message names the offending parameter and the call that would have worked, and
  states whether anything was mutated ("Nothing was staged.").

## Phase 0 - Test harness groundwork (no behaviour change)

Files: `RoslynSentinel.Tests.Battery/GitToolsSmokeTests.cs`.

- Add test helpers on the existing temp-repo fixture: `WriteFile(rel, content)`,
  `RunGitCapture(args) -> stdout`, `StagedPaths()` (`git diff --cached --name-only`), and
  `HeadPaths()` (`git show --name-only --format= HEAD`). Existing `RunGit` discards output.
- Add fixture options: a `.gitignore` containing `TestResults/` and a helper that force-tracks a
  file under it (`git add -f`) to reproduce blocker case 2.
- Add a CRLF helper (`git config core.autocrlf true` and `core.safecrlf warn`) so the LF-to-CRLF
  warning noise is reproducible.

Exit: existing tests still pass; no production code touched.

## Phase 1 - Unblock commit (the documented blocker, plus gaps A, B and the stderr defects)

Files: `RoslynSentinel.Tools.Basic/GitImpl.cs`, `GitTools.cs`, `IGitOperations.cs`,
`RoslynSentinel.Common/ToolEnums.cs`.

1. **Path classification helper** in `GitImpl` (private): given the parsed list, return
   `{ Untracked, InIndex, HeadOnly, Missing }` using
   `git ls-files -z --cached -- <paths>` and `git ls-tree -r -z --name-only HEAD -- <paths>` plus a
   disk existence check. Unborn HEAD (no commits): treat `ls-tree` failure as empty.
2. **`StageAsync(listed)` rewrite:**
   - Classify first. If any path is `Missing` (not tracked, not on disk), return an error naming
     every such path. Mutate nothing.
   - `HeadOnly` paths (deletion already staged): skip; report them as already staged.
   - `InIndex` paths: `git add -u -- <paths>` (no untracked scan, so no ignored-path advisory).
   - `Untracked` paths: `git add -- <paths>`. If stderr contains the ignored-paths advisory, return
     an error naming the ignored paths, stating the tool has no `-f`, and stating to remove them
     from the list.
   - Run with literal pathspecs (`--literal-pathspecs` before the subcommand) and normalize `\` to
     `/`. Reject any path resolving outside the repo root.
3. **`CommitAsync(listed)`:** for tracked paths, do not pre-stage; rely on
   `git commit -m ... -- <paths>` (`--only`). Pre-stage only `Untracked` paths (they must be in the
   index for the pathspec to match). Same classification and validation up front.
4. **Gaps A and B - `files`/`paths` imply `listed`:** in `GitTools.Git`, when `resolvedPaths` is
   non-empty and `scope` is null, set `scope = listed` for `stage`/`add` and `commit`. An explicit
   `all` or `tracked` combined with a file list remains an error. Update the `scope` and `files`
   descriptions: "files implies scope=listed; pass scope only to override or for tracked/all."
   Keep `listed` accepted.
5. **Stderr hygiene:** add `CleanGitStderr(string)` that drops `warning: ... LF will be replaced by
   CRLF` and `The file will have its original line endings` lines and caps output at about 2000
   chars with a "(N more lines omitted)" suffix. Apply it wherever `.Stderr.Trim()` feeds an error
   (`StageAsync`, `UnstageAsync`, `CommitAsync`, `ResetAsync`, `RevertAsync`, `CheckoutAsync`,
   `BranchAsync`, log/diff/show).
6. **Leftover reporting:** add `RemainingStaged` (list of paths) to `GitCommitResult`, populated by
   `git diff --cached --name-only` after a successful commit. Empty means the index is clean.
7. **Failed-call state note:** when `CommitAsync` staged something and then the commit failed, append
   "Staging already ran; the index now holds N staged paths. Call Git(operation: unstage) to undo."
   (With step 3, this only applies to newly added untracked files.)

Tests (`GitToolsSmokeTests.cs`):
- Stage a rename with `scope: all`, then commit `scope: listed` naming both sides. Succeeds (blocker case 1).
- Commit `scope: listed` for deletion of a force-tracked file under a gitignored directory.
  Succeeds, no advisory error (blocker case 2).
- `commit` with `files` and no `scope` commits exactly those paths and leaves other staged paths
  staged (gap A). Assert `HeadPaths()` equals the list and `RemainingStaged` holds the rest.
- `stage` with `files` and no `scope` stages only those paths (gap B).
- `stage`/`commit` naming a missing, untracked-on-nowhere path: error names it, `StagedPaths()`
  unchanged (atomic failure).
- Listed untracked path under a gitignored directory: error names it and the missing `-f`.
- Rename listed by only one side: commit succeeds, `RemainingStaged` contains the other side.
- Filename containing `[` and `*` is staged literally (not globbed).
- CRLF fixture: a failing call's error contains no `LF will be replaced` text.
- `files` plus `scope: all` and `files` plus `paths` still return their existing refusals.

Exit: Build clean, new tests green, baseline unchanged. **Stop the server and reconnect** before Phase 2.

## Phase 2 - Hook alignment

Files: `.claude/hooks/enforce-dogfood.ps1`, `.claude/hooks/enforce-dogfood.Tests.ps1`,
`.claude/hooks/friction-cases.Tests.ps1`.

Rationale: a scope-less commit no longer stages anything (`CommitAsync` comment), so the
dirty-worktree count no longer measures sweep risk. Only files already staged can be swept in.

1. Replace the `git status --porcelain` dirty count (line ~346) with
   `git diff --cached --name-only`. The hook cannot tell which session staged what, so the rule
   is: deny a scope-less, file-less commit when the staged set is empty ("nothing staged; stage
   first or pass files"), allow it otherwise. See "Risks and open decisions" for the trade-off.
2. Treat `files`/`paths` alone (no `scope`) as an explicit listing, matching the tool change
   (gap A fix removes the loophole this would otherwise create).
3. Update the deny messages to show `files` without `scope` as the short form.
4. Add hook tests: scope-less commit with empty index denied; with staged paths allowed; `files`
   only allowed; missing trailer still denied.

Exit: both `.Tests.ps1` files pass. Note: hook edits take effect on the next tool call; verify with
a real `Git(commit)`.

## Phase 3 - Read-side parity (status, diff, show)

Files: `GitImpl.cs`, `GitTools.cs`, `IGitOperations.cs`.

1. **Gap D - status parsing:** switch to `git -c core.quotePath=false status --porcelain=v1 -z`.
   Parse NUL-separated records; for `R`/`C` entries the next record is the original path. Add
   `OriginalPath` to `GitStatusEntry`. Paths returned must round-trip into `files`.
2. **Gap C - full listings:** add `maxEntries` (default 50, max 5000) to `status`, replacing the
   fixed threshold; truncation flag and per-status counts stay. Add `nameOnly` and `stat` (bool) to
   `diff` and `show`, returning a file list (`--name-status`) or `--stat` text instead of the patch.
3. `maxBytes` compares string length (chars) but is documented as bytes. Either measure bytes
   (`Encoding.UTF8.GetByteCount`) or rename the description to characters; also avoid cutting a
   surrogate pair. Minor; do it here since the code is open.
4. Status labels for conflict pairs (`AA`, `DD`, `UU`) report `conflict` for both sides instead of
   staged "added"/"deleted".

Tests: path with spaces and a non-ASCII name round-trip status -> `files`; staged rename returns
`Path` and `OriginalPath`; `maxEntries` above the 50 threshold returns every entry; `nameOnly` and
`stat` on a multi-file commit.

## Phase 4 - Ref parameter, reset safety, checkout, pull, environment

Files: `GitImpl.cs`, `GitTools.cs`, `IGitOperations.cs`, `GitToolsSmokeTests.cs`.

1. **Gap K - single `ref` parameter:** add `ref` (string?) as the spelling for "a git ref" on
   log (start ref), show, diff, reset. `target`, `commitHash`, `branchName` remain accepted aliases
   for their existing operations; supplying both `ref` and an alias with different values returns
   the same both-supplied refusal used for `files`/`paths`. Update all descriptions.
2. **Gap E - reset requires an explicit ref:** remove the `HEAD~1` default. A missing ref returns
   an error: "reset needs an explicit ref, e.g. ref: \"HEAD~1\" to undo the last commit (working
   tree kept). To unstage without moving HEAD use operation=unstage." Update the two existing reset
   tests to pass `ref: "HEAD~1"`.
3. **Gap F - checkout createBranch:** if the branch exists, do a plain checkout; otherwise `-b`.
   Set `CreatedNewBranch` from what actually happened. Match the description.
4. **Gap G - pull:** pass `--no-rebase` when `rebase` is false.
5. **Gap I - non-interactive environment:** in `RunGitAsync` set `GIT_TERMINAL_PROMPT=0` (and
   `GCM_INTERACTIVE=never`) so credential prompts fail fast instead of hanging to the 30 s timeout.
6. **Gap J - no-ops become refusals:**
   - `startPoint` without `createBranch` on `checkout` returns an error naming the missing flag.
   - `amend` when HEAD is already contained in the upstream (`git branch -r --contains HEAD` is
     non-empty) is refused: force-push is not exposed, so the amend would strand the branch. Message
     names the alternative (new commit, or `revert`).
7. `checkout <name>` gets a trailing `--` to disambiguate a branch from a same-named path.

Tests: bare `reset` returns the named-parameter error and moves nothing; `reset` with `ref` works
(updated existing tests); `createBranch` on an existing branch succeeds with
`CreatedNewBranch = false`; `ref` plus conflicting alias refused; `startPoint` without
`createBranch` refused; amend of a pushed HEAD refused (bare-repo remote fixture); `pull` on
divergent branches with no config merges instead of failing (remote fixture).

## Phase 5 - Escape hatch for in-progress operations (gap H)

Files: `RoslynSentinel.Common/ToolEnums.cs`, `GitTools.cs`, `IGitOperations.cs`, `GitImpl.cs`,
`GitToolsSmokeTests.cs`. No registry/DI change: `Git` is already registered.

1. Add `abort` to `GitOperation`. Implement `AbortAsync`: detect the in-progress operation from
   `.git` state (`MERGE_HEAD`, `rebase-merge`/`rebase-apply`, `REVERT_HEAD`, `CHERRY_PICK_HEAD`) and
   run the matching `--abort`. With nothing in progress, return a clear "nothing to abort".
2. `status` reports an `InProgress` field (`merge`, `rebase`, `revert`, none) so the state is
   visible before it bites.
3. Conflict failures from `pull`, `revert` and `commit` append: "Repository is mid-<op>. Resolve
   the conflicts and commit, or call Git(operation: abort) to back out."
4. `revert` of a merge commit: accept `mainline` (int) and pass `-m`; without it, the error names
   the parameter.

Tests: pull conflict -> `abort` restores clean tree; revert conflict -> `abort`; `abort` with
nothing in progress; status shows `InProgress` during a merge; revert of a merge commit with and
without `mainline`.

## Phase 6 - Error codes and documentation close-out

1. Give `GitError` results a `Detail` and a specific `ErrorCode` where the cause is known
   (`GitPathNotFound`, `GitIgnoredPath`, `GitNothingToCommit`, `GitOperationInProgress`,
   `GitRefRequired`), reusing the taxonomy in `project_tool_error_code_taxonomy_proposal`. Existing
   tests asserting `GitError` keep passing by keeping it as the fallback code.
2. Move the source blocker doc to resolved with a resolution note citing the phase commit hashes
   (read `CommitHashLength` off the `Git` result); add an entry to `docs/current/CLOSED.md`; remove
   any matching line from `docs/current/TODO.md`.
3. Update memory `project_git_tool_defects_2026_09_12` (worktree/stash/tag remain open) and index.

## Explicitly out of scope

`stash`, `tag`, `worktree`, `restore`, `rm`/`mv`, force-push, `reset --hard`, `branch -D`. These stay
documented shell exceptions per `docs/current/TODO.md`; destructive modes stay unexposed by design.

## Risks and open decisions

- **Behaviour claims are from git source knowledge, not yet observed.** Phase 1 tests are the
  verification; if `git commit -- <path>` does not resolve a staged-deleted path on the installed git
  version, fall back to including `HeadOnly` paths only in the commit pathspec after classification.
- **Hook rule in Phase 2 (item 1) - DECIDED 2026-09-29:** allow a scope-less, file-less commit
  when something is staged. This trades away the per-file audit for those commits; accepted.
- **Reset default removal (E)** is a breaking change to callers relying on `HEAD~1`; both in-repo
  tests are updated in the same commit. Grep agent docs/prompts for bare `reset` usage first.
- **Amend refusal (J)** depends on a configured upstream; with no upstream, allow.
