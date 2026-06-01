# Spec — Migration Scan Result Handling & Error Surfacing
<!-- spec-migration-scan-result-handling-v2.md / v2 -->
**Target server:** RoslynSentinel (rhale78)
**Date:** 2026-05-31
**Supersedes:** spec-migration-scan-result-handling-v1.md
**Motivated by:** MCP tool usage feedback session (May 31, 2026) — agent could not consume
`scan_migration_candidates` results that spilled to file, and `get_async_migration_progress`
failed silently.

---

## Changelog v1 → v2

| Item | Change |
|---|---|
| F1 | `RoslynSentinel.Core` does not exist. All new types go in `RoslynSentinel.Server/MigrationEnvelope.cs`. |
| F2 | `ReadEnvelope` (read-tool spec) carries line/byte/truncation signals — a different concept. `MigrationResult<T>`, `ResultError`, `LargeResultInfo` must be created from scratch. Do not merge. |
| F3 | Root cause confirmed: no threshold exists in server code; the 9KB spill was the MCP client (VS Code / Copilot) writing an oversized inline response to a temp file. `summarize` + pagination are the primary fix. Server-side threshold + `get_scan_result` is the fallback. §2.3 and §3 rewritten accordingly. |
| F4 | Option B selected. Create a separate `get_scan_result` tool; leave `get_operation_detail` unchanged. §3 rewritten. |
| F5 | Do not rename `changeId`. Document `LargeResultInfo.OperationId ≡ changeId` in §1.1 and §3. |
| F6 | Breaking return-type changes to `scan_migration_candidates` and `get_async_migration_progress` accepted. Agent must verify no asyncify-agents-v3 file parses the current bare-array / report shape before landing. |
| F7 | Score range is −20 to ~115 (not 0–100). Buckets: `<0`, `0–25`, `26–50`, `51–75`, `76+`. |
| F8 | `find_migration_candidates` is a stale inventory entry. `FindMigrationCandidatesAsync` is the internal engine method name; it does not surface as a separate MCP tool. One query tool only: `ScanMigrationCandidates`. |
| §8 | Open items resolved and removed. No pre-implementation questions remain. |

---

## 0. Read this first (implementing agent)

This spec fixes a cluster of result-handling defects in the migration tooling. Do **not** treat each
item as an isolated patch — the envelope in §1 is the keystone; §2–§5 all depend on it. Implement
§1 before anything else.

**Anti-circling instructions:**

- **Project:** All new types go in `RoslynSentinel.Server`. There is no `RoslynSentinel.Core`
  project. New file: `RoslynSentinel.Server/MigrationEnvelope.cs`.

- **No envelope reuse from the read-tool spec.** `ReadEnvelope` carries file-read metadata
  (TotalLines, TruncationInfo, SliceRange). `MigrationResult<T>` carries migration scan results and
  errors. They are different concepts. Create from scratch per §1.

- **One query tool.** `scan_migration_candidates` (wrapper: `ScanMigrationCandidates` line 239,
  `SentinelQualityTools.cs`) delegates to `FindMigrationCandidatesAsync` (engine line 1781,
  `AsyncOptimizationEngine.cs`). `find_migration_candidates` is a stale inventory entry — it does
  not exist as an MCP tool. Do not search for it.

- **The 9KB spill was client-side, not server-side.** There is no existing inline-vs-file threshold
  in `scan_migration_candidates` or anywhere else in the server. The tool always returns inline.
  The spill was the MCP client (VS Code) writing an oversized inline response to a temp file the
  agent could not parse. The fix is to keep responses small by construction (`summarize`,
  pagination). The server-side threshold in §2.3 is a safety net for genuinely large results — its
  value is that *your* file path is readable as structured JSON, unlike the client's opaque text
  spill.

- **Do not set the threshold value at or near 9KB.** 9KB appears only as a regression assertion
  in T3 (§6): a ~9KB result must stay inline. The actual threshold value must be sized to
  comfortable page headroom (see §2.3). Do not anchor it to the client's spill point.

- **Do not add a general-purpose `read_file` tool.** The MCP runs in a sealed, shell-forbidden
  mode. The new `get_scan_result` tool in §3 is scoped to operation-result files only. Any path
  that is not a known operation-result file must be rejected.

- **Breaking changes are accepted.** `scan_migration_candidates` and `get_async_migration_progress`
  return-type changes will break callers parsing the current bare shapes. Before landing, confirm no
  file in `asyncify-agents-v3/` parses the current `List<MigrationCandidateFinding>` array or bare
  `AsyncMigrationProgressReport` shape. Update any that do in the same change.

