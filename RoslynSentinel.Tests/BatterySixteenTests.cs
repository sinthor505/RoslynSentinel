using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ────────────────────────────────────────────────────────────────────────────
// Battery #16 — DependencyInjectionEngine, ExhaustiveAnalyzerEngine,
//               GranularRefactoringEngine, HealthOrchestrationEngine
// ────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class DependencyInjectionEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private DependencyInjectionEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DependencyInjectionEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public void AnalyzeDependencies_UnknownFile_ThrowsException()
    {
        Assert.ThrowsAsync<Exception>(
            async () => await _engine.AnalyzeDependenciesAsync("NoSuchFile.cs", "MyClass"),
            "missing file should throw Exception");
    }

    [Test]
    public async Task FindDiRegistrations_UnknownFile_ReturnsEmptyList()
    {
        var registrations = await _engine.FindDiRegistrationsAsync("NoSuchFile.cs");
        Assert.That(registrations, Is.Empty, "unknown file should yield empty registrations list");
    }

    [Test]
    public async Task FindDiRegistrations_WithBuilderCalls_ReturnsRegistrations()
    {
        const string source = @"
using Microsoft.Extensions.DependencyInjection;
public static class ServiceExtensions
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMyService, MyServiceImpl>();
        services.AddScoped<IOrderService, OrderService>();
    }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("ServiceExtensions.cs", source)]));

        var registrations = await _engine.FindDiRegistrationsAsync(filePath: "ServiceExtensions.cs");

        Assert.That(registrations, Is.Not.Empty, "file with AddSingleton/AddScoped calls should yield registrations");
    }
}

[TestFixture]
public class ExhaustiveAnalyzerEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private ExhaustiveAnalyzerEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ExhaustiveAnalyzerEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task RunDiagnosticRule_UnknownFile_ReturnsEmptyList()
    {
        var issues = await _engine.RunDiagnosticRuleAsync("NoSuchFile.cs", "CS0001");
        Assert.That(issues, Is.Empty, "unknown file should yield no issues");
    }

    [Test]
    public async Task RunDiagnosticRule_CleanFile_NonExistentRule_ReturnsEmpty()
    {
        // Real implementation: a clean file has no "CS9999" diagnostics
        const string source = @"
public class Foo
{
    public void Bar() { }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Foo.cs", source)]));

        var issues = await _engine.RunDiagnosticRuleAsync("Foo.cs", "CS9999");

        Assert.That(issues, Is.Empty, "clean file has no CS9999 diagnostics");
    }

    [Test]
    public async Task RunDiagnosticRule_FileWithTypeError_CS0029_IsDetected()
    {
        // CS0029: Cannot implicitly convert type 'string' to 'int'
        const string source = @"
public class Broken
{
    public void M() { int x = ""bad""; }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Broken.cs", source)]));

        var issues = await _engine.RunDiagnosticRuleAsync("Broken.cs", "CS0029");

        Assert.That(issues, Is.Not.Empty, "type mismatch should produce CS0029");
        Assert.That(issues.All(i => i.RuleId == "CS0029"), Is.True);
    }

    [Test]
    public async Task RunDiagnosticRule_AllDiagnostics_EmptyRuleId()
    {
        // Passing empty ruleId should return ALL diagnostics on the file
        const string source = @"
public class Broken
{
    public void M() { int x = ""bad""; }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Broken.cs", source)]));

        var issues = await _engine.RunDiagnosticRuleAsync("Broken.cs", "");

        Assert.That(issues, Is.Not.Empty, "broken file should have at least one diagnostic when rule filter is empty");
        Assert.That(issues.All(i => i.RuleId != null), Is.True, "every issue must have a non-null RuleId");
    }
}

[TestFixture]
public class GranularRefactoringEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private GranularRefactoringEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new GranularRefactoringEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task InlineField_UnknownFile_ReturnsEmptyString()
    {
        // InlineFieldAsync returns "" for an unknown file (not an error comment)
        var result = await _engine.InlineFieldAsync("NoSuchFile.cs", "_field");
        Assert.That(result, Is.EqualTo(""), "unknown file should return empty string");
    }

    [Test]
    public async Task InlineField_FieldNotFound_ReturnsErrorComment()
    {
        const string source = @"
public class MyClass
{
    private int _count = 0;
    public int GetCount() => _count;
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("MyClass.cs", source)]));

        var result = await _engine.InlineFieldAsync("MyClass.cs", "_nonexistent");

        Assert.That(result, Does.StartWith("// ERROR:"), "absent field should return error comment");
    }

    [Test]
    public async Task InlineField_SingleUsePrivateField_InlinesValue()
    {
        const string source = @"
public class Calc
{
    private int _factor = 2;
    public int Double(int x) => x * _factor;
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Calc.cs", source)]));

        var result = await _engine.InlineFieldAsync("Calc.cs", "_factor");

        Assert.That(result, Does.Not.StartWith("// ERROR:"), "valid field should not return error comment");
        Assert.That(result, Does.Contain("2"), "inlined value should appear in output");
    }
}

[TestFixture]
public class HealthOrchestrationEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private HealthOrchestrationEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        var config = new SentinelConfiguration();
        var pse = new ProjectStructureEngine(_mgr, config);
        var ae = new AnalysisEngine(_mgr, config);
        var ase = new AsyncSafetyEngine(_mgr);
        _engine = new HealthOrchestrationEngine(_mgr, pse, ae, ase, config);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task GenerateHealthReport_MinimalSolution_ReturnsReport()
    {
        var report = await _engine.GenerateComprehensiveHealthReportAsync();
        Assert.That(report, Is.Not.Null, "health check should return a non-null report");
    }

    [Test]
    public async Task GenerateHealthReport_ReportHasStatusMessage()
    {
        var report = await _engine.GenerateComprehensiveHealthReportAsync();
        Assert.That(report.StatusMessage, Is.Not.Null.Or.Empty,
            "report should contain a non-empty StatusMessage");
    }

    [Test]
    public async Task GenerateHealthReport_TotalIssues_IsNonNegative()
    {
        var report = await _engine.GenerateComprehensiveHealthReportAsync();
        Assert.That(report.TotalIssues, Is.GreaterThanOrEqualTo(0),
            "issue count should be non-negative");
    }
}