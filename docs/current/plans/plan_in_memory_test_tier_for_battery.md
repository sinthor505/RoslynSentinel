# Plan: Move engine-correctness Battery tests onto an in-memory workspace tier

**Status:** IN PROGRESS 2026-10-02. Step 0 landed: commit 5bc79a1 (fakes, `DiskWriteRoundTripTests`, `ModifyModifierBatchTests`) plus a second commit for the batch-edit fixes and the converted `ModifyAttributeBatchTests`/`ModifyBaseTypeBatchTests` (Build 0 errors, 48/48 batch tests pass). Steps 1-3 remain.

## Problem
Battery.Basic was the slowest project. Test-level parallelism (commit 3037e5b) fixed serialization, leaving it CPU-bound:
933 test-seconds, 78 s wall on 16 cores. Most tests check engine/tool code-correctness but each pays for a
temp directory, MSBuild load and real disk write through `PersistentWorkspaceManager` (drift, ledger, operation
blobs). Disk-level concerns (BOM, line endings, watcher, drift, undo, path case) should be owned by a few dedicated
tests, not re-exercised by every test.

## Decision
Two tiers:
- **In-memory tier (most tests):** `InMemoryWorkspace.Create((relPath, source)...)` over `FakeWorkspaceManager`
  (Roslyn `AdhocWorkspace`; apply updates the in-memory `CurrentSolution` and returns `PreImages`). The real tool
  classes, validation and compile gate run unchanged. Marked `[Parallelizable(ParallelScope.All)]`.
- **Disk tier (small):** `DiskWriteRoundTrip` helper + `DiskWriteRoundTripTests` push text through `FileIoHelper`
  into a temp file and assert raw bytes (CRLF/LF/mixed preserved, no BOM added, UTF-8 round trip). Other real-disk
  tests (watcher, drift, undo, path-case, subprocess) stay on `TestSolutionFixture`.

## Execution rules
- Build 0 errors before any commit; stage files explicitly via `Git(scope: listed)`; Co-Authored-By trailer.
- Run `RunTest` calls sequentially, never two at once: they contend on the build and cause false 5-minute
  full-build timeouts (seen once: `RunFullBuildAsync_CalledTwiceWithObsoleteCallSite_...`; passed alone).
- In-memory projects need a global-usings document (the helper adds `GlobalUsings.InMemory.cs`) or the compile
  gate rejects `[Serializable]`, `Task`, etc. `PathOf` uses `Path.GetFullPath` because
  `DocumentLookup` is not separator-insensitive.
- Dog-fooding applies (CLAUDE.md). New files: `WriteFile(operation: CreateFile)`; `CreateFile` only scaffolds a type.
- Baseline: 3 known `RoslynSentinel.Tests` failures (`Scope_DoesNotLeakIntoConcurrentUnscopedWorkAsync`,
  `ExcludeTools_ClassThatKeepsItsPrefix_IsRemovedByShortenedName`,
  `IncludeTools_ShortenedPrefixedClassName_ActivatesThePrefixedClass`) - assumed pre-existing, not proven against a baseline run.

## Steps

### Step 0 - Land the in-flight work
- Done (second commit): `ModifyAttributeBatchTests.cs` and `ModifyBaseTypeBatchTests.cs` converted to in-memory, plus the
  subagent's fixes in `Common/{BatchTypes,RoslynFormattingHelper,ToolParams}.cs`, new `Common/ReplaceNodesResult.cs`,
  `Engines.Basic/MemberRefactoringEngine.cs`, new `Engines.Basic/BaseTypeTextEditBuilder.cs`,
  `Tools.Basic/{RefactoringStructuralImpl,RefactoringStructuralTools}.cs`: ModifyBaseType batch applies as text spans
  (overlap -> `CannotEdit` naming both edits), `ReplaceNodesFormattedAsync` returns `ReplaceNodesResult` with
  `UnlocatedNodes` (modifier batch refuses to write when a node is unlocated), and `attribute` is an alias for
  `existingAttribute` (add/replace/remove, singular and batch; conflicting values rejected naming both).
- Known leftovers from that work: the modifier-batch unlocated-node error path is covered only by a helper-level test
  (no tool-level test possible); single-edit `ModifyAttribute(action: remove)` on a type-level target fails with "target
  not found" (`RemoveAttributeAsync` excludes type candidates, the batch path supports them) - not fixed; only the
  base-type fold change got no mutation check beyond the helper test; the alias tests have no mutation check.
