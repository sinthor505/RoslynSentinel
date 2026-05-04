# Roslyn Sentinel

**Roslyn Sentinel** is a high-performance, persistent MCP (Model Context Protocol) server designed to give AI agents "Compiler-Grade Intelligence." It keeps your .NET solution "hot" in memory, maintaining an active `MSBuildWorkspace` to eliminate cold-start delays and provide deep semantic analysis across massive (300k+ LOC) codebases.

---

## 📚 Documentation

| Document | Purpose | Status |
|----------|---------|--------|
| **[TOOL_DOCUMENTATION.md](./TOOL_DOCUMENTATION.md)** | Complete reference with pre/post code examples for all ~240 tools | ✅ NEW |
| [UNFINISHED_FEATURES.md](./UNFINISHED_FEATURES.md) | Stub methods, deferred bugs, known limitations | ✅ Complete |
| [UNFINISHED.md](./UNFINISHED.md) | Full development history (16 sessions, 600+ tests) | ✅ Complete |
| [README.md](./README.md) | High-level overview (this file) | ✅ Current |
| [DOCUMENTATION_INDEX.md](./DOCUMENTATION_INDEX.md) | Navigation guide for all docs | ✅ Complete |

**Start here:** Read [TOOL_DOCUMENTATION.md](./TOOL_DOCUMENTATION.md) for realistic before/after examples of every tool.

---

## 🚀 236 MCP Tools across 55 Specialized Engines

Roslyn Sentinel is built on a modular engine architecture, providing a vast library of surgical refactorings, architectural audits, modernizations, and code generation tools.

### 🏗️ Infrastructure & Workspace (26 tools)
*   **`PersistentWorkspaceManager`**: Manages the "Hot" solution, self-change tracking, and proactive FS synchronization.
*   **`SentinelConfiguration`**: Centralized Feature Toggle System for 40+ granular analysis rules.
*   **`ValidationEngine`**: Speculative in-memory compilation (CSXXXX diagnostic return) for Unified Diffs.
*   **Project/solution diagnostics**: `get_project_diagnostics`, `get_solution_diagnostics`, `split_project_by_folder`.
*   **Namespace management**: `fix_mismatched_namespaces`, `move_file_to_namespace_folder`.

### 🛠️ Refactoring — 63 tools ("The Surgical Suite")
*   **`RefactoringEngine`**: Rename (solution-wide), Safe-Delete (reflection-aware), Change Signature, Extract Method/Interface, `sync_interface_to_implementation`, `update_xml_docs_from_signature`, `convert_expression_body` (block↔expression form by member name), `extract_constant` (literal → named `const`), `analyze_control_flow` (always/sometimes/never-returns per method), `analyze_data_flow` (read/written/captured variables per method), `format_document_preview` *(new — preview formatter changes before applying)*.
*   **`GranularRefactoringEngine`**: `introduce_field`, `introduce_parameter`, `introduce_variable`, `introduce_parameter_object` (groups method parameters into a new `record` type; adds TODO for call-site updates).
*   **`RefinementEngine`**: `pull_up_member` — move a method from derived class to base class.
*   **`AdvancedRefactoringEngine`**: Replace string concatenation with interpolation; optimize `.Result`/`.Wait()` to `await`.
*   **`MappingEngine`**: DTO ↔ entity mapping generation, invert assignments.
*   **`CodeFlowEngine`**: Reduce block nesting depth.
*   **`AdvancedStructuralEngine`**: Replace constructor with factory.
*   **Surgical member editing** (all on `RefactoringEngine`, all with `autoStage=true`):
    - `add_member_to_class` — append a member to a class/interface/record/struct
    - `insert_member_after` / `insert_member_before` — positional insertion relative to a named member
    - `replace_member` — replace a named member wholesale with new source
    - `remove_member` — remove a named member
    - `add_using_directive` — add `using X.Y.Z;` idempotently (supports `static` usings)
    - `add_enum_value` — append a named value (+optional explicit integer) to an enum
    - `add_attribute` / `remove_attribute` — attach or remove `[ApiController]`, `[Required]`, etc. (matches with/without `Attribute` suffix)
    - `add_base_type` / `remove_base_type` — add or remove a base class or interface (idempotent)
    - `change_accessibility` — swap `public`/`private`/`internal`/`protected`/`protected internal`/`private protected` on any member or type
    - `add_modifier` / `remove_modifier` — toggle `virtual`, `abstract`, `sealed`, `static`, `readonly`, `override`, `partial`, `async`, `new` (idempotent)
    - `add_summary_comment` — add or replace `/// <summary>...</summary>` doc comment on any member or type
    - `add_property` — generate an auto-property from name/type/accessibility/getter/setter/init flags
    - `add_field` — generate a field from name/type/accessibility/readonly/static/initializer flags
    - `sort_members` — reorder class members by convention: fields → constructors → properties → methods → nested types
    - `wrap_in_try_catch` — wrap a line range in `try { } catch (ExceptionType ex) { }` with optional catch body
    - `add_constructor_parameter` — add a DI dependency in one shot: new private readonly field + ctor parameter + body assignment
    - `wrap_in_region` — surround a line range with `#region name` / `#endregion`

