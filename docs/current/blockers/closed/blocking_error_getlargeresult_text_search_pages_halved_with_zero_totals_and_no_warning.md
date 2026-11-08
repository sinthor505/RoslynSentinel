# `GetLargeResult` text-search pages silently halved, with `totalRecords: 0`, `hasMoreData: false` and no warning

**Status:** OPEN 2026-10-13. Root cause traced to source (three separate defects in one method) and reproduced live; fix not implemented.

## What was being attempted

An `explore-haiku` subagent (reported 2026-10-13) paged a stored `Search` (mode: text) result,
`resultId: b50f1d5483f248b18cff1ee614f9476b` (135 records), with
`GetLargeResult(resultId, limit: 50, offset: 0 | 50 | 100)`.

Re-confirmed by the writer of this doc the same day against two fresh stored results:

- `Search(mode: text, query: "ResultWrapperType", includeTests: true, maxResults: 200)` -> 67 matches,
  `resultId 2a6eb7f1a75e41cbb7a91beb2eafc868`, 20610 bytes.
- `Search(mode: text, query: "ResultWrapperType\\.[A-Za-z]+", includeTests: true, maxResults: 200)` -> 61 matches,
  `resultId 782b03c17f6a4349941b55548ed5c375`, 18704 bytes. Every match is regex-only (the pattern does not
  match as a literal substring), so `LiteralResults` is empty.

Then `GetLargeResult(resultId: ..., limit: 50, offset: 0)` on each.

## The exact symptom

Reported by the subagent (resultId b50f...):

```
limit: 50, offset: 0    -> 25 rows, totalRecords: 0, hasMoreData: false
limit: 50, offset: 50   -> 25 rows, totalRecords: 0, hasMoreData: false
limit: 50, offset: 100  -> 35 rows, totalRecords: 0, hasMoreData: false
```

Rows 25-49 and 75-99 are never returned by a naive offset-advances-by-limit loop. Re-fetching offsets 25
and 75 with `limit: 25` covered everything.

Reproduced (writer, regex-only result 782b03c1...), tail of the response:

```
"regexOverlapCount":0,"regexPatternValid":true},"findings":[],"totalRecords":0,"hasMoreData":false}
```

The page held 25 `regexResults` and `"literalResults":[]`, although 61 were stored and 50 were requested.
There was no `warningDetails` field in the response.

Reproduced on the literal-heavy result (2a6eb7f1...): 25 `literalResults` returned for `limit: 50`,
`totalRecords: 67`, `hasMoreData: true`, again with no `warningDetails`. So the 25-row cut and the missing
warning occur regardless of list; only the zero totals depend on the list being regex-only.

## Source trace

All in `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`, method `GetLargeResult`
(tool wrapper: `WorkspaceReadNavigationTools.cs:104-122`). Stored payload is
`ResultWrapper { Type = TextSearchMatchList, Data = TextSearchResult }` (written by
`SearchSolutionText`, `WorkspaceReadNavigationImpl.cs:623` and `:645`; record
`TextSearchResult(List<TextSearchMatch> LiteralResults, List<TextSearchMatch> RegexResults, int RegexOverlapCount, bool RegexPatternValid)`
at `RoslynSentinel.Common/TextSearchResult.cs:12`).

**Defect 1: the "25 rows" is not a cap constant, it is the size-shrink loop halving 50.**
`case ResultWrapperType.TextSearchMatchList` (`:1251`) takes `Skip(offset).Take(limit)` of each list
(`:1260-1261`), then loops while the serialized page exceeds
`LargeResultHelper.OffloadThresholdBytes - 256` (threshold 15360 bytes, per the offload message), halving the
larger list each pass (`:1274` literal, `:1276` regex). A 50-row page of these matches is over ~15 KB, so it
is cut to 25 (50 / 2). The final 35-row page at offset 100 is simply the remainder fitting under the
threshold. There is no literal `25` anywhere. The list-shaped branches share the same behaviour through
`ShrinkListToFit` (`:1475`, halves with `Take(Math.Max(1, Count / 2))`). Confirmed by reproduction (25 rows
for `limit: 50` on both fresh results).

