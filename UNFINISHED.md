# Unfinished Capabilities (Stubbed / Simulated)

The following capabilities were identified as highly valuable for an AI-native Roslyn MCP server but require complex, deep syntax tree transformations (AST manipulation), data-flow analysis, or integration with external Roslyn packages (`Microsoft.CodeAnalysis.Features`) that are not easily implementable in a single pass. 

These have been removed from the active MCP toolset to prevent hallucinations or empty responses, and are listed here for future implementation.

## Advanced Refactoring & Logic
- `InlineVariable`: Inlines a local temporary variable into all its usages within the scope.
- `ConvertPropertyToMethods`: Converts a simple property into a formal GetX() / SetX() method pair and updates references.
- `WrapInUsing`: Wraps a line range of code in a using statement for an IDisposable object.
- `InvertBooleanLogic`: Inverts the meaning of a boolean variable and automatically updates all its logical usages solution-wide.
- `FlattenIfsToSwitch`: Flattens nested if/else chains into a modern, concise switch expression.
- `ExtensionToStatic`: Converts an extension method into a standard static method call.
- `PullUpMember` / `PushDownMember`: Moves a member to a base class or derived class.
- `InlineMethod`: Replaces all call sites with the method's body.

## Code Style & Modernization (IDE Rules)
- `SimplifyAllNames`: Simplifies all names and member access in a file according to IDE style rules.
- `UseCollectionExpressions`: Converts array and collection creation to modern C# collection expressions (`[...]`).
- `UseSpanForParsing`: Upgrades string manipulation logic to use `Span<char>` for zero-allocation performance.
- `UseSwitchExpression`: Upgrades standard switch statements to modern C# switch expressions (IDE0066).

## Dependency & Project Management
- `FindUnusedReferences`: Identifies NuGet package references in a project that are not being used by the code.
- `CheckPackageInconsistency`: Checks for NuGet package version inconsistencies across multiple projects.
- `SafeDeleteSymbol`: Safely deletes a symbol only if it has zero usages in the entire codebase.
- `SyncTypeAndFilename`: Synchronizes the filename to match the primary type declared within it.
- `FindCircularDependencies`: Identifies circular project references (`A -> B -> A`).

## Performance & Analysis
- `OptimizeResourceDisposal`: Scans for `IDisposable` objects that are not properly disposed of within a using block or statement.
- `FindBoxingAllocations`: Finds potential boxing allocations (e.g., value types assigned to objects).
- `GenerateCallTree`: Generates a markdown call tree for a specific method for documentation purposes.
- `FindUninstantiatedTypes`: Identifies classes that are never instantiated across the entire solution.
- `DocumentPocoFields`: Adds comprehensive `[Description]` or XML comments to all fields in a POCO class.
- `DetectUnreachableCode`: Analyzes control flow to detect unreachable code paths.
- `ConvertIfToSwitchExpression`: Converts a simple if/else if structure into a concise switch expression.
- `DetectLongParameterLists`: Detects methods with too many parameters and suggests a Parameter Object.
- `DetectInefficientStringComparisons`: Scans for common string comparison pitfalls.

## Massive Analyzer & Refactoring Suites (Simulated)
To prevent overwhelming the LLM context window with 300+ tools, the programmatic exposure of every single `IDE0xxx`, `EPCxxx`, and Visual Studio Code Refactoring (e.g., `ExtractMethod`, `ChangeSignature`, `InlineClass`) has been documented here instead of being exposed as individual MCP endpoints. 
Implementing these robustly requires integrating the `Microsoft.CodeAnalysis.Features` and `Microsoft.CodeAnalysis.CSharp.Features` workspaces libraries and dynamically querying the `CodeRefactoringService` and `DiagnosticAnalyzerService`.
