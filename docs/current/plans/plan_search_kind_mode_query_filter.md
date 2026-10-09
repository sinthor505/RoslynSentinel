# Plan: make Search's declaration-kind modes honour `query`, and say so in the Search description

**Status:** IMPLEMENTED 2026-10-08 (steps 1-3 done; step 4 live check pending a server restart). Priority P1.

**Implementation notes (deviations from the draft, per user decisions):**
- Verified: `ExtractOutlineItems` `item.Name` is the bare identifier for every kind (namespaces dotted).
- Added a hard cap: `WorkspaceTools.KindListingMaxItems = 100` (single named constant), applied only by `Search` (the separate `ListAll` tool stays uncapped/unfiltered). `ListAll` Impl gained `nameFilter`, `exactMatch`, `maxItems`. When truncated: `TotalRecords` = full match count, `HasMoreData = true`, `WarningDetails` = "Truncated: showing the first N of M ...". Zero matches stays `NoMatches` isError.
- Measured: the first 100 real method records in the offload format are about 26.9 KB (21.9 KB compact), above the 15 KB (15360) offload threshold, so a full 100-row page is still offloaded. About 55 rows fit under the threshold. Decision pending: lower the cap to ~50, or slim the records (shorter/relative paths). Change `KindListingMaxItems` and the "100" in the Search descriptions (guarded by `SearchTool_DescriptionStatesTheKindListingCap`).
- Tests: 7 new tests in `RoslynSentinel.Tests.Tools.Basic/BatteryTwentyTests.cs` (`SearchKindMode_*`, `ListAllTool_MoreDeclarationsThanSearchCap_IsNotCappedAsync`, `SearchTool_DescriptionStatesTheKindListingCap`).
- Step 4 live verification (restart server, `Search(mode: method, query: "GetSolutionAsync")`) not done: the session was told not to restart the server. Source: journal digest `.claude/journal/digest_20261008-1509.md` (window 2026-10-02..08), cluster "Search".

## Problem

`Search(mode: class|method|property|enum|field|...)` silently drops `query`, `exactMatch` and `fileGlob`
and lists every declaration of that kind. Agents pass `query` out of habit (the parameter is the
obvious one), get thousands of rows, and the result is offloaded to a 1-1.5 MB large-result file.
The journal records this at least 14 times across 7+ sessions:
`[47b2c93d:L4]`, `[47b2c93d:L29]`, `[ebd9923b:L4]`, `[f768a4e8:L6]`, `[748a8fb9:L9]`, `[a08be84f:L4]`,
`[a08be84f:L39]`, `[a08be84f:L50]`, `[a08be84f:L57]`, `[50e0e6ac:L11]`, `[50e0e6ac:L19]`,
`[50e0e6ac:L20]`, and the digest group "Search(mode" (3 entries). One of the entries shows the agent
set `exactMatch: true` and still received all 782 properties, so the intent was unambiguous.
This planner reproduced it live on 2026-10-08: `Search(mode: method, query: "RunTest_FilterMatchesZeroTests_DetailReportsZeroMatchAsync", exactMatch: true)`
returned `totalRecords: 6032`, 1,546,452 bytes, offloaded.

Traced to source (not just the journal):
- `RoslynSentinel.Tools.Basic/WorkspaceTools.cs` `DispatchSearch`, `default:` branch (about line 690):
  `return _readNav.ListAll(reason, SearchModeToListAllKind(mode), projectName, cancellationToken);`
  `query`, `exactMatch` are never passed.
- `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` `ListAll` (line 332) has no name parameter;
  it filters by `kindFilter` only (`item.Kind != kindFilter`).
- The tool `[Description]` on `SearchSolution` (WorkspaceTools.cs about line 612) does say "query ... is
  ignored for declaration-kind modes", but the schema still advertises `query` and `exactMatch` on the same
  tool, and `exactMatch` is described as "mode: symbol only". A documented trap is still a trap: the
  environment offers a parameter, accepts it, and returns a wrong-sized answer with no warning.
- Related description gaps in the same tool: text mode treats `query` as both a literal and a regex
  (`WorkspaceReadNavigationImpl.SearchSolutionText`, line 402) but nothing says so (`[a08be84f:L62]`, the
  agent tried an `isRegex` parameter); `filePath` is ignored in text mode (`[5b4488f3:L10]`) and agents also
  guess a `path` parameter (`[a08be84f:L32]`).

