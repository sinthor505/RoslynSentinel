# Unfinished Capabilities (Backlog)

The following advanced capabilities require deep data-flow analysis or cross-file AST rewriting that is currently in the architectural planning phase.

## Advanced AST Refactoring
- `PullUpMember` / `PushDownMember`: Moves members through type hierarchies with reference updates.
- `InlineMethod` (Deep): Replaces all call sites with the method's body while managing scope, parameters, and variable naming for complex methods.
- `OrganizeImports`: Groups and sorts using directives globally across the solution based on custom rules.

## High-Level Optimization
- `UseSpanForParsing`: Automatically upgrades string manipulation logic to use `Span<char>` and `Memory<T>` for zero-allocation parsing.
- `AutoParallelize`: Identifies sequential data processing loops that can be safely converted to `Parallel.ForEach` or `Task.WhenAll`.
- `VectorizeLoop`: Suggests using SIMD (System.Runtime.Intrinsics) for compute-heavy numerical loops.

## Advanced Dependency Analysis
- `VisualDependencyGraph`: Generates a Mermaid.js or Graphviz representation of the project dependency tree.

---
*Note: The following items have been promoted to the core engine suite and are now fully functional:*
*   **Logical**: `InvertBooleanLogic`, `FlattenIfsToSwitch`, `ConvertPropertyToMethods`.
*   **Dependency**: `CheckPackageInconsistency`, `FindUnusedReferences`.
*   **Modernization**: `LockModernization (.NET 10)`, `FieldBackedProperties (C# 14)`, `ImplicitSpanCleanup`, `SafeDelete`.

## 🚀 Future Architectural Vision: Intent-Based AST Commands

The goal is to shift from the **String-Replacement Model** (where the AI sends raw C# strings) to an **Intent-Based AST Command Model**. This eliminates syntax errors caused by malformed strings and allows Roslyn to handle the heavy lifting of code generation, formatting, and trivia preservation.

### Concept: The "Refactor Recipe"
Instead of re-emitting code, the AI provides a structured manifest of logical changes.

**Example: Dependency Injection & Method Guard**
```json
{
  "target": "OrderService",
  "actions": [
    { 
      "op": "InjectDependency", 
      "type": "ILogger<OrderService>", 
      "name": "_logger", 
      "access": "private readonly" 
    },
    { 
      "op": "AddGuard", 
      "member": "ProcessOrder", 
      "param": "order", 
      "type": "NullCheck" 
    }
  ]
}
```
