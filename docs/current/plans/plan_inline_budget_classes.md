# Plan: Per-result inline budget classes for the large-result offload

**Status:** IN PROGRESS 2026-11-26. Steps 1-12, 14, 15, 21 done (efb0ca55, 061dd50d, 3ec48be7, dcf158b2, c99a321a, c02e171a, 33f92df9, 3ed84cb6, 27dcd63f, 78f5751e, 3add9ba4, 039e8da3, 9544e7c0, 4cb876cc, 16161d9e, 7061be5e, 60094893, 59000bec).

## Problem

Every tool result over the inline budget is paged or offloaded, and the model must call `GetLargeResult`
to read the rest. That is the intended guard for unexpectedly huge results (a 300 KB `ReadFile`), but it is
counterproductive for results the model needs whole: `McpServerStatus` tool listings (20-30 KB) and long
error text. Source: owner request in the plan-design chat, 2026-11-26 (no prior doc).

Owner framing (2026-11-26): the budget is a heuristic, not a rigid limit. The failure it guards against is a
model naively calling a tool and getting a massive (about 10x) result dumped into its context, which sinks the
run, since few tools warn about or let the caller bound result size. A result a few KB over the budget is
better returned inline than forced through a round trip. McpServerStatus is a weak example; the issue occurs
in other cases (evidence: transcript scan, Q17).

Grounding (explore-tier2, 2026-11-26, HEAD 000b3b49):
- The budget is a `const`: `LargeResultHelper.InlineBudgetChars` = 15360 (`Common/LargeResultHelper.cs:21`),
  `EchoReserveChars` = 2560 (`:27`). The marshaller's effective budget is 15360 - 2560 = 12800
  (`Common/ToolResultMarshaller.cs:104`, check at `:115`). Echo is off by default, so the reserve is mostly
  slack; `McpServerStatus(settings: all)` at 13.5K and 14.1K was cut only because of it.
- `MarshalAsync` already receives the tool's `MethodInfo` and ignores it. The backstop
  `LargeResultOffloadFilter` (`Server.Advanced/ServiceRegistrationExtensionsBasic.cs:686`) sees only the tool
  name; there is no per-tool exemption. Precedent for a name-keyed method-attribute lookup:
  `UnrecoverableBreakerPolicy` (`Common/UnrecoverableBreakerAttribute.cs:50`).
- Error bodies: `ResultPager.Fit` pins `errorData.errorCode` and `errorData.message` (`Common/ResultPager.cs:21-23`),
  so an error up to 15360 goes out whole; above that the backstop pointer replaces it and carries no message
  (`ServiceRegistrationExtensionsBasic.cs:798-812`). Thrown-exception bodies are mapped outside the offload filter
  and never measured.
- Measured: `McpServerStatus(toolListing: inactive)` 22,768 chars (44 of 96 rows inline);
  `toolListing: all` about 25K. Call transcripts (all 50 files, 788 calls): 4 offloads, 0 `GetLargeResult` calls.
- Placement: `ToolResultMarshaller.cs` (625 lines), `LargeResultHelper.cs` (163), `SequencePager.cs`,
  `ResultPager.cs`, `SentinelToolFactory.cs`, `PagingHints.cs` are all in `Common`.
  `ServiceRegistrationExtensionsBasic.cs` is 1446 lines (54 under the limit).
  `WorkspaceReadNavigationImpl.cs` (2316) and `WorkspaceFileEditImpl.cs` (2727) are over 1500.

## Decision

- 2026-11-26 Q1: **default budget plus a raised class, capped at one ceiling.** A method attribute sets a tool's
  static budget class; an optional per-call field on `PagingHints` raises it for one call. Both resolve to at
  most one ceiling constant. The backstop offload filter honours the same resolved budget. Why: covers static
  (always-whole tools) and dynamic (only some calls) cases; a "never offload" tag was rejected because it
  removes the only size guard, and the mission targets small-context models. Evidence: `MethodInfo` already
  reaches `MarshalAsync`. (The per-call field was later cut by Q14/Q15; the ceiling became relative by Q18.)
- 2026-11-26 Q2: **failure results get the raised ceiling automatically.** Why: an error is rarely worth paging,
  and this needs no per-tool tagging.
- 2026-11-26 Q3: **in scope:** (1) reserve echo chars only when echo is enabled (superseded by Q13: no reserve
  at all); (2) the backstop pointer for a
  failure body keeps `errorData` code and message; (4) the default budget is configurable per deployment (flag
  or env var; the ceiling stays fixed); (5) size-guard tests pin whole-needed responses under their cap.
- 2026-11-26 Q4: **tag McpServerStatus with the raised class**; its listing redesign stays with
  `proposals/proposal_mcpserverstatus_single_view_parameter.md`. Why: listing calls (22.8-25K) need the whole list.
- 2026-11-26 Q5: **ceiling 32768 chars.** Why: fits the 25K listing and 20-30K error text with headroom.
- 2026-11-26 Q6: **named classes:** `[InlineBudget(InlineBudgetClass.Whole)]`, `enum InlineBudgetClass { Default, Whole }`;
  `Default` maps to the configurable default budget, `Whole` to the ceiling constant. Why: one definition per
  value (checklist item 2); tests assert on the enum.
- 2026-11-26 Q11: **thrown-exception bodies out of scope**; left to DRAFT `plan_shared_tool_execution_pipeline.md`.
- 2026-11-26 Q12: settled by Q4: McpServerStatus is the only tool tagged `Whole` at first.
- 2026-11-26 Q13: **the budget applies to the response body only; echo and stamps are additive on top.** The
  marshaller, the backstop filter and the `GetLargeResult` page builders use the resolved body budget with no
  echo/stamp reserve subtracted; `EchoReserveChars` leaves the budget arithmetic. Why (owner): a model must see
  the same body size whether echo or stamps are enabled; the small wire overhead is acceptable. Evidence
  (explore-tier2 hand count): echo adds about 2.3K, halt stamp about 0.75K, stale stamp about 0.6K.
  Supersedes the reserve part of `proposals/proposal_marshaller_owns_echo_and_stamps.md`.
- 2026-11-26 Q10 (investigation): **per-call budget slot on the `OversizeUnitSignal` pattern.** The marshaller
  resolves the budget (method attribute, failure rule) and writes it to an async-local per-call holder that the
  offload filter opens around `next` and reads afterwards; with no value written (mode off, unhooked tool,
  filter-built body) the filter uses the default budget, raised to the ceiling when the body's top-level
  `isError` is true. Evidence: `Common/OversizeUnitSignal.cs` (mutable AsyncLocal holder, raised from the tool
  continuation, read by the filter); `MarshalAsync` already receives `MethodInfo`; the filter already parses
  `isError` before stubbing.
- 2026-11-26 Q17 (probe): scan of all `ModelTestingResults` transcripts with
  `<scratch root>\plan_design_inline_budget_classes\Find-OffloadedResults.ps1` (output `offloads.csv`,
  `inline.csv`, `summary.txt` beside it). 15,889 transcripts, 124,225 tool calls, 2,906 calls with `largeResult`.
  Archived copies are counted more than once, so the counts are upper bounds. Findings:
  - 2,700 are detached payloads (RenameSymbol, Member, RunTest `changedFiles`/`fullResults`), fetched about 0%:
    working as designed, not in scope.
  - 24 failure results (compile-gate rejections from ReplaceSnippet, ReplaceRange, CreateFile, EditSnippets,
    Member; 13-32.5K chars) were cut by `Fit` on `errorData.structuredDetail[0].diagnostics` (e.g. 2 of 29 kept),
    or the whole `structuredDetail` was dropped (32.5K, 3 calls). **0 of 24 were followed by GetLargeResult.**
    This is the main measured whole-needed case and supports Q2. Diagnostic verbosity (about 750 chars each)
    is logged in INBOX as a separate issue.
  - Non-detached offloads by size: 213 under 15K, 7 at 15-18K, 12 at 18-32K, 2 at 64-150K. Most of the
    under-15K group was cut only by the 12.8K effective budget (eval runs have echo on). Example: ListAll at
    13,084, 53 calls, 42% paged on.
  - ReadFile sequences (up to 82,784 chars) were paged on 97% of the time: paging works there.
  - Search at 21.9K: paged on 22% of the time; the model re-searched instead.
- 2026-11-26 Q16: **tolerance 25%, applied to the resolved budget** (default and `Whole` alike): offload only
  when the body exceeds budget x 1.25 (19,200 default, 40,960 `Whole`). One constant. Why (owner): a result a
  few KB over is cheaper inline than a round trip; Q17 shows 64-150K dumps are still caught.
- 2026-11-26 Q14/Q15: **keep the method attribute, tag McpServerStatus `Whole`, no per-call hint.** Why: errors
  are covered by the failure rule; McpServerStatus (22.8-25K) is the one tool over the default plus tolerance;
  the per-call hint has no user (checklist item 7). Q7 is dropped with it.
