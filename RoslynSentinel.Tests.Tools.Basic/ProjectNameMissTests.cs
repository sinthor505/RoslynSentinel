using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Project-name not-found facts and options (Step 5d-3). Through-tool cases use MoveAllTypesToFiles scope=project; the
// in-memory workspace names its only project "InMemoryProject", so the candidate case misspells that name.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ProjectNameMissTests
{
    private const string Source = "public class Alpha\r\n{\r\n}\r\n\r\npublic class Beta\r\n{\r\n}\r\n";

    [Test]
    public async Task MoveAllTypesToFiles_ProjectNameMisspelt_ReturnsDidYouMeanWithRetryOptionAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create(("Types.cs", Source));
        var before = workspace.ReadText("Types.cs");
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.project, "InMemoryProjct");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("Did you mean 'InMemoryProject'"));
        Assert.That(message, Does.Contain("Retry with projectName: 'InMemoryProject'"));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(workspace.ReadText("Types.cs"), Is.EqualTo(before), "A not-found project must not write any file.");
    }

    [Test]
    public async Task MoveAllTypesToFiles_ProjectNameWithNoSimilarName_StatesNoSimilarNameAndListsProjects()
    {
        using var workspace = InMemoryWorkspace.Create(("Types.cs", Source));
        var before = workspace.ReadText("Types.cs");
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.project, "Zzz.Missing");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No loaded project has a similar name to 'Zzz.Missing'"));
        Assert.That(message, Does.Contain("Loaded projects:"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(workspace.ReadText("Types.cs"), Is.EqualTo(before), "A not-found project must not write any file.");
    }

    [Test]
    public void ProjectNotFoundDetail_Build_WithCandidate_NamesRetryOptionAndLoadedProject()
    {
        var text = ProjectNotFoundDetail.Build("ContosoOrders.Cor", new[] { "ContosoOrders.Core" });
        Assert.That(text, Does.StartWith("Project 'ContosoOrders.Cor' was not found among the 1 loaded project(s); project names are compared ignoring case."));
        Assert.That(text, Does.Contain("Did you mean 'ContosoOrders.Core'? Retry with projectName: 'ContosoOrders.Core'."));
        Assert.That(text, Does.Not.Contain("No loaded project has a similar name"));
        Assert.That(text, Does.EndWith("Loaded projects: ContosoOrders.Core."));
    }

    [Test]
    public void ProjectNotFoundDetail_Build_WithoutCandidate_StatesNoSimilarNameAndCapsListAtTen()
    {
        var names = Enumerable.Range(1, 12).Select(i => $"Lib{i:00}").ToList();
        var text = ProjectNotFoundDetail.Build("Zzz.Missing", names);
        Assert.That(text, Does.StartWith("Project 'Zzz.Missing' was not found among the 12 loaded project(s); project names are compared ignoring case."));
        Assert.That(text, Does.Contain("No loaded project has a similar name to 'Zzz.Missing'."));
        Assert.That(text, Does.Not.Contain("Did you mean"));
        Assert.That(text, Does.Contain("Lib10"));
        Assert.That(text, Does.Not.Contain("Lib11"));
        Assert.That(text, Does.Contain("(+2 more)."));
    }
}
