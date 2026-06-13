// Battery #10 — ValidationEngine / MetricsEngine / DiagnosticEngine / SecurityEngine
// Adds dedicated XxxEngineTests fixture classes for 4 more engine classes.
// All tests run in-memory via AdhocWorkspace (no MSBuild/project-file loading).

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ════════════════════════════════════════════════════════════════════════════════
// A. ValidationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ValidationEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private DiffEngine _diffEngine = null!;
    private ValidationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _diffEngine = new DiffEngine(_workspaceManager);
        _engine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, _diffEngine);

        var solution = TestSolutionBuilder.CreateSolutionWithProject("Source",
            [("Greeter.cs", "public class Greeter { public string Greet() => \"Hello\"; }")]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task ValidateChanges_ValidNewContent_SucceedsWithNoErrors()
    {
        var result = await _engine.ValidateChangesAsync(new Dictionary<FilePath, string>
        {
            [new FilePath("Greeter.cs")] = "public class Greeter { public string Greet() => \"Hi!\"; }"
        });

        Assert.That(result.Success, Is.True, "Syntactically valid replacement should pass");
        Assert.That(result.Diagnostics, Is.Empty, "No compiler errors expected for valid code");
    }

    [Test]
    public async Task ValidateChanges_FileNotFound_ReturnsErrorReport()
    {
        var result = await _engine.ValidateChangesAsync(new Dictionary<FilePath, string>
        {
            [new FilePath("DoesNotExist.cs")] = "public class X {}"
        });

        Assert.That(result.Success, Is.False, "Missing file should not be valid");
        Assert.That(result.Diagnostics.Any(d => d.Id == "RS001"), Is.True,
            "Should include RS001 file-not-found diagnostic");
    }

    [Test]
    public async Task ValidateChanges_BreakingContent_ReturnsFalseWithCompileError()
    {
        // CS0029: cannot implicitly convert type 'string' to 'int'
        var result = await _engine.ValidateChangesAsync(new Dictionary<FilePath, string>
        {
            [new FilePath("Greeter.cs")] = "public class Greeter { void M() { int x = \"not a number\"; } }"
        });

        Assert.That(result, Is.Not.Null, "Should always return a report, never throw");
        Assert.That(result.Success, Is.False, "Type-mismatch compile error should fail validation");
        Assert.That(result.Diagnostics, Is.Not.Empty, "At least one error diagnostic expected");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. MetricsEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class MetricsEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private MetricsEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MetricsEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task GetSolutionMetrics_SingleProjectWithClassAndTwoMethods_ReturnsCorrectCounts()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Core",
            [("Service.cs", "public class Service { public void Run() {} public void Stop() {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var metrics = await _engine.GetSolutionMetricsAsync();

        Assert.That(metrics.ProjectCount, Is.EqualTo(1), "One project");
        Assert.That(metrics.TotalFiles, Is.EqualTo(1), "One document");
        Assert.That(metrics.TotalTypes, Is.EqualTo(1), "One class declaration");
        Assert.That(metrics.TotalMethods, Is.EqualTo(2), "Two methods: Run + Stop");
    }

    [Test]
    public async Task AnalyzeTypeCohesion_UnknownFile_ReturnsEmptyList()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Core", [("Foo.cs", "public class Foo {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeTypeCohesionAsync("DoesNotExist.cs");

        Assert.That(result, Is.Empty, "Unknown file path should return empty cohesion list");
    }

    [Test]
    public async Task GetSolutionMetrics_FilterByProjectName_MatchesCorrectProject()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Core",
            [("X.cs", "public class X {}"), ("Y.cs", "public class Y {}")]);
        _workspaceManager.SetTestSolution(solution);

        var metrics = await _engine.GetSolutionMetricsAsync(projectName: "Core");

        Assert.That(metrics.Projects, Has.Count.EqualTo(1), "Filter must match exactly one project");
        Assert.That(metrics.TotalTypes, Is.EqualTo(2), "Both X and Y should count");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. DiagnosticEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class DiagnosticEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private DiagnosticEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DiagnosticEngine(_workspaceManager);

        var solution = TestSolutionBuilder.CreateSolutionWithProject("Source",
            [("Clean.cs", "public class Clean { public int Add(int a, int b) => a + b; }")]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task GetFileDiagnostics_CleanFile_ReturnsZeroErrors()
    {
        var summary = await _engine.GetFileDiagnosticsAsync("Clean.cs");

        Assert.That(summary.Data.Errors, Is.EqualTo(0), "Clean file should report no errors");
    }

    [Test]
    public void GetFileDiagnostics_UnknownFile_ThrowsException()
    {
        Assert.ThrowsAsync<Exception>(() => _engine.GetFileDiagnosticsAsync("DoesNotExist.cs"));
    }

    [Test]
    public async Task GetProjectDiagnostics_KnownProject_ReturnsSummaryWithNonNegativeCounts()
    {
        var summary = await _engine.GetProjectDiagnosticsAsync("Source");

        Assert.That(summary, Is.Not.Null, "Known project should return a summary");
        Assert.That(summary.Data.Errors, Is.GreaterThanOrEqualTo(0));
        Assert.That(summary.Data.Warnings, Is.GreaterThanOrEqualTo(0));
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// D. SecurityEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SecurityEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Sec.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("SecProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task AnalyzeSecurity_HardcodedPassword_DetectsHardcodedSecret()
    {
        SetSource(@"
public class Config
{
    public static string GetConnection()
    {
        string password = ""superSecret123"";
        return password;
    }
}");
        var issues = await _engine.AnalyzeSecurityAsync("Sec.cs");

        Assert.That(issues, Is.Not.Empty, "Hardcoded password variable should be flagged");
        Assert.That(issues.Any(i => i.IssueType == "HardcodedSecret"), Is.True,
            "IssueType must be HardcodedSecret");
    }

    [Test]
    public async Task AnalyzeSecurity_CleanCode_ReturnsNoIssues()
    {
        SetSource("public class Safe { public int Add(int a, int b) => a + b; }");

        var issues = await _engine.AnalyzeSecurityAsync("Sec.cs");

        Assert.That(issues, Is.Empty, "Clean code should yield no security issues");
    }

    [Test]
    public async Task CheckForSqlInjection_DynamicStringConcat_DetectsInjectionRisk()
    {
        SetSource(@"
public class Repo
{
    public void LoadData(string tableName)
    {
        ExecuteQuery(""SELECT * FROM "" + tableName);
    }
}");
        var issues = await _engine.CheckForSqlInjectionAsync("Sec.cs");

        Assert.That(issues, Is.Not.Empty, "Dynamic concat in SQL call must be flagged");
        Assert.That(issues.Any(i => i.IssueType == "PossibleSqlInjection"), Is.True,
            "IssueType must be PossibleSqlInjection");
    }
}
