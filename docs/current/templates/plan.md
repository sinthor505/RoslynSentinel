# Plan: <what will be implemented, in one line>

**Status:** <DRAFT | APPROVED | IN PROGRESS | IMPLEMENTED> <YYYY-MM-DD>. <One sentence; commit hashes once built.>

## Problem
<!-- What is wrong and the evidence. Link the proposal/design doc if one exists. -->

## Decision
<!-- The chosen approach, stated up front. -->

## Execution rules
<!-- Rules that apply to every step: build gates, test baselines, tooling constraints. Omit if none. -->

## Steps
<!-- One sub-heading per step, in order. Definitions before the call sites that use them.
     Slice for parallel lanes (two implementing agents at most; they share bin/obj):
     - Depends on the real prerequisite, not the previous step: a step needing only an earlier helper can pair with
       its siblings right after it.
     - A sweep over many sites: make the inventory Step 1 and derive the slice map from it.
     - Slices meant to run as a pair touch disjoint files. A large file with many sites gets a prep slice that adds
       shared factories, so the sites become one mechanical sweep rather than many sequential slices.
     - New tests go in per-slice test files; update shared pinned assertions in one slice.
     - Mark each slice text-neutral (new members with no callers, consumer fixes) or behaviour-changing (wire text,
       output shape) and schedule text-neutral slices first, so a hold (another plan's A/B baseline, a review)
       does not idle the run. Name any cross-plan baseline dependency in Risks.
     - New types/members that reference each other: Step 1 is a compile-green contract skeleton (final signatures,
       `throw new NotImplementedException(); // TODO implement (Step n)` bodies); the last step finds no stubs left.
     Checks before handing over (verified names, tier sizing, lane per slice): the plan-writer skill. -->

### Step 1 - <name>
- Files: <paths>
- Depends on: <step numbers, or none>
- Effect: <text-neutral | behaviour-changing: what a caller or model sees differently>
- Change: <what, concretely>
- Done when: <verifiable condition, e.g. Build 0 errors, named test passes>
<!-- Test, eval or LIVE step: state the outcome classes it reports (ideal vs limit, one named class per outcome),
     not just pass/fail. See reference_model_eval_procedure.md, "Scoring design". -->

## Out of scope
<!-- Deliberately excluded. -->

## Risks and open decisions
<!-- Anything unverified. Do not route around an open question; flag it. -->
