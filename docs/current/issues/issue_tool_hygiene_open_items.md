# Small open tool-hygiene items (DTO hook gap, GitImpl ex.Message, battery-test file leaks) - OPEN

**Status:** OPEN 2026-10-17. Three unrelated small defects, none started.

## Summary
Moved verbatim from the restructured TODO.md.

## Moved from TODO.md (2026-10-17)

### DTO hook gap: `Member replace`, `ReplaceSnippet` and `ApplyDiff` are not inspected (2026-10-16) - not started

`enforce-dto-justification.ps1` only inspects `Member` addTopLevelType/addMember, `CreateFile` and `WriteFile`. A new DTO added through `Member replace`, `ReplaceSnippet` or `ApplyDiff` is covered by the skill's wording only. Unverified: whether `tool_input.reason` reaches PreToolUse as a plain string for MCP calls (inferred from `ToolCallReason.cs:23-41`; check `.claude/journal/dto-hook.jsonl` after the first live trigger).

### `GitImpl` generic catch blocks put `ex.Message` into `Error`

**Found:** 2026-09-30, during the Git listed-scope/shell-parity plan (Phases 1-6). About 16
`catch (Exception ex)` blocks in `RoslynSentinel.Tools.Basic/GitImpl.cs` build a failed result from
`ex.Message`, which can leak raw exception text (and potentially internal paths) into the tool's
`Error`/`ResultError.Message`, against the CLAUDE.md rule that raw exceptions never reach a tool result.

**Suggested approach:** route these through one helper that logs the exception server-side and returns a
fixed, actionable message (plus a `GitError` code and `Detail` naming the operation and a next step), and
add a test that forces an exception path and asserts no exception text is exposed.

### Battery tests with bare relative file names leak files into the working directory (2026-10-17) - not started

Found fixing BatteryTwentyFourTests.MethodSignature_Remove_LastParameter_UpdatesCallSite: a stale CRLF Order.cs in the test bin dir (left by another test) was read by the chokepoint's disk pre-image check (PersistentWorkspaceManager ~:1582) and tripped EolChangeGuard. Only BatteryTwentyFourTests got a Setup/TearDown cleanup; other battery classes using bare names can leak the same way. Consider a shared test-base cleanup.

### `SubAgentEval` child's full `RunTest` reported 1 failed test (2657 passed) - unexplained (2026-10-01; moved from TODO.md 2026-10-18)

Found in the live `SubAgentEval` smoke run (run `20261001-222319-107-295d110c`). The same suite in the
main checkout has 0 failures. `SubAgentEvalResult` carries counts only, so the failing test's name was
not captured. Candidates: a test that assumes the repo root has a `.git` directory (a worktree has a
`.git` file), a path-length issue under `RoslynSentinel-TestRuns\SubAgent\<runId>\subagent\Worktree`, or
a flake from running alongside other sessions. Next step: re-run `SubAgentEval` (or `RunTest` in a
manual worktree at that path) and capture which test fails; consider adding the first failing test name
to `SubAgentEvalResult` so this is visible without digging. Audit verdict UNSURE (not verifiable from source).
