---
name: plan-design
description: Interactive design session that turns an idea, proposal, finding or issue doc (or a fresh chat) into an APPROVED plan that /implement-plan can run with no open questions - grounds every claim in current source, classifies each open question (owner decision, investigation, probe, implementer default), settles them one by one, reviews the design against the design checklist, designs tests and measurement up front, writes the plan with the plan-writer checks and passes an independent readiness review. Use when the user says "design a plan for <doc>", "flesh out <proposal>", "plan-design" or wants to turn an idea into an implementable plan. Never implements.
---

# Plan design

Goal: one or more `docs/current/plans/plan_<slug>.md` at Status `APPROVED` that a fresh session can
run with `/implement-plan` without asking anything. The owner is present throughout, so ask freely.
The session never edits C# and never dispatches implementers; probes and spikes live under the scratch
root, never in the repo. CLAUDE.md applies (ASCII only, doc genres per
`docs/current/templates/README.md`, `Git` scope `listed`). The design rules are
`docs/current/references/reference_design_review_checklist.md` (items 1-12); read it before phase 2.

**State lives in the draft plan.** Create it at the end of phase 1 and keep it current: every answer
goes into its `## Decision` log at once, every open question sits in "Risks and open decisions", and
the Status line reads `DRAFT <date>. plan-design phase <n>: <k> of <m> questions open.` After
compaction or in a new session, re-invoke this skill on the draft plan and resume from its Status line.

## Asking the owner

The `AskUserQuestion` dialog is narrow, not resizable, and drops newlines and formatting. So:
- **Simple choices** (a name, a yes/no, a pick between two short options): ask in the dialog. One
  topic per question, two only if both are small; more questions beat one overloaded question.
- **Anything else** (architecture, behaviour, wire shape, test strategy, anything one sentence cannot
  describe): first write it in the chat, one heading per question: the problem, the finding it rests
  on with evidence, each option with an example (code shape, output, call sequence) and its
  implications (files, effort, risk, what it rules out later), and a recommendation. Then ask in the
  dialog with short option labels that point back to the chat ("Q4: option B").
- **Separate turns.** Write the chat write-up and end the turn. Ask in the dialog on the next turn,
  or take the answer from the chat. While the dialog is open, chat text from the same turn is hidden
  (Claude Code issue #80576).
- **One decision per number.** Never use lettered sub-parts (Q9a/b/c); give each its own number. An
  answer like "as recommended" settles only the questions shown, each with its own recommendation, in
  that message.
- **Consequences go in the option.** An option that accepts a checklist exception, cuts scope or
  defers a feature says so in its label and description, for example "parse text (accepts item-6
  exception)" or "identifiers out of v1".
