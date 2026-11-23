using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Pins the wire codes the Build and RunTest tools report for a run that did not start (plan_remove_engine_result_wrapper Step 16).
/// Each case fails before any `dotnet` process starts (scopeName validation, or the no-test-project check), so an in-memory solution
/// is enough (no subprocess).
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class NotRunWireCodeTests
{
    private const string FileName = "Calc.cs";
    private const string Fixture = "public class Calc { }";

    [Test]
    public async Task Build_QuickBuildProjectScopeWithoutScopeName_ReturnsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var result = await Battery.Basic.TestTools.WorkspaceTools(workspace.Manager).BuildTool(reason: "test message", BuildVerifyLevel.quickBuild, scope: ToolScope.project);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("scopeName (projectName) is required"));
    }

    [Test]
    public async Task Build_QuickBuildFileScopeWithoutScopeName_ReturnsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var result = await Battery.Basic.TestTools.WorkspaceTools(workspace.Manager).BuildTool(reason: "test message", BuildVerifyLevel.quickBuild, scope: ToolScope.file);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("scopeName (filePath) is required"));
    }

    [Test]
    public async Task RunTest_ProjectScopeWithoutScopeName_ReturnsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var result = await Battery.Basic.TestTools.WorkspaceTools(workspace.Manager).RunTestTool(reason: "test message", scope: ToolScope.project);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("scopeName (projectName) is required"));
    }

    [Test]
    public async Task RunTest_SolutionScopeWithoutTestProject_ReturnsNoProjectsAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var result = await Battery.Basic.TestTools.WorkspaceTools(workspace.Manager).RunTestTool(reason: "test message", scope: ToolScope.solution);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NoProjects));
        Assert.That(result.ErrorData!.Message, Does.Contain("No test projects"));
    }
}
