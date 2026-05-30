# RoslynSentinel Tools Review — Optimization & Sealed-Tank Analysis
**Date:** 2026-05-30  
**Tool count:** 317  
**Status:** Substantive, production-grade; consolidation opportunity and critical gaps identified

---

## Token Cost Summary

| Measure | Value |
|---|---|
| Raw file (pretty-printed) | ~62k tokens |
| **Injected payload (name + desc + schema, minified)** | **~48k tokens** |
| Descriptions only | ~25k tokens |
| Descriptions > 800 chars | 27 tools |
| Descriptions < 60 chars | 29 tools (3 near-empty) |

**Bottom line:** The ~48k-token tool-list payload recurs every turn before the agent does anything. This is a significant fixed tax on context. The payload can realistically be trimmed to ~30–35k tokens without losing selection signal through consolidation and description cleanup.

---

## Descriptions — Shortening Targets

### The return-field dump problem

27 tools (800–2426 chars) include exhaustive field-by-field documentation of their return types inside the description. This duplicates what's in the schema and is not needed for tool selection — the model picks based on *what it does*, not on a list of DTO fields.

**Worst offenders:**

- `flag_migration_candidates_in_project` (2426 chars) — includes the entire async-migration scoring rubric (+40, +30, -20, disqualification rules…). The rubric belongs in docs; the description should be one paragraph with a reference.
- `run_bridge_batch` (1746), `run_uplift_batch` (1683), `run_uplift_batch_multi` (1145) — each lists a numbered algorithm step-by-step AND a full return-field dump.
- `propagate_cancellation_token_*` (1369/1193/1055 across three tools) — three-step transformation narrative is useful; field-by-field return block is not.
- `convert_to_async_bridge` (1620) — algorithm is good; the DTO field list adds no signal.

**Recommended fix:** Strip the return-field dumps. Replace with one-sentence summary of what's returned (e.g., "Returns a staged change-set; call apply_staged_changes to commit."). Move algorithmic detail and rubrics to separate `docs/` markdown files.

**Estimated savings:** 27 tools averaging 1100 chars, trimmed to ~400 → ~700 chars each → ~19k chars saved → **~5k tokens reclaimed**.

---

## Descriptions — Revisions Needed

### Dead references to removed standard tools (16 tools)

**Critical issue for the sealed tank:** At least 16 tools are framed as bug-fix wrappers around "the standard MS/VS tool":

- **12 `_safe` variants:** `extract_method_safe`, `extract_constant_safe`, `encapsulate_field_safe`, `generate_to_string_safe`, `convert_switch_to_pattern_safe`, `convert_property_safe`, `implement_interface_safe`, `find_callers_safe`, `find_implementations_safe`, `interpolate_string_safe`, `format_document_safe`, `make_method_thread_safe`
- **4 `_smart/_preview` variants:** `format_document_preview`, `preview_add_missing_usings`, `convert_string_format_to_interpolated_smart`, `analyze_foreach_for_linq_conversion`

Each description is structured as:
```
FIXES MS BUG: The standard X tool has problem Y. This tool fixes it by doing Z.
[Details of the MS bug]
Unlike the built-in Y, this tool [improvement].
```

**Why this is a problem in a sealed tank:** The generic VS Code / C# extension tools are being **removed**. There is no "standard tool" left to contrast against. The `_safe` suffix implies an unsafe sibling that no longer exists. The description becomes stale and actively confusing — it argues against a ghost.

**Recommended fix:**

1. **Rename to the clean name:** `extract_method_safe` → `extract_method`, `encapsulate_field_safe` → `encapsulate_field`, etc.
2. **Strip the "FIXES MS BUG" preamble** — the bug is in a tool you're removing, not the description subject.
3. **Lead with what the tool does, not what it doesn't do:**
   - OLD: "FIXES MS BUG: The standard extract_constant tool requires exact 1-based line/column coordinates…"
   - NEW: "Extracts a constant from a code file using contextSnippet for disambiguation (no line/column arithmetic required). Replaces all identical literals in the file."

**Estimated savings:** ~200–400 chars per tool × 16 tools → ~4k chars → **~1k tokens**. Plus clarity and coherence in a sealed tank.

---

### The 3 near-empty descriptions

Insufficient detail for reliable tool selection:

- `find_task_yield_usage` — "Detects Task.Yield() calls."
- `find_task_delay_usage` — "Detects Task.Delay() usage."
- `class_to_record` — "Converts a class to a C# record."

