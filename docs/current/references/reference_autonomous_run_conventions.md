# Reference: conventions shared by long agent-driven runs (todo-punchlist, implement-plan)

**Status:** CURRENT 2026-11-07. Distilled from the 2026-10-20 and 2026-11-07 TODO runs.

## Purpose

The `todo-punchlist` and `implement-plan` skills (and `todo-review` for the review half) share
standing rulings, a tracker, a dispatch contract and an end-of-run report. They live here so the
skills do not drift apart. Each skill says which sections it uses; the skill wins where it is more
specific.

## How it works

### Standing rulings (the owner gave these once; do not ask again, copy them into the tracker)

- Restart your **own** server instance freely when it is idle (no agent mid-operation); never
  another session's. After server source changes: `Build(level: fullBuild)`, then restart only when
  a live check needs it.
- Model-eval runs are allowed. First query `http://192.168.1.112:1234/api/v1/models` and
  `http://192.168.1.113:1234/api/v1/models` for an entry whose `loaded_instances` is non-empty and
  whose key is a `qwen3.5` variant; pass that exact key with `-Model`. Never load or unload models.
  If neither host has one, defer the eval work.
- Commit each finished slice to master; never push.
- Decisions: with the owner present, ask with `AskUserQuestion`, recommended option first. With the
  owner away, defer the decision (tracker "Deferred decisions", with a recommendation) and continue
  with work that does not depend on it.

### Live-window steps

Actions on the owner's running environment rather than the repo: restarting other VS Code windows
or other sessions' servers, `/mcp` reconnect or fresh-session checks of the client tool list,
editing `C:\Users\Administrator\.mcp.json` or user-profile settings, publishing hooks to the profile
(`scripts/publish-agent-identity-hook.ps1`), anything that needs two real windows. Unattended they
disrupt other sessions or need a human at the client. Usual ruling: do the code part, put the live
step in the tracker's morning checklist. Restarting your own idle instance is not a live-window step.

### Tracker

- Path `.claude/handoff/<yyyy-MM-dd>_<kind>_<slug>_<sid8>.progress.md` (`<sid8>` from the journal
  filename; the folder is git-ignored). Overwrite `.claude/handoff/LATEST.md` with its filename.
- Add a `project_` memory file and a MEMORY.md line "READ <tracker> first" so it survives
  compaction; at the end mark it closed and move the index line to MEMORY_CLOSED.md.
- Only the main session writes it, after every state change (dispatch, report, commit, decision).
  Dispatchers report; they do not edit it.

Common sections (a skill adds its own, for example Scoping notes):

```markdown
# <Run kind>: <subject> (started <date>)
READ THIS FIRST after compaction or in a resumed session. Journal: <path>
## Owner rulings            (scope, decisions, standing rulings from this reference)
## Rules to keep following  (mode, tiers from .claude/implementer-tiers.json, 0-failure test baseline, commit rule)
## Status
| # | Item or step | Ref | Status |
## Batches                  (letter, items, state, agent id)
## In flight
## Commits                  (hash, items)
## Morning checklist        (live-window steps, things to check by hand)
## Deferred decisions       (each a question the owner can answer quickly, with a recommendation)
```

Status values: `TODO` / `WIP` / `DONE (hash)` / `PARTIAL (what is left)` / `DEFERRED (why)` /
`STALE (evidence)`.

### Dispatch modes

- **Direct:** the main session briefs `implementer` / `implementer-sweep` / `test-writer` itself
  (load `dispatch-implementer` first), one slice at a time, reads the report, then runs `Build` and
  the relevant `RunTest` and commits. For small, tier-sized work.
- **Batched:** one `orchestrator` per batch; **one batch at a time** (an orchestrator plus its child
  is already the two-agent limit, and Build/RunTest cannot overlap). Batches group work by code area
  so no two touch the same files. The brief points at tracker rows rather than restating them and says:
  - run every child in the foreground (`run_in_background: false`); load `dispatch-implementer`;
    re-verify scoping anchors before briefing; append each dispatch to
    `.claude/journal/implementer-ledger.md`
  - per slice: `Build` plus `RunTest` filtered to the touched test classes, with code and tests in one
    implementer dispatch when they fit the tier (`dispatch-implementer`, "Tests and test runs"); no
    full project runs (the main session runs them in a worktree after the batch)
  - commit each finished slice with `Git` scope `listed`,
    the skill's message rule and the `Co-Authored-By` trailer; never push
  - leave `// TODO` markers per CLAUDE.md; defer anything needing design or owner judgement and keep
    going; do not edit the tracker
  - final report, at most 15 lines: per item `#n STATUS (hash) - one line`, new TODOs, owner
    questions with a recommendation, tests run and result
- Before each dispatch or batch: `Git(status)`. Name other sessions' dirty files in the brief as not
  ours. Resume a finished agent with `SendMessage` for small follow-ups instead of a fresh spawn.

### After each batch (or each Direct step that changed code)

One full run per affected test project after a batch, started by the main session in the background
with `scripts/Invoke-WorktreeTests.ps1 -RepoPath c:\tmp\rs-testtree -Ref <batch's last commit>` (separate
worktree, so it does not block the next batch's builds); the next batch is dispatched at once. Inside a batch and
after a Direct step, filtered runs of the touched test classes only (owner rule 2026-11-09: catching a
failure one batch late is acceptable; bisect the batch's commits to locate it). Baseline is 0 failures. Re-run a failure alone: if it passes, record it as a flake; if
it comes from another session's commits, say so; otherwise it is the next thing to fix.

### Failures and stalls

A tool failure follows the CLAUDE.md blocker rule (blocker doc, item `DEFERRED (blocker <slug>)`),
then the run continues with other work. If a dispatcher stalls or returns without a report, check
`ListAgents`, record the state in the tracker and resume or re-dispatch. If usage runs low, the
tracker is the handoff: bring it up to date, then follow the `wrapup` skill.

### End-of-run summary

Commits per batch, the last full-run test result, counts done/partial/deferred/stale, the morning
checklist, deferred decisions (question + recommendation each), new TODOs. Point to the tracker for
detail; do not repeat it.

## Gotchas

- An orchestrator that backgrounds its children stalls: their reports go to the main session, not
  to it. Always brief `run_in_background: false`.
- The `enforce-dogfood.ps1` hook refuses an implementer brief that does not match the
  `dispatch-implementer` template (for example `Symbols (...)` instead of a line starting `Symbols:`).
- A new `[McpServerToolType]` class is invisible to the running session even after a restart; verify
  it with `LaunchTestInstance` (`reference_live_server_freshness.md`) and leave the in-session check
  for a fresh chat.
- `"isServerBinaryStale": true` only means a newer DLL exists; it matters only when the next step
  depends on the new server behaviour.