- **`ScanMigrationCandidates` is currently expression-bodied.** Adding the solution guard and
  envelope wrap requires a braced method body. Convert it — this aligns with the project's C# style
  (no expression-bodied members).

---

## 1. Shared result/error envelope (keystone — implement first)

All migration tools must return through a single envelope so success, error, and large-result-pointer
cases are structurally distinguishable by the caller.

### 1.1 Envelope shape

Create `RoslynSentinel.Server/MigrationEnvelope.cs`:

```csharp
public sealed class MigrationResult<T>
{
    public bool Success { get; set; }
    public T Data { get; set; }                      // null when Success is false or result is in LargeResult
    public ResultError Error { get; set; }           // null when Success is true
    public LargeResultInfo LargeResult { get; set; } // null when result is inline
}

public sealed class ResultError
{
    public string ErrorCode { get; set; }   // see §1.2
    public string Message { get; set; }     // human-actionable; always populated on failure
    public string Detail { get; set; }      // exception message / stack summary; may be null
}

public sealed class LargeResultInfo
{
    public bool WrittenToFile { get; set; }
    public string FilePath { get; set; }    // absolute path; pass to get_scan_result
    public string OperationId { get; set; } // ≡ changeId parameter on get_scan_result (see §3, F5)
    public long SizeBytes { get; set; }
    public int? TotalRecords { get; set; }  // null if unknown at write time
}
```

### 1.2 Error codes

| ErrorCode | Meaning |
|---|---|
| `SolutionNotLoaded` | No solution loaded; caller must `load_solution` first |
| `FeatureDisabled` | Feature flag off |
| `InvalidArgument` | Bad/unresolvable parameter (e.g. `filePath` matched no documents, or non-operation-result path passed to `get_scan_result`) |
| `Exception` | Unhandled exception; `Detail` carries the exception message |

Unknown/unhandled failures must return `Success = false` with `ErrorCode = "Exception"` and a
populated `Message`/`Detail`. **No silent failures. No empty `Success = true` with a null `Data`.**

---

## 2. `scan_migration_candidates` — return-shape controls

There is no existing inline-vs-file threshold. The tool currently always returns a bare
`List<MigrationCandidateFinding>` inline. The three file-write incidents in the feedback were
client-side spills of large inline responses. The fixes below prevent responses from growing large
enough for the client to spill.

Implement in order: 2.1 → 2.2 → 2.3 → 2.4 (2.1 and 2.2 can overlap; 2.3 depends on 2.2;
2.4 depends on all of 2.1–2.3).

### 2.1 `summarize` parameter (primary fix — highest leverage)

- New optional `bool summarize = false` parameter.
- When `true`: compute and return `MigrationScanSummary` inline. Never touch the full candidate
  list. Always inline-safe regardless of solution size.
- Mirror the existing `get_diagnostics` summarize behaviour for parameter name, default, and
  response structure.

Score buckets (F7 — score range is −20 to ~115):

| Bucket key | Range |
|---|---|
| `"<0"` | score < 0 |
| `"0-25"` | 0 ≤ score ≤ 25 |
| `"26-50"` | 26 ≤ score ≤ 50 |
| `"51-75"` | 51 ≤ score ≤ 75 |
| `"76+"` | score > 75 |

```csharp
public sealed class MigrationScanSummary
{
    public int TotalCandidates { get; set; }
    public Dictionary<string, int> ByPattern { get; set; }
    public Dictionary<string, int> ByProject { get; set; }
    public Dictionary<string, int> ByScoreBucket { get; set; } // keys per table above
}
```

When `summarize = true`, the outer envelope is `MigrationResult<MigrationScanSummary>`.
When `summarize = false`, the outer envelope is `MigrationResult<List<MigrationCandidateFinding>>`.

### 2.2 `limit` / `offset` pagination

- New optional parameters: `int limit = 50`, `int offset = 0`.
- Mirror `get_comprehensive_health_report` pagination semantics exactly — same parameter names,
  same `HasMore` / `TotalRecords` contract. Do not invent a new paging shape.
- Apply `Skip(offset).Take(limit)` to the ordered candidate list.
- Always populate `TotalRecords` in the response (either in `LargeResultInfo` or alongside `Data`)
  so the caller knows how many pages remain.
- The threshold check in §2.3 applies to the **serialized page slice only**, not the full result
  set. A small page from a large result must always return inline.

### 2.3 Server-side file-write threshold (safety net — build from scratch)

