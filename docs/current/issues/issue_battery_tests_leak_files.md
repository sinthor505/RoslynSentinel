# Battery tests leak files into the test working directory - OPEN

**Status:** OPEN 2026-10-20. Design pending (separate session); this doc is the self-contained starting point, built from a static read-only survey -- nothing was run.

## Summary

34 `Battery*Tests` fixtures across four test projects build their in-memory solutions through one
helper that gives every document a **bare relative file path** (`"Order.cs"`). When a tool under
test applies an edit, the write chokepoint resolves that bare name against the **process working
directory** (the test bin dir, e.g. `bin\Debug\net10.0`) and writes the file there. The file is
never removed. Later tests that reuse the name then see that file as their on-disk pre-image, so a
result is judged against a previous test's output. Concrete symptom already seen: a stale CRLF
`Order.cs` left in the bin dir made `EolChangeGuard` refuse a new LF write (pre-image says CRLF,
new content LF) in `BatteryTwentyFourTests.MethodSignature_Remove_LastParameter_UpdatesCallSite`
(`RoslynSentinel.Tests.Tools.Advanced/BatteryTwentyFourTests.cs`, around line 1183).

This is an environment defect of the test harness, not of any test author: the builder makes the
leaking form the default and nothing in the harness tracks or removes what a tool writes.

## Why it matters

- Order-dependent and cross-fixture-dependent test results. A fixture can pass or fail depending
  on which fixture ran before it in the same process, or concurrently (fixtures run
  `Parallelizable(ParallelScope.Fixtures)`).
