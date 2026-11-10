using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// MoveAllTypesToFiles tests split out of MoveTypeAndExtractTests (Advanced) so they run against the Basic core tools.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MoveAllTypesToFilesTests
{
    private static readonly string CrlfSimpleSource = Sources.Order.ReplaceLineEndings("\r\n");

    // --- MoveAllTypesToFiles (file scope) ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveAllTypesToFiles_FileScope_EachTypeGetsOwnFile()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.file, workspace.PathOf("Order.cs"));
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Status.cs"), Does.Contain("enum Status"));
        Assert.That(workspace.ReadText("IService.cs"), Does.Contain("interface IService"));
        Assert.That(workspace.ReadText("Order.cs"), Does.Not.Contain("enum Status"));
    }

    // --- MoveAllTypesToFiles (project scope) ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveAllTypesToFilesInProject_ProjectScope_MovesStatusToOwnFile()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.project, "InMemoryProject");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Status.cs"), Does.Contain("enum Status"));
    }

    // --- MoveAllTypesToFiles (solution scope) ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveAllTypesToFilesInSolution_SolutionScope_MovesStatusToOwnFile()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.solution);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Status.cs"), Does.Contain("enum Status"));
    }
}
