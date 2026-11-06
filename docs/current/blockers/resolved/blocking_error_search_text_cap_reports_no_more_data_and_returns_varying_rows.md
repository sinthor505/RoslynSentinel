# `Search(mode: text)` at `maxResults` reports `hasMoreData: false`, returns different rows between runs, and warns even when nothing was cut

**Status:** RESOLVED 2026-11-05. See Resolution. One related contradiction on the last `GetLargeResult` page stays open.

## Resolution

**Root cause:** `SearchSolutionText` stopped its parallel scan once `maxResults` was reached, so scheduling chose the rows, and every
count was then taken from the capped lists.

**Fix** (the commit that moved this file to `resolved/`), in `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`:

- Every document is scanned, collecting lightweight hits. Hits are then sorted, deduped and capped once across literal and regex rows.
  Previews, context and enclosing members are built only for the kept rows.
- `TotalRecords` is the true match count, and `HasMoreData` is true when it exceeds the rows returned. The warning fires only then.
- `regexOverlapCount` is counted before the cap.
- A file reached through two projects now gives one row instead of two.

**Tests:** five new `SearchSolutionText_*` tests in `RoslynSentinel.Tests.Tools.Basic/BatteryTwentyTests.cs`. Full solution run: 6986
tests, 6954 passed, 31 skipped, 1 failed. The failure is `LaterCommitTouchingServerSource_IsStaleAndListed` ("git failed to run:
rev-parse HEAD"), which is outside this change and passes when run alone (8 of 8 in its class).

**Verified live** on a test instance of the working tree:

| Check | Result |
| --- | --- |
| Original query, cap 20 | 29 total, `hasMoreData` true, "20 of 29 matches returned across 6 files." |
| `public async Task`, cap 3, 10 runs | 1 distinct row set |
| `BuildRecoveryHintAsync`, 3 matches, cap 3 | no warning, `hasMoreData` false |
| Same query, cap 2 | warning, `hasMoreData` true |

The original exact-cap query no longer matches anything, because the fix removed that text, so the check used a different query.

**Scan cost:** warm runs took about 640 ms for a query with no hits and 640-850 ms for `public` (19,356 matches). One no-hit run took
10.6 s, unexplained and not repeated. The old early-stopping code was not timed.

**Still open:** a capped result that is also split into pages ends on "COMPLETE ... nothing more to fetch", with `hasMoreData` false and
no warning, though the first response says "200 of 19356". This predates the fix. `// TODO` at `RoslynSentinel.Common/SequenceMessages.cs:25`.

The sections below are the original report. The source lines they cite are from before the fix.

## What was being attempted

Confirming that no production code classifies field reads and writes, for
`docs/current/proposals/proposal_ilspy_derived_capabilities.md`:

```
Search(mode: text, query: "ValueUsageInfo|IsWrittenTo|GetValueUsageInfo|DataFlowsIn|WrittenInside",
       fileGlob: "RoslynSentinel.*/**/*.cs", maxResults: 20)
```

The running server reported `"isServerBinaryStale": true` throughout. The live results match the source below.

## The exact symptom

**The truncation signals contradict each other.** First call above, verbatim fields:

```
"totalRecords":20,"hasMoreData":false,
"warningDetails":"20 matches found - returning first (20) matches scanned - Narrow fileGlob/pattern or increase maxResults to see more.",
"statusMessage":"20 matches across 5 files."
```

The cap hid any `ValueUsageInfo` or `IsWrittenTo` hits, so a narrower query was needed to answer the question.

**The returned rows differ between identical calls.** `Search(mode: text, query: "public async Task",
fileGlob: "RoslynSentinel.*/**/*.cs", maxResults: 3)`, run twice:

```
Run 1: Common/AgentLoop/ModelAgentRunner.cs:103, Common/LmStudioAgentClient.cs:116, Common/LmStudioClient.cs:40
Run 2: Common/AgentLoop/ModelAgentRunner.cs:103, Common/LmStudioClient.cs:40,       Common/MigrationLedger.cs:117
```

Run 2 omits `LmStudioAgentClient.cs:116`, which sorts before `MigrationLedger.cs`, so it did not return the first three
matches.

**The warning fires when nothing was cut.** `Search(mode: text, query: "Volatile.Read(ref resultCount)",
fileGlob: "RoslynSentinel.Tools.Basic/**/*.cs")`:

```
maxResults: 50 -> totalRecords 2, no warningDetails
maxResults: 2  -> totalRecords 2, "2 matches found - returning first (2) matches scanned - Narrow fileGlob/pattern or increase maxResults to see more."
```

Both calls return the complete result set, yet the second tells the caller there may be more.

## Source trace

All in `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`, method `SearchSolutionText`:

- `:999` scans documents with `Parallel.ForEachAsync`.
- `:1004` and `:1056` stop the scan once the shared `resultCount` reaches `maxResults`. Which documents finish first
  decides which matches exist, so scheduling picks the rows.
- `:1126-1127` claims otherwise: "sort deterministically before any maxResults truncation so repeated identical searches
  return identical result sets". The sort at `:1128` only orders whatever the early-stopped scan collected.
- `:1135` computes `totalResultCount` from the already truncated lists, so it can never exceed what was returned.
- `:1201` warns on `totalResultCount >= maxResults`, which is also true when exactly `maxResults` matches exist.
  `:1203` then prints that capped number as "N matches found".
- `:1212` sets `TotalRecords` to the returned count and never sets `HasMoreData`, so it keeps the default `false`.
  `RoslynSentinel.Common/SentinelCallToolResult.cs:301` documents `TotalRecords` as the total "before pagination was
  applied", and `:309` documents `HasMoreData` as true when more exists. `ListSummary` is built from the same capped rows
  (`:1211-1212`), so `statusMessage` states "20 matches across 5 files" as fact.

Related, from reading the source only:

- `:1130` and `:1133` apply `Take(maxResults)` to literal and regex rows separately, so a call may return more than
  `maxResults` rows in total, and the warning's numbers would then disagree with each other.
- `:1134` computes `regexOverlapCount` as raw regex rows minus kept regex rows, so regex rows cut by the cap are counted as
  overlaps.

A text search of `RoslynSentinel.Tests*` for the warning text finds nothing, so no test pins this behaviour.

## What is and is not confirmed

Confirmed:

- The contradictory signals, the varying rows and the warning at the exact cap, each reproduced live above.
- The cause of each, by the source lines above.

Not confirmed:

- The `Take` and `regexOverlapCount` items were not reproduced live.
- How often the rows vary. Two runs were enough to show it, but no rate was measured.
- The scan cost of removing the early stop. It has not been measured.

## Why this matters

An agent asking "does this exist anywhere?" gets a partial answer whose machine-readable fields say it is complete.

- A weak model is likely to read `hasMoreData: false` and `totalRecords` and stop. The only correct signal is free text in
  `warningDetails`, and that text also fires when the result is complete, so it does not reliably mean anything.
- Varying rows make a repeated search look like the code changed, and make failures harder to reproduce.
- The tool's description says that `totalRecords` and `hasMoreData` show truncation, though it says so of the
  declaration-kind modes, so an agent has every reason to trust the same fields here.

The same family of defect appeared in
`docs/current/blockers/resolved/blocking_error_getlargeresult_text_search_pages_halved_with_zero_totals_and_no_warning.md`.

## Suggested direction (not implemented)

1. Make the rows deterministic: scan fully, or scan documents in sorted order and stop only after the cap plus one match.
   Measure both on this solution first, because the early stop exists for speed.
2. Apply one cap across literal and regex rows, after their dedupe.
3. Set `HasMoreData` from whether a match beyond the cap was seen. Warn only in that case.
4. Report a true `TotalRecords` after a full scan. After an early stop, leave it null rather than give the capped count, and
   word the warning as "more than N".
5. Compute `regexOverlapCount` before the cap.
6. Add tests: exactly at the cap (no warning, `hasMoreData: false`), over the cap (`hasMoreData: true`), and the same capped
   query repeated across many documents returning identical rows.

A `// TODO` marks the warning site in `WorkspaceReadNavigationImpl.cs`.
