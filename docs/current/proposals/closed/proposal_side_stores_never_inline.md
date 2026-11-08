# Retire the Build / RunTest / changed-content side stores (plan_wire_result_marshaller Step 8)

**Status:** IMPLEMENTED 2026-11-07 (slices af7e210f, d9fd0ec6, c6215b34 and the changed-content slice; live check pending; the text below is kept as designed). Was: APPROVED 2026-11-07 (direction, not yet built). The original Step 8 (declare `[NeverInline]` properties and extend
the marshaller) was found not to work as written; the approved direction is "B for Build, C for RunTest and changed
content" below. The 2026-11-07 survey facts are kept because the slices depend on them.

## Motivation

[plan_wire_result_marshaller.md](../../plans/plan_wire_result_marshaller.md) Step 8 wants the last three writers that store a
file outside the marshaller (`LargeResultHelper.StoreRawJsonAsync`) to go away, so every stored result is a marshaller
`Envelope` and Step 10 can delete the `Raw` wrapper type and its reader. Two read-only surveys on 2026-11-07 (explore-tier2,
source only, nothing run) established what exists. Verified facts carry file:line; items under "Not checked" are hypotheses.

### The three side stores today

| | Build full diagnostics | RunTest full results | Changed content |
| --- | --- | --- | --- |
| Writer | `Tools.Basic/WorkspaceBuildTestImpl.cs:222` `StoreFullDiagnosticsAsync` | `WorkspaceBuildTestImpl.cs:239` `StoreFullTestResultAsync` | `Common/ValidateAndApplyHelper.cs:200` `StoreChangedContentAsync` |
| Id property | `BuildResult.FullDiagnosticsResultId` (`Common/BuildResult.cs:26`) | `TestRunResult.FullResultsId` (`Engines.Basic/TestRunEngine.cs:58`) | `ApplyOutcome.ChangedContentResultId` (`ApplyOutcome.cs:46`), copied to `AppliedChangeSummary.ChangedContentResultId` (`AppliedChangeSummary.cs:20`) |
| Stored JSON | `BuildFullDiagnostics(Errors, Warnings)` (`BuildResult.cs:63`), uncapped, `[JsonIgnore]` on `BuildResult.FullDiagnostics` | the whole unprojected engine `TestRunResult` (`TestRunEngine.cs:43`) | `Dictionary<string,string>` keyed by file path |
| Serializer | `SharedJsonOptions.Compact`, no naming policy, so PascalCase keys | same | same |
| Stored when | `Outcome == Failed` or `OmittedErrorCount > 0` (`WorkspaceBuildTestImpl.cs:324`; `:95` in `GetDiagnostics`) | `FailedCount > 0` and not summary (`:386`) | every real apply that wrote files, unless `ChangedContentOptions.InlineOnApply` (`ValidateAndApplyHelper.cs:155`) |
| Wrapper type | `Raw` (`StoreRawJsonAsync`, `LargeResultHelper.cs:95`), no budget check | `Raw` | `Raw`; null when no solution root |
| Inline rows vs stored rows | same type (`DiagnosticInfo`), inline capped at 20 | different: inline is a projection, stored is the unprojected row | different: inline is a note plus id, stored is the content |

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

### Marshaller facts the design rests on

- `SentinelToolFactory.MarshalResultAsync` hands `ToolResultMarshaller.MarshalAsync` the **raw DTO object** before the SDK
  serializes it (`Common/SentinelToolFactory.cs` ~:101-112, ~:160-170), so the marshaller knows the runtime type and can read
  never-serialized members. `SentinelCallToolResult.Paging` (`PagingHints`) and `GuidanceHint` already use that channel.
- `MarshalAsync` order (`Common/ToolResultMarshaller.cs:78-127`): `ForwardGuidance`, `WithNotFoundNotes`, `Passthrough`
  (serialize), budget = 15360 - 2560 = 12800 chars, `if (text.Length <= budget) return text;` at `:98`, then
  `FitAndStoreAsync` (`:326-395`): parse, `SequencePager.ResolveSequence`, `ResultPager.Fit` (the only place hints are
  resolved), `TryStoreAsync` stores the **full original wire text** via `LargeResultHelper.StoreEnvelopeAsync`, pointer
  message hard-coded "INCOMPLETE: this result was cut to fit".
- `LargeResultHelper.StoreEnvelopeAsync(string, string? root, CT, omitted, sequence)` (`LargeResultHelper.cs:107`) is public,
  takes a string, has no budget check and already has a non-marshaller caller (`Tools.Basic/ResidualMentionSummarizer.cs:197`).
