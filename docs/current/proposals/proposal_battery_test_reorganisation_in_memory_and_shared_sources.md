# Fix the Battery test file leak with an in-memory tier, an explicit WorkspaceAccess mode, and (optionally) topic classes and shared sources

**Status:** PROPOSED 2026-10-20. Direction agreed in discussion, not built. A read-only classification survey ran the same day and is recorded below (reported by a subagent, not spot-checked). This is the starting point for the effort.

## Motivation

1. **Leaked files.** `Battery*Tests` fixtures build solutions through `RoslynSentinel.Tests/TestSolutionBuilder.cs`
   with bare relative paths (`"Order.cs"`) and hand them to the real `PersistentWorkspaceManager` via
   `SetTestSolution`. A tool that writes resolves the bare name against the process working directory and leaves
   the file in the test bin dir; later tests read it as their pre-image (symptom: `EolChangeGuard` refusing a write
   in `BatteryTwentyFourTests.MethodSignature_Remove_LastParameter_UpdatesCallSite`; also `FilePathWrapper`'s
   `Validated = File.Exists(Absolute)` flips for a leftover bare name, `FilePathWrapper.cs:37-43`).
   Original defect and options A-D: [issue_battery_tests_leak_files.md](../issues/issue_battery_tests_leak_files.md).
2. **Slow tests / existing remedy.** The in-memory tier (`RoslynSentinel.Tests/Fakes/InMemoryWorkspace.cs` over
   `FakeWorkspaceManager`) runs the real tool code with no temp dir, MSBuild load or disk write. Tracked in
   [plan_in_memory_test_tier_for_battery.md](../plans/plan_in_memory_test_tier_for_battery.md).
3. **Undocumented third test tier.** Docs describe two options (fake, or `TestSolutionFixture`). The Battery tests
   use a third that nothing documents, and it is the one that leaks:

   | Tier | Manager | Solution source | Disk writes |
   | --- | --- | --- | --- |
   | In-memory | `FakeWorkspaceManager` | `InMemoryWorkspace` | No |
   | Hybrid (Battery today) | real `PersistentWorkspaceManager` + `SetTestSolution` | `TestSolutionBuilder` | Yes (the leak) |
   | Real | real `PersistentWorkspaceManager` | `TestSolutionFixture` (copy of `Samples/ContosoOrders` in a temp dir) | Yes (temp dir) |

   The header comment of `FakeWorkspaceManager.cs` is also stale: it says every other member throws
   `NotImplementedException`, but apply, the ledger and validation are implemented.
4. **Ad-hoc input and construction.** An earlier survey (session efbf4503, 2026-10-15) found about 1,267
   class-skeleton literals in 155 test files and 139 private `SetSource`/`SetMultipleFiles` copies in 70 files
   (approximate). Engine/tool construction repeats at about 1,800 sites:
   [plan_test_construction_factories_pilot.md](../plans/plan_test_construction_factories_pilot.md).

## Survey result (2026-10-20, explore-sonnet, static; not spot-checked)

The leak is much narrower than the issue's "34 fixtures". About 901 tests:

- **~784 read-only** (never write; cannot leak). All of `Battery.Advanced` calls engines directly and
  `SetTestSolution` only sets `CurrentSolution` (`PersistentWorkspaceManager.cs:1385`).
- **~80 write and could convert** (assert only on resulting text/result): ~76 in `BatteryTwentyFour` (about 40 of
  them only assert `Is.Not.Null`), 3 `DeleteUnusedSymbol` tests in `BatteryTwenty`, 1 in `BatteryEighteen`
  (`SortAndDeduplicateUsings_FileWithUsings_ReturnsResult`, `writeToFile` defaults true at
  `MsToolAugmentEngine.cs:628`; likely the source of the dated `Test.cs` in the Tools.Basic bin dir - inference).
