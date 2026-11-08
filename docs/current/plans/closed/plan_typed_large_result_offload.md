# Plan: typed large-result offload - IPageableResult, PagedArray read branch, first users, list-case fold

**Status:** SUPERSEDED 2026-11-02 by [plan_wire_result_marshaller.md](../plan_wire_result_marshaller.md) (never started;
kept for its source re-verification). Was: DRAFT 2026-11-02. Built from a source re-verification of
[proposal_central_large_result_offload.md](../../proposals/closed/proposal_central_large_result_offload.md); decisions D1-D4
below deviate from the proposal and need confirmation before Step 1 is dispatched.

## Problem

See the proposal. In short: record paging in `GetLargeResult` exists only for tools that opt in through
`SentinelCallToolResult<T>.ForPossiblyLargeDataAsync` with a hand-typed `ResultWrapperType`; everything else
falls to the backstop filter's Raw char window, which ignores `limit` and has no `totalRecords`. Reproduced
again 2026-11-02: a `Git(diff)` of two `.md` files (44 KB) came back as a filter pointer with `itemCount: null`.

### Proposal claims corrected by the 2026-11-02 re-verification

Source at HEAD 617f9695, read through the MCP tools.

1. **Ten list cases, not eight.** `GetLargeResult`'s switch (`Tools.Basic/WorkspaceReadNavigationImpl.cs:1658`,
   `default:` at 1966) has eight `Deserialize<List<T>>` -> `Skip/Take` -> `ShrinkListToFit` cases
   (`MigrationCandidateFindingList` 1660, `ApiSurfaceEntryList` 1679, `SolutionSymbolEntryList` 1695,
   `CodeInventoryReport` 1711, `BreakingChangeList` 1787, `ProjectFileList` 1855, `ProjectInfoList` 1871,
   `SolutionItemFileList` 1887) and two with the same shape over a raw `JsonArray`
   (`SymbolRelationshipResultList` 1915, `ErrorStructuredDetailList` 1946). The proposal's list leaves out
   `CodeInventoryReport` and `BreakingChangeList`. `TextSearchMatchList` (1803) and
   `BroadenedSymbolRelationshipResults` (1933) are bespoke; six cases are single-object.
2. **The four Raw callers are not "header + one line-structured text blob".** `BuildResult` and
   `TestRunResult` are already structured and projected (`Common/BuildResultProjector.cs:15`,
   `Engines.Basic/TestRunResultProjector.cs:10`; tails stripped on clean runs, `WorkspaceBuildTestImpl.cs:129,139`),
   per the resolved `finding_build_offload_hides_error_count.md`. Their big lists are stored separately and
   uncapped as **Raw** by `StoreFullDiagnosticsAsync` (`WorkspaceBuildTestImpl.cs:213`) and
   `StoreFullTestResultAsync` (`:230`), so `fullDiagnosticsResultId`/`fullResultsId` page as char windows.
   `SubAgentResult.Output` is free text; `SubAgentEvalResult` is small and fixed-shape. Line records fit none of them.
3. **A second reader exists.** `SubAgentEvalResult.ResolveOffloadedResult` (`Common/SubAgentEvalResult.cs:~161-225`)
   follows both pointer shapes and requires the stored `Data` to be a JSON object (pinned by
   `Tests.SubAgent/SubAgentOffloadedResultTests.cs:35,52`). The proposal's `{ header, records }` file shape
   would break it.
4. **The records property name has no source.** STJ does not serialize an explicit interface member, and
   SemanticFindReplace's wire name is `sites`, so "remove the records property from the JsonObject" needs the
   type to declare the name.
5. **Slice 1 cannot be exact.** The filter (registered 8th of 11, `ServiceRegistrationExtensionsBasic.cs:51-61`)
   measures `text.Length` after `WirePathRelativize` (shrinks paths, inserts `solutionRoot`) and before
   `ToolCallEcho` (adds `toolCall`, up to ~2.3 KB). The helper runs inside the tool, so it can only measure
   its own envelope conservatively. The helper today measures UTF-8 bytes of the bare `data` with default STJ
   options (PascalCase, nulls, HTML escaping; `LargeResultHelper.cs:49`), unlike both the stored form
   (`SharedJsonOptions.Default`) and the wire.
