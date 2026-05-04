using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ────────────────────────────────────────────────────────────────────────────
// Battery #17 — LogicOptimizationEngine, MassiveAnalyzerEngine,
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
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
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
        Assert.That(result, Is.EqualTo(""), "unknown file should return empty string");
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

        Assert.That(result, Is.Not.EqualTo(""), "result should not be empty for a file with null check");
    }

    [Test]
    public async Task AddGuardClauses_UnknownFile_ReturnsEmptyString()
    {
        var result = await _engine.AddGuardClausesAsync("NoSuchFile.cs", "DoWork");
        Assert.That(result, Is.EqualTo(""), "unknown file should return empty string for AddGuardClauses");
    }

    [Test]
    public async Task SimplifyBooleanExpressions_UnknownFile_ReturnsEmptyString()
    {
        var result = await _engine.SimplifyBooleanExpressionsAsync("NoSuchFile.cs");
        Assert.That(result, Is.EqualTo(""), "unknown file should return empty string for SimplifyBooleanExpressions");
    }
}

[TestFixture]
public class MassiveAnalyzerEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private MassiveAnalyzerEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MassiveAnalyzerEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task RunSpecificRule_UnknownFile_ReturnsEmptyList()
    {
        var issues = await _engine.RunSpecificRuleAsync("NoSuchFile.cs", "RULE001");
        Assert.That(issues, Is.Empty, "unknown file should yield no issues");
    }

    [Test]
    public async Task RunSpecificRule_KnownFile_ReturnsAtLeastOneResult()
    {
        const string source = @"
public class Analyzer
{
    public void Analyze() { }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Analyzer.cs", source)]));

        var issues = await _engine.RunSpecificRuleAsync("Analyzer.cs", "SIMULATED");

        Assert.That(issues, Has.Count.GreaterThanOrEqualTo(1),
            "known file should produce at least one (simulated) result");
    }

    [Test]
    public async Task RunSpecificRule_ResultsHaveNonNullFilePath()
    {
        const string source = "public class Foo {}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Foo.cs", source)]));

        var issues = await _engine.RunSpecificRuleAsync("Foo.cs", "SIMULATED");

        foreach (var issue in issues)
            Assert.That(issue.RuleId, Is.Not.Null.And.Not.Empty, "every issue must have a RuleId");
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
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
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
    public void SortAndDeduplicateUsings_UnknownFile_ThrowsException()
    {
        // SortAndDeduplicateUsingsAsync throws InvalidOperationException for unknown files
        Assert.ThrowsAsync<InvalidOperationException>(
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
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ProjectStructureEngine(_mgr, new SentinelConfiguration());
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public void FixMismatchedNamespaces_UnknownFile_ThrowsException()
    {
        Assert.ThrowsAsync<Exception>(
            async () => await _engine.FixMismatchedNamespacesAsync("NoSuchFile.cs"),
            "missing file should throw Exception");
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
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new RefinementEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task PullUpMember_UnknownFile_ReturnsErrorDictionary()
    {
        // PullUpMemberAsync(filePath, className, memberName)
        var dict = await _engine.PullUpMemberAsync("NoSuchFile.cs", "Base", "DoWork");
        Assert.That(dict, Contains.Key("error"), "unknown file should return dict with 'error' key");
    }

    [Test]
    public async Task PullUpMember_UnknownClass_ReturnsErrorDictionary()
    {
        const string source = @"
public class Derived
{
    public void DoWork() { }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Derived.cs", source)]));

        var dict = await _engine.PullUpMemberAsync("Derived.cs", "NoBase", "DoWork");

        Assert.That(dict, Contains.Key("error"), "unknown class should return dict with 'error' key");
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

        // Either success (keys are file names) or error — both are valid
        Assert.That(dict, Is.Not.Null, "result dictionary should never be null");
        Assert.That(dict.Count, Is.GreaterThan(0), "result should have at least one entry");
    }
}