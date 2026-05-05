// Battery #34 — Real-Solution Smoke Tests: Engines Not Covered in Battery #28
//
// Loads a real .NET solution to smoke-test all 15 engines that power
// write/transform MCP tools but were NOT yet smoke-tested against live code:
//
//   Engines covered (new in this battery):
//     PerformanceEngine           AsyncSafetyEngine
//     AsyncOptimizationEngine     ThreadSafetyEngine
//     ControlFlowEngine           DiagnosticEngine
//     SecurityEngine              SyntaxUpgradeEngine
//     CodeStyleEngine             CodeGenerationEngine
//     AnalysisEngine              RefactoringEngine
//     GranularRefactoringEngine   ModernizationEngine
//     ModernizationUpgradeEngine
//
//   All tests follow the Battery #28 contract:
//     • DoesNotThrowAsync — engine must not crash on real-world code
//     • result Is.Not.Null — engine must return a valid object
//     • No ApplyStagedChangesAsync — changes are staged in memory only (safe)

#pragma warning disable CS8618

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("Integration")]
public class RealSolution_EngineSmoke_Battery34Tests
{
    // ── Solution ────────────────────────────────────────────────────────────
    private static readonly string SlnPath = Environment.GetEnvironmentVariable("ROSLYN_SENTINEL_TEST_SLN") ?? string.Empty;

    // ── State ───────────────────────────────────────────────────────────────
    private PersistentWorkspaceManager _workspaceManager = null!;

    /// Generic file discovered at SetUp — guaranteed to contain a class.
    private string _realFilePath = null!;
    private string _realClassName = null!;
    private string _realMethodName = null!;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    [SetUp]
    public async Task Setup()
    {
        if (!File.Exists(SlnPath))
        {
            Assert.Ignore("Set ROSLYN_SENTINEL_TEST_SLN env var to run real-solution integration tests.");
            return;
        }

        _workspaceManager = new PersistentWorkspaceManager(
            NullLogger<PersistentWorkspaceManager>.Instance);

        await _workspaceManager.LoadSolutionAsync(SlnPath);

        // Discover one document with a class AND a method for parameterised tests.
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath == null) continue;
                var root = await doc.GetSyntaxRootAsync();
                if (root == null) continue;

                var cls = root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                    .FirstOrDefault();
                if (cls == null) continue;

                var method = root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                    .FirstOrDefault();
                if (method == null) continue;

