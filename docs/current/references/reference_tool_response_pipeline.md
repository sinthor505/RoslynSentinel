# Reference: how a tool's return value becomes the response (result marshaller and call-tool filters)

**Status:** CURRENT 2026-11-06. Verified against source on this date; cites symbols, not line numbers (lines drift).

## Purpose

Read this before changing tool response shape, size or paging, `GetLargeResult`, a stamp on the response, or a
call-tool filter. Two mechanisms touch a response, and picking the wrong one is the usual mistake:

- **The result marshaller** (`Common/ToolResultMarshaller.cs`) runs once per call with the typed result, inside the
  SDK's tool invocation. It owns serialization, the size budget, offload and paging.
- **The call-tool filters** (`Server.Advanced/ServiceRegistrationExtensionsBasic.cs`) wrap the invocation and see only
  the request and the serialized text. They own validation, refusals, exception mapping, stamps and telemetry.

Tools and engines return DTOs. They do not measure, store or trim a result.

## How it works

### Path of one call

```
request
  -> filters, outermost first (table below); a breaker or the argument validator may refuse here
  -> SDK AIFunctionMcpServerTool -> the tool method returns a DTO
  -> MarshalResult hook: SentinelToolFactory.MarshalResultAsync -> ToolResultMarshaller.MarshalAsync
  -> a string, which the SDK emits verbatim as the text content
  -> filters unwind, innermost first: WirePathRelativize, LargeResultOffload (backstop), Drift, exception
     catch-all, ToolError, WorkspaceStaleStamp, HaltStamp, ToolTelemetry, ToolCallEcho
response
```

### The seam (`Common/SentinelToolFactory.cs`)

`McpToolSchemaPatcher.CreateSentinelTool` builds each tool through `SentinelToolFactory.Create` unless the mode is
`off`. The SDK does not expose `MarshalResult` on `McpServerToolCreateOptions`, so the factory creates the `AIFunction`
itself and replicates the SDK's private builders (member list and SDK line numbers in the class remarks). The tool is
still the SDK's own `AIFunctionMcpServerTool`. The hook gets the awaited value of a `Task<T>` method and the declared
`T`; for `Task<object>` the runtime type must come from the value. `SentinelToolFactory.IsHooked` says whether a tool
name was built this way.

`ROSLYNSENTINEL_RESULT_MARSHALLER` (`Common/ResultMarshallerOptions.cs`; environment variable only):

| Value | Effect |
| --- | --- |
| `on`, unset, or unrecognized | Factory plus marshaller; the marshaller owns the wire result |
| `passthrough` | Factory plus marshaller; text is byte-identical to the SDK's, nothing is cut |
| `off` | SDK's own tool creation; no `IToolResultMarshaller` registered |

### Marshaller decisions (`ToolResultMarshaller.MarshalAsync`, `FitAndStoreAsync`)

Budget: `LargeResultHelper.InlineBudgetChars` (15360) minus `EchoReserveChars` (2560), in chars of the wire text. In order:

1. `ForwardGuidance` hands the never-serialized `GuidanceHint` to the telemetry collector. Then `WithNotFoundNotes` adds any
   `NotFoundEvidence` findings to a not-found error, through `IFindingsEnvelope` (filter 8 below covers the thrown form).
2. `Passthrough`: `null`, `string`, `ContentBlock`, `AIContent`, `CallToolResult` and enumerables of the content types
   are returned unchanged and never cut. Anything else is serialized once with the tool's wire options, by default
   `McpJsonUtilities.DefaultOptions` (camelCase, nulls omitted), not `SharedJsonOptions`.
3. Mode not `on`, or text within budget: return the text.
4. Page already built by `GetLargeResult` (`Paging.AlreadyPaged`) and still over budget: one unit is larger than a page,
   so it goes out unchanged, logged, with `OversizeUnitSignal` raised. Never a second cut or a new `resultId`.
5. JSON root is not an object: return the full text (the offload filter handles it).
6. `SequencePager.ResolveSequence` finds the sequence, first match wins: `Paging.RecordsPaths`; the `[ResultRecords]`
   list if non-empty; a bare array or string `successData`; otherwise the larger of the longest string directly under
   `successData` (lines) and all non-empty arrays directly under it (records). If found, the response is the page at
   offset 0, and `largeResult` carries `returned` and `nextOffset`. `SequenceMessages.StripToolPaging` removes the tool's
   own `hasMoreData`, `nextOffset` and any call hint in `statusMessage`, then the marshaller sets `hasMoreData: true`.
7. No sequence, or the parts outside it are over budget alone: `ResultPager.Fit` cuts arrays and strings at any depth and
   `largeResult.omitted[]` lists each cut (`path`, `returned`, `total`, `unit`, `inferred`). Nothing cuttable: full text.
