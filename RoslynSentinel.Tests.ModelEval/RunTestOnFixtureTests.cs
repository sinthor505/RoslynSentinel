using System.Text.Json;
using System.Text.RegularExpressions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Model-free proof that the <c>RunTest</c> tool works through an eval world (<see cref="ModelEvalMcpEnvironment.CreateAsync"/>) over a
/// scenario fixture: the world is built once over the golden state of <see cref="ScenarioId"/> (a restored ContosoOrders copy with the
/// scenario's files written before the solution loads), then RunTest is called through the in-process MCP client with
/// <c>scope: project</c>, <c>scopeName: ContosoOrders.Tests</c> and a filter naming one seed test class. The DoneWhen sentence of every
/// scenario relies on this call (plan_modeleval_first_slice_scenario_rework Decision 2). No LM Studio is needed: the agent client is
/// never used. The fixture needs a package restore (NuGet cache or network) and dotnet, so the class is explicit. Filter:
/// <c>RunTestOnFixtureTests</c>.
/// </summary>
// SchemaOptions, HiddenSchemaParams and the ToolArgumentValidator caches are process-static and every world resets them, so this
// fixture must not run in parallel with other world-building fixtures.
[TestFixture]
[NonParallelizable]
[Explicit("Needs dotnet and a package restore (a few minutes the first time), no model. Run: RunTestOnFixtureTests")]
public class RunTestOnFixtureTests
{
    // Any scenario whose golden fixture builds works; this one keeps its ChangeValidatorTests (two xUnit facts) in the seed, and the
    // rework keeps that class (plan spec for S2: "existing ChangeValidatorTests stay").
    private const string ScenarioId = "WidenToReadOnlyDictionary";
    private const string TestProject = "ContosoOrders.Tests";
    private const string SeedTestClassFilter = "FullyQualifiedName~ChangeValidatorTests";

    private static readonly Regex HeadlineCounts = new(@"(?<total>\d+) tests, (?<passed>\d+) passed, (?<failed>\d+) failed", RegexOptions.Compiled);

    private ModelEvalMcpEnvironment _world = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var scenario = ImplementationScenarioRegistry.Find(ScenarioId);
        var sampleDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            nameof(RunTestOnFixtureTests),
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(sampleDirectory);

        // The golden state (seed overlaid with step 1) is written before the solution loads, so the server never sees the writes.
        _world = await ModelEvalMcpEnvironment.CreateAsync(
            sampleDirectory,
            restorePackages: true,
            beforeLoad: (fixture, _, _) =>
            {
                WriteFiles(fixture.SolutionDirectory, scenario.FilesForStep(0));
                WriteFiles(fixture.SolutionDirectory, scenario.FilesForStep(1));
                return Task.CompletedTask;
            },
            afterLoad: null,
            TestContext.CurrentContext.CancellationToken);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }
    }

    [Test]
    public async Task RunTest_RunsTheFixtureTestProject_FromTheEvalWorld()
    {
        // suite is left out on purpose: the default is what a scenario's DoneWhen sentence leads a model to call.
        var result = await CallRunTestAsync(suite: null);
        TestContext.Out.WriteLine(TextOf(result));

        var (total, passed, failed) = ReadCounts(result);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.Not.True, "RunTest returned a tool error: " + TextOf(result));
            Assert.That(total, Is.GreaterThan(0), "no test ran in " + TestProject);
            Assert.That(passed, Is.GreaterThan(0));
            Assert.That(failed, Is.Zero, "the golden state of " + ScenarioId + " must pass its seed tests");
        });
    }

    [Test]
    public async Task RunTest_DefaultSuite_IncludesTheFixtureTests()
    {
        // The fast suite adds a category exclusion to the filter; the fixture's xUnit tests must not be filtered out by it, because
        // every scenario's DoneWhen sentence depends on RunTest finding them. The full suite is the reference count.
        var fast = await CallRunTestAsync(suite: "fast");
        var full = await CallRunTestAsync(suite: "full");
        TestContext.Out.WriteLine("fast: " + TextOf(fast));
        TestContext.Out.WriteLine("full: " + TextOf(full));

        var fastCounts = ReadCounts(fast);
        var fullCounts = ReadCounts(full);
        Assert.Multiple(() =>
        {
            Assert.That(fast.IsError, Is.Not.True, "RunTest (suite fast) returned a tool error: " + TextOf(fast));
            Assert.That(full.IsError, Is.Not.True, "RunTest (suite full) returned a tool error: " + TextOf(full));
            Assert.That(fullCounts.Total, Is.GreaterThan(0), "no test ran with suite full, so the comparison below would be vacuous");
            Assert.That(fastCounts.Total, Is.GreaterThan(0), "suite fast ran no fixture test");
            Assert.That(fastCounts.Total, Is.EqualTo(fullCounts.Total), "suite fast filtered out fixture tests that suite full runs");
        });
    }

    private static void WriteFiles(string solutionDirectory, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (relativePath, text) in files)
        {
            var path = Path.Combine(solutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    private Task<CallToolResult> CallRunTestAsync(string? suite)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["reason"] = "Checks that RunTest runs the fixture test project of an eval world",
            ["scope"] = "project",
            ["scopeName"] = TestProject,
            ["filter"] = SeedTestClassFilter,
            ["summary"] = true,
        };
        if (suite is not null)
        {
            arguments["suite"] = suite;
        }

        return _world.Client.CallToolAsync("RunTest", arguments, cancellationToken: TestContext.CurrentContext.CancellationToken).AsTask();
    }

    private static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    // The counts come from successData.{totalCount,passedCount,failedCount} of the reply; the headline text ("N tests, N passed,
    // N failed") is the fallback when the reply is not JSON of that shape.
    private static (int Total, int Passed, int Failed) ReadCounts(CallToolResult result)
    {
        var text = TextOf(result);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("successData", out var successData) && successData.ValueKind == JsonValueKind.Object
                ? successData
                : root;
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("totalCount", out var total) && total.TryGetInt32(out var totalCount)
                && data.TryGetProperty("passedCount", out var passed) && passed.TryGetInt32(out var passedCount)
                && data.TryGetProperty("failedCount", out var failed) && failed.TryGetInt32(out var failedCount))
            {
                return (totalCount, passedCount, failedCount);
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the headline.
        }

        var match = HeadlineCounts.Match(text);
        if (match.Success)
        {
            return (int.Parse(match.Groups["total"].Value), int.Parse(match.Groups["passed"].Value), int.Parse(match.Groups["failed"].Value));
        }

        Assert.Fail("The RunTest reply carries neither totalCount/passedCount/failedCount nor a headline with counts: " + text);
        return default;
    }
}
