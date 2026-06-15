# Spec — Tool Description Compression
<!-- spec-tool-description-compression-v1.md / v1 -->
**Target server:** RoslynSentinel (rhale78)
**Date:** 2026-05-31
**Motivated by:** Token analysis showing ~20,000 tokens of tool schema per session; top 10 heavy
tools account for ~3,560 tokens despite being 10% of the tool count. The dominant cost is
reference lists (valid values, operation fields, transform names) embedded in descriptions that
get loaded on every session start regardless of whether they are used.

---

## 0. Read this first (implementing agent)

**The problem:** Tool descriptions carry two distinct kinds of content:
1. **Behavioral description** — what the tool does, what parameters mean, error conditions,
   return shape. Agents need this to *call* the tool. Keep it.
2. **Reference enumeration** — lists of valid values, operation field tables, transform
   catalogues. Agents need this to *choose* values. Move it.

Reference enumerations are loaded into every session whether or not the agent uses that tool.
Moving them to a dedicated `describe_tool_options` tool reduces the per-session schema cost and
lets agents pull reference detail on demand.

**The fix is description compression, not tool splitting.** More tools adds ~80 tokens of JSON
schema overhead per tool. Splitting `async_migrate` into 6 tools saves ~370 tokens on descriptions
but adds ~480 in schema overhead — net loss. Keep the tool surface unchanged; compress only the
description strings.

**Anti-circling instructions:**

- Do **not** split any existing tool into multiple tools. One tool stays one tool.
- Do **not** compress descriptions for light or medium tools (under ~200 tokens). The ROI is
  negligible and risks stripping information agents need to avoid errors. Touch only the tools
  listed in §2.
- Do **not** remove behavioral content from descriptions — only reference enumerations. If you
  are unsure whether a sentence is behavioral or reference, keep it.
- `describe_tool_options` returns read-only reference data. It must not trigger any workspace
  operation, compilation, or file I/O.
- The compressed descriptions in §2 are **exact replacement text**. Do not paraphrase them
  further. They were written to preserve all behavioral content while removing only enumerations.
- For tools not covered in §2 with pre-written replacements (apply_file_codemod,
  apply_method_codemod, apply_class_codemod, generate, convert_switch_to_pattern_safe,
  analyze_switch_for_pattern_conversion, analyze_foreach_for_linq_conversion): follow the
  compression pattern in §1.2 to write replacements yourself. Do not proceed on those tools
  without first reading §1.2.

---

## 1. New tool: `describe_tool_options`

**File:** `RoslynSentinel.Server/SentinelQualityTools.cs` (or the file containing tool
registrations — add alongside other `[McpServerTool]` methods)

### 1.1 Purpose

Returns the reference enumeration for a named tool: valid operation values and their required
fields, valid transform/kind names, valid detector IDs by domain, etc. Replaces the inline
reference tables currently embedded in heavy tool descriptions.

### 1.2 Compression pattern (use this when writing replacements for §2 tools not pre-written)

A description should answer exactly four questions:
1. What does this tool do? (1 sentence)
2. What are the key parameters? (1 line each, names and types only — no value lists)
3. What does it return?
4. Any non-obvious error conditions or usage constraints?

Everything else — lists of valid values, operation field tables, transform catalogues — belongs
in `describe_tool_options`, not in the `[Description]` attribute.

**Before** (excerpt from `async_migrate`):
```
operation values and required input fields:
  "propagate_cancellation_token"
      input.BatchTargets    — list of { FilePath, MethodNames? }
      input.DryRun          — optional, default false
      input.MaxItems        — optional, default 100
  "convert_to_async_bridge"
  ... (repeats for 6 operations)
```

**After:**
```
operation: one of six async-migration operations — call describe_tool_options("async_migrate")
for valid values and required fields per operation.
```

### 1.3 Tool definition

```csharp
[McpServerTool]
[Description("""
    Returns reference documentation for a named tool: valid operation values, required input
    fields per operation, valid transform/kind/detector names, and parameter defaults. Call this
    once at the start of a session when you need to know what values a tool accepts.

    toolName: the MCP tool name (e.g. "async_migrate", "scan", "apply_file_codemod").

    Returns a ToolOptionsResult with a Description string (human-readable reference table) and
    a StructuredOptions object (machine-readable key→field-list map). Returns ErrorCode =
    "UnknownTool" if the tool name is not recognised.
    """)]
public ToolOptionsResult DescribeToolOptions(string toolName)
{
    return toolName switch
    {
        "async_migrate"                        => AsyncMigrateOptions(),
        "scan"                                 => ScanOptions(),
        "apply_file_codemod"                   => ApplyFileCodemodOptions(),
        "apply_method_codemod"                 => ApplyMethodCodemodOptions(),
        "apply_class_codemod"                  => ApplyClassCodemodOptions(),
        "generate"                             => GenerateOptions(),
        "convert_switch_to_pattern_safe"       => ConvertSwitchOptions(),
        "analyze_switch_for_pattern_conversion" => AnalyzeSwitchOptions(),
        "analyze_foreach_for_linq_conversion"  => AnalyzeForeachOptions(),
        _ => new ToolOptionsResult
        {
            Description = $"Unknown tool '{toolName}'.",
            Error       = new ResultError { ErrorCode = "UnknownTool", Message = $"No options registered for '{toolName}'." }
        }
    };
}
```

