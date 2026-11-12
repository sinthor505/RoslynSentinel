using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Project-name not-found facts and options of DocumentLookup.GetDocuments (Step 5e slice 6, inventory C2), reached through
// QualifyCrefs scope=project, the tool that passes a project filter into GetDocuments. The in-memory workspace names its
// only project "InMemoryProject", so the candidate case misspells that name. The retry parameter is scopeName.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class QualifyCrefsProjectMissTests
{
    private const string Source = "public class Alpha\r\n{\r\n}\r\n";

    private static QualifyCrefsTools ToolFor(InMemoryWorkspace workspace) => new(
        new QualifyCrefsImpl(Battery.Basic.TestEngines.ValidationEngine(workspace.Manager), workspace.Manager, NullLogger.Instance));

    private static Task<SentinelCallToolResult<object>> QualifyProjectAsync(QualifyCrefsTools tool, string scopeName) =>
        tool.QualifyCrefsTool(
            reason: new ToolCallReason("project miss facts and options"),
            scope: ToolScope.project,
            scopeName: scopeName,
            containingType: null,
            dryRun: false,
            returnDiff: false);

    [Test]
    public async Task ScopeProject_MisspeltProject_OffersTheNearProjectWithItsRetryAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create(("Types.cs", Source));
        var before = workspace.ReadText("Types.cs");

        var result = await QualifyProjectAsync(ToolFor(workspace), "InMemoryProjct");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("Did you mean 'InMemoryProject'? Retry with scopeName: 'InMemoryProject'."));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        Assert.That(message, Does.Not.Contain("No loaded project has a similar name"));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(workspace.ReadText("Types.cs"), Is.EqualTo(before), "A not-found project must not write any file.");
    }

    [Test]
    public async Task ScopeProject_NoSimilarProject_StatesTheNoCandidateFactAndListsTheProjects()
    {
        using var workspace = InMemoryWorkspace.Create(("Types.cs", Source));
        var before = workspace.ReadText("Types.cs");

        var result = await QualifyProjectAsync(ToolFor(workspace), "Zzz.Missing");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No loaded project has a similar name to 'Zzz.Missing'."));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(workspace.ReadText("Types.cs"), Is.EqualTo(before), "A not-found project must not write any file.");
    }
}
