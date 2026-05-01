# Roslyn Sentinel

**Roslyn Sentinel** is a high-performance, persistent MCP (Model Context Protocol) server designed to give AI agents "Compiler-Grade Intelligence." It keeps your .NET solution "hot" in memory, maintaining an active `MSBuildWorkspace` to eliminate cold-start delays and provide deep semantic analysis across massive (300k+ LOC) codebases.

## 🚀 162 MCP Tools across 52 Specialized Engines

Roslyn Sentinel is built on a modular engine architecture, providing a vast library of surgical refactorings, architectural audits, modernizations, and code generation tools.

### 🏗️ Infrastructure & Workspace (26 tools)
*   **`PersistentWorkspaceManager`**: Manages the "Hot" solution, self-change tracking, and proactive FS synchronization.
*   **`SentinelConfiguration`**: Centralized Feature Toggle System for 40+ granular analysis rules.
*   **`ValidationEngine`**: Speculative in-memory compilation (CSXXXX diagnostic return) for Unified Diffs.
*   **Project/solution diagnostics**: `get_project_diagnostics`, `get_solution_diagnostics`, `split_project_by_folder`.
*   **Namespace management**: `fix_mismatched_namespaces`, `move_file_to_namespace_folder`.

### 🛠️ Refactoring — 37 tools ("The Surgical Suite")
*   **`RefactoringEngine`**: Rename (solution-wide), Safe-Delete (reflection-aware), Change Signature, Extract Method/Interface.
*   **`GranularRefactoringEngine`**: Introduce/Inline Field & Parameter, Extract to Partial.
*   **`MappingEngine`**: DTO ↔ entity mapping generation, invert assignments.
*   **`CodeFlowEngine`**: Reduce block nesting depth.
*   **`AdvancedRefactoringEngine`**: Optimize Task.Wait calls.
*   **`AdvancedStructuralEngine`**: Replace constructor with factory.

### ⚡ Modernization — 23 tools (.NET 8/9/10 & C# 12/13/14)
*   **`CodeStyleEngine`**: .NET 10 **Lock Modernization**, C# 14 **Field-Backed Properties**, **Implicit Span Cleanup**, Collection Expressions (`[]`).
*   **`SyntaxUpgradeEngine`**: Modern Guard Clauses (`ThrowIfNull`), Switch Expressions, `upgrade_to_modern_guards`, `convert_switch_to_expression`, `cleanup_implicit_spans`.
*   **`ModernizationEngine`**: Class-to-Record / Record-to-Class (POCO modernization).
*   **`IDEStyleEngine`**: Simplify member access chains.
*   **`ImmutabilityEngine`**: Convert mutable classes to immutable (init-only / records).
*   **`AsyncOptimizationEngine`**: `optimize_to_value_task`, `optimize_independent_awaits`, `generate_async_overload`.
*   **`ModernLoggingEngine`**: Convert to source-generated logging.
*   **`LogicOptimizationEngine`**: Simplify boolean expressions.
*   **`AdvancedLogicEngine`**: `convert_static_to_extension`, `invert_boolean_logic`.

### 🔍 Intelligence & Analysis — 36 tools
*   **`AnalysisEngine`**: Find large types/methods, duplicate methods, interface extraction candidates.
*   **`MetricsEngine`**: Code quality metrics (cyclomatic complexity, maintainability index, LCOM cohesion).
*   **`SymbolNavigationEngine`**: `get_call_graph`, `get_reverse_call_graph` (who calls this method), extension method discovery.
*   **`ArchitecturalEngine`**: `find_circular_dependencies` (Tarjan's SCC), `convert_to_background_service`.
*   **`DeadCodeEngine`**: Detect unreachable, unused, and untestable code.
*   **`AsyncSafetyEngine`**: Flag `.Result`, `.Wait()`, ConfigureAwait, `find_missing_cancellation_tokens`.
*   **`DependencyInjectionEngine`**: Analyze service lifetimes and DI correctness.
*   **`SemanticSearchEngine`**: Cross-solution symbol and usage search.

### 🔎 Quality & Anti-Patterns — 30 tools
*   **`AntiPatternEngine`**: `find_mutable_public_properties`, `find_naming_violations`, `find_string_magic_values`, `analyze_exception_handling`.
*   **`PerformanceEngine`**: Boxing detection, LINQ materialization, string concatenation in loops.
*   **`SecurityEngine`**: SQL injection, hard-coded secrets, unsafe deserialization.
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

Roslyn Sentinel is backed by an exhaustive suite of **146 functional tests**, ensuring that every toggle and every transformation is verifiably correct.

```bash
dotnet test
```

## 📜 Roadmap
See [UNFINISHED.md](./UNFINISHED.md) for the roadmap of complex data-flow refactorings and the **Intent-Based AST Command Model**.
