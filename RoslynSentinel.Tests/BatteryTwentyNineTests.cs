// Battery 29 — Real-solution smoke tests for all 15 remaining engines
// Loads ExpressRecipe.sln, discovers a real .cs file, class, method, and project,
// then exercises every engine's public async API against live code.
// All tests are read-only (engines return new content strings; nothing is written to disk).

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// B29 — All 15 remaining engines exercised against the ExpressRecipe solution
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Integration")]
public class B29_AllEngines_RealSolution_SmokeTests
{
    private const string SlnPath = @"E:\source\repos\rhale78\ExpressRecipe\ExpressRecipe.sln";

    private PersistentWorkspaceManager _workspaceManager = null!;
    private SentinelConfiguration _config = null!;

    private string _realFilePath = null!;
    private string _realClassName = null!;
    private string _realMethodName = null!;
    private string _realProjectName = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!File.Exists(SlnPath))
            Assert.Ignore("ExpressRecipe solution not found — skipping B29 integration tests.");

        _config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        await _workspaceManager.LoadSolutionAsync(SlnPath);

        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        // Capture project name from the first available project
        _realProjectName = solution.Projects.FirstOrDefault()?.Name
            ?? throw new InvalidOperationException("No projects found in solution.");

        // Discover a .cs file that has a class with at least one non-constructor method
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                var root = await doc.GetSyntaxRootAsync();
                if (root == null) continue;

                var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                if (cls == null) continue;

                var method = cls.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Body != null); // block-bodied only for flow analysis
                if (method == null) continue;

                _realFilePath = doc.FilePath;
                _realClassName = cls.Identifier.Text;
                _realMethodName = method.Identifier.Text;
                return;
            }
        }

        Assert.Ignore("No suitable class+method document found in ExpressRecipe solution.");
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // ══════════════════════════════════════════════════════════════════════════
    // 1. AsyncSafetyEngine (6 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AsyncSafetyEngine_DetectAsyncVoidMethods_DoesNotThrow()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        List<AsyncSafetyReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.DetectAsyncVoidMethodsAsync(_realFilePath),
            "DetectAsyncVoidMethodsAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null, "Result must not be null.");
    }

    [Test]
    public async Task AsyncSafetyEngine_FindConfigureAwaitMissing_DoesNotThrow()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        List<AsyncSafetyReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindConfigureAwaitMissingAsync(_realFilePath),
            "FindConfigureAwaitMissingAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AsyncSafetyEngine_FindBlockingCallsInAsync_DoesNotThrow()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        List<AsyncSafetyReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindBlockingCallsInAsyncAsync(_realFilePath),
            "FindBlockingCallsInAsyncAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AsyncSafetyEngine_FindUnsafeLazyInit_DoesNotThrow()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        List<AsyncSafetyReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindUnsafeLazyInitAsync(_realFilePath),
            "FindUnsafeLazyInitAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AsyncSafetyEngine_DetectValueTaskMisuse_DoesNotThrow()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        List<AsyncSafetyReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.DetectValueTaskMisuseAsync(_realFilePath),
            "DetectValueTaskMisuseAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AsyncSafetyEngine_FindUnawaitedFireAndForget_DoesNotThrow()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        List<AsyncSafetyReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindUnawaitedFireAndForgetAsync(_realFilePath),
            "FindUnawaitedFireAndForgetAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. CodeSmellAndStyleEngine (2 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CodeSmellAndStyleEngine_ScanForSmells_DoesNotThrow()
    {
        var engine = new CodeSmellAndStyleEngine(_workspaceManager);
        List<CodeSmell>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ScanForSmellsAsync(_realFilePath),
            "ScanForSmellsAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task CodeSmellAndStyleEngine_UseSwitchExpression_DoesNotThrow()
    {
        var engine = new CodeSmellAndStyleEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.UseSwitchExpressionAsync(_realFilePath),
            "UseSwitchExpressionAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null, "Result string must not be null (may be empty if no candidates).");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. SyntaxUpgradeEngine (5 tests) — all features enabled by default config
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SyntaxUpgradeEngine_UpgradeToModernGuards_DoesNotThrow()
    {
        var engine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.UpgradeToModernGuardsAsync(_realFilePath),
            "UpgradeToModernGuardsAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null, "Must return non-null (empty string OK if feature gated or no candidates).");
    }

    [Test]
    public async Task SyntaxUpgradeEngine_AddBraces_DoesNotThrow()
    {
        var engine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AddBracesAsync(_realFilePath),
            "AddBracesAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task SyntaxUpgradeEngine_UpgradePatternMatching_DoesNotThrow()
    {
        var engine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.UpgradePatternMatchingAsync(_realFilePath),
            "UpgradePatternMatchingAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task SyntaxUpgradeEngine_UseFieldBackedProperties_DoesNotThrow()
    {
        var engine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.UseFieldBackedPropertiesAsync(_realFilePath),
            "UseFieldBackedPropertiesAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task SyntaxUpgradeEngine_CleanupImplicitSpans_DoesNotThrow()
    {
        var engine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.CleanupImplicitSpansAsync(_realFilePath),
            "CleanupImplicitSpansAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. CodeHealingEngine (2 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CodeHealingEngine_FixThreadSleep_DoesNotThrow()
    {
        var engine = new CodeHealingEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FixThreadSleepAsync(_realFilePath),
            "FixThreadSleepAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null, "Must return non-null (empty string OK if no Thread.Sleep found or feature gated).");
    }

    [Test]
    public async Task CodeHealingEngine_AddRetryPolicy_DoesNotThrow()
    {
        // sl=0, el=0 causes AddRetryPolicyAsync to fall back to the first method in the file
        var engine = new CodeHealingEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AddRetryPolicyAsync(_realFilePath, 0, 0, 3),
            "AddRetryPolicyAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5. AdvancedLogicEngine (3 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AdvancedLogicEngine_InvertBooleanLogic_NonExistentBool_ReturnsEmptyDict()
    {
        var engine = new AdvancedLogicEngine(_workspaceManager);
        Dictionary<string, string>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.InvertBooleanLogicAsync(_realFilePath, "__nonExistentBoolXYZ__"),
            "InvertBooleanLogicAsync must not throw even when bool name is not found.");
        Assert.That(result, Is.Not.Null, "Must return a dict (empty is OK when bool not found).");
    }

    [Test]
    public async Task AdvancedLogicEngine_ConvertForEachToFor_DoesNotThrow()
    {
        var engine = new AdvancedLogicEngine(_workspaceManager);
        string? result = null;
        // Line 1 is likely a using directive — no foreach; method gracefully returns original
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ConvertForEachToForAsync(_realFilePath, 1),
            "ConvertForEachToForAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AdvancedLogicEngine_ConvertWhileToFor_DoesNotThrow()
    {
        var engine = new AdvancedLogicEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ConvertWhileToForAsync(_realFilePath, 1),
            "ConvertWhileToForAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6. AdvancedRefactoringEngine (2 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AdvancedRefactoringEngine_ReplaceStringConcatWithInterpolation_DoesNotThrow()
    {
        var engine = new AdvancedRefactoringEngine(_workspaceManager);
        string? result = null;
        // Real file path is required; engine throws "File not found." on path miss
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ReplaceStringConcatWithInterpolationAsync(_realFilePath),
            "ReplaceStringConcatWithInterpolationAsync must not throw when given a real file path.");
        Assert.That(result, Is.Not.Null, "Must return non-null (unchanged source if no string concat found).");
    }

    [Test]
    public async Task AdvancedRefactoringEngine_OptimizeTaskWait_DoesNotThrow()
    {
        var engine = new AdvancedRefactoringEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.OptimizeTaskWaitAsync(_realFilePath),
            "OptimizeTaskWaitAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7. ModernizationEngine (3 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ModernizationEngine_ClassToRecord_DoesNotThrow()
    {
        var engine = new ModernizationEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ClassToRecordAsync(_realFilePath, _realClassName),
            "ClassToRecordAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null, "Must return non-null (empty string if feature gated or class cannot be converted).");
    }

    [Test]
    public async Task ModernizationEngine_ConvertMethodToExpressionBody_DoesNotThrow()
    {
        var engine = new ModernizationEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ConvertMethodToExpressionBodyAsync(_realFilePath, _realMethodName),
            "ConvertMethodToExpressionBodyAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ModernizationEngine_ConvertToPattern_DoesNotThrow()
    {
        var engine = new ModernizationEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ConvertToPatternAsync(_realFilePath),
            "ConvertToPatternAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8. GranularRefactoringEngine (3 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GranularRefactoringEngine_RunMicroRefactoring_DoesNotThrow()
    {
        var engine = new GranularRefactoringEngine(_workspaceManager);
        string? result = null;
        // RunMicroRefactoringAsync has real dispatch — use a known valid ID
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.RunMicroRefactoringAsync(_realFilePath, "add-braces", 1),
            "RunMicroRefactoringAsync must not throw on real solution with valid ID.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GranularRefactoringEngine_InlineField_NonExistentField_ReturnsErrorString()
    {
        var engine = new GranularRefactoringEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.InlineFieldAsync(_realFilePath, "__nonExistentFieldXYZ__"),
            "InlineFieldAsync must not throw even when field is not found.");
        Assert.That(result, Is.Not.Null, "Must return a non-null string (error message if field not found).");
        // Engine prefixes error messages with "// ERROR:" when field is missing
        Assert.That(result, Does.StartWith("// ERROR:").Or.Not.Contain("System."),
            "Non-found field must produce a graceful error message, not an exception trace.");
    }

    [Test]
    public async Task GranularRefactoringEngine_MoveTypeToOuterScope_DoesNotThrow()
    {
        var engine = new GranularRefactoringEngine(_workspaceManager);
        string? result = null;
        // Non-existent nested type → engine returns original source or descriptive message
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.MoveTypeToOuterScopeAsync(_realFilePath, "__nonExistentNestedType__"),
            "MoveTypeToOuterScopeAsync must not throw even when nested type is not found.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 9. ControlFlowEngine (3 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ControlFlowEngine_AnalyzePathCoverage_DoesNotThrow()
    {
        var engine = new ControlFlowEngine(_workspaceManager);
        PathCoverageReport? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AnalyzePathCoverageAsync(_realFilePath, _realMethodName),
            "AnalyzePathCoverageAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null, "PathCoverageReport must not be null.");
        Assert.That(result.MethodName, Is.EqualTo(_realMethodName),
            "Report must carry back the queried method name.");
    }

    [Test]
    public async Task ControlFlowEngine_AnalyzeMethodControlFlow_DoesNotThrow()
    {
        var engine = new ControlFlowEngine(_workspaceManager);
        ControlFlowAnalysisResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AnalyzeMethodControlFlowAsync(_realFilePath, _realMethodName),
            "AnalyzeMethodControlFlowAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.MethodName, Is.EqualTo(_realMethodName));
    }

    [Test]
    public async Task ControlFlowEngine_AnalyzeMethodDataFlow_DoesNotThrow()
    {
        var engine = new ControlFlowEngine(_workspaceManager);
        DataFlowAnalysisResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AnalyzeMethodDataFlowAsync(_realFilePath, _realMethodName),
            "AnalyzeMethodDataFlowAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.MethodName, Is.EqualTo(_realMethodName));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 10. CodeStyleEngine (4 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CodeStyleEngine_FixDangerousLock_DoesNotThrow()
    {
        var engine = new CodeStyleEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FixDangerousLockAsync(_realFilePath),
            "FixDangerousLockAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task CodeStyleEngine_SimplifyVerbosity_DoesNotThrow()
    {
        var engine = new CodeStyleEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.SimplifyVerbosityAsync(_realFilePath),
            "SimplifyVerbosityAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task CodeStyleEngine_UseCollectionExpressions_DoesNotThrow()
    {
        var engine = new CodeStyleEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.UseCollectionExpressionsAsync(_realFilePath),
            "UseCollectionExpressionsAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task CodeStyleEngine_UseIndexFromEnd_DoesNotThrow()
    {
        var engine = new CodeStyleEngine(_workspaceManager, _config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.UseIndexFromEndAsync(_realFilePath),
            "UseIndexFromEndAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 11. PerformanceEngine (4 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PerformanceEngine_AnalyzePerformance_DoesNotThrow()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        List<PerformanceIssueReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AnalyzePerformanceAsync(_realFilePath),
            "AnalyzePerformanceAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task PerformanceEngine_OptimizeResourceDisposal_DoesNotThrow()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        List<PerformanceIssueReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.OptimizeResourceDisposalAsync(_realFilePath),
            "OptimizeResourceDisposalAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task PerformanceEngine_DetectInefficientStringComparisons_DoesNotThrow()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        List<PerformanceIssueReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.DetectInefficientStringComparisonsAsync(_realFilePath),
            "DetectInefficientStringComparisonsAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task PerformanceEngine_FindBoxingAllocations_DoesNotThrow()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        List<PerformanceIssueReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindBoxingAllocationsAsync(_realFilePath),
            "FindBoxingAllocationsAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 12. DependencyEngine (2 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DependencyEngine_GetProjectDependencies_DoesNotThrow()
    {
        var engine = new DependencyEngine(_workspaceManager);
        DependencyEngine.ProjectDependencyReport? result = null;
        // Uses real project name discovered in SetUp — must not throw
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GetProjectDependenciesAsync(_realProjectName),
            "GetProjectDependenciesAsync must not throw when given a real project name.");
        Assert.That(result, Is.Not.Null, "ProjectDependencyReport must not be null.");
        Assert.That(result.ProjectReferences, Is.Not.Null, "ProjectReferences list must not be null.");
        Assert.That(result.PackageReferences, Is.Not.Null, "PackageReferences list must not be null.");
    }

    [Test]
    public async Task DependencyEngine_FindUnusedReferences_DoesNotThrow()
    {
        var engine = new DependencyEngine(_workspaceManager);
        List<string>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindUnusedReferencesAsync(_realProjectName),
            "FindUnusedReferencesAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null, "Result list must not be null (may be empty).");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 13. ThreadSafetyEngine (2 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ThreadSafetyEngine_MakeMethodThreadSafe_NonExistentMethod_GracefulError()
    {
        var engine = new ThreadSafetyEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.MakeMethodThreadSafeAsync(_realFilePath, "__nonExistentMethodXYZ__"),
            "MakeMethodThreadSafeAsync must not throw even when method is not found.");
        Assert.That(result, Is.Not.Null,
            "Must return non-null (error message string when method not found).");
        // Engine returns "// Error: Method '...' not found or has no body." for missing methods
        Assert.That(result, Does.Contain("nonExistentMethodXYZ").Or.StartsWith("// Error"),
            "Non-found method must produce a graceful error string.");
    }

    [Test]
    public async Task ThreadSafetyEngine_ConvertLockToSemaphoreSlim_DoesNotThrow()
    {
        var engine = new ThreadSafetyEngine(_workspaceManager);
        string? result = null;
        // Pass a real method name; if method has no lock statements, engine returns source unchanged
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ConvertLockToSemaphoreSlimAsync(_realFilePath, _realMethodName),
            "ConvertLockToSemaphoreSlimAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 14. MsToolAugmentEngine (5 tests)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MsToolAugmentEngine_SortAndDeduplicateUsings_DoesNotThrow()
    {
        var engine = new MsToolAugmentEngine(_workspaceManager);
        UsingsCleanupResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.SortAndDeduplicateUsingsAsync(_realFilePath),
            "SortAndDeduplicateUsingsAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null, "UsingsCleanupResult must not be null.");
        Assert.That(result.UpdatedContent, Is.Not.Null.And.Not.Empty,
            "UpdatedContent must contain the reformatted source.");
        Assert.That(result.OriginalCount, Is.GreaterThanOrEqualTo(0),
            "OriginalCount must be a non-negative integer.");
    }

    [Test]
    public async Task MsToolAugmentEngine_FormatDocumentSafe_DoesNotThrow()
    {
        var engine = new MsToolAugmentEngine(_workspaceManager);
        MsAugmentResult? result = null;
        // preview=true (default) — returns formatted content without writing to disk
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FormatDocumentSafeAsync(_realFilePath, preview: true),
            "FormatDocumentSafeAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True, "Formatting a real file must succeed.");
        Assert.That(result.UpdatedContent, Is.Not.Null.And.Not.Empty,
            "Formatted content must not be empty.");
    }

    [Test]
    public async Task MsToolAugmentEngine_GenerateToStringSafe_DoesNotThrow()
    {
        var engine = new MsToolAugmentEngine(_workspaceManager);
        MsAugmentResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GenerateToStringSafeAsync(_realFilePath, _realClassName),
            "GenerateToStringSafeAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null);
        // Success OR graceful failure — either way, result must carry a message
        if (!result.Success)
            Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
                "Failed result must carry a non-empty error message.");
    }

    [Test]
    public async Task MsToolAugmentEngine_EncapsulateFieldSafe_NonExistentField_ReturnsFailGracefully()
    {
        var engine = new MsToolAugmentEngine(_workspaceManager);
        MsAugmentResult? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.EncapsulateFieldSafeAsync(_realFilePath, "__nonExistentFieldXYZ__"),
            "EncapsulateFieldSafeAsync must not throw when field is not found.");
        Assert.That(result, Is.Not.Null, "Must return a result object (not null) on field-not-found.");
        Assert.That(result.Success, Is.False,
            "Result must be Success=false when field does not exist.");
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
            "Error property must carry a descriptive message when field not found.");
    }

    [Test]
    public async Task MsToolAugmentEngine_PreviewAddMissingUsings_DoesNotThrow()
    {
        var engine = new MsToolAugmentEngine(_workspaceManager);
        AddUsingsPreview? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.PreviewAddMissingUsingsAsync(_realFilePath),
            "PreviewAddMissingUsingsAsync must not throw on real solution.");
        Assert.That(result, Is.Not.Null, "AddUsingsPreview must not be null.");
    }
}