- `ResultPager.Fit` 5-arg overload (`ResultPager.cs:127-183`) calls `MoveOut` for every never-inline path with no size
  check (`:145`), then cuts. `ReflectHints` (`:74-110`) reads direct public properties of the success-data type only.
- `GetLargeResult` on an `Envelope` without `Sequence` calls `PageEnvelope` (`WorkspaceReadNavigationImpl.cs:1682-1745`):
  `path` is dot names and `[i]` indexes; only a `JsonArray` or a string can be paged (`:1727-1733`).
- `ToolCallContext` (`Common/ToolCallContext.cs`) is `AsyncLocal`, set by the outermost filter, carries `SolutionRoot` and
  write-gate flags, has no payload slot, and the marshaller does not read it today.
- The request-filter backstop (`Server.Advanced/ServiceRegistrationExtensionsBasic.cs:593`) sees serialized text and calls
  `StoreRawJsonAsync` above 15360 chars (note: a different threshold from the marshaller's 12800).

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
| `Raw` writers/readers that stay until Step 10 | the filter backstop `ServiceRegistrationExtensionsBasic.cs:593`; `GetLargeResultTests` T9-T12b |

## Proposal

Three ways to retire the stores were compared. The approved direction is a combination: **B for Build, C for RunTest and
changed content**. A (the original Step 8) is not pursued.

### A. `[NeverInline]` DTO properties plus marshaller changes (original Step 8; rejected)

Add `BuildResult.FullErrors`, `TestRunResult.FullResults`, `AppliedChangeSummary.ChangedFiles`, all `[NeverInline]`, and
teach the marshaller to resolve hints before the budget check, `MoveOut`, store and page the remainder.

```json
// wire text as the tool returns it (~900 KB on a 2,160-error build)
{ "successData": { "outcome": "Failed", "errorCount": 2160, "omittedErrorCount": 2140,
    "errors": [ "...20..." ], "fullErrors": [ "...2160..." ] } }
// after the marshaller: parse, MoveOut fullErrors, store Envelope, re-serialize
{ "largeResult": { "resultId": "ab12", "omitted": [{ "path": "successData.fullErrors", "total": 2160, "returned": 0 }] },
  "successData": { "outcome": "Failed", "errorCount": 2160, "omittedErrorCount": 2140, "errors": [ "...20..." ], "fullErrors": [] } }
```

Why it fails as written:

1. **`[NeverInline]` is applied only over budget.** `MarshalAsync` returns the text unchanged at `:98` before any hint
   lookup; a small failed build with a populated `fullErrors` goes inline and stores nothing. The only test,
   `NeverInline_IsAlwaysMovedOut_EvenWhenTheEnvelopeFits` (`ResultPagerTests.cs:455`), calls `Fit` directly.
2. **The sequence pager runs first and ignores `[NeverInline]`.** The default rule takes every non-empty array directly under
   `successData`; a populated `[NeverInline]` array could become the page sequence.
3. **The old id fields cannot be "populated from the new resultId".** The DTO is serialized before the store runs.
4. **A dictionary cannot be paged by path.** `ResultPager.SelectNode` (`ResultPager.cs:190-232`) splits on `.`, and
   `PageEnvelope` rejects non-array, non-string nodes. `AppliedChangeSummary.ChangedContent` is such a dictionary and must
   stay inline for no-stage, dry-run and `InlineOnApply` results (`AppliedChangeSummary.cs:51-52`).
5. **The nested case is not found.** `GetDiagnostics` carries the `BuildResult` under `successData.buildVerification`
   (`DiagnosticReport.cs:124`); `ReflectHints` reads only direct properties.
6. **Wording.** A deliberate move-out would print "INCOMPLETE: this result was cut to fit" (`ToolResultMarshaller.cs` ~:366).
7. **Cost.** The full list is serialized into the wire text, parsed, deep-cloned, moved out and serialized again on every
   call that has one, and the inline `errors`/`omittedErrorCount` duplicate what `largeResult.omitted` says.

Fixing 1, 2, 5 and 6 needs the most marshaller machinery of the three options for the least change in behaviour.

### B. Change the DTO: stop capping Build and let the marshaller page (approved for Build)

The Build store exists only because the tool caps inline `errors` at 20. Inline rows and stored rows are the same
`DiagnosticInfo` type, so the store is a hand-rolled copy of what the marshaller already does for every other tool.
Remove the cap and `StoreFullDiagnosticsAsync`; put `[ResultRecords]` on `Errors` so a long `warnings` list never wins the
default sequence rule; mark `FullDiagnosticsResultId` and `OmittedErrorCount` `[Obsolete]` (set to null / 0).

```json
// 12 errors: fits, everything inline, no store, no pointer (today: 12 inline plus a stored copy nobody needs)
{ "successData": { "outcome": "Failed", "errorCount": 12, "errors": [ "...12..." ] } }
// 2,160 errors: existing marshaller decision 6 pages errors at offset 0
{ "largeResult": { "resultId": "ab12", "returned": 85, "nextOffset": 85, "totalRecords": 2160, "hasMoreData": true },
  "successData": { "outcome": "Failed", "errorCount": 2160, "errors": [ "...85..." ] } }
```

Zero marshaller changes. Problem 5 disappears because `Fit` already cuts at any depth under
`successData.buildVerification`. The model gets record paging (`offset`/`limit`, camelCase) instead of raw character
windows over PascalCase. Cost: a 2,160-error build serializes about 900 KB of wire text before the cut (today the same
bytes are serialized into the store, so the delta is one parse and one deep clone; not measured).

B does **not** fit RunTest or changed content: RunTest's inline rows are a projection of the stored rows (dropping the
projection would push 50 stack traces through `Fit`, which cuts each string separately and lists 50 `omitted` entries), and
changed content is a path-keyed dictionary that is inline in some modes and cannot be paged (problem 4).

