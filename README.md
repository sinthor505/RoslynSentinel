# Roslyn Sentinel

**Roslyn Sentinel** is a high-performance, persistent MCP (Model Context Protocol) server designed to give AI agents "Compiler-Grade Intelligence." It keeps your .NET solution "hot" in memory, maintaining an active `MSBuildWorkspace` to eliminate cold-start delays and provide deep semantic analysis across massive (300k+ LOC) codebases.

## 🚀 176 MCP Tools across 53 Specialized Engines

Roslyn Sentinel is built on a modular engine architecture, providing a vast library of surgical refactorings, architectural audits, modernizations, and code generation tools.

### 🏗️ Infrastructure & Workspace (26 tools)
*   **`PersistentWorkspaceManager`**: Manages the "Hot" solution, self-change tracking, and proactive FS synchronization.
*   **`SentinelConfiguration`**: Centralized Feature Toggle System for 40+ granular analysis rules.
*   **`ValidationEngine`**: Speculative in-memory compilation (CSXXXX diagnostic return) for Unified Diffs.
*   **Project/solution diagnostics**: `get_project_diagnostics`, `get_solution_diagnostics`, `split_project_by_folder`.
*   **Namespace management**: `fix_mismatched_namespaces`, `move_file_to_namespace_folder`.

### 🛠️ Refactoring — 44 tools ("The Surgical Suite")
*   **`RefactoringEngine`**: Rename (solution-wide), Safe-Delete (reflection-aware), Change Signature, Extract Method/Interface.
*   **`GranularRefactoringEngine`**: `introduce_field`, `introduce_parameter`, `introduce_variable` — promote expressions to named locals/fields/parameters at a given line+column.
*   **`RefinementEngine`**: `pull_up_member` — move a method from derived class to base class, adding `virtual` and removing `override`.
*   **`AdvancedRefactoringEngine`**: Replace string concatenation with interpolation; optimize `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` to `await`.
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
    - `add_attribute` — attach `[ApiController]`, `[Required]`, etc. to any type or member
    - `add_base_type` — add a base class or interface to a type's inheritance list (idempotent)

### ⚡ Modernization — 23 tools (.NET 8/9/10 & C# 12/13/14)
*   **`CodeStyleEngine`**: .NET 10 **Lock Modernization**, C# 14 **Field-Backed Properties**, **Implicit Span Cleanup**, Collection Expressions (`[]`).
*   **`SyntaxUpgradeEngine`**: Modern Guard Clauses (`ThrowIfNull`), Switch Expressions, `upgrade_to_modern_guards`, `convert_switch_to_expression`, `cleanup_implicit_spans`.
*   **`ModernizationEngine`**: Class-to-Record / Record-to-Class (POCO modernization).
*   **`IDEStyleEngine`**: Simplify member access chains; `use_object_initializers` — converts `new T()` + consecutive property assignments into object initializer syntax.
*   **`ImmutabilityEngine`**: Convert mutable classes to immutable (init-only / records).
*   **`AsyncOptimizationEngine`**: `optimize_to_value_task`, `optimize_independent_awaits`, `generate_async_overload`.
*   **`ModernLoggingEngine`**: Convert to source-generated logging.
*   **`LogicOptimizationEngine`**: Simplify boolean expressions.
*   **`AdvancedLogicEngine`**: `convert_static_to_extension`, `invert_boolean_logic`, `convert_foreach_to_for` — rewrite `foreach` over an indexed collection to an equivalent `for` loop with index variable.

### 🔍 Intelligence & Analysis — 40 tools
*   **`AnalysisEngine`**: Find large types/methods, duplicate methods, interface extraction candidates, **memory leak detection** (event subscription without `IDisposable`), **infinite loop detection**, **call tree generation**, **equality override generation** (`HashCode.Combine`).
*   **`MetricsEngine`**: Code quality metrics (cyclomatic complexity, maintainability index, LCOM cohesion).
*   **`SymbolNavigationEngine`**: `get_call_graph`, `get_reverse_call_graph` (who calls this method), extension method discovery.
*   **`ArchitecturalEngine`**: `find_circular_dependencies` (Tarjan's SCC), `convert_to_background_service`.
*   **`DeadCodeEngine`**: Unused private members/constructors (with DI false-positive avoidance), unmatched event subscriptions (`+=` without `-=`).
*   **`AsyncSafetyEngine`**: Flag `.Result`, `.Wait()`, `ConfigureAwait`, `find_missing_cancellation_tokens`; detect `Task.Yield`, `Task.Delay`, `Task.Delay(0)`, and sequential `await` patterns better served by `Task.WhenAll`.
*   **`DependencyInjectionEngine`**: Analyze service lifetimes, DI correctness, and `find_services_not_registered` — heuristically detects constructor-injected services that are never registered in the DI container (catches the missing-registration bug class).
*   **`DiscoveryEngine`**: `find_all_throw_sites` (find every throw across a file/project/solution, filterable by exception type), `find_object_creation_sites` (find every `new T()` for a named type), `get_public_api_surface` (enumerate all public types/methods/properties in a project for API audits).
*   **`SemanticSearchEngine`**: Cross-solution symbol and usage search.

### 🔎 Quality & Anti-Patterns — 33 tools
*   **`AntiPatternEngine`**: `find_mutable_public_properties`, `find_naming_violations`, `find_string_magic_values`, `analyze_exception_handling`.
*   **`PerformanceEngine`**: Boxing detection, LINQ materialization, string concatenation in loops.
*   **`SecurityEngine`**: SQL injection (dynamic/interpolated strings in SQL calls, `check_for_sql_injection`), hardcoded secrets (name-pattern matching), weak hash algorithms (MD5/SHA1), insecure `new Random()` in security-sensitive contexts.
*   **`TestingEngine`**: Missing assertions, test code smell detection.

### 🏭 Code Generation — 10 tools
*   **`CodeGenerationEngine`**: Fluent builder, default config JSON, decorator class generation.
*   **`ApiIntegrationEngine`**: `add_validation_to_poco` — add `[Required]`/`[Range]` annotations to plain objects.
*   **`AsyncOptimizationEngine`**: Generate async method overloads.

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

Roslyn Sentinel is backed by an exhaustive suite of **205 functional tests**, ensuring that every toggle and every transformation is verifiably correct.

```bash
dotnet test
```

## 📜 Roadmap
See [UNFINISHED.md](./UNFINISHED.md) for the roadmap of complex data-flow refactorings and the **Intent-Based AST Command Model**.