- **Dependencies:** say when a question will affect later ones ("Q3 decides whether Q5 and Q6 are
  needed"). For a dependent question, write "the options below assume Q2 = A; this still needs your
  answer". Write "settled by Q2 = A" only when the earlier answer decides it. Never write "depends on
  Q2: assumes A", which reads as settled. Ask a dependent question only after the one it depends on
  is answered, unless the dependency is stated.
- **Stable labels.** A settled decision gets a short label in the Decision log, for example "D9c:
  near-miss identifiers out of v1". Later text refers to it by that label and the same words, never a
  paraphrase.
- **Reopened decisions.** A question that changes an earlier decision says "Reopens Q2" and lists
  what changes downstream.
- Question text stands alone (item number and title, what the choice decides); recommended option
  first, labelled "(Recommended)". Up to 4 questions per call, grouped by topic.
- Effort: tag each option label S/M/L/DS (`plan-writer` skill, section 3a), in the chat write-up too.

## 1. Intake

- **From a doc:** read it. State back, in a few lines, the goal, the non-goals and what "it worked"
  would mean. Correct with the owner before going on.
- **From a fresh chat:** a short interview, one topic per question: the problem and its evidence, the
  constraints (hardware, hosts, layering, other plans), what "it worked" means, what is out of scope.
- If the owner is unsure whether to do it at all, write an idea or proposal doc instead (template) and
  stop; that is a valid outcome.
- Create the draft plan from `templates/plan.md`: Problem (linking the source doc), an empty
  Decision log, the questions known so far in "Risks and open decisions". The source doc stays as is.

## 2. Ground

Dispatch one `explore-tier2` (`model: sonnet` if the work crosses projects or the doc is long) with
the doc path, the checklist path and this brief. Per claim or anchor in the doc, one line:
- verified (current `file:line`), stale (what changed, with the commit), or hypothesis (untraced);
- numbers: reproducible from the named file or script, or not;
- prior attempts: `closed/` folders, `CLOSED.md`, `git log` for the doc ID and the key symbols;
- overlaps: TODO.md and INBOX.md entries, plans IN PROGRESS in the same area (especially ones
  splitting or moving the files this design touches), dirty files or peer sessions there (`ListAgents`);
- gaps: what the doc assumes but never names (config entry point, DTOs, transcript or output fields,
  parsers and scripts that read them, tests, docs to update);
- for the checklist: existing helpers, DTOs, constants and factories near the change (items 1-3); the
  project, layer and line count of every file and class the design touches (item 4); callers of every
  member it changes and the wire surface it affects (item 5); test fixtures already used for this
  area (item 9).

Show the owner a short summary: stale claims, hypotheses the design rests on, the gaps, reuse
candidates, and any touched class over 1500 lines or in `Common`. Do not read code yourself beyond a
small verification read.

## 3. Question inventory

List every open question: the doc's own, the gaps from grounding, and these standard ones when they
apply - defaults and configuration surface, failure and cancel paths, backward compatibility of
persisted output, tests and measurement (phase 6), and whether the doc holds **independent features**
(each useful alone, or one's design depending on another's measured result). A split is an owner
decision: recommend separate plans when one feature's design depends on another's outcome.

Classify each question, show the numbered list grouped by class (with dependencies between questions
marked), and let the owner reclassify:

| Class | Settled by |
| --- | --- |
| owner decision | the owner (see "Asking the owner") |
| investigation | `explore-tier1` (lookups) or `explore-tier2` (multi-file); at most two at once |
| probe | a measurement in this session (phase 4, "Probes") |
| implementer default | low risk and reversible: the step states the default and it is no longer open |

The numbers stay fixed for the session. Group the owner decisions by topic, upstream groups first;
phase 4 settles one group per round.

## 4. Resolve (loop until nothing is open)

- **Order:** investigations and probes first when an owner decision depends on their result; run
  independent ones in parallel with the owner's questions.
- **One group per round.** Before asking, run a quick checklist pass (items 1-7) on each option, so
  its checklist cost is known. A group whose questions depend on each other gets a combinations
  table: one row per viable combination, with columns for what is built, what is left out, checklist
  exceptions, effort, and the questions it opens later. Mark one row recommended; the owner picks a
  row. Small independent questions can still be asked one at a time.
- **Design so far.** After each group, update a `## Current design` section in the draft plan (built,
  left out, exceptions, still open) and post it in the chat, so the owner sees the end result of the
  answers so far.
- **Probes:**
  - Keep them small (a few requests, minutes). Scripts and output go under
    `<scratch root>\plan_design_<slug>\`; never commit them.
  - Before touching a model host, run `scripts\roslynsentinel-evalqueue.ps1 hosts` and
    `status -ActiveOnly`. A host that is reachable, already has the needed model loaded and has no
    running or queued job is idle: run the probe at once. Otherwise ask the owner first.
  - Never load or unload a model for a probe without owner OK (a reload resets the sampler).
    Anything larger than a probe (an eval batch) needs owner OK and goes through the evalqueue.
  - Record the exact request, the result and the date in the Decision entry, so the plan cites a
    measured fact.
- **Record:** each answer becomes a dated `## Decision` entry (question number, choice, one line of
  why, evidence). Remove it from "Risks and open decisions"; update the Status line count.
- A new question raised by an answer joins the inventory with the next number. A question too big for
  this session is deferred only with the owner's agreement: the steps depending on it are marked, and
  the plan cannot reach APPROVED until they are cut or the question is settled.

