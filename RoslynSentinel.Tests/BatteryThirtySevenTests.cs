using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for accuracy fixes shipped in this sprint:
///  1. AntiPatternFinding.Pattern serializes as "patternType" in JSON (MCP wire format).
///  2. DetectAntiPatternsAsync — solution-wide scan excludes .Tests / .Benchmarks projects.
///  3. FindMissingCancellationTokensAsync — solution-wide scan excludes .Tests / .Benchmarks projects.
///  4. TIME_ABSTRACTION — skips static classes and *Helper/*Extensions/*Base classes.
///  5. NAME_MISMATCH / MULTI_TYPE — skips Roslyn source-generator output (.g.cs files).
/// </summary>
[TestFixture]
public class BatteryThirtySevenTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AntiPatternEngine _antiPatternEngine;
    private ProjectStructureEngine _structureEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _antiPatternEngine = new AntiPatternEngine(_workspaceManager);
        _structureEngine = new ProjectStructureEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetProject(string projectName, string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(projectName, [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 1 — AntiPatternFinding JSON serialization
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void AntiPatternFinding_Pattern_SerializesAsPatternType()
    {
        var finding = new AntiPatternFinding("BlockingTaskWait", "Uses .Result", "High", "Foo.cs", 10, ".Result");
        var json = System.Text.Json.JsonSerializer.Serialize(finding);

        Assert.That(json, Does.Contain("\"patternType\""),
            "JSON must use 'patternType' key so MCP clients can filter by pattern");
        Assert.That(json, Does.Not.Contain("\"pattern\":"),
            "'pattern' (lowercase) must not appear — it was the broken wire name");
    }

    [Test]
    public void AntiPatternFinding_PatternValue_RoundTripsCorrectly()
    {
        var original = new AntiPatternFinding("AsyncVoidMethod", "desc", "Medium", "Bar.cs", 5, "async void M()");
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AntiPatternFinding>(json);

        Assert.That(deserialized?.Pattern, Is.EqualTo("AsyncVoidMethod"),
            "Pattern value must survive a JSON round-trip");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2 — DetectAntiPatternsAsync excludes test projects
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DetectAntiPatterns_SolutionWide_ExcludesTestsProject()
    {
        // An async void method in a .Tests project should NOT be reported
        SetProject("MyApp.Tests", @"
using System.Threading.Tasks;
class FakeTest {
    async void BadAsyncVoid() { await Task.CompletedTask; }
}");

        var results = await _antiPatternEngine.DetectAntiPatternsAsync();
        Assert.That(results, Is.Empty,
            "Solution-wide scan must exclude .Tests projects to avoid test-code false positives");
    }

    [Test]
    public async Task DetectAntiPatterns_SolutionWide_ExcludesBenchmarksProject()
    {
        SetProject("MyApp.Benchmarks", @"
using System.Threading.Tasks;
class Bench {
    async void BadAsyncVoid() { await Task.CompletedTask; }
}");

        var results = await _antiPatternEngine.DetectAntiPatternsAsync();
        Assert.That(results, Is.Empty,
            "Solution-wide scan must exclude .Benchmarks projects");
    }

    [Test]
    public async Task DetectAntiPatterns_SolutionWide_StillFindsProductionCode()
    {
        // An async void method in a production project MUST still be reported
        SetProject("MyApp.Service", @"
using System.Threading.Tasks;
class EventHandler {
    async void OnClick() { await Task.CompletedTask; }
}");

        var results = await _antiPatternEngine.DetectAntiPatternsAsync(
            patternFilter: ["AsyncVoidMethod"]);
        Assert.That(results, Is.Not.Empty,
            "Production-project findings must still be reported");
        Assert.That(results.Any(f => f.Pattern == "AsyncVoidMethod"), Is.True);
    }

    [Test]
    public async Task DetectAntiPatterns_ExplicitProjectName_StillWorksForTestProject()
    {
        // When explicitly targeting a .Tests project, the exclusion must NOT apply
        SetProject("MyApp.Tests", @"
using System.Threading.Tasks;
class Fixture {
    async void BadSetup() { await Task.CompletedTask; }
}");

        var results = await _antiPatternEngine.DetectAntiPatternsAsync(
            projectName: "MyApp.Tests",
            patternFilter: ["AsyncVoidMethod"]);
        Assert.That(results, Is.Not.Empty,
            "Explicit projectName scope must bypass the test-project exclusion");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3 — FindMissingCancellationTokensAsync excludes test projects
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FindMissingCancellationTokens_SolutionWide_ExcludesTestsProject()
    {
        SetProject("MyApp.Tests", @"
using System.Threading.Tasks;
class Tests {
    public async Task Setup() { await Task.Delay(10); }
}");

        var results = await _antiPatternEngine.FindMissingCancellationTokensAsync();
        Assert.That(results, Is.Empty,
            "Missing-CT scan must exclude .Tests projects — xUnit test methods generate 85%+ false positives");
    }

    [Test]
    public async Task FindMissingCancellationTokens_SolutionWide_StillFindsProductionCode()
    {
        SetProject("MyApp.Service", @"
using System.Threading.Tasks;
class MyService {
    public async Task DoWork() { await SaveAsync(default); }
    private Task SaveAsync(System.Threading.CancellationToken ct) => Task.CompletedTask;
}");

        var results = await _antiPatternEngine.FindMissingCancellationTokensAsync();
        Assert.That(results, Is.Not.Empty,
            "Production-service missing-CT findings must still be reported");
        Assert.That(results.Any(f => f.MethodName == "DoWork"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4 — TIME_ABSTRACTION skips static classes and utility classes
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task TimeAbstraction_StaticClass_NotFlagged()
    {
        SetProject("MyApp.Service", @"
using System;
public static class DateHelper {
    public static string GetTimestamp() => DateTime.UtcNow.ToString(""o"");
}");

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.TimeAbstraction);
        Assert.That(results, Is.Empty,
            "Static helper classes must not be flagged for TIME_ABSTRACTION — injecting TimeProvider is not applicable");
    }

    [Test]
    public async Task TimeAbstraction_HelperSuffixClass_NotFlagged()
    {
        SetProject("MyApp.Service", @"
using System;
public class SqlHelper {
    public string GetTimestamp() => DateTime.UtcNow.ToString(""o"");
}");

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.TimeAbstraction);
        Assert.That(results, Is.Empty,
            "Classes ending in 'Helper' must not be flagged — they are utilities, not DI-injectable services");
    }

    [Test]
    public async Task TimeAbstraction_ExtensionsClass_NotFlagged()
    {
        SetProject("MyApp.Service", @"
using System;
public static class DateTimeExtensions {
    public static bool IsExpired(this DateTime dt) => dt < DateTime.UtcNow;
}");

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.TimeAbstraction);
        Assert.That(results, Is.Empty,
            "Extension method classes must not be flagged for TIME_ABSTRACTION");
    }

    [Test]
    public async Task TimeAbstraction_RepositoryClass_NotFlagged()
    {
        SetProject("MyApp.Service", @"
using System;
public class NotificationRepository {
    public void Insert(object item) {
        var ts = DateTime.UtcNow; // audit column — not a testability issue
    }
}");

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.TimeAbstraction);
        Assert.That(results, Is.Empty,
            "Repository classes use DateTime.UtcNow for SQL audit columns — TimeProvider injection is not applicable");
    }

    [Test]
    public async Task TimeAbstraction_WorkerClass_NotFlagged()
    {
        SetProject("MyApp.Service", @"
using System;
public class LowStockMonitorWorker {
    public void Run() {
        var now = DateTime.UtcNow; // scheduling reference — not a testability concern
    }
}");

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.TimeAbstraction);
        Assert.That(results, Is.Empty,
            "Worker/background-service classes must not be flagged for TIME_ABSTRACTION");
    }

    [Test]
    public async Task TimeAbstraction_ExporterClass_NotFlagged()
    {
        SetProject("MyApp.Service", @"
using System;
public class MealPlanPdfExporter {
    public string Generate() => $""Generated at {DateTime.UtcNow:o}"";
}");

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.TimeAbstraction);
        Assert.That(results, Is.Empty,
            "Exporter/document-generation classes must not be flagged for TIME_ABSTRACTION");
    }

    [Test]
    public async Task TimeAbstraction_ProcessorClass_NotFlagged()
    {
        SetProject("MyApp.Service", @"
using System;
public class BatchProductProcessor {
    public void Process() {
        var batchTs = DateTime.UtcNow;
    }
}");

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.TimeAbstraction);
        Assert.That(results, Is.Empty,
            "Processor/batch classes must not be flagged for TIME_ABSTRACTION");
    }

    [Test]
    public async Task TimeAbstraction_ServiceClass_IsFlagged()
    {
        SetProject("MyApp.Service", @"
using System;
public class OrderService {
    public void ProcessOrder() {
        var ts = DateTime.UtcNow;
    }
}");

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.TimeAbstraction);
        Assert.That(results, Is.Not.Empty,
            "Non-static service classes using DateTime.UtcNow must still be flagged");
        Assert.That(results.Any(r => r.Contains("TIME_ABSTRACTION")), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5 — NAME_MISMATCH and MULTI_TYPE skip .g.cs files
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NameMismatch_GeneratedFile_Suppressed()
    {
        // "Foo.g.cs" containing class "ServiceMetadata" — a classic Roslyn source-gen pattern
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "MyApp.Service",
            [("Foo.g.cs", "public static class ServiceMetadata { public const string Name = \"svc\"; }")]);
        _workspaceManager.SetTestSolution(solution);

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.NameMismatch);
        Assert.That(results, Is.Empty,
            "NAME_MISMATCH must be suppressed for .g.cs source-generator output files");
    }

    [Test]
    public async Task MultiType_GeneratedFile_Suppressed()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "MyApp.Service",
            [("Generated.g.cs", "public class Alpha { } public class Beta { } public class Gamma { }")]);
        _workspaceManager.SetTestSolution(solution);

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.MultiType);
        Assert.That(results, Is.Empty,
            "MULTI_TYPE must be suppressed for .g.cs files — generators routinely emit multiple types per file");
    }

    [Test]
    public async Task NameMismatch_NonGeneratedFile_StillReported()
    {
        // A hand-written mismatch must still fire
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "MyApp.Service",
            [("Foo.cs", "public class Bar { }")]);
        _workspaceManager.SetTestSolution(solution);

        var results = await _structureEngine.FindStructuralSmellsAsync(
            typeFilter: ProjectStructureEngine.StructuralSmellType.NameMismatch);
        Assert.That(results.Any(r => r.Contains("NAME_MISMATCH")), Is.True,
            "NAME_MISMATCH must still fire for hand-written files where type ≠ filename");
    }
}