- **37 need real disk**: 14 in `BatteryTwentyFour` (12 "soft" - `MoveMember` and `SyncTypeAndFilename` tests that use
  real paths only to place a new file, convertible via `PathOf`; 2 "hard" that assert `File.Exists`), 8 in
  `BatteryEighteen` (engine methods call `File.ReadAllTextAsync` on bare paths, e.g. `MsToolAugmentEngine.cs:693`,
  `:758`, `:1065`, `:1233`; they can read a stale leftover), 10 in `BatteryTwenty` (real `.sln`, `WorkspaceVersion`,
  `ListWorkspaceSolutions`), 4 in `BatteryFour` (own temp files), 1 in `BatteryTwentyTwo` (`TestSolutionFixture`).
  The fake cannot fix the engines that read disk themselves.

`FakeWorkspaceManager.ApplyProposedChangesAsync` (`Fakes/FakeWorkspaceManager.cs:79-157`) handles new, existing and
removed documents, and fills `PreImages` from the workspace, not disk; a move is add plus delete. It lacks a
disk-based EOL backstop, `WorkspaceVersion` (always 0), drift/halt checks, rollback options and undo, so a
stale-disk-pre-image test passes vacuously on it.

Other findings: duplicated sources (`RichSource` in Eighteen/TwentyOne/TwentyTwo, `SimpleSource` in Seven/TwentyFour,
compared by eye only; `AsyncSource` has the same name but different content in TwentyOne/TwentyThree);
`SetSource` defined 36 times in 20 files; tools are built per test in `[SetUp]`, none in the test body; asserts of the
form `Is.EqualTo("Test.cs")` on a bare path (`BatteryEightTests.cs:60`, `BatteryThirteenTests.cs:95`, several in
`BatteryThirtyOneTests.cs`) break under absolute virtual paths, while `Does.Contain("X.cs")` asserts are robust;
checked-out `.cs` files are CRLF (`.editorconfig:38`) but `.gitattributes` is only `* text=auto`, so verbatim/raw
literals silently become LF on an `autocrlf=false` or Linux checkout.

## Proposal

Proposed (not decided in detail). Part A fixes the leak; B-D are independent and optional.

**A. Narrow leak fix.**
1. Split `BatteryTwentyFour`'s ~76 convertible writers (and the 12 "soft" disk tests) into topic-named classes on
   `InMemoryWorkspace` (`[Parallelizable(ParallelScope.All)]`), e.g. `MethodSignatureCallSiteTests`,
   `MoveMemberTests`. Delete moved tests from the old class in the same slice. Strengthen the ~40 `Is.Not.Null`
   tests while moving.
2. Leave the remaining real-disk tests in their classes (`TwentyFour` 2 hard, `Eighteen` 8, `Twenty` 10, `Four` 4,
   `TwentyTwo` 1) marked `[NonParallelizable]` with a why-comment and a shared cleanup helper in `RoslynSentinel.Tests`.
   Exclusivity makes `BatteryTwentyFour`'s wipe-all-`*.cs` helper safe. Exclusivity semantics are unverified.
3. Fix `BatteryEighteen.SortAndDeduplicateUsings` (pass `writeToFile: false` or use a temp path).
4. Regression test per the issue: pre-seed a stale CRLF `Order.cs` in the bin dir and assert the disk-bound path
   still passes; a green suite alone says nothing (no current test pre-seeds it).

**B. `WorkspaceAccess` on the fake (decided in discussion).**
- `FakeWorkspaceManager` takes a **required** `WorkspaceAccess` enum (`ReadOnly`, `ReadWrite`) in its constructor, so
  the mode is visible where the fake is created. No default: ~35 `new FakeWorkspaceManager()` sites in 23 files (search
  capped, so slightly more) would silently break under a `ReadOnly` default, and an optional parameter relocates the
  failure (see memory "prefer mandatory params"). An enum reads at the call site; a bool does not. `InMemoryWorkspace.Create`
  takes the same parameter (its users all write, so `ReadWrite`).