## Decision

Honour `query` in the declaration-kind modes as a case-insensitive name filter (exact when `exactMatch` is
true, which is the existing default; substring when false), and return `NoMatches` with a pointer to
`mode: symbol` when the filter removes everything. Fix the Search descriptions to say what each mode does with
`query`/`exactMatch`. This is a "make the invalid call do the obvious thing" change, not a rejection: it keeps
`Search` backward compatible for callers that pass no `query`.

## Execution rules

- One compile-green slice per step. Build with `Build` (0 errors) after every step.
- No change to the public tool signatures of `ListAll` (both facades) or `Search`; only the Impl method gains
  two optional parameters. Positional callers of `WorkspaceReadNavigationImpl.ListAll` are updated in the same
  edit (use `ReplaceSnippet` with `batchEdits`, definition first).
- ASCII-only punctuation in all new strings.

## Steps

### Step 1 - Add `nameFilter` / `exactMatch` to `ListAll` and pass them from Search
- Files:
  - `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceReadNavigationImpl.cs`
  - `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceTools.cs`
  - `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceReadNavigationTools.cs`
- Symbols:
  - `WorkspaceReadNavigationImpl.ListAll` (line 332). New signature:
    `(ToolCallReason reason, ListAllKind kind = ListAllKind.all, string? projectName = null, string? nameFilter = null, bool exactMatch = true, CancellationToken cancellationToken = default)`.
    Inside the `foreach (var item in ExtractOutlineItems(root))` loop (after the `kindFilter` check) skip the item
    when `nameFilter` is non-empty and `item.Name` does not match: `string.Equals(item.Name, nameFilter, StringComparison.OrdinalIgnoreCase)`
    when `exactMatch`, else `item.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)`.
    First read `ExtractOutlineItems` (WorkspaceReadNavigationImpl.cs line 247) to confirm what `item.Name` holds
    for method/constructor/namespace; if it includes a parameter list or dotted prefix, compare against the bare identifier
    (hypothesis: it is the bare identifier; confirm before writing the comparison).
    After the loops, if `nameFilter` was supplied and `entries.Count == 0`, return
    `new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ToolErrorCode.NoMatches, ...) }` with a message that names
    the kind and the filter, says whether it was exact, and adds `"Use mode: symbol to search every kind at once, or pass exactMatch: false for a substring match."`
    (`ToolErrorCode.NoMatches` is declared at `RoslynSentinel.Common/SentinelCallToolResult.cs:62`.)
  - `WorkspaceTools.DispatchSearch` `default:` branch: pass `nameFilter: query, exactMatch: exactMatch, cancellationToken: cancellationToken`
    (named arguments, because `cancellationToken` is no longer the 4th positional parameter).
  - `WorkspaceTools.ListAll` (about line 606) body `_readNav.ListAll(reason, kind, projectName, cancellationToken)` -> use
    `cancellationToken: cancellationToken` as a named argument.
  - `WorkspaceReadNavigationTools.ListAll` (about line 70) body `_impl.ListAll(reason, kind, projectName, cancellationToken)` -> same named-argument fix.
- Call sites (measured with `FindReferences` on `ListAll`, 2026-10-08): positional callers of the Impl method are exactly three:
  `WorkspaceTools.cs` ListAll facade (~606), `WorkspaceTools.cs` `DispatchSearch` default branch (~690), `WorkspaceReadNavigationTools.cs` ListAll facade (~70).
  The other 17 hits are the `SearchModeToListAllKind` switch arms, the two facade parameter declarations, and two tests
  (`RoslynSentinel.Tests.Tools.Advanced/ComprehensiveToolTests.cs:276,289`) that call the facade by named arguments and are unaffected.
- Tool: `ReplaceSnippet` with `batchEdits` (definition in WorkspaceReadNavigationImpl.cs first, then the two callers).
- Done when: `Build` reports 0 errors.