**Defect 2: the shrink warning is built, then dropped.** The branch computes `WarningDetails = shrunk ? "Page
shrunk to ... Call again with offset: ..." : null` (`:1286`; the other branches build the equivalent "Page
shrunk from N to M ..." string) onto the inner `result`. The final return (`:1445-1451`) copies only
`SuccessData = result.SuccessData` (`:1448`), `TotalRecords` (`:1449`) and `HasMoreData`; it never copies
`result.WarningDetails`. So the one signal that tells a caller "this page was cut, resume at offset X" never
leaves the server. Confirmed by reproduction (no `warningDetails` in either response). This applies to every
typed list branch, not only text search.

**Defect 3: `totalRecords`/`hasMoreData` are computed from the wrong list.** After the switch
(`:1435-1439`):

```
var dataArray = all.Data as JsonArray
    ?? all.Data?.AsObject().FirstOrDefault().Value?.AsArray()
    ?? [];
totalRecords = dataArray.Count;
hasMorePages = (offset + limit) < totalRecords;
```

`TextSearchResult` is an object, not an array, so this takes the first property of the stored object, which is
`LiteralResults` (stored JSON order: `LiteralResults` at line 4, `RegexResults` at line 5 of the stored file).
For a regex-only search `LiteralResults` is `[]`, so `totalRecords = 0` and `hasMorePages = (offset + limit) < 0`
is always false. For a literal-only search the total is right but ignores `RegexResults`. In both cases the
count is the stored size of one list, never the combined `literal + regex` count that `Search` itself
reported (`SearchSolutionText` passes `literalResults.Count + regexResults.Count` as `totalRecords`).

Also: `hasMorePages` uses the requested `limit`, not the number of rows actually returned after shrinking, so
even with a correct total it would say "no more" when a shrunk final window drops rows (for example
`offset + limit >= total` but the page was halved).

Verified the stored subagent file directly:
`.roslynsentinel/largeresults/largeresult_20261013T185003Z_b50f1d5483f248b18cff1ee614f9476b.json` has
`"Type": "TextSearchMatchList"`, `"LiteralResults": []`, and 135 `"MatchedAs": "Regex"` entries (0 Literal).
That is exactly the zero-totals shape, so the subagent's 135-row result was regex-only.

## What is and is not confirmed

Confirmed (source plus live reproduction on 2026-10-13):
- The 25-row page size comes from the halving loop, not a constant (`:1267-1279`, `:1475`).
- `result.WarningDetails` is dropped by the final return (`:1445-1451`).
- `totalRecords: 0` / `hasMoreData: false` for a regex-only stored text search (reproduced on 782b03c1...,
  and the stored subagent file has the matching shape).
- Literal-and-regex results are paged with the same `offset` applied to both lists independently
  (`:1260-1261`), so the page window is not a single flat record index; `offset` only means a flat index when
  one list is empty. The "Call again with offset" hint at `:1286` uses `Math.Max(literal, regex)`, which is
  not a correct resume offset when both lists are populated. Read from source, not separately reproduced.

Not confirmed / hypothesis:
- The exact per-row byte size that makes 50 rows exceed the threshold was not measured; 25 rows fitting and 50
  not is consistent with ~300-600 bytes per row.
- Whether other typed list branches also show `hasMoreData` wrong after shrink was read from source (same
  post-switch block) but not reproduced against a stored non-text result.
- Existing coverage: `RoslynSentinel.Tests.Tools.Basic/GetLargeResultTests.cs` has no `TextSearch` test
  (text search for "TextSearch" in that file returned 0 matches), which is likely why this shipped.

## Why this matters

Silent data loss for any agent paging a Search result. The tool tells the agent the opposite of the truth:
the page is half the requested size, `totalRecords` says 0, `hasMoreData` says false, and the warning that
would explain the cut is thrown away. A weak model following the documented contract (`limit: 50`, then
advance `offset` by 50 while `hasMoreData`) stops after one page or skips every other half-page, and
believes it has the full result set. `Search` is the front door for finding callers and text, so this feeds
wrong "complete" answers into refactoring decisions. Per the failure doctrine this is an environment defect:
the tool should either honour `limit`, or say plainly how many rows it returned and where to resume.

## Suggested direction (not implemented)

1. Copy `result.WarningDetails` into the final return (`:1445-1451`) so the shrink message reaches the caller.
   Cheapest, highest value.
2. For `TextSearchMatchList`, compute totals from the combined list:
   `totalRecords = LiteralResults.Count + RegexResults.Count`, and page over the flattened
   `literal.Concat(regex)` sequence with a single flat `offset`, then split back into the two properties.
   This also fixes the per-list offset and the resume hint.
3. Compute `hasMoreData` from rows actually returned: `offset + returnedCount < totalRecords`, not
   `offset + limit < totalRecords`, and return an explicit `nextOffset` (as the Raw branch already does with
   `nextOffset`) so a caller never has to infer it.
4. Optional: set `LargeResultHelper.OffloadThresholdBytes` headroom for pages or cap `limit` in the tool
   description to what will actually fit (the current description says "Maximum number of records to return",
   which is not true when the size backstop trims it).
5. Replace the "first property of the object" fallback (`:1436`) with per-type totals so a new wrapper type
   cannot silently report 0.

Regression test sketch (in `GetLargeResultTests.cs`, using the existing `WriteLargeResultFile` helper at
line 66):
- Write a `TextSearchResult` with `LiteralResults = []` and 135 `RegexResults` whose previews are long enough
  that 50 rows exceed the threshold; wrap as `ResultWrapperType.TextSearchMatchList`.
- Call `GetLargeResult(resultId, limit: 50, offset: 0)`. Assert `TotalRecords == 135` (not 0), `HasMoreData`
  is true, the returned row count equals what the response says it returned, and `WarningDetails` is non-null
  when the page was shrunk.
- Loop: advance `offset` by the returned count (or `nextOffset`) until `HasMoreData` is false; assert the
  union of rows equals the 135 stored rows with no gaps or duplicates.
- Repeat with a mixed result (literal 40 + regex 95) to cover the combined count and the flat offset, and with
  a result small enough that no shrink occurs (no warning, `HasMoreData` false on the last page).

## Resolution

Resolved 2026-10-13 in d771806bd6df7042bc5a8a020fdd622a520e624e.

- Root cause: as traced above. Totals came from the first list property (empty `LiteralResults` for a regex-only search), the shrink warning built on the inner result was dropped at the final return, and `hasMoreData` compared against `offset + limit`.
- Fix: `GetLargeResult` pages text-search literal and regex rows as one flat sequence (literal first) with one offset; `TotalRecords` is the combined count; the shrink loop never goes below one row; `result.WarningDetails` is copied into the final return (also fixes the dropped warning for other list-shaped branches); `hasMoreData` compares `offset + rows actually returned` with the total.
- Tests: `GetLargeResultTests` T13 (regex-only, 135 rows, limit 50, page-by-returned-count yields every row once), T14 (literal/regex boundary), T15 (small mixed result, no warning). `RoslynSentinel.Tests.Tools.Basic` filter `GetLargeResult`: 18 of 18 passing (baseline 15).
- Not done: a structured `nextOffset` field (the warning text carries the resume offset); the tool description still calls `limit` the maximum number of records without mentioning the size backstop.
