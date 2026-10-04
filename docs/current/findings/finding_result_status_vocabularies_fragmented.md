# Finding: Result and status types use independent vocabularies, and the same condition maps to different outcomes by path

**Status:** OPEN 2026-10-02. Fix not decided; the shared-interface design is in `proposals/proposal_shared_result_status_interface.md`.

## Context
Audit run 2026-10-02 in session cc715aa0 after the NoChange work (`finding_nochange_producer_classification.md`)
showed that one condition is spelled and handled differently in each layer. Method: a Sonnet agent used
`Search`, `ReadFile` and text search over the loaded solution (5 passes: enums, types, boundary/wire
sites, return families, concept matrix). Scratch outputs are not committed; the counts below are
the agent's. Claims marked "verified" were re-read by me against source; the rest are the agent's reading
and should be treated as unverified until re-read. The agent left 51 matrix cells UNREAD (7 in the
concept matrix, 32 in the type inventory, 12 in the boundary inventory), mostly consumer columns.

## What is broken
Counts (agent): 19 status/outcome enums, 3 string-code classes (`ToolErrorCode` 16 consts,
`MigrationErrorCode` 4, `GitErrorCodes` 6), 82 status-carrying types, about 20 payload-only types,
13 exception classes, 26 conversion sites between vocabularies, 7 `IsError` set-sites.

1. **One condition, opposite protocol flag (verified).** `catch (SolutionNotLoadedException)` in the call
   filter returns `IsError = false` with bare `ex.Message` text
   (`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs:282`). The typed path for the same
   condition gives `IsSuccess = false`, which the second filter turns into `IsError = true`
   (`:335`). The mapper branch `if (ex is SolutionNotLoadedException)` in
   `ToolErrorMapper.ToCodeAndMessage` (`ToolException.cs:213-216`) is unreachable: the class is
   `sealed ... : ToolException` (`ToolException.cs:29`) and the `ex is ToolException` test runs first
   (`:208`). Whether the soft treatment at `:282` is deliberate is not known.
2. **`IsError` is never derived from the typed `IsSuccess`.** It is re-parsed from the serialized
   `"isSuccess"` JSON key in four independent places: `ServiceRegistrationExtensionsBasic.cs:331`
   (`JsonDocument`) and `:472` (`JsonNode`), `ModelAgentRunner.cs:465`, `SubAgentChildServerLauncher.cs:419`
   (agent claim). Bodies keyed `"success"` (`EngineResultWrapper.cs:99-107`, `BasicRefactoringEngine.cs:23-25`,
   `ModelAgentRunner.cs:420/:436`) are invisible to all four; `DocWriteResult.Success` also uses a third
   spelling (agent claim). No result type declares `IsError`. Three setters (`:588`, `:668`, `:845`) return
   bare text with a literal `IsError = true`.
3. **`EditOutcome` is collapsed on the way to the wire (verified).** `RefactoringToolHelpers.ErrorCodeFor(EditOutcome)`
   (`RefactoringToolHelpers.cs:33-38`) maps `TargetNotFound`/`DocumentNotFound` to `NotFound`, `SourceInvalid`
   to `InvalidArgument`, and everything else (10 of 13 members, including `FeatureDisabled`) to
   `ToolErrorCode.Exception`, although `ToolErrorCode.FeatureDisabled` exists. `ToolErrorCode.Exception`
   appears at 100 literal sites in 19 files (24 in `CodemodTools.cs`, 18 in `AdvancedRefactoringTools.cs`).
4. **A failed build or test run is carried as success data (agent claim; `IsSuccess = true` is a hypothesis).**
   `Build` wraps `BuildResult` via `ForPossiblyLargeDataAsync` (`WorkspaceBuildTestImpl.cs:139`); `RunTest`
   with failing tests is `IsSuccess = true` (`:175`); `RunCompleted == false` yields `IsSuccess = false` with
   both `SuccessData` and `ErrorData` set (`:170-173`). `BuildOutcome` is referenced nowhere outside
   `BuildResult.cs`, and `SubAgentEvalResult.cs:71-72` re-reads the serialized `"outcome"` to compensate.
5. **`NoChange` is a failure in one tool family and a skip in another (agent claim, matches the NoChange
   finding).** `RefactoringStructuralImpl.cs:142/:239/:362/:1192` test `Outcome != Modified`;
   `AsyncifyTools.cs:1553` treats it as a skip.
