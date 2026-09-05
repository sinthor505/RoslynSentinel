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
    // Standalone fragment (not a full sentence on its own — designed to be spliced into a prompt via
    // {N} in string.Format) clarifying the one case "do not touch unrelated code" left ambiguous:
    // the bug's OWN now-superseded remnants (e.g. a vestigial local variable or an old helper method
    // that only the buggy code path called) are not "unrelated" just because they look unused after
    // the fix. Observed 2026-09-04: 4/5 passing runs in a batch left a dead `var rewritten =
    // fileText.Replace(...)` line (2/5 also left ReformatWholeFile itself unused) — AssertFixApplied
    // never required its removal, and deliberately so, per that method's own comment: leaving
    // ReformatWholeFile as dead code was previously judged a VALID fix (real-world precedent commit
    // 8a8963d). This fragment is therefore advisory only — added to PlanImplementVerifyAgentTests'
    // prompts as an exploratory wording change, NOT paired with any new mechanical assertion, and NOT
    // added to WholeFileRewriteAgentTests' prompts/AssertFixApplied, which keep tolerating dead code
    // by design. Kept as its own field, not folded into the templates directly, so either phase's
    // prompt can include or omit it independently (pass it into that phase's string.Format call, or
    // pass "" to omit) without duplicating the surrounding prompt text.
    private const string DeadCodeCleanupGuidance =
        "This is different from code that becomes unused BECAUSE of your fix — e.g. a local " +
        "variable that no longer has any use once you change what a method returns, or a private " +
        "helper method that only the old, buggy code path called. That code is part of the bug you " +
        "are fixing, not unrelated to it: remove it as part of this same change rather than leaving " +
        "dead code behind.";

    // Same task framing as PlanOnlyAgentTests.PlanOnlyUserPromptTemplate — kept as a separate copy
    // here (rather than a cross-file reference) since this phase's caller lives in a different test
    // class and the two prompts are free to diverge if either phase's test needs tuning without
    // affecting the other.
    private const string PlanUserPromptTemplate = """
        # Task: Plan a fix for a bug in FixtureHelpers/BlockConverter.cs

        The solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution, go
        straight to ReadFile/SearchSolutionText/ListAll on the path below.

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

        If the reusable pattern is a "find and replace a block, re-indenting only that block"
        helper: locate that helper and call it directly, exactly once, passing it the original,
        unmodified text plus the old and new block content. Leave the input text itself unmodified
        before calling the helper — the helper is the only thing that should ever change it. It
        needs the old block content to still be present in the text you pass it, so it can find
        and replace it.

        You do NOT have access to any file-editing tool in this session (ApplyDiff, CreateFile,
        DeleteFile, ChangeAccessibility, and ModifyModifier are all unavailable and will return an
        error if called) — this is a planning exercise only. Do not attempt to make the change.

        Respond with your plan: the root cause, exactly which method(s)/file(s) you would touch,
        and the specific content you would place in `BlockConverter.cs` (write out the actual code
        you'd use for the reused pattern, not just a description of it). Do not touch code unrelated
        to the bug. {1}
        """;

    // Splices the PREVIOUS PHASE'S OWN plan text (produced by this same model, moments earlier, in
    // a separate context) into an instruction shaped like WholeFileRewriteAgentTests's
    // ScriptedPlanUserPromptTemplate — the deliberate experimental difference from that test is that
    // the plan here is whatever the model itself came up with, not a hand-picked known-correct one.
    private const string ImplementUserPromptTemplate = """
        # Task: Fix a bug in FixtureHelpers/BlockConverter.cs

        The solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution, go
        straight to ReadFile/SearchSolutionText/ListAll on the path below.

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
        ones that look unused or unrelated — leave everything else exactly as you found it. {2}

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

        The solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution, go
        straight to ReadFile/SearchSolutionText/ListAll on the path below.

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
           scoped to the `ContosoOrders.Core` project — do not take it on faith. Use the cheapest
           build level that answers this (a quick build of just that one project), not a full or
           solution-wide build — this task only needs a yes/no compile signal, not a full
           rebuild, and an expensive build can eat your whole time budget for no benefit.

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

    // The exact set of tools actually called across 20 real PlanImplementVerify transcripts
    // against this fixture (58 ReadFile, 21 ApplyDiff, 18 SearchSolutionText, 15 Build, 4
    // LoadSolution, 4 ListWorkspaceSolutions, 3 ListAll, 3 ChangeAccessibility, 1 UsingDirective,
    // 1 ModifyModifier, 1 ListSolutionItems — 2026-09-05 analysis). Used only when
    // LlmOptions.MinimalToolSchema is set, to narrow the advertised tools/list schema down from
    // ActiveModes' full 48 tools to just these 11 — see project_granite42_8b_tool_schema_size_isolated
    // for why schema size itself (not context growth or task difficulty) is the latency driver
    // this is meant to test. Not a permanent restriction: this list reflects what qwen3.5-9b-coder
    // happened to use on this one fixture, not a general-purpose minimal toolset.
    private static readonly HashSet<string> MinimalToolNames = new(StringComparer.Ordinal)
    {
        "ReadFile", "ApplyDiff", "SearchSolutionText", "Build", "LoadSolution",
        "ListWorkspaceSolutions", "ListAll", "ChangeAccessibility", "UsingDirective",
        "ModifyModifier", "ListSolutionItems",
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
        // Deliberately NOT disposed: PersistentWorkspaceManager.Dispose() tears down its debounce
        // timer/semaphore synchronously, and a FileSystemWatcher event from the writes below can
        // still have OnDebounceTimerElapsed in flight on the thread pool at that point — disposing
        // immediately after the last write crashes the whole test host with an
        // ObjectDisposedException on the semaphore (observed on a real run). Left to be GC'd once
        // this method returns instead; nothing else in the test references it.
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
    }

    [TearDown]
    public void TearDown()
    {
        _fixture?.Dispose();

        if (_runDirectory is not null)
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(_runDirectory);
        }
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

        if (blockMutatingTools || LlmOptions.MinimalToolSchema)
        {
            mcpBuilder.WithRequestFilters(filters =>
            {
                if (blockMutatingTools)
                {
                    // Same shape as PlanOnlyAgentTests's filter: intercepts a blocked tool call and
                    // returns a readable error instead of forwarding it, so the model can reason
                    // about why it failed rather than hitting an unhandled protocol exception.
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
                }

                if (LlmOptions.MinimalToolSchema)
                {
                    // Narrows the advertised tools/list schema to MinimalToolNames regardless of
                    // phase (plan/implement/verify all get the same reduced schema) — this changes
                    // what the model SEES, unlike the call-blocking filter above which changes what
                    // it's ALLOWED to invoke. Both can be active together: a phase can advertise
                    // only 11 tools and still have some of those 11 blocked from executing.
                    filters.AddListToolsFilter(next => async (context, ct) =>
                    {
                        var result = await next(context, ct);
                        return new ListToolsResult
                        {
                            Tools = result.Tools.Where(t => MinimalToolNames.Contains(t.Name)).ToList(),
                        };
                    });
                }
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

            // 10-minute wall-clock cap per phase (raised from 5, 2026-09-02: see
            // docs/current/project_planimplementverify_phase_transition_gap.md — 5/39 implement-phase
            // runs stumbled on a wrong/guessed workspace path early, self-corrected, but then still
            // ran out the tighter budget before applying+verifying a fix, something the single-call
            // variants' much larger budget absorbs comfortably). Turn cap is just a safety ceiling
            // underneath that, not the real constraint.
            var runner = new ModelAgentRunner(
                agentClient, mcpClient, turnCap: turnCap, wallClockCap: TimeSpan.FromMinutes(10),
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

    // Observed 2026-09-04: a model that never reaches a real conclusion can keep "reasoning" until
    // cut off, re-deriving and re-stating the same paragraph verbatim dozens of times before
    // truncating mid-sentence — ExtractFinalMessage's ReasoningContent fallback has no way to tell
    // that apart from a genuine (if verbose) plan, and happily spliced 43KB of one such loop into
    // the next phase's prompt. Paragraph-level exact-duplicate detection catches this cheaply: a
    // real plan or verdict doesn't repeat whole paragraphs, a stuck model reliably does.
    private static bool LooksLikeRepetitionLoop(string text)
    {
        var paragraphs = text
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length >= 80) // skip short lines/headers, which legitimately repeat (e.g. "## Constraints")
            .ToList();
        if (paragraphs.Count < 4)
        {
            return false;
        }

        var duplicateCount = paragraphs.Count - paragraphs.Distinct(StringComparer.Ordinal).Count();
        return duplicateCount >= 3;
    }

    [Test]
    public async Task Model_FixesWholeFileRewriteBug_PlanImplementVerify()
    {
        var fixtureCorePath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core");
        var cancellationToken = TestContext.CurrentContext.CancellationToken;

        var planPrompt = string.Format(PlanUserPromptTemplate, fixtureCorePath, DeadCodeCleanupGuidance);
        var planResult = await RunPhaseAsync(
            "plan", AgentSystemPrompts.CodingAgent, planPrompt, blockMutatingTools: true, turnCap: 15, cancellationToken);

        Assert.That(planResult.Converged, Is.True,
            $"Plan phase did not converge (stopped: {planResult.StopReason}) within {planResult.TurnCount} turns — " +
            $"nothing to feed the implement phase. Transcript: {planResult.TranscriptPath}");

        var planText = ExtractFinalMessage(planResult);
        Assert.That(planText, Is.Not.Empty,
            $"Plan phase converged but produced no readable plan text. Transcript: {planResult.TranscriptPath}");
        Assert.That(LooksLikeRepetitionLoop(planText), Is.False,
            $"Plan phase produced a repetition loop, not a usable plan (the model re-stated the same " +
            $"paragraph verbatim instead of concluding) — refusing to splice this into the implement " +
            $"prompt. Transcript: {planResult.TranscriptPath}");

        var implementPrompt = string.Format(ImplementUserPromptTemplate, fixtureCorePath, planText, DeadCodeCleanupGuidance);
        // Turn cap raised 25 -> 35 alongside the 5->10 min wall-clock cap (2026-09-02, see
        // docs/current/project_planimplementverify_phase_transition_gap.md): 2 of the 5 known
        // wrong-workspace-stumble runs stopped on TurnCapExceeded specifically, not just
        // WallClockCapExceeded, so raising only the time budget would have left those runs still
        // capped out on turns before finishing.
        var implementResult = await RunPhaseAsync(
            "implement", AgentSystemPrompts.CodingAgent, implementPrompt, blockMutatingTools: false, turnCap: 35, cancellationToken);

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
        Assert.That(LooksLikeRepetitionLoop(verifyText), Is.False,
            $"Verify phase produced a repetition loop, not a real verdict (the model re-stated the same " +
            $"paragraph verbatim instead of concluding) — its text cannot be trusted to score for " +
            $"VERIFIED: PASS/FAIL. Transcript: {verifyResult.TranscriptPath}");

        // Exact tag, not sentiment scoring — see AgentSystemPrompts.CodeReviewer's doc comment for
        // why: free-text scoring of words like "correct"/"looks good" is exactly the kind of
        // fragile, model-mood-dependent signal this experiment is trying to get away from.
        var modelApproved = verifyText.Contains("VERIFIED: PASS", StringComparison.Ordinal);

        // Combined gate the user specifically asked for: mechanical correctness (the hard floor,
        // catches broken/wrong-but-approved code) AND the verify phase's own independent judgment
        // (an additional required signal, not a replacement for the mechanical check).
        await WholeFileRewriteAgentTests.AssertFixApplied(_fixture, implementResult, TestContext.CurrentContext.CancellationToken);
        Assert.That(modelApproved, Is.True,
            $"Model's own verify pass did not report VERIFIED: PASS. Verify transcript: {verifyResult.TranscriptPath}");
    }
}
