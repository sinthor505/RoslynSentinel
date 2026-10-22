# Plan: Fix the remaining working-directory test leaks outside the Battery fixtures and add a leak detector

**Status:** IMPLEMENTED 2026-10-21. Steps 1-4 done (9bd831a9, 6ffde50f, ba4cff77); Step 5 deliberately skipped. The leak was reproduced live first (C1..C5.cs), then fixed; the detector was proven with a probe test and is green in all eight projects. Follows Part A of proposal_battery_test_reorganisation_in_memory_and_shared_sources (open question 8).

## Problem
The Battery fixtures are fixed. The earlier file counts for other projects (about 25 / 10 / 3) overstated the problem. The survey found
only **2 files and 6 tests** that still leak, both in `RoslynSentinel.Tests.Advanced`.

Mechanism: the 2-argument `CreateSolutionWithProject` (`RoslynSentinel.Tests/TestSolutionBuilder.cs:13-73`) gives documents bare
relative paths. A tool that writes through the real `PersistentWorkspaceManager` then lands in the process working directory. A leak
needs a bare path and a tool call that applies the edit; engine-only preview calls cannot leak.

Evidence: `RoslynSentinel.Tests.Advanced/bin/Debug/net10.0` holds `C1..C5.cs` (`C1.cs` contains `NewM1`, a RenameSymbol result) and
`D1..D5.cs` (dated 2026-10-14). The `D*.cs` are not produced by any current test (unattributed). Stale files also sit in the bin folders of
Asyncify (3), Battery.Advanced (11) and Battery.Basic (1); no current source writes them.

| Project | Real leaks | Notes |
| --- | --- | --- |
| RoslynSentinel.Tests | 0 | About 18 disk tests use a unique temp dir and clean up; the write is the code under test, so they stay on disk. Root cause is the builder, not a test. |
| RoslynSentinel.Tests.Advanced | 2 files, 6 tests | `MassiveRefactoringTests.RenameSymbol_ShouldUpdateReferences` (`:107-129`, 5 tests, writes `C{id}.cs`, no cleanup); `DeepFunctionalVerificationTests.GetProjectDependencies_ShouldExtractFromCsproj` (`:160-189`, writes `MyProj.csproj` to `AppDomain.CurrentDomain.BaseDirectory`, deletes at the end but not in a `finally`). 57 other files use the bare-name builder but are read-only today. |
| RoslynSentinel.Tests.Asyncify | 0 | Fixed by commit 40c6cc58; fixtures use GUID temp dirs or never-created absolute paths. |
| Tests.Basic, Tests.Tools.Basic, Tests.Tools.Advanced, Tests.Battery.* | 0 | Temp paths with cleanup, or already guarded. |

Hazard (inferred): many fixtures reuse `C{id}.cs` and `Test.cs`; a stale file in the working directory can flip `FilePathWrapper.Validated`
(`FilePathWrapper.cs:37-43`). The assembly is `Parallelizable(ParallelScope.Fixtures)` (`AssemblyInfo.cs:8`) with a header saying no test touches
process-wide state; `MassiveRefactoringTests` contradicts it.

## Decision
Fix the two leaking files first, then add a cheap detector so a future leak fails loudly instead of lingering. Do not change the 2-argument
builder to return absolute paths now (breaks asserts such as `Is.EqualTo("Test.cs")`, noted at `BatteryEightTests.cs:60`).

## Execution rules
- C# edit dispatches are sequential, to `implementer` (tier per `.claude/implementer-tiers.json`; D0 for slices 1-3, S0 for slice 4).
- Build 0 errors after each slice; one `RunTest` at a time; run the touched project, compare with the baselines.
- Before slice 1, delete the stale files in the bin folders (non-C# artifacts, shell is fine) and record that this was done.

## Steps

### Step 1 - MassiveRefactoringTests off the working directory
- Files: `RoslynSentinel.Tests.Advanced/MassiveRefactoringTests.cs`.
- Change: per-test unique temp dir, `PathOf`, the 3-argument builder, write the pre-image source to disk, delete the dir in `TearDown`
  (pattern: `SentinelAsyncifyToolsTests.SetSource` in Asyncify). `BatteryToolFactory` is not visible to this project. Keep `BUG_62` on its bare path.
- Done when: Build 0 errors; the fixture passes; no `C*.cs` appears in the bin folder after a run.

### Step 2 - DeepFunctionalVerificationTests csproj location
- Files: `RoslynSentinel.Tests.Advanced/DeepFunctionalVerificationTests.cs`.
- Change: write `MyProj.csproj` into a unique temp dir and delete it in `try/finally`.
- Done when: the test passes; nothing written under `BaseDirectory`.

### Step 3 - Leak detector base and first two adopters
- Files: new `RoslynSentinel.Tests/WorkingDirectoryLeakDetector.cs`; derived `[SetUpFixture]` in `Tests.Advanced` and `Tests.Asyncify`.
- Change: base class snapshots top-level working-directory files in `[OneTimeSetUp]` and asserts none were added with `*.cs` or `*.csproj`
  in `[OneTimeTearDown]`; each project adds a derived fixture of about 6 lines. Nothing outside `ModelEval` (`ModelEvalAssemblySetUp.cs:21`) uses `[SetUpFixture]`, so there is no conflict.
- Done when: a deliberate leak makes the run fail and the clean run passes.

### Step 4 - Roll the detector out
- Files: the same derived fixture in `Tests.Basic`, `Tests.Battery.Advanced`, `Tests.Battery.Basic`, `Tests.Tools.Basic`, `Tests.Tools.Advanced`, `Tests.Server` (6 files, S0).
- Done when: each project's run is green with the detector; any failure is a real leak to triage.

### Step 5 (optional, not recommended now) - Absolute paths from the 2-argument builder
- One file, but breaks bare-name asserts; revisit only if a new leak slips past the detector.

## Out of scope
- Disk tests whose write is the code under test (about 18 files in `RoslynSentinel.Tests`, about 6 in `Tests.Basic`, 3 in Asyncify).
- Not surveyed: `Tests.Server`, `Tests.Integration`, `Tests.ModelEval`, `Tests.SubAgent`, `Tests.PlanStepRunner`. Step 4 will surface any leak there.

## Risks and open decisions
- The detector may turn existing runs red from stale files even though nothing leaks now; hence the one-time clean-up before Step 1.
- Whether NUnit 5 and `RunTest` surface a failing `OneTimeTearDown` is unverified; the deliberate-leak check in Step 3 settles it.
- The detector reports the first leak without naming the test.
- Moving the RenameSymbol tests to absolute paths may change JSON and path assertions (unverified).
- `C1.cs` has not been rewritten since 2026-10-14 although the DLL was rebuilt on 2026-10-21: either the write is now refused or the suite was not run from that folder. Unknown without a run.
- `[NonParallelizable]` semantics remain unverified (proposal open question 3).
- Not checked whether each cleanup in `Tests.Basic` is in a `finally`, or whether `WorkingDirectoryLeakGuardTests.cs:22` deletes in a `finally`.
