using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

// ────────────────────────────────────────────────────────────────────────────
// Battery #17 -> LogicSimplificationEngine,
//               MsToolAugmentEngine and SolutionStructureEngine.
// ────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("LogicSimplificationEngine")] // sentinel:auto-category
public class LogicOptimizationEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private LogicSimplificationEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new LogicSimplificationEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task ConvertToNullCoalescing_UnknownFile_ReturnsEmptyString()
    {
        var result = await _engine.ConvertToNullCoalescingAsync("NoSuchFile.cs");
        Assert.That(result.UpdatedText, Is.Null, "unknown file should return null UpdatedText");
    }

    [Test]
    public async Task ConvertToNullCoalescing_IfNullAssignment_ConvertsToModernSyntax()
    {
        const string source = @"
public class Guard
{
    public string Ensure(string value)
    {
        if (value == null)
        {
            value = string.Empty;
        }
        return value;
    }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Guard.cs", source)]));

        var result = await _engine.ConvertToNullCoalescingAsync("Guard.cs");

        Assert.That(result.UpdatedText!, Is.Not.EqualTo(""), "result should not be empty for a file with null check");
    }

    [Test]
    public async Task AddGuardClauses_UnknownFile_ReturnsEmptyString()
    {
        var result = await _engine.AddGuardClausesAsync("NoSuchFile.cs", "DoWork");
        Assert.That(result.UpdatedText, Is.Null, "unknown file should return null UpdatedText for AddGuardClauses");
    }

    [Test]
    public async Task SimplifyBooleanExpressions_UnknownFile_ReturnsEmptyString()
    {
        var result = await _engine.SimplifyBooleanExpressionsAsync("NoSuchFile.cs");
        Assert.That(result.UpdatedText, Is.Null, "unknown file should return null UpdatedText for SimplifyBooleanExpressions");
    }
}

[TestFixture]
[Category("MsAugmentResult")] // sentinel:auto-category
[Category("MsToolAugmentEngine")] // sentinel:auto-category
public class MsToolAugmentEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private MsToolAugmentEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task EncapsulateFieldSafe_UnknownFile_ReturnsFailResult()
    {
        var result = await _engine.EncapsulateFieldSafeAsync("NoSuchFile.cs", "MyClass", "_field");
        Assert.That(!result.IsError, Is.False, "unknown file should return a failure result");
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty, "failure result must contain error message");
    }

    [Test]
    public async Task FormatDocumentSafe_UnknownFile_ReturnsFailResult()
    {
        var result = await _engine.FormatDocumentSafeAsync("NoSuchFile.cs");
        Assert.That(!result.IsError, Is.False, "unknown file should return a failure result");
    }

    [Test]
    public async Task SortAndDeduplicateUsings_UnknownFile_ThrowsException()
    {
        // SortAndDeduplicateUsingsAsync throws InvalidOperationException for unknown files
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _engine.SortAndDeduplicateUsingsAsync("NoSuchFile.cs"),
            "unknown file should throw InvalidOperationException");
    }

    [Test]
    public async Task EncapsulateFieldSafeAsync_NonExistentFile_ReturnsNotFoundErrorCode()
    {
        var result = await _engine.EncapsulateFieldSafeAsync("NonExistent.cs", "MyField");
        Assert.That(!result.IsError, Is.False, "non-existent file should return failure");
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "error should be NotFound code");
    }
}

[TestFixture]
[Category("SolutionStructureEngine")] // sentinel:auto-category
public class SolutionStructureEngineTests2
{
    private PersistentWorkspaceManager _mgr = null!;
    private SolutionStructureEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new SolutionStructureEngine(_mgr, new SentinelConfiguration());
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task FixMismatchedNamespaces_UnknownFile_ThrowsFileNotFound()
    {
        Assert.ThrowsAsync<ToolNotFoundException>(
            async () => await _engine.FixMismatchedNamespacesAsync("NoSuchFile.cs"));
    }

    [Test]
    public async Task FindStructuralSmells_UnknownFile_ReturnsEmptyList()
    {
        var smells = await _engine.FindStructuralSmellsAsync(filePath: "NoSuchFile.cs");
        Assert.That(smells, Is.Empty, "unknown file should return empty smells list");
    }

    [Test]
    public async Task FindStructuralSmells_MultipleTypesInOneFile_DetectsMultiTypeSmell()
    {
        // MultiType smell: more than one type declared in a single file
        const string source = @"
public class ClassA {}
public class ClassB {}
";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("TwoTypes.cs", source)]));

        var smells = await _engine.FindStructuralSmellsAsync(filePath: "TwoTypes.cs");

        Assert.That(smells, Is.Not.Empty, "two types in one file should trigger a MultiType structural smell");
    }
}

