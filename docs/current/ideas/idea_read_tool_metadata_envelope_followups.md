# Read-tool metadata envelope (isComplete / truncation flag) - remaining tools

**Status:** NOT DESIGNED 2026-10-17. GetMethodSource and GetFileOutline were done 2026-08-27; the remaining read tools are open. The original spec moved to docs/obsolete/specs.

## What happened
Moved verbatim from the restructured TODO.md.

## Moved from TODO.md (2026-10-17)

### Read-tool metadata envelope (`isComplete`/truncation flag) — `GetMethodSource`/`GetFileOutline` done 2026-08-27

**Found:** 2026-08-24, while auditing `docs/spec-read-tool-metadata-envelope-v1.md` (kept in
`docs/current/`) for the docs reorganization pass. Zero `isComplete`/`IsTruncated`-style fields exist
anywhere in the codebase — the spec's proposed metadata envelope for read tools (so a caller can tell
whether a returned excerpt is the whole thing or was cut short) has not been started.

**Implemented 2026-08-27** (spec's steps 1–3): added `ReadEnvelope`
(`RoslynSentinel.Common/ReadEnvelope.cs`) — `SchemaVersion`, `LineCount`/`ByteCount` (total file, not
slice), `IsComplete`, `ReturnedFromLine`/`ReturnedToLine`, `ContinuationOffset`, `OutlineAvailable` —
plus `ReadEnvelopeBuilder.Build`/`BuildForWholeFile` and configurable `ReadEnvelopeThresholds`
(`ReadWholeMaxLines`=800, `OutlineAvailableMinLines`=400, `MaxReturnedLines`=1200, matching the spec's
suggested defaults). Wired into:
- `GetMethodSource` — `MethodSourceResult` gained an `Envelope` property; the envelope describes the
  **containing file's** scope while `ReturnedFromLine`/`ReturnedToLine` are the method's own line span
  (per spec, so a caller gets file-level scope even when it only asked for one method).
- `GetFileOutline` — return shape changed from a bare `List<OutlineItem>` to a new `FileOutlineResult
  { Envelope, Symbols }` record (this is a breaking shape change to `Data`; the one existing test
  asserting the old bare-list shape was updated).

**Steps 4–5 resolved 2026-08-27, no code change:**
- Step 4 (configurable thresholds) — decided the existing `ReadEnvelopeThresholds` static settable
  properties (`ReadWholeMaxLines`/`OutlineAvailableMinLines`/`MaxReturnedLines`) already *are* the
  tuning surface; a `read-limits.json`/env-var loader was judged unnecessary since nothing else in the
  repo loads per-feature JSON config at startup, and this matches the existing precedent
  (`ScanResultHelper.ThresholdBytes` is likewise a hardcoded constant, not file/env-configurable).
- Step 5 (fold the contract into "the tools-review doc") — that doc is `docs/obsolete/tool_review_v1.md`
  (where `BatchResultSummary` is documented), already retired to `obsolete/`. No live successor exists
  to fold this into; `tool-terminology-refinement-reference-v1.md` is a different, naming-focused doc,
  not a shared-contract catalog. Decided this TODO.md entry plus `ReadEnvelope.cs`'s own doc comments
  are the durable record — no new doc created.

`Read` File was left unwired: it already has its own startLine/endLine slicing and inline
`filePath/startLine/endLine/totalLines/source` shape; folding it into `ReadEnvelope` would touch three
response shapes (whole-file, ranged, offloaded) and wasn't part of this pass's scope. `MethodNotFound`
also still returns a plain error rather than `MethodFound=false` + populated envelope, since
`GetMethodSource`'s result type has no `MethodFound` field today — a further contract change, not
attempted here.

Verified via `dotnet build RoslynSentinel.slnx` (0 errors) and the full test suite across all 5 test
projects (0 new failures): new `ReadEnvelopeBuilderTests` (8 tests, spec's "BuildEnvelope helper" list)
plus envelope assertions added to `GetMethodSourceTests` and the `GetFileOutline` enum test.

**Remaining work if picked up again:** wire `ReadFile` into the envelope, and decide `MethodNotFound`'s
contract (`MethodFound` field + populated envelope instead of a plain error). Steps 4–5 are closed, not
just deferred.
