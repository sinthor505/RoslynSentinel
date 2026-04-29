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
