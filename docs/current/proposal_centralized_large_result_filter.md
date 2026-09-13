# Centralized large-result filter: generic offload backstop for un-wired tools

## Motivation

RoslynSentinel has two large-result mechanisms today, and they cover different scopes:

1. **Per-caller typed offload.** `ToolResult<T>.ForPossiblyLargeDataAsync` (`RoslynSentinel.Common/
   ToolResult.cs:102-126`) builds the tool's own typed `T`, calls `LargeResultHelper.
   StoreLargeResultAsync<T>` (`RoslynSentinel.Common/LargeResultHelper.cs:29-51`), which serializes
   `data` and, if the JSON exceeds `LargeResultHelper.OffloadThresholdBytes` (30 KiB,
   `LargeResultHelper.cs:19`), writes it to `.roslynsentinel/largeresults/largeresult_<timestamp>_
   <resultId>.json` wrapped in a `ResultWrapper { Type, Data }` tagged with a `ResultWrapperType`
   value. The tool's response then carries a `LargeResultInfo` (resultType, resultId, sizeBytes,
   message) in place of `Data`, and `GetLargeResult` (`RoslynSentinel.Server.Basic/
   WorkspaceReadNavigationImpl.cs:760-`) reads it back, switching on `ResultWrapperType` to
   deserialize into the right concrete shape and apply offset/limit paging.

   This is wired into a handful of call sites only — Intelligence/Scan/Asyncify tools per
   `project_offload_helper_partial_wiring` (memory). The current 16 `ResultWrapperType` members
   (`LargeResultHelper.cs:66-84`: `MigrationCandidateFindingList`, `ApiSurfaceEntryList`,
   `CodeInventoryReport`, `MethodSource`, `FileSource`, `MigrationScanSummary`,
   `MemberChangedContent`, `BreakingChangeList`, `TextSearchMatchList`, `ProjectFileList`,
   `ProjectInfoList`, `SolutionItemFileList`, `SolutionItemsAllResult`,
   `SolutionSymbolEntryList`, `SymbolRelationshipResultList`, `BroadenedSymbolRelationshipResults`)
   are each a hand-written case in `GetLargeResult`'s switch, each deserializing into its own
   concrete record/list type and applying (or explicitly not applying — see
   `WorkspaceReadNavigationImpl.cs:1011-1023`) offset/limit paging according to that type's shape.

2. **Filter-level logging only, no offload.** `RoslynSentinel.Server.Basic/
   ServiceRegistrationExtensionsBasic.cs:379-420`, the "Cheap large-result logging" filter, runs
   post-`next()` for every tool call, sums the character length of every `TextContentBlock` in the
   final `CallToolResult`, and — if that sum exceeds `LargeResultHelper.OffloadThresholdBytes` —
   only calls `logger?.LogWarning("Large tool result: tool '{Tool}' returned {SizeChars} chars...")`
   (`ServiceRegistrationExtensionsBasic.cs:409-411`). It does not offload anything. The comment
   above it is explicit about why it exists at all: "most tool result types aren't individually
   wired into LargeResultInfo... this is the only signal for 'this tool call returned a lot of
   data' for those tools" (`ServiceRegistrationExtensionsBasic.cs:381-383`). That gap — a tool with
   no typed offload wiring still ships its full payload over the wire to the agent, up to whatever
   size it happens to be — is what this proposal closes.

The standing default for closing that gap has been "wire `ForPossiblyLargeDataAsync` into the next
tool as it comes up" (per `project_offload_helper_partial_wiring`). That is real, incremental
progress but leaves every not-yet-visited tool exposed indefinitely, and there is no way to backstop
the *next* tool anyone adds without remembering to wire it in by hand. This proposal instead upgrades
the existing filter from log-only to an active generic backstop, so a tool with no per-caller offload
wiring is still protected the moment it's added, without a request to touch that tool's code at all.

## Decision

Upgrade the filter at `ServiceRegistrationExtensionsBasic.cs:379-420` from logging to actually
offloading, as a **generic catch-all only** — not a full replacement of `ForPossiblyLargeDataAsync`,
and not a redesign of `GetLargeResult`'s existing per-type dispatch. The two mechanisms coexist:
typed per-caller offload keeps its per-type fidelity where it's already wired in; the filter is the
backstop for every other tool.

### Why not a full replacement (rejected alternative)

The filter runs after `next()` returns the tool's already-serialized `CallToolResult` — opaque JSON
text inside `TextContentBlock`s. It never sees the tool's original typed `T`. That means it
structurally cannot do what `ForPossiblyLargeDataAsync` does today: it cannot populate a
tool-specific `resultType` string with real per-type meaning, it cannot read or preserve a
`TotalRecords`/`Warning`/`WorkspaceVersion` field that lives elsewhere in that tool's own envelope
while swapping out just `Data`, and it has no way to know whether the payload is a flat list (where
offset/limit paging makes sense) or a single object or a map (where, per
`WorkspaceReadNavigationImpl.cs:1011-1023`, paging explicitly does not apply).

The alternative considered and rejected here was having the filter itself recover that missing
structure by sniffing the parsed JSON — e.g. guessing "this looks like a `data.results` array, so
treat it as list-shaped" or inferring a `resultType` from the tool name. Rejected because:

- It is the same fragility class as the existing IsError-sync filter two entries above it in the
  same file (`ServiceRegistrationExtensionsBasic.cs:296-343`, which already only inspects a
  top-level `success` boolean and explicitly gives up via `catch (JsonException)` when the body
  isn't shaped as expected) — but higher-stakes, because that filter only flips a boolean flag on
  failure, while a sniffing-based offload would change the *payload shape itself* on success. A
  wrong guess there corrupts the very thing the agent is trying to read, not just a status flag.
- It permanently loses per-tool metadata fidelity (`TotalRecords`, `Warning`, a meaningful
  `resultType` distinguishing e.g. `MethodSource` from `FileSource`) that the typed path already
  provides for free wherever it's wired in. There is no reason to degrade an already-correct call
  site to gain a generic backstop for the ones that aren't wired in yet.

So: this proposal is scoped to the generic catch-all only. Existing `ForPossiblyLargeDataAsync` call
sites are unchanged and not superseded.

### Filter behavior (proposed)

Same shape as the six other filters already registered in `ServiceRegistrationExtensionsBasic.cs`'s
`WithRequestFilters` block (`filters.AddCallToolFilter(next => new
ModelContextProtocol.Server.McpRequestHandler<CallToolRequestParams, CallToolResult>(async
(context, cancellationToken) => {...}))`, wrapped in try/catch with `Debug.WriteLine` on internal
failure so a bug in the guardrail itself never breaks the call it's guarding — matching every
existing filter in that block, e.g. `ServiceRegistrationExtensionsBasic.cs:414-416`).

Post-`next()`:

1. Sum `TextContentBlock` text length exactly as today's logging version does
   (`ServiceRegistrationExtensionsBasic.cs:392-404`).
2. If the sum is at or under `LargeResultHelper.OffloadThresholdBytes`: return `result` unchanged.
   This is the common case and must stay a pure pass-through — no behavior change versus today for
   any tool whose response is already small.
3. If over threshold: write the raw response text verbatim to disk and obtain a `resultId`, then
   replace `result.Content` with a single small pointer `TextContentBlock`, e.g.:

   ```
   {
     "offloaded": true,
     "resultId": "<guid-n>",
     "sizeBytes": <n>,
     "message": "Result is <n> bytes (threshold: 30720). Use GetLargeResult(resultId: \"<id>\") to page through results."
   }
   ```

   wording matched to `ForPossiblyLargeDataAsync`'s existing message
   (`ToolResult.cs:123-124`) so a caller sees the same phrasing regardless of which offload path
   produced it.

### Storage: needs a new raw-text path, not a reuse of the typed one

Checked `LargeResultHelper.StoreLargeResultAsync<T>` (`LargeResultHelper.cs:29-51`): every existing
caller passes an already-typed `T` and the method itself calls
`JsonSerializer.SerializeToUtf8Bytes(data)` internally (`LargeResultHelper.cs:32`) before wrapping it
in a `ResultWrapper`. There is no overload that accepts already-serialized text. The filter only has
the final JSON *string* (the tool's `CallToolResult` content, already serialized once by the tool
itself) — re-parsing it into a `JsonNode`/`object` just to hand it back to
`StoreLargeResultAsync<T>` for re-serialization would be wasted work and a second place a shape
mismatch could creep in. Proposed: add a raw-text overload (e.g.
`StoreLargeRawResultAsync(string json, string? solutionRoot, CancellationToken)`) that writes a
`ResultWrapper { Type = ResultWrapperType.Raw, Data = JsonNode.Parse(json) }` directly, reusing the
same `.roslynsentinel/largeresults/largeresult_<timestamp>_<resultId>.json` naming and directory
convention (`LargeResultHelper.cs:44-49`) so `GetLargeResult`'s existing file-resolution logic
(`WorkspaceReadNavigationImpl.cs:772-799`, which globs `largeresult_*_{resultId}.json` under
`.roslynsentinel/largeresults`) needs no change to find it.

`solutionRoot` for the filter: none of the existing filters in this block currently resolve it —
the drift-check filter fetches `PersistentWorkspaceManager` off `context.Server.Services`
(`ServiceRegistrationExtensionsBasic.cs:358`), which exposes `GetSolutionRoot()`-equivalent state.
The new filter would do the same. If no solution is loaded (`solutionRoot` null/empty),
`StoreLargeResultAsync`'s existing behavior is to skip offload and return the data as-is
(`LargeResultHelper.cs:33-36`) — the raw-text overload should mirror that: fail closed to
pass-through rather than erroring, consistent with "a guardrail must never itself break a call."

### New `ResultWrapperType.Raw` case in `GetLargeResult`

`GetLargeResult`'s switch (`WorkspaceReadNavigationImpl.cs:831-1033`) has one case per existing
`ResultWrapperType`, each deserializing into a specific concrete type. Add a `Raw` case that replays
the stored `JsonNode` verbatim as `Data` without deserializing into any concrete shape — the same
"pass through as raw JSON nodes" pattern the switch already uses for
`SymbolRelationshipResultList` when "element shape varies by searchKind"
(`WorkspaceReadNavigationImpl.cs:998-1010`), and the same "paging doesn't apply, return the whole
thing" precedent already established for `BroadenedSymbolRelationshipResults`, a map rather than a
flat list (`WorkspaceReadNavigationImpl.cs:1011-1023`, comment: "a map, not a flat list, so
limit/offset (list-shaped paging) don't apply").

**Resolved:** `Raw` cannot reuse list-shaped `.Skip(offset).Take(limit)` paging (it doesn't know its
own shape — the whole point of this path is that the filter never inspected the payload's
semantics), and critically, `GetLargeResult` is itself a tool call that passes back through this same
filter chain on its way out. If `Raw` ever returned the *whole* stored payload in one response, a
payload that was too big to inline in the first place would still be too big when handed back by
`GetLargeResult` — the filter would catch it again and re-offload it under a *new* `resultId`,
producing an unbounded fetch → still-too-big → re-offload → fetch loop with no exit, which is worse
than the original problem for a weak agent (a silent infinite loop rather than one oversized
response).

So `Raw` paging is **not** "skip/take over parsed elements" — it is a **mandatory byte-window slice
of the stored raw text**, guaranteed under `OffloadThresholdBytes` by construction:

- `offset`/`limit` (when provided) index into the stored text as a character window, not a list index.
- When omitted, default to a fixed window (e.g. the first `OffloadThresholdBytes` bytes) rather than
  the whole text — there is no "return everything" mode for `Raw`.
- The response includes `hasMore`/a next `offset` so the caller can page through the remaining text
  in bounded chunks, mirroring the existing `HasMorePages`/offset-limit convention used elsewhere in
  `ToolResult<T>`.
- This guarantees every `GetLargeResult(Raw)` response is itself under threshold, so it can never
  trigger the same filter's offload path on its own way out — no re-offload is possible by
  construction, which removes the need for a second runtime size check at this call site.

## Related defect: `Build` is a concrete, confirmed example of the gap

Raised from another session and verified against source here. Two independent bugs in the `Build`
tool (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:1322-1357`), both root-caused, not
restated from a transcript:

