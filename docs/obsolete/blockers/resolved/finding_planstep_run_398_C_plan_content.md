# PlanStepRunner run 20260910-013550-398 — Category C: Plan content

**Status: RESOLVED 2026-09-10.** C1/C2's live-run risk was actually closed by `50ce5b6`
(doc B, B1) in a separate session, independent of this doc's own remediation: the runner now
inlines each step's body by value into the model's prompt and never calls `ProjectDoc` for
step content at all, so a duplicate/misnumbered file can no longer be substituted in. The
tree consolidation below (retiring the old 13-step split to `docs/obsolete/`, replaced by a
stub at `docs/current/plans/plan-eval-defect-remediation-v2-steps.md`) was completed as
cleanup on top of that, not as an urgent fix. The "confirm before deleting" open question
below is answered: the old tree was confirmed superseded and moved. The `docs/tests/` paths
below are stale — the runner tree lives at `docs/testing/plan-eval-defect-remediation-v2-steps-runner/`.

**Source run:** `PlanStepRunner/20260910-013550-398/01-baseline/`
**Scope:** two plan-authoring defects (C1–C2). Tool defects are in
`finding_planstep_run_398_A_tool_defects.md`; harness in
`finding_planstep_run_398_B_harness.md`.

**Central finding:** two near-identical step-plan directories coexist under `docs/`, with
**different step numbering and contradictory gate instructions**. `ProjectDoc`'s basename
fallback (doc A, A1) resolved to the wrong one, and the wrong one told the model to advance.
The duplication is the underlying hazard; A1 only exposed it.

---

## The two directories

| | Old copy | Runner copy |
|---|---|---|
| Path | `docs/current/plans/plan-eval-defect-remediation-v2-steps/` | `docs/tests/plan-eval-defect-remediation-v2-steps-runner/` |
| Steps | 13 | 11 |
| Reachable via `ProjectDoc(docType:plan)` | Yes (it is under `plans/`) | **No** — see doc A, A1 |
| Scope-lock wording | **Absent** | Present in every step |
| Step 01 gate | "Proceed to `02-phase1-types.md`" | "Report the baseline counts and **stop**" |

```bash
$ ls docs/current/plans/plan-eval-defect-remediation-v2-steps/
00-index.md 01-baseline.md 02-phase1-types.md 03-phase1-engine-fix.md
04-phase1-tests.md 05-phase2-repro.md 06-phase2-trivia-intent.md
07-phase2-eol.md 08-phase2-fixture-tests.md 09-phase3-directivekind.md
10-phase3-finding-type.md 11-phase3-orientation-breaker.md
12-phase3-diffhunkanalyzer.md 13-final-verification.md

$ ls docs/tests/plan-eval-defect-remediation-v2-steps-runner/
00-index.md 01-baseline.md 02-phase1-types-and-engine-fix.md 03-phase1-tests.md
04-phase2-repro-and-trivia-intent.md 05-phase2-eol.md 06-phase2-fixture-tests.md
07-phase3-directivekind.md 08-phase3-finding-type.md 09-phase3-orientation-breaker.md
10-phase3-diffhunkanalyzer.md 11-final-verification.md
```

The runner copy merged old 02+03 into `02-phase1-types-and-engine-fix.md` and old 05+06 into
`04-phase2-repro-and-trivia-intent.md`, renumbering everything after. **Step numbers are
therefore reused with different meanings across the two trees** — old `07-phase2-eol.md` vs
runner `07-phase3-directivekind.md`, old `09-phase3-directivekind.md` vs runner
`09-phase3-orientation-breaker.md`. Only `00-index.md` and `01-baseline.md` collide on
filename, which is precisely why turn 1's substitution was silent.

---

## C1 — Duplicate plan trees with contradictory instructions (CRITICAL)

### The diff that caused the run to fail

`docs/tests/…-runner/01-baseline.md` (what the model **should** have received):

```markdown
# Step 0 — Baseline

**This step implements only this file.** Do not read, open, or act on any other plan step file
(e.g. via `ProjectDoc`) — another process runs each step as its own isolated task. Do not make
any code changes in this step. This is step 0 in the sequence; step 1.1 is
`02-phase1-types-and-engine-fix.md`, but you do not need to open it.
...
## Gate

No gate — this step only records a number. Report the baseline counts and stop — do not proceed
to any other step.
```

`docs/current/plans/…-steps/01-baseline.md` (what it **actually** received):

```markdown
# Step 0 — Baseline

## Prior state
...
## Gate

No gate — this step only records a number. Proceed to
[02-phase1-types.md](02-phase1-types.md) once you have the baseline counts.
```

Two differences, both decisive:

1. The scope-lock paragraph is **entirely absent** from the old copy.
2. The gate says the **opposite thing**: "Proceed to 02-phase1-types.md" vs "stop — do not
   proceed to any other step."

The model followed the instructions it was given. It completed the baseline correctly
(turns 2–4), then at turn 5 requested `02-phase1-types.md` — a filename that exists **only**
in the old tree — and began implementing it. At turn 22 it fetched `03-phase1-engine-fix.md`,
also old-tree-only, and continued into step 1.2.

Every safeguard against exactly this was written into the runner copy. None of it was ever
in the model's context.

### Why the old copy is still load-bearing

`docs/tests/…-runner/00-index.md` line 3 names the old plan as the source:

> Source plan: `docs/current/plans/plan-eval-defect-remediation-v2.md`.

