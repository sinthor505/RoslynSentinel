using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Setup shared by the lookup-miss test classes of the engines CodeTransformTools hosts (plan_not_found_error_orientation Step 5e):
/// the real tool class against the real engines from the production registration, over an in-memory test solution. An error result
/// never writes, so each test also checks that no transform result came back.
/// </summary>
public abstract class CodeTransformMissFixtureBase
{
    private ServiceProvider _provider = null!;
    private IWorkspaceManager _workspace = null!;

    protected CodeTransformTools Tools { get; private set; } = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddRoslynSentinelEnginesAdvanced();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsAdvanced(
            services, new HashSet<string>(["Experimental"], StringComparer.OrdinalIgnoreCase));

        _provider = services.BuildServiceProvider();
        _workspace = _provider.GetRequiredService<IWorkspaceManager>();
        Tools = _provider.GetRequiredService<CodeTransformTools>();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    /// <summary>Seeds one file and returns its absolute path (the tool resolves a relative path against a solution root an in-memory solution lacks).</summary>
    protected string SetSource(string fileName, string source)
    {
        var path = Path.Combine(Path.GetTempPath(), "TestProj", fileName);
        _workspace.SetTestSolution(RoslynSentinel.Tests.TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(path, source)]));
        return path;
    }

    /// <summary>Asserts the result is the NotFound error of a lookup miss and returns its message.</summary>
    protected static string AssertNotFound(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.True, "a lookup miss must be an error, not a success message");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.SuccessData, Is.Null, "nothing was transformed or written");
        return result.ErrorData!.Message;
    }
}
