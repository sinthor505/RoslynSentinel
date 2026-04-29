# Unfinished Capabilities (Backlog)

The following advanced capabilities require deep data-flow analysis or cross-file AST rewriting that is currently in the architectural planning phase.

## 🚀 Future Architectural Vision: Intent-Based AST Commands

The core goal of Roslyn Sentinel's evolution is to shift from the **String-Replacement Model** (where the AI sends raw C# strings) to an **Intent-Based AST Command Model**. 

### The Problem
AI agents currently provide raw C# strings during refactoring. This frequently leads to:
1.  **Syntax Errors**: Truncated responses or missing braces/semicolons.
2.  **Formatting Drift**: Inconsistent indentation or violation of `.editorconfig`.
3.  **Trivia Loss**: Accidental deletion of comments and XML documentation.

### The Solution: "Refactor Recipes"
In the future, the AI will provide a structured manifest of logical changes. Roslyn will then perform the heavy-duty AST manipulation, ensuring structural perfection and metadata preservation.

**Planned Recipe Ops:**
- `InjectDependency`: Injects a private readonly field and adds it to the primary or standard constructor.
- `AddGuard`: Injects a `ThrowIfNull` or range-check at the start of a specific member.
- `WrapInTryCatch`: Wraps a specific range of statements in a try-catch with specific logging/re-throw logic.
- `ApplyAttribute`: Adds an attribute to a class/method, automatically resolving the required `using` namespace.

---

## 🛠️ Advanced AST Refactoring (Backlog)
- **`PullUpMember` / `PushDownMember`**: Moves methods/fields through a type hierarchy while updating all references solution-wide.
- **`InlineMethod` (Deep)**: Handles complex inlining for methods with multiple return points, local variable collisions, and generic parameters.
- **`ExtractInterface` (Full)**: Automatically detects implementers and updates all variable declarations to use the new interface type instead of the concrete class.
- **`OrganizeImports` (Solution-Wide)**: Implements custom grouping and sorting rules for `using` directives across 100+ files in one pass.

## ⚡ High-Level Optimization (Backlog)
- **`UseSpanForParsing`**: Detects legacy string manipulation (Substring, Split, Regex) and upgrades it to zero-allocation `ReadOnlySpan<char>` and `Memory<T>`.
- **`AutoParallelize`**: Scans sequential data processing loops and automatically converts them to `Parallel.ForEach` or `Task.WhenAll` if thread-safe.
- **`VectorizeLoop`**: Identifies low-level numerical loops and suggests using **SIMD** (System.Runtime.Intrinsics) for hardware-accelerated performance.

## 🔍 Advanced Analysis & Visuals (Backlog)
- **`VisualDependencyGraph`**: Generates a Mermaid.js or Graphviz representation of the project and type dependency tree.
- **`AOTCompatibilityAudit`**: Scans for patterns (Reflection, Dynamic) that would prevent **Native AOT** compilation.

---
*Note: The following 40+ items have been promoted to the core engine suite and are now fully functional:*
*   **Modernization**: `.NET 10 Lock Modernization`, `C# 14 Field-Backed Properties`, `Implicit Span Cleanup`, `TimeProvider Injection`.
*   **Logical**: `BooleanInversion (Solution-Wide)`, `FlattenIfsToSwitch`, `ConvertPropertyToMethods`.
*   **Orchestration**: `Paged Health Reports`, `Feature Toggle System`, `Parallel Project Scanning`.
*   **Quality**: `BoxingAllocation Detection`, `ReflectionUsage Audit`, `MismatchedAwait Safety`.