### C. Change how the two-part result is built: a detached payload (approved for RunTest and changed content)

Let the tool say "this object belongs in the store, not in the text" as data rather than as an attribute: a
never-serialized `Detached` slot (`name`, `object`) on `SentinelCallToolResult` for tools that build their own result, and
the same slot on `ToolCallContext` for code that cannot reach the result (the apply helper). The marshaller, **before** the
budget check: `SerializeToNode` each payload with the wire options, graft it into the parsed tree at
`successData.<name>`, `MoveOut` that path, store the full tree as one `Envelope` (`StoreEnvelopeAsync`), then budget-check
the remainder exactly as today (sequence, `Fit`). Neutral pointer wording for a deliberate move-out.

```csharp
// WorkspaceBuildTestImpl.RunTest: replaces StoreFullTestResultAsync and the FullResultsId splice
result.Detach("fullResults", unprojectedRows);
// ValidateAndApplyHelper: replaces StoreChangedContentAsync, one site; the payload is a list, not a dictionary
ToolCallContext.Current?.Detach("changedFiles", changes.Select(c => new ChangedFile(c.Key.Path, c.Value)).ToList());
```

```json
{ "largeResult": { "resultId": "cd34", "resultType": "Envelope",
    "message": "Stored, not inline: fullResults (7 records). GetLargeResult(resultId: \"cd34\", path: \"successData.fullResults\")",
    "omitted": [{ "path": "successData.fullResults", "total": 7, "returned": 0, "unit": "records" }] },
  "successData": { "passed": 120, "failed": 7, "results": [ "...7 projected rows..." ] } }
```

Why it beats A: the payload is never serialized into the wire text and never passes through hint reflection, so problems
1, 2, 5 and 6 are gone by construction and problem 4 is solved by choosing a list for the payload with no DTO change. The
~50 `ChangedContentResultId:` copies keep compiling (they copy `null`), so that sweep moves to Step 10 instead of blocking
Step 8. Problem 3 stays by design: there is no id in the DTO; `largeResult.resultId` is the id, and `NotEchoedHint` and the
descriptions point there. With no solution root the marshaller emits the pointer with `writtenToFile: false` and
`warningDetails`, where the stores today return a null id silently. Tests that call impl methods directly see the payload on
the result or context without a marshaller.

### Slices

1. **Build (B).** `WorkspaceBuildTestImpl.Build`: remove the 20-cap and `StoreFullDiagnosticsAsync`; `[ResultRecords]` on
   `BuildResult.Errors`; `FullDiagnosticsResultId`, `OmittedErrorCount` `[Obsolete]`; `BuildFullDiagnostics` and the
   `[JsonIgnore]` `FullDiagnostics` property removed or `[Obsolete]`; `ToolParams.cs:12`; `BuildToolResultShapeTests`,
   `BuildResultProjectorTests`. `GetDiagnostics.buildVerification` (`:95`) comes for free. Acceptance: a 2,000-error build
   through the marshaller stays within the marshaller's per-call time (measure once before committing to it).
