using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Tests for the QualifyCrefs tool (QualifyCrefsTools over QualifyCrefsImpl) over an in-memory workspace: a dry run plans
/// and diffs without writing, apply rewrites only the files with bare member crefs, a second apply is a no-op, scope
/// validation rejects a missing scopeName and an unknown project or file, project and file scope touch only their target,
/// and containingType limits the rewrite to one type.
/// </summary>
[TestFixture]
public class QualifyCrefsToolTests
{
    private const string QualifyProject = "Qualify";
    private const string OtherProject = "Other";

    private const string AlphaPath = @"C:\fake\Qualify\Alpha.cs";
    private const string GammaPath = @"C:\fake\Other\Gamma.cs";
    private const string CleanPath = @"C:\fake\Qualify\Clean.cs";
    private const string MembersPath = @"C:\fake\Qualify\Members.cs";

    private const string AlphaSource = @"namespace QualifyFixtures
{
    public class Alpha
    {
        public int Count { get; set; }

        /// <summary>Reads <see cref=""Count""/>.</summary>
        public void Use() { }
    }
}
";

    private const string GammaSource = @"namespace QualifyFixtures
{
    public class Gamma
    {
        public int Total { get; set; }

        /// <summary>Adds up <see cref=""Total""/>.</summary>
        public void Sum() { }
    }
}
";

    private const string CleanSource = @"namespace QualifyFixtures
{
    public class Clean
    {
        /// <summary>Plain text with no crefs.</summary>
        public void Noop() { }
    }
}
";

    private const string MembersSource = @"namespace QualifyFixtures
{
    public class Delta
    {
        public int Count { get; set; }

        /// <summary>Reads <see cref=""Count""/>.</summary>
        public void Use() { }
    }

    public class Epsilon
    {
        public int Value { get; set; }

        /// <summary>Reads <see cref=""Value""/>.</summary>
        public void Read() { }
    }
}
";

