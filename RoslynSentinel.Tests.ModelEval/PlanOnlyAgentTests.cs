using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// Same fixture/bug/prompt ambiguity as <see cref="WholeFileRewriteAgentTests"/>'s
/// MinimalGuidanceDisambiguated test, but ApplyDiffTool/ChangeAccessibilityTool/ModifyModifierTool/CreateFileTool/
/// DeleteFileTool are blocked by a request filter -> the model can only read, search, and list, then must
/// state its plan in its final response instead of executing it. Cheaper (fewer turns, no build/edit
/// round-trips) than the full execute-and-verify tests, and isolates whether the "private" fork found
/// in project_minimalguidance_reasoning_pattern_analysis already exists at planning time, before any
/// tool-call commitment -> as opposed to something that only emerges once the model starts
/// improvising edits turn-by-turn. Note that analysis's pass/fail labels predate the AssertFixApplied
/// flip (see WholeFileRewriteAgentTests) and should be read with "calling/exposing the helper" as the
/// CORRECT outcome, not the failure. See also PlanThenExecuteAgentTests, which tests whether forcing
/// this same plan before editing changes the execution-time outcome (the actual anchoring hypothesis
/// this test alone can't confirm).
/// </summary>
[TestFixture]
public class PlanOnlyAgentTests
{
    // Same task framing as WholeFileRewriteAgentTests.DisambiguatedMinimalGuidanceUserPromptTemplate,
    // but asks for a plan instead of a fix, and is explicit that no edit tools exist -> without this,
    // the shared AgentSystemPrompts.CodingAgent's final workflow step ("report what you changed") plus the
    // model's own instinct to just try ApplyDiffTool would otherwise produce confused turns where it
    // repeatedly attempts a blocked tool instead of answering the question actually being asked here.
    private const string PlanOnlyUserPromptTemplate = """
        # Task: Plan a fix for a bug in FixtureHelpers/BlockConverter.cs

        Users report that editing shapes via `{0}/FixtureHelpers/BlockConverter.cs` sometimes
        changes unrelated formatting elsewhere in the same file, even though they only asked for
        one class to be converted.

        Investigate `BlockConverter.cs`, find the root cause, and plan a fix. A similar bug was
        already fixed elsewhere in this codebase using a reusable pattern - look for it and plan to
        reuse that same approach rather than inventing a new one.

        If the existing fix lives in a private method in another file, plan to call it directly
        rather than copying its body - your plan should raise its accessibility (e.g. to
        `internal`) so it can be called cross-file, but should not duplicate its logic, and should
        not modify anything else in that file.

        You do NOT have access to any file-editing tool in this session (ApplyDiff,
        ApplyUnifiedDiff, WriteFile, DeleteFile, ChangeAccessibility, and ModifyModifier are all
        unavailable and will return an error if called) - this is a planning exercise only. Do not
        attempt to make the change.

        Respond with your plan: the root cause, exactly which method(s)/file(s) you would touch,
        and the specific content you would place in `BlockConverter.cs` (write out the actual code
        you'd use for the reused pattern, not just a description of it). Do not touch code unrelated
        to the bug.
        """;

    // Tool surface: ModelEvalToolSurfaces.Basic (the "Refactor" and "Workspace" modes, see WholeFileRewriteAgentTests for why
    // both are needed even though this fixture blocks every mutating tool they'd otherwise expose) -> the request filter in
    // ConfigureToolSurface is what actually enforces "no edits", not the mode selection.

    // Tool names blocked for this fixture. Matches WholeFileRewriteAgentTests's fixture (BlockConverter/
    // BlockEditHelpers/Shape.cs) so the same investigation surface is available read-only; blocks every
    // mutating tool exposed by ActiveModes above, not just ApplyDiffTool/ApplyUnifiedDiffTool, so the model
    // can't route around the restriction via ChangeAccessibilityTool/ModifyModifierTool/WriteFileTool/DeleteFileTool
    // either.
    private static readonly HashSet<string> BlockedToolNames = new(StringComparer.Ordinal)
    {
        "ApplyDiff", "ApplyUnifiedDiff", "ApplyDiffWithConfirmationCode", "ChangeAccessibility", "ModifyModifier",
        "WriteFile", "DeleteFile",
    };

    private ModelEvalMcpEnvironment? _environment;

