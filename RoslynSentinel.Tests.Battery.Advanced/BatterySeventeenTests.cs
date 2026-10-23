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
    public async Task ConvertToNullCoalescing_MissingFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ConvertToNullCoalescingAsync("NoSuchFile.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
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

        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("??"));
    }

    [Test]
    public async Task AddGuardClauses_MissingFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.AddGuardClausesAsync("NoSuchFile.cs", "DoWork"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task SimplifyBooleanExpressions_MissingFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.SimplifyBooleanExpressionsAsync("NoSuchFile.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
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
    public async Task EncapsulateFieldSafe_UnknownFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.EncapsulateFieldSafeAsync("NoSuchFile.cs", "MyClass", "_field"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task FormatDocumentSafe_UnknownFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.FormatDocumentSafeAsync("NoSuchFile.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task SortAndDeduplicateUsings_UnknownFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.SortAndDeduplicateUsingsAsync("NoSuchFile.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task EncapsulateFieldSafeAsync_NonExistentFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.EncapsulateFieldSafeAsync("NonExistent.cs", "MyField"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "error should be NotFound code");
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
        await Assert.ThrowsAsync<ToolNotFoundException>(
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

        Assert.That(smells.Select(s => s.ToString()), Has.Some.Contains("MULTI_TYPE"), "two types in one file should trigger a MultiType structural smell");
    }
}