6. **More writers than the proposal lists.** Six direct `StoreLargeResultAsync` callers bypass the helper
   (`AsyncifyTools.cs:278`, `ScanTools.cs:1084,1118`, `SentinelCallToolResult.cs:408`,
   `WorkspaceFileEditImpl.cs:410`, `WorkspaceReadNavigationImpl.cs:364`), and three `StoreRawJsonAsync` callers
   besides the filter (`WorkspaceBuildTestImpl.cs:213,230`, `ValidateAndApplyHelper.cs:206`).
7. **SemanticFindReplace has seven anonymous shapes**, not one (`SemanticFindReplaceTools.cs:160-249`, three
   preview and four apply branches); the element type is already the named record `SemanticReplaceSite`
   (`Engines.Basic/SemanticReplaceTypes.cs:35`).
8. **Deleting a list `case` before retention expiry breaks stored files** (Slice 4 vs Slice 6 in the proposal).
   Folding the label onto the generic branch does not.

Open question (e), now measured: 137 `[McpServerTool]` methods in Tools.*, 87 returning
`SentinelCallToolResult<object>` (74 distinct names), about 11-12 tools with anonymous `SuccessData`.

## Decision

The proposal's core stands: a tool-side typed helper over `IPageableResult`, one generic `PagedArray` read
branch, and the filter kept as backstop plus forgotten-opt-in detector. Deviations (confirm before dispatch):

- **D1 - file shape.** Keep `ResultWrapper.Data` as the full serialized object (or bare array) and add
  `ResultWrapper.RecordsProperty` (`string?`). `PagedArray` with `RecordsProperty` set pages
  `Data[RecordsProperty]` and returns the other properties as the header; `PagedArray` with
  `RecordsProperty == null` and an array `Data` pages the array. No `header`/`records` split on disk, so
  `SubAgentEvalResult.ResolveOffloadedResult` and old files keep working. The proposal rejected a declared
  path for the *filter*; this name is a single top-level property declared by the type at write time.
- **D2 - interface.** `IPageableResult { IReadOnlyList<object?> Records { get; } string RecordsJsonName { get; } }`,
  implemented explicitly so STJ ignores both. The helper checks that the serialized object has
  `RecordsJsonName` as an array of `Records.Count` elements and fails loudly (structured error, plus
  `Debug.Fail`) if not. `totalRecords` is always `Records.Count`; callers no longer pass it.
- **D3 - drop `TextLinesResult` and the line-records slice.** Replace it with Step 6: the Build/RunTest
  full-detail stores become `PagedArray` over their existing structured lists. `SubAgent` and `SubAgentEval`
  stay Raw (free text and a small fixed shape). No new text DTO.
- **D4 - guard.** Replace the "helper call inside the tool method" syntax test (it cannot see through the
  Tools -> `*Impl` split) with (a) a reflection-built set of pageable tool names that the filter's detector logs
  against at error level, and (b) a ratchet on `[McpServerTool]` methods returning
  `SentinelCallToolResult<object>` (baseline 87), since an untyped tool can never be detected.

## Execution rules

- Every step: dispatch per the `dispatch-implementer` skill (load it first); main session reviews and commits.
  `Build` 0 errors; run `GetLargeResultTests`, `LargeResultOffloadFilterTests` and the step's touched fixtures,
  one `RunTest` at a time; baseline is 0 failures.
- A step touching the filter or `GetLargeResult` ends with `Build(level: fullBuild)`, server restart and one
  live call before commit.
- Each step pins `totalRecords` for the path it changes with a test that goes through that path, not only the
  shared helper.
- Old beside new: nothing in Steps 1-8 deletes a `ResultWrapperType` value or a stored-file reader.
- Commits carry `Plan: plan_typed_large_result_offload`; the last carries `Closes:` for this plan and the proposal.
- Leave `// TODO` (no separate approval) for incidental finds in touched files: the `GetLargeResult` catch
  returning `ex.Message` (`WorkspaceReadNavigationImpl.cs:2034-2040`); the impl's `charLimit` after
  `CancellationToken` (`:1497`); stale `docs/current/blockers/...reoffload_loop.md` paths in comments
  (`ServiceRegistrationExtensionsBasic.cs:487`, `WorkspaceReadNavigationImpl.cs:2049`); the filter dropping
  other text blocks when it offloads one; RunTest's `!RunCompleted` branch returning without the helper.

## Steps

### Step 1 - One threshold measure (proposal Slice 1)
- Files: `RoslynSentinel.Common/LargeResultHelper.cs`, `RoslynSentinel.Common/SentinelCallToolResult.cs`,
  `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`; tests in `Tests.Server`.
