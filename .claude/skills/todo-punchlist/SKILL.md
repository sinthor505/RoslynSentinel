---
name: todo-punchlist
description: Drive a long autonomous (usually overnight) run through docs/current/TODO.md - numbered punchlist grouped by effort, owner confirms scope, decisions settled during scoping, then batches handed to orchestrators, each finished slice committed to master, progress tracked in a compaction-proof tracker, summary at the end. Use when the user says "todo punchlist", "overnight run" or asks to work through TODO.md autonomously.
---

# TODO punchlist

Two halves: an **interactive** half (phases 1-4, owner present) that settles scope and every decision
it can, and an **autonomous** half (phases 5-8, owner away) that never stops to ask. The main session
plans, dispatches, reads short reports and keeps the tracker; it reads no diffs and briefs no
implementers itself (Batched mode). Read
`docs/current/references/reference_autonomous_run_conventions.md` first: standing rulings, live-window
steps, tracker, dispatch contract, failures and summary are defined there. CLAUDE.md applies throughout.

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
- `LIVE-WINDOW` items: explain what the live step involves (reference, "Live-window steps") and
  confirm the usual ruling: code part overnight, live step on the morning checklist.

## 4. Tracker (write before the first dispatch)

Per the reference, `<kind>_<slug>` = `todo_punchlist`. Extra sections: `## Scoping notes` (anchors
from phase 3, unverified). The Status table's Ref column is the TODO.md line at the start; match
entries by text later, as lines shift.

## 5. Batches

- Batched mode only (reference, "Dispatch modes"). Group `CLEAR` items by code area; cheapest first,
  parked items last.
- Brief additions to the reference's orchestrator contract: commit messages name the item numbers;
  close finished entries with `scripts/Close-TodoItem.ps1 -Prefix -Title -Evidence`; shrink partial
  entries in TODO.md to what is left.
- Read the report, update the tracker.
- Every progress update to the owner leads with done/total, remaining, time so far and a rough time
  left, e.g. "7 of 16 items done, 9 remaining; ~50 min so far, ~2 h left (about 17 min/batch)". Log
  each batch's duration (`duration_ms` from its notification) in the tracker and estimate from it.

## 6. After each batch

Full `RunTest` (`suite: "full"`) and flake handling per the reference. A real new failure becomes the first item of
the next batch.

## 7. Failures and stalls

Per the reference.

## 8. Summary (end of run)

Update TODO.md / CLOSED.md leftovers and the tracker; close the `project_` memory; final message
per the reference's end-of-run summary.
