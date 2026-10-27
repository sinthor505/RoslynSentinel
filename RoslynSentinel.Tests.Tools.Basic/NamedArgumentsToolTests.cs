using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Text.Json;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Tests for NamedArgumentsTools: positional-to-named argument conversion with preview/apply modes and argument validation.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("NamedArgumentsTools")] // sentinel:auto-category
public class NamedArgumentsToolTests
{
    private const string Source = @"public class C
{
    public void M(int first, int second) { }
    public void N(int only) { }

    public void Run()
    {
        M(1, 2);
        N(3);
    }
}";

    private const string FixtureRelativePath = "ContosoOrders.Core/NamedArgumentsFixture.cs";

    private static NamedArgumentsTools BuildTools(IWorkspaceManager workspaceManager) =>
    new(new NamedArgumentsEngine(workspaceManager),
        new ValidationEngine(workspaceManager),
        workspaceManager,
        NullLogger<NamedArgumentsTools>.Instance);

    [Test]
    [Description("Preview reports per-file counts and leaves the file untouched")]
    public async Task NamedArguments_Preview_ReportsCountsWithoutWriting()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, Source));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.NamedArgumentsTool(
            new ToolCallReason("testing preview mode"),
            NamedArgumentsScope.file, scopeName: workspace.PathOf(FixtureRelativePath), targetSymbol: null, minParameters: null,
            literalArgumentsOnly: false, SemanticReplaceMode.preview);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var json = JsonSerializer.Serialize(result.SuccessData);
        Assert.That(json, Does.Contain("\"totalCallSites\":1"));
        Assert.That(json, Does.Contain("\"totalArguments\":2"));
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(Source));
    }

    [Test]
    [Description("Apply names the arguments of multi-parameter calls and skips single-parameter calls by default")]
    public async Task NamedArguments_Apply_NamesArgumentsOnDisk()
    {
        // Kept on disk on purpose: the single end-to-end apply smoke for this tool (real write through
        // PersistentWorkspaceManager, then read back from disk). Other apply tests use InMemoryWorkspace.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        File.WriteAllText(targetFile, Source, System.Text.Encoding.UTF8);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var result = await tools.NamedArgumentsTool(
            new ToolCallReason("testing apply mode"),
            NamedArgumentsScope.file, scopeName: targetFile, targetSymbol: null, minParameters: null,
            literalArgumentsOnly: false, SemanticReplaceMode.apply);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var disk = File.ReadAllText(targetFile);
        Assert.That(disk, Does.Contain("M(first: 1, second: 2);"));
        Assert.That(disk, Does.Contain("N(3);"));
    }

    [Test]
    [Description("targetSymbol narrows the conversion to one method and lowers the default minimum to 1")]
    public async Task NamedArguments_TargetSymbol_ConvertsOnlyThatMethod()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, Source));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.NamedArgumentsTool(
            new ToolCallReason("testing targetSymbol"),
            NamedArgumentsScope.solution, scopeName: null, targetSymbol: "M:C.N(System.Int32)", minParameters: null,
            literalArgumentsOnly: false, SemanticReplaceMode.apply);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var content = workspace.ReadText(FixtureRelativePath);
        Assert.That(content, Does.Contain("N(only: 3);"));
        Assert.That(content, Does.Contain("M(1, 2);"));
    }

    [Test]
    [Description("scopeName is rejected for solution scope with an actionable message")]
    public async Task NamedArguments_SolutionScopeWithScopeName_ReturnsInvalidArgument()
    {
        using var workspace = InMemoryWorkspace.Create();
        var tools = BuildTools(workspace.Manager);

        var result = await tools.NamedArgumentsTool(
            new ToolCallReason("testing validation"),
            NamedArgumentsScope.solution, scopeName: "Some.Project", targetSymbol: null, minParameters: null,
            literalArgumentsOnly: false, SemanticReplaceMode.preview);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("scopeName"));
    }

    [Test]
    [Description("project and file scope require scopeName")]
    public async Task NamedArguments_ProjectScopeWithoutScopeName_ReturnsInvalidArgument()
    {
        using var workspace = InMemoryWorkspace.Create();
        var tools = BuildTools(workspace.Manager);

        var result = await tools.NamedArgumentsTool(
            new ToolCallReason("testing validation"),
            NamedArgumentsScope.project, scopeName: null, targetSymbol: null, minParameters: null,
            literalArgumentsOnly: false, SemanticReplaceMode.preview);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("scopeName is required"));
    }

    [Test]
    [Description("minParameters below 1 is rejected")]
    public async Task NamedArguments_MinParametersZero_ReturnsInvalidArgument()
    {
        using var workspace = InMemoryWorkspace.Create();
        var tools = BuildTools(workspace.Manager);

        var result = await tools.NamedArgumentsTool(
            new ToolCallReason("testing validation"),
            NamedArgumentsScope.solution, scopeName: null, targetSymbol: null, minParameters: 0,
            literalArgumentsOnly: false, SemanticReplaceMode.preview);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("minParameters"));
    }
}