- 2026-11-26 Q9: **one configurable default, read everywhere the budget is used.** Startup setting (flag
  `--inline-budget-chars`, env `ROSLYNSENTINEL_INLINE_BUDGET_CHARS`, default 15360, minimum 1024, raised to 8192 by Q25) behind one
  accessor that replaces the constant in the marshaller, the offload filter, both `GetLargeResult` page builders
  (`WorkspaceReadNavigationImpl.cs:1896`, `:2035`), ReadFile's no-solution gate and status message
  (`WorkspaceFileEditImpl.cs:843`, `:1036`) and the `AsyncifyTools.cs:228` warning. Why (owner): strong models
  (Claude) should get larger inline results and larger pages, not 15 KB windows. Claude Code can read offloaded
  results natively, so an oversized result there is manageable; eval harnesses cannot, which is why the default
  stays 15360.
- 2026-11-26 Q18: **`Whole` budget = max(32768, 2 x default).** Why: keeps `Whole` above `Default` on a raised
  deployment (64K default gives 128K `Whole`) and 32768 at the stock default (Q5).
- 2026-11-26 Q19 (design review, item 4): **extract the offload filter first.** `AddLargeResultOffloadFilter`
  (`ServiceRegistrationExtensionsBasic.cs:637-825`, 189 lines) moves to its own file before it grows; the host
  file is 1446 lines and the change adds about 30-50. Text-neutral Step 1.
- 2026-11-26 Q20: **measure by re-scan, no eval gate.** The tests pin the behaviour; after the change ships, the
  promoted scan runs on the next normal eval batches. Expected at the stock default: 0 failure bodies cut at or
  under 40,960 chars and 0 non-detached offloads at or under 19,200 chars. A miss becomes an INBOX line with run ids.
- 2026-11-26 Q21: **promote the scan to `scripts/Find-OffloadedResults.ps1`**, de-duplicating archived copies
  and adding a `-Since` filter.
- 2026-11-26 Design review (items 1-7): pass after Q19. Item 4 exception (owner-noted): in
  `WorkspaceReadNavigationImpl.cs` (2316 lines) and `WorkspaceFileEditImpl.cs` (2707) the change swaps an
  expression and adds no member. Item 3: the per-call slot mirrors `OversizeUnitSignal` instead of extending it,
  so "oversize unit" keeps one meaning; the resolver lives in `LargeResultHelper`, which owns the budget
  constants. Item 5: the attribute lookup is cached per `MethodInfo` (one dictionary lookup per call). Item 7:
  simplest alternative (one raised global default) rejected because it grows every routine result for
  small-context models.
- 2026-11-26 Implementer defaults (no longer open): `EchoReserveChars` is deleted once unused; `GetLargeResult`
  pages fill `DefaultBudgetChars` minus the existing 64-char margin, with no tolerance; ReadFile's gate and its
  "rest stored" message compare against the `Default` offload threshold; a cut result fills its class budget,
  not the threshold; the failure pointer copies the body's `errorData.errorCode` and `errorData.message` nodes
  verbatim under `errorData` (the existing wire names, so no new DTO).

- 2026-11-26 Readiness review 1 (explore-tier2 sonnet): 4 blockers, 11 should-fix; all folded into the steps
  below. Corrections it established: `AddLargeResultOffloadFilter` is `private static void
  AddLargeResultOffloadFilter(IMcpRequestFilterBuilder)` referenced as a method group at
  `ServiceRegistrationExtensionsBasic.cs:256`, not an extension method; `MoveMember` creates `public class X` in
  `X.cs`. The filter parses `isError` only after it has stored the envelope (size check `:686`, store, parse
  about `:765`), so Step 5 reorders. `minValue` on `StartupSetting.Int` rejects (default plus an `InvalidValue`
  problem, and `HaltOnErrors` halts) rather than clamps. `docs/generated/configuration.md` is pinned by
  `Tests.ModelEval/ConfigurationReferenceFreshnessTests.cs` and regenerated by `scripts/Generate-ArchitectureMap.ps1`.
  `SequenceLargeResultPagingTests.cs:27`, `EnvelopeLargeResultPagingTests.cs:27` and `ReadFilePagingLoopTests.cs:29`
  hard-code `PageBudget = 15360 - 2560`; `ReadFilePagingLoopTests.cs:52` pins page offsets observed from an
  in-process run at budget 12800. `McpServerStatusTool` is at `ServerStatusTools.cs:63` (attributes from `:51`).
  `WorkspaceFileEditImpl.cs` is 2727 lines; `AsyncifyTools.cs` is 4061.
- 2026-11-26 Implementer defaults added by the review: in mode `Passthrough` the class is still resolved and the
  signal set before the mode check (harmless; the filter honours it); the detached-remainder check in
  `FinishDetachedAsync` (`ToolResultMarshaller.cs:558`) compares against the threshold like the main check; `Whole`
  is computed in `long` and clamped to `int.MaxValue`; the page margin becomes a shared
  `LargeResultHelper.PageSafetyMarginChars` (64) instead of two private locals; `InlineBudgetChars` and
  `EchoReserveChars` are deleted in a cleanup step once every reference has moved, and a new
  `StockBudgetChars` (15360) is the setting's default value; a raw `CallToolResult` with `IsError` and no
  top-level JSON `isError` key falls to the `Default` threshold in the filter (accepted).

- 2026-11-26 Q22: **known limit: `[InlineBudget]` is not honoured in marshaller mode `off`.** In that mode no
  marshaller writes the signal, so a non-error `Whole` result between the two thresholds is offloaded; failures
  still get the `Whole` threshold through the body's `isError`. Documented in the attribute's doc comment
  (Step 2) and the pipeline reference (Step 15). Why: mode `off` is a debug mode that already differs from
  production (no cutting, detached payloads dropped); a name-keyed scan in the filter would add a fourth
  tool-name derivation (`UnrecoverableBreakerPolicy`, `SentinelToolFactory.ResolveToolName`,
  `McpToolSchemaPatcher.DiscoverToolMethods`) that could drift from the SDK's names.
- 2026-11-26 Q23: **the failure pointer's `errorData.message` is capped at `DefaultBudgetChars`.** When cut, the
  pointer also carries `errorData.messageTotalChars` (int, the original length). Why: the message gets the same
  room as any result and the pointer stays under the threshold (`ResultPager` pins the message at any size).

- 2026-11-26 Readiness review 2 (explore-tier2 sonnet, fresh): 2 blockers, 12 should-fix; folded into the steps
  below or settled as Q24-Q28. Corrections it established: the existing `LargeResultOffloadFilterTests` is a full
  in-process host with no seam for a fake `next`, so the extracted filter gets a public static handler like
  `ErrorPolicyFilter.InvokeAsync` (`ErrorPolicyFilter.cs:91`) and its own unit fixture; Build and RunTest failures
  carry `IsError` plus `successData` (`WorkspaceBuildTestImpl.cs:475`, `:541`); the filter's `isError` parse is at
  `ServiceRegistrationExtensionsBasic.cs:737-748`; `MarshalAsync` spans `ToolResultMarshaller.cs:86-146`; the filter
  should land before the marshaller change (a filter with no signal is more lenient than today); the offload stub is
  an anonymous object serialized with `SharedJsonOptions.Compact` (no naming policy) at about `:801`; `GitTools.cs:69`
  states "about 15K characters" and is baked into both `ToolSurfaceGolden/_catalog.json` files.
- 2026-11-26 Q24: **`Whole` applies to error-only failures**: `isError: true` and no `successData`. Build and RunTest
  failures (which page `successData` lists) keep the `Default` class. One helper,
  `LargeResultHelper.IsErrorOnlyBody(JsonNode?)`, decides it for both the marshaller and the filter. Why: Q17's
  whole-needed failures are compile-gate rejections (`errorData` only); a 2,000-error build should still page.
- 2026-11-26 Q25: **minimum configured default 8192** (margin 2,048 for the pointer at the minimum). Owner follow-up:
  a per-call "already stored" mark prevents re-offload loops. Existing slot: `OversizeUnitSignal` covers only the
  oversize-unit path and `GetLargeResult` pages (`PagingHints.ForAlreadyPagedPage`); nothing marks the marshaller's
  own cut. So `InlineBudgetSignal` also carries `Stored`, set by the marshaller whenever it stores (cut, page or
  detached payload); the filter never stores a response whose `Stored` is set (it logs "Result marshaller miss" if
  that response is still over the threshold). (The `Stored` part was dropped by Q29.)
- 2026-11-26 Q26: **no hold** for the plain-text plan's A/B. Peer roslynsentinel-dc (a96bc27f) replied NO-HOLD:
  every queued eval job is pinned to a commit. Its request: put the hash of the behaviour-changing commit in the
  plan and tell it, so it can mark a batch boundary (its SweepConstructionSites follow-ups compare with pre-change
  baselines). Also noted: plain is now the default tool-result format and is about 1.01x the JSON size, so the
  thresholds carry over.