### 1.4 Return type

Add to `MigrationEnvelope.cs` or a new `ToolOptionsResult.cs` in `RoslynSentinel.Server/`:

```csharp
public sealed class ToolOptionsResult
{
    public string Description { get; set; }               // human-readable reference table
    public Dictionary<string, object> StructuredOptions { get; set; } // machine-readable
    public ResultError Error { get; set; }                // null on success
}
```

### 1.5 Content of each options method

Each private `XxxOptions()` method returns the exact content that was in the tool description
before compression. This is a direct cut-and-paste of the removed reference lists, reformatted
as a `Description` string and a `StructuredOptions` dictionary. No new content — only relocated
content.

---

## 2. Description replacements for heavy tools

Apply these exact replacement strings. Do not paraphrase further.

### 2.1 `async_migrate` (~450 tokens → ~60 tokens)

**File:** `SentinelQualityTools.cs` line 1282
**Current description start:** `"Unified dispatcher for the six async-migration operations."`

**Replace entire `[Description(...)]` with:**

```csharp
[Description("""
    Unified dispatcher for six async-migration operations. Dispatches to the appropriate
    engine based on the operation string; all operations check the circuit breaker first
    and return BatchResultSummary.

    operation: one of six values — call describe_tool_options("async_migrate") for valid
               values and required input fields per operation.
    input:     AsyncMigrateInput — fields vary by operation; see describe_tool_options.

    Returns BatchResultSummary. Use get_operation_detail(changeId) for per-item details.
    Severity="halt" means the circuit breaker opened — call get_breaker_status then reset_breaker.
    """)]
```

**Content to move into `AsyncMigrateOptions()`:** the full operation×field table currently at
lines 1285–1321 of `SentinelQualityTools.cs`.

---

### 2.2 `scan_migration_candidates` (~240 tokens → ~80 tokens)

**File:** `SentinelQualityTools.cs` line 223
**Current description start:** `"Returns all methods in the solution (or scoped to a file/project)"`

The current description for this tool is already reasonable. After the v2 patch adds `summarize`,
`topN`, `minScore`, `limit`, and `offset`, it will grow. Apply this replacement at the same time
as the patch spec parameters are added:

**Replace entire `[Description(...)]` with:**

```csharp
[Description("""
    Returns [MigrationCandidate]-attributed methods added by flag_migration_candidate.
    Uses syntax-level analysis — no compilation needed.

    filePath:    restrict to one file (full or partial path suffix).
    projectName: restrict to one project (case-insensitive).
    pattern:     restrict to one pattern — call describe_tool_options("scan_migration_candidates")
                 for valid pattern values.
    summarize:   when true, return counts only (always inline-safe). Add topN/minScore to include
                 top actionable targets alongside counts.
    topN:        when summarize=true, include this many top-scored candidates in TopCandidates.
    minScore:    when summarize=true, filter TopCandidates to score >= minScore.
    limit/offset: page the full candidate list (summarize=false only).

    Returns MigrationResult<List<MigrationCandidateFinding>> or MigrationResult<MigrationScanSummary>.
    A method flagged for two patterns appears twice. Each finding includes a Summary field.
    """)]
```

**Content to move into `ScanMigrationCandidatesOptions()`:** the list of valid `pattern` values
(currently inline in the description). Also add `scan_migration_candidates` as a key in the
`DescribeToolOptions` switch.

---

### 2.3 `apply_file_codemod`, `apply_method_codemod`, `apply_class_codemod` (~420/380/350 tokens)

**These tools are not in the uploaded source files.** Find them in the codebase. Apply the
§1.2 compression pattern:

1. Keep: what the tool does (1 sentence), parameter names and types, return shape, circuit
   breaker / error conditions.
2. Move to `DescribeToolOptions`: the named transform list with per-transform descriptions.
3. Add a single line to the compressed description:
   `transform: the codemod to apply — call describe_tool_options("<toolname>") for valid transforms.`
4. Implement `ApplyFileCodemodOptions()`, `ApplyMethodCodemodOptions()`,
   `ApplyClassCodemodOptions()` returning the moved transform lists.

---

