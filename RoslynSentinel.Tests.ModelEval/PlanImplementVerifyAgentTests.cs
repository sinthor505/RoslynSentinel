using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Tests.ModelEval.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Tests the plan/implement/verify split proposed after <c>project_scriptedplan_5run_result</c>
/// (execution of a known-correct plan went 5/5, vs ~20-34% when the model must plan itself) —
/// instead of one model juggling bug-location, planning, and execution turn-by-turn in a single
/// context, this splits the same fixture/bug into three separate model calls, each with its own
/// fresh context and its own tool availability: a read-only plan phase (same prompt shape as
/// <see cref="PlanOnlyAgentTests"/>), a full-tool-access implement phase that is handed the PREVIOUS
/// PHASE'S OWN plan text verbatim (not a hand-picked one, unlike
/// <see cref="WholeFileRewriteAgentTests"/>'s ScriptedPlan test), and a read-only verify phase that
/// independently judges the on-disk result. Pass/fail requires both the mechanical
/// <see cref="WholeFileRewriteAgentTests.AssertFixApplied(RoslynSentinel.Tests.TestSolutionFixture, AgentRunResult)"/>
/// check AND the verify phase's own "VERIFIED: PASS" verdict — see that method's shared static form
/// and <see cref="AgentSystemPrompts.CodeReviewer"/> for why an independent model judgment is
/// required in addition to, not instead of, the mechanical check.
/// </summary>
[TestFixture]
public class PlanImplementVerifyAgentTests
{
    // Same task framing as PlanOnlyAgentTests.PlanOnlyUserPromptTemplate — kept as a separate copy
    // here (rather than a cross-file reference) since this phase's caller lives in a different test
    // class and the two prompts are free to diverge if either phase's test needs tuning without
    // affecting the other.
    private const string PlanUserPromptTemplate = """
        # Task: Plan a fix for a bug in FixtureHelpers/BlockConverter.cs

        Users report that editing shapes via `{0}/FixtureHelpers/BlockConverter.cs` sometimes
        changes unrelated formatting elsewhere in the same file, even though they only asked for
        one class to be converted.

        Investigate `BlockConverter.cs`, find the root cause, and plan a fix. A similar bug was
        already fixed elsewhere in this codebase using a reusable pattern — look for it and plan to
        reuse that same approach rather than inventing a new one.

        If the existing fix lives in a private method in another file, plan to call it directly
        rather than copying its body — your plan should raise its accessibility (e.g. to
        `internal`) so it can be called cross-file, but should not duplicate its logic, and should
        not modify anything else in that file.

        You do NOT have access to any file-editing tool in this session (ApplyDiff, CreateFile,
        DeleteFile, ChangeAccessibility, and ModifyModifier are all unavailable and will return an
        error if called) — this is a planning exercise only. Do not attempt to make the change.

        Respond with your plan: the root cause, exactly which method(s)/file(s) you would touch,
        and the specific content you would place in `BlockConverter.cs` (write out the actual code
        you'd use for the reused pattern, not just a description of it). Do not touch code unrelated
        to the bug.
        """;

    // Splices the PREVIOUS PHASE'S OWN plan text (produced by this same model, moments earlier, in
    // a separate context) into an instruction shaped like WholeFileRewriteAgentTests's
    // ScriptedPlanUserPromptTemplate — the deliberate experimental difference from that test is that
    // the plan here is whatever the model itself came up with, not a hand-picked known-correct one.
    private const string ImplementUserPromptTemplate = """
        # Task: Fix a bug in FixtureHelpers/BlockConverter.cs

        Users report that editing shapes via `{0}/FixtureHelpers/BlockConverter.cs` sometimes
        changes unrelated formatting elsewhere in the same file, even though they only asked for
        one class to be converted.

        Another agent already investigated this bug and produced the following plan. Apply it as
        described. If a step is ambiguous or turns out to be wrong once you look at the real code,
        use your best judgment to fix the underlying bug correctly rather than following the plan
        literally off a cliff — but stay within the spirit of what it describes rather than
        redesigning the fix from scratch.

        ## Plan from the previous investigation

        {1}

        ## Constraints

        Do not modify any method, field, or class that is not directly involved in this fix, even
        ones that look unused or unrelated — leave everything else exactly as you found it.

        Verify your fix compiles, using an MCP tool (you have no terminal access). Scope the build
        to just the `ContosoOrders.Core` project rather than the whole solution.

        Report what you changed and the verification result.
        """;

