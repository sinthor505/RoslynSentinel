# Named result DTOs for Search callers / implementations / references (finish plan_wire_result_marshaller Step 7)

**Status:** IMPLEMENTED 2026-11-06. Built: `CallersSearchResult`, `ImplementationsSearchResult` and
`ReferencesSearchResult` in `RoslynSentinel.Tools.Basic/SearchRelationshipResults.cs`, used by
`SymbolRelationshipImpl.FindReferences`, with the same JSON as the anonymous objects they replaced.

Differences from the text below, which is kept as proposed:

- The records carry NO `[ResultRecords]`. `SequencePager.ResolveSequence` treats a non-empty declared list as the only
  sequence, so declaring `Callers` would drop `Implementations` out of paging; the default sequence pages both.
- [proposal_sequence_pager.md](../proposal_sequence_pager.md) (2026-11-04) replaced the `two-list limitation` section and
  open questions 2, 3 and 5. Open questions 1 and 4 are closed: placed in `Tools.Basic`, no marker interface. Open
  questions 6 and 7 only matter while `ResultPager.Fit` remains as a fallback.
- `Rest of Step 7`: no scan-tool or `AsyncifyTools` attributes (those DTOs were not inspected; the default sequence is
  expected to handle them), and the bare-array pager test already exists as `BareArraySuccessData_IsOneList`.

## Motivation

