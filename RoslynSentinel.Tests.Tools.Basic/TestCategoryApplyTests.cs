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
/// Tests for applying a category plan (TestCategoryApplyEngine and TagTestCategories dryRun: false) over an in-memory
/// workspace: attributes land with correct placement, stale marked attributes go while hand-written ones stay, a second
/// run is a no-op, and one test project's rejected batch does not block the others.
/// </summary>
[TestFixture]
[Category("TestCategoryApplyEngine")] // sentinel:auto-category
[Category("TestCategoryApplyResult")] // sentinel:auto-category
[Category("TestCategoryFramework")] // sentinel:auto-category
[Category("TestCategoryPlan")] // sentinel:auto-category
[Category("TestCategoryProjectApplyResult")] // sentinel:auto-category
[Category("TestCategoryTaggingImpl")] // sentinel:auto-category
[Category("TestCategoryTaggingTools")] // sentinel:auto-category
public class TestCategoryApplyTests
{
    private const string Marker = "// sentinel:auto-category";

    private const string TargetsSource = @"
namespace Targets.Engines
{
    public class Alpha { public void Run() { } }
    public class Beta { public int Value { get; set; } }
}";

    private const string GoodSource = @"using NUnit.Framework;
using Targets.Engines;
namespace FixturesGood
{
    [TestFixture]
    public class DrawerGood
    {
        [Test]
        public void T1() { new Alpha().Run(); }

        [Test]
        public void T2() { new Alpha().Run(); }

        [Test]
        [Category(""Gone"")] // sentinel:auto-category
        [Category(""Hand"")]
        public void T3() { var b = new Beta(); }
    }
}
";

    // A local CategoryAttribute(int) shadows NUnit's, so the generated [Category("Alpha")] cannot compile in this project.
    private const string BadSource = @"using NUnit.Framework;
using Targets.Engines;
namespace FixturesBad
{
    public sealed class CategoryAttribute : System.Attribute { public CategoryAttribute(int level) { } }

    [TestFixture]
    public class DrawerBad
    {
        [Test]
        public void T1() { new Alpha().Run(); }
    }
}
";

    private const string GoodPath = @"C:\fake\FixturesGood\Tests.cs";
    private const string BadPath = @"C:\fake\FixturesBad\Tests.cs";

