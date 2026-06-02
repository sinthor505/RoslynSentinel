# Spec — Migration Scan Summary Patch (post-v2)
<!-- spec-migration-scan-summary-patch-v1.md / v7 -->
**Target server:** RoslynSentinel (rhale78)
**Date:** 2026-05-31
**Supersedes:** spec-migration-scan-summary-patch-v1.md / v6
**Depends on:** spec-migration-scan-result-handling-v2.md fully implemented and passing
**Motivated by:** Six MCP tool usage feedback sessions (May 31, 2026):
- Session 1 (post-v2): naming confusion, missing actionable detail in summary, two missing fields → §P1–P5
- Session 2 (post-patch): summarize ordering bug, SyntaxTree compilation error, unjustified
  solution-load gate, inconsistent score reason format, opaque async_migrate errors → §B1–B6
- Session 3 (post-B fixes): B2 root cause refined (designer files — later superseded), minScore
  ignored in summarize path → B2 updated, §B7 added
- Session 4: VS Code offload distinction clarified; minScore also ignored in paged mode → §B7b added
- Session 5: B1 root cause confirmed (byClass bloat + ordering), B2 root cause corrected
  (cross-project compilation in FindObsoleteCallersAsync), Bug 3 threshold confirmed → B1/B2
  updated; v2 spec §2.3 threshold updated to 30KB
- Session 6: VS Code intercept threshold confirmed ~10KB (not ~64KB); fat TopCandidates
  identified as third cause of B1; describe_tool_options registry incomplete; empty response
  semantics gap → B1 rewritten with slim types + 5-fix structure; B8/B9 pending

---

## 0. Read this first (implementing agent)

This document has two sections:

**§P1–P5 — Summary enhancements** (from session 1): five independent changes, implement in any
order.

**§B1–B7b — Bug fixes** (from sessions 2–4): B1 and B2 are critical path and should be
implemented first. B3–B7b are independent of each other and of B1/B2.

**Cross-references to other specs:**
- `get_scan_result` tool (the paging tool for offloaded results) is fully specced in
  `spec-migration-scan-result-handling-v2.md §3`. It is not re-specced here. If it is not yet
  implemented, implement it from that spec alongside B1 — B1's summarize fix and `get_scan_result`
  together close the two critical gaps from session 2.
- `describe_tool_options` tool is fully specced in `spec-tool-description-compression-v1.md §1`.
  It is not re-specced here. If it is not yet implemented, implement it from that spec. Do not
  add references to `describe_tool_options` in tool descriptions until the tool itself is
  implemented and available — agents in sessions without it will hit a dead end. Sequence:
  implement the tool first, then add description cross-references.
- `async_migrate` opaque error (§B5) uses the same `MigrationResult<T>` envelope and error code
  pattern specced in `spec-migration-scan-result-handling-v2.md §1`. Read that section before
  implementing B5.

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

- **B1 (summarize ordering):** The fix is an ordering guard only — do not restructure the
  threshold or pagination logic. See §B1 for the exact insertion point.

- **B2 (SyntaxTree error):** Root cause is cross-project compilation mismatch in
  `FindObsoleteCallersAsync` — always get the compilation from the **referencing document's
  project**, not the declaring project. Do not look for designer files or generated files as the
  cause. See §B2 for the fix pattern.

- **B4 (score reason format):** Standardize to structured format only. Do not add a free-text
  fallback — if a reason cannot be expressed as key:points pairs, that is a gap in the scoring
  model to fix, not a reason to preserve mixed formats.

- **B7 (minScore filter):** The fix is a one-line filter addition in `BuildScanSummaryAsync`
  before aggregation. Do not restructure the summarize path. See §B7.

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

## Out of scope — §P1–P5

- Combined `scan_migration_candidates` + `get_async_migration_progress` health snapshot — deferred,
  complexity not justified yet.
- Any changes to `get_operation_detail` or the v2 mutation-blob path.
- Any changes to the deferred tool loading model or server startup/registration.

---

## §B — Bug fixes (session 2, 2026-05-31)

B1 and B2 are critical path. B3, B4, B5 are independent of each other and of B1/B2.

---

## B1 — `summarize=true` not inline-safe: ordering bug + byClass bloat + fat TopCandidates (critical)

**Files:** `RoslynSentinel.Server/SentinelQualityTools.cs`,
`RoslynSentinel.Server/AsyncOptimizationEngine.cs`,
`RoslynSentinel.Server/MigrationEnvelope.cs`

