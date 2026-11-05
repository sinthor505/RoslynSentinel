# Sequence pager: every pageable result is one ordered sequence of units that each fit the size cap

**Status:** PROPOSED 2026-11-04. Design agreed in discussion; nothing built; slice 1 (read-only explorer pass) done 2026-11-04, its findings are in Open questions, and the challenges it raised (9-12) are decided.

## Motivation

The current pager (`ResultPager.Fit`, `RoslynSentinel.Common/ResultPager.cs`) is shape-agnostic: it cuts any array or
string under `successData` at any depth, records each cut in `largeResult.omitted`, and the model pages one
`omitted[].path` at a time with `GetLargeResult(path, offset, limit)`. That generality costs:

1. **Starvation with two large lists.** `CutToFit` (`ResultPager.cs:384-446`) keeps the longest prefix of the chosen
   node that fits while every other node stays whole, never fewer than one element; the Phase 1 loop in `Fit` then
   picks the next largest. Worked example, derived from reading the code (no test covers it): `callers` 300,
   `implementations` 250, room for about 100 entries. Callers is cut first, implementations alone are still over
   budget, so callers keeps 1; implementations is then cut to about 99. The model sees 1/300 callers, 99/250
   implementations.
2. **One declared records path.** `ReflectHints` (`ResultPager.cs:72`) does `recordsPath ??= path`, so a second
   `[ResultRecords]` property is silently ignored.
3. **Wire complexity for the model.** Every cut is an `omitted` entry with `path`, `returned`, `total`, `unit`,
   `inferred`, and offsets are per path. Live example, `ReadFile` on `ResultPager.cs` (2026-11-03):

   ```json
   "largeResult": {
     "resultId": "e4f3cad5...", "totalRecords": 553,
     "message": "INCOMPLETE: this result was cut to fit (successData.source 224/553 lines) ... Call GetLargeResult(..., path: \"successData.source\") ...",
     "omitted": [ { "path": "successData.source", "returned": 224, "total": 553, "unit": "lines", "inferred": true } ]
   }
   ```
4. **Attributes cannot mark most results.** `[ResultRecords]` is read off the runtime type of `SuccessData`; anonymous
   objects (`Search` callers / implementations / references) and bare lists (`ListAll`, `LocateSymbol`,
   `QuerySymbolRelationships`) cannot carry it
   ([proposal_search_relationship_result_dtos.md](./proposal_search_relationship_result_dtos.md)).
5. **Per-tool fixes do not scale.** Reshaping each multi-list result (tagged lists, nested DTOs) repeats the same
   decision at every tool.

## Proposal

### Decided

1. **One record sequence per result**, chosen in this order:
   - the lists named in the envelope's `Paging` hints, if set;
   - else every array directly under `successData` (a bare-array `successData` is a sequence of one list);
   - else the lines of the result's one large string (`ReadFile`, `GetMethodSource`).
2. **Order:** `Interleaved` (round-robin, the default) or `Linear` (first list fills the page before the next).
   `Merged(key)` (k-way merge on a shared sort key such as `filePath`, `line`) is deferred.