    // Aliases of _environment's members, assigned in SetUp so the test bodies keep their field names; _environment owns disposal.
    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private LmStudioAgentClient _agentClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        _environment = await ModelEvalMcpEnvironment.CreateAsync(
            _runDirectory,
            restorePackages: false,
            beforeLoad: null,
            afterLoad: StageFilesAsync,
            TestContext.CurrentContext.CancellationToken,
            configureToolSurface: ConfigureToolSurface);
        _host = _environment.Host;
        _mcpClient = _environment.Client;
        _fixture = _environment.Fixture;
        _agentClient = _environment.AgentClient;
    }

    // The Basic tool surface, plus a request filter that blocks every mutating tool in BlockedToolNames before it reaches
    // the real implementation, returning an error the model can read and reason about (rather than an unhandled protocol
    // exception) -> mirrors the shape of the centralized filters AddRoslynSentinelToolsBasic itself registers (see
    // ServiceRegistrationExtensionsBasic.cs), just test-local instead of shared, since only this experiment needs a
    // read-only toolset.
    private static void ConfigureToolSurface(IMcpServerBuilder mcpBuilder, IServiceCollection services)
    {
        ModelEvalToolSurfaces.Basic(mcpBuilder, services);

        mcpBuilder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => new McpRequestHandler<CallToolRequestParams, CallToolResult>(
                async (context, cancellationToken) =>
                {
                    if (context.Params?.Name is { } toolName && BlockedToolNames.Contains(toolName))
                    {
                        return new CallToolResult
                        {
                            Content =
                            [
                                new TextContentBlock
                                {
                                    Text = $"{toolName} is unavailable in this session - this is a " +
                                        "planning-only exercise. Describe what you would do instead " +
                                        "of calling this tool.",
                                },
                            ],
                            IsError = true,
                        };
                    }

                    return await next(context, cancellationToken);
                }));
        });
    }

    private static async Task StageFilesAsync(
        RoslynSentinel.Tests.TestSolutionFixture fixture,
        IWorkspaceManager workspaceManager,
        CancellationToken cancellationToken)
    {
        await fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs"),
            WholeFileRewriteReproducer.HelperFileContent,
            reloadSolution: false,
            cancellationToken: cancellationToken);
        await fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs"),
            WholeFileRewriteReproducer.BuggyFileContent,
            reloadSolution: false,
            cancellationToken: cancellationToken);
        await fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "Shape.cs"),
            WholeFileRewriteReproducer.TargetAbstractClassFileContent,
            reloadSolution: true,
            cancellationToken: cancellationToken);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
            _environment = null;
        }

        if (_runDirectory is not null)
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(_runDirectory);
        }
    }

    /// <summary>
    /// The model investigates and states a plan, but never calls ApplyDiffTool -> scores the plan's own
    /// text for the same "private"/"public"/"call" vs. "copy"/"pattern" fork found in
    /// project_minimalguidance_reasoning_pattern_analysis. Per WholeFileRewriteAgentTests.
    /// AssertFixApplied's flipped scoring, "make it public and call it" is now the CORRECT plan
    /// (matching the real-world consolidation precedent, commit 8a8963d) -> a model that instead
    /// plans to copy the pattern into a new private method is the fork this experiment surfaces.
    /// </summary>
    [Test]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_PlansWholeFileRewriteFix_PrefersCallingHelper()
    {
        var runner = new ModelAgentRunner(
            _agentClient, _mcpClient, repeatedFailureLimit: 10, maxTokensPerTurn: 16384, turnCap: 15, wallClockCap: TimeSpan.FromMinutes(15),
            logger: _host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var userPrompt = string.Format(PlanOnlyUserPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        var result = await runner.RunAsync(EvalContractOptions.ComposeSystemPrompt(), userPrompt, _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        var blockedCalls = result.Transcript.Turns
            .SelectMany(t => t.ToolCalls)
            .Where(tc => BlockedToolNames.Contains(tc.ToolName))
            .ToList();
        Assert.That(blockedCalls, Is.Empty,
            $"Model attempted a blocked mutating tool ({string.Join(", ", blockedCalls.Select(c => c.ToolName))}) " +
            $"instead of only planning. Transcript: {result.TranscriptPath}");

        // This model streams its final answer as ReasoningContent (not Content) on a turn that
        // makes no tool calls -> observed across every run of this fixture 2026-08-31, where
        // Content was consistently empty ("(none)" in agent.log) despite the model's full plan,
        // including exact code, appearing under Reasoning instead. Fall back to ReasoningContent
        // so a plan-bearing turn isn't scored as an empty response.
        var finalTurn = result.Transcript.Turns
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t.ModelMessage.Content) || !string.IsNullOrWhiteSpace(t.ModelMessage.ReasoningContent));
        var finalMessage = finalTurn is null
            ? string.Empty
            : (string.IsNullOrWhiteSpace(finalTurn.ModelMessage.Content) ? finalTurn.ModelMessage.ReasoningContent : finalTurn.ModelMessage.Content) ?? string.Empty;
        Assert.That(finalMessage, Does.Contain("ReplaceBlockFormatted"),
            $"The plan should name ReplaceBlockFormatted as the pattern to reuse. Transcript: {result.TranscriptPath}");

        // This is the metric under test, not a pass/fail gate -> see the class doc comment. Left as an
        // assertion (rather than only manual review) so it shows up directly in the test's own output
        // per run, but intentionally does not fail the test: a run that reasons "make it public" here
        // is exactly the data point this experiment exists to surface, not a defect in the harness.
        TestContext.Out.WriteLine(finalMessage.Contains("private", StringComparison.OrdinalIgnoreCase)
            ? "PLAN MENTIONS 'private' - model's stated plan leans toward calling/exposing the helper (correct)."
            : "Plan does not mention 'private' - model's stated plan leans toward copying the pattern (incorrect).");
    }
}
