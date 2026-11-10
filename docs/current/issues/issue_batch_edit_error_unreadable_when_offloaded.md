# A failed ReplaceSnippet batch returns an error nobody can read - OPEN

**Status:** OPEN 2026-11-09. Cause traced in source; fix decided in the todo review 2026-11-09 (READY), not built.

## Summary
A 15-item `ReplaceSnippet` `batchEdits` exact-match failure returned `isError` with the body offloaded to a
35 KB large result, and `GetLargeResult` refused to page it ("stored whole; no array or multi-line string
to page"). A Haiku implementer could not see why the batch failed (cause: continuation-line indentation
repeated in every item).

## Why it matters
An agent cannot recover from an error it cannot read; it retries blind or escalates.

## What is known
Traced in source; the stored 35 KB file was not inspected.
- Each failing item's message (echoed snippet plus up to five closest windows) is capped at
  `MaxMessageChars = 6000` (`SnippetErrorFormatter.cs` ~49).
- `ReplaceSnippetBatch` (`WorkspaceFileEditImpl.cs` ~981) joins all per-item messages with no aggregate
  cap: 15 items at roughly 2.3 KB each is about 35 KB (batch limit 20, ~660).
- `ResultPager` pins `errorData.message` and `errorCode` (never cut, `ResultPager.cs` ~21-24, exemption
  ~292). With nothing cuttable, the backstop `AddLargeResultOffloadFilter` (15360 chars) stores the whole
  body as an Envelope.
- `GetLargeResult`'s page-path walker also skips `errorData.message`, hence "stored whole"
  (`WorkspaceReadNavigationImpl.cs` ~1733).
- Per `reference_tool_response_pipeline.md`, tools never trim results. A cap on the batch message is
  message design (a bounded diagnostic), not response trimming.
- "Never offload errors" was rejected: it pushes 35 KB inline and breaks the budget invariant.

## Decisions (todo review 2026-11-09)
- Fix both ends (READY, Small-Medium):
  - (a) Source: `ReplaceSnippetBatch` gives full detail for the first 3 failures and one line per
    remaining failing index, under a total cap of about 8-10K chars. Check the other batch tools that
    join per-edit errors for the same pattern.
  - (b) Reader: `GetLargeResult` pages a whole-stored `errorData.message` by lines when no other path is
    pageable. `Fit` keeps the pin.

## What would resolve it
- A `Tests.Tools.Basic` test with 15 non-matching batch edits: the message stays under 12,800 chars and
  names every failing index.
- A paging test: an Envelope holding a 30 KB `errorData.message` gets a page from `GetLargeResult`.