3. **Page building.** Walk the sequence from `offset`; add units until `limit` is reached or the next unit would
   exceed the size cap (each unit's JSON measured once and summed, no binary search). Everything outside the sequence
   is returned unchanged; each list holds only its share of the page. The first response is the page at offset 0. The
   full result is still stored under `resultId`, so later pages never re-run the tool.
4. **Unit rule: every unit fits under the cap.**
   - Text pages by lines.
   - A line longer than the cap is split into cap-sized chunks, each counted as a unit; the message says the line
     continues.
   - A record must fit. A tool whose record can carry unbounded text changes its shape: clip the text to a window
     with a pointer to the full read, or replace the content with a pointer to the call that pages it.
   - Parts outside the sequence (summaries, signatures) stay bounded.
   - Nested lists are flattened to a top-level sequence, or their inner lists are bounded.
   - A violation at runtime returns the oversize unit alone on its page (paging still advances) and writes a contract
     violation (tool name, path) to the server log. Nothing is cut inside a unit; no `omitted` entry is written.
5. **Wire.**
   - Paging fields live in `largeResult`: `totalRecords` (exists), new `returned` and `nextOffset`. NOT the envelope's
     `TotalRecords` / `HasMoreData` / `NextOffset`: those describe the tool's own result (a full count at about 20
     sites, `ReadFile` re-query by `startLine`, `ListAll`'s cap; Open question 11), not a page of the stored result,
     and reusing them would leave the model unable to tell "re-run the tool" from "call `GetLargeResult`".
   - The message prints the full next call verbatim. For a line sequence it also gives the file line range when the
     tool knows its start line.
   - `GetLargeResult(resultId, offset, limit)`: `offset` always counts units of the sequence for Envelope results (the
     Raw and legacy stores page differently: Open question 10). `path` keeps working for existing readers until they
     are moved.
6. **Declaration on the envelope, not an attribute.**

   ```csharp
   public sealed record PagingHints(IReadOnlyList<string> RecordsPaths, PagingOrder Order = PagingOrder.Interleaved);
   public enum PagingOrder { Interleaved, Linear }

   // on SentinelCallToolResult<TSuccess, TError>
   [JsonIgnore]   // server-side only, like Status/Code/Message
   public PagingHints? Paging { get; init; }
   ```
   ```csharp
   var data = new { callers, implementations, summary };
   return new SentinelCallToolResult<object>
   {
       IsError = false,
       SuccessData = data,
       Paging = new([nameof(data.callers), nameof(data.implementations)]),
   };
   ```
   - `nameof` works on an anonymous-type local, so names stay compiler-checked.
   - Precedence: envelope `Paging` > `[ResultRecords]` on the type > the default sequence.
   - `ResolveHints` returns a `PagingHints` instead of today's tuple, so the pager has one input type.
   - A path that does not resolve to an array in the serialized result is ignored and logged (server log, not a
     model-visible Finding); unit tests at construction sites catch typos.
7. **`IPageableResult` is not needed.** Never use an interface as the declared type of `SuccessData`
   (`SentinelCallToolResult.cs:246`, `TSuccess? SuccessData`): System.Text.Json writes runtime types only for
   `object`, so an empty marker interface would serialize `"successData": {}`. A marker for a ratchet test stays
   possible later.
8. **`Search` `all` keeps `{callers, implementations, summary}`.** No DTO reshaping is needed for paging; named
   `*SearchResult` records become an identification choice only.
9. **`[NeverInline]` means "kept out of the response, stored only, with a pointer in the result"** (decided
   2026-11-04, Open questions 5 and 9). It applies to dictionaries of large strings (`ChangedContent`,
   `ContentPreviews`) and to a `Diff` that sits beside lists. A diff that is the tool's main output (the `Rename`
   diff) is the result's one large string and pages by lines.
10. **One page cursor per response** (decided 2026-11-04, Open question 12): `largeResult` carries it on a first
    response, the page record (`EnvelopePageResult`) on a `GetLargeResult` page. `GetLargeResult` stops writing the
    duplicate envelope `NextOffset` / `HasMoreData` on pages.

### Examples

`GetMethodSource` on a 2000-line, 40 KB method (counts are estimates: about 20 bytes per line, about 700 lines under
a 15 KB cap):

```json
"successData": { "signature": "...", "source": "<method lines 1-700>" },
"largeResult": {
  "resultId": "a1b2...", "totalRecords": 2000, "returned": 700, "nextOffset": 700,
  "message": "INCOMPLETE: lines 1-700 of 2000 returned (file lines 384-1083). Call GetLargeResult(reason: \"...\", resultId: \"a1b2...\", offset: 700) for the next page."
}
```

`Search` `all`, 300 callers and 250 implementations, interleaved:

```json
"successData": { "callers": [ /* 0..49 */ ], "implementations": [ /* 0..49 */ ], "summary": { ... } },
"largeResult": {
  "resultId": "e4f3...", "totalRecords": 550, "returned": 100, "nextOffset": 100,
  "message": "INCOMPLETE: 100 of 550 records returned. Call GetLargeResult(reason: \"...\", resultId: \"e4f3...\", offset: 100) for the next page."
}
```

`GetLargeResult(resultId, offset: 100)` returns the same shape with callers 50..99 and implementations 50..99, then
`nextOffset: 200`.

### Effect of the default sequence (input to Open question 2)

Output changes only when the result is over budget, two or more arrays sit directly under `successData`, and the
smaller list is larger than its round-robin share of what fits. Otherwise it is byte-identical. Room for 100 entries,
entries of equal size assumed:

