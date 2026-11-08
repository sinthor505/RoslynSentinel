# Typed large-result offload: IPageableResult, PagedArray and line records (retire the Raw window as a normal path)

**Status:** SUPERSEDED 2026-11-02 by [plan_wire_result_marshaller.md](../../plans/plan_wire_result_marshaller.md):
offload moves to one marshaller at the SDK `MarshalResult` seam instead of a tool-side typed helper; Slices 1-5
were never started. Was: PROPOSED 2026-10-28, REVISED 2026-10-29 after review. Slice 0 is DONE (commit 22a081bb,
test `T12b`). Slices 1-5 are not started. Decided in review: the mechanism is a tool-side typed helper,
not the filter; text output pages as line records. Open questions (a) and (d) of the first draft are
closed by those decisions; (b), (c) and (e) remain and are listed below.

## Motivation

Whether a tool's large result can be paged by record depends on whether its author remembered to call
an opt-in helper. Tools that forgot fall into a generic char-window path that ignores `limit` and
(before Slice 0) reported a character count as `totalRecords`. A weak agent following the server's own
hint (`GetLargeResult(..., limit: 3)`) gets a 7.5 KB slice of JSON text and no way to ask for "the next
3 sites". That is an environment defect (the offload contract is optional and the fallback has no
record notion), not an agent error.

### Evidence: a real run

A `GetLargeResult` call with `limit: 3` on a stored `SemanticFindReplace` preview returned
`successData = { text: <7552-char window of serialized JSON>, offset, nextOffset, totalChars: 46985 }`
and (before Slice 0) `totalRecords = 46985`. `limit` was ignored.

### Cause, traced (read from source 2026-10-28, re-read 2026-10-29)

1. `SemanticFindReplace` returns an anonymous object `{ mode, rewritten, skipped, sites[] }` as
   `SuccessData` (`RoslynSentinel.Tools.Basic/SemanticFindReplaceTools.cs`, preview branch at about
   lines 147-170) and never calls `SentinelCallToolResult<T>.ForPossiblyLargeDataAsync`
   (`RoslynSentinel.Common/SentinelCallToolResult.cs:342`).
2. The catch-all `AddLargeResultOffloadFilter`
   (`RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs:456-620`) runs after the
   tool and sees only the serialized response text (`block.Text`, line ~502) plus the tool name. Above
   the threshold it stores the whole MCP envelope with `LargeResultHelper.StoreRawJsonAsync`
   (`Common/LargeResultHelper.cs:80`), tagged `ResultWrapperType.Raw`, and replaces the response with
   a pointer `{ offloaded, resultId, sizeBytes, itemCount, isError, statusMessage, listSummary, message }`.
   `itemCount` is the envelope's `totalRecords` if present, else `CountResultItems` (sums every array
   under `successData`, recursively). It skips the `GetLargeResult` tool by name to avoid a re-offload loop.
3. `GetLargeResult`'s Raw branch (`Tools.Basic/WorkspaceReadNavigationImpl.cs:1586-1646`) pages the
   compact-serialized stored node as a char window capped at `(15360 - 256) / 2 = 7552` chars with a
   shrink-and-verify loop, honors only `charLimit`, and (since Slice 0) leaves `TotalRecords` null.
4. Record-aware paging exists only in the typed switch (`WorkspaceReadNavigationImpl.cs:1658-1944`,
   18 `case ResultWrapperType.*` labels plus `default:`). The eight list wrappers
   (`MigrationCandidateFindingList`, `ApiSurfaceEntryList`, `SolutionSymbolEntryList`,
   `ProjectFileList`, `ProjectInfoList`, `SolutionItemFileList`, `SymbolRelationshipResultList`,
   `ErrorStructuredDetailList`; precedent commit a1c83722) each do
   `Deserialize<List<T>>` -> `Skip(offset).Take(limit)` -> `ShrinkListToFit` -> serialize. The `T` exists
   only so `Skip/Take` and the shrink loop have something to operate on; the stored `data` is already
   the bare JSON array. The remaining cases are single-object or map shaped and bespoke.

### Two file shapes on disk today

| Written by | `type` | `data` holds |
| --- | --- | --- |
| `ForPossiblyLargeDataAsync`, list wrapper | e.g. `ProjectFileList` | the bare `[...]` array |
| `ForPossiblyLargeDataAsync`, Raw-typed caller (4 sites, below) | `Raw` | the bare payload object |
| filter backstop | `Raw` | the full envelope `{ isError, successData, ... }` |

