---
name: implement-plan
description: Review a plan or proposal under docs/current/, settle its open decisions with the owner, then drive it to the end - Direct mode (main session dispatches implementers) for small plans, Batched mode (orchestrator per batch) for large ones - committing each slice with Plan/Closes trailers, tracking progress in a compaction-proof tracker, and closing the plan when done. Use when the user says "review and implement <plan>", "implement <plan/proposal path>" or asks to drive a plan or proposal to completion.
---

# Implement a plan

Phases 1-3 are interactive (owner present); phases 4-6 drive the plan to the end. Read
`docs/current/references/reference_autonomous_run_conventions.md` first: standing rulings,
live-window steps, tracker, dispatch modes, failures and summary are defined there. Plan doc rules
(Status line, `Plan:`/`Closes:` trailers, `plans/closed/`) are in `docs/current/templates/README.md`.
CLAUDE.md applies throughout.

## 1. Locate and review

- Resolve the doc from the name or path (`plans/plan_<slug>.md`, `proposals/proposal_<slug>.md`;
  `Glob` on `docs/current/**/*<name>*.md`; built-in tools are fine for Markdown). Several matches:
  ask which.
- **Proposal with no steps:** dispatch `design-doc-scribe` to draft `plans/plan_<slug>.md` from
  `templates/plan.md`, linking the proposal. Show the owner the step list before reviewing.
- Dispatch `explore-tier2` (`model: sonnet` for a plan over ~6 steps or crossing projects) to review
  the plan against the current source. Per step, at most 6 lines:
  - status: done already (`Git` log for `Plan: <id>` trailers, and the code), stale anchors, or not started;
  - well-formed: concrete "Done when", definitions before call sites, one-way layering;
  - rules it touches: DTO conventions (`dto-conventions` skill), the three things a new MCP tool needs,
    a new `[McpServerToolType]` (live check only via `LaunchTestInstance` this session), live-window steps;
  - size: fits implementer tier D1 or S0 (`.claude/implementer-tiers.json`), or needs `implementer-senior`;
  - open questions, each with options and a recommendation.
  Also: is another session working on it (Status IN PROGRESS by someone else, recent `Plan:` commits,
  dirty files in the plan's areas)? If so, stop and ask.

## 2. Settle

- Open decisions = the plan's "Risks and open decisions" plus the review's questions. Ask them with
  `AskUserQuestion`, up to 4 per call, recommended option first, previews for code-shape choices.
- Record each answer as a dated entry in the plan's `## Decision` section. A decision too big to
  settle now is deferred with the steps that depend on it (mark them in the plan).
- Ask once: **will the owner be present during the run?** Present: mid-run decisions are asked with a
  recommendation. Away: they are deferred (reference, "Standing rulings").
- Set the Status line to `APPROVED <date>. <one sentence>` and commit the doc changes
  (`Git` scope `listed`, `Plan: <id>` trailer).

## 3. Choose the mode (propose, owner confirms)

- **Direct:** 4 or fewer steps, each within D1 or S0, none needing a senior. The main session
  dispatches implementers step by step (reference, "Dispatch modes").
- **Batched:** anything larger. Batches are consecutive steps sharing a code area; one orchestrator
  per batch, one batch at a time.

## 4. Tracker (write before the first dispatch)

Per the reference, `<kind>_<slug>` = `plan_<slug>`. The Status table has one row per plan step; Ref
is the step heading. Owner rulings include the mode and the presence answer.

## 5. Execute (in plan step order)

- Each slice commits with a `Plan: <id>` trailer above `Co-Authored-By` (put this in every brief).
- After each Direct step or each batch: update the tracker, mark the step in the plan doc
  (`- Done: <hash>`), update its Status line to `IN PROGRESS <date>. Steps <n-m> done (<hashes>).`,
  and commit the doc. Run tests per the reference.
- Server source changed and a step needs a live check: `Build(level: fullBuild)`, restart your own
  idle instance, `LoadSolution`, then check.
- A step blocked by a deferred decision or a blocker: continue with steps that do not depend on it;
  stop when only dependent steps remain.
- A step that turns out larger than the review said: split it into compile-green slices (new member
  beside the old, `[Obsolete]`, `// TODO` for the rest, per CLAUDE.md) rather than one big dispatch;
  note the split in the plan.

## 6. Close

- One full `RunTest` per the reference.
- **All steps done:** Status `IMPLEMENTED <date>. <hashes>`; move the plan to `plans/closed/` and fix
  relative links; update the source proposal's Status line; close the plan's TODO.md entry with
  `scripts/Close-TodoItem.ps1`; the final commit carries `Closes: <id>`.
- **Not all done:** Status stays `IN PROGRESS` with what is left and why; shrink its TODO.md line to
  match; deferred decisions go to the plan's "Risks and open decisions" as well as the tracker.
- Close the `project_` memory; final message per the reference's end-of-run summary, plus the plan's
  final Status line.
