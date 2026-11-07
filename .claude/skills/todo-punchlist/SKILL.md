---
name: todo-punchlist
description: Drive a long autonomous (usually overnight) run through docs/current/TODO.md - numbered punchlist grouped by effort, owner confirms scope, decisions settled during scoping, then batches handed to orchestrators, each finished slice committed to master, progress tracked in a compaction-proof tracker, summary at the end. Use when the user says "todo punchlist", "overnight run" or asks to work through TODO.md autonomously.
---

# TODO punchlist

Two halves: an **interactive** half (phases 1-4, owner present) that settles scope and every decision
it can, and an **autonomous** half (phases 5-8, owner away) that never stops to ask. The main session
plans, dispatches, reads short reports and keeps the tracker; it reads no diffs and briefs no
implementers itself. CLAUDE.md applies throughout (dog-fooding, `Git` scope `listed`, ASCII only).

## 1. Punchlist

- Read `docs/current/TODO.md` yourself (one small file) and the latest
  `.claude/handoff/*_todo_review_*.md` record if there is one. Show a numbered list, one sentence per
  item, grouped **Quick wins / Small / Medium / Large / Needs owner or blocked**, cheapest first. Give
  each item its TODO.md line number. Mark `[READY]` items `(ready)` (settled by a todo review) and
  `[PARKED]` items `(parked)`, as well as the "Needs owner review" section and `PARKED` lines in the
  memory index.
- Items that need several decisions or a design are better settled by a `todo-review` session
  first: say so and suggest it rather than scoping them overnight.
- The numbers are permanent for the run: tracker, briefs, commit messages and the summary use them.

## 2. Confirm scope

The owner picks the items (ranges, groups, exclusions). For the parked items, ask about each one in
or near the selection. They may have been parked when they were added and since become good
overnight candidates. Give a recommendation for each (include / leave).

## 3. Scope and settle decisions (owner still present)

- Dispatch `explore-tier2` to scope the selected items (at most two at once, split by area). Each
  item comes back as one of: `CLEAR` (file:symbol anchors, acceptance test), `DECISION` (the question,
  options, recommendation), `NEEDS-DESIGN`, `STALE` (evidence it is already done), `LIVE-WINDOW`.
  Write the anchors into the tracker's Scoping notes (they are unverified until a batch re-checks them).
  For a `[READY]` item, the scoper reads the linked doc's `## Decisions` section and treats those as
  owner rulings: do not ask them again; report `DECISION` only for a question they do not cover.
- Ask every `DECISION` with `AskUserQuestion`, up to 4 per call, recommended option first. A simple
  one is settled now. Otherwise the owner says defer: mark it `DEFERRED (owner)`, do not dispatch it.
- `NEEDS-DESIGN` items are deferred without asking, unless the owner wants to design one now.
- **Live-window steps** are actions on the owner's running environment rather than the repo:
  restarting other VS Code windows or other sessions' servers, `/mcp` reconnect or fresh-session
  checks of the client tool list, editing `C:\Users\Administrator\.mcp.json` or user-profile
  settings, publishing hooks to the profile (`scripts/publish-agent-identity-hook.ps1`), anything
  that needs two real windows. Unattended, they disrupt other sessions or need a human at the client.
  Explain this to the owner and ask whether to do the code part and put the live step in the morning
  checklist (the usual answer).

Standing rulings (do not ask again; copy into the tracker):
- Restart your **own** server instance freely when it is idle (no agent mid-operation).
- Model-eval runs are allowed. First query `http://192.168.1.112:1234/api/v1/models` and `.113`
  (`loaded_instances` non-empty) for a loaded `qwen3.5` variant; pass its exact key with `-Model`.
  Never load or unload models. If neither host has one, defer the eval items.
- Never push. A decision that comes up during the autonomous half is deferred, not asked.

## 4. Tracker (write before the first dispatch)

Path `.claude/handoff/<yyyy-MM-dd>_todo_punchlist_<sid8>.progress.md` (`<sid8>` from the journal
filename); overwrite `.claude/handoff/LATEST.md` with its filename. Add a `project_` memory file and
MEMORY.md line "READ <tracker> first" so it survives compaction. Only the main session writes the
tracker, after every state change (dispatch, report, commit, decision).

```markdown
# Punchlist run: TODO.md items <range> (started <date>)
READ THIS FIRST after compaction or in a resumed session. Journal: <path>
## Owner rulings            (scope, exclusions, decisions from phase 3, standing rulings)
## Rules to keep following  (tiers from .claude/implementer-tiers.json, 0-failure test baseline, commit rule)
## Item status
| # | Item | L | Status |    L = TODO.md line at start; match by text later, lines shift
## Batches                  (letter, items, state, orchestrator agent id)
## In flight
## Scoping notes            (anchors from phase 3, unverified)
## Commits                  (hash, items)
## Morning checklist        (live-window steps, things to check by hand)
## Deferred decisions       (each one phrased as a question the owner can answer quickly, with a recommendation)
```

Status: `TODO` / `WIP` / `DONE (hash)` / `PARTIAL (what is left)` / `DEFERRED (why)` / `STALE (evidence)`.

## 5. Batches

- Group `CLEAR` items into batches A, B, C... by code area so no two batches touch the same files;
  cheapest first, parked items last. Run **one batch at a time** (an orchestrator plus its child is
  already the two-agent limit, and Build/RunTest cannot overlap).
- Before each batch: `Git(status)`. Other sessions may be dirty in the tree; name their files in
  the brief as not ours.
- Dispatch one `orchestrator` per batch. Its brief points at the tracker rows rather than restating
  them, and says:
  - run every child in the foreground (`run_in_background: false`); load `dispatch-implementer`;
    re-verify scoping anchors before briefing; append each dispatch to `.claude/journal/implementer-ledger.md`
  - build and run relevant tests per slice; commit each finished slice with `Git` scope `listed`,
    message naming the item numbers, `Co-Authored-By` trailer; never push
  - close finished entries with `scripts/Close-TodoItem.ps1 -Prefix -Title -Evidence`; shrink
    partial entries in TODO.md to what is left; leave `// TODO` markers per CLAUDE.md
  - defer anything needing design or owner judgement and keep going; do not edit the tracker
  - final report, at most 15 lines: per item `#n STATUS (hash) - one line`, new TODOs, owner
    questions with a recommendation, tests run and result
- Read the report, update the tracker. Resume the same orchestrator with `SendMessage` for small
  follow-ups instead of spawning a new one.

## 6. After each batch

One full `RunTest` (plain call, nothing else running). Baseline is 0 failures. Re-run a failure
alone: if it passes, record it as a flake in the tracker; if it comes from another session's commits,
say so; otherwise it becomes the first item of the next batch.

## 7. Failures and stalls

A tool failure follows the CLAUDE.md blocker rule (blocker doc, item `DEFERRED (blocker <slug>)`),
then the run continues with other items. If an orchestrator stalls or returns without a report,
check `ListAgents`, record the state in the tracker and resume or re-dispatch the remaining items.
If usage runs low, the tracker is the handoff: bring it up to date, then follow `wrapup`.

## 8. Summary (end of run)

- Update TODO.md / CLOSED.md leftovers and the tracker; mark the `project_` memory closed.
- Final message: commits per batch, the last full-run result, counts done/partial/deferred/stale,
  the Morning checklist, Deferred decisions (question + recommendation each), new TODOs. Point to
  the tracker for detail; do not repeat it.