**Issues:**
- No scope parameters (solution-wide? file? project?).
- No preconditions mentioned (`class_to_record` — does it handle positional params? mutable fields?).
- No output description.

**Recommended fix:** Match the detail level of peer tools in the same family. Example revised descriptions:

```
find_task_yield_usage
Scans the solution (or a specific file/project) for Task.Yield() calls.
Returns call site locations with containing type, method, line number, and snippet.
Scope: filePath (single file) or projectName (restrict to project) or null (entire solution).

class_to_record
Converts a class to a record. Automatically migrates positional parameters if the class has
a constructor with auto-properties matching constructor parameters. Handles both nominal
and positional record syntax. Precondition: class must not have mutable fields or complex
initialization logic incompatible with record semantics.
```

---

## Useful vs. Unnecessary for Normal Use

### Clear consolidation target: the `find_*` family (84 tools)

**Issue:** 6+ single-API detector tools that should collapse into one parameterized tool:

- `find_task_yield_usage`, `find_task_delay_usage`, `find_task_delay_zero_usage`, `find_task_when_all_usage`, `find_task_void_usage`, `find_task_run_in` 
  - **Consolidate into:** `find_task_api_usage(api: "Yield|Delay|Delay_Zero|WhenAll|Void|RunSync")`
  - **Savings:** 6 tools → 1, ~2k tokens of description payload eliminated

- `find_unsafe_lazy_init` / `find_unsafe_lazy_init_thread` — near-identical, parameterize with `checkThreadSafety: true/false`

- `find_mutable_public_properties` / `find_mutable_public_collection_properties` — split by collection type, parameter `collectionsOnly: true/false`

**Realistic consolidation target:** ~10–15 tools reduced, saving ~3k–4k tokens of payload.

### Migration-specific cluster (12 tools, ~8k tokens)

These are purpose-built for the Avaal Express async migration:

- `flag_migration_candidate(s)*` (3 tools)
- `run_bridge_batch`, `run_uplift_batch(_multi)` (3 tools)
- `convert_to_async_bridge`
- `propagate_cancellation_token_*` (3 tools)
- `find_obsolete_callers`, `get_async_migration_progress`

**Assessment:** High-value *during* migration; dead weight for normal refactoring. The sealed tank benefits from **profile splitting**: load `migration` tools on-demand for active migration work, default to a `general` refactoring profile.

**Alternative:** Keep them but rank them last (agent selection tends toward recency), or move them to a separate `advanced/` section of the tool manifest if your harness supports tool categories.

### Genuinely niche scaffolders (7 tools, keep but low priority)

Useful occasionally; not worth permanent context residency:

- `add_benchmark_stub`
- `generate_http_client`
- `generate_decorator_class`
- `generate_fluent_builder`
- `generate_mapping`
- `generate_repository_interface`
- `generate_classes_from_json`

**Recommendation:** Documented but opt-in. Do not load by default. Include a `list_features` capability so the agent can discover them when relevant.

### Core keepers (the sealed-tank spine, ~60 tools)

These justify removing generic file/shell access:

- **Workspace fundamentals:** `load_solution`, `get_workspace_health`, `get_feature_status`
- **Semantic query:** `get_code_inventory`, `get_type_members_detail`, `get_symbol_info`, `get_type_hierarchy`, `get_call_graph`, `get_reverse_call_graph`, `get_blast_radius`
- **Safe refactoring:** `rename_symbol`, `extract_method_safe`, `extract_constant_safe`, `convert_*_safe`, `apply_staged_changes`, `validate_staged_changes`, `get_staged_changes`, `discard_staged_changes`
- **Analysis:** `get_diagnostics_summary`, `get_solution_diagnostics`, `find_callers_safe`, `find_implementations_safe`, `analyze_stack_overflow_risks`
- **Scoped documentation:** `read_*`, `update_*`, `write_handoff`, `append_completed_work`, `list_project_documentation`

---

## Missing Tools (Critical for Sealed Tank)

### 1. Read a method's / file's actual source (🔴 BLOCKER)

**The problem:** Many of your best tools require `contextSnippet` — a *verbatim substring of the code*:

- `extract_method_safe`, `extract_constant_safe`, `convert_*_safe`, `find_implementations_safe`, `analyze_switch_for_pattern_conversion`

**With generic `read_file` removed, how does the agent obtain a verbatim snippet of code it has never seen?**