### Problem
`summarize=true` consistently produces 11–82KB responses that VS Code intercepts and writes to
a session-resources path the agent cannot reach. Three compounding causes confirmed across
sessions 2–6:

1. **Ordering bug:** The threshold check ran before the summarize branch, serializing all
   candidates before aggregation. The early-return guard (below) fixes this, but is not
   sufficient alone — the summary itself can still exceed VS Code's ~10KB intercept threshold.
2. **`byClass` bloat:** Unbounded `ClassCandidateSummary` entries (each with `FilePath`) inflate
   the summary to 11KB+ on large projects regardless of `topN`. 20-entry cap was insufficient
   because `FilePath` alone adds significant per-entry cost.
3. **Fat `TopCandidates`:** Each `MigrationCandidateFinding` entry carries `FilePath`,
   `FlaggedDate`, `Line`, full `Reason` breakdown, and caller lists. 10 full entries adds
   several KB to the summary.

The fix requires all three changes. Ordering guard alone is not enough.

### Fix 1 — Ordering guard (SentinelQualityTools.cs)

Add an early-return guard at the top of `ScanMigrationCandidates` before any threshold logic:

```csharp
// summarize path: always inline, never touches threshold
if (summarize)
{
    var summary = await BuildScanSummaryAsync(filePath, projectName, pattern, topN, minScore);
    return new MigrationResult<MigrationScanSummary> { Success = true, Data = summary };
}
// candidates path: threshold logic follows here
```

### Fix 2 — Slim types (MigrationEnvelope.cs)

Replace the full types in `MigrationScanSummary` with compact slim types that omit
high-cost fields:

**Add `ClassCandidateSummarySlim`** — replaces `ClassCandidateSummary` in summary context:

```csharp
public sealed record ClassCandidateSummarySlim(
    string ClassName,
    string ProjectName,
    int    Count);
// No FilePath — omitted to control size
```

**Add `TopCandidateSummaryEntry`** — replaces `MigrationCandidateFinding` in summary context:

```csharp
public sealed record TopCandidateSummaryEntry(
    string MethodName,
    string ClassName,
    string Pattern,
    int    Score,
    string Summary);  // truncated to 120 chars; omits FilePath, FlaggedDate, Line, Reason
```

**Update `MigrationScanSummary`** to use the slim types:

```csharp
public sealed class MigrationScanSummary
{
    public int TotalCandidates { get; set; }
    public Dictionary<string, int> ByPattern { get; set; }
    public List<ClassCandidateSummarySlim> ByClass { get; set; }  // slim — no FilePath
    public bool ByClassTruncated { get; set; }
    public Dictionary<string, int> ByScoreBucket { get; set; }
    public List<TopCandidateSummaryEntry> TopCandidates { get; set; } // slim — 120-char summary
}
```

Do not modify `ClassCandidateSummary` or `MigrationCandidateFinding` — they are used in the
paged (non-summary) path and must remain unchanged.

### Fix 3 — Tighter caps (SentinelQualityTools.cs / BuildScanSummaryAsync)

```csharp
const int MaxByClass       = 10;  // reduced from 20; slim type makes each entry cheaper
const int MaxTopCandidates =  5;  // hard cap; silently clamp topN > 5 to 5
```

Cap `ByClass` at 10 slim entries. Cap `TopCandidates` at 5 slim entries regardless of the
`topN` parameter value — `topN` is a hint, not a contract, for the summary path. Callers
requesting `topN > 5` get 5. Document this in the tool description.

```csharp
var allByClass = BuildByClass(candidates); // sorted descending by Count
summary.ByClass          = allByClass.Take(MaxByClass).ToList();
summary.ByClassTruncated = allByClass.Count > MaxByClass;

var effectiveTopN = Math.Min(topN ?? MaxTopCandidates, MaxTopCandidates);
summary.TopCandidates = candidates
    .OrderByDescending(c => c.Score)
    .Take(effectiveTopN)
    .Select(c => new TopCandidateSummaryEntry(
        c.MethodName,
        c.ClassName,
        c.Pattern,
        c.Score,
        (c.Summary ?? string.Empty)[..Math.Min(120, (c.Summary ?? string.Empty).Length)]))
    .ToList();
```

### Fix 4 — 8KB overflow safety net (SentinelQualityTools.cs)

