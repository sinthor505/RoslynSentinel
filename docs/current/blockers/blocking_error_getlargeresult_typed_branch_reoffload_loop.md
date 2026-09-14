# `GetLargeResult` re-offload loop on typed (non-Raw) branches — no way to page a `SymbolRelationshipResultList`

**Status:** OPEN — traced to source, fix not yet applied (report-only per dog-fooding policy).

## What was being attempted

Model (qwen3-4b, via LM Studio) was asked to list all members with the `[McpServerTool]`
attribute. Transcript: `lmstudio_logs/Solution Projects List - 2026-09-14 10.12.md`.

1. Called `QuerySymbolRelationships(name: "McpServerTool", searchKind: "attributeUsages")`
   (log line 119-126). Result (log line 133) came back offloaded: 117 records, `sizeBytes: 80572`,
   `resultId: "5f84296fa24d466d8bc8ead032da1a68"`, `largeResult.resultType: "FindUsagesSearchKind"`.
2. Called `GetLargeResult(resultId: "5f84296fa24d466d8bc8ead032da1a68", limit: 50, offset: 0)`
   (log line 149-157) expecting the first page of 50 records back.

## The exact error / unexpected result (verbatim)

Instead of a page of records, the second call returned another offload envelope (log line 164):

```
{"offloaded":true,"resultId":"57073a2234614e1683b875f1af3153cc","sizeBytes":35745,"message":"Result is 35745 bytes (threshold: 30720). Use GetLargeResult(resultId: \"57073a2234614e1683b875f1af3153cc\") to page through results."}
```

Every subsequent `GetLargeResult` call against the new `resultId` will reproduce the same shape —
a still-too-big blob under yet another `resultId` — because the branch that served the first call
does nothing different for a smaller `limit`; see Root cause. The transcript ends here with the
model asking the user whether to keep retrieving (log line 168-169), i.e. it has no way to escape
the loop on its own.

## Where it happened

- Tool: `GetLargeResult`, wrapper type `ResultWrapperType.SymbolRelationshipResultList` branch.
- `RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs:1084-1096` (the branch dispatched to
  for this resultId, based on the originating tool being `QuerySymbolRelationships`).
- Re-offloaded by `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs:390-409`
  ("Generic large-result offload backstop" `AddCallToolFilter`).
- Threshold constant: `RoslynSentinel.Common/LargeResultHelper.cs:19`
  (`OffloadThresholdBytes = 30 * 1024`).

Note: the *first* offload's `largeResult.resultType` was logged as `"FindUsagesSearchKind"` (log
line 133), not `"SymbolRelationshipResultList"` — that field appears to log the search-kind enum
rather than the `ResultWrapperType` used for storage/retrieval. This doc treats
`SymbolRelationshipResultList` as the retrieval-side wrapper type based on the `QuerySymbolRelationships`
→ `GetLargeResult` code path in `WorkspaceReadNavigationImpl.cs:1084-1096`; the exact mapping from
`FindUsagesSearchKind` to that switch case has not been independently re-derived in this pass and is
flagged here as unconfirmed rather than asserted as fact.

## Root cause (traced to source)

1. **The generic offload filter re-wraps ANY oversized outgoing response, including
   `GetLargeResult`'s own output, unconditionally.**
   `ServiceRegistrationExtensionsBasic.cs:378-409` — the filter comment states it "sums the length
   of text content blocks in the response... above `LargeResultHelper.OffloadThresholdBytes`, writes
   the raw response text verbatim to disk... and replaces the response with a small pointer" and
   that it is "shape-agnostic" because "most tool result types aren't individually wired into the
   typed... path." It runs on every tool call's response, with no exclusion for `GetLargeResult`
   itself.

