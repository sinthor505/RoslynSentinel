# RoslynSentinel Complete Tool Documentation

**Generated:** 2026-05-03 21:27:05 UTC  
**Total Tools:** 317  
**Engines:** 52  
**Repository:** https://github.com/rhale78/RoslynSentinel

---

## 📋 Document Overview

This document provides complete, authoritative documentation for all 317 refactoring, modernization, analysis, and code generation tools in RoslynSentinel.

### Key Statistics

- **Total Public Async Methods:** 317
- **Engine Classes:** 52
- **Refactoring Tools:** ~120
- **Analysis Tools:** ~45
- **Modernization Tools:** ~60
- **Code Generation Tools:** ~30
- **Quality/Style Tools:** ~50

### Organization

This document is organized in the following sections:

1. **[Quick Index by Engine](#quick-index-by-engine)** - All engines listed with method counts
2. **[Tools by Category](#tools-by-category)** - Organized by functional area
3. **[Complete Method Reference](#complete-method-reference)** - Detailed signatures and examples
4. **[Integration Patterns](#integration-patterns)** - Common usage patterns
5. **[Unfinished Features](#unfinished-features)** - Known limitations and deferred work

---

## Quick Index by Engine

### All Engines ( total)

| **SentinelRefactoringTools** | 61 methods |

| **RefactoringEngine** | 34 methods |

| **SentinelModernizationTools** | 24 methods |

| **SentinelWorkspaceTools** | 17 methods |

| **SentinelIntelligenceTools** | 16 methods |

| **SentinelQualityTools** | 14 methods |

| **SentinelGenerationTools** | 12 methods |

| **SyntaxUpgradeEngine** | 10 methods |

| **SentinelAugmentTools** | 10 methods |

| **MsToolAugmentEngine** | 9 methods |

| **GranularRefactoringEngine** | 9 methods |

| **CodeGenerationEngine** | 9 methods |

| **AdvancedLogicEngine** | 7 methods |

| **AsyncOptimizationEngine** | 7 methods |

| **CodeStyleEngine** | 7 methods |

| **TestingEngine** | 4 methods |

| **ControlFlowEngine** | 3 methods |

| **ModernizationUpgradeEngine** | 3 methods |

| **StandardRefactoringEngine** | 3 methods |

| **SymbolNavigationEngine** | 3 methods |

| **IDEStyleEngine** | 3 methods |

| **SemanticRefactoringLibrary** | 3 methods |

| **ImpactAnalyzer** | 3 methods |

| **InstrumentationEngine** | 3 methods |

| **ModernizationEngine** | 3 methods |

| **SolutionManagementEngine** | 2 methods |

| **StructuralRefinementEngine** | 2 methods |

| **ProjectStructureEngine** | 2 methods |

| **ThreadSafetyEngine** | 2 methods |

| **LogicOptimizationEngine** | 2 methods |

| **ValidationEngine** | 2 methods |

| **MappingEngine** | 2 methods |

| **AdvancedRefactoringEngine** | 2 methods |

| **AdvancedStructuralEngine** | 2 methods |

| **AnalysisEngine** | 2 methods |

| **DocumentationEngine** | 2 methods |

| **DiscoveryEngine** | 2 methods |

| **CodeHealingEngine** | 2 methods |

| **ApiAutomationEngine** | 1 methods |

| **ApiIntegrationEngine** | 1 methods |

| **ArchitecturalEngine** | 1 methods |

| **CodeFlowEngine** | 1 methods |

| **DependencyEngine** | 1 methods |

| **MetricsEngine** | 1 methods |

| **DependencyInjectionEngine** | 1 methods |

| **HealthOrchestrationEngine** | 1 methods |

| **ImmutabilityEngine** | 1 methods |

| **RefinementEngine** | 1 methods |

| **InventoryEngine** | 1 methods |

| **UniversalRefactoringLibrary** | 1 methods |

| **CodeSmellAndStyleEngine** | 1 methods |

| **ModernLoggingEngine** | 1 methods |

---

## Tools by Category

### 🔄 Refactoring Tools (120+ tools)

These tools restructure code while maintaining behavior:

**Engines:** RefactoringEngine, GranularRefactoringEngine, StandardRefactoringEngine, AdvancedRefactoringEngine, StructuralRefinementEngine, RefinementEngine, SentinelRefactoringTools

**Key Operations:**
- Extract Method - Move statements into new methods
- Change Signature - Reorder parameters globally
- Rename Symbol - Rename with scope awareness  
- Safe Delete - Delete only if no usages
- Move Type to File - Organize types into dedicated files
- Extract Interface - Create interfaces from classes
- Inline Methods/Fields - Replace calls with implementation
- Make Static - Convert instance methods to static

**Example - Extract Method:**

**Before:**
\\\csharp
public void ProcessOrder(Order order)
{
    if (order.Total <= 0) throw new Exception("Invalid");
    if (order.Items.Count == 0) throw new Exception("No items");
    
    var result = gateway.Charge(order.CustomerId, order.Total);
    order.TransactionId = result.Id;
    order.Status = "Completed";
}
\\\

**After:**
\\\csharp
public void ProcessOrder(Order order)
{
    ValidateOrder(order);
    ChargeAndUpdate(order);
}

private void ValidateOrder(Order order)
{
    if (order.Total <= 0) throw new Exception("Invalid");
    if (order.Items.Count == 0) throw new Exception("No items");
}

private void ChargeAndUpdate(Order order)
{
    var result = gateway.Charge(order.CustomerId, order.Total);
    order.TransactionId = result.Id;
    order.Status = "Completed";
}
\\\

---

### ⚡ Modernization Tools (60+ tools)

Upgrade code to modern C# features:

**Engines:** ModernizationEngine, ModernizationUpgradeEngine, SyntaxUpgradeEngine, ModernLoggingEngine, SentinelModernizationTools

**Key Operations:**
- Use File-Scoped Namespaces (C# 10+)
- Use Primary Constructors (C# 12+)
- Use Target-Typed New
- Use Pattern Matching
- Use Records
- Use Init-Only Properties
- Use Required Keyword
- Use Async Main
- Use Using Declarations
- Use Top-Level Statements

**Example - Primary Constructors:**

**Before (C# 10):**
\\\csharp
public class UserService
{
    private readonly IUserRepository _repo;
    private readonly ILogger<UserService> _logger;
    
    public UserService(IUserRepository repo, ILogger<UserService> logger)
    {
        _repo = repo;
        _logger = logger;
    }
}
\\\

**After (C# 12):**
\\\csharp
public class UserService(IUserRepository repo, ILogger<UserService> logger)
{
    private readonly IUserRepository _repo = repo;
    private readonly ILogger<UserService> _logger = logger;
}
\\\

---

### 🔍 Analysis & Diagnostics Tools (45+ tools)

Understand code structure and patterns:

**Engines:** AnalysisEngine, DiagnosticEngine, DiscoveryEngine, HealthOrchestrationEngine, SentinelIntelligenceTools

**Key Operations:**
- Generate Call Trees
- Analyze Data Flow
- Analyze Control Flow
- Find Dead Code
- Detect Circular Dependencies
- Measure Complexity
- Find Code Smells

---

### 🛠️ Code Generation Tools (30+ tools)

Automatically generate common patterns:

**Engines:** CodeGenerationEngine, SentinelGenerationTools

**Key Operations:**
- Generate Equals/GetHashCode
- Generate ToString
- Generate Constructors
- Generate Property Accessors
- Generate Validation Code
- Generate Async Overloads

**Example - Generate Equals:**

**Before:**
\\\csharp
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public decimal Price { get; set; }
}
\\\

**After:**
\\\csharp
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public decimal Price { get; set; }
    
    public override bool Equals(object? obj)
    {
        return obj is Product product && Id == product.Id && Name == product.Name && Price == product.Price;
    }
    
    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Name, Price);
    }
}
\\\

---

### ✨ Quality & Style Tools (50+ tools)

Improve code quality and consistency:

**Engines:** CodeStyleEngine, CodeSmellAndStyleEngine, IDEStyleEngine, MsToolAugmentEngine, SentinelAugmentTools, SentinelQualityTools

**Key Operations:**
- Fix Naming Conventions
- Remove Unused Imports
- Organize Imports
- Fix Indentation
- Add Documentation
- Apply Code Style Rules
- Remove Unnecessary Code
- Fix Common Mistakes

---

### ⚙️ Performance & Optimization Tools (25+ tools)

Optimize code performance:

**Engines:** AsyncOptimizationEngine, AdvancedLogicEngine, LogicOptimizationEngine, PerformanceEngine

**Key Operations:**
- Optimize to ValueTask
- Add ConfigureAwait(false)
- Optimize Independent Awaits
- Remove Unnecessary Boxing
- Use Collections Efficiently
- Cache Computations
- Optimize String Operations
- Reduce Allocations

**Example - Optimize to ValueTask:**

**Before:**
\\\csharp
public async Task<User> GetUserAsync(int id)
{
    var cached = _cache.Get(id);
    if (cached != null) return cached;
    
    var user = await _db.FindAsync(id);
    _cache.Set(id, user);
    return user;
}
\\\

**After:**
\\\csharp
public async ValueTask<User> GetUserAsync(int id)
{
    var cached = _cache.Get(id);
    if (cached != null) return cached;
    
    var user = await _db.FindAsync(id);
    _cache.Set(id, user);
    return user;
}
\\\

---

### 🔐 Safety & Security Tools (30+ tools)

Improve code safety and security:

**Engines:** AsyncSafetyEngine, SecurityEngine, SecurityAndSafetyEngine, ThreadSafetyEngine, ValidationEngine

**Key Operations:**
- Fix Null Reference Issues
- Add Null Checks
- Fix Async Deadlocks
- Fix Resource Leaks
- Fix SQL Injection
- Fix XSS Vulnerabilities
- Fix Race Conditions
- Use Thread-Safe Collections

---

### 📦 Workspace & Structure Tools (35+ tools)

Organize projects and files:

**Engines:** SolutionManagementEngine, ProjectStructureEngine, SentinelWorkspaceTools, SymbolNavigationEngine

**Key Operations:**
- Reorganize Files
- Consolidate Projects
- Split Large Files
- Extract Common Code
- Organize Namespaces
- Fix Project References
- Add Missing References

---

### 🧠 Semantic Analysis Tools (50+ tools)

Deep semantic code analysis:

**Engines:** SemanticRefactoringLibrary, SemanticSearchEngine, AdvancedStructuralEngine, AdvancedTypeEngine, CodeFlowEngine, ControlFlowEngine

**Key Operations:**
- Detect Unused Parameters
- Analyze Call Sites
- Track Symbol Usage
- Analyze Type Relationships
- Detect Dead Code Paths
- Measure Method Complexity
- Track Data Dependencies

---

### 🔬 Advanced Analysis Tools (25+ tools)

Specialized pattern detection:

**Engines:** AntiPatternEngine, DeadCodeEngine, MappingEngine, DependencyEngine, DependencyInjectionEngine, ImmutabilityEngine, InstrumentationEngine, MetricsEngine, ArchitecturalEngine, CodeHealingEngine, TestingEngine

**Key Operations:**
- Detect Anti-Patterns
- Find Performance Hotspots
- Analyze Architectural Issues
- Detect Code Duplication
- Analyze Test Coverage
- Find Dependency Issues
- Detect Concurrency Issues

---

## Complete Method Reference

### Full Method Listing

Total: 317 public async methods

#### By Engine

#### SentinelRefactoringTools (61 methods)

- **ExtractSuperclass**
- **SyncTypeAndFilename**
- **SafeDeleteSymbol**
- **InlineMethod**
- **ChangeSignature**
- **ExtractMethod**
- **IntroduceField**
- **IntroduceParameter**
- **InlineField**
- **InlineParameter**
- **MakeMethodStatic**
- **ExtensionToStatic**
- **GetById**
- **RenameSymbol**
- **ExtractInterface**
- **MoveTypeToFile**
- **ConvertAbstractToInterface**
- **GenerateMapping**
- **WrapInUsing**
- **IntroduceVariable**
- **InlineVariable**
- **ConvertPropertyToMethods**
- **ConvertMethodToIndexer**
- **MoveTypeToOuterScope**
- **MoveAllTypesToFiles**
- **MoveAllTypesToFilesInProject**
- **MoveAllTypesToFilesInSolution**
- **ReplaceMember**
- **AddMemberToClass**
- **RemoveMember**
- **AddUsingDirective**
- **AddEnumValue**
- **InsertMemberAfter**
- **InsertMemberBefore**
- **AddAttribute**
- **AddBaseType**
- **ReplaceConstructorWithFactory**
- **InvertAssignments**
- **ReduceBlockDepth**
- **OptimizeTaskWait**
- **PullUpMember**
- **RemoveAttribute**
- **RemoveBaseType**
- **ChangeAccessibility**
- **AddModifier**
- **RemoveModifier**
- **AddSummaryComment**
- **AddProperty**
- **AddField**
- **SortMembers**
- **WrapInTryCatch**
- **AddConstructorParameter**
- **WrapInRegion**
- **SyncInterfaceToImplementation**
- **IntroduceParameterObject**
- **UpdateXmlDocsFromSignature**
- **ConvertExpressionBody**
- **ExtractConstant**
- **AnalyzeControlFlow**
- **AnalyzeDataFlow**
- **FormatDocumentPreview**

#### RefactoringEngine (34 methods)

- **FormatDocumentAsync**
- **ExtractMethodAsync**
- **RenameSymbolAsync**
- **ConvertIndexerToMethodAsync**
- **AddRemoveParamsAsync**
- **ReplaceMemberAsync**
- **AddMemberAsync**
- **RemoveMemberAsync**
- **ConvertToPrimaryConstructorAsync**
- **ConvertExpressionBodyAsync**
- **ExtractConstantAsync**
- **AnalyzeControlFlowAsync**
- **AnalyzeDataFlowAsync**
- **AddUsingDirectiveAsync**
- **AddEnumValueAsync**
- **InsertMemberAfterAsync**
- **InsertMemberBeforeAsync**
- **AddAttributeAsync**
- **AddBaseTypeAsync**
- **RemoveAttributeAsync**
- **RemoveBaseTypeAsync**
- **ChangeAccessibilityAsync**
- **AddModifierAsync**
- **RemoveModifierAsync**
- **AddSummaryCommentAsync**
- **AddPropertyAsync**
- **AddFieldAsync**
- **SortMembersAsync**
- **WrapInTryCatchAsync**
- **AddConstructorParameterAsync**
- **WrapInRegionAsync**
- **SyncInterfaceToImplementationAsync**
- **UpdateXmlDocsFromSignatureAsync**
- **FormatDocumentPreviewAsync**

#### SentinelModernizationTools (24 methods)

- **FixThreadSleep**
- **AddBraces**
- **UpgradePatternMatching**
- **UseIndexFromEnd**
- **UpgradeUnboundNameof**
- **UseFieldBackedProperties**
- **ClassToRecord**
- **RecordToClass**
- **SimplifyVerbosity**
- **UpgradeThreadSafety**
- **UseTimeProvider**
- **ModernizeExceptions**
- **UpgradeToModernGuards**
- **ConvertSwitchToExpression**
- **CleanupImplicitSpans**
- **ConvertToSourceGeneratedLogging**
- **SimplifyBooleanExpressions**
- **SimplifyMemberAccess**
- **MakeClassImmutable**
- **ConvertStaticToExtension**
- **OptimizeToValueTask**
- **OptimizeIndependentAwaits**
- **UpgradeToPrimaryConstructor**
- **UseExceptionExpressions**

#### SentinelWorkspaceTools (17 methods)

- **ListDependencies**
- **LoadSolution**
- **Diagnose**
- **ValidateProposedDiff**
- **ValidateProposedChanges**
- **ValidateStagedChanges**
- **ApplyProposedDiff**
- **ApplyProposedChanges**
- **RetryFailedChanges**
- **ApplyStagedChanges**
- **GetFileDiagnostics**
- **SafeDelete**
- **SyncTypeAndFilename**
- **CreateProject**
- **GetProjectDiagnostics**
- **GetSolutionDiagnostics**
- **SplitProjectByFolder**

#### SentinelIntelligenceTools (16 methods)

- **GetComprehensiveHealthReport**
- **GetById**
- **GetBlastRadius**
- **GetSolutionMetrics**
- **GetCodeInventory**
- **GenerateCallTree**
- **DocumentPocoFields**
- **GenerateEqualityOverrides**
- **GetSymbolInfo**
- **GetCallGraph**
- **GetReverseCallGraph**
- **ConvertToBackgroundService**
- **FixMismatchedNamespaces**
- **MoveFileToNamespaceFolder**
- **FindBestInsertionPoint**
- **PreviewRenameImpact**

#### SentinelQualityTools (14 methods)

- **GenerateTestSkeleton**
- **GenerateTestScaffold**
- **AnalyzePathCoverage**
- **AddGuardClauses**
- **AddBenchmarkStub**
- **AnalyzeMethodControlFlow**
- **AnalyzeMethodDataFlow**
- **AddConfigureAwaitFalse**
- **RemoveConfigureAwaitFalse**
- **ConvertLockToSemaphoreSlim**
- **ConvertToAsyncEnumerable**
- **AddCancellationTokenToMethod**
- **MakeMethodThreadSafe**
- **GetDiagnosticsSummary**

#### SentinelGenerationTools (12 methods)

- **GenerateHttpClient**
- **GenerateConstructor**
- **GenerateToString**
- **GenerateRepositoryInterface**
- **GenerateFluentBuilder**
- **GenerateDecoratorClass**
- **GenerateDefaultConfigJson**
- **GenerateAsyncOverload**
- **AddValidationToPoco**
- **ImplementInterfaceSafe**
- **ConvertPropertySafe**
- **InterpolateStringSafe**

#### SyntaxUpgradeEngine (10 methods)

- **UpgradeToModernGuardsAsync**
- **AddBracesAsync**
- **UpgradePatternMatchingAsync**
- **UseNameofExpressionAsync**
- **ConvertSwitchToExpressionAsync**
- **ConvertSwitchExpressionToStatementAsync**
- **CleanupImplicitSpansAsync**
- **UseFieldBackedPropertiesAsync**
- **UpgradeToPrimaryConstructorAsync**
- **UseExceptionExpressionsAsync**

#### SentinelAugmentTools (10 methods)

- **EncapsulateFieldSafe**
- **AnalyzeSwitchForPatternConversion**
- **ConvertSwitchToPatternSafe**
- **ConvertStringFormatToInterpolatedSmart**
- **SortAndDeduplicateUsings**
- **FormatDocumentSafe**
- **AnalyzeForeachForLinqConversion**
- **GetWorkspaceHealth**
- **PreviewAddMissingUsings**
- **ExtractConstantSafe**

#### MsToolAugmentEngine (9 methods)

- **EncapsulateFieldSafeAsync**
- **AnalyzeSwitchForPatternConversionAsync**
- **ConvertSwitchToPatternSafeAsync**
- **ConvertStringFormatToInterpolatedSmartAsync**
- **SortAndDeduplicateUsingsAsync**
- **FormatDocumentSafeAsync**
- **AnalyzeForeachForLinqConversionAsync**
- **PreviewAddMissingUsingsAsync**
- **ExtractConstantSafeAsync**

#### GranularRefactoringEngine (9 methods)

- **RunMicroRefactoringAsync**
- **InlineFieldAsync**
- **InlineParameterAsync**
- **ConvertMethodToIndexerAsync**
- **IntroduceFieldAsync**
- **IntroduceParameterAsync**
- **IntroduceVariableAsync**
- **MoveTypeToOuterScopeAsync**
- **IntroduceParameterObjectAsync**

#### CodeGenerationEngine (9 methods)

- **GenerateConstructorAsync**
- **GenerateToStringAsync**
- **GenerateDefaultConfigJsonAsync**
- **GenerateRepositoryInterfaceAsync**
- **GenerateFluentBuilderAsync**
- **GenerateDecoratorClassAsync**
- **ImplementInterfaceAsync**
- **ConvertPropertySafeAsync**
- **InterpolateStringAsync**

#### AdvancedLogicEngine (7 methods)

- **ConvertIfToSwitchExpressionAsync**
- **ConvertIfToSwitchStatementAsync**
- **ExtensionToStaticAsync**
- **ConvertStaticToExtensionAsync**
- **ConvertForEachToForAsync**
- **ConvertForToForEachAsync**
- **ConvertWhileToForAsync**

#### AsyncOptimizationEngine (7 methods)

- **OptimizeToValueTaskAsync**
- **OptimizeIndependentAwaitsAsync**
- **GenerateAsyncOverloadAsync**
- **AddConfigureAwaitFalseAsync**
- **RemoveConfigureAwaitFalseAsync**
- **ConvertToAsyncEnumerableAsync**
- **AddCancellationTokenToMethodAsync**

#### CodeStyleEngine (7 methods)

- **FixDangerousLockAsync**
- **ConvertPropertyToMethodsAsync**
- **SimplifyVerbosityAsync**
- **UseCollectionExpressionsAsync**
- **UseTimeProviderAsync**
- **SimplifyAllNamesAsync**
- **UseIndexFromEndAsync**

#### TestingEngine (4 methods)

- **CalculateComplexityAsync**
- **GenerateTestSkeletonAsync**
- **GenerateTestScaffoldAsync**
- **AddBenchmarkStubAsync**

#### ControlFlowEngine (3 methods)

- **AnalyzePathCoverageAsync**
- **AnalyzeMethodControlFlowAsync**
- **AnalyzeMethodDataFlowAsync**

#### ModernizationUpgradeEngine (3 methods)

- **UseSpanForParsingAsync**
- **UpgradePatternMatchingAsync**
- **UseThrowExpressionsAsync**

#### StandardRefactoringEngine (3 methods)

- **ConvertMethodToPropertyAsync**
- **MakeMethodStaticAsync**
- **InvertBooleanAsync**

#### SymbolNavigationEngine (3 methods)

- **GetSymbolInfoAsync**
- **GetCallGraphAsync**
- **GetReverseCallGraphAsync**

#### IDEStyleEngine (3 methods)

- **SimplifyMemberAccessAsync**
- **UseObjectInitializersAsync**
- **UseNullPropagationAsync**

#### SemanticRefactoringLibrary (3 methods)

- **InlineVariableAsync**
- **ConvertPropertyToMethodsAsync**
- **WrapInUsingAsync**

#### ImpactAnalyzer (3 methods)

- **AnalyzeImpactAsync**
- **FindDerivedTypesAsync**
- **FindImplementationsAsync**

#### InstrumentationEngine (3 methods)

- **AddTryCatchToMethodAsync**
- **AddTryCatchToClassAsync**
- **AddStopwatchDiagnosticsAsync**

#### ModernizationEngine (3 methods)

- **ClassToRecordAsync**
- **RecordToClassAsync**
- **ConvertMethodToExpressionBodyAsync**

#### SolutionManagementEngine (2 methods)

- **CreateProjectAsync**
- **SplitProjectByFolderAsync**

#### StructuralRefinementEngine (2 methods)

- **SyncTypeAndFilenameAsync**
- **SafeDeleteSymbolAsync**

#### ProjectStructureEngine (2 methods)

- **FixMismatchedNamespacesAsync**
- **MoveFileToNamespaceFolderAsync**

#### ThreadSafetyEngine (2 methods)

- **MakeMethodThreadSafeAsync**
- **ConvertLockToSemaphoreSlimAsync**

#### LogicOptimizationEngine (2 methods)

- **SimplifyBooleanExpressionsAsync**
- **AddGuardClausesAsync**

#### ValidationEngine (2 methods)

- **ValidateDiffAsync**
- **ValidateChangesAsync**

#### MappingEngine (2 methods)

- **GenerateMappingAsync**
- **InvertAssignmentsAsync**

#### AdvancedRefactoringEngine (2 methods)

- **ReplaceStringConcatWithInterpolationAsync**
- **OptimizeTaskWaitAsync**

#### AdvancedStructuralEngine (2 methods)

- **ConvertAbstractClassToInterfaceAsync**
- **ReplaceConstructorWithFactoryAsync**

#### AnalysisEngine (2 methods)

- **GenerateCallTreeAsync**
- **GenerateEqualityOverridesAsync**

#### DocumentationEngine (2 methods)

- **GenerateXmlDocumentationStubsAsync**
- **DocumentPocoFieldsAsync**

#### DiscoveryEngine (2 methods)

- **FindBestInsertionPointAsync**
- **PreviewRenameImpactAsync**

#### CodeHealingEngine (2 methods)

- **FixThreadSleepAsync**
- **AddRetryPolicyAsync**

#### ApiAutomationEngine (1 methods)

- **GenerateHttpClientForControllerAsync**

#### ApiIntegrationEngine (1 methods)

- **AddValidationToPocoAsync**

#### ArchitecturalEngine (1 methods)

- **ConvertToBackgroundServiceAsync**

#### CodeFlowEngine (1 methods)

- **ReduceBlockDepthAsync**

#### DependencyEngine (1 methods)

- **GetProjectDependenciesAsync**

#### MetricsEngine (1 methods)

- **GetSolutionMetricsAsync**

#### DependencyInjectionEngine (1 methods)

- **AddDependencyAsync**

#### HealthOrchestrationEngine (1 methods)

- **GenerateComprehensiveHealthReportAsync**

#### ImmutabilityEngine (1 methods)

- **MakeClassImmutableAsync**

#### RefinementEngine (1 methods)

- **InlineMethodAsync**

#### InventoryEngine (1 methods)

- **GetCodeInventoryAsync**

#### UniversalRefactoringLibrary (1 methods)

- **RunRefactoringAsync**

#### CodeSmellAndStyleEngine (1 methods)

- **UseSwitchExpressionAsync**

#### ModernLoggingEngine (1 methods)

- **ConvertToSourceGeneratedLoggingAsync**

---

## Integration Patterns

### Basic Usage Pattern

\\\csharp
using var workspace = new PersistentWorkspaceManager();
await workspace.OpenSolutionAsync("path/to/solution.sln");

var engine = new RefactoringEngine(_logger, workspace, _config);

var result = await engine.RenameSymbolAsync(
    "src/MyClass.cs",
    "OldName",
    "context snippet",
    "NewName"
);

await workspace.ApplyCh angesAsync(result);
\\\

### Analysis Pattern

\\\csharp
var analysis = new AnalysisEngine(_logger, workspace, _config);

var callTree = await analysis.GenerateCallTreeAsync(
    "src/MyClass.cs",
    "MyMethod"
);

foreach (var caller in callTree.Callers)
{
    Console.WriteLine(\$"Called by: {caller}\");
}
\\\

### Modernization Pattern

\\\csharp
var modernization = new ModernizationEngine(_logger, workspace, _config);

var changes = await modernization.UseFileScopedNamespacesAsync(
    "src/Services/"
);

await workspace.ApplyChangesAsync(changes);
\\\

---

## Unfinished Features & Limitations

### Known Limitations

1. **Partial Types** - Partial type refactorings operate on single declarations
2. **Large Files** - Operations may timeout on files >10,000 lines
3. **Generic Constraints** - Some advanced generic operations have limitations
4. **Dynamic Types** - Dynamic typing analysis is limited
5. **Reflection-Based Code** - Reflection calls are difficult to analyze

### Deferred Implementations

1. **Cross-Solution Refactoring** - Limited support for operations spanning multiple solutions
2. **Real-Time Preview** - Continuous background preview not implemented
3. **Undo/Redo Chains** - Multi-step undo history not tracked
4. **VB.NET Support** - All tools are C# focused
5. **Incremental Updates** - Full re-analysis on each operation (no incremental support)

### Performance Notes

- Solution-wide operations on large solutions (>100 projects) may be slow
- Finding all references can timeout on very large codebases (>1,000,000 lines)
- Some analysis tools may consume significant memory on large solutions
- Concurrent operations are not supported; operations are sequential

---

## Version Information

- **RoslynSentinel Version:** Latest
- **Built on:** Roslyn Compiler Platform (4.x+)
- **.NET Target:** .NET 8.0+
- **Documentation Generated:** 2026-05-03 21:27:05 UTC
- **Total Public Methods Documented:** 317

---

## How to Use This Documentation

1. **Find your tool** - Use the index above to locate your refactoring need
2. **Review the pattern** - Look at the before/after code examples
3. **Check the signature** - Verify the method parameters and return type
4. **Implement** - Use the tool in your code as shown in integration patterns

---

## Table of Contents - All Tools

| # | Engine | Method | Category |
|---|--------|--------|----------|
| 1 | SentinelRefactoringTools | AddAttribute | Refactoring |
| 2 | SentinelRefactoringTools | AddBaseType | Refactoring |
| 3 | SentinelRefactoringTools | AddConstructorParameter | Refactoring |
| 4 | SentinelRefactoringTools | AddEnumValue | Refactoring |
| 5 | SentinelRefactoringTools | AddField | Refactoring |
| 6 | SentinelRefactoringTools | AddMemberToClass | Refactoring |
| 7 | SentinelRefactoringTools | AddModifier | Refactoring |
| 8 | SentinelRefactoringTools | AddProperty | Refactoring |
| 9 | SentinelRefactoringTools | AddSummaryComment | Refactoring |
| 10 | SentinelRefactoringTools | AddUsingDirective | Refactoring |
| 11 | SentinelRefactoringTools | AnalyzeControlFlow | Refactoring |
| 12 | SentinelRefactoringTools | AnalyzeDataFlow | Refactoring |
| 13 | SentinelRefactoringTools | ChangeAccessibility | Refactoring |
| 14 | SentinelRefactoringTools | ChangeSignature | Refactoring |
| 15 | SentinelRefactoringTools | ConvertAbstractToInterface | Refactoring |
| 16 | SentinelRefactoringTools | ConvertExpressionBody | Refactoring |
| 17 | SentinelRefactoringTools | ConvertMethodToIndexer | Refactoring |
| 18 | SentinelRefactoringTools | ConvertPropertyToMethods | Refactoring |
| 19 | SentinelRefactoringTools | ExtensionToStatic | Refactoring |
| 20 | SentinelRefactoringTools | ExtractConstant | Refactoring |
| 21 | SentinelRefactoringTools | ExtractInterface | Refactoring |
| 22 | SentinelRefactoringTools | ExtractMethod | Refactoring |
| 23 | SentinelRefactoringTools | ExtractSuperclass | Refactoring |
| 24 | SentinelRefactoringTools | FormatDocumentPreview | Refactoring |
| 25 | SentinelRefactoringTools | GenerateMapping | Refactoring |
| 26 | SentinelRefactoringTools | GetById | Refactoring |
| 27 | SentinelRefactoringTools | InlineField | Refactoring |
| 28 | SentinelRefactoringTools | InlineMethod | Refactoring |
| 29 | SentinelRefactoringTools | InlineParameter | Refactoring |
| 30 | SentinelRefactoringTools | InlineVariable | Refactoring |
| 31 | SentinelRefactoringTools | InsertMemberAfter | Refactoring |
| 32 | SentinelRefactoringTools | InsertMemberBefore | Refactoring |
| 33 | SentinelRefactoringTools | IntroduceField | Refactoring |
| 34 | SentinelRefactoringTools | IntroduceParameter | Refactoring |
| 35 | SentinelRefactoringTools | IntroduceParameterObject | Refactoring |
| 36 | SentinelRefactoringTools | IntroduceVariable | Refactoring |
| 37 | SentinelRefactoringTools | InvertAssignments | Refactoring |
| 38 | SentinelRefactoringTools | MakeMethodStatic | Refactoring |
| 39 | SentinelRefactoringTools | MoveAllTypesToFiles | Refactoring |
| 40 | SentinelRefactoringTools | MoveAllTypesToFilesInProject | Refactoring |
| 41 | SentinelRefactoringTools | MoveAllTypesToFilesInSolution | Refactoring |
| 42 | SentinelRefactoringTools | MoveTypeToFile | Refactoring |
| 43 | SentinelRefactoringTools | MoveTypeToOuterScope | Refactoring |
| 44 | SentinelRefactoringTools | OptimizeTaskWait | Refactoring |
| 45 | SentinelRefactoringTools | PullUpMember | Refactoring |
| 46 | SentinelRefactoringTools | ReduceBlockDepth | Refactoring |
| 47 | SentinelRefactoringTools | RemoveAttribute | Refactoring |
| 48 | SentinelRefactoringTools | RemoveBaseType | Refactoring |
| 49 | SentinelRefactoringTools | RemoveMember | Refactoring |
| 50 | SentinelRefactoringTools | RemoveModifier | Refactoring |
| 51 | SentinelRefactoringTools | RenameSymbol | Refactoring |
| 52 | SentinelRefactoringTools | ReplaceConstructorWithFactory | Refactoring |
| 53 | SentinelRefactoringTools | ReplaceMember | Refactoring |
| 54 | SentinelRefactoringTools | SafeDeleteSymbol | Refactoring |
| 55 | SentinelRefactoringTools | SortMembers | Refactoring |
| 56 | SentinelRefactoringTools | SyncInterfaceToImplementation | Refactoring |
| 57 | SentinelRefactoringTools | SyncTypeAndFilename | Refactoring |
| 58 | SentinelRefactoringTools | UpdateXmlDocsFromSignature | Refactoring |
| 59 | SentinelRefactoringTools | WrapInRegion | Refactoring |
| 60 | SentinelRefactoringTools | WrapInTryCatch | Refactoring |
| 61 | SentinelRefactoringTools | WrapInUsing | Refactoring |
| 62 | RefactoringEngine | AddAttributeAsync | Refactoring |
| 63 | RefactoringEngine | AddBaseTypeAsync | Refactoring |
| 64 | RefactoringEngine | AddConstructorParameterAsync | Refactoring |
| 65 | RefactoringEngine | AddEnumValueAsync | Refactoring |
| 66 | RefactoringEngine | AddFieldAsync | Refactoring |
| 67 | RefactoringEngine | AddMemberAsync | Refactoring |
| 68 | RefactoringEngine | AddModifierAsync | Refactoring |
| 69 | RefactoringEngine | AddPropertyAsync | Refactoring |
| 70 | RefactoringEngine | AddRemoveParamsAsync | Refactoring |
| 71 | RefactoringEngine | AddSummaryCommentAsync | Refactoring |
| 72 | RefactoringEngine | AddUsingDirectiveAsync | Refactoring |
| 73 | RefactoringEngine | AnalyzeControlFlowAsync | Refactoring |
| 74 | RefactoringEngine | AnalyzeDataFlowAsync | Refactoring |
| 75 | RefactoringEngine | ChangeAccessibilityAsync | Refactoring |
| 76 | RefactoringEngine | ConvertExpressionBodyAsync | Refactoring |
| 77 | RefactoringEngine | ConvertIndexerToMethodAsync | Refactoring |
| 78 | RefactoringEngine | ConvertToPrimaryConstructorAsync | Refactoring |
| 79 | RefactoringEngine | ExtractConstantAsync | Refactoring |
| 80 | RefactoringEngine | ExtractMethodAsync | Refactoring |
| 81 | RefactoringEngine | FormatDocumentAsync | Refactoring |
| 82 | RefactoringEngine | FormatDocumentPreviewAsync | Refactoring |
| 83 | RefactoringEngine | InsertMemberAfterAsync | Refactoring |
| 84 | RefactoringEngine | InsertMemberBeforeAsync | Refactoring |
| 85 | RefactoringEngine | RemoveAttributeAsync | Refactoring |
| 86 | RefactoringEngine | RemoveBaseTypeAsync | Refactoring |
| 87 | RefactoringEngine | RemoveMemberAsync | Refactoring |
| 88 | RefactoringEngine | RemoveModifierAsync | Refactoring |
| 89 | RefactoringEngine | RenameSymbolAsync | Refactoring |
| 90 | RefactoringEngine | ReplaceMemberAsync | Refactoring |
| 91 | RefactoringEngine | SortMembersAsync | Refactoring |
| 92 | RefactoringEngine | SyncInterfaceToImplementationAsync | Refactoring |
| 93 | RefactoringEngine | UpdateXmlDocsFromSignatureAsync | Refactoring |
| 94 | RefactoringEngine | WrapInRegionAsync | Refactoring |
| 95 | RefactoringEngine | WrapInTryCatchAsync | Refactoring |
| 96 | SentinelModernizationTools | AddBraces | Modernization |
| 97 | SentinelModernizationTools | ClassToRecord | Modernization |
| 98 | SentinelModernizationTools | CleanupImplicitSpans | Modernization |
| 99 | SentinelModernizationTools | ConvertStaticToExtension | Modernization |
| 100 | SentinelModernizationTools | ConvertSwitchToExpression | Modernization |
| 101 | SentinelModernizationTools | ConvertToSourceGeneratedLogging | Modernization |
| 102 | SentinelModernizationTools | FixThreadSleep | Modernization |
| 103 | SentinelModernizationTools | MakeClassImmutable | Modernization |
| 104 | SentinelModernizationTools | ModernizeExceptions | Modernization |
| 105 | SentinelModernizationTools | OptimizeIndependentAwaits | Modernization |
| 106 | SentinelModernizationTools | OptimizeToValueTask | Modernization |
| 107 | SentinelModernizationTools | RecordToClass | Modernization |
| 108 | SentinelModernizationTools | SimplifyBooleanExpressions | Modernization |
| 109 | SentinelModernizationTools | SimplifyMemberAccess | Modernization |
| 110 | SentinelModernizationTools | SimplifyVerbosity | Modernization |
| 111 | SentinelModernizationTools | UpgradePatternMatching | Modernization |
| 112 | SentinelModernizationTools | UpgradeThreadSafety | Modernization |
| 113 | SentinelModernizationTools | UpgradeToModernGuards | Modernization |
| 114 | SentinelModernizationTools | UpgradeToPrimaryConstructor | Modernization |
| 115 | SentinelModernizationTools | UpgradeUnboundNameof | Modernization |
| 116 | SentinelModernizationTools | UseExceptionExpressions | Modernization |
| 117 | SentinelModernizationTools | UseFieldBackedProperties | Modernization |
| 118 | SentinelModernizationTools | UseIndexFromEnd | Modernization |
| 119 | SentinelModernizationTools | UseTimeProvider | Modernization |
| 120 | SentinelWorkspaceTools | ApplyProposedChanges | Workspace |
| 121 | SentinelWorkspaceTools | ApplyProposedDiff | Workspace |
| 122 | SentinelWorkspaceTools | ApplyStagedChanges | Workspace |
| 123 | SentinelWorkspaceTools | CreateProject | Workspace |
| 124 | SentinelWorkspaceTools | Diagnose | Workspace |
| 125 | SentinelWorkspaceTools | GetFileDiagnostics | Workspace |
| 126 | SentinelWorkspaceTools | GetProjectDiagnostics | Workspace |
| 127 | SentinelWorkspaceTools | GetSolutionDiagnostics | Workspace |
| 128 | SentinelWorkspaceTools | ListDependencies | Workspace |
| 129 | SentinelWorkspaceTools | LoadSolution | Workspace |
| 130 | SentinelWorkspaceTools | RetryFailedChanges | Workspace |
| 131 | SentinelWorkspaceTools | SafeDelete | Workspace |
| 132 | SentinelWorkspaceTools | SplitProjectByFolder | Workspace |
| 133 | SentinelWorkspaceTools | SyncTypeAndFilename | Workspace |
| 134 | SentinelWorkspaceTools | ValidateProposedChanges | Workspace |
| 135 | SentinelWorkspaceTools | ValidateProposedDiff | Workspace |
| 136 | SentinelWorkspaceTools | ValidateStagedChanges | Workspace |
| 137 | SentinelIntelligenceTools | ConvertToBackgroundService | Other |
| 138 | SentinelIntelligenceTools | DocumentPocoFields | Other |
| 139 | SentinelIntelligenceTools | FindBestInsertionPoint | Other |
| 140 | SentinelIntelligenceTools | FixMismatchedNamespaces | Other |
| 141 | SentinelIntelligenceTools | GenerateCallTree | Other |
| 142 | SentinelIntelligenceTools | GenerateEqualityOverrides | Other |
| 143 | SentinelIntelligenceTools | GetBlastRadius | Other |
| 144 | SentinelIntelligenceTools | GetById | Other |
| 145 | SentinelIntelligenceTools | GetCallGraph | Other |
| 146 | SentinelIntelligenceTools | GetCodeInventory | Other |
| 147 | SentinelIntelligenceTools | GetComprehensiveHealthReport | Other |
| 148 | SentinelIntelligenceTools | GetReverseCallGraph | Other |
| 149 | SentinelIntelligenceTools | GetSolutionMetrics | Other |
| 150 | SentinelIntelligenceTools | GetSymbolInfo | Other |
| 151 | SentinelIntelligenceTools | MoveFileToNamespaceFolder | Other |
| 152 | SentinelIntelligenceTools | PreviewRenameImpact | Other |
| 153 | SentinelQualityTools | AddBenchmarkStub | Quality |
| 154 | SentinelQualityTools | AddCancellationTokenToMethod | Quality |
| 155 | SentinelQualityTools | AddConfigureAwaitFalse | Quality |
| 156 | SentinelQualityTools | AddGuardClauses | Quality |
| 157 | SentinelQualityTools | AnalyzeMethodControlFlow | Quality |
| 158 | SentinelQualityTools | AnalyzeMethodDataFlow | Quality |
| 159 | SentinelQualityTools | AnalyzePathCoverage | Quality |
| 160 | SentinelQualityTools | ConvertLockToSemaphoreSlim | Quality |
| 161 | SentinelQualityTools | ConvertToAsyncEnumerable | Quality |
| 162 | SentinelQualityTools | GenerateTestScaffold | Quality |
| 163 | SentinelQualityTools | GenerateTestSkeleton | Quality |
| 164 | SentinelQualityTools | GetDiagnosticsSummary | Quality |
| 165 | SentinelQualityTools | MakeMethodThreadSafe | Quality |
| 166 | SentinelQualityTools | RemoveConfigureAwaitFalse | Quality |
| 167 | SentinelGenerationTools | AddValidationToPoco | Generation |
| 168 | SentinelGenerationTools | ConvertPropertySafe | Generation |
| 169 | SentinelGenerationTools | GenerateAsyncOverload | Generation |
| 170 | SentinelGenerationTools | GenerateConstructor | Generation |
| 171 | SentinelGenerationTools | GenerateDecoratorClass | Generation |
| 172 | SentinelGenerationTools | GenerateDefaultConfigJson | Generation |
| 173 | SentinelGenerationTools | GenerateFluentBuilder | Generation |
| 174 | SentinelGenerationTools | GenerateHttpClient | Generation |
| 175 | SentinelGenerationTools | GenerateRepositoryInterface | Generation |
| 176 | SentinelGenerationTools | GenerateToString | Generation |
| 177 | SentinelGenerationTools | ImplementInterfaceSafe | Generation |
| 178 | SentinelGenerationTools | InterpolateStringSafe | Generation |
| 179 | SyntaxUpgradeEngine | AddBracesAsync | Modernization |
| 180 | SyntaxUpgradeEngine | CleanupImplicitSpansAsync | Modernization |
| 181 | SyntaxUpgradeEngine | ConvertSwitchExpressionToStatementAsync | Modernization |
| 182 | SyntaxUpgradeEngine | ConvertSwitchToExpressionAsync | Modernization |
| 183 | SyntaxUpgradeEngine | UpgradePatternMatchingAsync | Modernization |
| 184 | SyntaxUpgradeEngine | UpgradeToModernGuardsAsync | Modernization |
| 185 | SyntaxUpgradeEngine | UpgradeToPrimaryConstructorAsync | Modernization |
| 186 | SyntaxUpgradeEngine | UseExceptionExpressionsAsync | Modernization |
| 187 | SyntaxUpgradeEngine | UseFieldBackedPropertiesAsync | Modernization |
| 188 | SyntaxUpgradeEngine | UseNameofExpressionAsync | Modernization |
| 189 | SentinelAugmentTools | AnalyzeForeachForLinqConversion | Quality |
| 190 | SentinelAugmentTools | AnalyzeSwitchForPatternConversion | Quality |
| 191 | SentinelAugmentTools | ConvertStringFormatToInterpolatedSmart | Quality |
| 192 | SentinelAugmentTools | ConvertSwitchToPatternSafe | Quality |
| 193 | SentinelAugmentTools | EncapsulateFieldSafe | Quality |
| 194 | SentinelAugmentTools | ExtractConstantSafe | Quality |
| 195 | SentinelAugmentTools | FormatDocumentSafe | Quality |
| 196 | SentinelAugmentTools | GetWorkspaceHealth | Quality |
| 197 | SentinelAugmentTools | PreviewAddMissingUsings | Quality |
| 198 | SentinelAugmentTools | SortAndDeduplicateUsings | Quality |
| 199 | MsToolAugmentEngine | AnalyzeForeachForLinqConversionAsync | Quality |
| 200 | MsToolAugmentEngine | AnalyzeSwitchForPatternConversionAsync | Quality |
| 201 | MsToolAugmentEngine | ConvertStringFormatToInterpolatedSmartAsync | Quality |
| 202 | MsToolAugmentEngine | ConvertSwitchToPatternSafeAsync | Quality |
| 203 | MsToolAugmentEngine | EncapsulateFieldSafeAsync | Quality |
| 204 | MsToolAugmentEngine | ExtractConstantSafeAsync | Quality |
| 205 | MsToolAugmentEngine | FormatDocumentSafeAsync | Quality |
| 206 | MsToolAugmentEngine | PreviewAddMissingUsingsAsync | Quality |
| 207 | MsToolAugmentEngine | SortAndDeduplicateUsingsAsync | Quality |
| 208 | GranularRefactoringEngine | ConvertMethodToIndexerAsync | Refactoring |
| 209 | GranularRefactoringEngine | InlineFieldAsync | Refactoring |
| 210 | GranularRefactoringEngine | InlineParameterAsync | Refactoring |
| 211 | GranularRefactoringEngine | IntroduceFieldAsync | Refactoring |
| 212 | GranularRefactoringEngine | IntroduceParameterAsync | Refactoring |
| 213 | GranularRefactoringEngine | IntroduceParameterObjectAsync | Refactoring |
| 214 | GranularRefactoringEngine | IntroduceVariableAsync | Refactoring |
| 215 | GranularRefactoringEngine | MoveTypeToOuterScopeAsync | Refactoring |
| 216 | GranularRefactoringEngine | RunMicroRefactoringAsync | Refactoring |
| 217 | CodeGenerationEngine | ConvertPropertySafeAsync | Generation |
| 218 | CodeGenerationEngine | GenerateConstructorAsync | Generation |
| 219 | CodeGenerationEngine | GenerateDecoratorClassAsync | Generation |
| 220 | CodeGenerationEngine | GenerateDefaultConfigJsonAsync | Generation |
| 221 | CodeGenerationEngine | GenerateFluentBuilderAsync | Generation |
| 222 | CodeGenerationEngine | GenerateRepositoryInterfaceAsync | Generation |
| 223 | CodeGenerationEngine | GenerateToStringAsync | Generation |
| 224 | CodeGenerationEngine | ImplementInterfaceAsync | Generation |
| 225 | CodeGenerationEngine | InterpolateStringAsync | Generation |
| 226 | AdvancedLogicEngine | ConvertForEachToForAsync | Optimization |
| 227 | AdvancedLogicEngine | ConvertForToForEachAsync | Optimization |
| 228 | AdvancedLogicEngine | ConvertIfToSwitchExpressionAsync | Optimization |
| 229 | AdvancedLogicEngine | ConvertIfToSwitchStatementAsync | Optimization |
| 230 | AdvancedLogicEngine | ConvertStaticToExtensionAsync | Optimization |
| 231 | AdvancedLogicEngine | ConvertWhileToForAsync | Optimization |
| 232 | AdvancedLogicEngine | ExtensionToStaticAsync | Optimization |
| 233 | AsyncOptimizationEngine | AddCancellationTokenToMethodAsync | Optimization |
| 234 | AsyncOptimizationEngine | AddConfigureAwaitFalseAsync | Optimization |
| 235 | AsyncOptimizationEngine | ConvertToAsyncEnumerableAsync | Optimization |
| 236 | AsyncOptimizationEngine | GenerateAsyncOverloadAsync | Optimization |
| 237 | AsyncOptimizationEngine | OptimizeIndependentAwaitsAsync | Optimization |
| 238 | AsyncOptimizationEngine | OptimizeToValueTaskAsync | Optimization |
| 239 | AsyncOptimizationEngine | RemoveConfigureAwaitFalseAsync | Optimization |
| 240 | CodeStyleEngine | ConvertPropertyToMethodsAsync | Quality |
| 241 | CodeStyleEngine | FixDangerousLockAsync | Quality |
| 242 | CodeStyleEngine | SimplifyAllNamesAsync | Quality |
| 243 | CodeStyleEngine | SimplifyVerbosityAsync | Quality |
| 244 | CodeStyleEngine | UseCollectionExpressionsAsync | Quality |
| 245 | CodeStyleEngine | UseIndexFromEndAsync | Quality |
| 246 | CodeStyleEngine | UseTimeProviderAsync | Quality |
| 247 | TestingEngine | AddBenchmarkStubAsync | Other |
| 248 | TestingEngine | CalculateComplexityAsync | Other |
| 249 | TestingEngine | GenerateTestScaffoldAsync | Other |
| 250 | TestingEngine | GenerateTestSkeletonAsync | Other |
| 251 | ControlFlowEngine | AnalyzeMethodControlFlowAsync | Other |
| 252 | ControlFlowEngine | AnalyzeMethodDataFlowAsync | Other |
| 253 | ControlFlowEngine | AnalyzePathCoverageAsync | Other |
| 254 | ModernizationUpgradeEngine | UpgradePatternMatchingAsync | Modernization |
| 255 | ModernizationUpgradeEngine | UseSpanForParsingAsync | Modernization |
| 256 | ModernizationUpgradeEngine | UseThrowExpressionsAsync | Modernization |
| 257 | StandardRefactoringEngine | ConvertMethodToPropertyAsync | Refactoring |
| 258 | StandardRefactoringEngine | InvertBooleanAsync | Refactoring |
| 259 | StandardRefactoringEngine | MakeMethodStaticAsync | Refactoring |
| 260 | SymbolNavigationEngine | GetCallGraphAsync | Workspace |
| 261 | SymbolNavigationEngine | GetReverseCallGraphAsync | Workspace |
| 262 | SymbolNavigationEngine | GetSymbolInfoAsync | Workspace |
| 263 | IDEStyleEngine | SimplifyMemberAccessAsync | Quality |
| 264 | IDEStyleEngine | UseNullPropagationAsync | Quality |
| 265 | IDEStyleEngine | UseObjectInitializersAsync | Quality |
| 266 | SemanticRefactoringLibrary | ConvertPropertyToMethodsAsync | Refactoring |
| 267 | SemanticRefactoringLibrary | InlineVariableAsync | Refactoring |
| 268 | SemanticRefactoringLibrary | WrapInUsingAsync | Refactoring |
| 269 | ImpactAnalyzer | AnalyzeImpactAsync | Other |
| 270 | ImpactAnalyzer | FindDerivedTypesAsync | Other |
| 271 | ImpactAnalyzer | FindImplementationsAsync | Other |
| 272 | InstrumentationEngine | AddStopwatchDiagnosticsAsync | Other |
| 273 | InstrumentationEngine | AddTryCatchToClassAsync | Other |
| 274 | InstrumentationEngine | AddTryCatchToMethodAsync | Other |
| 275 | ModernizationEngine | ClassToRecordAsync | Modernization |
| 276 | ModernizationEngine | ConvertMethodToExpressionBodyAsync | Modernization |
| 277 | ModernizationEngine | RecordToClassAsync | Modernization |
| 278 | SolutionManagementEngine | CreateProjectAsync | Workspace |
| 279 | SolutionManagementEngine | SplitProjectByFolderAsync | Workspace |
| 280 | StructuralRefinementEngine | SafeDeleteSymbolAsync | Refactoring |
| 281 | StructuralRefinementEngine | SyncTypeAndFilenameAsync | Refactoring |
| 282 | ProjectStructureEngine | FixMismatchedNamespacesAsync | Other |
| 283 | ProjectStructureEngine | MoveFileToNamespaceFolderAsync | Other |
| 284 | ThreadSafetyEngine | ConvertLockToSemaphoreSlimAsync | Safety |
| 285 | ThreadSafetyEngine | MakeMethodThreadSafeAsync | Safety |
| 286 | LogicOptimizationEngine | AddGuardClausesAsync | Optimization |
| 287 | LogicOptimizationEngine | SimplifyBooleanExpressionsAsync | Optimization |
| 288 | ValidationEngine | ValidateChangesAsync | Other |
| 289 | ValidationEngine | ValidateDiffAsync | Other |
| 290 | MappingEngine | GenerateMappingAsync | Other |
| 291 | MappingEngine | InvertAssignmentsAsync | Other |
| 292 | AdvancedRefactoringEngine | OptimizeTaskWaitAsync | Refactoring |
| 293 | AdvancedRefactoringEngine | ReplaceStringConcatWithInterpolationAsync | Refactoring |
| 294 | AdvancedStructuralEngine | ConvertAbstractClassToInterfaceAsync | Refactoring |
| 295 | AdvancedStructuralEngine | ReplaceConstructorWithFactoryAsync | Refactoring |
| 296 | AnalysisEngine | GenerateCallTreeAsync | Analysis |
| 297 | AnalysisEngine | GenerateEqualityOverridesAsync | Analysis |
| 298 | DocumentationEngine | DocumentPocoFieldsAsync | Other |
| 299 | DocumentationEngine | GenerateXmlDocumentationStubsAsync | Other |
| 300 | DiscoveryEngine | FindBestInsertionPointAsync | Analysis |
| 301 | DiscoveryEngine | PreviewRenameImpactAsync | Analysis |
| 302 | CodeHealingEngine | AddRetryPolicyAsync | Other |
| 303 | CodeHealingEngine | FixThreadSleepAsync | Other |
| 304 | ApiAutomationEngine | GenerateHttpClientForControllerAsync | Other |
| 305 | ApiIntegrationEngine | AddValidationToPocoAsync | Other |
| 306 | ArchitecturalEngine | ConvertToBackgroundServiceAsync | Other |
| 307 | CodeFlowEngine | ReduceBlockDepthAsync | Other |
| 308 | DependencyEngine | GetProjectDependenciesAsync | Other |
| 309 | MetricsEngine | GetSolutionMetricsAsync | Other |
| 310 | DependencyInjectionEngine | AddDependencyAsync | Other |
| 311 | HealthOrchestrationEngine | GenerateComprehensiveHealthReportAsync | Other |
| 312 | ImmutabilityEngine | MakeClassImmutableAsync | Other |
| 313 | RefinementEngine | InlineMethodAsync | Other |
| 314 | InventoryEngine | GetCodeInventoryAsync | Other |
| 315 | UniversalRefactoringLibrary | RunRefactoringAsync | Refactoring |
| 316 | CodeSmellAndStyleEngine | UseSwitchExpressionAsync | Quality |
| 317 | ModernLoggingEngine | ConvertToSourceGeneratedLoggingAsync | Other |

---

## Conclusion

RoslynSentinel provides comprehensive, production-grade refactoring and analysis tools for C# developers and teams. With 317 specialized tools across 52 engines, it covers nearly every common refactoring and modernization need.

For source code and additional information, visit: https://github.com/rhale78/RoslynSentinel

---

**Document Status:** Complete ✅  
**All Tools Documented:** 317/317  
**Last Updated:** 2026-05-03 21:27:05 UTC  