### Evidence: raw size waste (fixed by Slice 0)

Measured on a stored preview (122 sites): `Data.ToString()` 47037 chars (indented, angle brackets
escaped) vs compact relaxed 35791 chars, so the window paged about 24% more text than needed. Slice 0
switched the Raw branch to `ToJsonString(SharedJsonOptions.Compact)`.

### Root cause: opt-in with no contract

`ForPossiblyLargeDataAsync` has 26 real call sites in 12 files (a symbol search reports 28 in 13; the
two extra are `<see cref>` mentions in `Common/SentinelCallToolResult.cs:243,389`).

| Layer | File: calls |
| --- | --- |
| Tools.Basic | RefactoringStructuralImpl 6, WorkspaceProjectManagementImpl 4, WorkspaceReadNavigationImpl 3, RefactoringExtractionDocsImpl 2, RefactoringSignatureImpl 2, SymbolRelationshipImpl 2, WorkspaceBuildTestImpl 2 |
| Tools.Advanced | AsyncifyTools 1, IntelligenceTools 1, ScanTools 1, SubAgentEvalImpl 1, SubAgentImpl 1 |

The opt-in is a method call with a hand-typed `ResultWrapperType` argument. Nothing in the type system
says a result is pageable, so nothing can check that a pageable result went through the helper, and
each new list wrapper needs a new enum value and a new copied `case`.

