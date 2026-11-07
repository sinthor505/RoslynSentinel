# Retire the Build / RunTest / changed-content side stores through [NeverInline] (plan_wire_result_marshaller Step 8)

**Status:** PROPOSED 2026-11-07. Awaiting a decision between options (a), (b) and (c); the survey below found that Step 8 as
written in the plan does not work.

## Motivation

[plan_wire_result_marshaller.md](../plans/plan_wire_result_marshaller.md) Step 8 wants the last three writers that store a
file outside the marshaller (`LargeResultHelper.StoreRawJsonAsync`) to become `[NeverInline]` DTO properties, so the
marshaller moves them to the store and reports them in `largeResult.omitted`. A read-only survey on 2026-11-07
(explore-tier2, source only, nothing run) established what exists and what the step would have to change. Verified facts
are marked with file:line; items under "Not checked" are hypotheses.

### The three side stores today

| | Build full diagnostics | RunTest full results | Changed content |
| --- | --- | --- | --- |
| Writer | `Tools.Basic/WorkspaceBuildTestImpl.cs:222` `StoreFullDiagnosticsAsync` | `WorkspaceBuildTestImpl.cs:239` `StoreFullTestResultAsync` | `Common/ValidateAndApplyHelper.cs:200` `StoreChangedContentAsync` |
| Id property | `BuildResult.FullDiagnosticsResultId` (`Common/BuildResult.cs:26`) | `TestRunResult.FullResultsId` (`Engines.Basic/TestRunEngine.cs:58`) | `ApplyOutcome.ChangedContentResultId` (`ApplyOutcome.cs:46`), copied to `AppliedChangeSummary.ChangedContentResultId` (`AppliedChangeSummary.cs:20`) |
| Stored JSON | `BuildFullDiagnostics(Errors, Warnings)` (`BuildResult.cs:63`), uncapped, `[JsonIgnore]` on `BuildResult.FullDiagnostics` | the whole unprojected engine `TestRunResult` (`TestRunEngine.cs:43`) | `Dictionary<string,string>` keyed by file path |
| Serializer | `SharedJsonOptions.Compact`, no naming policy, so PascalCase keys | same | same |
| Stored when | `Outcome == Failed` or `OmittedErrorCount > 0` (`WorkspaceBuildTestImpl.cs:315`; `:95` in `GetDiagnostics`) | `FailedCount > 0` and not summary (`:378`) | every real apply that wrote files, unless `ChangedContentOptions.InlineOnApply` (`ValidateAndApplyHelper.cs:155`) |
| Wrapper type | `Raw` (`StoreRawJsonAsync`, `LargeResultHelper.cs:94-96`) | `Raw` | `Raw`; null when no solution root |

Sizes: the full Build lists are uncapped (`BuildEngine.cs` ~:398 records a 2,160-error build of about 880 KB). The engine
caps RunTest `Results` at `maxDetails` (default 50) before the tool sees them (`TestRunEngine.cs:257`); the stored object is
at most 50 rows (a few hundred KB at worst is an estimate, not measured).

What a model sees today, from a build with 60 errors (illustrative shape, not a captured response):

```json
{ "successData": { "outcome": "Failed", "errorCount": 60, "omittedErrorCount": 40,
    "errors": [ "...20 items..." ], "fullDiagnosticsResultId": "ab12..." } }
```

It reads the rest with `GetLargeResult(resultId: "ab12...")`, which takes the legacy `Raw` branch
(`Tools.Basic/WorkspaceReadNavigationImpl.cs` ~:1882, character windows over `all.Data.ToJsonString(Compact)`).

### Consumers of the ids and the stored files

| Kind | Where |
| --- | --- |
| Argument alias `fullResultsId` to `resultId` | `Server.Advanced/ToolArgumentValidator.cs:663` (renames a call argument; harmless to keep) |
| Descriptions | `Common/ToolParams.cs:12` (Build), `:15` (RunTest); `Tools.Basic/WorkspaceBuildTestTools.cs:63`; `Tools.Basic/WorkspaceTools.cs:515` |
| Note text built from the id | `AppliedChangeSummary.cs:119-121` (`NotEchoedHint`), doc comment `ApplyOutcome.cs:32` |
| PlanStepRunner | `Utilities.PlanStepRunner/Program.cs:275` only text-scans the first `"errorCount"`; it never reads an id |
| ModelEval | none (`LargeResultPagingAgentTests` uses a synthetic source) |
| Build tests | `Tests.Tools.Basic/BuildToolResultShapeTests.cs:107,112,122,130`; `Tests/BuildResultProjectorTests.cs:159` |
| RunTest tests | `Tests.Tools.Basic/RunTestTests.cs:299,313,355-369,384-399`; `Tests.Basic/TestRunResultProjectorTests.cs:26,41,243-249` |
| Changed-content tests | `Tests.Basic/ChangedContentOffloadTests.cs` (3 tests call `StoreChangedContentAsync` directly), `AppliedChangeSummaryTests.cs` (18 hits), `Tests.Tools.Basic/CompactResultDefaultTests.cs` (5) |
| Changed-content construction sites | about 50 `ChangedContentResultId:` copies: `Tools.Advanced/AdvancedRefactoringTools.cs` (27), `Tools.Basic/RefactoringStructuralImpl.cs` (17), `RefactoringExtractionDocsImpl.cs` (5), `RefactoringSignatureImpl.cs` (3), `GenerationTools.cs` (1) |
| `Raw` writers/readers that stay until Step 10 | the filter backstop `Server.Advanced/ServiceRegistrationExtensionsBasic.cs` ~:593; `GetLargeResultTests` T9-T12b |

