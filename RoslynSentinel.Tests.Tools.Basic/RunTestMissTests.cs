using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The scope=project miss of TestRunEngine, reached through the RunTest tool (plan_not_found_error_orientation, Step 5e slice 4): a near project
/// name gives the ranked candidate with its retry (scopeName), an unrelated name gives the no-similar-name fact, and the loaded projects are
/// listed. The miss is raised before any `dotnet test` process starts, so an in-memory solution is enough (no subprocess); RunTest never writes,
/// and the file text is compared before and after anyway. The branch for a project that is loaded but has no project file path is not covered:
/// every in-memory project carries a file path, and a path that does exist would start a real `dotnet test`.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("TestRunResult")] // sentinel:auto-category
public sealed class RunTestMissTests
{
    private const string FileName = "Calc.cs";

    private const string Fixture = """
        namespace Billing.Orders;

        public class OrderPricingCalculator
        {
            public int ComputeTotal(int rate, int tax) => rate + tax;
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    private static async Task<string> RunMissAsync(string scopeName)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);

        var result = await Battery.Basic.TestTools.WorkspaceTools(workspace.Manager).RunTestTool(reason: "test message", ToolScope.project, scopeName: scopeName);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("TestRunFailed"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused lookup must not write the file.");
        return result.ErrorData!.Message;
    }

    [Test]
    public async Task RunTestProject_MisspeltProject_OffersTheNearProjectWithItsRetryAsync()
    {
        var message = await RunMissAsync("InMemoryProjct");

        Assert.That(message, Does.Contain("Did you mean 'InMemoryProject'? Retry with scopeName: 'InMemoryProject'."));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        Assert.That(message, Does.Contain("'InMemoryProjct'"), "The name the caller passed is echoed.");
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task RunTestProject_NoSimilarProject_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync("Zzz.Missing");

        Assert.That(message, Does.Contain("No loaded project has a similar name to 'Zzz.Missing'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }
}
