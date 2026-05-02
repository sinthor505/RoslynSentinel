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

---

## Session 12 — Regression Test Suite

Added `RegressionTests.cs` with 25 targeted regression tests covering every known bug fix and untested edge case:
- `ChangeSignature` call-site argument rewriting
- `ExtractInterface` block-style namespace handling
- `ConvertPropertySafe` virtual/override modifier preservation and contextSnippet disambiguation
- `InterpolateStringSafe` const format string resolution (the exact MS bug scenario)
- `MoveTypeToFile` interface/enum types; single-type-file boundary (engine bug fixed: same-path crash)
- `FindCallersSafe` overload disambiguation via contextSnippet
- `ImplementInterfaceSafe` partial implementation, property-only interfaces, read-only properties, override guard
- `FormatDocumentPreview` hunk content structure and clean-file consistency
- `GetDiagnosticsSummary` grouping and false-positive prevention

**Engine bug fixed:** `MoveTypeToFileAsync` crashed with `ArgumentException` (duplicate key) when moving a type whose name matched the source filename (e.g., `Solo` in `Solo.cs`). Now returns empty dict as a no-op in that case.

**Test count:** 448 passing, 0 failing.


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

*Note (session 9 — bug fixes): 4 correctness bugs fixed + 1 new tool added. (1) `Diagnose`/`GetHealthComponents` false negative: `MsBuildFound` now uses `MSBuildLocator.IsRegistered || instances.Any()` — previously returned false on SDK-only systems even when workspace was healthy; MSBuild-not-on-PATH downgraded from error→warning (code `W5001`). (2) `ExtractInterface` generated a bare `InterfaceDeclarationSyntax` with no `using` directives or namespace wrapper — now wraps output in a proper `CompilationUnitSyntax` with source file's usings and enclosing namespace (supports both file-scoped and block namespaces). (3) `ChangeSignature` was a complete stub (`return new Dictionary<string, string>()`); now fully implemented: finds method, validates permutation array, reorders parameter declarations, uses `SymbolFinder.FindReferencesAsync` to locate all call sites and reorders arguments to match. (4) New `ImplementInterfaceSafe` tool added to `CodeGenerationEngine`/`SentinelGenerationTools` — correctly generates `public` stubs with `throw new NotImplementedException()`, never adds `override` (which Microsoft's built-in `implement_interface` incorrectly adds). 8 new tests in `BugFixTests.cs`. Total: **405 passing tests, zero failures, 219 MCP tools across 55 engines**.*

*Note (session 10 — gap-filling tools): 6 new tools added to fill specific built-in tool gaps. (1) `ConvertPropertySafe` (`CodeGenerationEngine`/`SentinelGenerationTools`) — preserves initializers on `ToFullProperty` (moves to backing field initializer), preserves virtual/override/new modifiers; the built-in `convert_property` silently drops initializers. (2) `InterpolateStringSafe` — resolves const string format arguments via semantic model; the built-in `convert_to_interpolated_string` fails when the format string is a named const. (3) `FindCallersSafe` + (4) `FindImplementationsSafe` (`SymbolNavigationEngine`/`SentinelIntelligenceTools`) — locate symbol by name + optional contextSnippet without requiring line/column coordinates; built-in equivalents require a line number. (5) `FormatDocumentPreview` (`RefactoringEngine`/`SentinelRefactoringTools`) — returns a unified-diff-style hunk preview of what `format_document` would change without applying; useful for reviewing formatting impact. (6) `GetDiagnosticsSummary` (`DiagnosticEngine` injected into `SentinelQualityTools`) — groups Roslyn compiler diagnostics by ID across file/project/solution, sorted by count; reveals the most common error/warning codes at a glance. 13 new tests in `NewToolTests.cs`. Total: **418 passing tests, zero failures, 226 MCP tools across 55 engines**.*

*Note (session 11 — MoveTypeToFile content previews): `MoveTypeToFile` and `MoveAllTypesToFiles` tools updated to include `ContentPreviews` in their staged-change response. Previously the tool returned only a `ChangeId` and list of affected file paths (same null-snippet problem as the MS built-in), giving an AI no way to inspect the result before applying. Now both tools return an anonymous object with `ChangeId`, `Description`, `AffectedFiles`, and `ContentPreviews` (a dictionary mapping short filename → file content, truncated at 1500 chars for single moves and 15 lines for batch moves). The `autoStage=false` path continues to return the full raw content dictionary. 5 new tests added to `MoveTypeToFileTests.cs` covering: moved-type content, namespace/using preservation, source-file retention, batch moves, and non-empty content guarantees. Total: **423 passing tests, zero failures, 226 MCP tools**.*

*Note (session 12 — MsToolAugmentEngine, 5 augmented tools + regression hardening): `MsToolAugmentEngine.cs` added with 5 complete implementations that fix critical MS roslyn-mcp bugs: (1) `EncapsulateFieldSafeAsync` — generates correctly-named `_camelCase` backing field (MS version generates self-referential code with same name); (2) `AnalyzeSwitchForPatternConversionAsync` — pre-flight safety check for multi-assignment cases MS silently mishandles; (3) `ConvertSwitchToPatternSafeAsync` — rejects multi-assignment cases rather than generating broken code; (4) `ConvertStringFormatToInterpolatedSmartAsync` — resolves const string format arguments via semantic model (MS version fails on named consts); (5) `SortAndDeduplicateUsingsAsync` — sorts AND deduplicates in one pass (MS `sort_usings` does not deduplicate). `SentinelAugmentTools.cs` MCP wrapper exposes all 5 as named tools. 14 regression tests added to `RegressionTests.cs`. Also fixed a `GranularRefactoringEngine.cs` drop of variable declarations that caused silent failures. Total: **462 passing tests, zero failures, 231 MCP tools**.*

*Note (session 14 — startup timeout fix + 5 new MS augmented tools): Fixed critical bug where `PersistentWorkspaceManager` constructor called `MSBuildLocator.RegisterDefaults()` lazily, causing `tools/list` to take 8+ seconds on first call. Copilot CLI timed out before the response arrived, so Sentinel tools never appeared in the session. Fix: eagerly resolve `PersistentWorkspaceManager` via DI after `host.Build()` in `Program.cs`, so MSBuild loads at startup before any connections are accepted. Also added 5 new augmented tools to `MsToolAugmentEngine.cs` + `SentinelAugmentTools.cs` to fix additional confirmed MS roslyn-mcp bugs: (1) `FormatDocumentSafeAsync` — adds true preview=true support (MS `format_document` has no preview param, always applies); (2) `AnalyzeForeachForLinqConversionAsync` — pre-flight safety check that detects when MS `convert_foreach_linq` would silently destroy pre-loop `.Add()` calls by re-initializing the collection; (3) `GetWorkspaceHealthAsync` — reports actual project/document counts from workspace (MS `diagnose` reports healthy=false based on MSBuild path check even when all projects load fine); (4) `PreviewAddMissingUsingsAsync` — genuine read-only preview of which usings would be added (MS `add_missing_usings` with preview:true writes to disk anyway, ignoring the flag); (5) `ExtractConstantSafeAsync` — extracts literal to named const using contextSnippet instead of line/column offsets (MS version throws cryptic "Column N beyond end of line" errors). 11 regression tests added (Section 16 in `RegressionTests.cs`). Total: **477 passing tests, zero failures, 236 MCP tools**.*

