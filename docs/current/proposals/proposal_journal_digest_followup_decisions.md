# Decisions on the deferred items from the 2026-10-08 journal-digest work

**Status:** DECISION 2026-10-09. Owner decisions recorded for the items deferred at the end of the journal-digest implementation; none of the changes below is built yet unless marked.

## Motivation

The journal-digest session (handoff `.claude/handoff/2026-10-09_4f9f5991.md`) implemented eight plans and
deferred the items that needed an owner decision. This doc records each decision so planners and
implementers do not re-ask. Plans for the build work live in `docs/current/plans/`.

## Proposal

### Decided

| # | Item | Decision |
| --- | --- | --- |
| 1 | Halt wording and recovery tools | Reword the halt message to name the cause, the recovery call, and that read-only tools still work. Merge `ListExternalDiskChanges`, `AcknowledgeExternalFileChanges` and `IsSessionHalted` into one tool `ExternalFileDrift(operation: status \| list \| acknowledge, files?)`. Additionally the request filter stamps `isSessionHalted` and `sessionHaltReason` (plus the recovery guidance) onto responses while a breaker is tripped, so recovery does not need a separate discovery call. |
| 2 | Partial acknowledge | Acknowledging some drifted files clears the session-halt latch. Files not acknowledged stay flagged, so the next write that touches one re-trips the halt. Behaviour to be documented in the tool description and `reference_architecture_map.md`; revisit after real use. |
| 3 | Built-in `Read` on in-repo `.cs` | Blocked by `enforce-dogfood.ps1`, with the existing bypass routes. Agents that have no MCP tools but need to read C# (for example `failure-root-cause-analyst`) get the read-only MCP tools (`ReadFile`, `GetMethodSource`, `Search`) in their frontmatter first. |
| 4 | Enum case | Argument validation repairs a wrong-case enum value when exactly one case-insensitive match exists and says so in the result (`'nunit' -> 'NUnit'`). The pinned test `Call_WithWrongCaseName_IsRejectedWithClosestSuggestion` is changed to match. Rationale: case does not change tool behaviour, so rejecting it only costs the model a turn. |
| 5 | CLAUDE.md "Out of scope" | Add "no Write/Edit/shell writes on .cs files" to the slice-contract Out of scope line. |
| 6 | `build.ps1` HTTP copy | Delete the dead path. Add a separate `Launch-RoslynSentinelHttpServer.ps1` (optional mode, tools, port) that builds a fresh server into its own directory. |
| 7 | Orientation breaker threshold | Keep 10 until the journals show false trips. |
| 9 | Offload stub | Add `solutionRoot` to the offload stub so its paths have a base. |
| 10 | `.roslynsentinel/largeresults` retention | Sweep at server startup; auto-delete files older than 7 days (by mtime, that folder only). |
| 12 | Scoped operation ledger | Active but opened only by `MoveMember`; review and extend to more tools. Tracked in `TODO.md`. |
| 13 | RenameSymbol merge | Not building. The CS0111/CS0121 refusal is correct; add a code comment at the refusal pointing here so it is not re-investigated. |

| 11-16 | Larger items | Each gets a plan under `docs/current/plans/`; ship order comes from the plans. |
| 14 | Build result shape | Redesign what `Build` returns (owner questions in "Open questions"); no unconditional stdout/full-output offload. |

### Still open

- **#8 Solution auto-load.** The launcher appends `--solution=<repoRoot>\RoslynSentinel.slnx` when the cwd is
  inside the repo (`scripts/roslynsentinel-mcp-launch.ps1:327`). The server's startup load is fire-and-forget
  (`WarmupAndAutoLoad*`, "unawaited" per `Utilities.PlanStepRunner/Program.cs:191`), so tool calls made during the
  load get "load in progress, retry". The manual `LoadSolution` tool does await its load
  (`WorkspaceProjectManagementImpl.LoadSolution`) but has no timeout beyond the request token. Owner direction: make
  the load blocking for callers, with a 30-second cancellation timeout, i.e. a call that arrives mid-load waits for
  it instead of returning "retry". The plan must cover the timeout message and the launcher heuristic review. The
  `loadInProgress` status field waits on that.

## Alternatives considered, not pursued

- Fold the drift tools into `McpServerStatus`: it is a read-only snapshot and acknowledging changes halt state.
- Fold them into `McpServerControl`: that tool is process lifecycle.
- Keep case-sensitive enums: pinned by a test, but the pin protects nothing, because case never affects behaviour.

## Open questions

- **Build result shape.** What an agent needs from 1, 10, 100 or 1000 errors across files and projects: for a green
  build, probably only the projects built; for a failed one, an error summary (counts by project/file/code, first N
  details). Is stdout needed at all? Should a failed solution build re-run project by project in dependency order
  (for example `RoslynSentinel.Common` first) so the error list is the upstream root cause and not thousands of
  downstream test-project errors? Owner leaning yes on the focused-list idea; the plan must measure real outputs.
- Which of the larger items (same-file call-site relocation, `MoveMember` to static, `RunTest` output, `ReadFile`
  `lineEndings`/`hasBom`, Git tag/stash/worktree/hunk staging) ship first is decided by the planner output, not here.

## Cost / risk

- Merging the drift tools renames three tools: docs, hooks, tests, prompts and the generated architecture docs all
  reference the old names.
- Blocking `Read` can strand an agent that lacks MCP tools; the frontmatter change must land first.

## Related

- `docs/current/plans/plan_session_halt_recovery_and_git_gaps.md`, `docs/current/plans/plan_agent_tooling_hooks_and_small_ergonomics.md`
- `docs/current/proposal_scoped_operation_ledger.md`