### ⚡ Modernization — 30 tools (.NET 8/9/10 & C# 12/13/14)
*   **`CodeStyleEngine`**: .NET 10 **Lock Modernization**, C# 14 **Field-Backed Properties**, **Implicit Span Cleanup**, Collection Expressions (`[]`), `find_use_frozen_collections`.
*   **`SyntaxUpgradeEngine`**: Modern Guard Clauses (`ThrowIfNull`), Switch Expressions, `upgrade_to_modern_guards`, `convert_switch_to_expression`, `cleanup_implicit_spans`, `use_exception_expressions` (converts `throw new ArgumentNullException(nameof(x))` → `ArgumentNullException.ThrowIfNull(x)` etc.), `upgrade_to_primary_constructor` (converts pure-assignment ctors to C# 12 primary constructors).
*   **`ModernizationEngine`**: Class-to-Record / Record-to-Class (POCO modernization).
*   **`IDEStyleEngine`**: Simplify member access chains; `use_object_initializers`.
*   **`ImmutabilityEngine`**: Convert mutable classes to immutable (init-only / records).
*   **`AsyncOptimizationEngine`**: `optimize_to_value_task`, `optimize_independent_awaits`, `generate_async_overload`.
*   **`ModernLoggingEngine`**: Convert to source-generated logging.
*   **`LogicOptimizationEngine`**: Simplify boolean expressions.
*   **`AdvancedLogicEngine`**: `convert_static_to_extension`, `invert_boolean_logic`, `convert_foreach_to_for`.

### 🔍 Intelligence & Analysis — 40 tools
*   **`AnalysisEngine`**: Find large types/methods, duplicate methods, interface extraction candidates, **memory leak detection** (event subscription without `IDisposable`), **infinite loop detection**, **call tree generation**, **equality override generation** (`HashCode.Combine`).
*   **`MetricsEngine`**: Code quality metrics (cyclomatic complexity, maintainability index, LCOM cohesion).
*   **`SymbolNavigationEngine`**: `get_call_graph`, `get_reverse_call_graph` (who calls this method), extension method discovery, `find_callers_safe` (all references without line/col), `find_implementations_safe` (all implementations without line/col).
*   **`ArchitecturalEngine`**: `find_circular_dependencies` (Tarjan's SCC), `convert_to_background_service`.
*   **`DeadCodeEngine`**: Unused private members/constructors (with DI false-positive avoidance), unmatched event subscriptions (`+=` without `-=`).
*   **`AsyncSafetyEngine`**: Flag `.Result`, `.Wait()`, `ConfigureAwait`, `find_missing_cancellation_tokens`; `find_configure_await_missing`, `find_blocking_calls_in_async`, `find_async_in_constructor`, `find_task_run_in_async`, `find_concurrent_collection_opportunities`, `find_unsafe_lazy_init`, `find_async_over_sync` (async methods with no real awaits), `find_unawaited_fire_and_forget`, `detect_valuetask_misuse` (double-await, deferred, `Task.WhenAll`, `.Result` on ValueTask).
*   **`AsyncOptimizationEngine`**: `optimize_to_value_task` (with safety checks), `optimize_independent_awaits` (handles `var x = await` with dependency tracking), `generate_async_overload` (scaffold), `add_configure_await_false`, `remove_configure_await_false`, `convert_to_async_enumerable`, `add_cancellation_token_to_method` (adds CT + propagates to callees).
*   **`ThreadSafetyEngine`**: `convert_lock_to_semaphore_slim`, `make_method_thread_safe` (optional `lockFieldName` param to avoid field collisions).
*   **`DependencyInjectionEngine`**: Analyze service lifetimes, DI correctness, and `find_services_not_registered`.
*   **`DiscoveryEngine`**: `find_all_throw_sites`, `find_object_creation_sites`, `get_public_api_surface`, `find_best_insertion_point` (returns optimal line to insert a field/ctor/property/method/event/nested type by convention), `find_todo_fixme_comments` (scans TODO/FIXME/HACK/BUG/REVIEW comments across file/project/solution, severity-ranked), `preview_rename_impact` (impact analysis before rename: count, files, test refs).
*   **`SemanticSearchEngine`**: Cross-solution symbol and usage search.

### 🔎 Quality & Anti-Patterns — 39 tools
*   **`AntiPatternEngine`**: `find_mutable_public_properties`, `find_naming_violations`, `find_string_magic_values`, `analyze_exception_handling`, `find_long_parameter_list` (flags methods with ≥N params, skips DI-injection ctors), `find_primitive_obsession` (same primitive type 3+ times as distinct params), `find_inconsistent_async_suffix` (async methods missing "Async" or non-async methods with "Async" suffix).
*   **`PerformanceEngine`**: Boxing detection, LINQ materialization, string concatenation in loops.
*   **`SecurityEngine`**: SQL injection, hardcoded secrets, weak hash algorithms, insecure `new Random()`.
*   **`TestingEngine`**: Missing assertions, test code smell detection.
*   **`CodeStyleEngine`** (new): `find_use_frozen_collections` — detects `private static readonly Dictionary/HashSet` initialized inline that could be `FrozenDictionary`/`FrozenSet` for zero-allocation lookups.
*   **`GetDiagnosticsSummary`** *(new)*: Groups Roslyn compiler diagnostics (CS-codes) by ID across file/project/solution, sorted by frequency — shows which errors are most common without scrolling through hundreds of raw messages.

### 🏭 Code Generation — 14 tools
*   **`CodeGenerationEngine`**: Fluent builder, default config JSON, decorator class generation.
*   **`ImplementInterfaceSafe`** *(use instead of built-in `implement_interface`)*: Generates correct `public` method/property stubs with `throw new NotImplementedException()` — **never adds `override`** (which is incorrect for interface implementations).
*   **`ConvertPropertySafe`** *(new — use instead of built-in `convert_property`)*: Converts a property between auto-property and full property with backing field. Unlike the built-in, this **preserves initializers** when converting `ToFullProperty` (initializer moves to the backing field) and keeps `virtual`/`override`/`new` modifiers. Direction: `"ToFullProperty"` or `"ToAutoProperty"`.
*   **`InterpolateStringSafe`** *(new — use instead of built-in `convert_to_interpolated_string`)*: Converts `string.Format(...)` to an interpolated string. Unlike the built-in, this resolves **const string format arguments** via the semantic model, so it works even when the format string is a named const rather than a literal.
*   **`FormatDocumentPreview`** *(new)*: Returns a unified-diff-style preview of what `format_document` would change, without applying any changes. Returns `Changed=false` and an empty `Hunks` list if the file is already formatted correctly. Each hunk includes ±3 context lines.
*   **`ApiIntegrationEngine`**: `add_validation_to_poco` — add `[Required]`/`[Range]` annotations to plain objects.
*   **`AsyncOptimizationEngine`**: Generate async method overloads.

### 🔧 MS Standard Tool Augmentations — 10 tools (use these instead of the built-in equivalents)

**Original 5 — correctness fixes:**
*   **`EncapsulateFieldSafe`**: Wraps a field in a property with a correctly-named `_camelCase` backing field. The built-in `encapsulate_field` generates self-referential code (`Field { get { return Field; } }` with same name).
*   **`AnalyzeSwitchForPatternConversion`**: Pre-flight check — inspects a `switch` statement and reports whether pattern matching conversion is safe. Detects multi-assignment cases the MS tool silently mishandles.
*   **`ConvertSwitchToPatternSafe`**: Converts a `switch` statement to a `switch` expression. Rejects multi-assignment cases (returns error) rather than generating broken code.
*   **`InterpolateStringSmart`**: Converts `string.Format(...)` to an interpolated string. Unlike the built-in, resolves **const string format arguments** via the semantic model (the built-in fails on named consts).
*   **`SortDeduplicateUsings`**: Sorts AND deduplicates `using` directives in one pass. The built-in `sort_usings` does not remove duplicates.

**New 5 — missing features + UX fixes:**
*   **`FormatDocumentSafe`**: Adds true **preview support** to `format_document`. Default `preview=true` returns formatted content without writing. The built-in has no preview parameter at all.
*   **`AnalyzeForeachForLinqConversion`**: **Pre-flight safety check** before using `convert_foreach_linq`. The built-in silently destroys data when a collection is populated before the foreach (it re-initializes with `new List<T>()`, losing prior `.Add()` calls). Always call this first; only proceed if `IsSafeToConvert=true`.
*   **`GetWorkspaceHealth`**: Reports true workspace state (projects loaded, documents, errors). The built-in `diagnose` reports `healthy:false` even when all projects load correctly, because it tests MSBuild path existence rather than actual workspace state.
*   **`PreviewAddMissingUsings`**: Shows which usings would be added **without writing to disk**. The built-in `add_missing_usings` with `preview:true` applies changes anyway — the flag is completely ignored.
*   **`ExtractConstantSafe`**: Extracts a literal to a named constant using **contextSnippet** instead of line/column. The built-in requires exact 1-based char offsets and throws cryptic errors (`"Column 99 is beyond end of line"`) when they're off by even one character.

All 10 augmented tools support `lineBefore`/`lineAfter` disambiguation.

---

## 🤖 AI-First Tool Design

All tools that target a specific code location use a **`contextSnippet`** parameter — a verbatim substring from the source file — rather than `(line, column)` coordinates. An AI agent can paste a snippet it already sees in context without calculating any offset.

```
// Bad (requires coordinate math):
IntroduceField(filePath, line: 47, column: 23, "newFieldName")

// Good (AI pastes nearby text):
IntroduceField(filePath, contextSnippet: "var result = _service.Get(", "newFieldName")
```

**Rules:**
- If the snippet appears exactly once → position resolved, operation proceeds
- If the snippet is not found → descriptive error returned
- If the snippet matches multiple locations → provide `lineBefore` and/or `lineAfter` to disambiguate

**Disambiguation with surrounding-line context** (available on all contextSnippet tools):
```
// When "var x = 0;" appears in multiple methods:
IntroduceField(filePath,
    contextSnippet: "var x = 0;",
    lineBefore: "public void Method1()",   // verbatim text from the line above
    lineAfter:  "{")                        // verbatim text from the line below
```
- `lineBefore` / `lineAfter` are trimmed and matched with `Contains` — indentation differences are ignored
- If still ambiguous after both hints, an error is returned explaining which matches remain
- If no match passes the filter, an error names which contextSnippet + context was checked

This pattern is used by all position-based tools: `introduce_field`, `introduce_parameter`, `introduce_variable`, `safe_delete_symbol`, `get_blast_radius`, `get_symbol_info`, `preview_rename_impact`, `upgrade_unbound_nameof`, `extract_constant`, `convert_expression_body`, `analyze_control_flow`, `analyze_data_flow`, and all 10 MS augmented tools (`encapsulate_field_safe`, `analyze_switch_for_pattern`, `convert_switch_to_pattern_safe`, `interpolate_string_smart`, `sort_deduplicate_usings`, `format_document_safe`, `analyze_foreach_for_linq_conversion`, `preview_add_missing_usings`, `extract_constant_safe`).

For line-range tools (`extract_method`, `wrap_in_try_catch`, `wrap_in_using`), provide `startLineText`/`endLineText` — the exact physical text of the first and last lines — as a staleness guard.

---

## ⚙️ Feature Toggles & Rule Management

Roslyn Sentinel features a global **Feature Toggle System**. This allows you to enable or disable any of the 300+ rules globally at runtime. This integration is absolute: if a rule is disabled, it will not be executed by the `get_comprehensive_health_report` nor by any surgical refactoring tool.

### **Management Tools**
- **`list_features()`**: Returns a complete dictionary of all rules (e.g. `BoxingAllocation`, `LockModernization`, `ModernGuardClauses`) and their current status (`ENABLED/DISABLED`).
- **`get_feature_status(List<string> names)`**: Query the status of specific capabilities.
- **`update_features(List<KeyValuePair<string, bool>> updates)`**: Batch update rule statuses.
  - *Example Intent*: "Disable boxing detection and multi-type file rules to reduce noise."
  - *Result*: The server skips these passes in all subsequent solution-wide scans.

---

## 📦 Installation (Local-Per-Solution Pattern)

Roslyn Sentinel is meant for a **solution-enabled system**, not for generic global use. It should be installed locally per major solution or repository group.

1.  **Clone & Publish**:
    ```bash
    dotnet publish RoslynSentinel.Server/RoslynSentinel.Server.csproj -c Release -o ./publish
    ```

2.  **Unique Instances (Multi-Project Support)**:
    If you are running multiple .NET solutions simultaneously, give each instance a **unique name** (Stdio) or a **unique port** (SSE).
    
    *   **Instance A**: `./Server.exe --port=5001 --solution="ProjectA.sln"`
    *   **Instance B**: `./Server.exe --port=5002 --solution="ProjectB.sln"`

---

## 🧪 Verification

Roslyn Sentinel is backed by an exhaustive suite of **466 functional tests** (zero failures), ensuring that every toggle and every transformation is verifiably correct.

```bash
dotnet test
```

## 📜 Roadmap
See [UNFINISHED.md](./UNFINISHED.md) for the roadmap of complex data-flow refactorings and the **Intent-Based AST Command Model**.