## Proposal

Nothing here is decided. The shape below is the design Step 8 implies, with the changes the survey says it needs.

### Why Step 8 as written fails

1. **`[NeverInline]` is applied only over budget.** `ToolResultMarshaller.MarshalAsync` returns the text unchanged when
   `text.Length <= budget` (`ToolResultMarshaller.cs` ~:97), before any parse or hint lookup. `ResultPager.Fit` does call
   `MoveOut` for every `[NeverInline]` path unconditionally (`ResultPager.cs:145`), but the marshaller only reaches `Fit`
   over budget. A small failed build with a populated `fullErrors` list would send it inline, store nothing and add no
   pointer. The existing test `NeverInline_IsAlwaysMovedOut_EvenWhenTheEnvelopeFits` (`ResultPagerTests.cs:455`) calls
   `Fit` directly with a 1,000,000 budget, so it does not cover the marshaller. No marshaller-level test of
   `[NeverInline]` exists.
2. **The sequence pager runs first and ignores `[NeverInline]`.** `FitAndStoreAsync` calls `SequencePager.ResolveSequence`
   before `ResultPager.Fit`; the default rule takes every non-empty array directly under `successData` as the sequence.
   A populated `[NeverInline]` array could become the page sequence instead of being moved out. Whether `BuildPage` then
   falls back to `Fit` is not verified (the existing fallback is `PartsOutsideOverBudget`, ~:241-250).
3. **The old id fields cannot be "populated from the new resultId".** The DTO is serialized before the store runs, so no id
   exists yet. Keeping the id fields additive until Step 10 needs a write-back (for example a `ResultIdProperty` argument on
   `NeverInlineAttribute` that the marshaller patches into the text after the store) or the fields are dropped and the
   descriptions change.
4. **A dictionary cannot be paged by path.** `ResultPager.SelectNode` (`ResultPager.cs:190-232`) splits on `.` without
   escaping, so a key like `src/Foo.cs` cannot be addressed, and `PageEnvelope` (`WorkspaceReadNavigationImpl.cs:1688-1696`)
   rejects non-array, non-string nodes. `AppliedChangeSummary.ChangedContent` is such a dictionary, and it stays inline
   for no-stage, dry-run and `InlineOnApply` results (`AppliedChangeSummary.cs:51-52`), so it cannot simply take the
   attribute. A separate list property of `{path, text}` is needed.
5. **The nested case is not found.** `GetDiagnostics` carries the `BuildResult` under `successData.buildVerification`
   (`DiagnosticReport.cs:124`); `ResultPager.ReflectHints` (`ResultPager.cs:74-110`) reads only direct public properties of
   the success-data type.
6. **Wording.** A deliberate move-out would print "INCOMPLETE: this result was cut to fit", which is wrong when nothing
   was too big (`ToolResultMarshaller.cs` ~:366).

### What it takes, if built

Slice 0, marshaller (prerequisite for every other slice):
- Resolve hints before the budget check. Gate cheaply on "the success-data type has any `[NeverInline]` path" (hints are
  cached per type); parse only when one is non-empty.
- Build the moved-out base through a new public `ResultPager` helper that wraps `MoveOut`. If the base fits, store the
  full text as an `Envelope` and return the base plus a pointer. If it is still over budget, run `ResolveSequence` and `Fit`
  on the base, not on the full parse (emptied arrays are ignored by the default sequence rule, which removes problem 2).
- Neutral pointer wording for a deliberate move-out; keep `LargeResultHints.FetchCall(resultId, path:)`.
- Decide the id write-back (problem 3) or drop the old fields.
- A `Tests.Server` suite at marshaller level: under budget, over budget, store failure, no solution root.