After building `MigrationScanSummary`, serialize and check size. This should be unreachable
with the slim types and caps above, but acts as a safety net:

```csharp
var summaryJson  = JsonSerializer.SerializeToUtf8Bytes(summary);
if (summaryJson.Length > 8 * 1024)  // 8KB — use the SAME constant as the paged threshold
{
    // write to operations/ dir, return LargeResultInfo with OperationId
    // same path as the paged candidates overflow
}
```

**Use the same threshold constant as the paged path.** Do not introduce a second hardcoded
value. If the paged threshold constant is named `ThresholdBytes`, use `ThresholdBytes` here
(after updating it to 8KB per the threshold fix). Do not hardcode `8 * 1024` in two places.

### Fix 5 — Description update (scan_migration_candidates [Description])

Replace "always inline-safe" with:

```
summarize=true returns a guaranteed ≤2KB dashboard view. byClass is capped at 10 entries
(highest-count classes); ByClassTruncated=true when truncated. TopCandidates is capped at 5
entries regardless of topN. To get full candidate records, use summarize=false with limit/offset.
```

### Verification
- `summarize=true` on AvaalExpress (986 candidates) returns inline, well under VS Code's ~10KB
  intercept threshold. Serialized byte size of the response must be ≤ 2,048 bytes.
- T1, T10, T11 must all pass inline.
- `summarize=false` paged results are unaffected — `ClassCandidateSummary` and
  `MigrationCandidateFinding` in the paged path must not be modified.

---

## B2 — `get_async_migration_progress` SyntaxTree compilation error (critical)

**File:** `RoslynSentinel.Server/AntiPatternEngine.cs` — `FindObsoleteCallersAsync`

### Problem
After a successful `load_solution`, `get_async_migration_progress` throws:
`"SyntaxTree is not part of the compilation (Parameter 'syntaxTree')"`
Consistent across null scope and all project-scoped calls.

### Diagnosis (confirmed from source — session 5)
The crash is in `FindObsoleteCallersAsync`. `SymbolFinder.FindReferencesAsync` returns reference
locations that span **multiple projects** — a bridge method declared in `AvaalExpress` may be
called from `DispatchBase`, `Avaal.Common`, etc. The code was calling
`compilation.GetSemanticModel(refSyntaxTree)` using the **declaring project's compilation**
for every reference location, including those in other projects. A syntax tree from project B
is not part of project A's compilation — Roslyn throws.

This is not a generated-file or designer-file issue. The trees are valid and current; the
compilation object is simply wrong for cross-project references.

### Fix (implemented)
In `FindObsoleteCallersAsync`, for each reference location: get the document containing the
reference, look up **that document's project**, get that project's compilation, and guard with
`ContainsSyntaxTree` at both the project level and the compilation level before calling
`GetSemanticModel`:

```csharp
foreach (var referencedSymbol in references)
{
    foreach (var location in referencedSymbol.Locations)
    {
        var refDocument = solution.GetDocument(location.Document.Id);
        if (refDocument == null) { continue; }

        // Get the compilation for the referencing document's project —
        // NOT the declaring project's compilation
        var refProject = refDocument.Project;
        var refCompilation = await refProject.GetCompilationAsync(cancellationToken);
        if (refCompilation == null) { continue; }

        var refSyntaxTree = await refDocument.GetSyntaxTreeAsync(cancellationToken);
        if (refSyntaxTree == null || !refCompilation.ContainsSyntaxTree(refSyntaxTree))
        {
            continue; // excluded document — skip
        }

        var model = refCompilation.GetSemanticModel(refSyntaxTree); // safe
        // ... analysis
    }
}
```

The key invariant: **always get the compilation from the same project as the document**.
`ContainsSyntaxTree` at both levels is the safety net for any edge cases (generated files,
excluded documents) but the primary fix is the project lookup.

### Additional: `projectName` scope parameter
Already implemented as part of this fix. When `projectName` is supplied, restrict analysis to
that project only. When null, analyze the full solution.

### Test
- T8 (existing): `get_async_migration_progress` with no solution → `ErrorCode = "SolutionNotLoaded"`
- New T13: `get_async_migration_progress` after `load_solution` on a multi-project solution →
  no exception, returns `AsyncMigrationProgressReport` with non-null fields
- New T14: `get_async_migration_progress(projectName: "AvaalExpress")` → scoped report, no
  exception

---

