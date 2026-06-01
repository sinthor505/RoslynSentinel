# Spec — Migration Scan Summary Patch (post-v2)
<!-- spec-migration-scan-summary-patch-v1.md / v1 -->
**Target server:** RoslynSentinel (rhale78)
**Date:** 2026-05-31
**Depends on:** spec-migration-scan-result-handling-v2.md fully implemented and passing
**Motivated by:** MCP tool usage feedback session (May 31, 2026) — post-v2 agent test identified
naming confusion, missing actionable detail in summary, and two missing fields.

---

## 0. Read this first (implementing agent)

Five independent changes. None depend on each other — implement in any order. All are small and
contained to existing types or single tool methods.

**Anti-circling instructions:**

- **P3 (`<0` bucket):** Do not remove or keep the bucket without first checking the scoring floor
  in `FlagCandidatesInProjectAsync`. The decision depends on what you find. See §P3 for the exact
  decision tree.

- **P1 (`byClass` restructure):** The value type of `ByClass` changes from `Dictionary<string,
  int>` to `List<ClassCandidateSummary>`. This is a breaking change to the `MigrationScanSummary`
  shape. Any caller parsing the old flat map will break. `asyncify-agents-v3/` is confirmed absent
  (C4 from the v2 plan), so there are no known callers to update — but confirm nothing else in the
  workspace deserializes `MigrationScanSummary.ByProject` before landing.

- **P2 (Unicode escape):** Pick one fix approach and apply it consistently to all MCP tool
  serialization, not just the scan summary. If `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` is
  already set somewhere in the server's serializer config, you're already done — check before
  adding it again.

- **P5 (deferred loading):** This is a description-only change. Do not modify any loading,
  registration, or startup logic.

---

## P1 — `byProject` → `byClass` rename + structured tuples

**File:** `RoslynSentinel.Server/MigrationEnvelope.cs`

### Problem
`MigrationScanSummary.ByProject` keys are class/form names, not .csproj project names. The name
is wrong. A flat count map also gives no way to locate the class without a follow-up query.

### Fix
1. Rename the field from `ByProject` to `ByClass`.
2. Change the value type from `Dictionary<string, int>` to `List<ClassCandidateSummary>`.
3. Add new type to `MigrationEnvelope.cs`:

```csharp
public sealed class ClassCandidateSummary
{
    public string ClassName { get; set; }
    public string ProjectName { get; set; }  // .csproj name, not full path
    public string FilePath { get; set; }     // absolute path to the source file
    public int Count { get; set; }
}
```

4. In `FindMigrationCandidatesAsync` (or the summarize aggregation path), populate
   `ClassCandidateSummary` by grouping candidates by their source document. `ProjectName` comes
   from the containing `Project.Name` in the Roslyn workspace; `FilePath` comes from
   `Document.FilePath`.
5. Sort `ByClass` descending by `Count` so the highest-concentration classes appear first.
6. Update the `[Description(...)]` on `scan_migration_candidates` to reflect the new field name
   and type.

---

## P2 — Unicode escape in bucket key `"76\u002B"`

**File:** `RoslynSentinel.Server/` — wherever MCP tool responses are serialized

### Problem
`System.Text.Json` escapes `+` as `\u002B` by default. The `"76+"` bucket key renders as
`"76\u002B"` in raw JSON output. Technically valid but visually confusing in agent output.

### Fix — pick one approach, apply consistently

**Option A (preferred):** Set `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` on the
`JsonSerializerOptions` used for MCP tool responses. This stops unnecessary escaping of safe
printable ASCII characters across all tool output, not just this bucket.

```csharp
new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    // ... other existing options
}
```

**Option B (simpler, narrower):** Rename the bucket key from `"76+"` to `"76plus"`. No serializer
change needed. Update the key string in the aggregation code and in any documentation or test
assertions that reference it.

Check whether `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` is already set in the server's
serializer configuration before adding it. If it is, this is already fixed — verify and close.

---

## P3 — `<0` score bucket: confirm then act

**File:** `RoslynSentinel.Server/AsyncOptimizationEngine.cs` — `FlagCandidatesInProjectAsync`

### Problem
The `<0` bucket always shows 0. Either negative scores are theoretically possible but absent from
this codebase, or the scoring logic floors at 0 and the bucket can never fire.

### Decision tree — follow this exactly, do not guess

1. Find the scoring logic in `FlagCandidatesInProjectAsync`. Check whether any scoring path can
   produce a negative result (the original spec noted Virtual: −20 as a theoretical deduction).