    private static readonly string[] AllReferencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator);

    private static readonly Lazy<MetadataReference[]> FullReferences = new(
        () => AllReferencePaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray());

    private static readonly Lazy<MetadataReference[]> NoNUnitReferences = new(
        () => AllReferencePaths
            .Where(p => !Path.GetFileName(p).StartsWith("nunit.", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    private FakeWorkspaceManager _workspaceManager;
    private TestCategoryTaggingEngine _planner;
    private TestCategoryApplyEngine _applier;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        _planner = new TestCategoryTaggingEngine(_workspaceManager);
        _applier = new TestCategoryApplyEngine(_workspaceManager, new ValidationEngine(_workspaceManager));
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static Solution BuildSolution(bool includeBad)
    {
        var workspace = new AdhocWorkspace();
        var targets = workspace.CurrentSolution
            .AddProject("Targets", "Targets", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(NoNUnitReferences.Value)
            .AddDocument("Targets.cs", SourceText.From(TargetsSource), filePath: @"C:\fake\Targets\Targets.cs")
            .Project;

        var solution = targets.Solution;
        solution = AddTestProject(solution, targets.Id, "FixturesGood", "Tests.cs", GoodSource, GoodPath);
        if (includeBad)
        {
            solution = AddTestProject(solution, targets.Id, "FixturesBad", "Tests.cs", BadSource, BadPath);
        }

        return solution;
    }

    private static Solution AddTestProject(Solution solution, ProjectId targetsId, string name, string documentName, string source, string path) =>
        solution
            .AddProject(name, name, LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(FullReferences.Value)
            .AddProjectReference(new ProjectReference(targetsId))
            .AddDocument(documentName, SourceText.From(source), filePath: path)
            .Project.Solution;

    private Task<TestCategoryPlan> PlanAsync() => _planner.PlanAsync(
        new TestCategoryPlanOptions("Targets", null, null, null, 1.0, 0.5, TestCategoryFramework.Auto));

    private async Task<string> TextAsync(string path) =>
        (await _workspaceManager.GetDocumentTextAsync(path, ReadSource.Committed, CancellationToken.None))!;

    [Test]
    [Description("Apply adds a class-level and a method-level attribute with correct placement, removes the stale marked one and keeps the hand-written one")]
    public async Task Apply_AddsClassAndMethodLevel_RemovesStaleMarked_KeepsHandWritten()
    {
        _workspaceManager.SetTestSolution(BuildSolution(includeBad: false));
        var plan = await PlanAsync();
        Assert.That(plan.Error, Is.Null, plan.Error?.Message);

        var result = await _applier.ApplyAsync(plan, NullLogger.Instance);

        var project = result.Projects.Single();
        Assert.That(project.Error, Is.Null, project.Error?.Message);
        Assert.That(project.ClassLevelAdded, Is.EqualTo(1));
        Assert.That(project.MethodLevelAdded, Is.EqualTo(1));
        Assert.That(project.RemovedStale, Is.EqualTo(1));
        Assert.That(project.Failed, Is.EqualTo(0));
        Assert.That(project.AffectedFiles, Has.Count.EqualTo(1));
        Assert.That(project.ChangeId, Is.Not.Null);

        var text = await TextAsync(GoodPath);
        Assert.That(text, Does.Contain($"    [TestFixture]\n    [Category(\"Alpha\")] {Marker}\n    public class DrawerGood"),
            "the class-level attribute sits directly above the class, after [TestFixture], with the class's indent");
        Assert.That(text, Does.Contain($"        [Test]\n        [Category(\"Hand\")]\n        [Category(\"Beta\")] {Marker}\n        public void T3()"),
            "the stale marked line is gone with no blank line, the hand-written one stays, the new one sits above the method");
        Assert.That(text, Does.Not.Contain("Gone"));
    }

    [Test]
    [Description("A re-plan after apply yields zero edits (add is idempotent) and a second apply writes nothing")]
    [Category("TestProjectPlan")] // sentinel:auto-category
    public async Task Apply_SecondRun_ProducesZeroEdits()
    {
        _workspaceManager.SetTestSolution(BuildSolution(includeBad: false));
        await _applier.ApplyAsync(await PlanAsync(), NullLogger.Instance);
        var afterFirst = await TextAsync(GoodPath);

        var secondPlan = await PlanAsync();
        var secondResult = await _applier.ApplyAsync(secondPlan, NullLogger.Instance);

        Assert.That(secondPlan.Projects.Single().Edits, Is.Empty);
        Assert.That(secondResult.Projects.Single().AffectedFiles, Is.Empty);
        Assert.That(secondResult.Projects.Single().ChangeId, Is.Null);
        Assert.That(await TextAsync(GoodPath), Is.EqualTo(afterFirst));
    }

    [Test]
    [Description("A test project whose batch fails to compile is reported and left unchanged while the other project still applies")]
    [Category("TestProjectPlan")] // sentinel:auto-category
    public async Task Apply_OneProjectFailsCompile_OtherProjectStillApplies()
    {
        _workspaceManager.SetTestSolution(BuildSolution(includeBad: true));
        var plan = await PlanAsync();
        Assert.That(plan.Error, Is.Null, plan.Error?.Message);
        Assert.That(plan.Projects.Select(p => p.ProjectName), Is.EquivalentTo(new[] { "FixturesGood", "FixturesBad" }));

        var result = await _applier.ApplyAsync(plan, NullLogger.Instance);

        var bad = result.Projects.Single(p => p.ProjectName == "FixturesBad");
        var good = result.Projects.Single(p => p.ProjectName == "FixturesGood");

        Assert.That(bad.Error, Is.Not.Null);
        Assert.That(bad.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(bad.Failed, Is.EqualTo(plan.Projects.Single(p => p.ProjectName == "FixturesBad").Edits.Count));
        Assert.That(bad.AffectedFiles, Is.Empty);
        Assert.That(await TextAsync(BadPath), Is.EqualTo(BadSource), "the failed project's file is unchanged");

        Assert.That(good.Error, Is.Null, good.Error?.Message);
        Assert.That(good.ClassLevelAdded, Is.EqualTo(1));
        Assert.That(await TextAsync(GoodPath), Does.Contain($"[Category(\"Alpha\")] {Marker}"));
    }

    [Test]
    [Description("The tool reports a partial apply: status partially_applied, the failed project's error, and per-project counts")]
    public async Task Tool_DryRunFalse_PartialFailure_ReportsPerProjectOutcome()
    {
        _workspaceManager.SetTestSolution(BuildSolution(includeBad: true));
        var tool = new TestCategoryTaggingTools(new TestCategoryTaggingImpl(_planner, _applier, _workspaceManager, NullLogger.Instance));

        var result = await tool.TagTestCategories(
            new ToolCallReason("testing the TagTestCategories tool"), "Targets", null, null, null, 1.0, 0.5,
            TestCategoryFramework.Auto, dryRun: false);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(result.SuccessData, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
        Assert.That(json.GetProperty("status").GetString(), Is.EqualTo("partially_applied"));
        Assert.That(json.GetProperty("failed").GetInt32(), Is.GreaterThan(0));

        var projects = json.GetProperty("projects").EnumerateArray().ToDictionary(p => p.GetProperty("project").GetString()!);
        Assert.That(projects["FixturesBad"].GetProperty("status").GetString(), Is.EqualTo("failed"));
        Assert.That(projects["FixturesBad"].GetProperty("errorCode").GetString(), Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(projects["FixturesGood"].GetProperty("status").GetString(), Is.EqualTo("applied"));
        Assert.That(projects["FixturesGood"].GetProperty("classLevelAdded").GetInt32(), Is.EqualTo(1));
        Assert.That(projects["FixturesGood"].GetProperty("methodLevelAdded").GetInt32(), Is.EqualTo(1));
        Assert.That(projects["FixturesGood"].GetProperty("removedStale").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    [Description("When every project's batch fails the tool returns an error and nothing is written")]
    public async Task Tool_DryRunFalse_AllProjectsFail_ReturnsError()
    {
        var workspace = new AdhocWorkspace();
        var targets = workspace.CurrentSolution
            .AddProject("Targets", "Targets", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(NoNUnitReferences.Value)
            .AddDocument("Targets.cs", SourceText.From(TargetsSource), filePath: @"C:\fake\Targets\Targets.cs")
            .Project;
        _workspaceManager.SetTestSolution(AddTestProject(targets.Solution, targets.Id, "FixturesBad", "Tests.cs", BadSource, BadPath));
        var tool = new TestCategoryTaggingTools(new TestCategoryTaggingImpl(_planner, _applier, _workspaceManager, NullLogger.Instance));

        var result = await tool.TagTestCategories(
            new ToolCallReason("testing the TagTestCategories tool"), "Targets", null, null, null, 1.0, 0.5,
            TestCategoryFramework.Auto, dryRun: false);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(result.ErrorData.Message, Does.Contain("FixturesBad"));
        Assert.That(await TextAsync(BadPath), Is.EqualTo(BadSource));
    }

    [Test]
    [Description("A ToolException from the write chokepoint (here SessionHalted) keeps its own code and message instead of a generic Exception")]
    public async Task Tool_DryRunFalse_SessionHalted_ReportsSessionHaltedNotGenericException()
    {
        _workspaceManager.SetTestSolution(BuildSolution(includeBad: false));
        _workspaceManager.ApplyException = new SessionHaltedException("Session halted: external file drift was detected on a tracked file.");
        var tool = new TestCategoryTaggingTools(new TestCategoryTaggingImpl(_planner, _applier, _workspaceManager, NullLogger.Instance));

        var result = await tool.TagTestCategories(
            new ToolCallReason("testing the TagTestCategories tool"), "Targets", null, null, null, 1.0, 0.5,
            TestCategoryFramework.Auto, dryRun: false);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.SessionHalted));
        Assert.That(result.ErrorData.Message, Does.Contain("external file drift"));
    }
}