## B3 — `project_doc` unjustified solution-load gate

**File:** Wherever `project_doc` read operations check workspace state

### Problem
`project_doc` read operations return `"No solution is loaded"` when reading state files that are
purely filesystem-based — no Roslyn workspace is required. The guard is applied uniformly to all
`project_doc` operations when it should only apply to operations that actually require a loaded
workspace.

### Fix
1. Find the solution-loaded guard in the `project_doc` handler.
2. Identify which operations require Roslyn workspace state (semantic queries, compilation-
   dependent reads) and which are filesystem-only (reading state files, config, index files).
3. Remove the solution-loaded guard from filesystem-only operations. Keep it only on operations
   that genuinely require a loaded workspace.
4. If the operations are not easily distinguishable, add an `requiresWorkspace` flag or a
   separate code path for filesystem reads.

### Test
- New T15: `project_doc` read of a filesystem state file with no solution loaded → succeeds,
  returns file content (not `"No solution is loaded"`)

---

## B4 — Score reason format inconsistency

**Files:** `RoslynSentinel.Server/AsyncOptimizationEngine.cs` — scoring logic in
`FlagCandidatesInProjectAsync` and related methods

### Problem
`Reason` field in `MigrationCandidateFinding` is inconsistently formatted:
- Some candidates: structured component breakdown (`calls-CommonSearch:30 service-class:15 static:5`)
- Other candidates: free-text narrative (`"4 UI callers; CC=50 — too complex to bridge directly"`)

Mixed formats prevent programmatic parsing and make agent reasoning harder.

### Fix
Standardize to structured `key:points` pairs across all scoring paths. Format:

```
component-name:score component-name:score ...
```

Examples:
- `calls-CommonSearch:30 service-class:15 static-method:5`
- `ui-callers:20 cyclomatic-complexity-penalty:-15 no-async-overload:10`

Rules:
1. Every scoring component that contributes to the final score must appear as a `key:points` pair.
2. Penalty components use negative values (e.g. `complexity-penalty:-15`).
3. Do not use narrative prose anywhere in the `Reason` field.
4. If a condition is relevant but scores 0 (e.g. "too complex to bridge" is a flag, not a score
   deduction), add a zero-value entry or a separate `flags` field — do not embed it in `Reason`
   as prose.
5. Update all scoring paths that currently produce free-text reasons. Find them by searching for
   `Reason =` assignments that produce strings not matching the `word:number` pattern.

### Test
- New T16: All `MigrationCandidateFinding` records returned by `scan_migration_candidates`
  have a `Reason` field where every space-separated token matches `[\w\-]+:[0-9\-]+`
  (key:integer pairs). Zero free-text tokens.

---

## B5 — `async_migrate` opaque error responses

**File:** `RoslynSentinel.Server/SentinelQualityTools.cs` — `AsyncMigrate` dispatcher

### Problem
`async_migrate` failures return `"An error occurred invoking 'async_migrate'"` with no error
code and no detail. The agent cannot distinguish a bad operation name, a missing solution, a
circuit-breaker halt, or an engine exception.

### Spec reference
The envelope and error code pattern is fully defined in
`spec-migration-scan-result-handling-v2.md §1`. Read that section before implementing. Use
`MigrationResult<BatchResultSummary>` as the return type and `ResultError` for all failure cases.

### Fix
1. Change `AsyncMigrate` return type from `Task<BatchResultSummary>` to
   `Task<MigrationResult<BatchResultSummary>>`.
2. Add solution-loaded guard at the top → `ErrorCode = "SolutionNotLoaded"`.
3. Map the unknown-operation `ArgumentException` (currently thrown in the `switch` default arm)
   to `ErrorCode = "InvalidArgument"` with `Message` listing valid operation names.
4. Wrap the dispatch `switch` in try/catch: any other throw → `ErrorCode = "Exception"`,
   `Detail = ex.Message`.
5. On success: wrap the `BatchResultSummary` in `MigrationResult<BatchResultSummary>`
   with `Success = true`, `Data = result`.
6. Note: the circuit-breaker halt already returns a `BatchResultSummary` with
   `Severity = "halt"` — this is an in-band signal, not an error. Keep it as `Success = true`
   with a populated `Data`; do not reclassify it as an error envelope.

### Test
- New T17: `async_migrate` with unknown operation name → `Success = false`,
  `ErrorCode = "InvalidArgument"`, `Message` contains valid operation names
