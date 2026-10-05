using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// applyProjects restricts what is applied and reported, never what is planned: ubiquity shares come from the full
/// testScope, so a project's plan is the same with or without it, whereas testScope itself changes the statistics.
/// Fixture: type Hot is touched by 4/5 tests in FixturesP (80%) but only 4/35 (11%) across both test projects.
/// </summary>
[TestFixture]
public class TestCategoryApplyProjectsTests
{
    private const string Marker = "// sentinel:auto-category";

    private const string TargetsSource = @"
namespace Targets.Engines
{
    public class Hot { public void Run() { } }
    public class Cold { public void Run() { } }
    public class Q1 { public void Run() { } }
    public class Q2 { public void Run() { } }
    public class Q3 { public void Run() { } }
    public class Q4 { public void Run() { } }
    public class Q5 { public void Run() { } }
}";

    private const string PSource = @"using NUnit.Framework;
using Targets.Engines;
namespace FixturesP
{
    [TestFixture]
    public class DrawerP
    {
        [Test]
        public void T1() { new Hot().Run(); }

        [Test]
        public void T2() { new Hot().Run(); }

        [Test]
        public void T3() { new Hot().Run(); }

        [Test]
        public void T4() { new Hot().Run(); }

        [Test]
        public void T5() { new Cold().Run(); }
    }
}
";

    private const string PPath = @"C:\fake\FixturesP\Tests.cs";
    private const string QPath = @"C:\fake\FixturesQ\Tests.cs";

    // 30 tests spread over Q1..Q5 (6 each = 17% of all 35 tests, below the 25% default). The first test also carries a
    // stale marked attribute so a stale removal would be planned for FixturesQ.
    private static readonly string QSource = BuildQSource();

