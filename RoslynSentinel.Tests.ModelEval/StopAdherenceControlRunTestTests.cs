using System.Text.Json;
using System.Text.RegularExpressions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using ScenarioAgentTests = RoslynSentinel.Tests.ModelEval.LiveModel.ScenarioAgentTests;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Model-free proof that the stop-adherence control's DoneWhen is satisfiable in its own world (plan_modeleval_first_slice_scenario_rework
/// Step 63, risk R2): the world is built with <c>ScenarioAgentTests.ScenarioEnvironment.CreateAsync(StopAdherenceControl, ...)</c>, so
/// <c>RestorePackages: true</c> is exercised, and the <c>RunTest</c> tool is called through the in-process MCP client with
/// <c>scope: project</c>, <c>scopeName: ContosoOrders.Tests</c> and a filter naming <c>OrderCheckoutTests</c>. No model is contacted.
/// Needs dotnet and a package restore (a few minutes the first time), so the class is explicit. Filter: <c>StopAdherenceControlRunTestTests</c>.
/// The golden pass of the composed check (rename done, tests green) is deliberately not modelled here: it needs a rename performed
/// in the world, which a live control run covers end to end.
/// </summary>
// SchemaOptions, HiddenSchemaParams and the ToolArgumentValidator caches are process-static and every world resets them, so this
// fixture must not run in parallel with other world-building fixtures.
[TestFixture]
[NonParallelizable]
[Explicit("Needs dotnet and a package restore (a few minutes the first time), no model. Run: StopAdherenceControlRunTestTests")]
public class StopAdherenceControlRunTestTests
{
    private const string ModelVariable = "ROSLYNSENTINEL_LLM_MODEL";
    private const string PlaceholderModel = "model-free-test-placeholder";
    private const string TestProject = "ContosoOrders.Tests";
    private const string ControlTestClassFilter = "FullyQualifiedName~OrderCheckoutTests";

    private static readonly Regex HeadlineCounts = new(@"(?<total>\d+) tests, (?<passed>\d+) passed, (?<failed>\d+) failed", RegexOptions.Compiled);

    private string? _previousModel;
    private ModelEvalMcpEnvironment _world = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // The in-process environment builds an LM client that insists on a model name though no model call is made. The placeholder
        // is never contacted and is undone in OneTimeTearDown.
        _previousModel = Environment.GetEnvironmentVariable(ModelVariable);
        if (string.IsNullOrEmpty(_previousModel))
        {
            Environment.SetEnvironmentVariable(ModelVariable, PlaceholderModel);
        }

        LlmOptions.Configure([]);

        var sampleDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            nameof(StopAdherenceControlRunTestTests),
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(sampleDirectory);

        _world = await ScenarioAgentTests.ScenarioEnvironment.CreateAsync(
            StopAdherenceScenarios.StopAdherenceControl,
            sampleDirectory,
            TestContext.CurrentContext.CancellationToken);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }

        Environment.SetEnvironmentVariable(ModelVariable, _previousModel);
        LlmOptions.Configure([]);
    }

    [Test]
    public async Task RunTest_OrderCheckoutTests_RunsInTheControlWorld()
    {
        // suite is left out on purpose: the default is what the control's DoneWhen sentence leads a model to call.
        var result = await _world.Client.CallToolAsync(
            "RunTest",
            new Dictionary<string, object?>
            {
                ["reason"] = "Checks that RunTest runs OrderCheckoutTests in the stop-adherence control world",
                ["scope"] = "project",
                ["scopeName"] = TestProject,
                ["filter"] = ControlTestClassFilter,
                ["summary"] = true,
            },
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        TestContext.Out.WriteLine(TextOf(result));

        var (total, passed, failed) = ReadCounts(result);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.Not.True, "RunTest returned a tool error: " + TextOf(result));
            Assert.That(total, Is.GreaterThan(0), "no test ran in " + TestProject + " for the filter " + ControlTestClassFilter);
            Assert.That(passed, Is.GreaterThan(0));
            Assert.That(failed, Is.Zero, "the starting tree must pass OrderCheckoutTests (it calls GetFinalPrice, not CalcDisc)");
        });
    }

    [Test]
    public async Task Control_CheckFinalState_StartingTree_IsNotCorrect()
    {
        // The staged tree still has CalcDisc, so the composed grader must not call it Correct; the rename verdict is returned unchanged.
        var verdict = await StopAdherenceScenarios.StopAdherenceControl.CheckFinalState(_world.SolutionDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.That(verdict, Is.Not.EqualTo(FinalStateVerdict.Correct));
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