- `ApplyProposedChangesAsync` on a `ReadOnly` fake fails with an actionable, structured message naming the mode and
  the fix (match how the existing `ApplyException` seam surfaces). Not two classes: both would still implement the whole
  `IWorkspaceManager` (about 20 `NotImplemented` stubs duplicated), and the interface already separates reading
  (`ISolutionProvider`, `IWorkspaceReader`) from writing (`IWorkspaceMutator`).
- Migration: set every site to `ReadOnly`, run the suite, flip the sites that fail to `ReadWrite` (baseline is green).
  Cheaper than deciding per site, but a test that writes and only asserts `Is.Not.Null` passes under `ReadOnly`
  because tools catch the exception at the tool boundary and return an error result. To catch those, count rejected
  write attempts on the fake (and log them in a one-off audit run) so every attempted write is listed before the flip is
  trusted. `SemanticFindReplace(replaceConstruction)` can apply the edit.
- **Status: DONE 2026-10-20.** Every `FakeWorkspaceManager` site declares its mode (static `ReadOnly()` / `ReadWrite()`
  factories, or the enum constructor). `Tests.Tools.Basic`: all 15 sites went to `ReadOnly`, 13 tests failed, the 4
  fixtures that apply edits (`TestCategoryApply`, `TestCategoryApplyProjects`, `TestCategoryTagging`, `QualifyCrefs`)
  were flipped to `ReadWrite`; the other 11 sites stay `ReadOnly`. Audit run
  (`ROSLYNSENTINEL_FAKE_WRITE_AUDIT`, `dotnet test` on that project): the audit file was never created, so no
  `ReadOnly` fake received a write attempt, swallowed or not. The audit run also reported 2 failures in 11 minutes
  that `RunTest` (82 s, 1072/1072) does not reproduce; the cause is unexplained. The obsolete parameterless
  constructor and its guard test are removed. Gap: `RunTest` cannot pass an environment variable, so the audit needed a
  shell `dotnet test`.
- Limit: covers only tests that already use the fake. The hybrid tier (real manager + `SetTestSolution`, where the 784
  read-only tests sit) is untouched; moving them to a `ReadOnly` fake would turn a silent write into a failure, but the
  engines may need more of `IWorkspaceManager` than the fake implements (unverified).

**C. Topic regrouping and shared sources (optional, separate effort).** The numbered Battery fixtures are junk-drawers;
regrouping by tool is worthwhile but is no longer required by the leak. Decisions from the 2026-10-15 discussion:
- C# constants (raw string literals) in a `Support/` folder of `RoslynSentinel.Tests` (already referenced by 11 of 13
  test projects), not embedded files. Named, single-purpose, append-only samples (`Sources.Order`, ...), optional
  `Wrap.InMethod/InClass/InNamespace`. The wrappers save less than expected; the bigger win is removing copied fixtures
  and the four EOL helpers.
- Every sample goes through an explicit `Normalize(eol)`; `WithCrlf()` must normalise to LF first or CRLF becomes
  `\r\r\n`. Keep targeted bug-repro snippets inline.
- Proposed (not decided): a `Source` value carrying its relative path and an `InMemoryWorkspace.Create(params Source[])`
  overload, so tests need no `SetSource` wrapper and no path literals (`ws.PathOf(Sources.Order.Path)`).
- Start with the exact-copy fixtures in the snippet area and `Order.cs` (TwentyFour, Seven, Eighteen, TwentyOne,
  TwentyTwo); widen `SnippetMatcherTests.Corpus` with tab/BOM/non-ASCII.

**D. Construction via the `TestTools`/`TestEngines` factories** from the factories pilot where they exist, instead of a
per-class `BuildTools`. Sequencing against that plan is open.

Also: update the stale `FakeWorkspaceManager.cs` header, the `IWorkspaceManager` doc comment and `TestSolutionFixture`
doc comment to describe all three tiers (table above).

## Alternatives considered, not pursued

