# Where each ModelEval fixture and helper should live - OPEN

**Status:** OPEN 2026-11-23. Stub for a dedicated design session; replaces piecemeal moves out of Tests.ModelEval.

## Summary

Tests.ModelEval mixes live-model fixtures (need an LLM host) with model-free tests and shared harness code.
Worktree runs exclude ModelEval by default (`$excludedByDefault` in `scripts/Invoke-WorktreeTests.ps1`), so
model-free tests left there never run in the fast path. todo-punchlist #10 (2026-11-23, commits 1151a2cb ..
93831d31) moved about 70 files into Tests.Harness, but every remaining class turned out to be "blocked by one
named dependency", and two other assemblies (Tests.ErrorVariants, Tests.Harness) already carry diverged copies
or parallel versions of the same helpers. The owner decided (todo-review 2026-11-23) to settle placement for
the whole set in one session instead of continuing one class at a time.

## Why it matters

- Model-free tests outside the fast path regress unnoticed.
- Diverged copies (below) drift further with every fix applied to only one of them.
- Each piecemeal move re-discovers the same coupling and re-asks the same rulings.

## What is known

From the todo-review 2026-11-23 investigation (read-only, unverified by a build):

- **Archiver set.** `Tests.ModelEval/ModelTestingResultsArchiver.cs` + `RewriteBodyArchivedOutcomeAttribute`
  (an `[assembly:]` NUnit ITestAction) + 2 test classes (15 tests). 12 of the 15 (9 `DeriveSuffix` cases,
  `PendingArchivedOutcomes`, 2 end-to-end) use only AgentLoop types, not the archiver. About 15 live
  fixtures call the archiver. Tests.ErrorVariants carries its own copy that is NOT identical: ModelEval
  registers a pending rewrite only for Inconclusive (`Tests.ModelEval/ModelTestingResultsArchiver.cs:36-49`),
  ErrorVariants always writes Unfinished (`Tests.ErrorVariants/ModelTestingResultsArchiver.cs:28-33`);
  ErrorVariants references only AgentLoop and Common, so sharing via Harness would drag in Server.Advanced.
- **Resume tests.** `ResumeScenariosTests` (21) needs only `ScenarioAgentTests.NextSeedTurnNumber`
  (2-line static, `ScenarioAgentTests.cs:147-148`), which could sit beside `TranscriptResume` in Harness
  (`Tests.Harness/TranscriptResume.cs:145`). `ResumeFaultExecutionTests` (3) and `ResumeDriftProbeTests:49`
  use `ScenarioEnvironment` (`ScenarioAgentTests.cs:388-420`), a wrapper over internal
  `ModelEvalMcpEnvironment` (`ModelEvalMcpEnvironment.cs:34`), which uses `ChainEvalToolSurface`
  (`:100-101`).
- **Two environments.** `ModelEvalMcpEnvironment` (ModelEval) and `ModelEvalFakeModelEnvironment`
  (Harness) overlap; whether the fake one can host the resume fixtures is unchecked.
- **ChainEvalToolSurface** stays in ModelEval by an earlier owner ruling; it is also used by
  `EditArmToolSurface` and `EditArmToolSurfaceTests`, and by `ModelEvalToolSurfacesTests` (2, via
  `ModelEvalMcpEnvironment.CreateAsync`, `:25,73`).
- **Implementation scenarios.** `ImplementationScenarioGoldenTests` (4, `[Explicit]`, git only) needs
  `ImplementationScenarioRegistry`, which names `PlanStalenessReviewScenario`; that stays with
  `PlanStalenessReviewCheckTests` (calls its internal statics). `ImplementationScenario.cs` and
  `ImplementationScenarioChecks.cs` could move with it.
- Live (stay): the `*AgentTests` fixtures, `ModelEvalMcpEnvironment`, the `ModelEvalAssemblySetUp` probe.

## Open questions

1. Target layout: is Tests.Harness the home for all model-free eval tests and shared eval helpers, or does a
   lower, dependency-light eval-helper project (AgentLoop + Common only) make sense so ErrorVariants can share
   the archiver set instead of copying it?
2. Archiver set: one shared type versus per-assembly copies; reconcile the Inconclusive-vs-Unfinished
   behaviour difference first.
3. One environment or two: can `ModelEvalFakeModelEnvironment` replace `ScenarioEnvironment` for the resume
   fixtures?
4. Does the `ChainEvalToolSurface` ruling still hold with the full picture?
5. Which classes stay in ModelEval permanently, recorded as such so later sessions stop re-investigating them.

## What would resolve it

A per-file inventory of Tests.ModelEval, Tests.ErrorVariants and the eval parts of Tests.Harness (live or
model-free, blocking dependency, target project), the answers to the open questions, and a sliced plan doc
under `plans/`. TODO.md entry "Move the remaining model-free Tests.ModelEval fixtures into Tests.Harness"
unparks when that plan exists.