    private static readonly string[] AllReferencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator);

    private static readonly Lazy<MetadataReference[]> FullReferences = new(
        () => AllReferencePaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray());

    private static readonly Lazy<MetadataReference[]> NoNUnitReferences = new(
        () => AllReferencePaths
            .Where(p => !Path.GetFileName(p).StartsWith("nunit.", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private FakeWorkspaceManager _workspaceManager;
    private TestCategoryTaggingTools _tool;

    private static string BuildQSource()
    {
        var sb = new StringBuilder();
        sb.Append("using NUnit.Framework;\nusing Targets.Engines;\nnamespace FixturesQ\n{\n    [TestFixture]\n    public class DrawerQ\n    {\n");
        for (var i = 0; i < 30; i++)
        {
            sb.Append("        [Test]\n");
            if (i == 0)
            {
                sb.Append("        [Category(\"Gone\")] " + Marker + "\n");
            }

            sb.Append($"        public void T{i}() {{ new Q{(i % 5) + 1}().Run(); }}\n\n");
        }

        sb.Append("    }\n}\n");
        return sb.ToString();
    }

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        _workspaceManager.SetTestSolution(BuildSolution());
        var planner = new TestCategoryTaggingEngine(_workspaceManager);
        var applier = new TestCategoryApplyEngine(_workspaceManager, new ValidationEngine(_workspaceManager));
        _tool = new TestCategoryTaggingTools(new TestCategoryTaggingImpl(planner, applier, _workspaceManager, NullLogger.Instance));
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static Solution BuildSolution()
    {
        var workspace = new AdhocWorkspace();
        var targets = workspace.CurrentSolution
            .AddProject("Targets", "Targets", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(NoNUnitReferences.Value)
            .AddDocument("Targets.cs", SourceText.From(TargetsSource), filePath: @"C:\fake\Targets\Targets.cs")
            .Project;

        var solution = AddTestProject(targets.Solution, targets.Id, "FixturesP", PSource, PPath);
        return AddTestProject(solution, targets.Id, "FixturesQ", QSource, QPath);
    }

    private static Solution AddTestProject(Solution solution, ProjectId targetsId, string name, string source, string path) =>
        solution
            .AddProject(name, name, LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(FullReferences.Value)
            .AddProjectReference(new ProjectReference(targetsId))
            .AddDocument("Tests.cs", SourceText.From(source), filePath: path)
            .Project.Solution;

    private Task<SentinelCallToolResult<object>> CallAsync(
        string? testScope = null,
        bool dryRun = true,
        string? reportProject = null,
        string? applyProjects = null) =>
        _tool.TagTestCategories(
            new ToolCallReason("testing the TagTestCategories tool"),
            "Targets",
            testScope: testScope,
            excludedTargets: null,
            excludedTests: null,
            maxTestShare: 0.25,
            classLevelThreshold: 0.5,
            framework: TestCategoryFramework.Auto,
            dryRun: dryRun,
            reportProject: reportProject,
            applyProjects: applyProjects);

    private static JsonElement ToJson(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return JsonDocument.Parse(JsonSerializer.Serialize(result.SuccessData, WebOptions)).RootElement;
    }

    private async Task<string> TextAsync(string path) =>
        (await _workspaceManager.GetDocumentTextAsync(path, ReadSource.Committed, CancellationToken.None))!;

    [Test]
    [Description("A project's plan and the solution-wide parts are identical with applyProjects, while testScope alone changes the shares and auto-excludes Hot")]
    public async Task ApplyProjects_DoesNotChangePlan_ButTestScopeDoes()
    {
        var full = ToJson(await CallAsync());
        var applyP = ToJson(await CallAsync(applyProjects: "FixturesP"));
        var scopedP = ToJson(await CallAsync(testScope: "FixturesP"));

        // Unrestricted: both projects listed, nothing auto-excluded (Hot is 4/35 tests).
        Assert.That(full.GetProperty("projects").GetArrayLength(), Is.EqualTo(2));
        Assert.That(full.GetProperty("autoExcludedTypes").GetArrayLength(), Is.EqualTo(0));

        // applyProjects=P: only P's row, solution-wide parts unchanged, Hot still not excluded.
        Assert.That(applyP.GetProperty("projects").GetArrayLength(), Is.EqualTo(1));
        Assert.That(applyP.GetProperty("projects")[0].GetProperty("project").GetString(), Is.EqualTo("FixturesP"));
        Assert.That(applyP.GetProperty("autoExcludedTypes").GetRawText(), Is.EqualTo(full.GetProperty("autoExcludedTypes").GetRawText()));
        Assert.That(applyP.GetProperty("collisionGroups").GetRawText(), Is.EqualTo(full.GetProperty("collisionGroups").GetRawText()));
        Assert.That(applyP.GetProperty("totalTestsScanned").GetInt32(), Is.EqualTo(35));
        Assert.That(applyP.GetProperty("applyProjects").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "FixturesP" }));

        // The per-project row for P is the same as in the unrestricted run.
        var fullRowP = full.GetProperty("projects").EnumerateArray().Single(p => p.GetProperty("project").GetString() == "FixturesP");
        Assert.That(applyP.GetProperty("projects")[0].GetRawText(), Is.EqualTo(fullRowP.GetRawText()));

        // Same edits for P's detail view with and without applyProjects.
        var fullDetail = ToJson(await CallAsync(reportProject: "FixturesP"));
        var applyDetail = ToJson(await CallAsync(reportProject: "FixturesP", applyProjects: "FixturesP"));
        Assert.That(applyDetail.GetProperty("plannedEdits").GetRawText(), Is.EqualTo(fullDetail.GetProperty("plannedEdits").GetRawText()));
        Assert.That(applyDetail.GetProperty("plannedEdits").GetRawText(), Does.Contain("Hot"));

        // testScope=P recomputes the shares over P's 5 tests only (Hot = 80%), so Hot IS auto-excluded: the footgun.
        var excluded = scopedP.GetProperty("autoExcludedTypes");
        Assert.That(excluded.GetArrayLength(), Is.EqualTo(1));
        Assert.That(excluded.GetRawText(), Does.Contain("Hot"));
        Assert.That(scopedP.GetProperty("totalTestsScanned").GetInt32(), Is.EqualTo(5));
    }

    [Test]
    [Description("Apply with applyProjects writes only the named project's files and leaves the other project, including its stale marked attribute, untouched")]
    public async Task Apply_WithApplyProjects_WritesOnlyThoseProjects()
    {
        var json = ToJson(await CallAsync(dryRun: false, applyProjects: "FixturesP"));

        Assert.That(json.GetProperty("status").GetString(), Is.EqualTo("applied"));
        Assert.That(json.GetProperty("projects").GetArrayLength(), Is.EqualTo(1));
        Assert.That(json.GetProperty("projects")[0].GetProperty("project").GetString(), Is.EqualTo("FixturesP"));
        Assert.That(json.GetProperty("changeIds").GetArrayLength(), Is.EqualTo(1));

        var p = await TextAsync(PPath);
        Assert.That(p, Does.Contain($"[Category(\"Hot\")] {Marker}"), "Hot is class-level in P because the plan used the full testScope");
        Assert.That(p, Does.Contain($"[Category(\"Cold\")] {Marker}"));

        Assert.That(await TextAsync(QPath), Is.EqualTo(QSource), "FixturesQ is untouched: no adds and its stale marked attribute is not removed");
    }

    [Test]
    [Description("Without applyProjects the same apply also edits FixturesQ and removes its stale marked attribute")]
    public async Task Apply_WithoutApplyProjects_EditsEveryProject()
    {
        var json = ToJson(await CallAsync(dryRun: false));

        Assert.That(json.GetProperty("projects").GetArrayLength(), Is.EqualTo(2));
        var q = await TextAsync(QPath);
        Assert.That(q, Is.Not.EqualTo(QSource));
        Assert.That(q, Does.Not.Contain("Gone"));
    }

    [TestCase(true)]
    [TestCase(false)]
    [Description("An applyProjects name that is not a scanned test project is an InvalidArgument error listing the valid names; nothing is written")]
    public async Task UnknownApplyProjects_ReturnsInvalidArgumentListingValidNames(bool dryRun)
    {
        var result = await CallAsync(dryRun: dryRun, applyProjects: "FixturesP, NoSuchProject");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("applyProjects 'NoSuchProject'"));
        Assert.That(result.ErrorData.Message, Does.Contain("FixturesP").And.Contain("FixturesQ"));
        Assert.That(await TextAsync(PPath), Is.EqualTo(PSource));
        Assert.That(await TextAsync(QPath), Is.EqualTo(QSource));
    }

    [Test]
    [Description("applyProjects matches project names case-insensitively and tolerates spaces and duplicates")]
    public async Task ApplyProjects_IsCaseInsensitive()
    {
        var json = ToJson(await CallAsync(applyProjects: " fixturesq , FIXTURESQ "));

        Assert.That(json.GetProperty("projects").GetArrayLength(), Is.EqualTo(1));
        Assert.That(json.GetProperty("projects")[0].GetProperty("project").GetString(), Is.EqualTo("FixturesQ"));
    }

    [Test]
    [Description("reportProject outside applyProjects is a clear InvalidArgument error; inside it works")]
    public async Task ReportProject_MustBeWithinApplyProjects()
    {
        var outside = await CallAsync(reportProject: "FixturesQ", applyProjects: "FixturesP");

        Assert.That(outside.IsError, Is.True);
        Assert.That(outside.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(outside.ErrorData.Message, Does.Contain("outside applyProjects").And.Contain("FixturesP"));

        var inside = ToJson(await CallAsync(reportProject: "fixturesp", applyProjects: "FixturesP"));
        Assert.That(inside.GetProperty("project").GetString(), Is.EqualTo("FixturesP"));
    }
}