8. Store failure: the cut result goes out with a warning and no `resultId`.
9. Any exception: the original object is returned for the SDK to serialize, logged at error level, nothing tool-visible.

`largeResult` (`LargeResultInfo`, `resultType: "Envelope"`) is written as the first key of the response.

### Paging back (`GetLargeResult`, `Tools.Basic/WorkspaceReadNavigationImpl.cs`)

An `Envelope` file (`LargeResultHelper.StoreEnvelopeAsync`) holds the full marshaller text plus the `Sequence` or
`Omitted` it was cut with, under `.roslynsentinel/largeresults/` (`RetentionDays`: 7). The file's contents select the reader:

| Stored | Reader | Cursor |
| --- | --- | --- |
| `Envelope` with `Sequence` | `PageSequence` -> `SequencePager.BuildPage` | `offset`/`limit` count units (records, or lines with overlong lines chunked) |
| `Envelope` without `Sequence` | `PageEnvelope` -> `ResultPager` | `path` plus `offset`/`limit` |
| Older `ResultWrapperType` values, including `Raw` | Legacy cases in `GetLargeResult` | Per type |

Both Envelope readers mark the page `AlreadyPaged` and leave a small margin under the same budget. An omitted `limit`
fills the budget; a given `limit` is an upper bound.

### Hints a tool can give

| Hint | Where | Use |
| --- | --- | --- |
| `[ResultRecords]` | Property on the `SuccessData` type | The default sequence would pick the wrong list. Today: `TextSearchResult.LiteralResults`, `EnvelopePageResult.Items`, `BuildResult.Errors` |
| `SentinelCallToolResult.Paging` (`PagingHints`) | Set on the envelope; never serialized | Several lists form one sequence, or anonymous and bare-list shapes that cannot carry an attribute |
| `SentinelCallToolResult.Detach(name, list)` / `ToolCallContext.DetachCurrent` | Set on the result or the call context; never serialized | A payload that must be stored, not inline (RunTest `fullResults`, apply `changedFiles`). Must be an array or string |

### Call-tool filters

First added is outermost (csharp-sdk `McpServerImpl.BuildFilterPipeline` folds from the last filter inward). The order is
the `CallToolFilterRegistrations` table in `Server.Advanced/ServiceRegistrationExtensionsBasic.cs` (14 filters; the loop latch is listed as 3a to keep the older item numbers), pinned by
`ToolTelemetryFilterOrderTests` via `CallToolFilterNames`. In registration order:

1. `AddAgentIdentityFilter` (`Server.Advanced/AgentIdentityCapture.cs`): strips `_agentId`/`_agentType`/`_sessionId` from the
   arguments, latches the operating mode and sets the ambient `ToolCallContext` (`Common/ToolCallContext.cs`), with the
   `filePath` and missing term of the tools in its `MissingTermArgument` map. Outermost so the echo and the argument
   validator never see the keys. Never refuses or throws; on failure the call runs with a null context.
2. `AddToolCallEchoFilter`: stamps call id/tool/args onto the first text block (sees final content).
3. `AddToolTelemetryFilter` (`Server.Advanced/ToolTelemetryRecorder.cs`, `Common/ToolTelemetry.cs`): counts every call per
   tool (success / error / error code / duration) for `McpServerStatus.toolTelemetry`. Third on purpose, so it wraps the
   argument-validation and breaker refusals and sees the `IsError` that the ToolError filter (item 7) copied from the body.
   Records after `next` returns, so a status call is counted only from the following call on.
3a. `AddLoopLatchFilter` (`Server.Advanced/LoopLatchFilter.cs`, state `Common/ToolLoopLatch.cs`, flag `--loop-latch`, off by
   default and then a pure pass-through): when the same tool fails the same way twice (same tool + `errorData.errorCode` + first
   diagnostic id for a compile-gate rejection; arguments not part of the key) with no successful call in between, refuses that
   tool's next call WITHOUT running it, for that caller agent id only (no id = one shared `default` key), as a `TargetIneligible`
   failure body whose message starts "{Tool} is DISABLED until another tool succeeds". Any successful call releases the agent's
   latch. Sits fourth, outside argument validation, so plain-text validation rejections count and the refusal precedes them.
   `McpServerStatus.loopLatch` lists latched tools per agent (only while the flag is on). Harness side: `ModelAgentRunner` records a
   refusal as a `latch` assist and stops with `AgentStopReason.LoopLatchIgnored` after the model calls the refused tool twice more.
4. `AddArgumentValidationFilter` (`Server.Advanced/ToolArgumentValidator.cs`): repairs parameter-name case,
   rejects unknown/missing parameters before the SDK binder runs.