### 2.4 `generate` (~380 tokens)

**Not in uploaded source files.** Apply §1.2 compression pattern:

1. Keep: what the tool generates, parameter names, return shape, error conditions.
2. Move to `DescribeToolOptions`: the `kind` value list with per-kind descriptions.
3. Add: `kind: the artefact to generate — call describe_tool_options("generate") for valid values.`
4. Implement `GenerateOptions()`.

---

### 2.5 `convert_switch_to_pattern_safe`, `analyze_switch_for_pattern_conversion`,
       `analyze_foreach_for_linq_conversion` (~300/280/260 tokens)

**Not in uploaded source files.** Apply §1.2 compression pattern.

These tools likely carry long safety narratives and constraint lists. Compress:
1. Keep: what analysis is performed, key parameters, return shape, the safety constraint summary
   in one sentence (e.g. "Applies only when all arms are exhaustive and no fall-through exists").
2. Move to `DescribeToolOptions`: the detailed constraint explanations and per-case safety rules.
3. Implement `ConvertSwitchOptions()`, `AnalyzeSwitchOptions()`, `AnalyzeForeachOptions()`.

---

### 2.6 `scan` (~500 tokens — the heaviest single tool)

**Not in uploaded source files.** This tool lists all 94 detector IDs by domain. Apply
§1.2 compression pattern:

1. Keep: what the tool scans for, key parameters (scope, domain filter), return shape.
2. Move to `DescribeToolOptions("scan")`: the full detector ID list grouped by domain.
3. Add: `detectorIds: optional filter — call describe_tool_options("scan") for all valid IDs
   grouped by domain.`
4. Implement `ScanOptions()` returning the full detector catalogue.

This single change recovers the most tokens (~420 of the ~500 — the detector list dominates).

---

## 3. Token impact summary

| Tool | Before | After (description) | Options method (once/session) | Net per session |
|---|---|---|---|---|
| `scan` | ~500 | ~80 | ~420 (if called) | ~420 saved always; options amortized |
| `async_migrate` | ~450 | ~60 | ~390 (if called) | ~390 saved always |
| `apply_file_codemod` | ~420 | ~80 | ~340 (if called) | ~340 saved always |
| `apply_method_codemod` | ~380 | ~80 | ~300 (if called) | ~300 saved always |
| `apply_class_codemod` | ~350 | ~80 | ~270 (if called) | ~270 saved always |
| `generate` | ~380 | ~80 | ~300 (if called) | ~300 saved always |
| `convert_switch_to_pattern_safe` | ~300 | ~80 | ~220 (if called) | ~220 saved always |
| `analyze_switch_for_pattern_conversion` | ~280 | ~80 | ~200 (if called) | ~200 saved always |
| `analyze_foreach_for_linq_conversion` | ~260 | ~80 | ~180 (if called) | ~180 saved always |
| `scan_migration_candidates` | ~240 | ~80 | ~80 (if called) | ~160 saved always |
| `describe_tool_options` (new) | 0 | ~120 | — | −120 (new schema cost) |
| **Total** | **~3,560** | **~820** | varies | **~2,620 saved per session** |

In a typical migration session the agent uses `async_migrate`, `scan_migration_candidates`, and
one `apply_*_codemod` tool. It calls `describe_tool_options` for those three, paying ~1,010
tokens for the options it needs. Net saving vs. current: **~1,610 tokens per migration session**.
In a diagnostic-only session (scan + health tools), the saving is ~740 tokens with zero options
calls needed.

---

## 4. Test cases

No new test file required. These are description-content changes and a new read-only tool.

| # | Case | Assertion |
|---|---|---|
| T1 | `describe_tool_options("async_migrate")` | Returns non-null `Description`; contains all 6 operation names; `Error == null` |
| T2 | `describe_tool_options("scan")` | Returns non-null `Description`; contains detector IDs; `Error == null` |
| T3 | `describe_tool_options("apply_file_codemod")` | Returns non-null `Description`; `Error == null` |
| T4 | `describe_tool_options("unknown_tool")` | `Error.ErrorCode == "UnknownTool"` |
| T5 | `async_migrate` description length | Compressed description is ≤ 100 tokens (spot-check) |
| T6 | `scan` description length | Compressed description is ≤ 100 tokens (spot-check) |

---

## 5. Out of scope

- Compressing light or medium tools (under ~200 tokens) — ROI too low, risk of stripping needed content.
- Splitting any tool into multiple tools — adds schema overhead, net loss.
- Modifying return types or behavior of any existing tool — description strings only.
- Adding `describe_tool_options` to the mode/profile system — that is tracked separately in
  the VS Code / Copilot Token Efficiency work item. For now, `describe_tool_options` is always
  loaded and always available.

<!-- v1 -->
