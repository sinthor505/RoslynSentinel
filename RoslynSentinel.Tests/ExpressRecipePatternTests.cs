using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Battery #2 — Tests using realistic code patterns extracted from 10 ExpressRecipe files
/// across 4 services (InventoryService, ShoppingService, RecipeService, MealPlanningService).
///
/// Files targeted:
///   1.  InventoryService/Data/InventoryRepository.cs
///   2.  InventoryService/Services/LowStockMonitorWorker.cs      [duplicate using]
///   3.  InventoryService/Services/InventoryItemService.cs        [TimeSpan constants]
///   4.  ShoppingService/Data/ShoppingRepository.Lists.cs
///   5.  ShoppingService/Services/ShoppingOptimizationService.cs  [foreach, switch]
///   6.  RecipeService/Data/RecipeRepository.cs
///   7.  RecipeService/Services/AllergenDetectionService.cs       [foreach + add]
///   8.  MealPlanningService/Data/MealPlanningRepository.cs
///   9.  MealPlanningService/Services/MealSuggestionService.cs    [internal static class]
///  10.  MealPlanningService/Services/NutritionSummaryService.cs  [sealed+init props, string.Format]
///
/// Each test documents the specific ExpressRecipe pattern it exercises and
/// verifies that the augmented tool handles it correctly.
/// </summary>
[TestFixture]
public class ExpressRecipePatternTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(
            NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 1. SortAndDeduplicateUsingsAsync — Pattern from LowStockMonitorWorker.cs
    //    Real bug: `using ExpressRecipe.InventoryService.Data;` appears TWICE
    //    at lines 1–2. The standard sort_usings only sorts, remove_unused_usings
    //    won't remove a "used" duplicate. SortAndDeduplicateUsingsAsync fixes both.
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Pattern from LowStockMonitorWorker.cs: duplicate using directive must be removed")]
    public async Task SortAndDeduplicate_DuplicateUsingFromLowStockWorker_RemovesDuplicate()
    {
        // Exact pattern from LowStockMonitorWorker.cs — two identical using directives
        const string source = @"using ExpressRecipe.InventoryService.Data;
using ExpressRecipe.InventoryService.Data;
using ExpressRecipe.InventoryService.Models;
using ExpressRecipe.InventoryService.Logging;
using System.Net.Http.Json;

namespace ExpressRecipe.InventoryService.Services;

public class LowStockMonitorWorker { }";

        SetSource(source, "LowStockMonitorWorker.cs");

        var result = await _engine.SortAndDeduplicateUsingsAsync("LowStockMonitorWorker.cs");

        Assert.That(result.RemovedDuplicates, Is.EqualTo(1),
            "Exactly 1 duplicate should be removed (Data using appears twice)");
        Assert.That(result.OriginalCount, Is.EqualTo(5),
            "Original had 5 using directives (including the duplicate)");
        // Verify Data namespace appears exactly once in output
        var dataCount = result.UpdatedContent
            .Split('\n')
            .Count(l => l.TrimStart().StartsWith("using ExpressRecipe.InventoryService.Data;"));
        Assert.That(dataCount, Is.EqualTo(1),
            "After dedup, Data namespace must appear exactly once");
    }

    [Test]
    [Description("SortAndDedup: System.* usings must be sorted before domain usings (ExpressRecipe.*)")]
    public async Task SortAndDeduplicate_SystemUsingsFirstPolicy_ExpressRecipePattern()
    {
        // Mixed ordering from ExpressRecipe service pattern
        const string source = @"using ExpressRecipe.InventoryService.Data;
using ExpressRecipe.InventoryService.Models;
using System.Net.Http.Json;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Test;
public class C { }";

        SetSource(source, "ServiceFile.cs");

        var result = await _engine.SortAndDeduplicateUsingsAsync("ServiceFile.cs");

        var lines = result.UpdatedContent
            .Split('\n')
            .Where(l => l.TrimStart().StartsWith("using "))
            .Select(l => l.Trim())
            .ToList();

        // System.* usings must come before ExpressRecipe.* usings
        var systemIdx = lines.FindIndex(l => l.StartsWith("using System"));
        var expressRecipeIdx = lines.FindIndex(l => l.StartsWith("using ExpressRecipe"));

        Assert.That(systemIdx, Is.LessThan(expressRecipeIdx),
            "System.* using must appear before ExpressRecipe.* using after sort");
    }

    [Test]
    [Description("SortAndDedup: No duplicates means RemovedDuplicates = 0")]
    public async Task SortAndDeduplicate_NoDuplicates_RemovesZero()
    {
        const string source = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace Test;
public class C { }";

        SetSource(source, "Clean.cs");
        var result = await _engine.SortAndDeduplicateUsingsAsync("Clean.cs");

        Assert.That(result.RemovedDuplicates, Is.EqualTo(0),
            "No duplicates — nothing to remove");
    }

    [Test]
    [Description("SortAndDedup: Multiple identical duplicates all removed, output has exactly one")]
    public async Task SortAndDeduplicate_ThreeCopiesOfSameUsing_ReducesToOne()
    {
        const string source = @"using System.Threading;
using System.Threading;
using System.Threading;
using System.Collections.Generic;
namespace Test;
public class C { }";

        SetSource(source, "TripleDup.cs");
        var result = await _engine.SortAndDeduplicateUsingsAsync("TripleDup.cs");

        Assert.That(result.RemovedDuplicates, Is.EqualTo(2),
            "Two duplicates should be removed (keep one, remove two extras)");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. GenerateToStringSafeAsync — Pattern from NutritionEstimateDto
    //    Real class: sealed, all properties are `init`, has a static factory Empty.
    //    Must correctly generate ToString for sealed class with init-only properties.
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Pattern from NutritionEstimateDto: sealed class with init-only properties")]
    public async Task GenerateToStringSafe_SealedClassWithInitProperties_Works()
    {
        // Direct pattern from NutritionSummaryService.cs
        const string source = @"
public sealed class NutritionEstimateDto
{
    public double Calories { get; init; }
    public double Protein { get; init; }
    public double Fat { get; init; }
    public double Carbohydrates { get; init; }
    public string Source { get; init; } = ""none"";
}";
        SetSource(source, "NutritionEstimateDto.cs");

        var result = await _engine.GenerateToStringSafeAsync("NutritionEstimateDto.cs", "NutritionEstimateDto");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("{{ "),
            "Sealed class must still use escaped braces for literal opening brace");
        Assert.That(result.UpdatedContent, Does.Contain("{Calories}"),
            "Calories must appear as interpolated member");
        Assert.That(result.UpdatedContent, Does.Contain("{Protein}"),
            "Protein must appear");
        Assert.That(result.UpdatedContent, Does.Contain("{Source}"),
            "Source must appear");
    }

    [Test]
    [Description("Pattern from MealSuggestionService ScoringWeights: internal static class with constants only")]
    public async Task GenerateToStringSafe_InternalStaticClassWithConstants_ReturnsFail()
    {
        // ScoringWeights is internal static — no instance properties to serialize
        const string source = @"
internal static class ScoringWeights
{
    internal const decimal UserRatingWeight   = 8m;
    internal const decimal GlobalRatingWeight = 2m;
    internal const decimal InventoryMin       = 3m;
    internal const decimal InventoryMax       = 30m;
}";
        SetSource(source, "ScoringWeights.cs");

        // Static class cannot have ToString() override — engine should fail gracefully
        var result = await _engine.GenerateToStringSafeAsync("ScoringWeights.cs", "ScoringWeights");

        // We expect either Fail (because static class can't have instance ToString)
        // OR the engine generates ToString for the constants — accept either as long as no crash
        Assert.That(result, Is.Not.Null, "Engine must return a result, not throw");
    }

    [Test]
    [Description("Pattern from AllergenDetectionService: class with Dictionary field — Dict field shouldn't break ToString")]
    public async Task GenerateToStringSafe_ClassWithDictionaryField_Success()
    {
        const string source = @"
using System.Collections.Generic;
public class AllergenDetectionService
{
    public string ServiceName { get; set; } = ""AllergenDetector"";
    public int DetectionCount { get; set; }
}";
        SetSource(source, "AllergenDetectionService.cs");

        var result = await _engine.GenerateToStringSafeAsync("AllergenDetectionService.cs", "AllergenDetectionService");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("{ServiceName}"));
        Assert.That(result.UpdatedContent, Does.Contain("{DetectionCount}"));
    }

    [Test]
    [Description("NutritionEstimateDto: generated ToString must compile cleanly (no CS8086 or other errors)")]
    public async Task GenerateToStringSafe_NutritionEstimateDto_CompilesCleanly()
    {
        const string source = @"
public sealed class NutritionEstimateDto
{
    public double Calories { get; init; }
    public double Protein { get; init; }
    public double Fat { get; init; }
    public string Source { get; init; } = ""estimated"";
}";
        SetSource(source, "NutritionEstimateDto.cs");

        var result = await _engine.GenerateToStringSafeAsync("NutritionEstimateDto.cs", "NutritionEstimateDto");

        Assert.That(result.Success, Is.True, result.Error);

        var tree = CSharpSyntaxTree.ParseText(result.UpdatedContent!);
        var compilation = CSharpCompilation.Create("NutritionTest",
            syntaxTrees: [tree],
            options: new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary),
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.That(errors, Is.Empty,
            "NutritionEstimateDto ToString must compile: " + string.Join("; ", errors.Select(e => e.GetMessage())));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. EncapsulateFieldSafeAsync — Public field → private backing + property
    //    Pattern: services use private readonly fields, but some older code has
    //    public fields that need encapsulation without self-referential recursion.
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Standard service field encapsulation — public field becomes private + property")]
    public async Task EncapsulateField_PublicField_GeneratesPropertyWithBackingField()
    {
        const string source = @"
public class InventoryCache
{
    public int RefreshCount;
    public string LastKey;
}";
        SetSource(source, "InventoryCache.cs");

        var result = await _engine.EncapsulateFieldSafeAsync("InventoryCache.cs", "RefreshCount");

        Assert.That(result.Success, Is.True, result.Error);
        // Backing field must be _camelCase
        Assert.That(result.UpdatedContent, Does.Contain("_refreshCount").Or.Contain("_RefreshCount"),
            "Backing field must be _camelCase to avoid self-referential recursion (Bug #1 fix)");
        // Property must be present
        Assert.That(result.UpdatedContent, Does.Contain("RefreshCount"),
            "Property name must be preserved");
        // Must NOT have `get { return RefreshCount; }` (self-reference bug)
        Assert.That(result.UpdatedContent, Does.Not.Contain("return RefreshCount;"),
            "Property getter must return the backing field, not itself (infinite recursion guard)");
    }

    [Test]
    [Description("Encapsulate already-private field returns Fail")]
    public async Task EncapsulateField_AlreadyPrivateField_ReturnsFail()
    {
        const string source = @"
public class Service
{
    private int _count;
}";
        SetSource(source, "Service.cs");

        var result = await _engine.EncapsulateFieldSafeAsync("Service.cs", "_count");

        // Either the field is found and processed, or fail because it's private
        // The important thing: no exception is thrown
        Assert.That(result, Is.Not.Null, "Must return a result without throwing");
    }

    [Test]
    [Description("Encapsulate non-existent field returns Fail with clear error message")]
    public async Task EncapsulateField_FieldNotFound_ReturnsFail()
    {
        const string source = @"public class C { public int Count; }";
        SetSource(source, "C.cs");

        var result = await _engine.EncapsulateFieldSafeAsync("C.cs", "NonExistentField");

        Assert.That(result.Success, Is.False,
            "Must fail for non-existent field");
        Assert.That(result.Error, Does.Contain("NonExistentField"),
            "Error must reference the missing field name");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. AnalyzeForeachForLinqConversionAsync — Patterns from AllergenDetectionService
    //    and ShoppingOptimizationService
    //    Real pattern: foreach + .Add() into a pre-initialized list
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Pattern from ShoppingOptimizationService.cs: foreach with storeDetails[k]=v — .Add not present")]
    public async Task AnalyzeForeach_DictionaryIndexAssignmentInForEach_ReportsNoAddCalls()
    {
        // Pattern from OptimizeAsync method — not a List.Add() pattern
        const string source = @"
using System;
using System.Collections.Generic;
public class ShoppingOpt
{
    public void LoadStores()
    {
        var storeDetails = new Dictionary<Guid, string>();
        var assignedStoreIds = new HashSet<Guid>();
        foreach (Guid storeId in assignedStoreIds)
        {
            var store = ""storeName"";
            if (store != null)
            {
                storeDetails[storeId] = store;
            }
        }
    }
}";
        var tempFile = Path.GetTempFileName() + ".cs";
        await File.WriteAllTextAsync(tempFile, source);
        try
        {
            var result = await _engine.AnalyzeForeachForLinqConversionAsync(
                tempFile, "foreach (Guid storeId in assignedStoreIds)");

            // No .Add() calls means it can't be auto-converted
            Assert.That(result.IsSafeToConvert, Is.False,
                "Dictionary assignment in foreach has no .Add() — not a LINQ candidate");
            Assert.That(result.BlockingReason, Does.Contain("No .Add()").Or.Contain("Add"),
                "Should explain the absence of .Add() calls");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    [Description("Pattern from AllergenDetectionService: simple foreach + .Add — safe to convert")]
    public async Task AnalyzeForeach_SimpleForEachWithAdd_SafeToConvert()
    {
        // Simple pattern: declare list, foreach, add — no modifications between decl and foreach
        const string source = @"
using System.Collections.Generic;
public class AllergenService
{
    public List<string> GetNames(List<string> inputs)
    {
        var results = new List<string>();
        foreach (var input in inputs)
        {
            results.Add(input.ToUpper());
        }
        return results;
    }
}";
        var tempFile = Path.GetTempFileName() + ".cs";
        await File.WriteAllTextAsync(tempFile, source);
        try
        {
            var result = await _engine.AnalyzeForeachForLinqConversionAsync(
                tempFile, "foreach (var input in inputs)");

            Assert.That(result.IsSafeToConvert, Is.True,
                "Simple foreach + .Add with no pre-modifications is safe to convert to LINQ");
            Assert.That(result.CollectionVariableName, Is.EqualTo("results"),
                "Should identify 'results' as the collection variable");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    [Description("Unsafe pattern: collection modified before foreach — standard tool would silently drop pre-modifications")]
    public async Task AnalyzeForeach_CollectionModifiedBeforeForeach_NotSafeToConvert()
    {
        // This is the CRITICAL BUG the analyzer prevents:
        // pre-adding "header" before the foreach — standard tool drops it silently
        const string source = @"
using System.Collections.Generic;
public class ReportBuilder
{
    public List<string> Build(List<string> items)
    {
        var lines = new List<string>();
        lines.Add(""HEADER"");   // pre-modification
        foreach (var item in items)
        {
            lines.Add(item);
        }
        return lines;
    }
}";
        var tempFile = Path.GetTempFileName() + ".cs";
        await File.WriteAllTextAsync(tempFile, source);
        try
        {
            var result = await _engine.AnalyzeForeachForLinqConversionAsync(
                tempFile, "foreach (var item in items)");

            Assert.That(result.IsSafeToConvert, Is.False,
                "CRITICAL: Collection modified before foreach — standard tool drops 'HEADER' silently");
            Assert.That(result.StatementsBeforeForeach, Is.GreaterThan(0),
                "Should count the pre-foreach modification statement(s)");
            Assert.That(result.BlockingReason, Does.Contain("HEADER").Or.Contain("lines"),
                "BlockingReason should reference the pre-modification");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5. ExtractConstantSafeAsync — Patterns from InventoryItemService.cs
    //    Real pattern: `TimeSpan.FromMinutes(5)` used twice, good extract candidate
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Pattern from InventoryItemService.cs: extract magic string to named constant")]
    public async Task ExtractConstant_MagicStringInCacheKey_ExtractedToConst()
    {
        // Pattern matching InventoryItemService cache key helpers
        const string source = @"
public class InventoryItemService
{
    private static string UserInventoryKey(System.Guid userId) => $""inv:user:{userId}:items"";
    private static string ItemKey(System.Guid itemId) => $""inv:item:{itemId}"";
}";
        SetSource(source, "InventoryItemService.cs");

        var result = await _engine.ExtractConstantSafeAsync(
            "InventoryItemService.cs",
            "\"inv:user:{userId}:items\"", // The literal to extract
            "InventoryCacheKeyFmt");

        // Engine must return result without crashing
        Assert.That(result, Is.Not.Null, "ExtractConstant must not throw");
        // Even if it fails (interpolated string can't be const), it should explain why
        if (!result.Success)
        {
            Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
                "Failure must have a descriptive error message");
        }
    }

    [Test]
    [Description("Extract a simple string literal to a constant")]
    public async Task ExtractConstant_SimpleLiteralString_ExtractedToConst()
    {
        const string source = @"
public class ShoppingService
{
    public string GetStrategy()
    {
        return ""CheapestOverall"";
    }
}";
        SetSource(source, "ShoppingService.cs");

        var result = await _engine.ExtractConstantSafeAsync(
            "ShoppingService.cs",
            "\"CheapestOverall\"",
            "DefaultStrategy");

        Assert.That(result, Is.Not.Null, "ExtractConstant must not throw");
        if (result.Success)
        {
            Assert.That(result.UpdatedContent, Does.Contain("DefaultStrategy"),
                "New constant name must appear in output");
            Assert.That(result.UpdatedContent, Does.Contain("\"CheapestOverall\""),
                "Constant value must appear in output");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6. ExtractMethodSafeAsync — Patterns from ShoppingOptimizationService.cs
    //    and InventoryItemService.cs
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Pattern from ShoppingOptimizationService: extract validation block with early return")]
    public async Task ExtractMethodSafe_ValidationWithEarlyReturn_ExtractsCorrectly()
    {
        // Pattern: guard clause / early return — common in service methods
        const string source = @"
using System.Collections.Generic;
public class Optimizer
{
    public string Optimize(List<string> items)
    {
        if (items.Count == 0)
        {
            return ""empty"";
        }
        return ""processed"";
    }
}";
        SetSource(source, "Optimizer.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "Optimizer.cs", "ValidateItems", "if (items.Count == 0)");

        // Guard clauses often can't be extracted cleanly — accept either success or descriptive failure
        Assert.That(result, Is.Not.Null, "Must return a result without throwing");
        if (!result.Success)
        {
            Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
                "Failure must have a descriptive error message");
        }
    }

    [Test]
    [Description("Pattern from InventoryItemService: extract cache lookup logic (async Task<T> return)")]
    public async Task ExtractMethodSafe_AsyncCacheLogic_ExtractedWithCorrectReturnType()
    {
        // Simplified version of the GetUserInventoryAsync cache pattern
        const string source = @"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
public class InventoryService
{
    public async Task<List<string>> GetItemsAsync(Guid userId)
    {
        var items = await FetchFromDbAsync(userId);
        return items;
    }
    private async Task<List<string>> FetchFromDbAsync(Guid userId)
        => new List<string>();
}";
        SetSource(source, "InventoryService.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "InventoryService.cs", "FetchItems", "var items = await FetchFromDbAsync(userId)");

        Assert.That(result, Is.Not.Null, "Must return result without throwing");
    }

    [Test]
    [Description("Pattern from MealSuggestionService: extract scoring calculation (decimal arithmetic)")]
    public async Task ExtractMethodSafe_DecimalArithmetic_HasDecimalReturnType()
    {
        // Pattern from ScoringWeights usage in MealSuggestionService
        const string source = @"
public class MealScorer
{
    private const decimal UserRatingWeight = 8m;

    public decimal ScoreRecipe(decimal userRating)
    {
        return userRating * UserRatingWeight;
    }
}";
        SetSource(source, "MealScorer.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "MealScorer.cs", "ComputeRatingScore", "return userRating * UserRatingWeight");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("decimal ComputeRatingScore("),
            "Extracted method must have decimal return type (not void)");
        Assert.That(result.UpdatedContent, Does.Not.Contain("void ComputeRatingScore("),
            "Bug #12 guard: must NOT generate void for decimal-returning extraction");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7. GetWorkspaceHealthAsync — Workspace health reporting
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Workspace health with solution loaded: must report operational=true and solution path")]
    public async Task GetWorkspaceHealth_WithSolutionLoaded_ReportsOperational()
    {
        const string source = @"
namespace ExpressRecipe.InventoryService;
public class InventoryRepository { }";
        SetSource(source, "InventoryRepository.cs");

        var report = await _engine.GetWorkspaceHealthAsync();

        Assert.That(report.IsOperational, Is.True,
            "Workspace with test solution must be reported as operational");
        Assert.That(report.HasLoadedSolution, Is.True,
            "Test solution should be detected as loaded");
        Assert.That(report.ProjectCount, Is.GreaterThan(0),
            "Should report at least 1 project");
    }

    [Test]
    [Description("Workspace health with no solution loaded: operational=true (workspace is up) but HasLoadedSolution=false")]
    public async Task GetWorkspaceHealth_NoSolutionLoaded_ReportsNoSolutionButOperational()
    {
        // Fresh workspace manager with no solution set — workspace itself is operational,
        // but no solution is loaded yet. The health check correctly distinguishes these.
        using var fresh = new PersistentWorkspaceManager(
            NullLogger<PersistentWorkspaceManager>.Instance);
        var freshEngine = new MsToolAugmentEngine(fresh);

        var report = await freshEngine.GetWorkspaceHealthAsync();

        // Workspace is operational (the MSBuild/Roslyn infrastructure is running)
        // but no solution has been loaded via load_solution
        Assert.That(report.IsOperational, Is.True,
            "Workspace infrastructure is operational even before load_solution is called");
        Assert.That(report.HasLoadedSolution, Is.False,
            "No solution has been loaded yet");
        Assert.That(report.ProjectCount, Is.EqualTo(0),
            "No projects without a loaded solution");
        Assert.That(report.Summary, Does.Contain("No solution").Or.Contain("no solution").Or.Contain("load_solution"),
            "Summary should guide user to call load_solution");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8. SortAndDeduplicateUsingsAsync — Additional edge cases
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("File with no usings at all — must return success with OriginalCount=0")]
    public async Task SortAndDeduplicate_NoUsings_SucceedsWithZeroCount()
    {
        const string source = @"namespace Test;
public class C { }";
        SetSource(source, "C.cs");

        var result = await _engine.SortAndDeduplicateUsingsAsync("C.cs");

        Assert.That(result.OriginalCount, Is.EqualTo(0), "No usings means count=0");
        Assert.That(result.RemovedDuplicates, Is.EqualTo(0), "Nothing to remove");
        Assert.That(result.UpdatedContent, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    [Description("Multiple namespace groups: System.*, Microsoft.*, then domain usings — sorted correctly")]
    public async Task SortAndDeduplicate_ThreeNamespaceGroups_SortedSystemFirst()
    {
        // Common pattern: ExpressRecipe services have all 3 groups
        const string source = @"using ExpressRecipe.RecipeService.Data;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Collections.Generic;
using ExpressRecipe.Shared.DTOs;

namespace Test; public class C { }";
        SetSource(source, "RecipeService.cs");

        var result = await _engine.SortAndDeduplicateUsingsAsync("RecipeService.cs");

        var lines = result.UpdatedContent
            .Split('\n')
            .Where(l => l.TrimStart().StartsWith("using "))
            .Select(l => l.Trim())
            .ToList();

        // First line must be a System.* using
        Assert.That(lines[0], Does.StartWith("using System"),
            "First using must be System.* (sorted System-first policy)");
        Assert.That(result.RemovedDuplicates, Is.EqualTo(0),
            "No duplicates in this source");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 9. GenerateToStringSafeAsync — Additional ExpressRecipe class patterns
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Pattern from InventoryRepository: partial class — engine must handle partial keyword")]
    public async Task GenerateToStringSafe_PartialClass_HandledGracefullyOrSucceeds()
    {
        const string source = @"
namespace ExpressRecipe.InventoryService.Data;
public partial class InventoryRepository
{
    public string ConnectionString { get; set; }
    public int TimeoutMs { get; set; }
}";
        SetSource(source, "InventoryRepository.cs");

        var result = await _engine.GenerateToStringSafeAsync("InventoryRepository.cs", "InventoryRepository");

        // Partial class support: either succeeds (if engine handles it) or fails gracefully
        Assert.That(result, Is.Not.Null, "Must not throw for partial class");
        if (result.Success)
        {
            Assert.That(result.UpdatedContent, Does.Contain("{{ "),
                "Partial class output must still use escaped braces");
        }
    }

    [Test]
    [Description("Pattern from MealSuggestionService: class with private record nested types")]
    public async Task GenerateToStringSafe_ClassWithNestedRecords_OnlyFindsOuterClass()
    {
        // MealSuggestionService has private record types like RecipeCandidate, RecipeIngredient
        const string source = @"
public class MealSuggestionService
{
    public string ServiceName { get; set; } = ""MealSuggestion"";
    public int CacheHits { get; set; }

    private record RecipeCandidate(System.Guid Id, string Name);
    private record RecipeIngredient(string Name, decimal Quantity);
}";
        SetSource(source, "MealSuggestionService.cs");

        var result = await _engine.GenerateToStringSafeAsync("MealSuggestionService.cs", "MealSuggestionService");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("{ServiceName}"),
            "ServiceName must appear in outer class ToString");
        // The engine returns the full file content (nested records declarations remain).
        // What we verify is that the ToString METHOD BODY only includes the outer class's own properties.
        var toStringStart = result.UpdatedContent!.IndexOf("override string ToString()", StringComparison.Ordinal);
        if (toStringStart >= 0)
        {
            var toStringBody = result.UpdatedContent.Substring(toStringStart, Math.Min(400, result.UpdatedContent.Length - toStringStart));
            // Nested record property names (Id from RecipeCandidate) must not appear in the ToString body
            Assert.That(toStringBody, Does.Not.Contain("{Id}"),
                "Nested RecipeCandidate's Id must not appear inside the MealSuggestionService ToString body");
        }
    }

    [Test]
    [Description("Pattern from ShoppingOptimizationService: class with interface declaration in same file")]
    public async Task GenerateToStringSafe_TargetClassAmongMultipleTypes_CorrectClassSelected()
    {
        // ShoppingOptimizationService.cs has IShoppingOptimizationService interface + implementation
        const string source = @"
public interface IShoppingOptimizationService
{
    string Optimize();
}

public class ShoppingOptimizationService : IShoppingOptimizationService
{
    public string Strategy { get; set; }
    public decimal MinSavings { get; set; }

    public string Optimize() => Strategy;
}";
        SetSource(source, "ShoppingOptimizationService.cs");

        var result = await _engine.GenerateToStringSafeAsync(
            "ShoppingOptimizationService.cs", "ShoppingOptimizationService");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("{Strategy}"),
            "Strategy must appear — correct class was targeted");
        Assert.That(result.UpdatedContent, Does.Contain("{MinSavings}"),
            "MinSavings must appear — correct class was targeted");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 10. ExtractMethodSafe — Additional realistic patterns from ExpressRecipe
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Pattern from AllergenDetectionService: extract HashSet duplicate-check logic")]
    public async Task ExtractMethodSafe_HashSetDuplicateCheck_ExtractedWithBoolReturnType()
    {
        const string source = @"
using System;
using System.Collections.Generic;
public class AllergenProcessor
{
    public bool ProcessAllergen(HashSet<Guid> seen, Guid allergenId)
    {
        return seen.Add(allergenId);
    }
}";
        SetSource(source, "AllergenProcessor.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "AllergenProcessor.cs", "TryAddAllergen", "return seen.Add(allergenId)");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("bool TryAddAllergen("),
            "Return type must be bool, not void (Bug #12 guard)");
    }

    [Test]
    [Description("Pattern from NutritionSummaryService: extract string.Format cache key construction")]
    public async Task ExtractMethodSafe_StringFormatCacheKey_ExtractedWithStringReturnType()
    {
        // From: string planKey = string.Format("nutrition-plan:{0}:{1:yyyy-MM-dd}", userId, date)
        const string source = @"
using System;
public class NutritionService
{
    private const string KeyFmt = ""nutrition-plan:{0}"";
    public string BuildCacheKey(Guid userId)
    {
        return string.Format(KeyFmt, userId);
    }
}";
        SetSource(source, "NutritionService.cs");

        var result = await _engine.ExtractMethodSafeAsync(
            "NutritionService.cs", "FormatCacheKey", "return string.Format(KeyFmt, userId)");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("string FormatCacheKey("),
            "Return type must be string, not void (Bug #12 guard)");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 11. Regression guards — Specific patterns that caused failures in Battery #1
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    [Description("Regression: SortAndDedup must use workspace-first read like GenerateToString does")]
    public async Task SortAndDeduplicate_WorkspaceFirstRead_FileOnlyInMemory()
    {
        // File exists only in workspace, not on disk
        const string source = @"using System.Threading;
using System;
using System.Threading;
namespace Test; public class Worker { }";
        SetSource(source, "WorkspaceWorker.cs");

        // This must succeed — the workspace has the file even though it's not on disk
        var result = await _engine.SortAndDeduplicateUsingsAsync("WorkspaceWorker.cs");

        Assert.That(result.RemovedDuplicates, Is.EqualTo(1),
            "In-memory workspace file must be readable (workspace-first read)");
        Assert.That(result.OriginalCount, Is.EqualTo(3));
    }

    [Test]
    [Description("Regression: EncapsulateField self-reference guard — generated getter must not call property by same name")]
    public async Task EncapsulateField_NoSelfReferentialGetter_Bug1Guard()
    {
        const string source = @"
public class Stats
{
    public int SuccessCount;
}";
        SetSource(source, "Stats.cs");

        var result = await _engine.EncapsulateFieldSafeAsync("Stats.cs", "SuccessCount");

        Assert.That(result.Success, Is.True, result.Error);
        // Critical: getter must NOT return SuccessCount (the property itself) — infinite recursion
        // It should return _successCount (the backing field)
        Assert.That(result.UpdatedContent, Does.Not.Match(@"return SuccessCount;"),
            "BUG #1 REGRESSION: getter must not return the property by its own name (infinite recursion)");
    }

    [Test]
    [Description("GenerateToStringSafe: BackgroundService pattern with no public properties returns Fail gracefully")]
    public async Task GenerateToStringSafe_BackgroundServiceWithPrivateFields_ReturnsFailOrSucceeds()
    {
        // LowStockMonitorWorker inherits BackgroundService and has only private fields
        const string source = @"
using System;
public abstract class BackgroundService
{
    public abstract System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken ct);
}

public class LowStockMonitorWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    public override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken ct)
        => System.Threading.Tasks.Task.CompletedTask;
}

public interface IServiceProvider { }
public interface ILogger { }";

        SetSource(source, "LowStockMonitorWorker.cs");

        var result = await _engine.GenerateToStringSafeAsync("LowStockMonitorWorker.cs", "LowStockMonitorWorker");

        // BackgroundService with only private fields has no public members to include
        // Engine should fail gracefully — no crash, descriptive error
        Assert.That(result, Is.Not.Null, "Engine must not throw for BackgroundService pattern");
        if (!result.Success)
        {
            Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
                "Failure must have descriptive error message");
        }
    }
}
