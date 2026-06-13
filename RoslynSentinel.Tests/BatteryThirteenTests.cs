// Battery #13 — AnalysisEngine / CodeGenerationEngine / ControlFlowEngine / SymbolNavigationEngine
// CodeGenerationEngine is sync/JSON-only (no workspace needed for its primary methods).

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ════════════════════════════════════════════════════════════════════════════════
// A. AnalysisEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AnalysisEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AnalysisEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AnalysisEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindLargeTypes_SmallClass_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Tiny { }")]);
        _workspaceManager.SetTestSolution(solution);

        // A single-line class has 0 lines span — cannot exceed default 500 limit
        var reports = await _engine.FindLargeTypesAsync(maxLines: 500);

        Assert.That(reports, Is.Empty, "A tiny class should not exceed the line limit");
    }

    [Test]
    public async Task FindLargeTypes_ClassExceedingLimit_ReportsIt()
    {
        // Build a class body that spans more than 3 lines
        var source = "public class BigService {\n    private int _a;\n    private int _b;\n    private int _c;\n    private int _d;\n}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindLargeTypesAsync(maxLines: 3);

        Assert.That(reports, Is.Not.Empty, "BigService spans more than 3 lines and should be reported");
        Assert.That(reports.Any(r => r.TypeName == "BigService"), Is.True);
    }

    [Test]
    public async Task FindLargeMethods_MethodExceedingLimit_ReportsIt()
    {
        var source = @"
public class Calc {
    public int Add(int a, int b) {
        var x = a;
        var y = b;
        var z = x + y;
        return z;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.FindLargeMethodsAsync(maxLines: 2);

        Assert.That(reports, Is.Not.Empty, "Add() has more than 2 lines and should be reported");
        Assert.That(reports.Any(r => r.MethodName == "Add"), Is.True);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. CodeGenerationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class CodeGenerationEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private CodeGenerationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new CodeGenerationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public void GenerateClassesFromJson_StringProperty_GeneratesStringProp()
    {
        var json = """{"name": "Alice"}""";

        var result = _engine.GenerateClassesFromJson(json, "Person", "MyApp");

        Assert.That(result.filePath.Absolute, Is.EqualTo("Person.cs"));
        Assert.That(result.Content, Does.Contain("class Person"));
        Assert.That(result.Content, Does.Contain("string"));
        Assert.That(result.Content, Does.Contain("Name"));
    }

    [Test]
    public void GenerateClassesFromJson_NumberProperty_GeneratesDoubleProp()
    {
        // The engine maps JsonValueKind.Number → "double" for all numeric JSON values
        var json = """{"age": 30}""";

        var result = _engine.GenerateClassesFromJson(json, "PersonDto", "MyApp.Dtos");

        Assert.That(result.Content, Does.Contain("double"), "numeric JSON value should map to double property");
        Assert.That(result.Content, Does.Contain("Age"));
        Assert.That(result.Content, Does.Contain("namespace MyApp.Dtos"));
    }

    [Test]
    public void GenerateClassesFromJson_BoolProperty_GeneratesBoolProp()
    {
        var json = """{"isActive": true}""";

        var result = _engine.GenerateClassesFromJson(json, "Flag", "MyApp");

        Assert.That(result.Content, Does.Contain("bool"), "boolean JSON value should map to bool property");
        Assert.That(result.Content, Does.Contain("IsActive"));
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. ControlFlowEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ControlFlowEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ControlFlowEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ControlFlowEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task AnalyzePathCoverage_MethodWithIfElse_ReturnsBothBranchPaths()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Validator.cs", @"
public class Validator {
    public bool Validate(int x) {
        if (x > 0) { return true; }
        else { return false; }
    }
}")]);
        _workspaceManager.SetTestSolution(solution);

        var report = await _engine.AnalyzePathCoverageAsync("Validator.cs", "Validate");

        Assert.That(report.BranchesToTest, Has.Count.GreaterThanOrEqualTo(2),
            "if/else should produce at least 2 branch paths (True Path + False Path)");
        Assert.That(report.BranchesToTest.Any(b => b.Contains("True Path")), Is.True);
        Assert.That(report.BranchesToTest.Any(b => b.Contains("False Path")), Is.True);
    }

    [Test]
    public async Task AnalyzePathCoverage_MethodWithNoConditionals_ReturnsEmptyBranches()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Simple.cs", @"
public class Simple {
    public int Add(int a, int b) { return a + b; }
}")]);
        _workspaceManager.SetTestSolution(solution);

        var report = await _engine.AnalyzePathCoverageAsync("Simple.cs", "Add");

        Assert.That(report.BranchesToTest, Is.Empty, "Method with no conditionals has no branches to cover");
    }

    [Test]
    public async Task AnalyzeMethodControlFlow_UnknownFile_ReturnsErrorResult()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Simple.cs", "public class Simple { }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeMethodControlFlowAsync("DoesNotExist.cs", "SomeMethod");

        Assert.That(result.Error, Is.Not.Null.And.Not.Empty, "Unknown file should produce an error result");
        Assert.That(result.Error, Does.Contain("not found"), "Error should mention file not found");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// D. SymbolNavigationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SymbolNavigationEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SymbolNavigationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindReadonlyFieldCandidates_NonReadonlyPrivateField_ReportsIt()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { private int _count = 0; }")]);
        _workspaceManager.SetTestSolution(solution);

        var candidates = await _engine.FindReadonlyFieldCandidatesAsync("Service.cs");

        Assert.That(candidates.Any(c => c.FieldName == "_count"), Is.True,
            "Non-readonly private field should be reported as readonly candidate");
    }

    [Test]
    public async Task FindReadonlyFieldCandidates_ReadonlyField_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { private readonly int _count = 0; }")]);
        _workspaceManager.SetTestSolution(solution);

        var candidates = await _engine.FindReadonlyFieldCandidatesAsync("Service.cs");

        Assert.That(candidates.Any(c => c.FieldName == "_count"), Is.False,
            "Already-readonly field should NOT be reported");
    }

    [Test]
    public async Task FindReadonlyFieldCandidates_UnknownFile_ReturnsEmptyList()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { }")]);
        _workspaceManager.SetTestSolution(solution);

        var candidates = await _engine.FindReadonlyFieldCandidatesAsync("DoesNotExist.cs");

        Assert.That(candidates, Is.Empty, "Unknown file should return empty list, not throw");
    }
}
