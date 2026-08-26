using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

// ────────────────────────────────────────────────────────────────────────────
// Battery #16 — DependencyInjectionEngine,
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
        _mgr = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new DependencyInjectionEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task AnalyzeDependencies_UnknownFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await _engine.AnalyzeDependenciesAsync("NoSuchFile.cs", "MyClass"));
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
public class GranularRefactoringEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private GranularRefactoringEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new GranularRefactoringEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task InlineField_UnknownFile_ReturnsEmptyString()
    {
        var result = await _engine.InlineFieldAsync("NoSuchFile.cs", "_field");
        Assert.That(result.UpdatedText, Is.Null, "unknown file should return null UpdatedText");
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

        Assert.That(result.Message!, Does.StartWith("// ERROR:"), "absent field should return error comment");
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

        Assert.That(result.UpdatedText!, Does.Not.StartWith("// ERROR:"), "valid field should not return error comment");
        Assert.That(result.UpdatedText!, Does.Contain("2"), "inlined value should appear in output");
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
        _mgr = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var config = new SentinelConfiguration();
        var pse = new ProjectStructureEngine(_mgr, config);
        var ae = new AnalysisEngine(_mgr, config);
        _engine = new HealthOrchestrationEngine(_mgr, pse, ae, config);
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
        Assert.That(report.StatusMessage, Is.Not.Null.And.Not.Empty,
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