- 2026-11-26 Q27: **named DTO for the offload pointer.** `OffloadPointer` (new, `Common`) replaces the anonymous
  stub with the same wire names; `ErrorData` reuses `ResultError` (`SentinelCallToolResult.cs:474`; code plus the
  capped message, `Detail` and `StructuredDetail` null); `ErrorMessageTotalChars` is an `int?` on the pointer. Serialized
  with `SentinelToolFactory.WireSerializerOptions` (`SentinelToolFactory.cs:73`: the SDK's tool-result options with a
  relaxed encoder, the options every marshalled result is written with), so `ResultError` comes out as
  `errorCode`/`message` exactly as in a normal `errorData` (it has no `[JsonPropertyName]`, and `Compact` has no
  naming policy) and quotes reach the model literally (`McpJsonUtilities.DefaultOptions` would write `'`,
  pinned by `SentinelToolFactoryParityTests.cs:158-159`; corrected by readiness review 4). The pointer is renamed
  `OffloadPointerResult` (implementer defaults, review 3). NEW-DTO justification: no existing type carries the pointer fields (`LargeResultInfo` is the
  marshaller's `largeResult` block with different fields); closes the pointer entry of
  `findings/finding_anonymous_and_untyped_return_shapes.md`.
- 2026-11-26 Q28 (owner OK): **no end-to-end echo test for Q13.** The marshaller and the filter never read the echo
  setting, and Step 15 deletes `EchoReserveChars` with a search showing no reference left; the full in-process host
  plus an 18K tool body would be the only end-to-end route.

- 2026-11-26 Readiness review 3 (explore-tier2 sonnet, fresh): 3 blockers, 13 should-fix; folded into the steps
  below or settled as Q29. Corrections it established: `ErrorPolicyFilterTests.cs:95` calls `InvokeAsync` with
  plain parameters and a `() => ValueTask.FromResult(input)` delegate; the offload lambda reads `context.Params?.Name`,
  the logger and `PersistentWorkspaceManager.GetSolutionRoot` from `context.Server.Services`, and the statics
  `ResultMarshallerOptions.Mode` and `SentinelToolFactory.IsHooked`, and writes through `StoreEnvelopeAsync`;
  `text` is bound inside the `if` at `ToolResultMarshaller.cs:100`; every marshaller store goes through
  `TryStoreAsync` (`:234`, called at `:302`, `:372`, `:504`); `WorkspaceFileEditImpl.cs:1036` is a `const`
  (compare at `:1058`); the comment formerly at `:828` is in `AddWirePathRelativizeFilter` and stays in the host
  file; `ToolCallEchoOptions.Configure` has no non-test caller (production applies settings through
  `StartupSettings.Discover`, `Program.cs:16`, and `ConfigureAll`, `ServerHttp.cs:32`); the tool-surface goldens are
  read by `RoslynSentinel.Tests.Harness/ToolSurfaceGolden.cs` (`CatalogFileName`, `:45`) and
  `ToolSurfaceGoldenUpdateModeTests`.
- 2026-11-26 Q29: **no `Stored` mark; loops stay covered by `OversizeUnitSignal`.** Supersedes the Q25 follow-up.
  Why: the filter only offloads a response over its threshold, so a stored response within it is untouched
  anyway; the one stored response still over it (an error whose pinned `errorData.message` is huge) is better
  offloaded again with the Q23 cap than sent inline uncapped; the filter runs once per call and its pointer is
  small, so this cannot cycle, and the page re-offload loop is already blocked by `OversizeUnitSignal`. The
  existing "Result marshaller miss" error log flags the second store. A bounded registry keyed by `resultId` was
  discussed and split out as `ideas/idea_offload_registry.md` (telemetry, not loop prevention).
- 2026-11-26 Probe (McpServerStatus, live server at `c7af5ad3`, claude-lean, 124 declared tools):
  `McpServerStatus(toolListing: all, settings: all)` = 34,194 chars (`largeResult.sizeChars`), cut today to 1 of
  124 tool rows inline because the status and settings blocks fill the 12,800 budget. Under the `Whole` threshold
  (40,960) with about 6.8K headroom.
- 2026-11-26 Implementer defaults added by review 3: the marshaller decides error-only from the typed result through
  a new `IFindingsEnvelope.HasSuccessData` (`SuccessData is not null`), so it never re-parses its own text and the
  class is known before the mode check (it applies in mode `passthrough` too); the filter, which has only text, uses
  `LargeResultHelper.IsErrorOnlyBody` with shared key constants (`successData` absent or JSON null counts as absent);
  the pointer drops null fields (declared wire change: `itemCount`, `statusMessage`, `listSummary`, `solutionRoot`,
  `isError` are omitted when null instead of written as null); the pointer DTO is `OffloadPointerResult` (tool-result
  body suffix); `InlineBudgetAttribute` and `InlineBudgetSignal` are created with `DTO-EXEMPT:` in the call's
  `reason` (an attribute and a per-call signal, not data carriers). No contract-skeleton step: Steps 3-4 add small
  new members with no callers, which compile on their own (plan-writer section 2 deviation, noted).

- 2026-11-26 Readiness review 4 (explore-tier2 sonnet, fresh, at `c87fb4be`): 2 blockers, 7 should-fix; all
  folded in. Pointer serializer corrected to `SentinelToolFactory.WireSerializerOptions` (Q27 entry, Steps 4, 5, 13,
  17); Steps 14, 15 and 16a now depend on Step 7 as well (their first pages come from the marshaller);
  `EvalToolSurfaceParityTests` added to Steps 9 and 19; anchors refreshed (`WorkspaceFileEditImpl.cs:844`, `:1037`;
  `ReadFilePagingLoopTests.cs:54-55`); doc line 163 added to Step 20; Step 14 sizes its fixtures from the budget; the
  flag property is `StartupSetting.Flag`. It found no logic gaps in Steps 5 or 7.
- 2026-11-26 Readiness review 5 (explore-tier2 sonnet, fresh, at `4c485150`): no blockers; 5 should-fix, all folded
  in: the ReadFile loop re-pin is three steps (16a makes the asserts report observed offsets, 16b observes, 16c
  pins); Step 17 builds a claude-lean-sized surface, populates `StartupSettings.LastReport` and asserts the fixture
  is over the default threshold; the 11b cut test puts its bulk in `StructuredDetail`; Step 14 sizes the
  `GetLargeResultTests` fixture from the budget; Step 19 also runs Tests.Basic and Tests.Asyncify.
- 2026-11-26 Readiness (plan-design): independent review clean at `4c485150` (review 5: no blockers; its 5
  should-fix findings are folded into this revision).
- 2026-11-26 Run (/implement-plan): staleness check clean at `8f66a0a0` (Step 6 anchors `:844`, `:1037` unchanged).
  Mode Batched (A: 1-4; B: 5-10; C: 11a-13; D: 14-17; E: 18-19; main session 20-22); owner away, so mid-run
  decisions are deferred to the run tracker.
- 2026-11-26 Behaviour change landed: 27dcd63f (Steps 5-9: c99a321a, c02e171a, 33f92df9, 3ed84cb6, 27dcd63f; the marshaller change is 33f92df9). Sent to peer roslynsentinel-dc, which recorded it as a batch boundary.

## Execution rules

- One `Build`/`RunTest` at a time; the dispatcher serializes the Done-when builds of paired steps. Test baseline 0
  failures; every failure is new.
- All C# edits through the RoslynSentinel MCP tools. Implementers never run tests or scripts that run tests; the
  dispatcher runs the tests and scripts named in each step.
- Server source changes reach the live server only after commit and restart (CLAUDE.md, Verification).
- Terms: *budget* = chars a result body may fill when it is cut or paged; *offload threshold* = budget x
  `ToleranceFactor`, the size above which a body is cut or offloaded. Echo and stamps are added on top and never
  counted (Q13). *Error-only* = `isError` true and no success data (Q24).
- Existing tests pinned to the 12,800 budget go red after Steps 5-7 until their test step updates them; run a
  fixture only after its own test step. The old constants stay until Step 18, so nothing breaks compilation early.
- Tests that set `InlineBudgetOptions` change process-wide state: such a fixture is `[NonParallelizable]` (as
  `ToolCallEchoFilterTests` and `ToolCallEchoOptionsTests` are) and restores the setting in TearDown. Tests.Server
  runs fixtures in parallel (`AssemblyInfo.cs`).
- Placement (checklist item 4): the new `Common` files and the `LargeResultHelper` additions sit in `Common` because
  their main consumer, `ToolResultMarshaller`, is in `Common`, and Tools.Basic, Tools.Advanced and Server.Advanced
  also read them; `Common` is the lowest layer that needs them. Owner-noted exception: in
  `WorkspaceReadNavigationImpl.cs` (2316 lines), `WorkspaceFileEditImpl.cs` (2727) and `AsyncifyTools.cs` (4061) the
  change swaps expressions and comments and adds no member.
- Commit the behaviour-changing steps (5-9) together or in order, record the hash under the Decision log as
  `Behaviour change landed: <hash>`, and send it to peer roslynsentinel-dc (Q26). Re-check the anchors in
  `WorkspaceFileEditImpl.cs` before Step 6 (the file changed after grounding).

## Tests

All in-process; the one host fixture (`LargeResultOffloadFilterTests`) already exists and only has its constants
moved. Asserts use the shared constants and accessors, never literals; text is never asserted.

| Test (class.method) | Behaviour (Decision) | Fixture | Asserts / pass criterion | Step |
| --- | --- | --- | --- | --- |
| `LargeResultHelperBudgetTests.DefaultFollowsSetting` | Q9 | new, `[NonParallelizable]` | `DefaultBudgetChars` equals the configured value | 10 |
| `LargeResultHelperBudgetTests.WholeIsMaxOfFloorAndDouble` | Q18 | same | stock -> `WholeFloorChars`; configured `2 x WholeFloorChars` -> `2 x` that | 10 |
| `LargeResultHelperBudgetTests.ThresholdIsBudgetTimesTolerance` | Q16 | same | `OffloadThresholdChars(c) == (int)(BudgetChars(c) * ToleranceFactor)` for both classes | 10 |
| `LargeResultHelperBudgetTests.SettingBelowMinimumIsRejected` | Q25 | same | `BudgetCharsSetting.Read(StartupArgs.Empty, _ => "1024")` yields the default value and an invalid result (no global state) | 10 |
| `LargeResultHelperBudgetTests.ClassForAttributedMethodIsWhole` / `ClassForErrorOnlyIsWhole` / `ClassForPlainMethodIsDefault` | Q14, Q24 | same, private stub methods | enum value | 10 |
| `LargeResultHelperBudgetTests.IsErrorOnlyBody_*` (error-only true; `successData` null true; error with `successData` false; success false; non-object false) | Q24 | same | bool | 10 |
| `LargeResultHelperBudgetTests.HasSuccessDataReflectsSuccessData` | Q24 | same | `IFindingsEnvelope.HasSuccessData` false for a failure without success data, true for a success | 10 |
| `ToolResultMarshallerOnModeTests.InlineWithinTolerance` | Q16 | existing | body just under `OffloadThresholdChars(Default)` -> no `largeResult` | 11b |
| `ToolResultMarshallerOnModeTests.PagedAboveTolerance` | Q16 | existing | just over -> `largeResult` present; response length at most `OffloadThresholdChars(Default)` | 11b |
| `ToolResultMarshallerOnModeTests.WholeMethodInline` | Q4, Q14 | existing | attributed stub method, body between the two thresholds -> no `largeResult` | 11b |
| `ToolResultMarshallerOnModeTests.ErrorOnlyFailureInline` | Q24 | existing | error-only result between the thresholds -> no `largeResult` | 11b |
| `ToolResultMarshallerOnModeTests.ErrorOnlyAboveWholeIsCutButKeepsMessage` | Q2, Q3(2) | existing | error-only result above `OffloadThresholdChars(Whole)` with the bulk in `ResultError.StructuredDetail` (a cuttable list) and a short message -> `largeResult` present, `errorData.message` present (a huge message alone is pinned by `ResultPager` and never takes the cut path) | 11b |
| `ToolResultMarshallerOnModeTests.HonoursConfiguredDefault` | Q9 | existing, made `[NonParallelizable]` | configured `2 x StockBudgetChars`: a body between the stock and the configured threshold -> no `largeResult` | 11b |
| `ToolResultMarshallerOnModeTests.MarshalSetsSignalThreshold` | Q10 | existing | inside `InlineBudgetSignal.Open()`, `ThresholdChars` equals the class threshold after `MarshalAsync`, for a `Default`, a `Whole` and an error-only result | 11b |
| `ToolResultMarshallerPassthroughTests.PassthroughSetsWholeThreshold` | Q10, implementer default | existing | mode `passthrough`, attributed stub method -> `ThresholdChars == OffloadThresholdChars(Whole)` | 11b |
| `ToolResultMarshallerDetachedTests.RemainderUsesThreshold` | implementer default | existing | remainder between budget and threshold -> inline | 12 |
| `ToolResultMarshallerDetachedTests.WholeMethodWithDetachedPayload` | Q14 | same | attributed stub method with a detached payload and a remainder between the thresholds -> remainder inline, payload stored | 12 |
| `LargeResultOffloadFilterHandlerTests.HonoursSignalThreshold` | Q10 | new (`InvokeAsync` with a fake `next`, temp solution root) | `next` sets the `Whole` threshold and returns a body between thresholds -> content unchanged | 13 |
| `LargeResultOffloadFilterHandlerTests.ErrorOnlyWithoutSignalUsesWhole` | Q24 | same | error-only body between thresholds, no signal -> content unchanged | 13 |
| `LargeResultOffloadFilterHandlerTests.NonErrorAboveDefaultOffloaded` | Q16, Q22 | same | non-error body above `OffloadThresholdChars(Default)`, no signal (mode `off` case) -> `OffloadPointerResult` with a `resultId` | 13 |
| `LargeResultOffloadFilterHandlerTests.SkipsWhenOversizeUnitRaised` | Q29 | same | `next` raises `OversizeUnitSignal` and returns a body above the threshold -> content unchanged | 13 |
| `LargeResultOffloadFilterHandlerTests.OverThresholdAfterMarshallerIsOffloadedWithCap` | Q29, Q23 | same | `next` sets the `Whole` threshold and returns an error-only body above it whose message is longer than `DefaultBudgetChars` -> pointer with `ErrorData.Message.Length == DefaultBudgetChars` and `ErrorMessageTotalChars` = the original length | 13 |
| `LargeResultOffloadFilterHandlerTests.HonoursConfiguredDefault` | Q9 | same, `[NonParallelizable]` | configured `2 x StockBudgetChars`: body between the stock and the configured threshold -> unchanged | 13 |
| `LargeResultOffloadFilterHandlerTests.FailurePointerKeepsErrorCodeAndMessage` | Q3(2), Q27 | same | error-only body above the `Whole` threshold with a short message -> deserialized `ErrorData.ErrorCode` and `.Message` equal the body's | 13 |
| `LargeResultOffloadFilterHandlerTests.PointerWireNames` | Q27 | same | a success body that has every field: top-level keys are exactly `solutionRoot`, `offloaded`, `resultId`, `sizeBytes`, `sizeChars`, `itemCount`, `isError`, `statusMessage`, `listSummary`, `message`; a failure adds `errorData` (and `errorMessageTotalChars` when cut); no key has a JSON null value; with a `statusMessage` containing `'` and `<`, the pointer text contains no `\u00` | 13 |
| `SequenceLargeResultPagingTests.PageFillsConfiguredDefault` | Q9 | existing, made `[NonParallelizable]` | configured `2 x StockBudgetChars` -> first page longer than `StockBudgetChars`, at most the configured value | 14 |
| `EnvelopeLargeResultPagingTests.PageFillsConfiguredDefault` | Q9 | same | same, for `PageEnvelope` | 14 |
| `ReadFileTests.NoSolutionGateFollowsConfiguredDefault` | Q9 | existing (`ReadFileTests.cs`, beside the no-solution tests near `:334-460`), made `[NonParallelizable]` if not already | a file between the stock and the configured threshold is read whole | 15 |
| `McpServerStatusWireSizeTests.ListingAndSettingsFitWholeThreshold` | Q3(5), Q4 | new, Tests.Server | serialized envelope length at most `OffloadThresholdChars(Whole)` (live measurement 34,194; outcome classes in Step 17) | 17 |
| `McpServerStatusWireSizeTests.ToolIsTaggedWhole` | Q4 | same | the attribute on `McpServerStatusTool` is `Whole` | 17 |
| `ConfigurationReferenceFreshnessTests`, `EnvVarDeclarationGuardTests`, `KnownArgSetsTests` (Tests.ModelEval, by filter); `ServerSettingsDiscoveryTests` (Tests.Server) | Q9 | existing | pass after regeneration | 2, 19 |
| `ToolSurfaceGoldenUpdateModeTests` and the fixtures that call `ToolSurfaceGolden` (Tests.Harness; `Search(mode: references)` for `ToolSurfaceGolden`), `EvalToolSurfaceParityTests` (Tests.ModelEval) | Step 9 wire text | existing | pass after regeneration | 9, 19 |

## Steps

Lanes: after Step 3, lane A runs Steps 4 -> 5 -> 7 -> 8 (server path), lane B runs Step 6, then the test steps as
their prerequisites land (10 after 3; 13 after 5; 11a, 12 after 7; 11b after 11a; 14, 15, 16a after 6 and 7; 16b, 16c in order after 16a; 17 after 8).
Steps 1, 2, 9 and 21 are independent. Step 18 (cleanup) follows 9-17; then 19 (verification) and 20 (docs). Step
22 is a follow-up that does not block closing. At most two implementing agents at once; the dispatcher serializes
their builds.

### Step 1 - Extract the offload filter into its own file (enabling)
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`,
  `RoslynSentinel.Server.Advanced/LargeResultOffloadFilter.cs` (new)
- Depends on: none
- Effect: text-neutral.
- Change: `AddLargeResultOffloadFilter` is `private static void AddLargeResultOffloadFilter(IMcpRequestFilterBuilder ...)`
  (`:637-825`), registered as a method group at `:256` (`("LargeResultOffload", AddLargeResultOffloadFilter)`).
  1. `ModifyModifier`: make it `internal`.
  2. `MoveMember` (toolset `moveExtract` via `McpToolsetControl`) into new class `LargeResultOffloadFilter`; the
     engine creates `public class LargeResultOffloadFilter` in `LargeResultOffloadFilter.cs` beside the source.
  3. `ModifyModifier`: make the class `public static`.
  4. `ReplaceSnippet`: the `:256` entry references `LargeResultOffloadFilter.AddLargeResultOffloadFilter`. The filter
     name string `"LargeResultOffload"` does not change.
  If `MoveMember` cannot move it, report `TOOL-GAP:` and stop.
- Done when: `Build` 0 errors; dispatcher runs `LargeResultOffloadFilterTests` and `ToolTelemetryFilterOrderTests`
  (Tests.Server), all pass; the host file is under 1300 lines.
- Lane: implementer. Effort: S.
- Done: efb0ca55 (ModifyModifier is gated in claude-lean; one-token ReplaceSnippet edits instead)

### Step 2 - Budget class, attribute and setting (new code)
- Files: `RoslynSentinel.Common/InlineBudgetClass.cs` (new), `RoslynSentinel.Common/InlineBudgetAttribute.cs` (new),
  `RoslynSentinel.Common/InlineBudgetOptions.cs` (new)
- Depends on: none
- Effect: text-neutral (no callers yet); the configuration reference gains one setting.
- Change:
  - `public enum InlineBudgetClass { Default, Whole }`; docs: `Default` = the configured default budget, for routine
    results that page harmlessly; `Whole` = results the caller needs in one piece.
  - `[AttributeUsage(AttributeTargets.Method)] public sealed class InlineBudgetAttribute(InlineBudgetClass budgetClass) : Attribute`
    with `public InlineBudgetClass BudgetClass { get; } = budgetClass;` (create with `DTO-EXEMPT: attribute` in the
    call's `reason`). Doc: put on an `[McpServerTool]` method; the result marshaller reads it through the method's
    `MethodInfo`; not honoured in marshaller mode `off` (Q22).
  - `InlineBudgetOptions`, copied from `ToolCallEchoOptions` (`RoslynSentinel.Common/ToolCallEchoOptions.cs`):
    `[StartupSettingGroup("Inline budget")] public static class InlineBudgetOptions` with
    `public const int StockBudgetChars = 15 * 1024;`, `public const int MinimumBudgetChars = 8192;`,
    `public static readonly StartupSetting<int> BudgetCharsSetting = StartupSetting.Int("--inline-budget-chars", "ROSLYNSENTINEL_INLINE_BUDGET_CHARS", StockBudgetChars, "<one sentence: chars a tool result body may fill inline before it is paged; echo and stamps are extra>", SettingScope.Server, minValue: MinimumBudgetChars);`
    (signature `StartupSetting.Int(flag, env, defaultValue, description, scope, kind = Startup, minValue = null)`,
    `Configuration/StartupSetting.cs:175`), `public static int BudgetChars => BudgetCharsSetting.Value;` and
    `public static void Configure(string[] args)` calling `StartupSettings.ConfigureGroup(typeof(InlineBudgetOptions), args)`
    (for tests; production discovers the group through `StartupSettings.Discover`, so no call site is added).
  - Dispatcher, after the build: `pwsh -NoProfile -File scripts/Generate-ArchitectureMap.ps1` regenerates
    `docs/generated/configuration.md`; commit only the configuration diff (leave unrelated regenerated files
    unstaged and mention them).
- Done when: `Build` 0 errors; dispatcher runs `ConfigurationReferenceFreshnessTests`, `EnvVarDeclarationGuardTests`
  and `KnownArgSetsTests` (Tests.ModelEval, by class filter) and `ServerSettingsDiscoveryTests` (Tests.Server), all pass.
- Lane: implementer (code); dispatcher (regeneration). Effort: S.
- Done: 061dd50d

### Step 3 - Budget resolver and typed error-only flag (new code)
- Files: `RoslynSentinel.Common/LargeResultHelper.cs`, `RoslynSentinel.Common/SentinelCallToolResult.cs`
- Depends on: Step 2
- Effect: text-neutral (no callers yet; the new interface member is not serialized).
- Change:
  - `LargeResultHelper`, beside `InlineBudgetChars` (`:21`): `public const double ToleranceFactor = 1.25;`,
    `public const int WholeFloorChars = 32768;`, `public const int WholeMultiplier = 2;`,
    `public const int PageSafetyMarginChars = 64;`, `public const string IsErrorKey = "isError";`,
    `public const string SuccessDataKey = "successData";`, `public const string ErrorDataKey = "errorData";`,
    `public static int DefaultBudgetChars => InlineBudgetOptions.BudgetChars;`,
    `public static int BudgetChars(InlineBudgetClass budgetClass)` (`Default` -> `DefaultBudgetChars`; `Whole` ->
    `max(WholeFloorChars, WholeMultiplier * DefaultBudgetChars)` computed in `long`, clamped to `int.MaxValue`),
    `public static int OffloadThresholdChars(InlineBudgetClass budgetClass)` (`(int)Math.Min(int.MaxValue, BudgetChars(c) * ToleranceFactor)`),
    `public static InlineBudgetClass ClassFor(MethodInfo? method, bool errorOnly)` (`Whole` when `errorOnly`; else the
    method's `InlineBudgetAttribute.BudgetClass`; else `Default`; attribute lookups cached in a
    `ConcurrentDictionary<MethodInfo, InlineBudgetClass>`),
    `public static bool IsErrorOnlyBody(JsonNode? root)` (a `JsonObject` whose `IsErrorKey` value is JSON `true` and
    whose `SuccessDataKey` is absent or JSON null). Leave `InlineBudgetChars` and `EchoReserveChars` in place
    (deleted in Step 18). `ResultPager`'s private `ErrorKey` (`ResultPager.cs:50`) is left as is; add
    `// TODO use LargeResultHelper.ErrorDataKey` beside it and an INBOX line.
  - `IFindingsEnvelope` (`SentinelCallToolResult.cs:250-260`): add `/// <summary>True when success data is present (a failure may still carry it, e.g. Build).</summary> bool HasSuccessData { get; }`;
    implement it on `SentinelCallToolResult<TSuccess, TError>` as `[JsonIgnore] public bool HasSuccessData => SuccessData is not null;`.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: 3ec48be7

### Step 4 - Per-call signal and pointer DTO (new code)
- Files: `RoslynSentinel.Common/InlineBudgetSignal.cs` (new), `RoslynSentinel.Common/OffloadPointerResult.cs` (new)
- Depends on: Step 3
- Effect: text-neutral (no callers yet).
- Change:
  - `InlineBudgetSignal` (create with `DTO-EXEMPT: per-call signal` in the call's `reason`), mirroring
    `RoslynSentinel.Common/OversizeUnitSignal.cs` (53 lines: AsyncLocal `Slot`, `Open()` returning the disposable
    holder, restore-previous on `Dispose()`): `public static InlineBudgetSignal Open()`,
    `public static void SetThreshold(int thresholdChars)` (no-op when no holder is open) and
    `public int? ThresholdChars { get; }`. Doc: written by the result marshaller, read by the offload filter after `next`.
  - `OffloadPointerResult`, with this NEW-DTO block in the creating call's `reason`:
    `NEW-DTO: OffloadPointerResult (tool-result body, Common.LargeResult). Evaluated: LargeResultInfo (the marshaller's largeResult block; different fields), ResultError (reused for ErrorData), the anonymous stub it replaces. Wire impact: same key names as the stub; null fields now omitted; errorData and errorMessageTotalChars added on failures.`
    `public sealed record OffloadPointerResult` with `string? SolutionRoot`, `bool Offloaded`, `string? ResultId`,
    `int SizeBytes`, `int SizeChars`, `int? ItemCount`, `bool? IsError`, `string? StatusMessage`, `JsonNode? ListSummary`,
    `string Message`, `ResultError? ErrorData`, `int? ErrorMessageTotalChars`. Doc: serialized with
    `SentinelToolFactory.WireSerializerOptions` (camelCase, nulls omitted, relaxed encoder).
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: dcf158b2

### Step 5 - Offload filter: handler, threshold, typed pointer (behaviour-changing)
- Files: `RoslynSentinel.Server.Advanced/LargeResultOffloadFilter.cs` (from Step 1),
  `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs` (one comment in `AddWirePathRelativizeFilter`)
- Depends on: Steps 1, 3, 4
- Effect: behaviour-changing: the backstop offloads only above the default threshold (19,200 stock) or, for an
  error-only body, the `Whole` threshold; a failure pointer carries the capped error. Until Step 7 lands no signal is
  written, which only makes the filter more lenient than today.
- Change:
  - Move the filter lambda's body into
    `public static async ValueTask<CallToolResult> InvokeAsync(string? toolName, Func<string?> solutionRoot, ILogger? logger, Func<ValueTask<CallToolResult>> next, CancellationToken cancellationToken)`.
    `AddLargeResultOffloadFilter` registers a lambda that resolves the tool name (`context.Params?.Name`), the logger
    and a lazy solution-root accessor (`PersistentWorkspaceManager.GetSolutionRoot`) from `context.Server.Services` as
    today and calls it. The statics `ResultMarshallerOptions.Mode` and `SentinelToolFactory.IsHooked` stay read
    inside (they only choose the miss-log wording). `logger` may be null (tests pass null): use `logger?.` for every
    log call. Pattern: `ErrorPolicyFilter.InvokeAsync` (`ErrorPolicyFilter.cs:91`).
  - Inside, reordered (today the size check, formerly `:686`, runs before the store and the `isError` parse, formerly
    `:737-748`, after it):
    1. Open `InlineBudgetSignal` beside `OversizeUnitSignal.Open()` (formerly `:662`), around `next`.
    2. After `next`: return unchanged when `OversizeUnitSignal` was raised (as today).
    3. `threshold = signal.ThresholdChars ?? OffloadThresholdChars(Default)`; return unchanged when every text block
       is within it.
    4. With no signal value: parse the block (the parse moves up) and, when `LargeResultHelper.IsErrorOnlyBody(root)`,
       set `threshold = OffloadThresholdChars(Whole)`; return unchanged when within it.
    5. Store and stub as today, reusing the parsed root (parse it here if step 4 did not) for `isError`, `listSummary`,
       `totalRecords`, `statusMessage`. The existing "Result marshaller miss" log (formerly `:696-709`) keeps firing for
       a hooked tool in mode `on`, now naming the threshold. The stub is an `OffloadPointerResult` serialized with
       `SentinelToolFactory.WireSerializerOptions` (replacing the anonymous object and `SharedJsonOptions.Compact`, formerly
       `:799-813`); its `Message` names the threshold used. For an error-only body set
       `ErrorData = new ResultError(errorCode, message)` from the body's `errorData` (`ErrorDataKey`), the message cut to
       its first `LargeResultHelper.DefaultBudgetChars` chars, and `ErrorMessageTotalChars` = the original length when
       cut (Q23).
  - Replace every `InlineBudgetChars` use (formerly `:686`, `:703`, `:709`, `:812`) with the threshold; rename the log
    placeholder `{OffloadThresholdBytes}` (formerly `:702`) to `{OffloadThresholdChars}`; reword the comments that
    mention the reserve or "15KB" (formerly `:643`, `:696`) and, in the host file, the one in
    `AddWirePathRelativizeFilter` (formerly `:828`).
- Done when: `Build` 0 errors.
- Lane: implementer-senior (restructure inside a 189-line lambda). Effort: M. Pairs with: Step 6.
- Done: c99a321a

### Step 6 - Tool-side readers follow the configured default (behaviour-changing)
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`, `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`,
  `RoslynSentinel.Tools.Advanced/AsyncifyTools.cs`
- Depends on: Step 3
- Effect: behaviour-changing: `GetLargeResult` pages fill the configured default (15,296 at stock instead of
  12,736); ReadFile's no-solution gate and "rest stored" message follow the default threshold.
- Change (expression and comment swaps only, no new members; re-check the `WorkspaceFileEditImpl.cs` anchors first):
  - `PageSequence` (`:1896`) and `PageEnvelope` (`:2035`): drop the locals `safetyMarginChars` (`:1891`, `:1984`);
    the budget becomes `LargeResultHelper.DefaultBudgetChars - LargeResultHelper.PageSafetyMarginChars`. Update the
    comments at `:1884-1885` and `:1979` that mention the echo reserve.
  - `ReadFileWithoutSolutionAsync` (`:844` at `c87fb4be`): compare against `LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Default)`.
    `ReadFile` (`:1037`): `const int thresholdBytes = LargeResultHelper.InlineBudgetChars` becomes a non-const local
    `var thresholdBytes = LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Default);` (compared about `:1059`).
    (Both compare UTF-8 bytes with a char threshold; pre-existing, not changed.)
  - `ScanAsyncMigrationCandidatesTool` (`AsyncifyTools.cs:228`): `LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Default)`.
- Done when: `scripts/build.ps1 -Flavor Solution` 0 errors.
- Lane: implementer. Effort: S. Pairs with: Step 5.
- Done: c02e171a (verified with Build; scripts/build.ps1 hangs non-interactively, INBOX)

### Step 7 - Marshaller resolves the class and sets the signal (behaviour-changing)
- Files: `RoslynSentinel.Common/ToolResultMarshaller.cs`
- Depends on: Steps 4, 5
- Effect: behaviour-changing: bodies up to 19,200 chars (stock) go out inline; error-only failures and
  `[InlineBudget(Whole)]` tools up to 40,960; echo no longer shrinks the budget.
- Change, in `MarshalAsync` (`:86-146`):
  - Before the `if` at `:100` (so it also runs in mode `passthrough`):
    `var budgetClass = LargeResultHelper.ClassFor(method, errorOnly: result is IFindingsEnvelope { IsError: true, HasSuccessData: false });`
    `var threshold = LargeResultHelper.OffloadThresholdChars(budgetClass);` `var budget = LargeResultHelper.BudgetChars(budgetClass);`
    `InlineBudgetSignal.SetThreshold(threshold);`.
  - Remove `const int budget` (`:104`). The inline check (`:115`) compares with `threshold`. Cutting and paging
    (`FitAndStoreAsync`, the sequence page) keep filling `budget`. The already-paged warning (about `:127-130`)
    compares with `OffloadThresholdChars(Default)` (pages are built at the default budget).
  - `FinishDetachedAsync`: add a parameter `int thresholdChars` after `budget`; the remainder check (`:558`,
    `remainderText.Length <= budget`) compares with `thresholdChars`.
  - Class doc (`:27-31`, `:46-48`): no echo reserve; a cut body fills its budget and the 25% tolerance covers the
    pointer. Drop the `EchoReserveChars` cref there.
- Done when: `Build` 0 errors; `Search(mode: text, query: "EchoReserveChars", fileGlob: "RoslynSentinel.Common/ToolResultMarshaller.cs")`
  returns nothing.
- Lane: implementer. Effort: S.
- Done: 33f92df9

### Step 8 - Tag McpServerStatus (behaviour-changing)
- Files: `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`, `RoslynSentinel.Server.Advanced/ErrorPolicyFilter.cs`
- Depends on: Step 7
- Effect: behaviour-changing: `McpServerStatus` responses up to 40,960 chars go out inline.
- Change: add `[InlineBudget(InlineBudgetClass.Whole)]` to `McpServerStatusTool` (`ServerStatusTools.cs:63`,
  attribute stack from `:51`; the only declaration of the tool name). In `ErrorPolicyFilter.cs:78` reword the log text
  that says the appended block "shares the EchoReserveChars margin": the appended block is outside the body budget.
- Done when: `Build` 0 errors.
- Lane: implementer. Effort: S.
- Done: 3ed84cb6

### Step 9 - Git tool description and tool-surface goldens (behaviour-changing, wire text)
- Files: `RoslynSentinel.Tools.Basic/GitTools.cs` (verify the project with `Search(mode: class, query: "GitTools")`), then
  the `ToolSurfaceGolden` folders in Tests.Harness and Tests.ModelEval and `docs/generated/architecture_tools.md`
  (regenerated from HEAD; last regenerated by a peer at `c87fb4be`)
- Depends on: none
- Effect: behaviour-changing (tool description text).
- Change: in the `maxBytes` description at `GitTools.cs:69`, replace "Output over about 15K characters is paged via
  GetLargeResult regardless of maxBytes" with "Large output is paged via GetLargeResult regardless of maxBytes" (no
  number: the size is configurable). Dispatcher regenerates the golden catalogs by the update mode that
  `ToolSurfaceGoldenUpdateModeTests` exercises (read `ToolSurfaceGolden.cs` for the switch), from HEAD. Peer
  roslynsentinel-dc confirmed (2026-11-26) it has no pending golden work; check `Git(status)` first and stop only if a
  golden diff is not ours.
- Done when: `Build` 0 errors; dispatcher runs `ToolSurfaceGoldenUpdateModeTests` and the fixtures that reference
  `ToolSurfaceGolden` (Tests.Harness) and `EvalToolSurfaceParityTests` (Tests.ModelEval, `:101`, `:115`), all pass;
  `ClaudeLeanSchemaBudgetTests` (Tests.Server) passes.
- Lane: implementer (description); dispatcher (goldens). Effort: S.
- Done: 27dcd63f (19 golden files regenerated; docs/generated unchanged)

### Step 10 - Resolver and setting tests
- Files: `RoslynSentinel.Tests.Server/LargeResultHelperBudgetTests.cs` (new)
- Depends on: Step 3
- Effect: tests only.
- Change: the Step 10 rows of the Tests table. Set the option with
  `InlineBudgetOptions.Configure([$"{InlineBudgetOptions.BudgetCharsSetting.Flag}={value}"])` and `Configure([])` in
  TearDown (pattern: `ToolCallEchoFilterTests.cs:104`, `:116`; the flag property is `StartupSetting.Flag`,
  `Configuration/StartupSetting.cs:108`);
  `[NonParallelizable]`.
- Done when: dispatcher runs `LargeResultHelperBudgetTests`, all pass.
- Lane: test-writer. Effort: S. Pairs with: Step 4.
- Done: 78f5751e

### Step 11a - Marshaller tests: move pinned budgets
- Files: `RoslynSentinel.Tests.Server/ToolResultMarshallerOnModeTests.cs`
- Depends on: Step 7
- Effect: tests only.
- Change: move the existing `InlineBudgetChars` sites (`:149`, `:155`, `:291`, `:335`, `:350`, `:438`, `:455`, `:531`)
  to the accessor each test means: "fits inline" and "fitted plus pointer" bounds use `OffloadThresholdChars(Default)`;
  "a page fills" bounds use `BudgetChars(Default)`. The fixture whose sanity check at `:223` is `GreaterThan(12_800)`
  (about 14.7K) asserts against `OffloadThresholdChars(Default)` and is enlarged past it; the 20,000-char records at
  `:486`, `:506` are raised to clear the threshold by at least 25%. The 2,000-error build test (`:262`, `IsError` with
  success data) keeps the `Default` bound (Q24).
- Done when: dispatcher runs `ToolResultMarshallerOnModeTests`, all pass.
- Lane: test-writer. Effort: S.
- Done: 039e8da3, fix 9544e7c0 (MarshalPagedAsync whole-response bound is the threshold, not the budget)

### Step 11b - Marshaller tests: new behaviour
- Files: `RoslynSentinel.Tests.Server/ToolResultMarshallerOnModeTests.cs`, `RoslynSentinel.Tests.Server/ToolResultMarshallerPassthroughTests.cs`
- Depends on: Step 11a
- Effect: tests only.
- Change: the Step 11b rows of the Tests table; `ToolResultMarshallerOnModeTests` becomes `[NonParallelizable]` for
  `HonoursConfiguredDefault`.
- Done when: dispatcher runs both fixtures, all pass.
- Lane: test-writer. Effort: S.
- Done: 4cb876cc

### Step 12 - Detached marshaller tests
- Files: `RoslynSentinel.Tests.Server/ToolResultMarshallerDetachedTests.cs`
- Depends on: Step 7
- Effect: tests only.
- Change: move its one `InlineBudgetChars` site to the matching accessor; add the Step 12 rows.
- Done when: dispatcher runs `ToolResultMarshallerDetachedTests`, all pass.
- Lane: test-writer. Effort: S.
- Done: 16161d9e

### Step 13 - Offload filter tests
- Files: `RoslynSentinel.Tests.Server/LargeResultOffloadFilterHandlerTests.cs` (new),
  `RoslynSentinel.Tests.Server/LargeResultOffloadFilterTests.cs`
- Depends on: Step 5
- Effect: tests only.
- Change: new fixture with the Step 13 rows, calling
  `LargeResultOffloadFilter.InvokeAsync("SomeTool", () => tempRoot, null, () => ValueTask.FromResult(input), CancellationToken.None)`
  as `ErrorPolicyFilterTests.cs:95` does for its filter; `tempRoot` is a temp directory created in SetUp and deleted in
  TearDown (the handler writes the envelope under it). In the existing host fixture move the 3 `InlineBudgetChars`
  sites (`:181`, `:227`, `:251`) to `OffloadThresholdChars(Default)` and reword the `OffloadThresholdBytes` comments
  (`:29`, `:46`, `:283`). Existing parsers of the pointer that must stay green (run them, change nothing unless red):
  `ToolCallEchoFilterTests.cs:227`, `:245`, `:261` (asserts no `\u00` in the pointer text), `WirePathRelativizeFilterTests.cs:224`,
  `:297`, and the harness reader `SubAgentEvalResult.cs:186` (covered by `SubAgentOffloadedResultTests`).
- Done when: dispatcher runs both fixtures plus `ToolCallEchoFilterTests` and `WirePathRelativizeFilterTests`, all pass.
- Lane: test-writer. Effort: M.

### Step 14 - Paging tests
- Files: `RoslynSentinel.Tests.Tools.Basic/SequenceLargeResultPagingTests.cs`,
  `RoslynSentinel.Tests.Tools.Basic/EnvelopeLargeResultPagingTests.cs`, `RoslynSentinel.Tests.Tools.Basic/GetLargeResultTests.cs`
- Depends on: Steps 6, 7
- Effect: tests only.
- Change: `PageBudget = 15360 - 2560` (`:27` in the first two) becomes a `static readonly`
  `LargeResultHelper.DefaultBudgetChars - LargeResultHelper.PageSafetyMarginChars`; `GetLargeResultTests.cs:195`
  bounds the whole response by `LargeResultHelper.DefaultBudgetChars`, and that test's fixture (from `:185`, sizes
  hand-tuned against 15360 - 2560) is sized from `DefaultBudgetChars` instead of literals, asserting on the structured
  paging fields (`returned`, `nextOffset`, `hasMoreData`). The `pages > 2` fixtures in
  `SequenceLargeResultPagingTests` (200 records of about 180 chars, about 36K; 700 lines of about 53 chars, about 37K)
  are sized from the budget instead of a literal: record or line count = `3 * LargeResultHelper.DefaultBudgetChars / <per-item chars> + 10%`
  (at stock about 280 records and 950 lines), so they produce at least 3 pages at any configured default. Add the Step 14 rows; the two fixtures that set the option
  become `[NonParallelizable]`.
- Done when: dispatcher runs the three fixtures, all pass.
- Lane: test-writer. Effort: S. Pairs with: Step 15.
- Done: 7061be5e, fix 60094893 (capped count and last-page offset derived from the fixture size)

### Step 15 - ReadFile and method-source tests
- Files: `RoslynSentinel.Tests.Tools.Basic/ReadFileTests.cs`, `RoslynSentinel.Tests.Tools.Basic/ReadFileRecordsObservedTests.cs`,
  `RoslynSentinel.Tests.Tools.Basic/GetMethodSourceTests.cs`
- Depends on: Steps 6, 7
- Effect: tests only.
- Change: add `NoSolutionGateFollowsConfiguredDefault` to `ReadFileTests` beside the no-solution tests (near
  `:334-460`); move the one `InlineBudgetChars` reference in each of the other two files (`GetMethodSourceTests.cs:254`)
  to the accessor it means (a page or a gate: `OffloadThresholdChars(Default)`).
- Done when: dispatcher runs the three fixtures, all pass.
- Lane: test-writer. Effort: S. Pairs with: Step 14.
- Done: 59000bec

### Step 16a - ReadFile paging loop: new page budget, diagnosable asserts
- Files: `RoslynSentinel.Tests.Tools.Basic/ReadFilePagingLoopTests.cs`
- Depends on: Steps 6, 7 (the first page is cut by the marshaller, later pages by `GetLargeResult`)
- Effect: tests only.
- Change: `PageBudget` (`:29`) as in Step 14. Today the in-loop page-size assert and the needle assert fail before
  the observed offsets are reported; wrap the per-page asserts and the final offsets comparison in `Assert.Multiple`
  so a failure reports the observed page offsets and needle page offset. Leave `PinnedPageOffsets` and
  `PinnedNeedlePageOffset` (`:54-55`) unchanged.
- Done when: `Build` 0 errors. Expected state: red only on the pinned-offset and needle-offset asserts, with the
  observed values in the failure output.
- Lane: test-writer. Effort: S.

### Step 16b - Observe the new ReadFile page offsets (dispatcher)
- Files: none
- Depends on: Step 16a
- Effect: none.
- Change: run `ReadFilePagingLoopTests` (Tests.Tools.Basic); record from its failure output the observed page offsets
  and the needle page offset for the 300-method fixture. Any other red assert is a new failure: stop and report it.
- Done when: the observed offsets are written into the Step 16c brief.
- Lane: dispatcher. Effort: S.

### Step 16c - Re-pin the ReadFile page offsets
- Files: `RoslynSentinel.Tests.Tools.Basic/ReadFilePagingLoopTests.cs`
- Depends on: Step 16b
- Effect: tests only.
- Change: replace `PinnedPageOffsets` and `PinnedNeedlePageOffset` (`:54-55`) with the values from Step 16b; the
  comment says the first page is cut by the marshaller at the default budget and later pages fill
  `DefaultBudgetChars - PageSafetyMarginChars`, observed from this in-process run.
  `LargeResultPagingAgentTests.BuildTierRatesSource` (Tests.ModelEval) needs nothing while the fixture is unchanged.
- Done when: dispatcher runs `ReadFilePagingLoopTests`, all pass.
- Lane: test-writer. Effort: S.

### Step 17 - McpServerStatus size guard
- Files: `RoslynSentinel.Tests.Server/McpServerStatusWireSizeTests.cs` (new)
- Depends on: Step 8
- Effect: tests only.
- Change: copy the `ServerStatusTools` construction from `McpServerStatusToolListingTests` (Tests.Tools.Basic: a
  `Battery.Basic.TestEngines.PersistentWorkspaceManager` and an `ActiveToolSurface(...)`), but build the surface from
  every tool class in `ToolClassRegistry` (`BasicToolClassTypes` plus `AdvancedToolClassTypes`,
  `RoslynSentinel.Server.Advanced/ToolClassRegistry.cs`), so `toolListing: all` covers all declared tools as in
  production, with a claude-lean-sized active set (the mode the live measurement used). `settings: all` reads the static
  `StartupSettings.LastReport` (in `BuildConfiguration`): populate it in SetUp the way production does
  (`StartupSettings.Discover`, `Program.cs:16`, then the configure-all call used by the host, `ServerHttp.cs:32`) and
  restore it in TearDown; the fixture is `[NonParallelizable]`. Add a sanity assert that the serialized length is over
  `OffloadThresholdChars(Default)`, so the test cannot pass vacuously on a near-empty listing (pattern: the fixture
  sanity check at `ToolResultMarshallerOnModeTests.cs:263`). Call with `toolListing: all` and `settings: all` and
  serialize the whole returned envelope with
  `SentinelToolFactory.WireSerializerOptions` (the production wire options; the default encoder would inflate `'`,
  `<`, `>`, `&` and `+`). Construction precedent inside Tests.Server: `LoopLatchTests.cs:359` already builds
  `ServerStatusTools` with an `ActiveToolSurface`. Outcome classes: **fits** (at most `OffloadThresholdChars(Whole)`; the live
  measurement was 34,194); **over** (test red: the dispatcher adds an INBOX line with the measured size and raises it
  with the owner; the fix belongs to `proposal_mcpserverstatus_single_view_parameter.md`, not this plan).
