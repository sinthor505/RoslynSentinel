# Unfinished Capabilities (Backlog)

This document tracks the massive 300+ atomic refactoring and diagnostic targets currently planned for Roslyn Sentinel.

## 🚀 Future Architectural Vision: Intent-Based AST Commands
The server is transitioning to a "Refactor Recipe" model where AI agents issue high-level intents (e.g., `InjectDependency`, `AddGuard`, `WrapInTryCatch`) and Roslyn handles the structural manipulation, formatting, and trivia preservation.

---

## 🛠️ Advanced Refactoring Suite (Backlog)

### **Easy Difficulty**
- `ConvertIndexerToMethod`: Replaces indexer with `GetX(int index)` and `SetX(int index, T value)`.
- `ConvertPropertyToAutoProperty`: Removes manual backing fields if logic is trivial.
- `CopyType`: Creates a deep structural clone of a type in a new file.
- `Add/Remove params`: Toggles the `params` modifier on the final array parameter.
- `IDE0011 (Add Braces)`: Surgically adds braces to all single-line `if`, `foreach`, and `while` blocks.
- `EPC33 (Sleep in Async)`: Detects and replaces `Thread.Sleep` with `Task.Delay` in async contexts.

### **Medium Difficulty**
- `ConvertInterfaceToAbstractClass`: Adds method bodies and basic inheritance structure.
- `ReplaceConstructorWithFactoryMethod`: Moves instantiation logic to a static `Create` method.
- ~~`TransformParameters`: Wraps a group of parameters into a new Parameter Object (DTO).~~ ✅ **Done** — `introduce_parameter_object` in `GranularRefactoringEngine`
- `SpecificExceptionCatching`: Traces method calls to identify exact exception types and expands generic `catch(Exception)` blocks.
- `LocalFunctionMigration`: Automatically moves anonymous lambdas into structured `local functions`.
- `EPC26 (Tasks in Using)`: Detects unawaited tasks inside a `using` block that might outlive the disposal.

### **Hard Difficulty**
- `MoveInstanceMethod`: Moves a method to another type and updates every call site globally by resolving the new object reference.
- `UseBaseTypeWherePossible`: Scans the entire solution to find where a variable declaration can be safely down-cast to a more generic base type/interface.
- `UseSpanForParsing`: Refactors legacy string manipulation (Substrings/Regex) into high-performance `ReadOnlySpan<char>` logic.
- `AutoParallelize`: Identifies CPU-bound sequential loops and converts them to `Parallel.ForEach`.
- `InlineSQLValidation`: Cross-references string-embedded SQL against a provided schema for type and size consistency.

---

## 🔍 IDE & Static Analysis Targets (Backlog)

### **Modernization (C# 14 / .NET 10)**
- `IDE0042`: Deconstruct variable declarations.
- `IDE0050`: Convert anonymous types to Tuples.
- `IDE0210`: Convert to top-level statements.
- `IDE0250`: Automatically mark structs as `readonly`.
- `IDE0340`: Use unbound generic types in `nameof`.

### **Async/Wait Performance (EPC Suite)**
- `EPC16`: Flag awaiting null-conditional expressions (NullRef risk).
- `EPC18`: Flag implicit Task-to-String conversions.
- `EPC31`: Prevent returning null for Task-like types.
- `EPC32`: TaskCompletionSource configuration audits.

---
*Note: Over 30+ additional tools were promoted to core in this session by wrapping existing engine methods that had never been exposed as MCP tools: `SimplifyMemberAccess`, `MakeClassImmutable`, `OptimizeToValueTask`, `OptimizeIndependentAwaits`, `ReduceBlockDepth`, `OptimizeTaskWait`, `ReplaceConstructorWithFactory`, `InvertAssignments`, `GenerateDefaultConfigJson`, `GenerateAsyncOverload`, `AddValidationToPoco`, `GetProjectDiagnostics`, `GetSolutionDiagnostics`, `SplitProjectByFolder`, `ConvertToBackgroundService`, `FixMismatchedNamespaces`, `MoveFileToNamespaceFolder`, `UpgradeToModernGuards`, `ConvertSwitchToExpression`, `CleanupImplicitSpans`, `ConvertToSourceGeneratedLogging`, `SimplifyBooleanExpressions`, `ConvertStaticToExtension`, `InvertBooleanLogic`, plus 5 genuinely new tools: `GetReverseCallGraph`, `FindStringMagicValues`, `FindMissingCancellationTokens`, `AnalyzeExceptionHandling`, `GenerateDecoratorClass`. Total tool count is now 162.*

