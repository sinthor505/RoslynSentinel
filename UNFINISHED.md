# Unfinished Capabilities (Backlog)

The following advanced capabilities require deep data-flow analysis or cross-file AST rewriting that is currently in the architectural planning phase.

## Advanced AST Refactoring
- `InvertBooleanLogic`: Inverts the meaning of a boolean variable and automatically updates all its logical usages solution-wide.
- `FlattenIfsToSwitch`: Flattens complex, deeply nested if/else chains into modern switch expressions.
- `PullUpMember` / `PushDownMember`: Moves members through type hierarchies with reference updates.
- `InlineMethod`: Replaces all call sites with the method's body while managing scope and variable naming.
- `ConvertPropertyToMethods`: Converts properties to formal Get/Set pairs for legacy API compatibility.

## High-Level Optimization
- `UseSpanForParsing`: Automatically upgrades string manipulation logic to use `Span<char>` and `Memory<T>` for zero-allocation parsing.
- `AutoParallelize`: Identifies sequential data processing loops that can be safely converted to `Parallel.ForEach` or `Task.WhenAll`.

## Advanced Dependency Analysis
- `FindUnusedReferences`: Identifies NuGet package references in a project that are not actually being used by any code.
- `CheckPackageInconsistency`: Detects and resolves version conflicts for shared dependencies across 50+ projects.

---
*Note: Many previous "Unfinished" items (SafeDelete, TimeProvider, Collection Expressions, Circular Dependencies, Boxing detection) have been moved into the core high-performance engine suite and are now fully functional.*

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
    },
    { 
      "op": "WrapInTryCatch", 
      "member": "ProcessOrder", 
      "exception": "OrderException", 
      "logInside": "_logger.LogError(ex, \"Order failed\")" 
    }
  ]
}
```

### Advantages for AI Agents:
1.  **Zero Syntax Errors**: The AI never writes a semicolon or brace; Roslyn generates them.
2.  **Token Efficiency**: Only the *delta* is transmitted, not the entire method body.
3.  **Automatic Formatting**: Uses the project's existing `.editorconfig` rules automatically.
4.  **Trivia Safety**: Comments and doc-strings are never "eaten" or displaced.