    private static readonly string[] AllReferencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator);

    private static readonly Lazy<MetadataReference[]> FullReferences = new(
        () => AllReferencePaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray());

    private FakeWorkspaceManager _workspaceManager;
    private QualifyCrefsTools _tool;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        _tool = new QualifyCrefsTools(
            new QualifyCrefsImpl(new ValidationEngine(_workspaceManager), _workspaceManager, NullLogger.Instance));
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // One project per distinct project name, in first-seen order. DocumentationMode.Parse is set explicitly: without it
    // the tree does not structure crefs and nothing is found.
    private static Solution BuildSolution(params (string Project, string Path, string Source)[] files)
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        foreach (var group in files.GroupBy(f => f.Project))
        {
            var project = solution
                .AddProject(group.Key, group.Key, LanguageNames.CSharp)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithParseOptions(new CSharpParseOptions(documentationMode: DocumentationMode.Parse))
                .AddMetadataReferences(FullReferences.Value);

            foreach (var file in group)
            {
                project = project.AddDocument(Path.GetFileName(file.Path), SourceText.From(file.Source), filePath: file.Path).Project;
            }

            solution = project.Solution;
        }

        return solution;
    }

    private async Task<string> TextAsync(string path) =>
        (await _workspaceManager.GetDocumentTextAsync(path, ReadSource.Committed, CancellationToken.None))!;

    private Task<SentinelCallToolResult<object>> QualifyAsync(
        ToolScope scope,
        string? scopeName = null,
        string? containingType = null,
        bool dryRun = true,
        bool returnDiff = false) =>
        _tool.QualifyCrefs(
            reason: new ToolCallReason("testing qualify crefs"),
            scope: scope,
            scopeName: scopeName,
            containingType: containingType,
            dryRun: dryRun,
            returnDiff: returnDiff);

    private static QualifyCrefsSummary Summary(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return (QualifyCrefsSummary)result.SuccessData!;
    }

    [Test]
    public async Task DryRun_ReturnsPlanAndDiff_WithoutWriting()
    {
        _workspaceManager.SetTestSolution(BuildSolution(
            (QualifyProject, AlphaPath, AlphaSource),
            (QualifyProject, CleanPath, CleanSource)));
        var before = await TextAsync(AlphaPath);

        var summary = Summary(await QualifyAsync(ToolScope.solution, returnDiff: true));

        Assert.That(summary.Status, Is.EqualTo("dry_run_ok"));
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.ChangeId, Is.Null);
        Assert.That(summary.Qualified, Is.EqualTo(1));

        var project = summary.Projects.Single();
        Assert.That(project.Diff, Is.Not.Null.And.Not.Empty);
        Assert.That(project.LineChanges, Is.Not.Null.And.Not.Empty);

        Assert.That(await TextAsync(AlphaPath), Is.EqualTo(before));
        Assert.That(await TextAsync(AlphaPath), Does.Contain("cref=\"Count\""));
    }

    [Test]
    public async Task Apply_RewritesOnlyFilesWithBareCrefs_AndReturnsChangeId()
    {
        _workspaceManager.SetTestSolution(BuildSolution(
            (QualifyProject, AlphaPath, AlphaSource),
            (QualifyProject, CleanPath, CleanSource)));
        var cleanBefore = await TextAsync(CleanPath);

        var summary = Summary(await QualifyAsync(ToolScope.solution, dryRun: false));

        Assert.That(summary.Status, Is.EqualTo("applied"));
        Assert.That(summary.DryRun, Is.False);
        Assert.That(summary.ChangeId, Is.Not.Null);
        Assert.That(summary.AffectedFiles, Has.Count.EqualTo(1));
        Assert.That(summary.Projects.Single().AffectedFiles, Is.EqualTo(new[] { AlphaPath }));

        var alphaAfter = await TextAsync(AlphaPath);
        Assert.That(alphaAfter, Does.Contain("cref=\"Alpha.Count\""));
        Assert.That(alphaAfter, Does.Not.Contain("cref=\"Count\""));

        Assert.That(await TextAsync(CleanPath), Is.EqualTo(cleanBefore));
    }

    [Test]
    public async Task Apply_SecondRun_ReportsNoChanges()
    {
        _workspaceManager.SetTestSolution(BuildSolution((QualifyProject, AlphaPath, AlphaSource)));
        var original = await TextAsync(AlphaPath);

        var first = Summary(await QualifyAsync(ToolScope.solution, dryRun: false));
        Assert.That(first.Status, Is.EqualTo("applied"));
        var afterFirst = await TextAsync(AlphaPath);
        Assert.That(afterFirst, Is.Not.EqualTo(original), "the first apply must have changed the file, or the second run proves nothing");
        Assert.That(afterFirst, Does.Contain("cref=\"Alpha.Count\""));

        var second = Summary(await QualifyAsync(ToolScope.solution, dryRun: false));

        Assert.That(second.Status, Is.EqualTo("no_changes"));
        Assert.That(second.ChangeId, Is.Null);
        Assert.That(second.ChangeIds, Is.Empty);
        Assert.That(await TextAsync(AlphaPath), Is.EqualTo(afterFirst));
    }

    [TestCase(ToolScope.file)]
    [TestCase(ToolScope.project)]
    public async Task ScopeFileOrProject_WithoutScopeName_ReturnsInvalidArgument(ToolScope scope)
    {
        _workspaceManager.SetTestSolution(BuildSolution((QualifyProject, AlphaPath, AlphaSource)));
        var before = await TextAsync(AlphaPath);

        var result = await QualifyAsync(scope, dryRun: false);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("scopeName is required"));
        Assert.That(await TextAsync(AlphaPath), Is.EqualTo(before));
    }

    [Test]
    public async Task ScopeProject_WithProjectName_RewritesOnlyThatProject()
    {
        _workspaceManager.SetTestSolution(BuildSolution(
            (QualifyProject, AlphaPath, AlphaSource),
            (OtherProject, GammaPath, GammaSource)));
        var gammaBefore = await TextAsync(GammaPath);

        var summary = Summary(await QualifyAsync(ToolScope.project, scopeName: QualifyProject, dryRun: false));

        Assert.That(summary.Status, Is.EqualTo("applied"));
        Assert.That(summary.Projects.Select(p => p.Project), Is.EqualTo(new[] { QualifyProject }));
        Assert.That(await TextAsync(AlphaPath), Does.Contain("cref=\"Alpha.Count\""));
        Assert.That(await TextAsync(GammaPath), Is.EqualTo(gammaBefore));
        Assert.That(await TextAsync(GammaPath), Does.Contain("cref=\"Total\""));
    }

    [Test]
    public async Task ScopeProject_UnknownProjectName_ReturnsNotFound()
    {
        _workspaceManager.SetTestSolution(BuildSolution((QualifyProject, AlphaPath, AlphaSource)));
        var before = await TextAsync(AlphaPath);

        var result = await QualifyAsync(ToolScope.project, scopeName: "NoSuchProject", dryRun: false);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
        // Observed: the lookup throws for an unknown project before the impl's own zero-match branch, so the mapper returns NotFound.
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("NoSuchProject"));
        Assert.That(await TextAsync(AlphaPath), Is.EqualTo(before));
    }

    [Test]
    public async Task ScopeFile_UnknownFilePath_ReturnsNotFound()
    {
        _workspaceManager.SetTestSolution(BuildSolution((QualifyProject, AlphaPath, AlphaSource)));
        var before = await TextAsync(AlphaPath);

        var result = await QualifyAsync(ToolScope.file, scopeName: @"C:\fake\Qualify\NoSuchFile.cs", dryRun: false);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("NoSuchFile.cs"));
        Assert.That(await TextAsync(AlphaPath), Is.EqualTo(before));
    }

    [Test]
    public async Task FileScope_RewritesOnlyTheNamedFile()
    {
        _workspaceManager.SetTestSolution(BuildSolution(
            (QualifyProject, AlphaPath, AlphaSource),
            (QualifyProject, GammaPath, GammaSource)));
        var gammaBefore = await TextAsync(GammaPath);

        var summary = Summary(await QualifyAsync(ToolScope.file, scopeName: AlphaPath, dryRun: false));

        Assert.That(summary.Status, Is.EqualTo("applied"));
        Assert.That(summary.Projects.Single().AffectedFiles, Is.EqualTo(new[] { AlphaPath }));
        Assert.That(await TextAsync(AlphaPath), Does.Contain("cref=\"Alpha.Count\""));
        Assert.That(await TextAsync(GammaPath), Is.EqualTo(gammaBefore));
        Assert.That(await TextAsync(GammaPath), Does.Contain("cref=\"Total\""));
    }

    [Test]
    public async Task ContainingType_QualifiesOnlyMembersOfThatType()
    {
        _workspaceManager.SetTestSolution(BuildSolution((QualifyProject, MembersPath, MembersSource)));

        var summary = Summary(await QualifyAsync(
            ToolScope.file, scopeName: MembersPath, containingType: "Delta", dryRun: false));

        Assert.That(summary.Status, Is.EqualTo("applied"));
        var text = await TextAsync(MembersPath);
        Assert.That(text, Does.Contain("cref=\"Delta.Count\""));
        Assert.That(text, Does.Not.Contain("cref=\"Count\""));
        Assert.That(text, Does.Contain("cref=\"Value\""));
        Assert.That(text, Does.Not.Contain("cref=\"Epsilon.Value\""));
    }
}