Purpose: if a page still exceeds a safe inline size, write to a structured file the agent can
recover via `get_scan_result` (§3). This is the fallback, not the primary fix. Its value is
specifically that the output is **structured JSON** readable by §3, not an opaque text blob.

Implementation rules:

1. Serialize the candidate page to JSON first. Measure `byte[]` length of the **serialized
   payload**. This is the only correct measurement — not object-graph size, not pre-truncation
   field dump, not record count.
2. If serialized size ≤ threshold: set `Data`, leave `LargeResult = null`, return inline.
3. If serialized size > threshold: write JSON to
   `.roslynsentinel/operations/scan_{timestamp}_{guid}.json`, populate `LargeResultInfo`
   (`FilePath`, `OperationId = guid`, `SizeBytes`, `TotalRecords`), set `Data = null`.
4. Set threshold to a value comfortably above a typical 50-item page. Do not set it at or near
   9KB — that was the client's spill point, not a property of this server. A starting value of
   ~256KB is reasonable; make it configurable via server options if feasible.
5. The T3 regression (§6): a ~9KB result (~50–60 typical candidates) must stay inline under this
   threshold. If it does not, the measurement in step 1 is wrong — fix the measurement, not the
   threshold value.

### 2.4 Update wrapper and return type

- Change `ScanMigrationCandidates` return type to `Task<MigrationResult<...>>` (union of
  `MigrationResult<MigrationScanSummary>` and `MigrationResult<List<MigrationCandidateFinding>>`
  per `summarize` flag; an `object` return with the correct runtime type is acceptable if the MCP
  serializer handles it, otherwise use two overloads or a discriminated return type — pick the
  approach that matches existing patterns in the codebase).
- Convert expression-bodied method to braced body.
- Add solution-loaded guard at the top: if no solution loaded, return
  `ErrorCode = "SolutionNotLoaded"` immediately.
- Wrap `FindMigrationCandidatesAsync` call in try/catch; map exceptions to
  `ErrorCode = "Exception"` per §1.2.

---

## 3. `get_scan_result` — new tool for file-written scan results (F4: Option B)

`get_operation_detail` returns `OperationItemRecord[]` (mutation blobs). Extending it to return
`MigrationCandidateFinding[]` is a type conflict. A separate tool keeps both contracts clean.

### 3.1 New tool

```csharp
[McpServerTool]
[Description("""
    Reads a scan_migration_candidates result that was written to file because it exceeded
    the inline size threshold. Pass the FilePath or OperationId (≡ changeId) returned in
    LargeResultInfo from a prior scan_migration_candidates call.

    changeId: the OperationId value from LargeResultInfo (a GUID).
    filePath: alternatively, pass the absolute FilePath from LargeResultInfo directly.
    limit:    page size (default 50).
    offset:   zero-based page start (default 0).

    Returns MigrationResult<List<MigrationCandidateFinding>> with the same paging contract
    as scan_migration_candidates.
    """)]
public async Task<MigrationResult<List<MigrationCandidateFinding>>> GetScanResult(
    string changeId    = null,
    string filePath    = null,
    int    limit       = 50,
    int    offset      = 0)
```

### 3.2 Implementation rules

- Resolve the file: if `changeId` is supplied, look up the `.roslynsentinel/operations/scan_{*}_{changeId}.json`
  path; if `filePath` is supplied directly, verify it matches the `scan_` prefix pattern.
- Reject any path that does not match a known operation-result file with
  `ErrorCode = "InvalidArgument"`. Do not resolve arbitrary workspace paths.
- Deserialize as `List<MigrationCandidateFinding>`. Apply `Skip(offset).Take(limit)`.
- Return `MigrationResult<List<MigrationCandidateFinding>>` with `TotalRecords` populated.
- On missing file or bad GUID: `ErrorCode = "InvalidArgument"`, message stating what was not found.

### 3.3 F5 note

`LargeResultInfo.OperationId` and the `changeId` parameter on `get_scan_result` are the same
value (the GUID component of the scan file name). The naming difference is intentional:
`OperationId` is the envelope-side name; `changeId` matches the existing convention on
`get_operation_detail` for familiarity. Do not rename `changeId`.

---

## 4. `scan_migration_candidates` — `filePath` matching bug

Reported: doc says "full or partial path suffix" but a full absolute path silently returns `[]`
while a filename-only value returns correct results.

The contract is correct — a full path is a valid suffix of itself. The matcher is broken.

1. Find the `filePath` matching logic in `FindMigrationCandidatesAsync`
   (`AsyncOptimizationEngine.cs` line ~1803).