- Change: first verify which serializer options the SDK uses for the wire text (unverified; inferred from live
  output to omit nulls). Add `OffloadThresholdChars` (15360) beside `OffloadThresholdBytes`, which gets
  `[Obsolete("Use OffloadThresholdChars")]`. `ForPossiblyLargeDataAsync` builds the inline envelope it would
  return, serializes it with the wire options and offloads when `length + solutionRoot.Length + 32` exceeds
  the threshold (conservative: the helper may offload slightly earlier than the filter, never later).
  `StoreLargeResultAsync`'s own check stays for its direct callers with a `// TODO` naming them. Filter
  message says "chars", not "bytes" (`:613`); the `sizeBytes` field name stays for wire compatibility.
- Done when: a helper unit test with bare `data` under the threshold but an envelope (with `listSummary`) over
  it is offloaded by the helper (`largeResult` pointer), and a pipeline test shows the filter does not re-catch
  a helper-offloaded response.

### Step 2 - Contract and storage (Common only)
- Files: new `RoslynSentinel.Common/IPageableResult.cs`; `LargeResultHelper.cs`; `SentinelCallToolResult.cs`.
- Change: `IPageableResult` per D2; `ResultWrapperType.PagedArray`; `ResultWrapper.RecordsProperty`;
  `LargeResultHelper.StorePageableAsync` (object and list overloads) writing per D1; envelope helper
  `SentinelCallToolResult<T>.ForPageableResultAsync<T>(...) where T : IPageableResult` and
  `ForPageableListAsync<TItem>(IReadOnlyList<TItem>, ...)`, both measuring as Step 1 and setting
  `TotalRecords = Records.Count`. NEW-DTO justification for `IPageableResult` per `dto-conventions` (the
  proposal's block can be reused). No callers yet.
- Done when: Build 0 errors; unit tests pin the stored file shape for both overloads and the D2 mismatch check.

### Step 3 - Generic `PagedArray` branch in `GetLargeResult`
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`; `Tests.Tools.Basic/GetLargeResultTests.cs`.
- Change: one `case ResultWrapperType.PagedArray` per D1. Records are `List<JsonNode?>`; generalize
  `ShrinkListToFit` so the measured text includes the header (today it measures the page alone). Return the
  header object with `RecordsJsonName` set to the page (or the bare page for array data); `TotalRecords`,
  `NextOffset`, `WarningDetails` as the list cases do. The ten list cases and Raw are untouched.
- Done when: tests over hand-written `PagedArray` files (object and array) pin `limit: 3` -> 3 records,
  `totalRecords`, `nextOffset`, header fields present, a shrunk page with its warning, and an old-type file
  read unchanged. Server restart plus a live `GetLargeResult` on a hand-written file.

### Step 4 - SemanticFindReplace as first user
- Files: `RoslynSentinel.Tools.Basic/SemanticFindReplaceTools.cs`; new DTO file in `Tools.Basic` (or `Common`
  if a test needs it); SemanticFindReplace test files.
- Change: named `SemanticFindReplaceResult : IPageableResult` (NEW-DTO justification; reuse
  `SemanticReplaceSite` as element) with lowercase `[JsonPropertyName]` keys and nullable
  `rewritten/skipped/filesChanged/changeId/message` omitted when null, covering all seven branches; retype the
  tool method, its 13 returns and `InvalidArgument`; return via `ForPageableResultAsync`. Wire keys and site
  casing unchanged. Sequence before `proposal_semantic_find_replace_construction_initializers_and_skip_echo.md`
  (PROPOSED, edits the same response), which then edits the named DTO.
- Done when: existing SemanticFindReplace tests pass unchanged; a new test with a fixture over the threshold
  pins the helper pointer (not the filter's), `totalRecords == sites.Count`, and `GetLargeResult(limit: 3)`
  returning 3 sites plus the `mode` header. Live re-run of the proposal's 122-site preview after restart.

### Step 5 - Detector and ratchet (D4)
- Files: `ServiceRegistrationExtensionsBasic.cs` (detector); `Tests.Server` (new ratchet fixture, baseline file
  under `docs/`, modelled on `ToolHintRatchetTests`).
- Change: at startup build the set of tool names whose method returns `SentinelCallToolResult<T>` with
  `T : IPageableResult`; when the filter offloads one, log at error level as a forgotten opt-in. Ratchet: count
  `[McpServerTool]` methods returning `SentinelCallToolResult<object>`; fails on growth and on a stale baseline.
- Done when: a test shows the set contains `SemanticFindReplace` after Step 4; ratchet passes at 86 (87 minus
  SemanticFindReplace).

### Step 6 - Build/RunTest full-detail stores to `PagedArray` (replaces proposal Slice 3)
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`; `BuildFullDiagnostics` and `TestRunResult`
  types; `BuildToolResultShapeTests.cs:130-134`, `RunTestTests.cs:369-373`; descriptions at
  `Common/ToolParams.cs:11-15`, `WorkspaceBuildTestTools.cs:63`, `WorkspaceTools.cs:517`.
- Change: the two existing types implement `IPageableResult` (reuse-first, no new DTO); records are the
  diagnostics list and `Results`. `StoreFullDiagnosticsAsync`/`StoreFullTestResultAsync` switch from
  `StoreRawJsonAsync` to `StorePageableAsync`. The inline Build/RunTest offload stays Raw (projected, rarely
  over the threshold) with a `// TODO` at `:302,367`. First read `BuildFullDiagnostics`: if errors and
  warnings are two lists, page errors and say so in the description (one records list per D2).
- Done when: the two tests page `fullDiagnosticsResultId`/`fullResultsId` by records with
  `totalRecords` equal to the stored list count; descriptions say "records".

### Step 7 - Fold the ten list cases onto the generic branch
- Files: `WorkspaceReadNavigationImpl.cs`, then each producer (one per sub-step, one commit each).
- Change: first stack the ten old case labels onto the generic array path (stored files keep reading; their
  existing tests T3/T4/T7/T8 must pass unchanged). Then move each producer to `ForPageableListAsync` /
  `StorePageableAsync`, including the direct `StoreLargeResultAsync` list callers (`AsyncifyTools.cs:278`,
  `ScanTools.cs:1084,1118`, `SentinelCallToolResult.cs:408`). Mark each list enum value
  `[Obsolete("Use PagedArray")]` once it has no writer. Inline wire shape is unchanged (still a bare array).
- Done when: no production writer uses a list `ResultWrapperType`; all `GetLargeResultTests` pass.

### Step 8 - `GetLargeResult` `filter` and `tail` (proposal Slice 5)
- Files: `WorkspaceReadNavigationTools.cs`, `WorkspaceReadNavigationImpl.cs`, tests.
- Change: `filter` (regex over a string record, or over the compact-serialized record) and `tail`, applied before
  `Skip/Take`, `PagedArray` only; `totalRecords` reports the filtered count. Rejected with a structured error on
  other types.
- Done when: tests pin filtered `totalRecords` and `tail` paging on a Step 4 and a Step 6 store.

### Step 9 - Remove the folded enum values (no earlier than 7 days after Step 7 ships)
- Files: `LargeResultHelper.cs`, `WorkspaceReadNavigationImpl.cs`.
- Change: delete the ten obsolete list values and their stacked labels. Bespoke, single-object and `Raw` stay.
- Done when: Build 0 errors, no CS0618 left from this plan; plan and proposal closed.

## Out of scope

- Named DTOs for non-pageable tools (Member, ReadFile and the other ~11 anonymous-payload tools) and a
  non-pageable successor to `ForPossiblyLargeDataAsync`; until that exists the method is not `[Obsolete]`.
  Tracked by `finding_anonymous_and_untyped_return_shapes.md`.
- `TextSearchMatchList`, `BroadenedSymbolRelationshipResults` and the single-object cases.
- The ~2.3 KB the echo filter adds after the offload decision (the wire can exceed 15360 by that much).
- Unifying the two pointer shapes (typed `largeResult` vs the filter's top-level `offloaded`).

## Risks and open decisions

- **D1-D4 unconfirmed** (above). D1 is the largest deviation; the alternative is the proposal's split shape,
  which also requires updating `SubAgentEvalResult.ResolveOffloadedResult` and its tests.
- **Wire serializer options unverified** (Step 1 checks); the Step 1 margin depends on them.
- **Re-offload loop:** the generic branch must shrink including the header; a header over the budget on its
  own needs a structured error rather than a one-record page that is still too big (decide in Step 3).
- **Covariance:** `IReadOnlyList<object?> Records => Sites` relies on `List<SemanticReplaceSite>` being a
  reference-type list; a value-type element list needs a projection. Uncompiled assumption.
- **Step 6 wire change:** paging full diagnostics by records changes what agents get from
  `fullDiagnosticsResultId`; agent docs and the `fullResultsId` -> `resultId` alias
  (`Server.Advanced/ToolArgumentValidator.cs:667`) must be rechecked.
