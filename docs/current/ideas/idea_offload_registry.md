# Idea: Bounded in-memory registry of offloaded results, keyed by resultId

**Status:** NOT DESIGNED 2026-11-26. Raised by the owner during plan-design for plan_inline_budget_classes (Q29).

## What happened

Tuning the inline budget for `plan_inline_budget_classes` needed a scan of 15,889 eval transcripts
(`<scratch root>\plan_design_inline_budget_classes\Find-OffloadedResults.ps1`, promoted to
`scripts/Find-OffloadedResults.ps1` by that plan) to learn which tools get offloaded, how big the results are,
and whether the model fetches them. Among its findings:
- 24 compile-gate rejection bodies were cut, and 0 of them were fetched with `GetLargeResult`.
- Search results at 21.9K chars were offloaded, and the model fetched the next page only 22% of the time. The rest
  of the time it searched again instead.
- About 2,700 detached payloads were never fetched, which is by design.

The server records none of this live. Each answer needs an offline transcript scan.

## The actual gap

The server forgets an offload as soon as the pointer goes out. Nothing connects a stored `resultId` to the later
`GetLargeResult` call that reads it, or to a repeated call that makes it redundant. As a result:
- per-tool fetch rates and sizes are not visible in `McpServerStatus` during a session;
- a model that repeats an identical call instead of paging gets no fact pointing it at the stored result.

## Possible fixes (not decided)

- **Registry shape.** A keyed, bounded store: a `ConcurrentDictionary<string, OffloadRecord>` plus a
  `ConcurrentQueue<string>` of keys, evicting the oldest past about 100 entries; or a small locked LRU. The cap is
  the cleanup.
  - A `Channel` with `BoundedChannelFullMode.DropOldest` was considered and rejected: it is a FIFO queue with no
    lookup by key.
  - Each record holds tool name, argument hash, size, offload kind (sequence, cut, detached, backstop), time, and a
    fetched flag.
- **Use 1: live offload telemetry (most value).** Record each offload, and mark it fetched when `GetLargeResult`
  names its id. Show per-tool counts, fetch rate and median size in `McpServerStatus`. This is the Q17 scan done
  live.
  - Overlap to check first: the tool telemetry collector already counts `guidanceIssued`/`guidanceFollowed` per tool
    (`McpServerStatus.toolTelemetry`; the marshaller forwards `GuidanceHint` to it). An offload pointer may fit as one
    more guidance kind, with no new store. Not verified.
- **Use 2: repeated-call fact.** When a tool is called again with the same argument hash while an unfetched offload
  from it is still registered, add the fact "an earlier identical call's result is stored as resultId X" to the
  response. It is a fact and an option, not policy. Measure with use 1 first to see how often it happens.
- **Use 3 (low value): unread failure.** Flag an edit that follows an unfetched cut error. `plan_inline_budget_classes`
  keeps error-only bodies inline up to the `Whole` threshold, so this mostly goes away.
- **Use 4 (marginal): page from memory** instead of re-reading the envelope file in `GetLargeResult`.
- **Not a use: loop prevention.** The offload filter needs per-call knowledge, so a registry keyed by result does not
  help. That stays with `OversizeUnitSignal` (Q29 in the plan).

## Related

- `docs/current/plans/closed/plan_inline_budget_classes.md` (Q17 scan, Q25, Q29)
- `docs/current/references/reference_tool_response_pipeline.md` (marshaller, `GetLargeResult`, filters)
- `docs/current/ideas/idea_tool_attribution.md` (another per-call telemetry idea)
