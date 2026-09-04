---
name: querysymbolrelationships_offload_fix
description: QuerySymbolRelationships never called the large-result offload mechanism; fixed 0431e7a
metadata: 
  node_type: memory
  type: project
  originSessionId: c8de2a0e-3c38-4f60-966e-60972a77f7be
  modified: 2026-09-04T10:20:42.087Z
---

`QuerySymbolRelationships` (SentinelSymbolTools.cs) hand-built its `ToolResult<object>` responses directly (`new ToolResult<object> { Data = results }`) instead of calling `ToolResult<object>.ForPossiblyLargeDataAsync`, so large query results (e.g. `attributeUsages` on a broad attribute like `McpServerTool` — confirmed 70,172 chars against the 30,720-byte threshold) went out inline as raw JSON with only a passive `LogWarning` from the shape-agnostic MCP call-tool filter — never actually offloaded to disk.

Root cause was isolated to this one file: sibling Basic-project tool files (`SentinelWorkspaceTools.cs`, `SentinelRefactoringTools.cs`) and Advanced-project files (Intelligence/Scan/Asyncify) all correctly call the offload builder. `SentinelSymbolTools.cs` had zero references to `ForPossiblyLargeDataAsync`/`StoreLargeResultAsync`/`LargeResultHelper` anywhere.

**Why the offload mechanism doesn't need per-field summarization**: `ForPossiblyLargeDataAsync`/`StoreLargeResultAsync` don't trim or compress fields (e.g. long `filePath` strings) — they offload the *entire* payload to `.roslynsentinel/largeresults/*.json` and return a `resultId` for `GetLargeResult` to page through. So "how do we shrink the filePath field" wasn't the right question — the mechanism already solves bulk by moving it out of the inline response, not by summarizing content.

**Fix** (commit 0431e7a): Added two new `ResultWrapperType` enum cases in `LargeResultHelper.cs` — `SymbolRelationshipResultList` (list-shaped; element type varies per `searchKind`: `ImplementationInfo`, `AttributeUsageSite`, `ObjectCreationSite`, `ExtensionMethodInfo`, `SearchResult` — so `GetLargeResult` deserializes generically as `JsonArray`/`JsonNode` rather than one concrete record type) and `BroadenedSymbolRelationshipResults` (map-shaped, `Dictionary<string, List<object>>`, for the broaden-on-empty fallback path — treated like `SolutionItemsAllResult`, no list-style paging). Both call sites in `QuerySymbolRelationships` now route through `ForPossiblyLargeDataAsync`, and `GetLargeResult`'s switch in `SentinelWorkspaceTools.cs` (~line 2685) got matching cases plus a `TotalRecords`/`HasMorePages` branch for the map-shaped type.

**Why:** User reported a 70K-char response from a routine query; root cause was a straight omission (not a design gap in the offload mechanism itself — no base class enforces the builder, so a hand-rolled `ToolResult<object>` compiles silently and is invisible except via a runtime log line).

**How to apply:** If another tool is suspected of the same gap, grep the file for `ForPossiblyLargeDataAsync|StoreLargeResultAsync|LargeResultHelper` — zero hits in a tool file that returns lists/dictionaries is the smoking gun. See [[project_offload_helper_partial_wiring]] for the broader partial-wiring context this closes one instance of. Broader finding: this class of bug is only caught by a runtime log warning, not compile-time or test-time — other unaudited tool files may have the same gap.