- Verified at commit time: Build 0 errors; `ModifyAttributeBatchTests|ModifyBaseTypeBatchTests|ModifyModifierBatchTests` 48/48.
  The full solution suite was not run.
- Change: a background subagent was dispatched to (1) make `ApplyBaseTypeBatchAsync` safe for nested type + container
  (apply as spans against the original snapshot, like ModifyAttribute commit 44ecdc0), (2) make
  `ReplaceNodesFormattedAsync` stop silently skipping a replacement it cannot locate (surface it; update all callers),
  (3) accept `attribute` as an alias for `existingAttribute` on ModifyAttribute `action: add` (singular and batch;
  update descriptions; reject conflicting values). It was told to add in-memory regression tests to the two batch fixtures.
  Its result had not arrived when this doc was written - check `git status`/`Git diff` and re-run its tests before trusting it.
- Done when: Build 0 errors; `RunTest` Battery.Basic filtered to `ModifyAttributeBatchTests|ModifyModifierBatchTests|ModifyBaseTypeBatchTests`
  and `RoslynSentinel.Tests` filtered to `DiskWriteRoundTripTests` pass; then commit the listed files.

### Step 1 - Classify and convert the rest of the slow tests
- Files: remaining slow Battery.Basic fixtures. Earlier classification found ~65 slow tests, of which ~33 are done;
  ~26 more are convertible (`ReplaceSnippet`, `ApplyDiff` size guard, `InsertAfter` EOL, `AddMember`, preview candidates,
  `MoveMember`). The rest genuinely need disk (BOM, EOL on real files, watcher, drift, undo, path-case, subprocess).
- Change: write the classification table (test -> in-memory | disk) into a finding/reference doc; convert the in-memory ones
  with the pattern in `ModifyAttributeBatchTests.cs` (`InMemoryWorkspace.Create`, `workspace.PathOf`, `workspace.ReadText`,
  static `BuildTools(workspace.Manager)`). Tests that asserted BOM/EOL via disk move to `DiskWriteRoundTripTests` or stay on disk.
- Done when: converted fixtures pass; for each, a quick mutation check shows the in-memory test still fails on the original bug.

### Step 2 - Re-measure
- Change: run Battery.Basic alone (no concurrent RunTest) and compare to baseline. Measured after the pilot:
  382/382 pass, 1m20 (80 s) wall alone - no wall gain yet because only 33 tests were converted. After the tiptoe
  conversions (ReplaceSnippetBatch/ErrorCode/SizeGuard, MemberSingleDeclaration, MemberInsertAfterEol; 22 more tests;
  commits 39be3d3, c4865a4, 8042edf, 8fa4c6c): 397 pass, 6 skip, 1m04 test duration alone (baseline 1m10) - small gain,
  the remaining cost is the disk-bound fixtures. Classification: docs/current/findings/finding_battery_basic_slow_test_classification.md.
  Parked: ApplyDiffSizeGuardTests (guard reads old content from real disk), PreviewInstanceMoveCallSitesTests (897 lines,
  shared SetUp, ledger and cross-project tests). Check for flakiness (several runs); `NonParallelizable`
  global-exclusion semantics are unverified.
- Done when: wall time and test-seconds recorded in this doc.

### Step 3 - Optional follow-ups
- `scripts/Get-TestSlowest.ps1` to list slowest tests from the trx.
- Cap workers in `Test-Parallel.ps1` (step 4 of the original speed plan).
- Tiered test protocol: omit Battery.*/ModelEval.* during incremental work; run them when a batch is complete.
- Confirm the 3 `RoslynSentinel.Tests` baseline failures against a clean run.
- Disk-tier coverage gaps: `PersistentWorkspaceManager.ApplyProposedChangesAsync` BOM-preservation on an existing BOM file
  (only `FileIoHelper` is covered so far).

## Out of scope
- Rewriting disk-specific tests (watcher, drift, undo, path-case) onto the fake.
- Changing production apply behavior.

## Risks and open decisions
- The remaining Battery.Basic cost being per-test workspace setup is a hypothesis; never timed in isolation.
- Header comment in `InMemoryWorkspace.cs` references `DiskWriteRoundTrip`; that helper now exists in `RoslynSentinel.Tests/DiskWriteRoundTripTests.cs` (verify the comment's wording still fits).
- The subagent result (Step 0) is unverified at the time of writing.