5. `AddHaltStampFilter` (`Server.Advanced/HaltStamp.cs`): while any breaker is tripped or the drift latch is set, inserts
   four top-level properties into the first text block of the response: `isSessionHalted`, `sessionHaltKind`,
   `sessionHaltReason`, `sessionHaltRecovery`, so the model learns the recovery call without a discovery call. The
   single source of truth is `HaltInfo.From(...)` in `Common/HaltInfo.cs`. Four kinds, first match wins (the others
   show once it clears): `unrecoverable`, `externalDrift`, `orientation`, `mutation`. The property is named
   `isSessionHalted` for all four kinds, which overstates it for `orientation` and `mutation` (other tools still
   run); `sessionHaltKind` and the reason text carry the precision. State is read after `next` returns (so a breaker an
   inner filter just reset is not stamped). Skipped for a call to `ExternalFileDrift` when the active kind is
   `externalDrift` (its own reply carries the state), and for any body that already contains an `isSessionHalted`
   key. For `mutation`, the recovery text names `ResetMutationBreaker` only if that tool is live right now: the
   filter asks the server's tool collection (`IOptions<McpServerOptions>` -> `ToolCollection.TryGetPrimitive`, the
   same collection `ToolsetService` mutates), which is correct for static modes, claude-lean and tools switched on
   by `McpToolsetControl`; failure to resolve counts as "not active" (recovery says to stop and report). A body that
   is not a JSON object (for example the plain-text refusal from the breaker filters) is wrapped as
   `{"isSessionHalted":...,"message":"<original text>"}`. Any failure is swallowed (a diagnostic aid never breaks a
   call); nothing is stamped when nothing is tripped.
6. `AddWorkspaceStaleStampFilter`: while a workspace reload has failed and not yet succeeded, stamps `workspaceStale`,
   `workspaceStaleReason`, `workspaceStaleRecovery` onto the first text block (state read after `next`).
7. `AddToolErrorFilter` domain-failure sync: response JSON with top-level `isError:true` -> protocol-level
   `CallToolResult.IsError=true` (same name and polarity).
8. `AddWorkspaceNotLoadedFilter` exception catch-all (`CallToolFailureHandler`): an escaped exception becomes a structured
   failure body (`ToolErrorMapper.ToResultError`: `SolutionNotLoadedException` keeps its actionable message, a typed
   `ToolException` keeps its code) with `IsError=true`; a client abort is rethrown. A thrown error never reaches the
   marshaller, so `ToFailureResult` adds the `NotFoundEvidence` findings itself.
9. Post-call drift diagnostic (`AddDriftFilter`, `GetContentExternalFileChangesAsync`, log only).
10. `AddLargeResultOffloadFilter`: the backstop for text the marshaller did not cut (exception catch-all and refusal
    bodies, plain-string returns, marshaller mode off). A text block over `InlineBudgetChars` (read as chars) is stored as
    an `Envelope` file via `StoreEnvelopeAsync` (no omitted list, no sequence; `GetLargeResult` pages the largest array or
    string, inferred), and the caller gets a pointer with a `resultId`. A body that is not JSON cannot be stored and goes
    out whole. Skips any response that raised `OversizeUnitSignal`, to avoid a re-offload loop. Two raisers: the marshaller
    (mode On, for an oversize-unit result it returns whole) and `PagingHints.ForAlreadyPagedPage()` (`Common/PagingHints.cs`),
    the only producer of the `AlreadyPaged` hint, which the `GetLargeResult` page builders call while building the page.
    The second works in every mode (mode off registers no marshaller) and after a marshaller exception. The signal is per
    call (async-local, opened by this filter) and only raised on those paths, so no other tool's oversize response is
    skipped. There is no tool-name exemption. In mode On a hooked tool's over-threshold text logs "Result marshaller miss" at error level;
    otherwise (mode off, unhooked tool) it logs the "Large tool result" warning.
11. `AddWirePathRelativizeFilter`: when `WirePathOptions.Enabled`, rewrites solution-root absolute paths in JSON
    text bodies to relative ones (`WirePathRewriter.RewriteJsonText`).
12. Orientation (automatic) breaker, then the unrecoverable breaker (filters 12 and 13). The latter refuses every tool that is not
    marked `[UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]` on its method
    (`Common/UnrecoverableBreakerAttribute.cs`, resolved by `UnrecoverableBreakerPolicy.IsAllowed` via reflection).
    Absent attribute = refused. Currently 8 names are allowed: `ReadFile`, `ListAll`, `ListSolutionItems`,
    `GetFileOutline`, `GetOperationDetail`, `GetWorkspaceHealth`, `ExternalFileDrift`, `Git`. A tool name declared by
    both a facade (`WorkspaceTools`) and its own class needs the attribute on both.

### Stored results and detached payloads

