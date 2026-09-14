# Run summary — self-run `manual-selfrun-20260914-004605`

Punch list of issues found while executing steps 02-12 of this self-run. Full narrative detail
for every item lives in the run's own
`C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md` — this doc indexes
each issue to its distilled writeup so nothing has to be re-derived from the raw log.

## Punch list — new tool/environment defects found this run

| # | Issue | Severity | Doc |
|---|---|---|---|
| 1 | `ReplaceSnippet`/`Member(replace)` report fabricated success on formatting-only no-op edits — real `changeId` returned, zero bytes actually written | High — silent success, wrong data | [finding_writepath_noop_fabricated_success_formatting_only_edits.md](finding_writepath_noop_fabricated_success_formatting_only_edits.md) |
| 2 | `Git` status/diff/commit silently resolve to the primary repo's `master`, not the active worktree — blocked a real commit this run | High — silent success, wrong data; blocked forward progress once | [finding_git_tool_worktree_resolution_wrong_repo_root.md](finding_git_tool_worktree_resolution_wrong_repo_root.md) (full detail: [blocking_error_git_tool_commit_reports_clean_tree_worktree.md](blockers/blocking_error_git_tool_commit_reports_clean_tree_worktree.md)) |
| 3 | `Member(add, typedKind: ...)` has no valid value for adding a whole method, class, or record — crashes with a raw `JsonException` on the natural guess | Medium — crash, but recoverable via a documented workaround | [finding_member_typedkind_missing_method_and_type_add_paths.md](finding_member_typedkind_missing_method_and_type_add_paths.md) |
| 4 | `Build(quickBuild)` intermittently reports a ~26,700-line phantom error cascade on a clean worktree; `fullBuild` shows 0 errors on the same tree | Medium — would have derailed step 10 if trusted as instructed | [finding_build_quickbuild_spurious_error_cascade.md](finding_build_quickbuild_spurious_error_cascade.md) |
| 5 | `Git`'s array-typed `paths`/`files` params, and an invalid `operation` value (`show`), both crash with a raw `JsonException` instead of a clean rejection | Medium — crash, recoverable via one-string-per-call workaround | [finding_git_tool_array_param_and_invalid_operation_crash.md](finding_git_tool_array_param_and_invalid_operation_crash.md) |
| 6 | `Git(diff)` renders non-ASCII characters as mojibake (`—` → `ΓÇö`); the real file bytes are correct | Low — cosmetic, ground truth always recoverable via `ReadFile` | [finding_git_diff_output_mojibake_encoding_defect.md](finding_git_diff_output_mojibake_encoding_defect.md) |

## Cross-referenced — already tracked elsewhere, not duplicated

| # | Issue | Where it's tracked |
|---|---|---|
| 7 | A manually-launched HTTP server can execute stale pre-edit code while `Build`'s own check spawns a separate, correctly-fresh `dotnet build` — false sense that a running server reflects current source | Existing memory: `feedback_stale_server_before_rebuild` |
| 8 | `Git` tool has no branch/push/checkout/worktree/stash operations at all, forcing a shell fallback | Existing: [docs/current/TODO.md](TODO.md) (distinct from #2 above — that's wrong resolution of *existing* ops, this is *missing* ops entirely) |

## Fixed within this run, not carried forward as open items

- **BatchTypes.cs formatting defect** (DirectiveKind/BreakerOpen split onto separate lines) and
  **`RunFullBuildAsync` missing `BuildNotRun` guard on a zero-project solution** — both surfaced
  by an independent review of the run branch's diff against `master`, fixed with a regression
  test, committed `3bd391e`.
- **`EolUtilitiesTests.cs` `ProjectId`/`DocumentId` mismatch** — a genuine pre-existing bug found
  and fixed within step 06, no longer open.

## Explicitly not defects (noted in the log, no action needed)

- `RunTest(scope:"file")`/`Build(verifyLevel:...)` correctly rejecting bad input — clean,
  informative rejections, not a gap.
- Self-inflicted mistakes not attributable to the tools: a quote-escaping error, a test-design
  bug, an XML doc-comment double-escaping gotcha (the tool escapes entities for you — don't
  pre-escape), and two instances of using plain `Edit`/`Write` instead of MCP tools (a
  dogfooding-mandate violation on the model's part, not a tool defect — did trigger a real
  `SessionHalted` from external-drift detection, confirming that guard works as intended).

## Outcome

Branch pushed to `origin` (`sinthor505/RoslynSentinel`); PR #1 opened at
https://github.com/sinthor505/RoslynSentinel/pull/1.