*Note (subsequent session): 24 stub engine methods were replaced with real implementations across 7 engines: `AsyncSafetyEngine` (`FindTaskYieldUsage`, `FindTaskDelayUsage`, `FindTaskDelayZeroUsage`, `FindTaskWhenAllUsage`), `SecurityEngine` (`AnalyzeSecurityAsync`, `CheckForSqlInjectionAsync`), `DeadCodeEngine` (`FindUnusedPrivateMembersAsync`, `FindUnusedConstructorsAsync`, `CheckForUnusedEventSubscriptionsAsync`), `AnalysisEngine` (`DetectMemoryLeaksAsync`, `FindPossibleInfiniteLoopsAsync`, `GenerateEqualityOverridesAsync`, `GenerateCallTreeAsync`), `GranularRefactoringEngine` (`IntroduceFieldAsync`, `IntroduceParameterAsync`, `IntroduceVariableAsync`), `AdvancedRefactoringEngine` (`ReplaceStringConcatWithInterpolationAsync`, `OptimizeTaskWaitAsync`), `RefinementEngine` (`PullUpMemberAsync`). 63 comprehensive tests were added in `NewImplementationsTests.cs`, bringing the total to 205 passing tests.*

## Still-Stub Methods (Skipped — no real implementation in engine)
These engine methods exist as stubs/no-ops and were not wrapped:
- `ConvertForToForEach`, `ConvertWhileToFor` (AdvancedLogicEngine)
- `InlineClassAsync` (AdvancedStructuralEngine)
- `UseSpanForParsing` (ModernizationUpgradeEngine)
- `UseThrowExpressions`, `UseNullPropagation` (IDEStyleEngine/ModernizationUpgradeEngine)
- `AddRetryPolicy`, `RunSpecificRule`, `RunMicroRefactoring`
- `ModernizationUpgradeEngine.ConvertSwitchToExpression` — duplicate name conflict with `SyntaxUpgradeEngine` version

*Note (session 4 — surgical code editing batch 2): 12 new MCP tools added via 13 new engine methods on `RefactoringEngine`: `RemoveAttribute`, `RemoveBaseType`, `ChangeAccessibility`, `AddModifier`, `RemoveModifier`, `AddSummaryComment`, `AddProperty`, `AddField`, `SortMembers`, `WrapInTryCatch`, `AddConstructorParameter`, `WrapInRegion`. 38 new tests added in `CodeEditingTests.cs`. Total: **277 passing tests, 188 MCP tools across 53 engines**.*

*Note (session 6 — bug fixes + 16 new high-ROI tools): Fixed 5 existing bugs (`MakeMethodThreadSafe` hardcoded lock name, `FindTaskWhenAllUsage` false positives on dependent awaits, `OptimizeToValueTask` unsafe blind replace, `OptimizeIndependentAwaits` missing `var x = await` pattern, `GenerateAsyncOverload` Task.Run stub). Added 11 easy tools: `find_best_insertion_point` (DiscoveryEngine), `find_todo_fixme_comments` (DiscoveryEngine), `preview_rename_impact` (DiscoveryEngine), `find_async_over_sync` (AsyncSafetyEngine), `find_unawaited_fire_and_forget` (AsyncSafetyEngine), `find_long_parameter_list` (AntiPatternEngine), `find_primitive_obsession` (AntiPatternEngine), `find_inconsistent_async_suffix` (AntiPatternEngine), `find_use_frozen_collections` (CodeStyleEngine), `use_exception_expressions` (SyntaxUpgradeEngine), `update_xml_docs_from_signature` (RefactoringEngine). Added 5 medium tools: `detect_valuetask_misuse` (AsyncSafetyEngine), `upgrade_to_primary_constructor` (SyntaxUpgradeEngine), `add_cancellation_token_to_method` (AsyncOptimizationEngine), `sync_interface_to_implementation` (RefactoringEngine), `introduce_parameter_object` (GranularRefactoringEngine). 60 new tests added across `ToolGapsTests.cs` and `AdvancedToolsTests.cs`. Total: **346 passing tests, 214 MCP tools across 55 engines**.*