### Step 2 - Regression tests for the kind-mode query filter
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Tools.Basic\BatteryTwentyTests.cs`
- Change: add four tests next to the existing `SearchSolutionText_*` tests (line ~205), using the same `_workspaceTools.SearchSolution(reason: "test message", mode: ..., query: ...)` pattern and the same fixture (it contains an `Order` type; confirm names with `GetFileOutline`):
  - `SearchKindMode_ClassWithExactQuery_ReturnsOnlyThatClassAsync` - `mode: SearchMode.@class, query: "<an existing class>"`, assert every returned entry's name equals the query (case-insensitive) and count is 1.
  - `SearchKindMode_MethodWithSubstringQuery_ReturnsOnlyMatchingMethodsAsync` - `exactMatch: false`, assert all returned names contain the query and fewer rows than the no-query call.
  - `SearchKindMode_QueryMatchesNothing_ReturnsNoMatchesNamingTheKindAsync` - assert `IsError`, `ErrorData.ErrorCode == "NoMatches"`, message contains the kind and `mode: symbol`.
  - `SearchKindMode_NoQuery_StillListsEveryDeclarationOfTheKindAsync` - guards backward compatibility.
- Done when: the four named tests pass (`RunTest` scope=project `RoslynSentinel.Tests.Tools.Basic`, filter `FullyQualifiedName~SearchKindMode_`).

### Step 3 - Correct the Search descriptions
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceTools.cs`
- Symbols: the `[Description]` on `SearchSolution` (about line 612) and the `[Description]` attributes of its parameters `query`, `fileGlob`, `exactMatch`, `filePath`.
- Change (strings only):
  - Tool description: replace "it is ignored for declaration-kind modes" with: "In declaration-kind modes (all, namespace, class, interface, method, property, struct, record, enum, enumMember, constructor, field) query is an optional case-insensitive name filter (exact match unless exactMatch is false); with no query every declaration of that kind is listed, which can be thousands of rows - prefer passing a query. In text mode query is matched both as a literal and as a regex; fileGlob (not path) restricts files."
  - `query`: "mode: text = literal-or-regex pattern; symbol/references = symbol name; declaration-kind modes = optional name filter."
  - `exactMatch`: "mode: symbol and declaration-kind modes. true (default) = exact name; false = substring."
  - `filePath`: keep "mode: symbol/references only" and add "Ignored in text mode - use fileGlob."
- Done when: `Build` reports 0 errors and step 2's tests still pass.

### Step 4 - Verification
- `Build` (0 errors).
- `RunTest` at solution scope; compare to the known-failure baseline (`reference_known_failing_tests`); report only new failures. If `ArchitectureDocFreshnessTests` fails because tool descriptions are embedded in `docs/generated/*.md`, regenerate with `scripts/Generate-ArchitectureMap.ps1` and include the regenerated files.
- `McpServerControl` stop (with the confirm guard), then `LoadSolution`, then one live call: `Search(mode: method, query: "GetSolutionAsync", exactMatch: true)` returns a handful of rows, not thousands.

## Out of scope

- Requiring a `query` (or `projectName`) for the largest kinds (method/property/field/constructor). See open decisions.
- Making `NoMatches` a non-error empty result (see open decisions); `mode: text` behaviour is unchanged.
- The `fileGlob` zero-match defect: already fixed in `d3125a2` (resolved blocker `blocking_error_search_fileglob_matches_zero_files_absolute_paths.md`).
- `ListAll` itself (the separate tool) gains no new parameter.

## Risks and open decisions

- *Decision for the human:* after this change an unfiltered `mode: method` is still allowed and still returns ~6000 rows. A mandatory-parameter variant (reject the call for method/property/field/constructor/enumMember/class when both `query` and `projectName` are empty) closes the footgun completely but breaks any caller that lists whole kinds on purpose (the `ListAll` tool exists for that). Tradeoff: safety for weak models vs. a behaviour change on a public tool. Recommended: ship this plan first, re-read the next digest, then decide.
- *Decision for the human:* `NoMatches` is returned as `isError: true` (same as text mode, and the orientation breaker is fed by that outcome). Journal entries `[96b0b939:L11]` and `[748a8fb9:L3]` say a valid empty answer reads like a failure. Changing it touches the breaker semantics (`RecordSearchOutcome`, `NoSearchMatchesException`) and is *needs design*, deliberately not done here.
- Hypothesis (unverified): `ExtractOutlineItems` `item.Name` is the bare identifier for every kind. Step 1 requires confirming it; if namespaces are dotted, exact match should accept the full dotted name.
- Stale server: the live Search tool will keep the old behaviour until the server is restarted after a `fullBuild`.