| Lists | Today (largest cut first) | Interleaved | Changed? |
| --- | --- | --- | --- |
| results 300, warnings 4 | 96 / 4 | 96 / 4 | no |
| results 300, skippedFiles 40 | 60 / 40 | 60 / 40 | no |
| results 300, candidates 70 | 30 / 70 | 50 / 50 | yes |
| callers 300, implementations 250 | 1 / 99 | 50 / 50 | yes (fixes the starvation) |

### Slices

Migration-first throughout: new code beside the old, old marked `[Obsolete]`, callers moved, old removed last.

1. **Read-only explorer pass** (DONE 2026-11-04; findings in Open questions): the current paging path end to end,
   `GetLargeResult` and `EnvelopePageResult`, readers of the current wire contract, tool-level paging users,
   `[ResultRecords]` / `[NeverInline]` uses, static measurement of multi-list / bare-list / nested-list results,
   unit-rule candidates.
2. **Runtime probe** (temporary, in the marshaller): log every result that would page differently under the default
   sequence and every unit-rule violation; run the full test suite and the LargeResultPaging model-eval smoke.
3. **Golden tests:** two large lists (red today), interleaved, linear, small list survives, bare-array `successData`,
   string lines, overlong line chunks, oversize record returned alone and logged, page continuation from offset.
4. **Sequence page builder** in `Common`, beside `Fit`.
5. **Marshaller:** first page from the sequence builder; `largeResult.returned` / `nextOffset`; message.
6. **`GetLargeResult`:** offset-only paging over the stored sequence; `path` documented as the old way. Includes
   `PageEnvelope`'s own refit of an over-budget page (Open question 13).
6a. **Raw and legacy stores** (Open question 10): move the Raw writers to Envelope results; remove the legacy typed
   branches once no writer is left.
7. **`PagingHints` on the envelope** plus the `Search` construction sites (`SymbolRelationshipImpl.cs:290`, `:308`,
   `:343`, per the earlier proposal).
8. **Default sequence switched on**, after the slice 2 measurement.
9. **Shape fixes** for each unit-rule violation from slice 2 (or a `// TODO` at the site).
10. **Retire** `Fit`'s cutting logic, `omitted` and `GetLargeResult(path:)` once their readers have moved.

Later, not scheduled: `Merged(key)` order; an `IPageableResult` marker for a ratchet test; named `Search` records.

## Alternatives considered, not pursued

- **Multi-path `[ResultRecords]` on top of `Fit`** (option 2 of the earlier proposal). Makes `inferred` accurate but
  `Fit` would still pick the largest declared list first, so the starvation remains.
- **A group layered on `Fit`:** a `group` field on each `omitted` entry or a separate `groups` block, addressed by
  `path: "successData"` or a dedicated `group` parameter. Works, but adds a second addressing scheme beside per-list
  paths, and passing a group offset with a list path silently skips records. Replaced by the single sequence.
- **Class-level attribute** (`ResultRecordsSet`, then `ResultPagingOptions`). Per type only, and cannot mark anonymous
  objects or bare lists. Replaced by envelope `PagingHints`.
- **`MultipleResultSets` boolean on the envelope.** Duplicates what `RecordsPaths` says and allows contradictory
  states (true with one path, false with two, true with none). `Paging is not null` is already the opt-in.
- **`SentinelCallToolResult<IPageableResult>` for every tool.** System.Text.Json serializes declared interface members
  only (Decided 7).
- **Object-level `IPageableResult.Page(offset, limit)`** on C# objects. A count-based second paging system beside the
  character budget; `GetLargeResult` pages stored JSON; does not work for anonymous objects.
- **One tagged list for `all`** (`ReferenceHit` with a relation tag). Wire break and a wide record; unnecessary once
  the pager interleaves. A tag named `kind` would also collide with `ImplementationInfo.Kind`.
- **Polymorphic list** (`[JsonPolymorphic]`). Avoided by dto-conventions section 5; same discriminator collision.
- **Nested shapes:** a list of lists (meaning by position only); named groups (records at `groups[i].items`, which a
  declaration cannot name); grouped by file (page counts become files, not hits).
- **Falling back to `Fit` for a unit larger than the cap.** Needs a second-level cursor or `omitted`; replaced by the
  unit rule.
- **`offset` as a file line number.** Its meaning would differ per tool; units plus a reported file range instead.

## Open questions

Answered by slice 1 (read-only explorer pass, 2026-11-04; file:line from its report, read in the code unless marked):