    // Re-states the bug report so the reviewer has the same context the implementer had, then
    // points it at the four correctness checks AssertFixApplied enforces mechanically — the model's
    // own read of the code is independent of (and a different kind of signal from) that mechanical
    // check, not a restatement of it.
    private const string VerifyUserPromptTemplate = """
        # Task: Review a fix for a bug in FixtureHelpers/BlockConverter.cs

        Users had reported that editing shapes via `{0}/FixtureHelpers/BlockConverter.cs`
        sometimes changed unrelated formatting elsewhere in the same file, even though they only
        asked for one class to be converted. An agent has since applied a fix.

        Read the current contents of `{0}/FixtureHelpers/BlockConverter.cs` and
        `{0}/FixtureHelpers/BlockEditHelpers.cs` and judge:

        1. Does `ConvertAbstractClassToInterface` no longer call a whole-file rewrite helper (i.e.
           no more reformatting of the entire file for a single-class edit)?
        2. Is the fix reusing the existing `ReplaceBlockFormatted` helper (calling it, with its
           accessibility raised so it can be called cross-file) rather than duplicating its logic
           into a new method?
        3. Are `UnrelatedMethodBefore` and `UnrelatedMethodAfter` — and everything else in both
           files — byte-for-byte unchanged from what an unrelated method should look like (i.e. no
           incidental reformatting)?
        4. Does the affected project actually build? Verify this yourself with an MCP build tool
           scoped to the `ContosoOrders.Core` project — do not take it on faith.

        State your verdict as described in your system prompt.
        """;