[plan_wire_result_marshaller.md](../../plans/plan_wire_result_marshaller.md) Step 7 still lists one open item:
`[ResultRecords]` for `Search` references / callers / implementations ("anonymous objects need a named record, a
wire-shape decision"), plus bare-list tools. A read-only investigation on 2026-11-04 (explore-tier2) found:

- The three `Search` modes all run `SymbolRelationshipImpl.FindReferences` and return an anonymous object in
  `SentinelCallToolResult<object>.SuccessData` (summary built at `SymbolRelationshipImpl.cs:286`, `:304`, `:339`;
  result built at `:290`, `:308`, `:343`):

  | Mode | SuccessData (wire keys) | List element |
  | --- | --- | --- |
  | callers | `{ callers, summary }` | `CallerInfo` (`SymbolNavigationEngine.cs:16`: CallerMethod, CallerType, FilePath, Line, CodeSnippet) |
  | implementations | `{ implementations, summary }` | `ImplementationInfo` (`:51`: TypeName, FilePath?, Line?, Kind) |
  | references (`all`) | `{ callers, implementations, summary }` | both |

  `summary` is a `ListSummary` (`Common/SummarizeListResult.cs:51`: TotalCount, FileCount, ByFile, TruncatedFileCount).
- The pager reads `[ResultRecords]` by reflection on the runtime type of `SuccessData`
  (`ResultPager.ReflectHints`, `Common/ResultPager.cs:72`). An anonymous object has no attributes and a bare
  `List<T>` has no properties, so both get no hint and `Fit` (`:125`) cuts the largest array and marks it
  `inferred: true`.
- What the model sees does NOT depend on the hint: the `INCOMPLETE:` message, `omitted[0].total` and
  `largeResult.totalRecords` come from `omitted[0]` (explorer cites `ToolResultMarshaller.cs:153-154`; verify before
  relying on it). The only wire difference a declared hint makes is `omitted[].inferred` (false instead of true), plus
  which node is cut first when two large arrays compete.
- Production uses of `[ResultRecords]` today: `TextSearchResult.LiteralResults` (`Common/TextSearchResult.cs:12`)
  and `EnvelopePageResult.Items`. Existing tests show both behaviours with a probe class:
  `ResultPagerTests.Golden_Search300Hits_CutsTheLargestArray_AndMarksItInferred` and
  `Golden_Search300Hits_WithADeclaredRecordsHint_IsNotInferred`. No test covers a bare-array `successData`.

So the motivation is identification and declared intent ("which list is the records"), not a correctness defect.
The author's initial read was "leave it to inference"; the user prefers a named DTO because it is easier to identify.

## Proposal

### Named result records (recommended direction)

Three records, same wire keys as today, each reusing `ListSummary` and the existing element types:

```
CallersSearchResult([property: ResultRecords] List<CallerInfo> Callers, ListSummary Summary)
ImplementationsSearchResult([property: ResultRecords] List<ImplementationInfo> Implementations, ListSummary Summary)
ReferencesSearchResult([property: ResultRecords] List<CallerInfo> Callers, List<ImplementationInfo> Implementations, ListSummary Summary)
```

Where they live is undecided (Common next to `TextSearchResult`, or Tools.Basic next to the impl; `CallerInfo` and
`ImplementationInfo` are in `Engines.Basic`, so Common cannot reference them without a move -- check layering).
Property names are camelCased on the wire (System.Text.Json via `McpJsonUtilities.DefaultOptions`), so the JSON is
byte-identical to the anonymous objects.

Naming: `*SearchResult` rather than `CallersResult` etc. because the results come from `Search` modes (not a tool named
FindReferences), it groups in a type search, and it matches `TextSearchResult`. Collisions checked 2026-11-04:
`RoslynSentinel.Engines.Basic/DiscoveryEngine.cs:17` already declares `record SearchResult(...)` (never use the bare
name) and `Common/BatchTypes.cs:427` has the unrelated `UpliftCallersResult`.

Each record needs a NEW-DTO justification (dto-conventions section 2). Candidates to name as evaluated:
`ListSummary` (counts only, no records), `TextSearchResult` (text-search specific), `EnvelopePageResult` (the
`GetLargeResult` page).

### The two-list limitation (references / `all` mode)

`ReflectHints` keeps one records path: `recordsPath ??= path`, and `Fit` takes one `string? recordsPath`. A record with
two `[ResultRecords]` properties silently ignores the second. In `ReferencesSearchResult` only `Callers` would be
declared; `Implementations` falls back to inference (`inferred: true`). Two ways to live with it:

1. Accept the asymmetry and say so in a code comment on the record.
2. Extend the pager to a set of records paths: `ReflectHints` returns `IReadOnlyList<string>`, `Fit` prefers the
   largest candidate whose path is in the set. The public `Fit(..., string? recordsPath, ...)` overload makes this a
   migration-first change (new overload beside the old, old `[Obsolete]`, callers moved). No wire change; new golden
   cases in `ResultPagerTests`. Behaviour is unchanged (inference already cuts the largest list); only the `inferred`
   flag becomes accurate.

### Rest of Step 7 (independent of the Search decision)

- Scan tools and `AsyncifyTools` `page` return named DTOs (`Tools.Advanced/ScanTools.cs:84-232`,
  `AsyncifyTools.cs:277`): add `[ResultRecords]` on each existing list property. Shapes NOT yet inspected.
- `ListAll` (`WorkspaceReadNavigationImpl.cs:614`), `LocateSymbol` (`SymbolNavigationImpl.cs:74`) and
  `QuerySymbolRelationships` (`SymbolRelationshipImpl.cs:85,99,136,143`) return a bare list (or a
  `Dictionary<string, List<object>>` at `:143`). An attribute cannot attach to a bare list and wrapping it would turn
  an array into an object (wire break), so these stay on inference.
- Add a pager test for a bare-array `successData` (none exists).
- Reword plan Step 7 "Done when" to: "`[ResultRecords]` where a DTO has a records property; bare lists and anonymous
  `Search` results rely on inference" if the named records are NOT built.

## Alternatives considered, not pursued

- **Leave `Search` to inference.** No new types, no test churn, no model-visible difference. Rejected as the sole
  option only because the user wants identifiable DTOs; it remains the fallback.
- **Generic `ListResult<T>(List<T> Items, ListSummary Summary)`.** One type, but the wire keys become `items`/`summary`
  instead of `callers`/`implementations`: a wire break for all three modes.
- **`IPageableResult` interface** (`proposal_central_large_result_offload.md`, `plan_typed_large_result_offload.md`,
  SUPERSEDED 2026-11-02, never built). Shape was `Records` + `RecordsJsonName`, implemented explicitly. The
  `[ResultRecords]` attribute replaced it: the pager reflects on the runtime type, nothing is exposed and no name
  string has to match a property. An EMPTY marker interface on the new records is still possible if a ratchet test
  ("which results carry a list") is wanted; the compiler and marshaller would not use it.
- **`Dictionary<string, List<object>>`** (`{"callers":[...],"implementations":[...]}`). Same JSON as today's `all`
  mode but: an attribute cannot mark a dictionary entry (no gain for the pager), element types are lost to `object`,
  `summary` (a `ListSummary`, not a list) cannot live in the same dictionary so a wrapper record is needed anyway, and
  a key typo changes tool output with nothing checking it.
- **One tagged list for `all` mode** (each element carries `Kind = caller|implementation`, e.g. a wide
  `ReferenceHit(Kind, FilePath, Line, Name, Container, CodeSnippet?)`). Gives one declared list, one count, one paging
  path and matches how `summary.byFile` already combines both (`combinedPaths`, `SymbolRelationshipImpl.cs:339`). Not
  pursued yet because it breaks the `callers`/`implementations` keys in `all` mode (callers of the keys: tests such as
  `FindReferencesIncludeTestsTests`, tool descriptions, agent prompts, model-eval assertions -- not measured), loses
  CallerInfo/ImplementationInfo typing, and only helps `all`; `callers` and `implementations` modes already have one
  list. Polymorphic `$type` metadata is avoided by dto-conventions section 5. Arguably the better model-facing design;
  needs a call-site measurement before it can be decided.

## Open questions

1. Names and placement of the three records (layering: element types live in `Engines.Basic`).
2. For `ReferencesSearchResult`: accept the single declared list, or take the multi-path pager change first?
3. Tagged single list for `all` mode: worth measuring the readers of `callers`/`implementations` (needs an explorer
   pass: tests, descriptions, prompts, evals) before deciding?
4. Marker interface (empty `IPageableResult`) for a ratchet test: wanted or not?
5. Scan-tool DTO shapes: does any hold two comparably large arrays (that is the only case where the hint changes the
   output beyond the `inferred` flag)? Not inspected.
6. Does `ToolResultMarshaller` use the hint anywhere beyond `ResolveHints`/`Fit`? Only the pager side was read.
7. Hypothesis, untested: `summary.byFile` could outgrow `callers` in a result with few callers across many files and
   win the inference; no real case found.

## Cost / risk

- Three new records plus NEW-DTO blocks; the DTO hook (`enforce-dto-justification.ps1`) will require them in the
  creating call's `reason`.
- Wire shape must stay byte-identical (property names/order, nulls). `SuccessData` is typed `object`, so nothing flags
  a difference; add a serialization-equality test against the old anonymous shape.
- Test churn: `FindReferencesIncludeTestsTests` reads `callers`/`implementations` by case-sensitive reflection
  (`PathsIn`, explorer cites `:99-105`); other `Callers_*`/`All_*` tests not traced. `FindReferencesAmbiguityToolTests`
  and `PathCaseLookupRegressionTests` appear unaffected.
- Three result sites to change in `SymbolRelationshipImpl.FindReferences` (`:290`, `:308`, `:343`); one `Impl` file.
- Pager multi-path change: public `Fit` overload, `ResultPager` goldens, any other `ResolveHints` caller; small, in
  `Common`.
- No model-visible gain from any of this beyond the `inferred` flag and cut priority; the value is identification and
  declared intent.

## Related

- [plan_wire_result_marshaller.md](../../plans/plan_wire_result_marshaller.md) -- Step 7 "Done when" and the open item.
- [proposal_central_large_result_offload.md](../proposal_central_large_result_offload.md) and
  [plan_typed_large_result_offload.md](../../plans/plan_typed_large_result_offload.md) -- the superseded
  `IPageableResult` design.
- `dto-conventions` skill (reuse-first, NEW-DTO format, suffix table, wire safety).