Slice 1, Build: `BuildResult.FullErrors` and `FullWarnings` (`List<DiagnosticInfo>?`), populated under today's store
condition only. Edit `WorkspaceBuildTestImpl.cs` (`Build`, `StoreFullDiagnosticsAsync`), `ToolParams.cs:12`,
`BuildToolResultShapeTests`, `BuildResultProjectorTests`. Decide `GetDiagnostics.buildVerification` here (nested
`[NeverInline]` support, or leave that one `StoreRawJsonAsync` use with a `// TODO`).

Slice 2, RunTest: `TestRunResult.FullResults` (`List<TestCaseResult>?`), set after projection from the unprojected rows.
Edit `TestRunEngine.cs`, `WorkspaceBuildTestImpl.cs`, `ToolParams.cs:15`, `WorkspaceBuildTestTools.cs:63`,
`WorkspaceTools.cs:515`, `RunTestTests`, `TestRunResultProjectorTests`.

Slice 3, changed content (last): add `ChangedFiles` (`List<{path, text}>`) beside the old id, mark the id `[Obsolete]`,
move the ~50 construction sites in slices, and change the `Note` text to point at `largeResult.message`. If the sweep is
too large for the implementer tiers, record it as a `TOOL-GAP:` instead of hand-editing.

Every `[NeverInline]` property must be an array or string so `GetLargeResult` can page it.

### What changes for the model

Raw character windows over PascalCase `{Errors, Warnings}` become `Envelope` path paging over camelCase
`successData.fullErrors`. In `off`/`passthrough` mode and in tests that call a tool method directly, nothing is moved out,
so the full lists would appear inline; the id-based tests (`BuildToolResultShapeTests.cs:130`, `RunTestTests.cs:369`)
would have to run the tool result through a marshaller with a temporary solution root (whether `Tests.Tools.Basic` can host
one is not checked). An in-memory run with no solution root gives a pointer with `warningDetails` and no id, where the
changed-content store today returns a null id silently.

## Alternatives considered, not pursued

- **Do Step 8 as written (declare the properties and let the existing marshaller handle them).** Rejected by problems 1
  and 2 above: small responses would carry the full list inline, and a populated array can be taken as the page sequence.
- **Make the stores themselves "unconditional" and drop the inline caps.** Not pursued: it would serialize the full list on
  every call. Today the full lists are serialized only inside the store and only when the condition holds; with a populated
  `[NeverInline]` list the wire text includes it, is parsed, has the list moved out and is serialized again (about one
  extra parse and one extra serialization per call that has a list; not measured).
- **A dictionary as `[NeverInline]`** (`proposal_sequence_pager.md:102-105,240-245` mentions the idea). Stores the content
  but cannot retrieve it by path (problem 4).

## Open questions

1. Which option: (a) build all three slices, (b) keep the three stores and name them as accepted exceptions to Step 7's
   "no stores outside the marshaller", or (c) slice 0 plus Build and RunTest only, leaving changed content as the
   accepted exception? Recommendation: (c), or (b) if touching these tools is unwelcome. Changed content needs a shape
   change and a ~50-site sweep for a store that is already conditional and works.
2. Id write-back (`ResultIdProperty` on the attribute) or drop the old id fields and change the descriptions?
3. Is nested `[NeverInline]` support worth building for `GetDiagnostics.buildVerification`, or does it keep its one raw
   store?
4. Not checked: `SequencePager.BuildPage` behaviour with a populated `[NeverInline]` array; per-row sizes of
   `DiagnosticInfo` and `TestCaseResult` and any timing; how `FilePathWrapper` serializes as a dictionary key and whether the
   stored path form changes from absolute to wire-relative; the exact `GetDiagnostics` DTO shapes and tests; how the
   offload filter, echo and stamps interact with a pointer on a small response; whether `Tests.Tools.Basic` can host a
   marshaller; the remaining `Tests.Basic` and `Tests.Tools.Basic` hits for the apply-result id beyond those listed.

## Cost / risk

- Slice 0 is contained and additive (no production type declares `[NeverInline]` yet). Slices 1-3 change model-visible
  output of tools that models use constantly (`Build`, `RunTest`, every refactoring apply).
- Step 10 cannot delete the `Raw` wrapper type and its reader until all three stores are gone (options (a)) or are
  accepted as exceptions that keep it (options (b) and (c) for changed content).
- Test churn: about ten test files rewritten to read the new pointer instead of an id field.
- `Utilities.PlanStepRunner` is not affected (it reads `errorCount` from the inline header).

## Related

- [plan_wire_result_marshaller.md](../plans/plan_wire_result_marshaller.md) -- Step 8 (HOLD note), Step 10.
- [proposal_sequence_pager.md](./proposal_sequence_pager.md) -- the default sequence rule and the dictionary note.
- [reference_tool_response_pipeline.md](../references/reference_tool_response_pipeline.md) -- marshaller decisions 3-7 and the
  hints table (`[NeverInline]`: no production use yet).