    // "Refactor" (not "Refactoring") and "Workspace" are the exact mode strings
    // AddRoslynSentinelToolsBasic checks — see WholeFileRewriteAgentTests.ActiveModes for why both
    // modes are needed for every phase here, including the read-only ones: the tools this fixture's
    // prompts need (ReadFile/ListAll/SearchSolutionText/Build) live in SentinelWorkspaceTools, gated
    // by "Workspace", alongside the mutating tools gated by "Refactor" — the per-phase BlockedToolNames
    // filter below is what actually enforces read-only, not the mode selection.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Refactor", "Workspace",
    };

    // Blocks every mutating tool exposed by ActiveModes so the plan and verify phases genuinely
    // cannot edit files, matching PlanOnlyAgentTests.BlockedToolNames.
    private static readonly HashSet<string> BlockedToolNames = new(StringComparer.Ordinal)
    {
        "ApplyDiff", "ApplyDiffWithConfirmationCode", "ChangeAccessibility", "ModifyModifier",
        "CreateFile", "DeleteFile",
    };

    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set — model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        _fixture = new RoslynSentinel.Tests.TestSolutionFixture();

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        // The fixture files are written once, up front, via a throwaway workspace manager that is
        // never used again — each phase's RunPhaseAsync spins up its own real host/workspace and
        // just re-loads this same on-disk solution path, so all three phases see identical starting
        // content without needing this manager (or the tool server it would require) to stay alive.
        var writerServices = new ServiceCollection();
        writerServices.AddLogging();
        writerServices.AddRoslynSentinelEnginesBasic();
        var writerProvider = writerServices.BuildServiceProvider();
        var writerWorkspaceManager = writerProvider.GetRequiredService<IWorkspaceManager>();
        await writerWorkspaceManager.LoadSolutionAsync(_fixture.SolutionPath, TestContext.CurrentContext.CancellationToken);

        await _fixture.AddFileToSolution(
            writerWorkspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs"),
            WholeFileRewriteReproducer.HelperFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            writerWorkspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs"),
            WholeFileRewriteReproducer.BuggyFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            writerWorkspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "Shape.cs"),
            WholeFileRewriteReproducer.TargetAbstractClassFileContent,
            reloadSolution: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        (writerProvider as IDisposable)?.Dispose();
    }

    [TearDown]
    public void TearDown()
    {
        _fixture?.Dispose();
    }

    /// <summary>
    /// One phase's worth of MCP host/client/runner, torn down before the next phase starts so
    /// tool-availability differences between phases (blocked vs. unblocked) can't leak, and so each
    /// phase's model call gets a genuinely fresh <c>messages</c> list inside
    /// <see cref="ModelAgentRunner.RunAsync"/> — the "fresh context per phase" the design calls for.
    /// The fixture's files already exist on disk from <see cref="SetUp"/>; this only loads the
    /// existing solution path into this phase's own <see cref="IWorkspaceManager"/> view of it.
    /// </summary>
    private async Task<AgentRunResult> RunPhaseAsync(
        string phaseName,
        string systemPrompt,
        string userPrompt,
        bool blockMutatingTools,
        int turnCap,
        CancellationToken cancellationToken)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = RoslynSentinelTaskTools.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        if (blockMutatingTools)
        {
            // Same shape as PlanOnlyAgentTests's filter: intercepts a blocked tool call and returns
            // a readable error instead of forwarding it, so the model can reason about why it
            // failed rather than hitting an unhandled protocol exception.
            mcpBuilder.WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(next => new McpRequestHandler<CallToolRequestParams, CallToolResult>(
                    async (context, ct) =>
                    {
                        if (context.Params?.Name is { } toolName && BlockedToolNames.Contains(toolName))
                        {
                            return new CallToolResult
                            {
                                Content =
                                [
                                    new TextContentBlock
                                    {
                                        Text = $"{toolName} is unavailable in this session — you have " +
                                            "read-only tools only here. Describe what you would do " +
                                            "instead of calling this tool.",
                                    },
                                ],
                                IsError = true,
                            };
                        }

                        return await next(context, ct);
                    }));
            });
        }

        var phaseRunDirectory = Path.Combine(_runDirectory, phaseName);

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddHttpClient<LmStudioAgentClient>(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
        });
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(phaseRunDirectory, "agent.log")));

        var host = hostBuilder.Build();
        McpClient? mcpClient = null;
        try
        {
            _ = host.RunAsync();

            var workspaceManager = host.Services.GetRequiredService<IWorkspaceManager>();
            await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, cancellationToken);

            var clientTransport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: NullLoggerFactory.Instance);

            mcpClient = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);
            var agentClient = host.Services.GetRequiredService<LmStudioAgentClient>();

            // 5-minute wall-clock cap per phase per the user's instruction (.113 alone completes
            // today's single-call prompts in ~1-3 minutes; three narrower-scoped calls should each
            // need less, not more). Turn cap is just a safety ceiling underneath that, not the real
            // constraint.
            var runner = new ModelAgentRunner(
                agentClient, mcpClient, turnCap: turnCap, wallClockCap: TimeSpan.FromMinutes(5),
                logger: host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());

            return await runner.RunAsync(systemPrompt, userPrompt, phaseRunDirectory, cancellationToken);
        }
        finally
        {
            if (mcpClient is not null)
            {
                await mcpClient.DisposeAsync();
            }

            await host.StopAsync();
            host.Dispose();
        }
    }

    // This model streams its final answer as ReasoningContent (not Content) on a turn that makes no
    // tool calls — observed across every PlanOnlyAgentTests run 2026-08-31. Fall back to
    // ReasoningContent so a text-bearing turn (plan text here, verify verdict in the verify phase)
    // isn't scored as empty.
    private static string ExtractFinalMessage(AgentRunResult result)
    {
        var finalTurn = result.Transcript.Turns
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t.ModelMessage.Content) || !string.IsNullOrWhiteSpace(t.ModelMessage.ReasoningContent));
        return finalTurn is null
            ? string.Empty
            : (string.IsNullOrWhiteSpace(finalTurn.ModelMessage.Content) ? finalTurn.ModelMessage.ReasoningContent : finalTurn.ModelMessage.Content) ?? string.Empty;
    }

    [Test]
    public async Task Model_FixesWholeFileRewriteBug_PlanImplementVerify()
    {
        var fixtureCorePath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core");
        var cancellationToken = TestContext.CurrentContext.CancellationToken;

        var planPrompt = string.Format(PlanUserPromptTemplate, fixtureCorePath);
        var planResult = await RunPhaseAsync(
            "plan", AgentSystemPrompts.CodingAgent, planPrompt, blockMutatingTools: true, turnCap: 15, cancellationToken);

        Assert.That(planResult.Converged, Is.True,
            $"Plan phase did not converge (stopped: {planResult.StopReason}) within {planResult.TurnCount} turns — " +
            $"nothing to feed the implement phase. Transcript: {planResult.TranscriptPath}");

        var planText = ExtractFinalMessage(planResult);
        Assert.That(planText, Is.Not.Empty,
            $"Plan phase converged but produced no readable plan text. Transcript: {planResult.TranscriptPath}");

        var implementPrompt = string.Format(ImplementUserPromptTemplate, fixtureCorePath, planText);
        var implementResult = await RunPhaseAsync(
            "implement", AgentSystemPrompts.CodingAgent, implementPrompt, blockMutatingTools: false, turnCap: 25, cancellationToken);

        Assert.That(implementResult.Converged, Is.True,
            $"Implement phase did not converge (stopped: {implementResult.StopReason}) within {implementResult.TurnCount} turns. " +
            $"Plan transcript: {planResult.TranscriptPath}. Implement transcript: {implementResult.TranscriptPath}");

        var verifyPrompt = string.Format(VerifyUserPromptTemplate, fixtureCorePath);
        var verifyResult = await RunPhaseAsync(
            "verify", AgentSystemPrompts.CodeReviewer, verifyPrompt, blockMutatingTools: true, turnCap: 15, cancellationToken);

        Assert.That(verifyResult.Converged, Is.True,
            $"Verify phase did not converge (stopped: {verifyResult.StopReason}) within {verifyResult.TurnCount} turns. " +
            $"Transcript: {verifyResult.TranscriptPath}");

        var verifyText = ExtractFinalMessage(verifyResult);

        // Exact tag, not sentiment scoring — see AgentSystemPrompts.CodeReviewer's doc comment for
        // why: free-text scoring of words like "correct"/"looks good" is exactly the kind of
        // fragile, model-mood-dependent signal this experiment is trying to get away from.
        var modelApproved = verifyText.Contains("VERIFIED: PASS", StringComparison.Ordinal);

        // Combined gate the user specifically asked for: mechanical correctness (the hard floor,
        // catches broken/wrong-but-approved code) AND the verify phase's own independent judgment
        // (an additional required signal, not a replacement for the mechanical check).
        WholeFileRewriteAgentTests.AssertFixApplied(_fixture, implementResult);
        Assert.That(modelApproved, Is.True,
            $"Model's own verify pass did not report VERIFIED: PASS. Verify transcript: {verifyResult.TranscriptPath}");
    }
}
