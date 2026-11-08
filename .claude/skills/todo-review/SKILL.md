---
name: todo-review
description: Interactive review of docs/current/TODO.md with the owner present - triage open items, investigate the ones that need a decision or design, settle them with recommendations, and record each outcome in the repo ([READY], designed, [PARKED] or closed) so a later todo-punchlist run starts from settled items. Use when the user says "todo review" or asks to settle, solidify or groom TODO.md items. Never implements.
---

# TODO review

Goal: every reviewed item leaves the session as `[READY]` (decided and scoped), designed (doc
written), `[PARKED]` (reason and unpark trigger) or closed (stale or dropped). The owner is present
throughout, so ask freely; the session never edits C# or dispatches implementers. CLAUDE.md applies
(ASCII only, `Git` scope `listed`, doc genres per `docs/current/templates/README.md`).

## 1. Triage list

Read `docs/current/TODO.md` yourself and add three more sources:
- the "Needs owner review" section of TODO.md;
- `PARKED` lines in the memory index;
- the "Deferred decisions" sections of earlier `.claude/handoff/*_todo_punchlist_*.progress.md` and
  `*overnight*.progress.md` trackers (skip entries already answered in a later review record).

Not `// TODO` comments in code (too many; a sweep of their own). Show a numbered list, one sentence
per item, grouped by what blocks it: **Suspected stale / Needs decision / Needs design /
Under-specified (no clear "done") / Already actionable**. Give each its TODO.md line or source path.
Existing `[READY]` items go under Already actionable unless something has changed.

## 2. Owner picks

The owner chooses which items to review. The numbers are permanent for the session.

## 3. Investigate

Dispatch `explore-tier2` (at most two at once, split by area; `model: sonnet` for a hard design
question). Per item it returns, in at most about 15 lines:
- what the item is, in plain words, and what goes wrong if it stays undone;
- current state with evidence (`file:line`, commit, test name): this catches stale items;
- each decision point separately, each answerable in one choice;
- per decision, 2-3 options, each with what concretely changes (files, tools), effort
  (Small/Medium/Large) and risk or trade-off, plus a recommendation;
- for an implementable item, the acceptance check (test or observable result).

Do not read the code yourself beyond a small verification read.

## 4. Settle

Ask with `AskUserQuestion`. **One item per question**: never bundle several items into one
question or one multi-select. An item with several independent decisions gets one question per
decision, each repeating the item context. Up to 4 questions per call, grouped by item rather than
packed with unrelated ones.

Each question text stands alone and carries:
- the item number, title and TODO.md line;
- the problem in one or two plain sentences;
- the finding the choice rests on, with evidence;
- what this choice decides.

Each option's description names the concrete outcome (what gets done or recorded, effort, risk);
its `preview` shows the exact TODO.md line or Decisions entry that choice would write (the code or
text instead, for code-shape or wording choices). Recommended option first, labelled
"(Recommended)". When detail does not fit, put it in the preview rather than shortening the
question.

Per question the owner answers, or says "explain in more detail", "design now", "park" or "drop".
"Explain in more detail" gets a fuller restatement of the same question, not a new investigation.
Follow-up questions that an answer raises go in the next call, not a new investigation, unless the
answer invalidates the scoping.

## 5. Record each outcome (in the repo, not just locally)

- **Decided:** add a dated `## Decisions` section (or entry) to the item's linked doc: the question,
  the choice, one line of why, the acceptance check. With no linked doc, the TODO.md line itself
  carries the decision if it fits in one sentence; otherwise create the doc per templates/README.md.
  Rewrite the TODO.md line as one actionable sentence with the tag `[READY]`.
- **Design now:** small designs are worked out in the session and written up by
  `design-doc-scribe` in the folder templates/README.md prescribes; the TODO.md line links it and
  becomes `[READY]` if the design leaves no open question. A large design gets a stub doc (problem,
  constraints, open questions) and a suggestion to design it in a fresh chat; the line stays `[OPEN]`.
- **Stale / dropped:** `scripts/Close-TodoItem.ps1 -Prefix -Title -Evidence` (evidence: commit,
  test, or "dropped by owner <date>: <reason>").
- **Parked:** tag `[PARKED]`; the line says why and what would unpark it.
- A source outside TODO.md (memory PARKED line, tracker deferred decision) that is now settled: put
  the result into TODO.md or close it, and update or close the memory entry.

TODO.md stays one line per entry; design prose goes in the linked doc, never in TODO.md.

## 6. Review record and commit

- Write `.claude/handoff/<yyyy-MM-dd>_todo_review_<sid8>.md` (`<sid8>` from the journal filename):
  one table row per item `# | item | outcome | where recorded`, then items left unreviewed and
  questions still open. The next `todo-punchlist` run reads it.
- `Git(status)`, then commit TODO.md, CLOSED.md and the docs written this session with scope
  `listed` and the `Co-Authored-By` trailer. Never push.
- Final message: counts per outcome, the commit hash, the review record path, and the `[READY]`
  items grouped by effort as a ready-made punchlist candidate.