1. **Do envelope hints reach the pager?** Yes. `ToolResultMarshaller.MarshalAsync` (`ToolResultMarshaller.cs:69`)
   gets the awaited tool result object through `SentinelToolFactory`'s `MarshalResult` hook
   (`SentinelToolFactory.cs:111`, `:159-168`). The 11 call-tool filters (`ServiceRegistrationExtensionsBasic.cs:57-70`)
   wrap `next(...)` and see only the finished `CallToolResult` (one filter read; the other 10 follow the same shape,
   not read individually). So a `[JsonIgnore]` `Paging` survives. Caveats: `string`, `CallToolResult` and
   `ContentBlock` results pass through unchanged and can never carry `Paging`; the pager runs only in marshaller mode
   On and only above the budget (`ToolResultMarshaller.cs:84-91`). Budget: 15360 minus a 2560 echo reserve = 12800
   chars (`LargeResultHelper.cs:14`, `:28`; `ToolResultMarshaller.cs:85`).
2. **Parts outside the sequence.** `ListSummary.ByFile` is capped by `maxFilesShown` (default 20,
   `SummarizeListResult.cs:19-23`); `ByProject` is not capped (`:36-45`). `FindReferences` repeats `summary` inside
   `successData` as well as in the envelope's `listSummary` (`SymbolRelationshipImpl.cs:287-343`). Still open: the
   numeric bound (what share of the cap these parts may take before it counts as a violation).
3. **Line ranges.** `GetMethodSource` has `Envelope.ReturnedFromLine` (`WorkspaceReadNavigationImpl.cs:352-357`);
   `ReadFile` has `startLine` / `endLine` / `totalLines`. Hypothesis: `GetMethodSource`'s `source` is `ToFullString()`
   (`:344`, includes leading trivia) while its span comes from `GetLocation()` (`:351`, excludes it), so line 1 of
   `source` may not be `returnedFromLine`.
4. **Two or more large strings and no list.** No such result exists; dictionaries of strings are the near case (9).
5. **`[NeverInline]`.** No production use; 3 test uses (`ResultPagerTests.cs:100-107`). It was meant for the
   Build / Test / ChangedContent side stores (plan_wire_result_marshaller Step 8, not done; those still use the Raw
   store). DECIDED 2026-11-04: keep it as "outside the sequence, stored only, with a pointer" (Decided 9).

Still open:

6. **Default sequence on by default, or opt-in first.** Static sample of results with two or more top-level lists
   (a sample, not a count; sites of the form `SuccessData = new X(...)` and multi-line records were not scanned):
   `FindReferences` `all` (`SymbolRelationshipImpl.cs:343`); `TextSearchResult` literal / regex
   (`WorkspaceReadNavigationImpl.cs:1141`); `CodeInventoryReport`, 4 lists (`IntelligenceTools.cs:108`);
   `SolutionItemsAllResult`, 3 lists (`WorkspaceProjectManagementImpl.cs:167`); `AppliedChangeSummary`; `GetTypeInfo`
   with `include: both` (`SymbolNavigationImpl.cs:222`); `MoveAllTypesToFiles` (`AdvancedRefactoringTools.cs:355`);
   `BuildResult` (lists capped by the projector, `BuildResultProjector.cs:18-27`). The slice 2 probe gives the runtime
   count.
7. **Overlong-line chunks:** marker wording, and whether chunks count in `totalRecords`.
8. **Round-robin counts entries, not size:** a list of large entries takes more of the budget per round. Probably
   acceptable; not measured.

Challenges to Decided items (found by slice 1; 9-12 DECIDED 2026-11-04 as recommended, 13 is scope only):

9. **Dictionaries of large strings, and unbounded diffs (Decided 1 and 4).** `AppliedChangeSummary.ChangedContent`
   (full file texts; inline only for dry run, no-stage or `InlineOnApply`, `AppliedChangeSummary.cs:42-44`) and
   `MoveAllTypesToFiles`'s `ContentPreviews` are dictionaries of strings. `Diff` strings (`AppliedChangeSummary`,
   `RefactoringSignatureImpl.cs:128`, `MoveAllTypesToFiles`) are unbounded and sit beside lists. None is an array or
   the result's one large string, so Decided 1 gives them no sequence; today `Fit` cuts strings at any depth.
   DECIDED: stored-only with a pointer via `[NeverInline]`; a diff that is the main output pages by lines (Decided 9).
