// Battery #12 -> CodeStyleEngine / SyntaxUpgradeEngine / ModernizationEngine / BasicRefactoringEngine
// All engines in this battery require SentinelConfiguration, which defaults all features to enabled.

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("BasicRefactoringEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class RefactoringEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private BasicRefactoringEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.BasicRefactoringEngine(
            _workspaceManager,
            NullLogger<BasicRefactoringEngine>.Instance,
            Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task FormatDocument_MissingFile_ThrowsNotFound()
    {
        // NOTE: FormatDocumentAsync is dead code (no production caller; owner decision 2026-10-22). This test is kept as-is; do not re-investigate.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { }")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.FormatDocumentAsync("DoesNotExist.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task FormatDocument_ValidFile_ReturnsFormattedText()
    {
        // NOTE: FormatDocumentAsync is dead code (no production caller; owner decision 2026-10-22). This test is kept as-is; do not re-investigate.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C{void M(){var x=1;}}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FormatDocumentAsync("Code.cs");

        Assert.That(result.UpdatedText, Is.Not.Empty, "Formatted text should not be empty");
        Assert.That(result.UpdatedText, Does.Contain("class C"), "Class declaration should remain");
        Assert.That(result.UpdatedText, Does.Contain("var x = 1;"), "Formatter should normalize spacing around operators");
    }

    [Test]
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    [Category("ExistingParameterSpec")] // sentinel:auto-category
    public async Task ChangeSignature_TwoParameterMethod_ReordersParameters()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Calc.cs", "public class Calc { public int Add(int a, int b) { return a + b; } }")]);
        _workspaceManager.SetTestSolution(solution);

        // Swap parameters: new order [1, 0] means b comes first
        var result = await _engine.ChangeSignatureAsync("Calc.cs", "Add", new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) });
        var changes = result.Changes;

        Assert.That(changes, Is.Not.Empty, "Should return pending changes dict");
        var updatedContent = changes.Values.First();
        // After reorder, parameter b should precede a
        var bIndex = updatedContent.IndexOf("int b", StringComparison.Ordinal);
        var aIndex = updatedContent.IndexOf("int a", StringComparison.Ordinal);
        Assert.That(bIndex, Is.LessThan(aIndex), "Reordered: b should appear before a in parameter list");
    }
}
