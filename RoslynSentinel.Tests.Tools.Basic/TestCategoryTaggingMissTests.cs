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
/// The testScope project miss of TestCategoryTaggingEngine, reached through TagTestCategories (plan_not_found_error_orientation, Step 5e
/// slice 4): a near project name gives the ranked candidate with its retry, an unrelated name gives the no-similar-name fact, and the
/// loaded project names are listed either way. Run with dryRun: false so a refused call is shown to write nothing (every document text is
/// compared before and after).
/// </summary>
[TestFixture]
[Category("TestCategoryTaggingTools")] // sentinel:auto-category
public class TestCategoryTaggingMissTests
{
    private const string TargetsSource = """
        namespace Targets.Engines
        {
            public class Alpha { public void Run() { } }
        }
        """;

    private const string TestsSource = """
        using NUnit.Framework;
        using Targets.Engines;
        namespace Fixtures
        {
            [TestFixture]
            public class Drawer
            {
                [Test] public void T1() { new Alpha().Run(); }
            }
        }
        """;

    private static readonly string[] AllReferencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator);

    // Target projects must not see a test framework, or they would be classified as test projects themselves.
    private static readonly Lazy<MetadataReference[]> NoNUnitReferences = new(
        () => AllReferencePaths
            .Where(p => !Path.GetFileName(p).StartsWith("nunit.", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    private static readonly Lazy<MetadataReference[]> FullReferences = new(
        () => AllReferencePaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray());

    private FakeWorkspaceManager _workspaceManager;
    private TestCategoryTaggingTools _tool;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = FakeWorkspaceManager.ReadWrite();
        _workspaceManager.SetTestSolution(BuildSolution());
        _tool = Battery.Basic.TestTools.TestCategoryTaggingTools(
            Battery.Basic.TestTools.TestCategoryTaggingImpl(
                Battery.Basic.TestEngines.TestCategoryTaggingEngine(_workspaceManager),
                Battery.Basic.TestEngines.TestCategoryApplyEngine(_workspaceManager, Battery.Basic.TestEngines.ValidationEngine(_workspaceManager)),
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

        return targets.Solution
            .AddProject("Fixtures", "Fixtures", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(FullReferences.Value)
            .AddProjectReference(new ProjectReference(targets.Id))
            .AddDocument("Tests.cs", SourceText.From(TestsSource), filePath: @"C:\fake\Fixtures\Tests.cs")
            .Project.Solution;
    }

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    // Runs one refused call with dryRun: false: a NotFound error and no document changed. Returns the error text.
    private async Task<string> RunMissAsync(string testScope)
    {
        var before = await _workspaceManager.GetDocumentTextAsync(@"C:\fake\Fixtures\Tests.cs", ReadSource.Committed, CancellationToken.None);

        var result = await _tool.TagTestCategoriesTool(
            new ToolCallReason("testing a testScope miss"),
            "Targets",
            testScope: testScope,
            excludedTargets: null,
            excludedTests: null,
            maxTestShare: 1.0,
            classLevelThreshold: 0.5,
            framework: TestCategoryFramework.Auto,
            dryRun: false,
            reportProject: null);

        Assert.That(result.IsError, Is.True, "a testScope miss must be an error");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData?.Message);
        var after = await _workspaceManager.GetDocumentTextAsync(@"C:\fake\Fixtures\Tests.cs", ReadSource.Committed, CancellationToken.None);
        Assert.That(after, Is.EqualTo(before), "A refused lookup must not write the test file.");
        return result.ErrorData!.Message;
    }

    [Test]
    public async Task TestScope_MisspeltProject_OffersTheNearProjectWithItsRetryAsync()
    {
        var message = await RunMissAsync("Fixtrs");

        Assert.That(message, Does.Contain("Project 'Fixtrs' was not found among the 2 loaded project(s)"));
        Assert.That(message, Does.Contain("Did you mean 'Fixtures'? Retry with testScope: 'Fixtures'."));
        Assert.That(message, Does.Contain("Loaded projects: Fixtures, Targets."));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task TestScope_NoSimilarProject_StatesTheNoCandidateFactAndListsTheProjectsAsync()
    {
        var message = await RunMissAsync("Zzz.Missing");

        Assert.That(message, Does.Contain("No loaded project has a similar name to 'Zzz.Missing'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("Loaded projects: Fixtures, Targets."));
        AssertFactsAndOptionsOnly(message);
    }
}