2. Check in order:
   - Path separator mismatch (`\` vs `/`) between stored document path and the input.
   - Case sensitivity on drive letter or segments (Windows paths are case-insensitive).
   - A leading-segment anchor that prevents matching a full path as a suffix.
3. Normalize both sides before comparison: consistent separators, `OrdinalIgnoreCase` on Windows.
   Perform a genuine suffix match after normalization.
4. When a supplied `filePath` resolves to zero documents: return
   `ErrorCode = "InvalidArgument"` with a clear message — **never silent empty success**.

---

## 5. `get_async_migration_progress` — surface the failure

Reported: failed silently with no code, no detail. Agent abandoned it.

1. Locate `GetAsyncMigrationProgressAsync` (`AntiPatternEngine.cs` line ~2723).
2. Add solution-loaded guard: if no solution → return `ErrorCode = "SolutionNotLoaded"`.
3. Wrap body in try/catch: any throw → `ErrorCode = "Exception"`, `Detail = ex.Message`.
4. Change `GetAsyncMigrationProgress` wrapper return type to
   `Task<MigrationResult<AsyncMigrationProgressReport>>`.
5. **No silent failures.** The previous "An error occurred" with no code is not acceptable.

---

## 6. Test cases (load-bearing — do not simplify away)

File: `RoslynSentinel.Tests/MigrationScanResultTests.cs`
Pattern: NUnit `[TestFixture]` + `TestSolutionBuilder.CreateSolutionWithProject()` per existing
test conventions.

| # | Case | Assertion |
|---|---|---|
| T1 | `summarize=true`, solution-wide | Inline; `MigrationScanSummary` populated; all five bucket keys present; `LargeResult == null` |
| T2 | Paged scan, page fits inline | `Data` non-null; `TotalRecords` set; `LargeResult == null` even if total result set is large |
| T3 | ~9KB-equivalent result, no pagination | `Data` non-null, `LargeResult == null` — confirms threshold does not trip at 9KB; fails if serialized-byte measurement is wrong |
| T4 | Result genuinely exceeds threshold | `LargeResult.WrittenToFile == true`; `FilePath` and `OperationId` populated; `Data == null` |
| T5 | `get_scan_result` on T4's spilled file | Returns `List<MigrationCandidateFinding>` (structured records, not preview text); paging works; `TotalRecords` matches T4 |
| T6 | Full absolute `filePath` in scan | Returns same records as filename-only input |
| T7 | `filePath` matching nothing | `Success == false`; `ErrorCode == "InvalidArgument"`; not empty success |
| T8 | `get_async_migration_progress`, no solution loaded | `Success == false`; `ErrorCode == "SolutionNotLoaded"` |
| T9 | `get_async_migration_progress`, forced exception | `Success == false`; `ErrorCode == "Exception"`; `Detail` non-empty |

T3 is the regression anchor for the threshold measurement. If T3 fails (9KB result goes to file),
the measurement in §2.3 step 1 is wrong — fix the measurement, not the threshold value.

---

## 7. Out of scope (do not pull in)

- General `read_file` / `get_file_content` for arbitrary paths — see §0.
- `get_method_source` / `search_solution_text` additions — tracked in `roslyn-sentinel-tools-review-v1.md`.
- File-read metadata envelope for source files — tracked in `spec-read-tool-metadata-envelope-v1.md`.
- `get_operation_detail` changes — leave it unchanged (F4 Option B decision).

---

## 8. Implementation order summary

```
Phase 1  §1      Create MigrationEnvelope.cs (keystone — all phases depend on this)
Phase 2  §4      Fix filePath matching in FindMigrationCandidatesAsync (~line 1803)
         §2.1    Add summarize parameter + MigrationScanSummary
         §2.2    Add limit/offset pagination          } can overlap
         §2.3    Build inline-vs-file threshold
         §2.4    Update ScanMigrationCandidates wrapper + return type
Phase 3  §3      Create get_scan_result tool (depends on §1, §2.3)
Phase 4  §5      Fix get_async_migration_progress error surfacing
Phase 5  §6      Write all 9 test cases
```

Pre-landing checklist:
- [ ] Confirmed no asyncify-agents-v3 file parses current bare `List<MigrationCandidateFinding>`
      or `AsyncMigrationProgressReport` shape (F6).
- [ ] All 9 T-cases pass with `dotnet test`.
- [ ] T3 passes inline (threshold measurement validated).
- [ ] T6 passes (full absolute path = filename-only result).
- [ ] JSON shape spot-checked: `{ "success": bool, "data": ..., "error": null/..., "largeResult": null/... }`.

<!-- v2 -->
