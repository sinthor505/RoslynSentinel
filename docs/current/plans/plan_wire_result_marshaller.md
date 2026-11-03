# Plan: one result marshaller at the SDK MarshalResult seam owns offload, paging, metadata and serialization

**Status:** APPROVED 2026-11-02. In progress. Step 0 package bump landed (3ed8568d; SDK 2.3.0, MEAI 10.11.0, build and
suite verified 2026-11-02, live restart checks pending). Step 1 implemented 2026-11-02 (switch default `off`; `MarshalResult`
receives the awaited value and declared `T` on MEAI 10.11.0). Step 2 implemented 2026-11-02 (`MarshalAsync` gained a `serializerOptions` parameter; live passthrough
check pending). Step 3 implemented 2026-11-02 (pager and synthetic goldens, not wired). Step 4 implemented 2026-11-02 (mode `on` store, pointer, Envelope reader; `GetLargeResult` cannot read
Envelope files until Step 5). Step 5 next. Supersedes
[plan_typed_large_result_offload.md](plan_typed_large_result_offload.md) and
[proposal_central_large_result_offload.md](../proposals/proposal_central_large_result_offload.md), and subsumes
change #5 of `proposal_envelope_field_promotion.md` (inline first page plus `resultId`).

## Problem

Offload metadata (which part of a result is the list, the true total, what was omitted, whether it is complete)
is not a property of the result. It is hand-supplied at 26 `SentinelCallToolResult<T>.ForPossiblyLargeDataAsync`
call sites (`totalRecords ?? 1`), re-guessed from text by `AddLargeResultOffloadFilter` (`itemCount`), and
re-guessed from JSON shape by `GetLargeResult` (first-array-property totals). Two accelerants make every fix
spawn another:

- **Five size and serialization forms.** SDK wire (`McpJsonUtilities.DefaultOptions`: camelCase, nulls omitted);
  helper check (UTF-8 bytes of bare data, default STJ, PascalCase, `LargeResultHelper.cs:~49`); typed files on
  disk (`SharedJsonOptions.Default`, indented PascalCase); filter check (chars, after WirePathRelativize, before
  ToolCallEcho); replay shrink loops (indented). Verified live 2026-11-02: replay pages carry PascalCase element
  keys under a camelCase envelope, and one stored result reported `sizeBytes: 44807` in the pointer and
  `totalChars: 39247` on replay.
- **Six writers, three file shapes, three pointer shapes** (`largeResult{}`, the filter's `offloaded` stub, bare
  ids such as `fullDiagnosticsResultId`), and one writer with no threshold (`ValidateAndApplyHelper`
  changed-content store).