- **Per-fixture temp dir with absolute paths (issue Option B) via find-replace only.** Same literal-rewrite burden as
  the in-memory conversion, keeps disk writes and the slow path; and the survey shows only ~94 tests are affected.
- **Wipe helper plus `[NonParallelizable]` on all 34 fixtures (issue Option A).** Serialises everything for a leak that
  only a few fixtures cause. Kept only for the disk-bound remainder.
- **Changing path resolution in `PersistentWorkspaceManager` (issue Option C).** Production change on the write
  chokepoint for a test-hygiene problem; not evaluated.
- **Separate `FakeReadonlyWorkspaceManager` / `FakeWriteableWorkspaceManager` classes.** Duplicates the stubs for one
  differing method; a constructor mode does the same job.
- **Defaulted `bool isReadOnly = true`.** Silently breaks every existing writing site; unmarked sites become the common
  case again.
- **Corpus as embedded files (`.cs.txt`).** Dropped in favour of constants.
- **Fully shared fixtures with no inline sources.** Targeted regression inputs must stay readable in the test.

## Open questions

1. Is the survey's classification right? `BatteryTwentyFour` counts are name- and call-based (about 30% of its lines
   read); the other fixtures were mostly classified by call graph. Spot-check before slicing.
2. Reported but unverified: `BatteryTwentyFourTests.Setup` builds `ValidationEngine` with `_diffEngine` before
   `_diffEngine` is assigned (null on the first test of a fresh instance).
3. `[NonParallelizable]` exclusivity semantics and the real serial cost (the plan also lists this as unverified).
4. Can the 12 "soft" disk tests in `TwentyFour` really run on `InMemoryWorkspace` via `PathOf`? Does the fake's
   add-document path (`solution.Projects.First()`) place `MoveMember` targets correctly?
5. Per-file CRLF/LF byte counts for the fixtures were not collected (a hook false positive blocked the command), and
   the duplicated constants were not byte-diffed.
6. Whether the ~784 read-only tests can run on a `ReadOnly` fake (what part of `IWorkspaceManager` do their engines use?).
7. Whether raw-literal tests elsewhere silently depend on CRLF (only about 4 of ~76 files were sampled earlier).
8. Whether to include the non-Battery projects that leak the same way (RoslynSentinel.Tests 25 files, Tests.Advanced 10,
   Tests.Asyncify 3).
9. Sequencing of Part D against the factories pilot (Steps 2-4 not started as of its 2026-10-17 handoff).

## Cost / risk

- Moving tests changes names, which breaks name-based entries in `docs/known-failing-tests.*.txt`; check per slice.
- Line-number-dependent asserts (e.g. `SnippetSelectorTests` asserts lines 5, 12, 19) can break when sources are composed.
- Path asserts on bare names must be rewritten when a test moves to absolute virtual paths.
- A required `WorkspaceAccess` parameter touches ~35 sites; the compiler guarantees none is missed.
- Moved tests lose git blame continuity. Shared sources couple tests to each other; keep them immutable and small.
- C# edits follow the dispatch rules (sequential implementer dispatches; no parallel C# edits).

## Related

- [issue_battery_tests_leak_files.md](../issues/issue_battery_tests_leak_files.md) - the defect and options A-D (its fixture count is overstated per the survey above).
- [plan_in_memory_test_tier_for_battery.md](../plans/plan_in_memory_test_tier_for_battery.md) - the in-memory tier and conversion steps.
- [plan_test_construction_factories_pilot.md](../plans/plan_test_construction_factories_pilot.md) - `TestTools`/`TestEngines` factories and `SemanticFindReplace(replaceConstruction)`.
- `.claude/handoff/2026-10-17_337a42c9.md` (local-only) - factories pilot handoff.
- Session transcript efbf4503-0571-42d5-afdd-575bebddc976 (2026-10-15, local) - the shared-sources discussion and duplication survey.
- `docs/current/findings/finding_battery_basic_slow_test_classification.md` - the earlier Battery.Basic classification, the model for this one.