- Done when: dispatcher runs `McpServerStatusWireSizeTests`, outcome **fits**.
- Lane: test-writer. Effort: S.

### Step 18 - Retire the old constants and stale comments (cleanup)
- Files: `RoslynSentinel.Common/LargeResultHelper.cs`, `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs` (`:141` comment)
- Depends on: Steps 9-17
- Effect: text-neutral.
- Change: `Search(mode: references, query: "InlineBudgetChars")` and `"EchoReserveChars"` (including tests) must show no
  code reference outside `LargeResultHelper.cs` (Steps 5-17 moved them); then delete both constants and reword the
  `WorkspaceBuildTestImpl.cs:141` comment that describes the old budget.
- Done when: `scripts/build.ps1 -Flavor Solution` 0 errors; `Search(mode: text, query: "InlineBudgetChars|EchoReserveChars", includeTests: true)`
  returns nothing in `.cs` files.
- Lane: implementer. Effort: S.

### Step 19 - Full verification
- Files: none
- Depends on: Step 18
- Effect: none.
- Change: `scripts/build.ps1 -Flavor Solution` 0 errors; compare warnings with `docs/known-build-warnings.*.txt`.
  `RunTest`, one at a time, 0 failures: Tests.Server (all), Tests.Tools.Basic (all), Tests.Basic (all; the
  source-scanning ratchets `TestPlacementRatchetTests` and `MessageTextRatchetTests`), Tests.Asyncify (all;
  `MigrationScanResultTests`), Tests.Harness (all, includes
  `SubAgentOffloadedResultTests` and the golden fixtures), Tests.ModelEval filtered to `EvalToolSurfaceParityTests`,
  `ConfigurationReferenceFreshnessTests`, `EnvVarDeclarationGuardTests` and `KnownArgSetsTests`.
