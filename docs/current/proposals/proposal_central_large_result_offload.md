# Central large-result offload and IPageable (retire ForPossiblyLargeDataAsync)

**Status:** PROPOSED 2026-10-28. Awaiting review; Slice 0 (Raw compact serialization, no char count as totalRecords) is being implemented separately in the same session and is IN PROGRESS, not done.

## Motivation

Whether a tool's large result can be paged by record depends on whether its author remembered to call
an opt-in helper. Tools that forgot fall into a generic char-window path that ignores `limit` and
reports a character count as `totalRecords`. A weak agent following the server's own hint
(`GetLargeResult(..., limit: 3)`) gets a 7.5 KB slice of JSON text and no way to ask for "the next 3
sites". That is an environment defect (the offload contract is optional and the fallback lies about
its units), not an agent error.

### Evidence: a real run

A `GetLargeResult` call with `limit: 3` on a stored `SemanticFindReplace` preview returned
`successData = { text: <7552-char window of serialized JSON>, offset, nextOffset, totalChars: 46985 }`
and `totalRecords = 46985`. 46985 is a character count, not a record count, and `limit` was ignored.

### Cause, traced

1. `SemanticFindReplace` returns an anonymous object `{ mode, rewritten, skipped, sites[] }` as
   `SuccessData` (`RoslynSentinel.Tools.Basic/SemanticFindReplaceTools.cs`, preview branch at about
   lines 147-170) and never calls `SentinelCallToolResult<T>.ForPossiblyLargeDataAsync`
   (`RoslynSentinel.Common/SentinelCallToolResult.cs:342`).
2. The catch-all `AddLargeResultOffloadFilter`
   (`RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs:455-620`) sees only the
   serialized response text (`block.Text`, line ~502), and above the threshold stores it with
   `LargeResultHelper.StoreRawJsonAsync` (`Common/LargeResultHelper.cs:80`), which tags it
   `ResultWrapperType.Raw` (line 90). It parses the whole MCP envelope into a `JsonNode` and stores it.
3. `GetLargeResult`'s Raw branch (`Tools.Basic/WorkspaceReadNavigationImpl.cs:1586-1640`) does
   `rawText = all.Data.ToString()`, caps the window at
   `worstCaseMaxSlice = (15360 - 256) / 2 = 7552`, honors only `charLimit`, and sets
   `TotalRecords = rawText.Length` (line ~1640).
4. Record-aware paging (`Skip(offset).Take(limit)` plus `ShrinkListToFit`) exists only for a handful of
   typed wrappers. The `GetLargeResult` switch has 18 `case ResultWrapperType.*` labels
   (`WorkspaceReadNavigationImpl.cs:1658-1944`) plus `default:` at 1964; Raw is handled before the
   switch. The eight list wrappers that use Skip/Take + `ShrinkListToFit` are
   `MigrationCandidateFindingList`, `ApiSurfaceEntryList`, `SolutionSymbolEntryList`,
   `ProjectFileList`, `ProjectInfoList`, `SolutionItemFileList`, `SymbolRelationshipResultList` and
   `ErrorStructuredDetailList` (precedent: commit a1c83722). The remaining wrapper types are
   single-object or map shaped and have their own bespoke branches.

### Evidence: raw size waste

Measured on a similar stored preview (122 sites):

| Representation | Chars |
| --- | --- |
| `Data.ToString()` (what the Raw window pages over) | 47037, including 244 `<` and 244 `>` escapes (about 5%) |
| relaxed encoder, indented | 44565 |
| relaxed encoder, compact | 35791 |

So the window pages about 24% more text than needed. Separately, the shared prefix
`RoslynSentinel.Tests.Tools.Basic/` repeats 122 times (about 8.7% of the payload). Wire-path
relativizing is already global (`Common/WirePathRewriter.cs`, `AddWirePathRelativizeFilter` at
`ServiceRegistrationExtensionsBasic.cs:632`); that prefix survives because it is a shared *directory*,
not an absolute root.

### Root cause: opt-in

`ForPossiblyLargeDataAsync` has 26 real call sites in 12 files. (A symbol search for callers reports
28 in 13 files; the two extra hits are `<see cref>` mentions in
`Common/SentinelCallToolResult.cs:243,389`, not calls.)