1. **`Build`'s return path bypasses offload entirely.** Line 1350 is a bare
   `Data = buildResult` — never `ForPossiblyLargeDataAsync`. This is exactly the un-wired-tool case
   this proposal's filter exists to backstop, and a good post-implementation smoke test for it.

2. **`Build`'s default level (`fullBuild`) never caps `Errors`/`Warnings`, and `maxDetails` silently
   does nothing under that default.** `RunFullBuildAsync` (`RoslynSentinel.Basic/BuildEngine.cs:83-202`)
   parses `dotnet build` output with a regex and puts the *entire* uncapped `errors`/`warnings` lists
   straight into `BuildResult.Errors`/`.Warnings` (`BuildEngine.cs:193-194`) — only `ErrorSummary`/
   `WarningSummary` (grouped by diagnostic Id) is bounded, at `SummaryTopN = 50`
   (`BuildEngine.cs:186,195-196`). Compare `RunQuickBuildAsync` (`BuildEngine.cs:18-77`), which at
   least threads `maxDetails` through to `DiagnosticEngine.GetSolutionDiagnosticsAsync(maxDetails,
   ...)` (`BuildEngine.cs:52`). The `Build` tool signature declares `maxDetails` as a top-level,
   schema-visible parameter with a default (`SentinelWorkspaceTools.cs:1330`), but only forwards it to
   `RunQuickBuildAsync` (`SentinelWorkspaceTools.cs:1343`) — the `fullBuild` branch
   (`SentinelWorkspaceTools.cs:1342`) doesn't accept it at all, and `fullBuild` is `Build`'s own
   default `level` (`SentinelWorkspaceTools.cs:1327`). So on an unmodified default call, a
   schema-visible parameter the agent may reasonably set to control response size has no effect —
   the same "optional parameter relocates the failure instead of preventing it" pattern flagged
   elsewhere in this repo's conventions. This is the mechanism that produced a reported 2,160-error /
   ~880 KB response.