- Done when: all of the above hold.
- Lane: dispatcher (plain calls). Effort: S.

### Step 20 - Docs
- Files: `docs/current/references/reference_tool_response_pipeline.md`,
  `docs/current/proposals/proposal_marshaller_owns_echo_and_stamps.md`,
  `docs/current/findings/finding_anonymous_and_untyped_return_shapes.md`
- Depends on: Step 19
- Effect: docs only.
- Change: pipeline reference: the budget section (body-only budget, classes, error-only rule, tolerance, setting,
  `InlineBudgetSignal`; every echo-reserve and old-constant mention, including lines 51, 154, 163, 203 and the prose
  near 227), the hints
  table gains `[InlineBudget]`, filter item 10 (threshold resolution, the "Result marshaller miss" second store,
  `OffloadPointerResult`, file `LargeResultOffloadFilter.cs`), the filter count (16 fixed filters plus optional
  `NearMissRecord` and `ErrorPolicy`; `LoopLatch` is fifth), and the Q22 limit. Proposal: a dated note that its reserve
  part is superseded (Q13). Finding: a dated note that the offload pointer is now `OffloadPointerResult`.
- Done when: `Grep` for `InlineBudgetChars|EchoReserveChars|echo reserve` in the pipeline reference returns nothing;
  links resolve.
