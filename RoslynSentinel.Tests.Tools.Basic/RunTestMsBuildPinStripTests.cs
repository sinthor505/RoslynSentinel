using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// End to end for the MSBuild-pin strip (blocking_error_runtest_no_trx_in_git_worktree): the real <see cref="TestRunEngine"/> runs the
/// restored ContosoOrders.Tests project (two xUnit facts) in a child <c>dotnet test</c>.
/// Green: the test process carries pin values that break any <c>dotnet test</c> that inherits them (nonexistent SDK paths); the
/// engine strips them, so the run reports its two tests.
/// Red control: the same values go through the engine's caller <c>environment</c> parameter, which is applied after the strip, and the
/// run fails with the no-TRX detail. That proves the values really break a run, so the green case cannot pass for another reason.
/// Why nonexistent paths: pins naming a valid but different installed SDK did not break a hand-run <c>dotnet test</c> reliably (the
/// worktree failure needed the server's node handshake to land on a mismatched SDK), while unresolvable SDK paths fail every time.
/// </summary>
// The tests set process-wide environment variables (MSBUILD_EXE_PATH and friends), which every child process and MSBuild load in
// the test host inherits, so they must not overlap other fixtures.
[TestFixture]
[NonParallelizable]
[Category("OutOfProcess")] // starts dotnet processes and needs a package restore; excluded from RunTest suite "fast" (the default), run with suite "full"
public class RunTestMsBuildPinStripTests
{
    private const string TestProject = "ContosoOrders.Tests";

    private static readonly string[] PinnedVariables = ["MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildExtensionsPath"];

    private static readonly Dictionary<string, string> BreakingPins = new()
    {
        ["MSBUILD_EXE_PATH"] = @"C:\nonexistent-sdk\MSBuild.dll",
        ["MSBuildSDKsPath"] = @"C:\nonexistent-sdk\Sdks",
        ["MSBuildExtensionsPath"] = @"C:\nonexistent-sdk",
    };

    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private PersistentWorkspaceManager _workspaceManager = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = new RoslynSentinel.Tests.TestSolutionFixture(restorePackages: true);
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        // Loads (and registers MSBuildLocator, which sets the real pins in this process) before any test breaks them.
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, null, RoslynSentinel.Tests.TestSolutionFixture.LoadTimeout, TestContext.CurrentContext.CancellationToken);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
    }

    [Test]
    public async Task RunAsync_WithBreakingPinsInTheProcessEnvironment_StripsThemAndReportsTheTests()
    {
        var original = SetProcessEnvironment(BreakingPins);
        try
        {
            var result = await RunContosoOrdersTestsAsync(environment: null);

            Assert.That(result.RanToCompletion, Is.True, result.Detail);
            Assert.That(result.Outcome, Is.EqualTo(TestRunOutcome.Passed), result.Detail);
            Assert.That(result.TotalCount, Is.GreaterThan(0), "the TRX was parsed and its tests counted");
            Assert.That(result.PassedCount, Is.EqualTo(result.TotalCount));
        }
        finally
        {
            RestoreProcessEnvironment(original);
        }
    }

    [Test]
    public async Task RunAsync_WithTheSameBreakingPinsInTheCallerEnvironment_FailsWithTheNoTrxDetail()
    {
        // The caller environment is applied after the strip, so these values reach the child. The process environment keeps its real pins.
        var result = await RunContosoOrdersTestsAsync(environment: BreakingPins);

        Assert.That(result.RanToCompletion, Is.False, "the breaking values must break a run, or the green case proves nothing");
        Assert.That(result.Outcome, Is.EqualTo(TestRunOutcome.RunError));
        Assert.That(result.TotalCount, Is.Zero);
        Assert.That(result.Detail, Does.Contain("No TRX result file was produced"));
        Assert.That(result.Detail, Does.Contain("dotnet test exited with code 1"));
    }

    private Task<TestRunResult> RunContosoOrdersTestsAsync(IReadOnlyDictionary<string, string>? environment)
    {
        var engine = new TestRunEngine(_workspaceManager);
        return engine.RunAsync(
            ToolScope.project,
            TestProject,
            filter: null,
            TestResultsFilter.failed,
            maxDetails: 50,
            timeoutSeconds: 240,
            summary: true,
            environment: environment,
            suite: TestSuite.full,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
    }

    private static Dictionary<string, string?> SetProcessEnvironment(IReadOnlyDictionary<string, string> values)
    {
        var original = new Dictionary<string, string?>();
        foreach (var name in PinnedVariables)
        {
            original[name] = Environment.GetEnvironmentVariable(name);
        }

        foreach (var (name, value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        return original;
    }

    private static void RestoreProcessEnvironment(IReadOnlyDictionary<string, string?> original)
    {
        foreach (var (name, value) in original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