## 5. Design review

Once the main questions are settled, check the design against checklist items 1-7 and write the
result in the chat, one line per item: pass, or the problem with evidence. Each failure becomes a new
numbered question back in phase 4, usually one of:
- change the design (a named type instead of a tuple, a shared constant, a typed exception, a
  structured field instead of text, a different home for the logic);
- add an enabling step (an extraction from an oversized class, a shared helper or factory, a
  single-source error/hint definition), text-neutral and early in the plan;
- an owner-approved exception, recorded in the Decision log with its reason. Each exception is its
  own explicit question, even when an earlier answer implied it; never record one as "approved via"
  another answer.

Item 7 always gets an explicit answer in the chat: the simplest version of the design, what small
change elsewhere would enable it, and why the chosen design is not more complex than needed.

## 6. Tests and measurement

Settle and record, before writing steps:
- **Test table** (checklist items 8-11): per test, name (class and method), behaviour covered,
  fixture, asserts (structured fields, not text), pass criterion, and a justification for any heavy
  fixture or out-of-process work. Every behaviour in the Decision log maps to at least one test.
- **Baseline:** the numbers the change is judged against, from existing runs (cite the file) or a
  baseline run that becomes a plan step ahead of the behaviour change.
- **Size:** blast radius per changed member (count from `InspectSymbol(aspect: blastRadius)` or
  `Search(mode: references)`), files and projects per step.
- **Eval or live work:** arms, scenarios, hosts, rounds per arm, pinned commit, outcome classes
  (`reference_model_eval_procedure.md`, "Scoring design"), and the decision thresholds agreed now,
  before any run: what result keeps the feature, what result drops or parks it.
- **Instrumentation** the measurement needs (output fields, parser updates) becomes plan steps.

## 7. Write the plan

Load `.claude/skills/plan-writer/SKILL.md` and follow it. The main session writes the plan (it holds
the decisions); dispatch `explore-tier1` to verify names, signatures and counts in bulk.
- Slice map (checklist item 12): each step lists its files; interdependent new code gets a contract
  skeleton first; pairs touch disjoint files; enabling and text-neutral steps come first.
- Every "implementer default" appears in its step with the default spelled out.
- Each step names its tests from the phase 6 table; eval steps carry the phase 6 thresholds.
- Show the owner the step list and the slice map before phase 8.

## 8. Readiness gate

Dispatch a fresh `explore-tier2` with `model: sonnet`, giving it the plan path and the checklist
path only (not this chat or the decisions' reasoning). Brief:
- for each step: could you write an implementer brief from this step alone? List every question you
  would have to ask, and every name, count or anchor that does not match the source;
- check the plan against every checklist item, citing the step that fails;
- run the checklist in `.claude/skills/implement-plan/SKILL.md`, phase 1 (status, well-formed, rules,
  size, parallel-ready, contract skeleton, open questions).

Each question or failure it returns goes back into phase 3 as a new numbered question. Repeat the
gate after fixes until it returns none (or only items the owner explicitly accepts). A repeat round
after a list of fixes may resume the same reviewer (`SendMessage`), asking for each finding to be
marked fixed or not plus any new ones; after a structural rewrite, use a fresh reviewer.

## 9. Finalize

- Add a dated Decision entry `Readiness (plan-design): independent review clean at <HEAD hash>`.
  `/implement-plan` reads this to skip re-settling.
- Status line: `APPROVED <date>. <one sentence>`.
- Source doc: Status `APPROVED <date>. Plan: plans/plan_<slug>.md` (several plans: list them).
- TODO.md: one `[READY]` line per plan, linking it (replace the source doc's line if it has one).
- `Git(status)`, then commit the plan(s), the source doc and TODO.md with scope `listed`, a
  `Plan: <plan id>` trailer and the `Co-Authored-By` trailer. Never push.
- Final message: the plan path(s), step count, lanes, the test table size, the measurement and its
  thresholds, decisions settled (count), owner-approved exceptions, the commit hash, and a suggestion
  to run `/implement-plan <plan>` in a fresh chat.
