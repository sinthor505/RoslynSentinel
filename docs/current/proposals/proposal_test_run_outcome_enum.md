# Replace RunTest's RunSucceeded/RunCompleted bools with a TestRunOutcome enum

**Status:** APPROVED 2026-11-23. Decided in todo review 2026-11-23; not built.

## Motivation

`TestRunResult` (`RoslynSentinel.Engines.Basic/TestRunEngine.cs:43`) carries two bools, `RunSucceeded` and `RunCompleted`, that a
caller must combine to read the outcome (`IsError`/`Code`/`Message` already combine them, `:71-83`). Two cases mislead an agent today:

- A filter that matches zero tests reads completed + succeeded, i.e. green, so a mistyped filter looks like a pass (kept as success by a
  2026-11-11 wording-only decision, comment at `WorkspaceBuildTestImpl.cs:175`).
- A non-zero exit with 0 failed tests (OneTimeTearDown error, assembly setup error, testhost crash mid-run) reads completed but not
  succeeded, so a model hunts for a failing test that does not exist; in the crash case tests silently never ran.

Build already reports a single `BuildOutcome`; RunTest should match.

## Proposal

Decided (owner, 2026-11-23):

- **Shape:** a positional `TestRunOutcome Outcome` parameter replaces both bools on `TestRunResult` (like `BuildResult`); no `[Obsolete]`
  phase. All readers move in the same slice: `TestRunEngine` (about 16 sites incl. `ProjectTestSummary` and the aggregation near
  `:228/:264/:294/:306`), `FullTestMarkerStore.cs:83-108`, `TestRunResultProjector.cs:23`, `WorkspaceBuildTestImpl.cs` (`:145-147`,
  `:170-192`, `:520-535`), and the test constructors (`RunTestTests`, `ResultStatusTests.MakeRun`, `FullTestMarkerStoreTests.Run`,
  `TestRunResultProjectorTests.BuildResult`, `ToolResultMarshallerDetachedTests.cs:166`).
- **Values:**

  | Value | When | IsError |
  | --- | --- | --- |
  | `Passed` | completed, exit 0, >= 1 test ran | false |
  | `TestsFailed` | >= 1 failed test | true (as today) |
  | `NoTestsMatched` | completed, 0 tests ran | false (2026-11-11 decision kept); message names the filter |
  | `BuildFailed` | `dotnet test`'s implicit build failed before tests | true |
  | `TimedOut` | run exceeded the timeout | true |
  | `RunError` | testhost crash or blame-hang, teardown/setup error, non-zero exit with 0 failed tests | true |

- `BuildFailed` is used only if it can be detected structurally (no TRX plus the build result, not message parsing); otherwise those
  runs map to `RunError` and the plan says so.
- **Scoping answer 2026-11-23:** a build failure before tests is NOT structurally detectable. `dotnet test` builds implicitly, so no
  separate build result exists, and "no TRX" is the same branch as a testhost crash (`RunOneProjectAsync`, the `!File.Exists(trxPath)`
  branch). The first slice therefore ships without a `BuildFailed` value: the no-TRX case maps to `RunError`, with its Detail text
  (lock detail or "No TRX result file was produced") as the cause. Add `BuildFailed` later only if a structural signal appears.

## Alternatives considered, not pursued

- Keep the bools `[Obsolete]` beside the enum: no external reader exists (no script parses `runSucceeded`), so the migration phase buys nothing.
- Derive `Outcome` from the bools and hide them: not how Build does it, and it cannot express the new cases.
- Three values (Passed/TestsFailed/Incomplete) or four (with NoTestsMatched): `Incomplete` would mix timeout, build failure and crash,
  which need different next actions.

## Open questions

- Is a build failure before tests structurally distinguishable today (TestRunEngine near `:538`)? Answered 2026-11-23: no (see Proposal).
- Cancellation path was not traced (probably throws rather than returns a result).

## Cost / risk

Medium. Payload shape change for RunTest; `ClaudeLeanSchemaBudgetTests` measures tool definitions only, so the budget should not move
(hypothesis). Acceptance: `ResultStatusTests` pins one row per value; a RunTest test per edge case (zero matched, timeout, build failure
or its RunError fallback, non-zero exit with 0 failed).

## Related

- TODO.md entry "`TestRunResult` outcome enum".