- `get_code_inventory` / `get_type_members_detail` return structure (types, methods, signatures) but not source bodies.
- `find_large_methods` even says "Methods over 50 lines are too large to modify safely without reading in full" — but nothing lets you read it in full.
- Without a source-reading aperture, the agent cannot use the snippet-based tools on unfamiliar code, or will rationalize its way back to a shell to view the file.

**Recommended tool:**

```csharp
get_method_source(filePath, methodName, maxLines: 500)
→ returns: source text, line range, surrounding context

OR

get_file_outline(filePath)
→ returns: full source OR a summary (signatures + first/last N lines of each method)
```

This must be added before the sealed tank is truly sealed. Without it, the contextSnippet-based tools are dead weight.

### 2. Text/regex search across the solution

**The gap:** General pattern search across the codebase (grep replacement).

- `find_string_magic_values` — magic numbers only.
- `find_todo_fixme_comments` — annotations only.
- No general "find all occurrences of literal / identifier / pattern X."

**Use cases the agent loses:**
- Locating a string constant ("find all usages of error code ERR_12345")
- Searching for a config key ("where is ConnectionStringXYZ referenced?")
- Regex pattern across the solution ("find all `Foo.Bar().Baz()` call chains")

**Recommended tool:**

```csharp
search_solution_text(pattern: string, isRegex: bool = false, fileGlob: string = "**/*.cs")
→ returns: file path, line, column, matched text snippet, containing method
```

Constrained to the loaded solution (no filesystem escape). Scoped to `.cs` files by default.

### 3. Capability discovery affordance

With 317 (or 200 after consolidation) tools and no shell fallback, the agent needs reliable self-routing.

- `list_features` / `get_feature_status` exist but unclear if they return enough granularity.
- Recommend: ensure they surface by category (analysis, refactoring, migration, docs) with brief descriptions so the agent can self-route when uncertain ("I need to find all SQL calls" → suggests `check_for_sql_injection`, `find_string_magic_values`).

---

## Summary of Optimization Opportunities

| Opportunity | Tools | Estimated Token Savings |
|---|---|---|
| Strip return-field dumps from descriptions | 27 long descriptions | ~5k |
| Remove "FIXES MS BUG" preambles (rename `_safe` variants) | 16 tools | ~1k |
| Consolidate `find_task_*` / overlapping pairs | ~12 tools | ~3–4k |
| Total payload reduction | — | **~10–12k tokens** |
| **Target payload size** | — | **~35–40k tokens** (from ~48k) |

---

## Sealed-Tank Readiness Checklist

- ✅ **All-RoslynSentinel:** No generic shell/file/search tools in the list (correct).
- ✅ **Scoped doc tools present:** `read_plan`, `update_current_state`, `read_completed_work`, etc. are the sanctioned file-access aperture.
- ✅ **Staged-change lifecycle:** `apply_staged_changes`, `validate_staged_changes`, etc. — good.
- ✅ **No dead input:** The tools have proper input schemas, no generic "run arbitrary X" tools.
- 🔴 **Missing source reader:** `get_method_source` / `get_file_outline` required before sealing.
- 🟡 **Dead reference descriptions:** 16 `_safe`/`_smart` tools reference removed standard tools; unclear naming post-seal.
- 🟡 **Bloated payload:** ~48k tokens of definitions on every turn; consolidation would help.
- 🟡 **Profile opportunity:** Migration tools could be opt-in rather than always-on.

---

## Recommendations (Priority Order)

1. **Add `get_method_source` tool.** Non-negotiable for sealed tank — required by contextSnippet-based tools.
2. **Add `search_solution_text` tool.** Closes the grep-replacement gap.
3. **Rename `_safe` variants, strip "FIXES MS BUG" preambles.** Removes confusion post-seal.
4. **Consolidate `find_task_*` family.** 6 tools → 1 parameterized tool. Save ~2k tokens.
5. **Trim return-field dumps from 27 long descriptions.** ~5k tokens reclaimed.
6. **Consider migration-tool profile.** Keep them, but mark as opt-in or rank them low for non-migration work.

**Post-optimization target:** ~35–40k-token payload, +2 critical tools, clearer naming, sealed tank is genuine and complete.

---

## Notes for Implementation

- If descriptions live in a source file (e.g., XML comments in C#, or a JSON schema generator), script a bulk trim pass on the return-field dumps.
- Tool renaming (`extract_method_safe` → `extract_method`) may require a client-side mapping layer if older scripts reference the `_safe` names; plan for deprecation period.
- Consolidation is safe — new parameterized tools are backward-compatible if you keep the single-API variants as aliases that call the parameterized version.

---

**v1** — 2026-05-30