- New T18: `async_migrate` with no solution loaded → `Success = false`,
  `ErrorCode = "SolutionNotLoaded"`
- T9 (existing, `get_async_migration_progress`): confirm pattern is consistent with B5 shape

---

## B6 — Large-result message names non-existent `read_file` tool

**Files:** Threshold/offload path added in v2 Phase 3c (wherever the large-result message string
is constructed); `[Description]` on `scan_migration_candidates` in `SentinelQualityTools.cs`

### Problem
When a result is offloaded to disk the server emits:
`"Use the read_file tool to access the content."`

`read_file` does not exist as an MCP tool in this server or in VS Code's tool set when shell
tools are forbidden. An agent reading this message correctly concludes there is a gap and either
stalls or self-corrects by trial-and-error. The recovery tool exists (`get_scan_result`) but is
not named. This is a documentation bug in the server output.

### Fix 1 — Update the large-result message string

Find the string `"read_file"` (or equivalent) in the offload path. Replace the entire
large-result notice with:

```
Result written to file ({SizeBytes} bytes, {TotalRecords} records).
Use get_scan_result(changeId: "{OperationId}") to page through results.
Pass limit and offset to control page size (default limit: 50).
```

Requirements:
- Must name `get_scan_result` exactly — not `read_file`, not a generic file tool.
- Must include the `OperationId` value inline so the agent can copy it directly into the next
  call without parsing `LargeResultInfo` separately.
- Must include `SizeBytes` and `TotalRecords` so the agent knows the scope before paging.

### Fix 2 — Add recovery path to `scan_migration_candidates` description

Add the following sentence to the `[Description]` on `scan_migration_candidates` (apply in the
same edit as any other description changes from this spec):

```
When results exceed the inline threshold, LargeResultInfo is populated instead of Data —
call get_scan_result(changeId) to read back the offloaded result in pages.
```

This ensures an agent that reads the tool description before calling knows the recovery path
exists, rather than discovering it only after hitting the offload case.

### Test
- New T19: Trigger a large-result offload (reuse T4 conditions). Assert the returned message
  string contains `"get_scan_result"` and does not contain `"read_file"`. Assert the `OperationId`
  value appears literally in the message string.

---

## B7 — `minScore` filter ignored in `summarize=true` path

**File:** `RoslynSentinel.Server/AsyncOptimizationEngine.cs` — `BuildScanSummaryAsync`
(or wherever summarize aggregation is performed)

### Problem
When `summarize=true` and `minScore` is supplied, the filter has no effect on payload size.
The full candidate set is being aggregated (and serialized) before the `minScore` filter is
applied — or the filter is only wired into the `TopCandidates` slice and not applied to the
aggregation input.

### Fix
Apply `minScore` to the full candidate list **before** any aggregation or serialization in the
summarize path. The filtered list is the input to all summary fields — `TotalCandidates`,
`ByPattern`, `ByClass`, `ByScoreBucket`, and `TopCandidates`:

```csharp
private async Task<MigrationScanSummary> BuildScanSummaryAsync(
    string? filePath, string? projectName, string? pattern,
    int? topN, int? minScore)
{
    var allCandidates = await GetAllCandidatesAsync(filePath, projectName, pattern);

    // Apply minScore before aggregation — not after
    var candidates = minScore.HasValue
        ? allCandidates.Where(c => c.Score >= minScore.Value).ToList()
        : allCandidates;

    return new MigrationScanSummary
    {
        TotalCandidates = candidates.Count,
        ByPattern       = candidates.GroupBy(c => c.Pattern)
                                    .ToDictionary(g => g.Key, g => g.Count()),
        ByClass         = BuildByClass(candidates),
        ByScoreBucket   = BuildByScoreBucket(candidates),
        TopCandidates   = topN.HasValue
                            ? candidates.OrderByDescending(c => c.Score).Take(topN.Value).ToList()
                            : null,
    };
}
```

`TotalCandidates` reflects the post-filter count. If the caller needs the unfiltered total
alongside a filtered summary, that is a separate feature request — do not add it here.

### Test
- New T20: `summarize=true, minScore=80` on a project with known candidates below and above 80
  → `TotalCandidates` equals only the count of candidates with `Score >= 80`; `ByPattern` counts
  sum to `TotalCandidates`; result is inline

---

## B7b — `minScore` filter ignored in paged (non-summary) mode

