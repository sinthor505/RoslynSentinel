using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// No-model checks for the seeded resume fault: ApplyResumeFaultCallsAsync runs the primary call and then the
/// ResumeFaultExtraCalls entries in order on a fresh fixture, and reports each call's real result. Nothing here
/// asks a model to act.
/// </summary>
[TestFixture]
public class ResumeFaultExecutionTests
{
    private const string TwinArguments = """
        {"reason":"change national to national-post in SummarizeShipping","action":"apply","filePath":"{SOLUTION}/ContosoOrders.Core/FixtureHelpers/OrderPricingCalculator.cs","oldContent":"_ => \"national\",","newContent":"_ => \"national-post\","}
        """;

    [Test]
    public async Task ApplyResumeFaultCalls_TwinReplaceSnippetInOneTurn_ExecutesBothInOrderWithRealResults()
    {
        LlmOptions.Configure([]);
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var scenario = BuildTwinScenario([("ReplaceSnippet", TwinArguments)], TwinArguments);
        var sampleDirectory = NewSampleDirectory();

        await using var environment = await ScenarioAgentTests.ScenarioEnvironment.CreateAsync(scenario, sampleDirectory, cancellationToken);

        var calls = await ScenarioAgentTests.ApplyResumeFaultCallsAsync(environment.Client, scenario, environment.SolutionDirectory, cancellationToken);

        // Printed before any assertion so the measurement survives a failing run.
        TestContext.Out.WriteLine($"FIRST: IsError={calls[0].IsError}");
        TestContext.Out.WriteLine(calls[0].ResultText);
        TestContext.Out.WriteLine($"SECOND: IsError={calls[1].IsError}");
        TestContext.Out.WriteLine(calls[1].ResultText);

        Assert.That(calls.Count, Is.EqualTo(2));
        Assert.That(calls[0].ToolName, Is.EqualTo("ReplaceSnippet"));
        Assert.That(calls[1].ToolName, Is.EqualTo("ReplaceSnippet"));
        Assert.That(calls[0].ArgumentsJson, Does.Not.Contain("{SOLUTION}"));
        Assert.That(calls[1].ArgumentsJson, Does.Not.Contain("{SOLUTION}"));
        Assert.That(calls[1].ArgumentsJson, Is.EqualTo(calls[0].ArgumentsJson));
        Assert.That(calls[0].IsError, Is.False);

        var calculatorText = await ScenarioFixtureFiles.ReadFileOrNullAsync(environment.SolutionDirectory, ScenarioFixtureFiles.CalculatorRelativePath, cancellationToken);
        Assert.That(calculatorText, Is.Not.Null);
        Assert.That(calculatorText!.Split("\"national-post\"").Length - 1, Is.EqualTo(1));
    }

    [Test]
    public async Task ApplyResumeFaultCalls_NoExtraCalls_ExecutesOnlyThePrimaryCall()
    {
        LlmOptions.Configure([]);
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var validateArguments = TwinArguments.Replace("\"action\":\"apply\"", "\"action\":\"validate\"");
        var scenario = BuildTwinScenario(null, validateArguments);
        var sampleDirectory = NewSampleDirectory();

        await using var environment = await ScenarioAgentTests.ScenarioEnvironment.CreateAsync(scenario, sampleDirectory, cancellationToken);

        var calls = await ScenarioAgentTests.ApplyResumeFaultCallsAsync(environment.Client, scenario, environment.SolutionDirectory, cancellationToken);

        Assert.That(calls.Count, Is.EqualTo(1));
        Assert.That(calls[0].ToolName, Is.EqualTo("ReplaceSnippet"));
    }

    private static ScenarioDefinition BuildTwinScenario(IReadOnlyList<(string Tool, string Arguments)>? extraCalls, string primaryArguments) =>
        new(
            Id: "TwinProbe",
            ScenarioType: 2,
            FaultKind: "",
            ToolUnderTest: "ReplaceSnippet",
            StagedFiles: ScenarioFixtureFiles.ChainStartingFiles,
            PromptTemplate: "x",
            TurnCap: 6,
            Expected: ExpectedOutcome.Recover,
            ExpectedRecoveryTool: null,
            CheckFinalState: (_, _) => Task.FromResult(FinalStateVerdict.Unchanged),
            SourceRun: "",
            ReproducesAt: "",
            FixedBy: "",
            ResumeFaultTool: "ReplaceSnippet",
            ResumeFaultArguments: primaryArguments,
            ResumeFaultSeeded: true,
            ResumeFaultExtraCalls: extraCalls);

    private static string NewSampleDirectory()
    {
        var directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            "ResumeFaultExecution",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