2. **The `Raw` branch was hardened against this exact re-offload trap; the other 15+ branches were
   not.** Commit `8b14a86f` (2026-09-12, "Add tests for centralized large-result filter, fix Raw
   re-offload loop") added a worst-case-size-then-shrink loop to the `Raw` branch specifically:
   `WorkspaceReadNavigationImpl.cs:874-898` computes a `worstCaseMaxSlice` bound, builds a candidate
   page, and repeatedly halves `length` (line 896) until `JsonSerializer.Serialize(pageData, ...)`
   verifiably fits under `OffloadThresholdBytes - envelopeOverheadBytes` (line 894), specifically
   "so this filter could never re-catch GetLargeResult's own Raw-branch output" (comment,
   lines 850-852). Confirmed via `git show 8b14a86 --stat`: only `WorkspaceReadNavigationImpl.cs`,
   `GetScanResultTests.cs`, and a new `LargeResultOffloadFilterTests.cs` were touched — none of the
   non-`Raw` switch branches were modified.

3. **Every other branch just does `.Skip(offset).Take(limit).ToList()` with no size verification.**
   E.g. `WorkspaceReadNavigationImpl.cs:1084-1096` (`SymbolRelationshipResultList`, the branch this
   transcript hit), and likewise `MigrationCandidateFindingList` (919-931), `ApiSurfaceEntryList`
   (933-943), `SolutionSymbolEntryList` (944-954), `CodeInventoryReport` (955-965),
   `BreakingChangeList` (1014-1024), `TextSearchMatchList` (1025-1038), `ProjectFileList`
   (1039-1049), `ProjectInfoList` (1050-1060), `SolutionItemFileList` (1061-1071),
   `BroadenedSymbolRelationshipResults` (1097-1103+). None of these scale the returned slice against
   actual per-record byte size or re-check the serialized response length before returning. If the
   caller's requested page (default `limit=50`, as in this transcript) still serializes past 30KB
   once wrapped in the `ToolResult<object>` envelope, the filter from point 1 catches *this*
   branch's output on the way out and re-offloads it under a brand-new `resultId` — same bug class
   `8b14a86f` fixed for `Raw`, just present in every sibling branch that commit didn't touch.

4. **The loop has no guaranteed convergence and gives the model no way to escape it.** Because
   record size (not the model's requested `limit`) determines whether the re-offload filter
   re-triggers, a weak/local model reducing `limit` has no visible signal of whether that will help,
   and nothing in the response tells it to. This is the failure mode the CLAUDE.md failure doctrine
   calls out directly: the environment (tool response) gave the model a next-step instruction
   ("Use `GetLargeResult(resultId: ...)` to page through results") that is not actually able to
   terminate.

## What's confirmed vs. not

- Confirmed: the filter's unconditional post-processing (`ServiceRegistrationExtensionsBasic.cs:390-409`).
- Confirmed: `8b14a86f`'s fix is scoped to the `Raw` branch only (`git show 8b14a86 --stat`).
- Confirmed: the `SymbolRelationshipResultList` branch (and all listed siblings) has no equivalent
  shrink-and-verify loop, by direct reading of `WorkspaceReadNavigationImpl.cs:919-1103`.
- Not confirmed: the exact internal mapping from `QuerySymbolRelationships`'s
  `largeResult.resultType: "FindUsagesSearchKind"` (log line 133) to the `ResultWrapperType` enum
  value stored on disk and read back by the switch in `GetLargeResult` — inferred from the
  `SymbolRelationshipResultList` case's own comment ("Element shape varies by searchKind...") rather
  than directly observed in this transcript.
- Distinct from `docs/current/CLOSED.md`'s existing large-result entries: the closed
  `ForPossiblyLargeDataAsync` / matching-`GetLargeResult`-enum entry (CLOSED.md:260) and the
  `8b14a86f` Raw-branch fix both predate and do not cover this — this is specifically about the
  non-`Raw`, list-shaped switch branches never receiving the same size-verification treatment.

## What unblocks this

Two independent options, for whoever picks this up (not a mandate, a menu):

1. **Generalize the Raw-branch fix.** Extract the shrink-and-verify pattern
   (`WorkspaceReadNavigationImpl.cs:874-898`) into a shared helper that takes a candidate page (any
   list-shaped `Data`), serializes the full `ToolResult<object>` envelope, and repeatedly halves the
   effective `limit` until the serialized size verifiably fits under
   `OffloadThresholdBytes - envelopeOverheadBytes` — mirroring lines 893-898 — then apply it to every
   branch at `WorkspaceReadNavigationImpl.cs:919-1103+` that currently does a bare
   `.Skip(offset).Take(limit).ToList()`.
2. **Exempt `GetLargeResult` from the generic filter and fail loudly instead.** In
   `ServiceRegistrationExtensionsBasic.cs:390-409`, skip re-offload when
   `context.Params?.Name == "GetLargeResult"` and instead have `GetLargeResult` return a hard
   error/warning naming the actual per-record size and suggesting a smaller `limit`, so the model
   sees a legible stop condition instead of an infinite blob-swap. This does not by itself guarantee
   convergence (a single record could itself exceed the threshold) but at least stops the loop from
   silently repeating forever.

Either option needs a decision on which typed branches are common enough to prioritize (this
transcript hit `SymbolRelationshipResultList`; `TextSearchMatchList` and `ProjectInfoList` are
likely equally exposed given large solutions) before implementation.

Once resolved, move this file to `docs/current/CLOSED.md` (or `docs/obsolete/blockers/`, per the
convention used elsewhere in this folder).