- Every stored result is an `Envelope` file; `GetLargeResult` reads nothing else. A file written by an older server
  returns a structured `NotFound` ("written by an older server version"); repeat the original call.
- RunTest (`fullResults`) and apply tools (`changedFiles`) hand the marshaller a detached payload: `Detach(name, list)` on
  the `SentinelCallToolResult`, or `ToolCallContext` (`ValidateAndApplyHelper.DetachChangedFiles`). It is stored as its
  own `Envelope` before the budget check and reached with `GetLargeResult(resultId, path: "successData.<name>")`. The
  payload must be an array or string and is dropped in `off`/`passthrough` mode. Build has no side store: it returns
  every error and the marshaller pages `errors`. `ReplaceSnippet`, `CreateFile`, `WriteFile`, `DeleteFile` and `ApplyDiff`
  return their own small result and detach nothing (pinned by `WholeFileApplyResultShapeTests`).
- `ResidualMentionSummarizer` also writes an `Envelope`, outside the marshaller (its `detailResultId`).
- `ResultPager.Fit` and `SequencePager` are both live; retiring `Fit`, `omitted` and `path` is a proposal slice
  ([proposal_sequence_pager.md](../proposals/proposal_sequence_pager.md)), not done.

## Usage

| I want to... | Do this |
| --- | --- |
| Return a result that can be large | Return the whole DTO; add a hint only if the default sequence picks wrongly |
| Change what a page or pointer says | `SequencePager`, `SequenceMessages`, `ToolResultMarshaller.PageSequenceAsync`, `LargeResultHints` |
| Decide using the typed result | The marshaller; filters only have text |
| Add a stamp or other text-level change | A filter (mind the order). If it adds text to every response, check `EchoReserveChars` |
| Change the size budget | `LargeResultHelper` constants; `GetLargeResult` pages use the same ones |
| Rule the marshaller out while debugging | Run a test instance with `ROSLYNSENTINEL_RESULT_MARSHALLER=off` |
| Upgrade the MCP SDK | Re-diff the replicated members against the new tag; `SentinelToolFactoryParityTests` is the guard |

Tests, one `RunTest` at a time:

| Area | Fixtures |
| --- | --- |
| Marshaller | `ToolResultMarshallerOnModeTests`, `ToolResultMarshallerPassthroughTests`, `ToolResultMarshallerDetachedTests` (`Tests.Server`) |
| Factory | `SentinelToolFactoryParityTests` (`Tests.Server`) |
| Pagers | `SequencePagerTests`, `ResultPagerTests` (`Tests.Server`) |
| Filters | `LargeResultOffloadFilterTests`, `ToolTelemetryFilterOrderTests` (`Tests.Server`) |
| `GetLargeResult` | `SequenceLargeResultPagingTests`, `EnvelopeLargeResultPagingTests`, `GetLargeResultTests`, `LargeResultHintsTests` (`Tests.Tools.Basic`) |
| Readers of stored results | `SubAgentOffloadedResultTests` (`Tests.SubAgent`) |
| A model following the pointer | `LargeResultPagingAgentTests` (`Tests.ModelEval`; run with `modeleval-runner`) |

## Gotchas

- **A cut result can contradict itself.** `StripToolPaging` touches only top-level fields, so a cut `GetMethodSource` page
  still has `successData.envelope` with `isComplete: true` and the pre-cut `returnedToLine`. Trust `largeResult` and the
  top-level `hasMoreData`. Seen live 2026-11-06; a `// TODO` in `PageSequenceAsync` marks it.
- **Text the marshaller never saw is not fitted.** That covers filter-built bodies and pass-through return kinds. Exception
  bodies come from filter 8, outside the offload filter, so nothing measures them.
- **What outer filters add is not measured.** The echo and the stamps run outside the offload filter, so only the reserve
  covers them. It is a measured guess; "Result marshaller miss" in the server log means it, or a pager, is wrong.
- **The stored text is the marshaller's, not the final wire text:** no echo or stamps, and paths not yet relativized.
  Pages are relativized on their way out like any other response.
- **Two offload paths still exist.** `largeResult.resultType` tells them apart: `Envelope` is the marshaller, `Raw` is the
  backstop or a side store.
- **`sizeBytes` in the pointer is UTF-8 bytes**, while the budget is chars. Do not compare the two.
- **Domain totals are not offload facts.** `totalRecords` and `hasMoreData` on the envelope keep their tool meaning (caps,
  native paging). On a cut result, `largeResult` is the source for what was cut.
- **Serialize with the wire options.** Measuring or storing with `SharedJsonOptions` gives different text (PascalCase, indented).
- **Changing any of this needs a restart.** See [reference_live_server_freshness.md](reference_live_server_freshness.md);
  the switch is also read when tools are built, so test a mode on a fresh instance.