**This proposal's filter would catch case 1 as a safety net** (an oversized `Build` response gets
offloaded generically once the filter ships) **but does not fix case 2's root cause** — the full
uncapped list is still constructed server-side before the filter ever sees it, and `maxDetails`
remains a schema lie for the default path regardless of what the filter does downstream. Per this
repo's failure doctrine, the filter is the safety net; `RunFullBuildAsync` capping `Errors`/
`Warnings` the same way `RunQuickBuildAsync` does (and either forwarding `maxDetails` into it or
making the parameter's `[Description]` state plainly that it applies to `quickBuild` only) is a
separate, independently-actionable fix that should not wait on this proposal's implementation or
review.

## Known gap this does not close

A tool that builds an oversized payload fully in memory before ever calling
`ForPossiblyLargeDataAsync` — i.e. constructs the whole `Data` object and returns it inline today —
still allocates that full object server-side under this design. The filter only intercepts the
already-serialized response on the way out; it shrinks the **wire response** (token cost to the
agent, MCP message size) but does nothing for **server-side memory pressure** from building the
oversized object in the first place. This is a mitigation for the response-size problem, not a fix
for the underlying construction cost — stating this explicitly so it is not read as a complete
solution to "a tool call did too much work."

## Verification (once implemented)

- Build to 0 errors.
- Filter-level test: a tool response whose serialized size exceeds
  `LargeResultHelper.OffloadThresholdBytes` gets its `Content` replaced with an offload pointer, and
  a follow-up `GetLargeResult(resultId: ...)` call returns the original content back verbatim
  (round-trip through the new `Raw` wrapper type).
- Regression test: a response under threshold passes through with `Content` unchanged — this is the
  existing log-only filter's current non-offload behavior and must not change.
- Coexistence check (not a new mechanism test): confirm the existing Intelligence/Scan/Asyncify
  tools already wired to `ForPossiblyLargeDataAsync` are unaffected — their responses are already
  small by the time they reach this filter (their own offload already ran), so this filter should
  see them under threshold and no-op.

## Status

**Implemented** by the user (andrewalmond86@gmail.com's session) on 2026-09-12, commit `047fe430`.
Supersedes the per-caller-only default described as the status quo in
`project_offload_helper_partial_wiring` (memory) — that memory's "wire it in as each tool comes up"
plan continues for the tools it already covers, but is no longer the only path to safety for a tool
with no wiring at all.

Both implementation-affecting questions raised above are resolved (see the "Resolved" note under
`ResultWrapperType.Raw` above): `StoreRawJsonAsync` (`LargeResultHelper.cs`) is the raw-text storage
path, and `Raw` paging in `GetLargeResult` is mandatory byte-window slicing, not list-shaped
skip/take. The `Build` tool's two related defects (see above) were fixed in the same commit:
`RunFullBuildAsync` now caps `Errors`/`Warnings` via `maxDetails`, and `Build`'s return path now goes
through `ForPossiblyLargeDataAsync` (using `ResultWrapperType.Raw`, since `BuildResult` has no
dedicated typed case and the generic backstop already covers it).

Build verified green (0 errors) before commit. Not yet done: the Verification section's round-trip/
regression/coexistence tests were not added as automated tests in this pass.