Four callers pass `ResultWrapperType.Raw` explicitly and page as char windows with a tool-supplied
`totalRecords` (for example RunTest's `FailedCount`):

| Caller | Location |
| --- | --- |
| `SubAgentImpl.SubAgent` | `RoslynSentinel.Tools.Advanced/SubAgentImpl.cs:85` |
| `SubAgentEvalImpl.SubAgentEval` | `RoslynSentinel.Tools.Advanced/SubAgentEvalImpl.cs:74` |
| `WorkspaceBuildTestImpl.Build` | `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs:303` |
| `WorkspaceBuildTestImpl.RunTest` | `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs:368` |

These are not single objects: each is a small structured header plus one large line-structured text
field. Lines are records.

## Proposal

Decided in review (2026-10-29):

1. **The tool is responsible for offload, through one typed helper.** A tool that can produce a large
   result builds a named result DTO, calls the shared helper, and returns what it returns. The helper
   runs inside the tool, where the typed object still exists, so record paging is decided at write
   time; nothing has to be discovered from serialized text.
2. **The filter stays, as backstop and detector only.** It still offloads anything over the threshold
   (so a forgotten opt-in degrades to a Raw window, never a 45 KB response), and when it catches a tool
   whose result type is pageable it logs at error level: that is a forgotten opt-in, not a legitimate
   offload.
3. **Text output is line records.** Build, RunTest, SubAgent and SubAgentEval migrate to a line-records
   DTO; `ResultWrapperType.Raw` remains only for the backstop.

### The contract

```
IPageableResult                      (Common; interface, not a DTO)
  IReadOnlyList<object> Records      the part that pages; everything else on the type is the header

PagedArray                           (new ResultWrapperType value)
  file: { type: PagedArray, header: { ...non-record properties... }, records: [ ... ] }
```

The helper (typed successor of `ForPossiblyLargeDataAsync`, constrained `where T : IPageableResult`)
serializes `T`, removes the records property from the resulting `JsonObject`, and stores header and
records separately. No path string, no reflection on text, no `T` needed at read time. `GetLargeResult`
gains one generic branch:

```
case PagedArray:
  page = records.Skip(offset).Take(limit), shrink by serialized size until it fits
  return header + page; TotalRecords = records.Count; NextOffset, WarningDetails as the list cases do today
```

Non-pageable results (Member, single-object tools) migrate from anonymous/`object` to a named DTO and
keep the Raw window; that is a separate, smaller track and not required for any slice below.

### Line records for text output

```
BuildResult : IPageableResult
  header : { succeeded, errorCount, warningCount, ... }
  records: string[] lines of output
```

A later slice adds generic `GetLargeResult` parameters `filter` (regex over each record's text; over
the serialized record for JSON records) and `tail`, applied before `Skip/Take`, with `totalRecords`
reporting the filtered count. Tool-specific grouping (MSBuild `error CS` lines, failed tests) belongs in
the tool at write time as extra header/records fields; `GetLargeResult` never learns a tool's output syntax.

### Guards against the residual opt-in

The call site can still skip the helper. Three cheap guards make that a build/test failure instead of
a silent Raw window:

1. The filter's error-level log when it catches a pageable-typed tool (above).
2. An architecture test: every `[McpServerTool]` method returning `SentinelCallToolResult<T>` with
   `T : IPageableResult` contains a call to the helper (syntax check; coarse but enough). It can land
   as a ratchet with a baseline (open question (e)).
3. The helper is the only public factory for a pageable envelope, so the by-hand route looks unusual.

### NEW-DTO justifications

```
NEW-DTO: IPageableResult (Result contract, Common)
Evaluated: ListSummary - per-file count summary, carries no records; LargeResultInfo - the offload
  pointer, not a payload; EngineResultWrapper<T> - no new uses per dto-conventions; the 8 list
  wrappers (e.g. ProjectFileList) - one concrete shape each, they are what will implement it
Wire impact: none (interface; records property keeps each type's existing name)

NEW-DTO: TextLinesResult (Result, Common)           name to confirm against dto-conventions suffix table
Evaluated: Build/RunTest/SubAgent current result types - to be read when the Slice 3 brief is
  written (unread as of 2026-10-29; the proposal only knows RunTest passes FailedCount); ReadFile's
  result - single source string, not line records
Wire impact: new tool output field(s) (records/header split for the 4 text callers)
```

`PagedArray` is an enum value and the header/records split is an extension of the existing
`ResultWrapper`; neither is a new DTO.

### Slices (old beside new, per CLAUDE.md)

0. DONE - Raw compact serialization; `TotalRecords` null for Raw; test `T12b`.
1. **Threshold measure (prerequisite).** Make the helper measure the full serialized envelope with the
   same measure the filter uses (open question (c)). Without this, a migrated tool's payload just under
   the helper's byte threshold can exceed the filter's char threshold once enveloped and be re-caught as
   Raw, silently losing record paging. Test: an envelope-inflated payload is offloaded by the helper,
   not the filter.
2. **`IPageableResult` + `PagedArray` + typed helper.** Add the interface, the enum value, the
   header/records file shape, the `GetLargeResult` branch, the filter's error-level detector. First user:
   `SemanticFindReplace` (named result type implementing the interface over `sites`). Test pins
   `totalRecords == sites.Count` and `limit: 3` returns 3 sites. Old `ForPossiblyLargeDataAsync` and
   the 18 cases untouched.
3. **Line records.** `TextLinesResult`; migrate Build, RunTest, SubAgent, SubAgentEval. Closes (d).
4. **Fold the eight list wrappers** onto `PagedArray` one at a time, deleting each `case`; mark
   `ForPossiblyLargeDataAsync` `[Obsolete("Use <typed helper>")]`; `// TODO migrate to typed offload` at
   remaining callers. Architecture test lands here or earlier as a ratchet.
5. **`GetLargeResult` `filter` and `tail`.** Separate slice so `totalRecords` semantics are pinned by
   Slice 2/3 tests first.
6. Last: remove `ForPossiblyLargeDataAsync`; shrink `ResultWrapperType` to `PagedArray`, `Raw` and the
   bespoke non-list cases, after stored files older than `RetentionDays` (7) have expired.

## Alternatives considered, not pursued

- **Filter-side central offload for ALL results, with `IPageable` honored by the filter** (first
  draft's tier 1/2). Rejected in review: the filter only has text and a tool name, so learning where
  the records are needs either a wire field on every envelope, a reflection lookup of the tool's
  return type (fails for `SentinelCallToolResult<object>`, which is exactly the SemanticFindReplace
  case), or a side channel keeping the typed object alive. The tool-side helper has the object for free.
- **Declared array path (JSONPath-like string) stored with the file.** Subsumed: with the object at
  write time the helper splits header from records itself; no path to declare, validate or walk.
- **JSON-level dominant-array paging in the filter (no declaration).** Rejected: ambiguous with
  several or nested arrays; a wrong guess silently pages the wrong thing, the same defect class as the
  original char-count `totalRecords`; `limit` would mean records for some shapes and not others.
- **Side channel keeping the typed result alive until the filter runs.** Rejected: offloaded files
  persist 7 days, so the object would be serialized anyway and read-time would need a typed
  deserialization registry, which is `ResultWrapperType` again.
- **Remove the filter once tools own offload.** Rejected: a forgotten opt-in would become a silent
  oversized response. The filter is the detector that makes the residual opt-in visible.
- **Require `IPageableResult` of every operation.** Rejected: Member and other true single objects get
  a named DTO and the Raw window; forcing a records list on them invites wrong `totalRecords`.
- **Return the whole stored payload from `GetLargeResult`.** Rejected in
  `proposal_centralized_large_result_filter.md` (about lines 154-175): the re-offload loop. Every
  `PagedArray` page keeps the existing shrink-and-verify guarantee.
- **Path-dedup / grouping rows by path.** Not proposed; wire-path relativizing is already global and
  grouping was measured at +13.6% bytes in `plans/closed/plan_wire_relative_paths_backstop_filter.md`.

## Open questions

**(a) Where does record paging happen?** CLOSED 2026-10-29: in the tool-side typed helper at write
time (header/records split), with one generic `PagedArray` read branch.

**(b) What does `ResultWrapperType` do in `GetLargeResult` beyond deserialization?** Still partly
verified. 18 cases each choose a paging/shrinking strategy; the post-switch totals block uses
`textSearchTotal`/`textSearchReturned` set by `TextSearchMatchList`. Not every bespoke case body has
been read. Slice 4 folds only the eight list cases, so the bespoke ones are out of scope until a
later decision.

**(c) Threshold measure.** Verified mismatch, now a Slice 1 prerequisite: the filter compares
`text.Length` (UTF-16 chars of the full serialized response, `ServiceRegistrationExtensionsBasic.cs:~504`)
against `OffloadThresholdBytes` (15 * 1024, `LargeResultHelper.cs:14`); `StoreLargeResultAsync`
compares `jsonBytes.Length` (UTF-8 bytes of bare `data`, default serializer options, no envelope,
`LargeResultHelper.cs:50`) against the same constant. Pick one measure, name the constant for it, and
make the helper measure the envelope. The pointer message also labels chars as `bytes` (line ~613).

**(d) The four Raw-typed callers.** CLOSED 2026-10-29: they migrate to line records (Slice 3).
`ResultWrapperType.Raw` stays for the backstop only.

**(e) Guardrail baseline.** The architecture test will fail today for at least `SemanticFindReplace`;
land it as a ratchet with a baseline of current offenders, or after Slice 2. Number of current
offenders (anonymous or `object` returns from `[McpServerTool]` methods) not measured.

**(f) Build/RunTest/SubAgent result shapes.** Unread. The Slice 3 brief needs one read of each to see
whether they already carry structured error lists or only a text blob, and to pick the header fields.

## Cost / risk

- **Touches:** `Common` (`SentinelCallToolResult`, `LargeResultHelper`, `ResultWrapper`, new
  `IPageableResult` and `TextLinesResult`), `Server.Advanced` (the filter's detector log),
  `Tools.Basic` `GetLargeResult` (largest file in the path, 2066 lines), 26 caller sites in 12 files,
  `SemanticFindReplace`, and an architecture test project.
- **Risk - re-offload loop.** Every `PagedArray` page must stay under the filter's threshold by
  construction (shrink-and-verify, as the list cases and Raw do today); see
  `docs/current/blockers/closed/blocking_error_getlargeresult_typed_branch_reoffload_loop.md`.
- **Risk - threshold mismatch (c)** silently downgrades a migrated tool to Raw; hence Slice 1 first.
- **Risk - stored-file compatibility.** `largeresult_*.json` files carry old `ResultWrapperType`
  values for up to 7 days; the old cases stay readable until then, so enum shrinking is Slice 6.
- **Risk - coverage.** Raw tests `T9`-`T12` do not assert `TotalRecords`; `T12b` pins Raw. Each slice's
  planner must specify a test that pins `totalRecords` semantics for the changed path, and the reviewer
  must confirm it exercises that path, not only the shared helper.
- **Makes harder:** filter changes need a server restart to verify (CLAUDE.md Verification); the
  filter gains a detector branch that needs its own test.
- **Makes easier:** a new list tool implements one interface and gets record paging, a real
  `totalRecords`, shrink-to-fit and (Slice 5) filter/tail with no `GetLargeResult` change; text output
  becomes "give me the error lines" in one call for a weak agent.

## Related

- `docs/current/proposals/closed/proposal_centralized_large_result_filter.md` (legacy root location) - origin of the
  Raw char window and the re-offload loop.
- `docs/current/plans/closed/plan_wire_relative_paths_backstop_filter.md` - global path relativizing.
- `docs/current/blockers/closed/blocking_error_getlargeresult_typed_branch_reoffload_loop.md` (archived).
- Commit a1c83722 - precedent for record-aware paging in typed wrappers.
- Commit 22a081bb - Slice 0 and the first draft of this proposal.
- `.claude/skills/dto-conventions/SKILL.md` - NEW-DTO format used above.