That is the **unsplit** `-v2.md` plan (a single file), not the `-v2-steps/` directory. The
split directory appears to be a superseded intermediate: `RunnerOptions.Parse` describes
`--plan-dir` as *"the runner-specific copy whose prompts omit the load-solution step the
runner already does itself"* ([RunnerOptions.cs:20-21](RoslynSentinel.Tools.PlanStepRunner/RunnerOptions.cs#L20-L21)),
implying the runner copy is the live one and `-v2-steps/` is its ancestor.

**Confirm before deleting** — this is the one judgment call in these three documents that
needs a human. If `-v2-steps/` is genuinely superseded, it should not be in `docs/current/`.

### Fix options

1. **Move `-v2-steps/` to `docs/obsolete/`.** The `GetDocTypeSubdirRoot` helper
   ([SentinelDocumentationTools.cs:103-107](RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs#L103-L107))
   explicitly documents `docs/obsolete/` as the retirement location for exactly this
   purpose, and roots `docType` subdirs at `docs/current/` when present — so moving it makes
   the old copy unreachable through `docType:plan` and the collision disappears.
   **Recommended.**
2. **Or make the runner copy the only copy** by relocating it to
   `docs/current/plans/plan-eval-defect-remediation-v2-steps/`, replacing the old one. Also
   makes it reachable via `docType:plan`, removing the A1 exposure for this plan.
3. **Do not** rely solely on fixing A1. The duplication is a standing trap for any human or
   agent grepping for `01-baseline.md`; A1 is one of several ways to fall into it.

Whichever is chosen, the surviving copy's `00-index.md` should state plainly that it is the
single source of truth and name the runner that consumes it.

---

## C2 — Isolation is asserted in prose but not enforced by layout

The runner copy's `00-index.md` (lines 3-8) states:

> This directory splits it into self-contained steps, each run by an automated runner as its
> own fresh, isolated model conversation with no memory of any other step. … the model must
> not read, open, or act on any other step file (via `ProjectDoc` or otherwise), and must
> stop after its own gate rather than continuing on to the next step itself.

But **every worktree contains all 24 step files** (11 runner + 13 old):

```bash
$ ls PlanStepRunner/20260910-013550-398/01-baseline/Worktree/docs/tests/plan-eval-defect-remediation-v2-steps-runner/
00-index.md 01-baseline.md 02-phase1-types-and-engine-fix.md ... 11-final-verification.md
```

The constraint depends entirely on the model choosing to honour a sentence — and in this run
the model never even saw the sentence. A plan whose correctness rests on the model not
reading files sitting in its own working directory is not isolated; it is unenforced.

Compounding this: `ProjectDoc(action: list)` walks all of `docs/`, so the next step's
filename is discoverable even without guessing. The old copy's gate then supplies an explicit
instruction to go read it.

### Fix options

1. **Stage only the current step file into the worktree** (harness-side; pairs with doc B, B1).
   Remove the plan directory from the worktree after the step file is read, or check out the
   worktree without `docs/tests/`.
2. **Prefer B1 (inline the step text)** — if the content is in the prompt and the directory
   is absent, isolation is structural rather than advisory.
3. If the files must stay, at minimum ensure the scope-lock is the **first line** of every
   step file in *both* trees, so a misresolved read still lands on a stop instruction. This
   is the cheap mitigation, not the real fix.

### Note on the "why this split" rationale

`00-index.md` lines 40-65 explain the merge decisions (1.1+1.2 as one continuous edit;
2.1+2.2 because the fix needs the repro's findings). That reasoning is sound and worth
keeping — the merges are why the runner copy has 11 steps instead of 13. The problem is not
the split; it is that the pre-merge version was left in place, reachable, and numbered
differently.

---

## What the plan got right

Worth recording, since these should survive any rewrite:

- Step 01's task was **unambiguous and correctly executed** — three named projects, run in
  turns 2–4, counts captured (`Tests.Basic` 228/228, `Tests.Battery` 902/898 with 4
  pre-existing failures, `Tests.Asyncify` 92/92).
- The instruction *"do not treat pre-existing failures as something you caused, and do not
  attempt to fix them"* worked: the model recorded the 4 Battery failures and did not chase
  them. Consistent with `reference_known_failing_tests`.
- The "Prior state" convention (index lines 55-57), including *"it can also just read the
  live code, which is authoritative if this note and the code disagree"*, is good practice
  and should be retained.

## Suggested priority

| # | Issue | Severity | Effort |
|---|---|---|---|
| C1 | Duplicate trees, contradictory gates | Critical | Low (needs a confirm-before-delete decision) |
| C2 | Isolation unenforced by layout | Medium | Low (largely subsumed by B1) |

C1 is low-effort but is the one item here requiring human confirmation: **verify
`docs/current/plans/plan-eval-defect-remediation-v2-steps/` is genuinely superseded before
moving or deleting it.**

---

## Fix order across all three documents

1. **B1** — inline step content into the prompt. Neutralizes A1 for the runner outright.
2. **A1** — `ProjectDoc` honours path-qualified names. Fixes the defect for every other caller.
3. **C1** — retire the duplicate tree. Removes the trap A1 exposed.
4. **A2 / B2** — stop advertising unexposed `WriteFile`; decide the mid-size-edit story.
5. **B3, B4** — read-only enforcement and the repeated-failure breaker.
6. **A3, A4, A5** — `WrapRange` blob gap, `searchMode`, `Member` generics.

Items 1–3 address the cause; 4 addresses what turned it terminal; 5 ensures the next
occurrence is caught in minutes rather than at the turn cap.