6. **Failure in a success shape.** `ValidateAndApplyHelper.cs:101` reports a blob integrity failure with
   `Error == null` plus a note; `:116` turns a no-op or disabled refactoring into a success shape;
   `:76` turns an `ApplyChangesResult` failure into `Exception` and drops `FailedFiles` (agent claim).
7. **No cancelled/timeout code on the tool surface.** `ToolErrorCode` has no such member, so
   cancellation and timeout fall to `Exception` (`ToolException.cs:218`, verified). `EngineOutcome.Timeout`,
   `AgentStopReason.TurnCapExceeded`/`WallClockCapExceeded` and `StreamIdleTimeoutException` exist in three
   unrelated places (agent claim).
8. **Same concept, several spellings, no shared type (agent claim).**
   - Ambiguous: `CallSiteStatus`, `DocumentLookupStatus`, `SnippetMatchOutcome`, `ToolErrorCode`, `ToolAmbiguousMatchException`.
   - Not found: `TargetNotFound`/`DocumentNotFound` (declared in both `EditOutcome` and `EngineOutcome`), `NotFound`, `NoMatches`, `SymbolNotResolved`, `Found == false`.
   - Already done: `EditOutcome.NoChange`, `ItemOutcome.AlreadySatisfied`, `OperationOutcome.NothingToDo`, `FormatPreviewResult.Changed == false`.
   - Message member: `Message`, `Error`, `ErrorMessage`, `Detail`, `StatusMessage`.
   - `FeatureDisabled` is declared three times (`EditOutcome`, `ToolErrorCode`, `MigrationErrorCode`); `MigrationErrorCode` duplicates four `ToolErrorCode` strings.
   - `Converged` has three meanings, `Skipped` several, `IsError` three senses (protocol flag, `AgentToolCallRecord.IsError`, `BlobWriteResult.IsIntegrityFailure`).

## Root cause
Not a single defect. Each layer grew its own result type for its own consumer: `EditOutcome`/`EngineResultBase`
for edit engines, `EngineResultWrapper<T>` (used only by `BuildEngine` and `DiagnosticEngine`, agent claim),
bespoke `bool Success` records, and `SentinelCallToolResult<T>` at the tool boundary. The boundary converts
by hand (26 sites) and the MCP flag is derived from serialized text rather than from a typed member, so
each conversion is a place to lose information or flip polarity. Item 1 shows the two derivations disagree.

## Why it matters
Per the failure doctrine, a weak model sees `IsError`/`errorCode` and nothing else. When `FeatureDisabled`,
a failed build, a stale view and an internal fault all arrive as `Exception` or as success, the model
cannot choose the right recovery, and our own consumers (`ModelAgentRunner`, `SubAgentChildServerLauncher`)
carry their own re-derivation to cope. Mapping a new engine result to the wire means choosing among
several unrelated shapes with no compiler help.

## Recommendation
Ranked by leverage. Nothing here is built.
1. Add an additive shared status interface in `Common` (`IsError`, `Status`, `Code`, `Message`; no
   `IsSuccess`, matching the MCP protocol's `isError`), and derive the MCP flag from it in one place
   (design and phases in the proposal). Decided in principle by the user 2026-10-02; polarity and member set
   revised 2026-10-04.
2. Make item 1 consistent: pick one polarity for `SolutionNotLoaded` and delete or fix the unreachable branch.
3. Fix `ErrorCodeFor(EditOutcome)` so `FeatureDisabled` maps to `ToolErrorCode.FeatureDisabled` and the
   `Cannot*` members stop defaulting to `Exception` (coordinate with `plan_error_codes_follow_cause.md`).
4. Decide NoChange / AlreadyInState (options a, b, c in the plan and `finding_nochange_producer_classification.md`)
   before any wrapper migration; the producer relabelling must precede it.
5. Add a cancelled/timeout code or an explicit decision that none is wanted.
6. Decide whether a failed build/test is an error result or success data with a status, then make the two consistent.

## Out of scope
- Anonymous and untyped return shapes: see `finding_anonymous_and_untyped_return_shapes.md`.
- Enums that are parameters or classifications, not outcomes (about 45, listed in the agent's enum pass).
- Test-project types.
