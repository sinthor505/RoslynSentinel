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
/// Tests for TestCategoryTaggingTools (TagTestCategories): the dry-run summary, the reportProject paging,
/// the dryRun: false apply summary, and the structured errors (missing targets, unknown project, bad share).
/// </summary>
[TestFixture]
public class TestCategoryTaggingToolTests
{
    private const string TargetsSource = @"
namespace Targets.Engines
{
    public class Alpha { public void Run() { } }
    public class Beta { public int Value { get; set; } }
    public class Gamma { public static void Go() { } }
}";

    private const string TestsSource = @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    [TestFixture]
    public class Drawer
    {
        [Test] public void T1() { new Alpha().Run(); }
        [Test] public void T2() { var b = new Beta(); b.Value = 1; }
        [Test] public void T3() { new Alpha().Run(); var n = new Beta().Value; }
        [Test] public void T4() { Gamma.Go(); }
        [Test] public void T5() { Assert.Pass(); }
    }
}";

    private static readonly string[] AllReferencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator);

    private static readonly Lazy<MetadataReference[]> FullReferences = new(
        () => AllReferencePaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray());

    // Target projects must not see a test framework, or they would be classified as test projects themselves.
    private static readonly Lazy<MetadataReference[]> NoNUnitReferences = new(
        () => AllReferencePaths
            .Where(p => !Path.GetFileName(p).StartsWith("nunit.", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    private FakeWorkspaceManager _workspaceManager;
    private TestCategoryTaggingTools _tool;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        _workspaceManager.SetTestSolution(BuildSolution());
        _tool = new TestCategoryTaggingTools(
            new TestCategoryTaggingImpl(
                new TestCategoryTaggingEngine(_workspaceManager),
                new TestCategoryApplyEngine(_workspaceManager, new ValidationEngine(_workspaceManager)),
                _workspaceManager,
                NullLogger.Instance));
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

        var tests = targets.Solution
            .AddProject("Fixtures", "Fixtures", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(FullReferences.Value)
            .AddProjectReference(new ProjectReference(targets.Id))
            .AddDocument("Tests.cs", SourceText.From(TestsSource), filePath: @"C:\fake\Fixtures\Tests.cs")
            .Project;

        return tests.Solution;
    }

    private Task<SentinelCallToolResult<object>> CallAsync(
        string targets = "Targets",
        TestCategoryFramework framework = TestCategoryFramework.Auto,
        bool dryRun = true,
        string? reportProject = null,
        double maxTestShare = 1.0)
    {
        return _tool.TagTestCategories(
            new ToolCallReason("testing the TagTestCategories tool"),
            targets,
            testScope: null,
            excludedTargets: null,
            excludedTests: null,
            maxTestShare: maxTestShare,
            classLevelThreshold: 0.5,
            framework: framework,
            dryRun: dryRun,
            reportProject: reportProject);
    }

    private static JsonElement ToJson(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return JsonDocument.Parse(JsonSerializer.Serialize(result.SuccessData, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
    }

    [Test]
    [Description("Without reportProject the tool returns the solution-wide parts plus a per-project summary table")]
    public async Task DryRun_Summary_ReturnsParametersAndPerProjectTable()
    {
        var json = ToJson(await CallAsync());

        Assert.That(json.GetProperty("mode").GetString(), Is.EqualTo("dryRun"));
        Assert.That(json.GetProperty("view").GetString(), Is.EqualTo("summary"));
        Assert.That(json.GetProperty("totalTestsScanned").GetInt32(), Is.EqualTo(5));

        var parameters = json.GetProperty("parametersUsed");
        Assert.That(parameters.GetProperty("maxTestShare").GetDouble(), Is.EqualTo(1.0));
        Assert.That(parameters.GetProperty("classLevelThreshold").GetDouble(), Is.EqualTo(0.5));
        Assert.That(parameters.GetProperty("targets")[0].GetString(), Is.EqualTo("Targets"));

        Assert.That(json.TryGetProperty("autoExcludedTypes", out _), Is.True);
        Assert.That(json.TryGetProperty("collisionGroups", out _), Is.True);
        Assert.That(json.TryGetProperty("warnings", out _), Is.True);

        var row = json.GetProperty("projects").EnumerateArray().Single();
        Assert.That(row.GetProperty("project").GetString(), Is.EqualTo("Fixtures"));
        Assert.That(row.GetProperty("framework").GetString(), Is.EqualTo("NUnit"));
        Assert.That(row.GetProperty("testsScanned").GetInt32(), Is.EqualTo(5));
        Assert.That(row.GetProperty("fixtures").GetInt32(), Is.EqualTo(1));
        Assert.That(row.GetProperty("classLevelAdds").GetInt32(), Is.EqualTo(0));
        Assert.That(row.GetProperty("methodLevelAdds").GetInt32(), Is.EqualTo(5));
        Assert.That(row.GetProperty("ambiguousFixtures").GetInt32(), Is.EqualTo(1));
        Assert.That(row.GetProperty("uncategorized").GetInt32(), Is.EqualTo(1));
        Assert.That(row.GetProperty("staleToRemove").GetInt32(), Is.EqualTo(0));
    }

    [Test]
    [Description("reportProject returns that project's fixtures, uncategorized test names, stale attributes and planned edits")]
    public async Task ReportProject_ReturnsProjectDetail()
    {
        var json = ToJson(await CallAsync(reportProject: "Fixtures"));

        Assert.That(json.GetProperty("view").GetString(), Is.EqualTo("project"));
        Assert.That(json.GetProperty("project").GetString(), Is.EqualTo("Fixtures"));
        Assert.That(json.GetProperty("testsScanned").GetInt32(), Is.EqualTo(5));

        var fixture = json.GetProperty("fixtures").EnumerateArray().Single();
        Assert.That(fixture.GetProperty("fixture").GetString(), Is.EqualTo("Fixtures.Drawer"));
        Assert.That(fixture.GetProperty("isAmbiguous").GetBoolean(), Is.True);
        Assert.That(fixture.GetProperty("testCount").GetInt32(), Is.EqualTo(5));

        var uncategorized = json.GetProperty("uncategorizedTests").EnumerateArray().Single();
        Assert.That(uncategorized.GetProperty("method").GetString(), Is.EqualTo("T5"));

        Assert.That(json.GetProperty("staleAttributes").GetArrayLength(), Is.EqualTo(0));

        var edits = json.GetProperty("plannedEdits").EnumerateArray().ToList();
        Assert.That(edits, Has.Count.EqualTo(5));
        Assert.That(
            edits.Select(e => e.GetProperty("attribute").GetString()),
            Does.Contain("[Category(\"Alpha\")] // sentinel:auto-category"));
        Assert.That(edits.All(e => e.GetProperty("level").GetString() == "Method"), Is.True);
    }

    [Test]
    [Description("reportProject matching is case-insensitive")]
    public async Task ReportProject_IsCaseInsensitive()
    {
        var json = ToJson(await CallAsync(reportProject: "fixtures"));

        Assert.That(json.GetProperty("project").GetString(), Is.EqualTo("Fixtures"));
    }

    [Test]
    [Description("An unknown reportProject returns InvalidArgument listing the valid project names")]
    public async Task ReportProject_Unknown_ListsValidProjectNames()
    {
        var result = await CallAsync(reportProject: "NoSuchProject");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("reportProject"));
        Assert.That(result.ErrorData.Message, Does.Contain("NoSuchProject"));
        Assert.That(result.ErrorData.Message, Does.Contain("Fixtures"));
    }

    [Test]
    [Description("dryRun: false applies the plan and returns the applied-change summary with per-project counts")]
    public async Task DryRunFalse_AppliesAndReturnsAppliedSummary()
    {
        var json = ToJson(await CallAsync(dryRun: false));

        Assert.That(json.GetProperty("status").GetString(), Is.EqualTo("applied"));
        Assert.That(json.GetProperty("dryRun").GetBoolean(), Is.False);
        Assert.That(json.GetProperty("validated").GetBoolean(), Is.True);
        Assert.That(json.GetProperty("methodLevelAdded").GetInt32(), Is.EqualTo(5));
        Assert.That(json.GetProperty("classLevelAdded").GetInt32(), Is.EqualTo(0));
        Assert.That(json.GetProperty("removedStale").GetInt32(), Is.EqualTo(0));
        Assert.That(json.GetProperty("failed").GetInt32(), Is.EqualTo(0));
        Assert.That(json.GetProperty("affectedFiles").GetArrayLength(), Is.EqualTo(1));
        Assert.That(json.GetProperty("changeIds").GetArrayLength(), Is.EqualTo(1));

        var project = json.GetProperty("projects").EnumerateArray().Single();
        Assert.That(project.GetProperty("project").GetString(), Is.EqualTo("Fixtures"));
        Assert.That(project.GetProperty("status").GetString(), Is.EqualTo("applied"));
        Assert.That(project.GetProperty("methodLevelAdded").GetInt32(), Is.EqualTo(5));

        var written = await _workspaceManager.GetDocumentTextAsync(@"C:\fake\Fixtures\Tests.cs", ReadSource.Committed, CancellationToken.None);
        Assert.That(written, Does.Contain("[Category(\"Alpha\")] // sentinel:auto-category"));
    }

    [Test]
    [Description("A second apply after a successful apply writes nothing: the plan has no edits left")]
    public async Task DryRunFalse_SecondRun_ReportsNoChanges()
    {
        await CallAsync(dryRun: false);

        var json = ToJson(await CallAsync(dryRun: false));

        Assert.That(json.GetProperty("status").GetString(), Is.EqualTo("no_changes"));
        Assert.That(json.GetProperty("methodLevelAdded").GetInt32(), Is.EqualTo(0));
        Assert.That(json.GetProperty("changeIds").GetArrayLength(), Is.EqualTo(0));
    }

    [TestCase("")]
    [TestCase("   ")]
    [Description("Missing or blank targets returns InvalidArgument naming the targets parameter")]
    public async Task MissingTargets_ReturnsInvalidArgumentNamingTargets(string targets)
    {
        var result = await CallAsync(targets: targets);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("targets is required"));
    }

    [Test]
    [Description("framework is the closed TestCategoryFramework enum (default Auto) serialized by name, so the schema lists the valid values")]
    public void FrameworkParameter_IsAStringNamedEnumDefaultingToAuto()
    {
        var parameter = typeof(TestCategoryTaggingTools).GetMethod(nameof(TestCategoryTaggingTools.TagTestCategories))!
            .GetParameters().Single(p => p.Name == "framework");

        Assert.That(parameter.ParameterType, Is.EqualTo(typeof(TestCategoryFramework)));
        Assert.That(parameter.HasDefaultValue, Is.True);
        Assert.That(parameter.DefaultValue, Is.EqualTo(TestCategoryFramework.Auto));
        Assert.That(
            Enum.GetNames<TestCategoryFramework>(),
            Is.EqualTo(new[] { "Auto", "NUnit", "XUnit", "MSTest" }));
        Assert.That(JsonSerializer.Serialize(TestCategoryFramework.NUnit), Is.EqualTo("\"NUnit\""));
        Assert.That(JsonSerializer.Deserialize<TestCategoryFramework>("\"xunit\""), Is.EqualTo(TestCategoryFramework.XUnit),
            "the converter binds names case-insensitively once an argument reaches it");
    }

    [TestCase(TestCategoryFramework.NUnit)]
    [TestCase(TestCategoryFramework.Auto)]
    [Description("An explicit framework that matches the project drives the plan like Auto does")]
    public async Task Framework_Explicit_PlansForMatchingProject(TestCategoryFramework framework)
    {
        var json = ToJson(await CallAsync(framework: framework));

        var row = json.GetProperty("projects").EnumerateArray().Single();
        Assert.That(row.GetProperty("framework").GetString(), Is.EqualTo("NUnit"));
    }

    [Test]
    [Description("A share outside 0..1 returns InvalidArgument naming maxTestShare")]
    public async Task MaxTestShareOutOfRange_ReturnsInvalidArgument()
    {
        var result = await CallAsync(maxTestShare: 1.5);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("maxTestShare"));
    }

    [Test]
    [Description("The tool is declared with McpServerTool and belongs to the testCategories toolset in the catalog")]
    public void Tool_IsDeclaredAndCatalogued()
    {
        var method = typeof(TestCategoryTaggingTools).GetMethod(nameof(TestCategoryTaggingTools.TagTestCategories));
        var attribute = (ModelContextProtocol.Server.McpServerToolAttribute?)Attribute.GetCustomAttribute(
            method!, typeof(ModelContextProtocol.Server.McpServerToolAttribute));

        Assert.That(attribute, Is.Not.Null);
        Assert.That(attribute!.Name, Is.EqualTo("TagTestCategories"));
        Assert.That(ToolsetCatalog.FindSet("TagTestCategories"), Is.EqualTo(ToolSetName.testCategories));
    }
}