2. **If negative scores are possible** (scoring path exists that produces < 0): keep the `<0`
   bucket. Add a one-line note to the `[Description(...)]` on `scan_migration_candidates`:
   *"The '<0' bucket is valid but rare; it applies to virtual methods with low async affinity."*
   (or similar — use the actual condition from the code.)
3. **If the scoring logic floors at 0** (no code path can produce a negative score): remove `<0`
   from the bucket keys. Update the bucket aggregation, the `MigrationScanSummary` documentation,
   and T1 in `MigrationScanResultTests.cs` (which asserts all 5 bucket keys are present — adjust
   to 4 if removing).

Do not leave a bucket in the schema that can never fire.

---

## P4 — `topN` / `minScore` on `summarize=true`

**Files:** `RoslynSentinel.Server/MigrationEnvelope.cs`,
`RoslynSentinel.Server/AsyncOptimizationEngine.cs`,
`RoslynSentinel.Server/SentinelQualityTools.cs`

### Problem
`summarize=true` returns counts but not which specific methods to act on. The most common
follow-up is "show me the top N high-confidence targets" — currently requires a second call with
`summarize=false` and client-side filtering.

### Fix

1. Add optional parameters to `ScanMigrationCandidates` (and pass through to engine):
   - `int? topN = null` — return the top N candidates by score when `summarize=true`
   - `int? minScore = null` — filter to candidates at or above this score threshold

2. Add `TopCandidates` to `MigrationScanSummary`:

```csharp
public sealed class MigrationScanSummary
{
    public int TotalCandidates { get; set; }
    public Dictionary<string, int> ByPattern { get; set; }
    public List<ClassCandidateSummary> ByClass { get; set; }   // per P1
    public Dictionary<string, int> ByScoreBucket { get; set; }
    public List<MigrationCandidateFinding> TopCandidates { get; set; } // null when topN not set
}
```

3. When `summarize=true` and `topN` or `minScore` is set:
   - Filter candidates to `score >= minScore` (skip if `minScore` is null)
   - Order by score descending
   - Take `topN` (skip if null — returns all that pass the score filter)
   - Populate `TopCandidates`
   - This runs **in addition to** the normal count aggregation — all existing summary fields are
     still populated

4. When `summarize=true` and neither `topN` nor `minScore` is set: `TopCandidates = null`
   (existing behaviour preserved).

5. `topN` and `minScore` are ignored when `summarize=false`. Document this in the tool
   description.

6. Update `[Description(...)]` on `scan_migration_candidates` to document both parameters with
   an example: *"e.g. topN=10, minScore=70 returns the 10 highest-scoring candidates alongside
   the summary counts in a single call."*

---

## P5 — `get_workspace_health`: add `loadedSolutionPath`

**File:** Wherever `get_workspace_health` builds its response object

### Problem
`get_workspace_health` reports `hasLoadedSolution: true/false` but not *which* solution is
loaded. An agent cannot confirm it's operating on the correct `.sln` without a follow-up query.

### Fix

1. Add `string? LoadedSolutionPath` to the workspace health response type. Null when no solution
   is loaded (`hasLoadedSolution = false`). Absolute path to the `.sln` file when loaded.
2. Populate from the workspace manager's current solution path (same source used to set
   `hasLoadedSolution`).
3. Update the `[Description(...)]` on `get_workspace_health` to document the new field.

**While in the description:** add the following sentence to the `get_workspace_health` tool
description to address agent confusion about deferred loading:

*"Call this tool directly — no prior tool_search step is required to use RoslynSentinel tools."*

This resolves the deferred-loading confusion (separate from P5 logically, but co-located in the
same description edit to avoid a sixth micro-change).

---

## Test updates

**File:** `RoslynSentinel.Tests/MigrationScanResultTests.cs`

| Test | Required update |
|---|---|
| T1 | Update to assert `ByClass` (not `ByProject`); assert `ClassCandidateSummary` fields populated; update bucket key assertions if `<0` removed (P3) |
| New: T10 | `summarize=true, topN=5, minScore=70` → `TopCandidates` has ≤ 5 items, all with `Score >= 70`, summary counts still populated |
| New: T11 | `summarize=true` without `topN`/`minScore` → `TopCandidates == null` (existing behaviour preserved) |
| New: T12 | `get_workspace_health` after `load_solution` → `LoadedSolutionPath` non-null, ends with `.sln` |

---

## Out of scope (do not pull in)

- Combined `scan_migration_candidates` + `get_async_migration_progress` health snapshot — deferred,
  complexity not justified yet.
- Any changes to `get_operation_detail` or the v2 mutation-blob path.
- Any changes to the deferred tool loading model or server startup/registration.

<!-- v1 -->