| Layer | File: calls |
| --- | --- |
| Tools.Basic | RefactoringStructuralImpl 6, WorkspaceProjectManagementImpl 4, WorkspaceReadNavigationImpl 3, RefactoringExtractionDocsImpl 2, RefactoringSignatureImpl 2, SymbolRelationshipImpl 2, WorkspaceBuildTestImpl 2 |
| Tools.Advanced | AsyncifyTools 1, IntelligenceTools 1, ScanTools 1, SubAgentEvalImpl 1, SubAgentImpl 1 |

Its own doc comment (`SentinelCallToolResult.cs:~335-341`) says it replaced hand-rolled offload blocks
that had diverged. A sibling opt-in variant, `ForPossiblyLargeDetailAsync` (`SentinelCallToolResult.cs`
~line 392, for `ResultError` detail lists), repeats the same pattern. Many callers return single
objects, not lists (Build, RunTest, Member, SubAgent, CodeInventoryReport), so "typed offload" is not
the same thing as "record paging".

Any tool that does not opt in (SemanticFindReplace, and per the filter's own comment "most tool result
types") lands on the Raw fallback. The filter comment says so explicitly
(`ServiceRegistrationExtensionsBasic.cs:~463-467`).

## Proposal

Two tiers. Decided: the two-tier shape and the migration order. Open: the mechanism for tier 2 (see
Open questions).

### Tier 1 - central offload for ALL results

The filter offloads anything over the threshold with no per-tool call. A tool author returns the
result and does nothing else. This removes the opt-in, which is the root cause above.

### Tier 2 - optional `IPageable` for list results

A minimal contract, for example `Count` plus `Slice(skip, take)` (or alternatively a declared
record-array property name). A result that implements it gets:

- record paging (`limit`/`offset` mean records),
- a real `totalRecords`,
- `ShrinkListToFit`-style page shrinking, shared instead of copied per wrapper.

Non-list results (single objects, maps, `CodeInventoryReport`, `Build`, `RunTest`) do **not** have to
implement it. They page as a char window (Raw), which must be:

- serialized compact with the relaxed encoder (about 24% less text on the measured sample), and
- never report characters as `totalRecords` (report `totalChars` only, or leave `totalRecords` null).

`IPageable` is explicitly **not** required of every operation.

### Guardrail

An architecture test fails when an `[McpServerTool]` method returns an anonymous object or an
unwrapped collection. Raw stays as the fallback for anything that slips through, with a logged
warning (the filter already logs "Large tool result" at `ServiceRegistrationExtensionsBasic.cs:~509`).
First migrated user: `SemanticFindReplace` (replace the anonymous preview object with a named result
type that implements `IPageable` over `sites`).

### Slice 0 (shipping separately, same session) - IN PROGRESS

Raw compact serialization plus stop reporting the char count as `totalRecords` for Raw, in the Raw
block of `WorkspaceReadNavigationImpl.GetLargeResult`. Test `T12b` in `GetLargeResultTests` is being
added with it. Existing Raw tests `T9`-`T12` do not assert `TotalRecords`, so they will not catch a
regression either way; `T12b` is the only test that will pin the new behavior. This slice needs
no new contract and does not depend on the rest of this proposal.

### Migration order (old beside new, per CLAUDE.md)

1. Slice 0 (above).
2. Add `IPageable`; make the filter (or `GetLargeResult`) honor it. Old paths untouched.
3. Mark `ForPossiblyLargeDataAsync` (and `ForPossiblyLargeDetailAsync`)
   `[Obsolete("Use central offload")]`.
4. Migrate the 26 callers in slices; leave `// TODO migrate to central offload` at the remaining ones.
   First user outside the existing callers: `SemanticFindReplace`.
5. Add the architecture-test guardrail (it can land earlier as a ratchet with a baseline of current
   offenders, see Open questions).
6. Remove `ForPossiblyLargeDataAsync` last, and `ResultWrapperType` only after the Raw decision below.

### Raw is not only the filter backstop

`ResultWrapperType.Raw` is also passed explicitly by four typed callers of `ForPossiblyLargeDataAsync`:

| Caller | Location |
| --- | --- |
| `SubAgentImpl.SubAgent` | `RoslynSentinel.Tools.Advanced/SubAgentImpl.cs:85` |
| `SubAgentEvalImpl.SubAgentEval` | `RoslynSentinel.Tools.Advanced/SubAgentEvalImpl.cs:74` |
| `WorkspaceBuildTestImpl.Build` | `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs:303` |
| `WorkspaceBuildTestImpl.RunTest` | `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs:368` |

These page as char windows today, with `totalRecords` supplied by the tool (for example RunTest's
`FailedCount`). Note the consequence for Slice 0: a Raw file written by one of these four carries a
meaningful tool-supplied record count in the *offload envelope*, while `GetLargeResult`'s Raw branch
today ignores it and reports the char count. Removing the enum value last therefore needs a decision
for these four (see Open questions): either they become plain central-offload users (the filter stores
them as Raw anyway, and the tool-supplied count is relayed from the envelope's `totalRecords` as the
filter already does at `ServiceRegistrationExtensionsBasic.cs:~548-553`), or they keep a Raw-like
wrapper type. Until that is decided, `ResultWrapperType.Raw` must not be deleted.

## Alternatives considered, not pursued

- **Keep the opt-in and migrate the stragglers.** Rejected as the main strategy: the helper already
  exists, was introduced to stop divergence, and still left tools (SemanticFindReplace and, per the
  filter comment, most result types) outside it. Each new tool re-opens the gap. A guardrail test
  alone does not remove the repetition.
- **Require `IPageable` of every operation.** Rejected: Build, RunTest, Member, SubAgent and
  `CodeInventoryReport` are single objects; forcing a fake list contract on them adds ceremony and
  invites wrong `totalRecords` values.
- **A separate path-dedup helper / group rows by path.** Not proposed. Wire-path relativizing is already
  global; grouping rows by path was measured at +13.6% bytes in
  `plans/plan_wire_relative_paths_backstop_filter.md` (line ~31 and open question 1 at line ~138)
  because it needs a new nested shape per list tool.
- **Shared-directory-prefix hoisting (about 8.7% on the sample).** Deferred until rows are typed
  (tier 2 gives a place to hang it); doing it on opaque Raw text would mean a text-level rewrite of
  serialized JSON.
- **Return the whole stored payload from `GetLargeResult(Raw)`.** Rejected in
  `proposal_centralized_large_result_filter.md` (about lines 154-175): `GetLargeResult` passes back
  through the same filter, so a too-big payload would be re-offloaded under a new `resultId`, an
  unterminating fetch/re-offload loop. Any tier-2 design must keep every `GetLargeResult` response
  under the threshold by construction (the existing `ShrinkListToFit` and Raw worst-case-then-verify
  loops).
- **JSON-level dominant-array paging in the filter (not ruled out).** The filter already parses the
  response into a `JsonNode` and counts arrays under `successData` for its `itemCount` hint
  (`LargeResultHelper.CountResultItems`, `LargeResultHelper.cs:~23-34`; filter use at
  `ServiceRegistrationExtensionsBasic.cs:~548-553`). It could page the largest array
  generically with no `IPageable` at all. Not evaluated: ambiguous when a result has several arrays or
  nested arrays, and `Skip/Take` over a `JsonArray` loses typed `ShrinkListToFit` behavior. Listed so
  the reviewer can choose between it and `IPageable`; it is an open design choice, not a rejected one.

## Open questions

All three are **UNVERIFIED as designs**; the facts below were read from source on 2026-10-28.

**(a) Where does record paging happen, given the filter sees serialized text?**
The filter handles `block.Text` only (`ServiceRegistrationExtensionsBasic.cs:~502`); it never sees the
typed object. Record paging needs one of: (1) keep the typed result alive until serialization (for
example a side channel on `CallToolResult`/context items set by the tool, so the filter can call
`IPageable.Slice`); (2) a declared array property name stored with the offloaded file so
`GetLargeResult` can page the JSON array; (3) the JSON-level dominant-array approach under
Alternatives. Undecided. Verified so far: the offloaded file stores the whole MCP envelope as `Data`
(`LargeResultHelper.StoreRawJsonAsync`, lines 80-100), so the array lives under `successData` in the
stored node.

**(b) What does `ResultWrapperType` do in `GetLargeResult` beyond deserialization?**
Partly verified. It selects far more than a deserializer: 18 switch cases at
`WorkspaceReadNavigationImpl.cs:1658-1944` each choose a paging/shrinking strategy, and a post-switch
totals block uses `textSearchTotal`/`textSearchReturned` (set by the `TextSearchMatchList` branch).
The Raw type is also special-cased before the switch and in `ForPossiblyLargeDataAsync`'s four
explicit users (above). I did not read every case body, so there may be further behavior (for example
per-type `statusMessage`/`listSummary` handling) that must be preserved when the enum goes away.

**(c) Does the filter's threshold match `LargeResultHelper.OffloadThresholdBytes`?**
Same constant, **different measures** (verified):

| Site | Compares | Against |
| --- | --- | --- |
| Filter, `ServiceRegistrationExtensionsBasic.cs:~504` | `text.Length` (UTF-16 chars of the full serialized response) | `OffloadThresholdBytes` (15 * 1024, `LargeResultHelper.cs:14`) |
| `StoreLargeResultAsync`, `LargeResultHelper.cs:50` | `jsonBytes.Length` (UTF-8 bytes of `data` alone, default `JsonSerializer` options, no envelope) | same constant |

So a typed result just under the threshold as bare `data` can exceed it once wrapped in the envelope
(`statusMessage`, `listSummary`, `workspaceVersion`, ...) and be re-caught by the filter as Raw. This
is inferred from reading the two comparisons, not reproduced. The central design should pick one
measure (and name the constant for it) rather than inherit both. The envelope message also labels the
char count as `bytes` (`Result is {text.Length} bytes`, line ~613).

**(d) Raw-typed typed callers.** Which of the four callers above becomes a plain central-offload user,
and which (if any) keeps a Raw-like wrapper? Needs a look at what the tool-supplied `totalRecords`
(for example RunTest `FailedCount`) is used for by agents.

**(e) Guardrail baseline.** An architecture test that fails on anonymous/unwrapped returns will fail
today for at least `SemanticFindReplace`; it needs either a ratchet baseline or the first migration to
land in the same slice. Number of current offenders not measured.

## Cost / risk

- **Touches:** `Common` (`SentinelCallToolResult`, `LargeResultHelper`, a new `IPageable`),
  `Server.Advanced` (the offload filter), `Tools.Basic` `GetLargeResult` (largest file in the path,
  2064 lines), 26 caller sites in 12 files across `Tools.Basic` and `Tools.Advanced`, and an
  architecture test project.
- **Risk - re-offload loop.** Any change to what `GetLargeResult` returns must keep every response
  under the filter's threshold by construction, or it reintroduces the loop documented in
  `docs/obsolete/blockers/blocking_error_getlargeresult_typed_branch_reoffload_loop.md` (archived).
- **Risk - stored-file compatibility.** Existing `largeresult_*.json` files (7-day retention,
  `LargeResultHelper.RetentionDays`) carry old `ResultWrapperType` values; `GetLargeResult` must keep
  reading them until they expire, so enum removal is a delayed step.
- **Risk - coverage.** Existing Raw tests `T9`-`T12` do not assert `TotalRecords`, so a green run says
  nothing about the reported-units defect. From the role boundaries: the planner of each slice must
  specify a test that pins `totalRecords` semantics (T12b is that test for Slice 0); the reviewer
  must confirm it exercises the changed path and not only the shared helper. An implementer with all
  tests green is correct from their vantage point; a missing assertion is a planner/reviewer gap.
- **Makes harder:** server-restart verification for filter changes (the running server is stale until
  restarted, see CLAUDE.md Verification); the filter becomes more than a shape-agnostic backstop and
  needs its own tests.
- **Makes easier:** new tools need no offload code; weak agents get one consistent `limit`/`totalRecords`
  meaning on every list result.

## Related

- `docs/current/proposal_centralized_large_result_filter.md` (legacy root location) - origin of the
  Raw char window and why it is a window (re-offload loop).
- `docs/current/plans/plan_wire_relative_paths_backstop_filter.md` - global path relativizing; the
  +13.6% row-grouping measurement.
- `docs/obsolete/blockers/blocking_error_getlargeresult_typed_branch_reoffload_loop.md` (archived) - the loop to
  avoid.
- Commit a1c83722 - precedent for adding record-aware paging to typed wrappers.
- Slice 0 test: `T12b` in `GetLargeResultTests` (passing).