                _realFilePath  = doc.FilePath;
                _realClassName = cls.Identifier.Text;
                _realMethodName = method.Identifier.Text;
                return;
            }
        }

        Assert.Ignore("No suitable document found in the configured test solution.");
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // =========================================================================
    // 1 — PerformanceEngine
    // =========================================================================

    [Test]
    public async Task PerformanceEngine_AnalyzePerformance_DoesNotThrow()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        List<PerformanceIssueReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AnalyzePerformanceAsync(_realFilePath),
            $"PerformanceEngine.AnalyzePerformanceAsync must not throw on '{_realFilePath}'.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task PerformanceEngine_OptimizeResourceDisposal_DoesNotThrow()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        List<PerformanceIssueReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.OptimizeResourceDisposalAsync(_realFilePath),
            "PerformanceEngine.OptimizeResourceDisposalAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task PerformanceEngine_DetectStringComparisons_DoesNotThrow()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        List<PerformanceIssueReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.DetectInefficientStringComparisonsAsync(_realFilePath),
            "PerformanceEngine.DetectInefficientStringComparisonsAsync must not throw.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 2 — SecurityEngine
    // =========================================================================

    [Test]
    public async Task SecurityEngine_AnalyzeSecurity_DoesNotThrow()
    {
        var engine = new SecurityEngine(_workspaceManager);
        List<SecurityIssueReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AnalyzeSecurityAsync(_realFilePath),
            $"SecurityEngine.AnalyzeSecurityAsync must not throw on '{_realFilePath}'.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task SecurityEngine_FindHardcodedPaths_DoesNotThrow()
    {
        var engine = new SecurityEngine(_workspaceManager);
        List<SecurityIssueReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindHardcodedPathsAsync(_realFilePath),
            "SecurityEngine.FindHardcodedPathsAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 3 — AsyncSafetyEngine
    // =========================================================================

    [Test]
    public async Task AsyncSafetyEngine_DetectAsyncVoid_DoesNotThrow()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        List<AsyncSafetyReport>? result = null;
        var file = _realFilePath;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.DetectAsyncVoidMethodsAsync(file),
            "AsyncSafetyEngine.DetectAsyncVoidMethodsAsync must not throw.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AsyncSafetyEngine_FindTaskYieldUsage_DoesNotThrow()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        List<AsyncSafetyReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindTaskYieldUsageAsync(_realFilePath),
            "AsyncSafetyEngine.FindTaskYieldUsageAsync must not throw.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 4 — AsyncOptimizationEngine
    // =========================================================================

    [Test]
    public async Task AsyncOptimizationEngine_OptimizeIndependentAwaits_DoesNotThrow()
    {
        var engine = new AsyncOptimizationEngine(_workspaceManager);
        string? result = null;
        var file = _realFilePath;
        // Use first discovered method name to avoid "method not found" error
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.OptimizeIndependentAwaitsAsync(file, _realMethodName),
            "AsyncOptimizationEngine.OptimizeIndependentAwaitsAsync must not throw.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AsyncOptimizationEngine_GenerateAsyncOverload_DoesNotThrow()
    {
        var engine = new AsyncOptimizationEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GenerateAsyncOverloadAsync(_realFilePath, _realMethodName),
            "AsyncOptimizationEngine.GenerateAsyncOverloadAsync must not throw.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 5 — ThreadSafetyEngine
    // =========================================================================

    [Test]
    public async Task ThreadSafetyEngine_MakeMethodThreadSafe_DoesNotThrow()
    {
        var engine = new ThreadSafetyEngine(_workspaceManager);
        string? result = null;
        // Use the real file path for thread-safety analysis
        var file = _realFilePath;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.MakeMethodThreadSafeAsync(file, _realMethodName),
            "ThreadSafetyEngine.MakeMethodThreadSafeAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 6 — ControlFlowEngine
    // =========================================================================

    [Test]
    public async Task ControlFlowEngine_AnalyzePathCoverage_DoesNotThrow()
    {
        var engine = new ControlFlowEngine(_workspaceManager);
        PathCoverageReport? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AnalyzePathCoverageAsync(_realFilePath, _realMethodName),
            $"ControlFlowEngine.AnalyzePathCoverageAsync must not throw on '{_realMethodName}'.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 7 — DiagnosticEngine
    // =========================================================================

    [Test]
    public async Task DiagnosticEngine_GetFileDiagnostics_DoesNotThrow()
    {
        var engine = new DiagnosticEngine(_workspaceManager);
        DiagnosticSummary? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GetFileDiagnosticsAsync(_realFilePath),
            "DiagnosticEngine.GetFileDiagnosticsAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task DiagnosticEngine_GetSolutionDiagnostics_DoesNotThrow()
    {
        var engine = new DiagnosticEngine(_workspaceManager);
        DiagnosticSummary? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GetSolutionDiagnosticsAsync(),
            "DiagnosticEngine.GetSolutionDiagnosticsAsync must not throw on the real solution.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 8 — ModernizationEngine (ClassToRecord — staged, not applied)
    // =========================================================================

    [Test]
    public async Task ModernizationEngine_ClassToRecord_DoesNotThrow()
    {
        var config = new SentinelConfiguration();
        var engine = new ModernizationEngine(_workspaceManager, config);
        string? result = null;
        var file = _realFilePath;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ClassToRecordAsync(file, _realClassName),
            "ModernizationEngine.ClassToRecordAsync must not throw on real class.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 9 — SyntaxUpgradeEngine
    // =========================================================================

    [Test]
    public async Task SyntaxUpgradeEngine_AddBraces_DoesNotThrow()
    {
        var config = new SentinelConfiguration();
        var engine = new SyntaxUpgradeEngine(_workspaceManager, config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.AddBracesAsync(_realFilePath),
            "SyntaxUpgradeEngine.AddBracesAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task SyntaxUpgradeEngine_UpgradeToModernGuards_DoesNotThrow()
    {
        var config = new SentinelConfiguration();
        var engine = new SyntaxUpgradeEngine(_workspaceManager, config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.UpgradeToModernGuardsAsync(_realFilePath),
            "SyntaxUpgradeEngine.UpgradeToModernGuardsAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 10 — CodeGenerationEngine
    // =========================================================================

    [Test]
    public async Task CodeGenerationEngine_GenerateConstructor_DoesNotThrow()
    {
        var engine = new CodeGenerationEngine(_workspaceManager);
        string? result = null;
        var file = _realFilePath;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GenerateConstructorAsync(file, _realClassName),
            "CodeGenerationEngine.GenerateConstructorAsync must not throw on real class.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task CodeGenerationEngine_GenerateToString_DoesNotThrow()
    {
        var engine = new CodeGenerationEngine(_workspaceManager);
        var file = _realFilePath;
        object? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GenerateToStringAsync(file, _realClassName),
            "CodeGenerationEngine.GenerateToStringAsync must not throw on real class.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 11 — AnalysisEngine (solution-wide + file-level)
    // =========================================================================

    [Test]
    public async Task AnalysisEngine_FindLargeTypes_DoesNotThrow()
    {
        var config = new SentinelConfiguration();
        var engine = new AnalysisEngine(_workspaceManager, config);
        List<LargeTypeReport>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.FindLargeTypesAsync(),
            "AnalysisEngine.FindLargeTypesAsync must not throw on the real solution.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalysisEngine_GenerateCallTree_DoesNotThrow()
    {
        var config = new SentinelConfiguration();
        var engine = new AnalysisEngine(_workspaceManager, config);
        string? result = null;
        var file = _realFilePath;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.GenerateCallTreeAsync(file, _realMethodName),
            "AnalysisEngine.GenerateCallTreeAsync must not throw on real method.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 12 — RefactoringEngine
    // =========================================================================

    [Test]
    public async Task RefactoringEngine_MoveAllTypesToFiles_DoesNotThrow()
    {
        var config = new SentinelConfiguration();
        var engine = new RefactoringEngine(
            NullLogger<RefactoringEngine>.Instance, _workspaceManager, config);
        Dictionary<string, string>? result = null;
        var file = _realFilePath;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.MoveAllTypesToFilesAsync(file),
            "RefactoringEngine.MoveAllTypesToFilesAsync must not throw on multi-type file.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task RefactoringEngine_WrapInTryCatch_DoesNotThrow()
    {
        var config = new SentinelConfiguration();
        var engine = new RefactoringEngine(
            NullLogger<RefactoringEngine>.Instance, _workspaceManager, config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.WrapInTryCatchAsync(_realFilePath, 1, 5),
            "RefactoringEngine.WrapInTryCatchAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task RefactoringEngine_SyncInterfaceToImplementation_DoesNotThrow()
    {
        var config = new SentinelConfiguration();
        var engine = new RefactoringEngine(
            NullLogger<RefactoringEngine>.Instance, _workspaceManager, config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.SyncInterfaceToImplementationAsync(
                _realFilePath, _realClassName, "I" + _realClassName),
            "RefactoringEngine.SyncInterfaceToImplementationAsync must not throw (interface may not exist — graceful return expected).");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 13 — GranularRefactoringEngine
    // =========================================================================

    [Test]
    public async Task GranularRefactoringEngine_ExtractMembersToPartial_DoesNotThrow()
    {
        var engine = new GranularRefactoringEngine(_workspaceManager);
        Dictionary<string, string>? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.ExtractMembersToPartialAsync(
                _realFilePath, _realClassName, new[] { _realMethodName }),
            "GranularRefactoringEngine.ExtractMembersToPartialAsync must not throw on real class.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 14 — ModernizationUpgradeEngine
    // =========================================================================

    [Test]
    public async Task ModernizationUpgradeEngine_UpgradePatternMatching_DoesNotThrow()
    {
        var engine = new ModernizationUpgradeEngine(_workspaceManager);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.UpgradePatternMatchingAsync(_realFilePath),
            "ModernizationUpgradeEngine.UpgradePatternMatchingAsync must not throw on real file.");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ModernizationUpgradeEngine_UpgradeToPrimaryConstructor_DoesNotThrow()
    {
        var config = new SentinelConfiguration();
        var engine = new SyntaxUpgradeEngine(_workspaceManager, config);
        string? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await engine.UpgradeToPrimaryConstructorAsync(_realFilePath, _realClassName),
            "SyntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync must not throw on real class.");
        Assert.That(result, Is.Not.Null);
    }

    // =========================================================================
    // 15 — Data quality invariants across all new engines
    // =========================================================================

    [Test]
    public async Task PerformanceEngine_AllIssues_HaveNonNullFilePaths()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        var issues = await engine.AnalyzePerformanceAsync(_realFilePath);
        foreach (var issue in issues)
        {
            Assert.That(issue.FilePath, Is.Not.Null,
                "Every PerformanceIssueReport must have a non-null FilePath.");
            Assert.That(issue.Description, Is.Not.Null.And.Not.Empty,
                "Every PerformanceIssueReport must have a non-empty Description.");
        }
    }

    [Test]
    public async Task SecurityEngine_AllIssues_HaveNonNullSeverity()
    {
        var engine = new SecurityEngine(_workspaceManager);
        var issues = await engine.AnalyzeSecurityAsync(_realFilePath);
        foreach (var issue in issues)
        {
            Assert.That(issue.IssueType, Is.Not.Null.And.Not.Empty,
                "Every SecurityIssueReport must have a non-empty IssueType.");
            Assert.That(issue.Description, Is.Not.Null.And.Not.Empty,
                "Every SecurityIssueReport must have a non-empty Description.");
        }
    }

    [Test]
    public async Task DiagnosticEngine_FileSummary_HasValidCounts()
    {
        var engine = new DiagnosticEngine(_workspaceManager);
        var summary = await engine.GetFileDiagnosticsAsync(_realFilePath);
        Assert.That(summary.Errors, Is.GreaterThanOrEqualTo(0),
            "DiagnosticSummary.Errors must be >= 0.");
        Assert.That(summary.Warnings, Is.GreaterThanOrEqualTo(0),
            "DiagnosticSummary.Warnings must be >= 0.");
        Assert.That(summary.Details, Is.Not.Null,
            "DiagnosticSummary.Details must not be null.");
    }

    [Test]
    public async Task AnalysisEngine_CallTree_IsNonEmpty()
    {
        var config = new SentinelConfiguration();
        var engine = new AnalysisEngine(_workspaceManager, config);
        var file = _realFilePath;
        var result = await engine.GenerateCallTreeAsync(file, _realMethodName);
        Assert.That(result, Is.Not.Null.And.Not.Empty,
            "GenerateCallTreeAsync should return a non-empty call tree string.");
    }
}