10. **"`offset` counts units for every tool" (Decided 5).** Not while the Raw store has live writers: the backstop
    filter (`ServiceRegistrationExtensionsBasic.cs:546`), the Build / Test full lists (`WorkspaceBuildTestImpl.cs:218`,
    `:235`) and `ValidateAndApplyHelper.cs:204` page by characters with `charLimit`
    (`WorkspaceReadNavigationImpl.cs:1698-1760`). About 16 legacy typed branches page with `limit` defaulting to 50
    (`:1684`); no production writer of those was found. DECIDED: Decided 5 is narrowed to Envelope results; slice 6a
    moves the Raw writers.
11. **Decided 5's reason was overstated.** The envelope's `TotalRecords` is mostly a plain full count (about 20
    sites); `NextOffset` is set only by `GetOperationDetail` and `GetLargeResult`; `HasMoreData` by `ListAll` (a cap
    with no offset), `AsyncifyTools` and `ReadFile` (re-query by `startLine`). Keeping page fields in `largeResult`
    still stands; the reason is reworded in Decided 5 (DECIDED).
12. **Three copies of paging fields.** The envelope (`TotalRecords` / `HasMoreData` / `NextOffset`),
    `EnvelopePageResult` (`Path`, `Offset`, `Returned`, `Total`, `Unit`, `NextOffset`, `HasMoreData`;
    `Tools.Basic/EnvelopePageResult.cs`) and the proposed `largeResult.returned` / `nextOffset`. `GetLargeResult`
    already writes `NextOffset` / `HasMoreData` twice (`WorkspaceReadNavigationImpl.cs:1540-1551`). DECIDED: one cursor
    per response (Decided 10).
13. **`GetLargeResult` pages are refitted.** `PageEnvelope` runs `ResultPager.Fit` on its own page when it is over
    budget (`:1560-1568`) and skips an element larger than a page with a warning (`:1546-1570`); pages also pass
    through the marshaller. Slice 6 ports that path as well as the first page.

Other slice 1 findings:

- `[ResultRecords]` on `TextSearchResult.LiteralResults` (`Common/TextSearchResult.cs`) misses regex searches:
  `literalResults` is empty there, so the declared node is not a candidate and `Fit` falls back to inference. The
  sequence fixes it (both lists join); a `// TODO` is at the declaration.
- `largeResult.totalRecords` and the `INCOMPLETE:` fetch call come from `omitted[0]`, the first cut in loop order, not
  the largest (`ToolResultMarshaller.cs:153-154`), so `totalRecords` mixes units (lines for `ReadFile`, items for a
  list).

## Cost / risk

- **Wire change for every paged result:** `omitted` and `path` are replaced by `largeResult.returned` / `nextOffset`
  and an offset-only next call. Readers that must move (slice 1, text-search count, may miss some): about 8
  production files (writers in `ToolResultMarshaller.cs` and `LargeResultHelper.cs`; readers in
  `WorkspaceReadNavigationImpl.cs`, `LargeResultHints.cs`, the `GetLargeResult` descriptions in
  `WorkspaceReadNavigationTools.cs`); 3 unit-test files (`ToolResultMarshallerOnModeTests`, `ResultPagerTests`,
  `EnvelopeLargeResultPagingTests`); 1 ModelEval file (`LargeResultPagingAgentTests`); no agent or skill files; 13
  docs. `GetLargeResultTests` (legacy and Raw) does not need to move.
- **Default sequence** changes splits for some multi-array results (table above).
- **Shape fixes** for unit-rule violations: count unknown until slice 2.
- **Two pagers during migration.**
- **New DTOs:** `PagingHints`, `PagingOrder`, new `LargeResultInfo` fields, possibly a page record; each needs a
  NEW-DTO justification (dto-conventions skill).
- Touches `Common` (pager, envelope), the marshaller, `GetLargeResult`, and the `Search` impl.

## Related

- [proposal_search_relationship_result_dtos.md](./proposal_search_relationship_result_dtos.md) -- its "two-list
  limitation" section and open questions 2, 3 and 5 are superseded by this proposal.
- [plan_wire_result_marshaller.md](../plans/plan_wire_result_marshaller.md) -- `Fit`'s design (Step 3) and Step 7.
- [proposal_central_large_result_offload.md](./proposal_central_large_result_offload.md) and
  [plan_typed_large_result_offload.md](../plans/plan_typed_large_result_offload.md) -- the superseded
  `IPageableResult` design.
- `dto-conventions` skill.
- Memory `reference_central_mcp_request_filter_pipeline` -- the text-based request filters (Open question 1).