2. **Marshaller (C).** `Detached` on `SentinelCallToolResult` and `ToolCallContext`; `MarshalAsync` resolves detached
   payloads before the `:98` budget check, grafts, moves out, stores, then continues as today; neutral pointer message;
   `LargeResultHints.FetchCall(resultId, path:)` kept. Additive; nothing declares it until slice 3. A `Tests.Server` suite:
   under budget, over budget (payload plus cut), store failure, no solution root, context null.
3. **RunTest (C).** `Detach("fullResults", ...)` replaces `StoreFullTestResultAsync` and the `:386` splice; `FullResultsId`
   `[Obsolete]`; `ToolParams.cs:15`, `WorkspaceBuildTestTools.cs:63`, `WorkspaceTools.cs:515`; `RunTestTests`,
   `TestRunResultProjectorTests`.
4. **Changed content (C via `ToolCallContext`).** One-line change in `ValidateAndApplyHelper` (`:200-216`, `:197`);
   `ChangedContentResultId` `[Obsolete]` on `ApplyOutcome` and `AppliedChangeSummary`; `NotEchoedHint` points at
   `largeResult`; `ChangedContentOffloadTests`, `AppliedChangeSummaryTests`, `CompactResultDefaultTests`. The ~50 copy sites
   are removed in Step 10 with the `Raw` deletion (`implementer-sweep`, or `TOOL-GAP:` if no tool fits).

Every detached payload must be an array or string at its path so `GetLargeResult` can page it.

### What changes for the model

Raw character windows over PascalCase `{Errors, Warnings}` become `Envelope` paging over camelCase paths: record paging for
Build (`offset`/`limit` on `successData.errors`), `path` paging for `successData.fullResults` and `successData.changedFiles`.
The id lives in `largeResult.resultId` instead of a DTO field. In `off`/`passthrough` mode nothing is detached, so the
RunTest and changed-content payloads are dropped (not inlined); Build lists appear in full.

## Alternatives considered, not pursued

- **A as written** (problems 1-7 above).
- **A with slice 0 marshaller surgery** (resolve hints before the budget check, nested hint support, id write-back via a
  `ResultIdProperty` on the attribute). Superseded by C, which reaches the same place without serializing the payload into
  the wire text or reflecting over nested types.
- **B for RunTest** (return the unprojected rows inline). The projection exists to shape what the model sees; `Fit` would
  cut 50 strings separately.
- **A dictionary as `[NeverInline]`** (`proposal_sequence_pager.md:102-105,240-245`). Stores the content but cannot
  retrieve it by path (problem 4).
- **Keep the three stores as accepted exceptions.** Blocks Step 10 (`Raw` cannot be deleted).

## Open questions

1. DECIDED 2026-11-07 (owner): `[NeverInline]` is removed (attribute, `ResultPager` never-inline paths, `MoveOut` default
   branch, tests). The detached-payload slot is the declarative way to store a payload. (Was: keep until Step 10, decide there.)
2. Not checked: `SequencePager.BuildPage` with an emptied array; per-row sizes of `DiagnosticInfo` and `TestCaseResult` and
   timing for a 2,000-error build; how `FilePathWrapper` serializes and whether the stored path form changes from absolute
   to wire-relative under `WirePathRelativize`; the exact `GetDiagnostics` DTO shapes and tests; how the offload filter,
   echo and stamps interact with a pointer on a small response; whether `Tests.Tools.Basic` can host a marshaller; the
   remaining `Tests.Basic` and `Tests.Tools.Basic` hits for the apply-result id beyond those listed; `LargeResultInfo` field
   casing on the wire.

## Cost / risk

- Slice 2 is contained and additive. Slices 1, 3 and 4 change model-visible output of tools that models use constantly
  (`Build`, `RunTest`, every refactoring apply).
- Step 10 can delete the `Raw` wrapper type and its reader once slices 1, 3 and 4 land and the filter backstop is replaced.
- Test churn: about ten test files rewritten to read `largeResult` instead of an id field.
- `Utilities.PlanStepRunner` is not affected (it reads `errorCount` from the inline header).

## Related

- [plan_wire_result_marshaller.md](../../plans/plan_wire_result_marshaller.md) -- Step 8 (HOLD note), Step 10.
- [proposal_sequence_pager.md](../proposal_sequence_pager.md) -- the default sequence rule and the dictionary note.
- [reference_tool_response_pipeline.md](../../references/reference_tool_response_pipeline.md) -- marshaller decisions 3-7 and the
  hints table (`[NeverInline]`: no production use yet).