- The failure surfaces far from its cause (an EOL-guard refusal in one test caused by another
  fixture's leftover), which is expensive to diagnose.
- Repository bin dirs accumulate stray `.cs` files; `Test.cs` in `Tests.Tools.Basic` was written
  on 2026-10-20 13:34, so the leak is ongoing.
- The only fix so far (BatteryTwentyFour's wipe) is itself unsafe under parallel fixtures (see
  "What is known").

## What is known

All of this was established by reading code and listing bin dirs on 2026-10-20. **No test was run.**

### Mechanism

- Choke point: `RoslynSentinel.Tests/TestSolutionBuilder.cs`, `CreateSolutionWithProject(string projectName, (string name, string content)[] documents)` (starts at line 13). It calls `solution.AddDocument(documentId, doc.name, SourceText.From(doc.content), filePath: doc.name)` (around line 67), so `filePath` is the bare name. The project path is a fake `Path.GetTempPath()/TestProj/<name>.csproj`, which is not where documents live.
- Write path: tools write through the write chokepoint. The pre-image capture region in `RoslynSentinel.Common/PersistentWorkspaceManager.cs` (about lines 1565-1600) calls `FileIoHelper.ReadAllTextIfExistsAsync(key, ...)` for each target path. A bare `key` resolves against the process working directory. That read is the pre-image the EOL guard compares against; it reads disk, not the workspace.
- Fixtures have **no common base class**. Each declares private wrappers (`SetSource`, `SetMultiFile`, `SetSources`, `SetProject`), e.g. `BatteryTwentyFourTests.cs:160-171`, `BatteryEightTests.cs:38`. There are about 55 direct builder call sites in Twenty-Five and Twenty-Six alone.

### Existing partial mitigations

- `TestSolutionBuilder.cs` lines ~78-134: an absolute-path overload `(projectName, projectCsprojPath, (name, content, filePath)[])`. Used only by BatteryTwentyFour (`SetPathAwareSolution` at :761 and four SyncTypeAndFilename tests), BatteryTen (:71, :89) and BatteryTwenty (:540, :643). Even these pass the absolute `sourcePath` to the tool (BatteryTwentyFour :789), i.e. every call and assert in such a test is path-aware.
- `RoslynSentinel.Tests/TestSolutionFixture.cs`: the only temp-dir helper. Copies the whole `Samples/ContosoOrders` project into a GUID temp dir, deleted in `Dispose`. Too heavy for these fixtures (they use a few in-memory snippets).
- Only `BatteryTwentyFourTests` cleans up: `Setup` (line ~106) and `TearDown` (lines ~138-143) call `DeleteLeakedRelativeFiles()` (lines ~152-158), which deletes **every** `*.cs` at the top level of `Directory.GetCurrentDirectory()` before and after each test. It has an explanatory doc comment on the pre-image problem. Drawbacks: blunt; does not track what the test created; and because the working directory is process-wide while fixtures run in parallel, it can delete another running fixture's files mid-test.
- `BatteryTwenty` and `BatteryFour` delete only their own temp files; their bare-name `SetSource` still leaks. `BatteryTen` is partial (temp at :70, :88).

### Fixture survey (static)

Columns: fixture, direct builder references / `"X.cs"` literal references (a proxy for the number of call sites and asserts that would need editing under an absolute-path approach), absolute/temp use, cleans up. "n" = none.

Tests.Tools.Advanced:
- TwentyFour 9/293, absolute yes (:672 :709 :763 :1253 :1286 :2066 :2094), cleanup YES (blunt wipe)
- TwentyThree 1/123 n; TwentyOne 1/99 n; TwentyTwo 2/82 (TestSolutionFixture at :419) n; ThirtyOne 2/57 n; Nineteen 1/39 (`"Order.cs"` 18 times) n; ThirtyThree 1/13 n

Tests.Tools.Basic:
- Twenty 5/79, mixed temp dirs (:162 :533 :642 :729), partial; Eighteen 1/46 n

Tests.Battery.Basic:
- Four 2/15, `GetTempFileName` :178, `SafeDelete` for its own temp files only (partial)

Tests.Battery.Advanced (all: no absolute/temp use, no cleanup, except Ten partial):
- TwentySix 55/110; TwentyFive 57/99; Thirty 1/53; TwentySeven 22/44; TwentyEight 16/32; Seven 8/29; ThirtyTwo 1/26; ThirtyFive 2/25; Fourteen 12/24; Twelve 12/24; Eleven 10/22; Five 2/22; Eight 7/21; Ten 8/20 (partial temp :70 :88); Nine 6/19; Fifteen 9/18; Six 3/18; Seventeen 5/16; Thirteen 9/16; ThirtySix 8/16; Sixteen 6/12; ThirtySeven 4/7; ThirtyEight 1/2; ThirtyNine 1/2

### Leaked files observed in bin dirs (`*.cs`, 2026-10-20)

- Tests.Battery.Advanced: 11 (AnimalBase, Caller, Dog, IGreeter, IService, Order, Order.Partial, Outer, Refactor, Status, Worker)
- Tests.Tools.Basic: 8 (including `Test.cs`, written 2026-10-20 13:34)
- Tests.Battery.Basic: 1 (`Test.cs`)
- Tests.Tools.Advanced: 0 (consistent with TwentyFour wiping every `*.cs`; note the wipe also hides leaks from other fixtures in that project)
- Out of scope but the same root cause: non-Battery projects also leak into their bin dirs (RoslynSentinel.Tests 25 files, Tests.Advanced 10, Tests.Asyncify 3).

### Shared names (collision risk -- hypothesis, not reproduced)

`Order.cs` is used by BatteryTwentyFour, BatteryNineteen, BatterySeven, BatteryThirty and BatteryEight. Parallel fixtures in the same process could overwrite or delete each other's file, or leave a CRLF/LF mismatch that trips `EolChangeGuard`. Note: BatteryNineteen's `Order.cs` use may be read-only (not checked); fixtures in different test projects run in different processes unless the runner starts them concurrently in the same directory (not checked).

### Lookup note

`DocumentLookup.cs` lines ~266-274 only use file-name match to *suggest close paths in error messages*. There is no evidence that a bare name given to a tool resolves to a document registered under an absolute path. So switching a fixture to absolute paths means editing every builder call and every assert/tool argument in it (tools take `"Order.cs"` as an argument today).

### Not checked

- No test was run; no fixture-to-leaked-file mapping was observed (which fixture wrote which leftover is unknown).
- NUnit `[NonParallelizable]` exclusivity semantics (does it exclude only other fixtures in the same assembly, and how does it interact with `Parallelizable(ParallelScope.Fixtures)` on sibling fixtures?) are unverified.
- Whether different test projects run concurrently against the same working directory is unverified.
- The `SetSource` wrappers of all 34 fixtures were not all read.
- The per-fixture counts are literal/reference counts from text, a proxy for edit effort, not a measured call-site count.

## Options (no decision made)

**A. Shared wipe helper plus serialisation.** Extract TwentyFour's wipe into one static helper in `RoslynSentinel.Tests` (the one project all four reference) and call it from every fixture's Setup/TearDown, plus `[NonParallelizable]` per fixture because the working directory is process-wide. `Tools.Advanced/AssemblyInfo.cs` already cites this pattern via `ServerBuildInfoEnvelopePathTests`.
- For: small per-fixture change; tests keep bare names, so no assert edits; fastest way to stop the bleeding.
- Against: blunt delete; serialises 34 fixtures (test-time cost, unmeasured); a tracked-names delete would miss files tools create themselves (e.g. MoveMember "Target" files); a snapshot-diff delete (record dir listing at Setup, delete additions at TearDown) is only safe if the fixture is exclusive; the leak mechanism itself remains, so new fixtures can forget it (can be mitigated by a base class or an analyzer/hygiene test).

**B. Per-fixture temp dir with absolute paths (the original decision).** Each fixture gets its own temp root; builder passes absolute `filePath`; deleted in TearDown.
- For: removes shared mutable state, so parallel fixtures are safe; no serialisation; cleanup is exact.
- Against: touches every builder call and every assert/tool argument; heaviest fixtures have 100+ `"X.cs"` literals (TwentyFour 293, TwentyThree 123, TwentySix 110); needs a design for how tools resolve a bare name against a per-fixture root (see lookup note: nothing resolves it today); large mechanical diff and review burden.

**C. Change resolution so bare names resolve against a per-test virtual root.** Either a production-side change at the pre-image / write resolution in `PersistentWorkspaceManager.cs` (~1565-1600) or a test-only seam (e.g. the builder registers documents under an absolute root while `DocumentLookup` still matches a bare name).
- For: could fix all 34 fixtures with no edits to tests.
- Against: not evaluated; a production change for a test-hygiene problem is risky (it sits on the write chokepoint every tool uses); a test-only seam may hide real path-resolution behaviour the tests ought to exercise. Treat as an open question, not a candidate yet.

**D. Hybrid / incremental.** Fix the `Order.cs` / BatteryTwentyFour case first (smallest, directly addresses the observed failure), then migrate fixtures in slices by whichever of A/B/C is chosen.
- For: cheap first win, regression test for the known symptom.
- Against: leaves the leak mechanism in place until later slices land.

## Open questions

1. Is the goal "no files left behind", "no cross-test interference", or both? A needs serialisation for the second; B gives both.
2. Can the builder (or `SetTestSolution`) own a per-test root so that tools given a bare name still work? What does `DocumentLookup` need for a bare name to match an absolute-path document? (Not evidenced today; see lookup note.)
3. How should files a tool creates itself (new files from MoveMember/extract operations) be tracked for cleanup?
4. What is the real cost of `[NonParallelizable]` across 34 fixtures? (Unmeasured; and its exclusivity semantics are unverified.)
5. Should a shared base class or a hygiene test (fail if the bin dir has stray `.cs` after a run) prevent new fixtures from reintroducing the leak?
6. Scope: include the non-Battery projects that leak the same way (RoslynSentinel.Tests 25 files, Tests.Advanced 10, Tests.Asyncify 3)?
7. Does the stale-CRLF acceptance case actually reproduce today? `BatteryTwentyFourTests` already pre-wipes at Setup, so it likely passes unless another parallel fixture writes `Order.cs` between Setup and the tool call. Not run.

## What would resolve it

A design decision between A-D (separate session), then implementation in slices. Acceptance case
(from TODO): `BatteryTwentyFourTests.MethodSignature_Remove_LastParameter_UpdatesCallSite` must pass
with a stale CRLF `Order.cs` pre-seeded in the bin dir. A regression test must pre-seed that file
and assert the test body passes, and it must exercise the changed path itself (the fixture's
Setup/TearDown or builder path), not only a shared helper. Passing the existing suite is not
evidence: no current test pre-seeds a stale file, so a green run says nothing about this hazard.
Per the role boundaries, the design/plan must specify that test; a reviewer must confirm it covers
the specified path.

A suggested slicing if option A is chosen (not decided):
1. `BatteryTwentyFourTests.cs`: `[NonParallelizable]` with a why-comment, plus the stale-CRLF regression test above.
2. New helper in `RoslynSentinel.Tests`; switch TwentyFour to it; add Nineteen.
3. TwentyOne, TwentyThree, TwentyTwo.
4. ThirtyOne, ThirtyThree, then Eighteen, Twenty, Four.
5. Battery.Advanced heaviest first, three fixtures per slice, `Order.cs` users (Seven, Eight, Thirty) early.
6. Remaining Battery.Advanced, three per slice.

Related: `docs/current/issues/issue_tool_hygiene_open_items.md` (the TODO entry previously pointed
there). When this closes, move the TODO entry to `CLOSED.md`.