- Lane: main session (non-C# docs). Effort: S.

### Step 21 - Promote the scan script
- Files: `scripts/Find-OffloadedResults.ps1` (new, from `<scratch root>\plan_design_inline_budget_classes\Find-OffloadedResults.ps1`)
- Depends on: none
- Effect: none on the server.
- Change: copy the scratch script; default `-Root` = scratch root (resolved by `scripts\roslynsentinel-scratch.ps1`)
  plus `ModelTestingResults`, and `-OutDir` under the scratch root, never the repo; skip `\Archived\` folders by
  default (`-IncludeArchived` keeps them) and de-duplicate by transcript `RunId`; add `-Since <date>` (transcript
  `Turns[0].StartedAt`); classify error-only failures separately from failures with `successData`; the summary's
  first line lists the parameters used.
- Done when: the script runs over the current results and reports 2,906 or fewer offloads.
- Lane: main session (non-C#). Effort: S.
- Done: 3add9ba4 (2,569 offloads after de-duplication; pre-change threshold check: 25 error-only failures at or
  under 40,960, 116 non-detached offloads at or under 19,200).

### Step 22 - Re-scan after the next eval batches (follow-up; does not block closing)
- Files: `docs/current/TODO.md` (one line at closing)
- Depends on: Steps 19, 21, plus the commit, a restart and at least one normal eval batch pinned at or after the
  behaviour-change hash.
- Effect: measurement.
- Change: `scripts/Find-OffloadedResults.ps1 -Since <commit date>`. Outcome classes: **met** (0 error-only failure
  bodies cut at or under 40,960 chars and 0 non-detached offloads at or under 19,200 chars, stock default); **miss**
  (any such row: INBOX line with run id, turn and tool).
- Done when: the TODO line is added at closing; the re-scan runs later.
- Lane: main session. Effort: S.

## Out of scope

- Thrown-exception bodies, which are mapped outside the offload filter (Q11; DRAFT
  `plan_shared_tool_execution_pipeline.md`).
- The McpServerStatus listing redesign (`proposal_mcpserverstatus_single_view_parameter.md`).
- Compile-gate diagnostic verbosity (INBOX 2026-11-26 18:40).
- Detached payload behaviour (RunTest `fullResults`, apply `changedFiles`).
- Retiring `ResultPager.Fit` (`proposal_sequence_pager.md`).
- A per-call budget hint (Q14/Q15: no user).
- A registry of offloaded results (`ideas/idea_offload_registry.md`).
- An upper bound on the configured default (a very high value may exceed a client's MCP output cap; Claude Code reads
  offloaded results natively, Q9).
- Stale literals that are not budget-tied (`TestRunResultProjectorTests.cs:236`, `BatchFailureMessageTests.cs:33`,
  `ReplaceSnippetBatchTests.cs:403`): they assert tool caps, not the budget, and stay green.

## Risks and open decisions

- Peer sessions may have the touched files dirty: check `ListAgents` and `Git(status)` before each slice.
- A raw `CallToolResult` with `IsError` and no top-level JSON `isError` falls to the `Default` threshold (accepted).
- `ClaudeLeanSchemaBudgetTests` has about 69 chars of headroom under 50,000 after `c87fb4be` (peer report,
  2026-11-26). This plan adds no tool or parameter description text; Step 9 shortens one. Any wording added to a
  description while implementing will trip that test.