All 11 call-tool filters (`Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `AddRoslynSentinelToolsBasic`)
see only serialized text. The typed result exists in exactly one place: inside the SDK's
`AIFunctionMcpServerTool.InvokeAsync`, between `AIFunction.InvokeAsync` and the default branch that serializes
it (`JsonSerializer.Serialize(result, AIFunction.JsonSerializerOptions.GetTypeInfo(typeof(object)))`, SDK tag
v2.3.0, `AIFunctionMcpServerTool.cs:288-334`). The SDK sets `AIFunctionFactoryOptions.MarshalResult` to identity
there (`:77`) but does not expose it on `McpServerToolCreateOptions`, and the tool's `AIFunction` is internal.

## Decision

1. **Seam.** `McpToolSchemaPatcher.CreateSentinelTool` builds the `AIFunction` itself with
   `AIFunctionFactory.Create(...)` and a Sentinel `MarshalResult`, then calls the public
   `McpServerTool.Create(AIFunction, McpServerToolCreateOptions)`. The result is still the SDK's own
   `AIFunctionMcpServerTool`, so invocation, the result switch, structured content and the legacy-wire output
   schema stay SDK-owned. Only the two private builders the `MethodInfo` overloads run are replicated, copied from
   the v2.3.0 source (table below). The repo moves from SDK 2.2.0 to 2.3.0 first (Step 0).
2. **One marshaller owns the wire.** `MarshalResult` is async, runs once per call with the typed instance and the
   declared return type, and returns a `string` (the SDK's `string text` branch emits it verbatim). It serializes
   once with the tool's wire options, measures that text, and only when over budget parses it to a `JsonNode`,
   fits it with a pure pager, awaits the store, and adds one pointer. Tools and engines return DTOs and never
   measure, store, trim or set offload metadata.
3. **One measure, one form.** Budget is chars of the final wire text. The stored file holds the same wire JSON,
   so disk equals wire and replay needs no type.
4. **Paging by path.** Every cut is recorded as `{path, returned, total, unit}`. A declared hint on the DTO
   (`[ResultRecords]`, `[NeverInline]`) wins; without one the pager cuts the largest array (prefix) or longest
   string (whole lines) and marks the cut `inferred`. A missing hint degrades to inferred paging, never to a char
   window. `GetLargeResult(resultId, path, offset, limit)` pages a path through the same pager.
5. **Domain vs offload metadata.** `TotalRecords`/`HasMoreData`/`NextOffset` on the envelope keep only their
   domain meaning (caps, native paging, e.g. ListAll's 800 matches). Offload facts live only in
   `largeResult.omitted`. Two facts, two fields, no override.

### SDK replication map (source: csharp-sdk tag v2.3.0, `src/ModelContextProtocol.Core/Server/`)

`AIFunctionMcpServerTool.cs`, `RequestServiceProvider.cs`, `McpServerToolCreateOptions.cs` and
`McpServerToolAttribute.cs` are byte-identical between v2.2.0 and v2.3.0 (`git diff --stat v2.2.0 v2.3.0` on the
server folder lists none of them), so the line numbers below hold for both tags.

| SDK private member | Lines | Replica in Sentinel | Notes |
| --- | --- | --- | --- |
| `CreateAIFunctionFactoryOptions` | `AIFunctionMcpServerTool.cs:71-110` | `SentinelToolFactory.CreateFunctionOptions` | Copy verbatim; swap `MarshalResult`; `IsAugmentedWith` replaced by the copied 4-type list |
| `RequestServiceProvider<T>.IsAugmentedWith` | `RequestServiceProvider.cs:19-23` | private static type set | `RequestContext<CallToolRequestParams>`, `McpServer`, `IProgress<ProgressNotificationValue>`, `ClaimsPrincipal` |
| `createTargetFunc` cast to `RequestServiceProvider` | `AIFunctionMcpServerTool.cs:63-67` | `args => CreateTarget(args.Services, toolType)` | `InvokeAsync` sets `request.Services` to the same provider it puts on `args.Services` (`:277-278`), so the existing `CreateTarget(((MessageContext)request).Services, ...)` receives the identical object |
| `DeriveOptions` | `AIFunctionMcpServerTool.cs:161-214` | `SentinelToolFactory.DeriveCreateOptions` | Public attribute surface except the four hint backing fields (`_destructive`, `_idempotent`, `_openWorld`, `_readOnly`, internal): read "was it set" from `method.GetCustomAttributesData()` named arguments |
| `CreateMetadata` | `AIFunctionMcpServerTool.cs:386-403` | `SentinelToolFactory.CreateMetadata` | Method, then declaring-type attributes, then method attributes |
| `McpServerToolCreateOptions.Clone` | `McpServerToolCreateOptions.cs:203` | not needed | Options are built fresh from `CreateOptions` |

Not replicated (run by the public `Create(AIFunction, options)` itself, `:113-159`): tool name validation,
`GetToolDescription`, `CreateOutputSchema`, `AddMcpHeaderExtensions`, annotations, `CreateMetaFromAttributes`
(it uses `function.UnderlyingMethod`, which `AIFunctionFactory` sets).

### NEW-DTO justifications

```
NEW-DTO: OmittedPart (Item, Common.LargeResult)
Evaluated: ListSummary - per-file match counts, no JSON path or returned/total pair; LargeResultInfo - one pointer
  per result, a cut is one of many inside it; ReadEnvelope - line-range metadata for read tools only
Wire impact: new tool output field(s) (largeResult.omitted[])
```

`LargeResultInfo` (`Common/SentinelCallToolResult.cs:432`) is reused and gains one property, `Omitted`
(reuse-first, section 1 of `dto-conventions`). `ResultRecordsAttribute`, `NeverInlineAttribute`,
`IToolResultMarshaller`, `SentinelToolFactory` and `ResultPager` are not DTOs. `ResultWrapperType.Envelope` is an
enum value.

## Execution rules

- Every C# step is dispatched (load `dispatch-implementer` first). Steps 1, 2 and 4 go to `implementer-senior`
  (SDK replication and the async seam); the pager (Step 3) and the call-site sweeps (Steps 7-8) fit
  `implementer` / `implementer-sweep`. The main session reviews and commits.
- `Build` 0 errors. Run `Tests.Server` (factory, filter, echo, relativize fixtures), `GetLargeResultTests`,
  `SubAgentOffloadedResultTests` and each step's fixtures, one `RunTest` at a time. Baseline is 0 failures.
- A step that changes tool creation, the marshaller, the filter or `GetLargeResult` ends with
  `Build(level: fullBuild)`, a server restart and the live call named in its Done line.
- Old beside new: the hook is behind `ROSLYNSENTINEL_RESULT_MARSHALLER` (off | passthrough | on, modelled on
  `WirePathOptions`) until Step 6 makes `on` the default. No stored-file reader or `ResultWrapperType` value is
  removed before Step 10.
- Wire changes are additive until Step 10. Commits carry `Plan: plan_wire_result_marshaller`.
- Incidental finds: `// TODO` in place, named in the report.

## Steps

### Step 0 - Upgrade the MCP SDK to 2.3.0
- Files: `RoslynSentinel.Common/RoslynSentinel.Common.csproj`, `RoslynSentinel.Server.Advanced/RoslynSentinel.Server.Advanced.csproj`,
  `RoslynSentinel.Tools.Advanced/RoslynSentinel.Tools.Advanced.csproj` and any other project referencing
  `ModelContextProtocol`, `ModelContextProtocol.AspNetCore` or `ModelContextProtocol.Extensions.Tasks` (find them
  with a text search over `*.csproj`); `docs/known-build-warnings.*.txt` if the warning set changes.
- Change: all three packages 2.2.0 -> 2.3.0 in one commit (2.3.0 is in the local NuGet cache). SDK 2.3.0 builds
  against Microsoft.Extensions.AI 10.11.0 (repo resolves 10.8.3 today); let it float up transitively, and pin it
  only if a direct reference forces a downgrade (NU1605). Relevant SDK changes v2.2.0 -> v2.3.0 (server folder,
  8 files, none in the tool factory): `McpServerPrimitiveCollection` now lists primitives in insertion order
  (84735d35, #1924), which can reorder `tools/list` and any test or schema-size snapshot that depends on it;
  `McpServerImpl` relaxed legacy request metadata validation (324ccd83, #1852); streamable HTTP transport
  changes. No tool-creation or result-marshalling API changed.
- Done when: `Build` 0 errors, no new warnings against the baseline, full suite green (update only order-dependent
  snapshots, each named in the report); `Build(level: fullBuild)`, restart, `McpServerStatus` and one call each to
  `Search`, `GetLargeResult` and `McpToolsetControl` (runtime tool add uses the primitive collection) on stdio, and
  one tool call on the HTTP host.

### Step 1 - Replicated factory, identity MarshalResult
- Files: new `RoslynSentinel.Common/SentinelToolFactory.cs`; `RoslynSentinel.Common/McpToolSchemaPatcher.cs`
  (`CreateSentinelTool`, `CreateOptions`, `CreateTarget`); new `Tests.Server/SentinelToolFactoryParityTests.cs`.
- Change: implement the replication map. `CreateSentinelTool` uses it when the switch is not `off`, with
  `MarshalResult` = identity. `services` may be null (`ToolSignatureCatalogReflection` passes `null!`); the
  service-binding check then behaves as the SDK's does with no provider. The three `Apply*` patches run on the
  returned tool unchanged. First confirm against the Microsoft.Extensions.AI version resolved after Step 0
  (expected 10.11.0) that `MarshalResult` receives the awaited value of a
  `Task<T>`/`ValueTask<T>` method and the declared `T` as the type argument.
- Done when: for every tool from `DiscoverAllDeclaredTools`, the replica and the SDK-built tool serialize to the
  same `ProtocolTool` JSON (name, title, description, inputSchema, outputSchema, annotations, icons, meta) and
  carry the same `Metadata` sequence (compared by item type and attribute values); three tools invoked through an
  in-memory server (one static, one instance with DI, one task-eligible name from `TaskEnabledToolsHelper`) return
  identical `CallToolResult` text both ways.

### Step 2 - Marshaller contract, pass-through mode
- Files: new `RoslynSentinel.Common/IToolResultMarshaller.cs`, `RoslynSentinel.Common/ToolResultMarshaller.cs`;
  `SentinelToolFactory.cs`; DI registration in `Server.Advanced/ServiceRegistrationExtensionsBasic.cs`; tests.
- Change: `MarshalResult` resolves `IToolResultMarshaller` from the root provider captured at creation (identity
  when absent) and calls `MarshalAsync(toolName, method, result, returnType, ct)`. Pass-through mode: `null`,
  `string`, `ContentBlock`, `AIContent`, `CallToolResult` and their enumerables return unchanged; anything else is
  serialized once with the tool's `SerializerOptions ?? McpJsonUtilities.DefaultOptions` via
  `GetTypeInfo(typeof(object))` and returned as `string`. The marshaller never throws: on any exception it returns
  the original object so the SDK serializes it as today, and logs at error level.
- Done when: a byte-identity test over a fixed set of real tool results (Search, ListAll, Member, Build, an error
  result) shows pass-through text equals the SDK's default text; live call after restart with
  `ROSLYNSENTINEL_RESULT_MARSHALLER=passthrough` shows unchanged output.

### Step 3 - Pager (pure, not wired)
- Files: new `RoslynSentinel.Common/ResultPager.cs`, `OmittedPart.cs`, `ResultRecordsAttribute.cs`,
  `NeverInlineAttribute.cs`; golden tests with captured payloads under `Tests.Server/Resources/`.
- Change: `ResultPager.Fit(JsonNode envelope, int budgetChars, PagerHints hints)` returns the inline node and the
  `OmittedPart` list. Pinned: top-level scalars, `errorData.errorCode`, `errorData.message`, `statusMessage`,
  `warningDetails`, `listSummary`, stamp keys. Bulk: arrays and strings under `successData` and `errorData`.
  `[NeverInline]` properties are always moved out. While over budget, cut the declared records path first, else
  the largest bulk node; arrays keep a prefix, strings keep whole lines; never below one element or line; a node
  still over budget alone is replaced by an empty prefix plus its `OmittedPart`. Measurement uses the wire
  options' `ToJsonString`. Hints resolve per runtime `SuccessData` type by reflection, cached, mapped to wire
  names through the options' naming policy and `[JsonPropertyName]`.
- Done when: golden tests on five payloads pass: a 2000-error Build, a 300-hit Search, the 44 KB `Git(diff)`, the
  122-site SemanticFindReplace preview, a big error. Each pins the inline length <= budget, every `OmittedPart`
  (`path`, `returned`, `total`, `unit`, `inferred`) and that pinned keys survive.

### Step 4 - Store, pointer and the second reader
- Files: `ToolResultMarshaller.cs`; `Common/LargeResultHelper.cs` (new `StoreEnvelopeAsync`, `ResultWrapperType.Envelope`);
  `Common/SentinelCallToolResult.cs` (`LargeResultInfo.Omitted`); `Common/SubAgentEvalResult.cs`
  (`ResolveOffloadedResult`); filter detector in `ServiceRegistrationExtensionsBasic.cs`; tests.
- Change: mode `on`: over `budget = InlineBudgetChars - EchoReserveChars`, parse, fit, await
  `StoreEnvelopeAsync` of the full wire text (Data = the envelope object, so `ResolveOffloadedResult` keeps an
  object), add `largeResult` with `resultType: "Envelope"`, `resultId`, `omitted[]`, `totalRecords` =
  `omitted[0].total` (compatibility only; `omitted` is authoritative) and a fetch hint from `LargeResultHints`.
  Store failure: return the fitted inline result with `omitted` and a warning, no `resultId`. Teach
  `ResolveOffloadedResult` the `Envelope` type. The filter logs at error level when it offloads a response from a
  hooked tool (that is a pager or reserve bug), and still offloads as backstop.
- Done when: tests pin pointer fields, the store-failure path and `ResolveOffloadedResult` on an `Envelope` file;
  `SubAgentOffloadedResultTests` pass unchanged.

### Step 5 - `GetLargeResult` by path
- Files: `Tools.Basic/WorkspaceReadNavigationTools.cs`, `WorkspaceReadNavigationImpl.cs` (`GetLargeResult`);
  `GetLargeResultTests.cs`.
- Change: optional `path` (default: the first `omitted` path of the stored result). For `Envelope` files, select
  the node at `path`, take `offset`/`limit` elements (or lines for a string) and fit the page with
  `ResultPager`, so the page is within budget by construction and the marshaller never re-cuts it. Response:
  `{ path, offset, returned, total, unit, nextOffset, hasMoreData, items | text }`. Old `ResultWrapperType`
  cases are untouched.
- Done when: tests page each Step 3 golden store by path to the end, `total` equals the stored count and the
  concatenated pages equal the stored node; live: the 44 KB `Git(diff)` and a large `Search` page by lines and by
  items after restart.

### Step 6 - Default on
- Files: the switch default; tool descriptions that mention `charLimit`/Raw windows (`ToolParams.cs`,
  `GetLargeResult` description).
- Change: `on` becomes the default. Descriptions name `path`/`limit` as the way to page.
- Done when: full suite green; live checks after restart: Git diff, Search, SemanticFindReplace preview, Build
  with errors, a large error result; a ModelEval smoke run on one overflow task (use `modeleval-runner`).

### Step 7 - Retire per-tool offload
- Files: `Common/SentinelCallToolResult.cs`; the 26 call sites in 12 files; the six direct `StoreLargeResultAsync`
  callers (`AsyncifyTools.cs`, `ScanTools.cs` x2, `SentinelCallToolResult.cs`, `WorkspaceFileEditImpl.cs`,
  `WorkspaceReadNavigationImpl.cs`); `ResultError.ForPossiblyLargeDetailAsync`; `ReadFile`'s
  `TrimRangeToInlineBudget`.
- Change: `ForPossiblyLargeDataAsync` and `ForPossiblyLargeDetailAsync` become
  `[Obsolete("Return the payload; the result marshaller offloads")]` pass-throughs that never store; a caller that
  passed a domain `totalRecords` sets the envelope field itself. Then sweep callers in slices (one project per
  slice) to plain construction. Where a tool has a records list, add `[ResultRecords]` to its DTO property.
- Done when: no production call to either method or to `StoreLargeResultAsync` outside the marshaller;
  `[ResultRecords]` on the list DTOs of ListAll, Search, QuerySymbolRelationships and the scan tools.

### Step 8 - Side stores become `[NeverInline]` properties
- Files: `Tools.Basic/WorkspaceBuildTestImpl.cs` (`StoreFullDiagnosticsAsync`, `StoreFullTestResultAsync`);
  `Common/ValidateAndApplyHelper.cs` (`StoreChangedContentAsync`); their result DTOs; descriptions in
  `Common/ToolParams.cs`, `WorkspaceBuildTestTools.cs`, `WorkspaceTools.cs`; `Server.Advanced/ToolArgumentValidator.cs`
  (`fullResultsId` alias); `BuildToolResultShapeTests`, `RunTestTests`, apply-result tests.
- Change: the full diagnostics, full test results and changed content become `[NeverInline]` DTO properties.
  The marshaller moves them to the store and reports them in `omitted`. Keep the old id fields populated from
  the new `resultId` until Step 10 (additive).
- Done when: tests page the full lists by path; `Utilities.PlanStepRunner` build parsing still reads
  `errorCount` (it is inline header data).

### Step 9 - One measure
- Files: `Common/LargeResultHelper.cs`, `ServiceRegistrationExtensionsBasic.cs` (filter), `GetLargeResult` shrink
  helpers.
- Change: `InlineBudgetChars` and `EchoReserveChars` are the only size constants; `OffloadThresholdBytes` gets
  `[Obsolete]` and loses its last readers; the filter compares the same constant; the pointer reports chars as
  `sizeChars` beside the old `sizeBytes`.
- Done when: no production reader of `OffloadThresholdBytes`; filter tests updated.

### Step 10 - Remove the legacy path (no earlier than 7 days after Steps 7-8 ship)
- Files: `LargeResultHelper.cs`, `WorkspaceReadNavigationImpl.cs`, `SentinelCallToolResult.cs`, the filter.
- Change: delete the old `ResultWrapperType` values and their `GetLargeResult` cases and shrink loops, the
  obsolete helpers, the bare id fields, `SentinelCallToolResult.LargeResult` (the marshaller writes the key) and
  the filter's `GetLargeResult` name exemption. The filter stays as a backstop that only fires on bodies the
  marshaller never saw (exception catch-all, refusals), logged at error level.
- Done when: Build 0 errors, no CS0618 from this plan, full suite green; plan closed with `Closes:` and the two
  superseded docs moved to `closed/`.

### Step 11 - DTO ratchet (G4, independent of offload)
- Files: new `Tests.Server` ratchet fixture modelled on `ToolHintRatchetTests`; baseline under `docs/`.
- Change: by reflection over `[McpServerTool]` methods, count returns of `object`, `string`,
  `SentinelCallToolResult<object>` and `SentinelCallToolResult<string>`; fail on growth and on a stale baseline.
  The migration itself follows `findings/finding_anonymous_and_untyped_return_shapes.md` and
  `findings/finding_engine_layer_anonymous_and_untyped_returns.md`.
- Done when: the ratchet passes at the measured baseline.

## Out of scope

- Merging the stamping filters (Echo, HaltStamp, WorkspaceStaleStamp, ToolError) into one finalizer so the
  reserve can go; worth doing after Step 10, not needed for correctness.
- Populating `StructuredContent`; the text channel stays (`proposal_structuredcontent_rollout.md`).
- The engine-layer DTO migration itself (Step 11 only freezes the count).
- `GetLargeResult` `filter`/`tail` parameters (plan_typed_large_result_offload Step 8); re-plan on top of `path`.

## Risks and open decisions

- **SDK drift.** v2.3.0 (9a327829, released 2026-10-20) is the target. The clone's HEAD (deb6afa9, 2026-10-30)
  already differs from it in `AIFunctionMcpServerTool.cs` (+190/-92 against v2.2.0, including a new
  `returnsCallToolResult` constructor argument), so the release after 2.3.0 will change the replicated code. Any
  later SDK upgrade must re-diff the replicated members against its tag; the Step 1 parity test is the guard.
- **Step 0 side effects.** The Microsoft.Extensions.AI bump (10.8.3 -> 10.11.0) and the insertion-order listing
  are the likely sources of test churn; neither is verified by a build yet.
- **Upstream request (user decision, outward-facing).** Asking the SDK to expose `MarshalResult` on
  `McpServerToolCreateOptions` would delete the replication. No existing issue found 2026-11-02.
- **MarshalResult semantics for async methods** are inferred from the MEAI docs, not run; Step 1 verifies.
- **Reserve.** `EchoReserveChars` (proposed 2560; echo adds up to ~2.3 KB, stamps a few hundred chars) is a
  measured guess; Step 4's filter detector shows if it is too small.
- **Inference on `object` tools** can pick the wrong node; `inferred: true` in `omitted` and the error-level
  detector make it visible, and a `[ResultRecords]` hint fixes it per type.
- **Startup cost.** Building tools through the replica costs the same as today (one `AIFunctionFactory.Create`
  per tool); the parity test builds both and is test-only.
- **Task-mode and HTTP.** `WithTasks` selects by tool name (`TaskEnabledToolsHelper.SelectExecutionMode`) and
  wraps the call, so the hook runs inside it; verify one task-mode call and the HTTP host in Step 1 / Step 6.
- **Wire compatibility.** `SubAgentEvalResult.ResolveOffloadedResult`, `ModelAgentRunner` (`_offloaded` stub) and
  key-pinning tests read today's shapes; all changes before Step 10 are additive.
