# Roslyn Sentinel

**Roslyn Sentinel** is a high-performance, persistent MCP (Model Context Protocol) server designed to give AI agents "Compiler-Grade Intelligence." It keeps your .NET solution "hot" in memory, maintaining an active `MSBuildWorkspace` to eliminate cold-start delays and provide deep semantic analysis across massive (300k+ LOC) codebases.

## 🚀 300+ Atomic Capabilities across 45+ Specialized Engines

Roslyn Sentinel is built on a modular engine architecture, providing a vast library of surgical refactorings, architectural audits, and modernizations.

### 🏗️ Infrastructure & Workspace
*   **`PersistentWorkspaceManager`**: Manages the "Hot" solution, self-change tracking, and proactive FS synchronization.
*   **`SentinelConfiguration`**: Centralized Feature Toggle System for 40+ granular analysis rules.
*   **`ValidationEngine`**: Speculative in-memory compilation (CSXXXX diagnostic return) for Unified Diffs.
*   **`DiffEngine`**: Industrial-grade Unified Diff generation and parsing.
*   **`DiagnosticEngine`**: Solution-wide aggregation of compiler errors, warnings, and information.
*   **`SolutionManagementEngine`**: Automated project creation, solution loading, and metadata mapping.
*   **`HealthOrchestrationEngine`**: Parallelized, paged solution-health diagnostics.

### 🛠️ Refactoring ("The Surgical Suite")
*   **`RefactoringEngine`**: Scalpel-mode member replacement, Rename (solution-wide), Safe-Delete (reflection-aware), Change Signature.
*   **`AdvancedRefactoringEngine`**: Extract Superclass, Interface extraction.
*   **`GranularRefactoringEngine`**: Introduce/Inline Field & Parameter, Extract to Partial, Move to Outer Scope.
*   **`StandardRefactoringEngine`**: Make Method Static, Encapsulate Field.
*   **`AdvancedStructuralEngine`**: Class Inlining, Member Pull-up/Push-down (Basic).
*   **`StructuralRefinementEngine`**: Filename-to-Type synchronization.
*   **`SemanticRefactoringLibrary`**: Wrap in Using, Inline Temporary Variable.

### ⚡ Modernization (.NET 8/9/10 & C# 12/13/14)
*   **`CodeStyleEngine`**: .NET 10 **Lock Modernization**, C# 14 **Field-Backed Properties**, **Implicit Span Cleanup**, Collection Expressions (`[]`).
*   **`SyntaxUpgradeEngine`**: Modern Guard Clauses (`ThrowIfNull`), Pattern Matching, Switch Expressions, Throw expressions.
*   **`ModernizationEngine`**: Class-to-Record / Record-to-Class (POCO modernization).
*   **`ModernLoggingEngine`**: Source-generated and structured logging upgrades.
*   **`ModernizationUpgradeEngine`**: Global usings, namespace simplification.

### 🔍 Intelligence & Analytics
*   **`ImpactAnalyzer`**: Semantic Blast Radius calculation (traces breaking changes across projects).
*   **`AnalysisEngine`**: Boxing detection, Large Type/Method audits, Uninstantiated types, Unused interfaces.
*   **`MetricsEngine`**: Cyclomatic complexity, maintainability index, solution-wide LOC.
*   **`SemanticSearchEngine`**: Search by Return Type, Attribute, or Regex.
*   **`InventoryEngine`**: Complete symbol inventory (Classes, Interfaces, Methods).
*   **`ArchitecturalEngine`**: Circular dependency detection, layer violation audits.

### 🛡️ Quality, Safety & Security
*   **`AsyncSafetyEngine`**: `async void` detection, Mismatched await, Sequential await parallelization.
*   **`ThreadSafetyEngine`**: Dangerous `lock(this)` detection, Nested locks, Semaphore leak audits.
*   **`SecurityEngine`**: SQL Injection detection (interpolation checks), hardcoded paths, credential scanning.
*   **`PerformanceEngine`**: Boxing allocation tracking, string builder optimization, LINQ-to-Loop conversion.
*   **`AsyncOptimizationEngine`**: `ConfigureAwait(false)` and `ValueTask` migration.

### 🧬 Generation & Automation
*   **`CodeGenerationEngine`**: JSON-to-Class, DTO, and POCO generation.
*   **`ApiAutomationEngine`**: HttpClient generation, Minimal API scaffolding.
*   **`TestingEngine`**: Unit test skeleton generation, BenchmarkDotNet stubs.
*   **`CodeHealingEngine`**: Automated retry policy (Polly) injection, strong-typed Exception generation.
*   **`DocumentationEngine`**: XML Doc generation and Markdown export.

---

## ⚙️ Configuration for AI Agents

### **Feature Toggles**
Enable/Disable any of the 300+ rules globally:
- `list_features()`: See all rules and their current status.
- `update_features(List<KeyValuePair<string, bool>> updates)`: Batch update statuses.

### **Claude Desktop / Windsurf / Cursor**
Add the server with unique names per solution to maintain isolation:
```json
{
  "mcpServers": {
    "sentinel-project-a": {
      "command": "dotnet",
      "args": [
        "C:/path/to/RoslynSentinel/publish/RoslynSentinel.Server.dll",
        "--solution=C:/repos/ProjectA/ProjectA.sln",
        "--mode=all"
      ]
    }
  }
}
```

---

## 🧪 Verification

Roslyn Sentinel is backed by an exhaustive suite of **142+ functional tests**, including:
*   **Massive Suites**: Stress tests for Intelligence, Modernization, Quality, and Refactoring.
*   **DeepFunctional**: Exact AST transformation verification.
*   **SolutionWide**: Paging and project aggregation audits.

```bash
dotnet test
```

## 📜 Unfinished Capabilities
See [UNFINISHED.md](./UNFINISHED.md) for the roadmap of complex data-flow refactorings and the **Intent-Based AST Command Model**.