**File:** `RoslynSentinel.Server/AsyncOptimizationEngine.cs` — `FindMigrationCandidatesAsync`

### Problem
When `summarize=false` and `minScore` is supplied, the filter is silently ignored — candidates
below the threshold are returned in the paged result. Session 4 confirmed candidates scored 55,
60, and 80 were returned when `minScore=85` was set. The `minScore` parameter is only wired into
the `TopCandidates` slice in the summarize path; it is never applied to the main candidate list
in the paged path.

This is a separate bug from B7 — different code path, different fix location.

### Fix
Apply `minScore` to the candidate list in `FindMigrationCandidatesAsync` **after** filtering by
`filePath`/`projectName`/`pattern` and **before** applying `Skip(offset).Take(limit)` pagination:

```csharp
// existing filters (filePath, projectName, pattern) applied first
var filtered = candidates;

if (minScore.HasValue)
{
    filtered = filtered.Where(c => c.Score >= minScore.Value).ToList();
}

var totalCount = filtered.Count;
var page = filtered
    .OrderByDescending(c => c.Score)
    .Skip(offset)
    .Take(limit)
    .ToList();
```

`TotalRecords` in the response must reflect the post-`minScore` count, not the pre-filter total.
Pagination (`Skip`/`Take`) applies to the filtered list, not the full list.

### Description update
Update the `[Description]` on `scan_migration_candidates` to state explicitly:

```
minScore: filters candidates in both paged and summary modes. TotalRecords reflects the
post-filter count. Ignored when not supplied.
```

Remove or correct any existing description text that implies `minScore` only applies to
`TopCandidates` in summary mode.

### Test
- New T21: `summarize=false, minScore=85, limit=20` on AvaalExpress → all returned candidates
  have `Score >= 85`; `TotalRecords` equals the count of candidates with `Score >= 85` (not 986)

---

## Test summary — all cases

| # | Section | Case | Key assertion |
|---|---|---|---|
| T1 | P1/P3 | `summarize=true` | `ByClass` populated; correct bucket keys |
| T10 | P4 | `summarize=true, topN=5, minScore=70` | `TopCandidates` ≤ 5, all Score ≥ 70 |
| T11 | P4 | `summarize=true`, no topN/minScore | `TopCandidates == null` |
| T12 | P5 | `get_workspace_health` after load | `LoadedSolutionPath` non-null, ends with `.sln` |
| T13 | B2 | `get_async_migration_progress` after load (solution with `.Designer.cs`) | No exception; report fields non-null |
| T14 | B2 | `get_async_migration_progress(projectName:...)` | Scoped report; no exception |
| T15 | B3 | `project_doc` read, no solution loaded | Succeeds; no `SolutionNotLoaded` error |
| T16 | B4 | All `MigrationCandidateFinding.Reason` fields | Every token matches `[\w\-]+:[0-9\-]+` |
| T17 | B5 | `async_migrate`, unknown operation | `ErrorCode = "InvalidArgument"` |
| T18 | B5 | `async_migrate`, no solution | `ErrorCode = "SolutionNotLoaded"` |
| T19 | B6 | Large-result offload message | Contains `"get_scan_result"` and `OperationId`; no `"read_file"` |
| T20 | B7 | `summarize=true, minScore=80` | `TotalCandidates` = filtered count; result inline |
| T21 | B7b | `summarize=false, minScore=85, limit=20` | All records have `Score >= 85`; `TotalRecords` = filtered count (not 986) |

Existing tests T8, T9 from `spec-migration-scan-result-handling-v2.md §6` remain load-bearing —
confirm they still pass after B2 and B5 changes.

---

## Out of scope — §B

- Re-speccing `get_scan_result` — fully specced in `spec-migration-scan-result-handling-v2.md §3`;
  implement from that document.
- Re-speccing `describe_tool_options` — fully specced in
  `spec-tool-description-compression-v1.md §1`; implement from that document.
- VS Code infrastructure-level response offload (where VS Code intercepts a large inline response
  and writes it to a resource file outside `.roslynsentinel/`). This is not fixable server-side.
  The correct mitigation is B1 — keep `summarize=true` responses small enough that VS Code never
  triggers its offload.
- Combined health snapshot tool — deferred (carried from §P out-of-scope).
- Any changes to the circuit-breaker logic or `BatchResultSummary` shape beyond the envelope wrap
  in B5.

<!-- v5 -->
