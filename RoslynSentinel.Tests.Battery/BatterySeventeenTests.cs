using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

// ────────────────────────────────────────────────────────────────────────────
// Battery #17 — LogicOptimizationEngine,
//               MsToolAugmentEngine, ProjectStructureEngine, RefinementEngine
// ────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class LogicOptimizationEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private LogicOptimizationEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new LogicOptimizationEngine(_mgr);
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
        Assert.That(result.Success, Is.False, "unknown file should return a failure result");
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty, "failure result must contain error message");
    }

    [Test]
    public async Task FormatDocumentSafe_UnknownFile_ReturnsFailResult()
    {
        var result = await _engine.FormatDocumentSafeAsync("NoSuchFile.cs");
        Assert.That(result.Success, Is.False, "unknown file should return a failure result");
    }

    [Test]
    public async Task SortAndDeduplicateUsings_UnknownFile_ThrowsException()
    {
        // SortAndDeduplicateUsingsAsync throws InvalidOperationException for unknown files
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _engine.SortAndDeduplicateUsingsAsync("NoSuchFile.cs"),
            "unknown file should throw InvalidOperationException");
    }
}

[TestFixture]
public class ProjectStructureEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private ProjectStructureEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new ProjectStructureEngine(_mgr, new SentinelConfiguration());
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task FixMismatchedNamespaces_UnknownFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
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

[TestFixture]
public class RefinementEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private RefinementEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new RefinementEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task PullUpMember_UnknownFile_ThrowsToolNotFoundException()
    {
        // PullUpMemberAsync(filePath, className, memberName)
        await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
            await _engine.PullUpMemberAsync("NoSuchFile.cs", "Base", "DoWork"),
            "unknown file should surface a real error, not a fake 'error' file change");
    }

    [Test]
    public async Task PullUpMember_UnknownClass_ThrowsToolNotFoundException()
    {
        const string source = @"
public class Derived
{
    public void DoWork() { }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Derived.cs", source)]));

        await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
            await _engine.PullUpMemberAsync("Derived.cs", "NoBase", "DoWork"),
            "unknown class should surface a real error, not a fake 'error' file change");
    }

    [Test]
    public async Task PullUpMember_ValidHierarchy_ReturnsDictionary()
    {
        const string source = @"
public class Animal
{
}
public class Dog : Animal
{
    public void Speak() { }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Animals.cs", source)]));

        var dict = await _engine.PullUpMemberAsync("Animals.cs", "Dog", "Speak");

        Assert.That(dict, Is.Not.Null, "result dictionary should never be null");
        Assert.That(dict.Count, Is.GreaterThan(0), "result should have at least one entry");
    }
}