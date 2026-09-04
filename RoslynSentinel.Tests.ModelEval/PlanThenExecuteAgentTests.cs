using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Tests.ModelEval.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Same fixture, prompt ambiguity, and disambiguating sentence as
/// <see cref="WholeFileRewriteAgentTests"/>'s MinimalGuidanceDisambiguated test, but additionally
/// instructs the model to write out its complete plan in prose, with no tool calls, before making its
/// first tool call — tests the "on-the-fly decisions fork 50/50, a committed-up-front plan anchors the
/// model" hypothesis raised alongside project_minimalguidance_reasoning_pattern_analysis. Nothing
/// enforces the instruction server-side (a request filter can't see whether the model's prior turn
/// was prose-only, only individual tool calls in isolation — see PlanOnlyAgentTests's doc comment for
/// why a true two-phase plan-then-execute would need a second full model round-trip instead) — this is
/// a prompt-only nudge, and <see cref="AssertFixApplied"/> additionally records whether the model
/// actually complied (turn 1 had zero tool calls) so compliant vs. non-compliant runs can be compared
/// post-hoc, the same technique used for the reasoning-pattern analysis this test follows up on.
/// </summary>
[TestFixture]
public class PlanThenExecuteAgentTests
{
    // Identical task/ambiguity-closing text to WholeFileRewriteAgentTests's
    // DisambiguatedMinimalGuidanceUserPromptTemplate, with one added instruction: state the full plan
    // before touching any tool. Keeping the rest of the prompt byte-for-byte identical to that test
    // isolates the "plan first" instruction as the only variable between the two.
    private const string PlanThenExecuteUserPromptTemplate = """
        # Task: Fix a bug in FixtureHelpers/BlockConverter.cs

        Users report that editing shapes via `{0}/FixtureHelpers/BlockConverter.cs` sometimes
        changes unrelated formatting elsewhere in the same file, even though they only asked for
        one class to be converted.

        Investigate `BlockConverter.cs`, find the root cause, and fix it. A similar bug was
        already fixed elsewhere in this codebase using a reusable pattern — look for it and reuse
        that same approach rather than inventing a new one.

        If the existing fix lives in a private method in another file, call it directly rather
        than copying its body into your own fix — raise its accessibility (e.g. to `internal`) so
        it can be called cross-file, but don't duplicate its logic, and don't modify anything else
        in that file. Once you switch the buggy method's call site to the shared method, delete
        only that one now-unused old method (the one the bug report is about) instead of leaving
        it behind — do not delete, rename, or otherwise modify any other method, field, or class,
        even ones that look unused, unrelated, or like dead code to you.

        Before making any tool call that edits a file, first write out your complete plan as plain
        text: the root cause, exactly which method(s)/file(s) you will modify, and the specific
        content you will place in `BlockConverter.cs`. Only after stating that plan in full should
        you begin making edit tool calls — do not interleave planning and editing.

        Verify your fix compiles, using an MCP tool (you have no terminal access). Scope the build
        to just the `ContosoOrders.Core` project rather than the whole solution.

        Do not modify any code unrelated to this specific bug, including code that looks unused —
        leave it exactly as you found it. Report what you changed and the verification result.
        """;

    // Same as WholeFileRewriteAgentTests.ActiveModes — full read+write toolset, nothing blocked here;
    // only the prompt differs from that test's MinimalGuidanceDisambiguated variant.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Refactor", "Workspace",
    };

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
                "ROSLYNSENTINEL_LLM_MODEL is not set — model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        _fixture = new RoslynSentinel.Tests.TestSolutionFixture();

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

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

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

        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(_runDirectory, "agent.log")));

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
        await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, TestContext.CurrentContext.CancellationToken);

        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs"),
            WholeFileRewriteReproducer.HelperFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs"),
            WholeFileRewriteReproducer.BuggyFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "Shape.cs"),
            WholeFileRewriteReproducer.TargetAbstractClassFileContent,
            reloadSolution: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _mcpClient = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        _agentClient = _host.Services.GetRequiredService<LmStudioAgentClient>();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _fixture?.Dispose();

        if (_runDirectory is not null)
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(_runDirectory);
        }
    }

    [Test]
    public async Task Model_FixesWholeFileRewriteBug_PlanThenExecute()
    {
        var runner = new ModelAgentRunner(
            _agentClient, _mcpClient, turnCap: 40, wallClockCap: TimeSpan.FromMinutes(30),
            logger: _host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var userPrompt = string.Format(PlanThenExecuteUserPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        var result = await runner.RunAsync(AgentSystemPrompts.CodingAgent, userPrompt, _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        // Not a pass/fail gate — recorded so compliant vs. non-compliant runs can be compared
        // post-hoc against AssertFixApplied's outcome (same technique as
        // project_minimalguidance_reasoning_pattern_analysis), since nothing server-side stops the
        // model from ignoring the "plan before editing" instruction.
        var firstTurn = result.Transcript.Turns.FirstOrDefault();
        var compliedWithPlanFirst = firstTurn is not null && firstTurn.ToolCalls.Count == 0;
        TestContext.Out.WriteLine(compliedWithPlanFirst
            ? "PLAN-FIRST: model's turn 1 had zero tool calls (stated a plan before acting)."
            : "NO PLAN-FIRST: model made a tool call on turn 1 (interleaved planning and editing, or skipped planning).");

        AssertFixApplied(result);
    }

    private void AssertFixApplied(AgentRunResult result)
    {
        var fixedPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs");
        Assert.That(File.Exists(fixedPath), Is.True, "BlockConverter.cs should still exist after the model's edits.");

        var fixedText = File.ReadAllText(fixedPath);

        Assert.That(fixedText, Does.Not.Contain("return ReformatWholeFile("),
            $"ConvertAbstractClassToInterface should no longer call ReformatWholeFile. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Not.Match(@"static\s+string\s+ReformatWholeFile\s*\("),
            $"The old ReformatWholeFile method should be deleted once its call site is replaced, " +
            $"not left behind unused. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("ReplaceBlockFormatted"),
            $"The fix should call ReplaceBlockFormatted (directly or qualified) — bringing it into " +
            $"scope, not duplicating its body. Transcript: {result.TranscriptPath}");

        // See WholeFileRewriteAgentTests.AssertFixApplied for the full rationale: the real-world
        // precedent (commit 8a8963d) was consolidating 52 duplicate call sites onto one shared
        // helper, so exposing and calling ReplaceBlockFormatted is the correct fix here, not
        // duplicating it.
        var helperPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs");
        var helperText = File.ReadAllText(helperPath);
        Assert.That(helperText, Does.Not.Contain("private static string ReplaceBlockFormatted"),
            $"ReplaceBlockFormatted should have its accessibility raised (internal/public) so " +
            $"BlockConverter.cs can call it directly instead of duplicating it. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Not.Match(@"(?:private\s+)?static\s+string\s+ReplaceBlockFormatted\s*\("),
            $"BlockConverter.cs should call the shared ReplaceBlockFormatted, not define its own " +
            $"copy of it. Transcript: {result.TranscriptPath}");

        Assert.That(fixedText, Does.Contain("public string UnrelatedMethodBefore( int    x , int y )"),
            $"UnrelatedMethodBefore's original (oddly-spaced) formatting should be untouched. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("public string UnrelatedMethodAfter(  string   s  )"),
            $"UnrelatedMethodAfter's original (oddly-spaced) formatting should be untouched. Transcript: {result.TranscriptPath}");

        // Threshold is 2, not 1: CompilerErrorLookupHelper's guidance (see RoslynSentinel.Basic
        // CompilerErrorLookupHelper.cs) is designed to be *read after* a failed ApplyDiff, so a
        // model that mis-qualifies a call (e.g. CS0103 on a static member of another class) and
        // then correctly follows the guidance on retry legitimately costs 2 error tool calls, not
        // 1 - observed directly in a 2026-08-31 PlanThenExecute run that fully fixed the bug in 2
        // ApplyDiff attempts but tripped a <=1 gate. 3+ on the same tool is real thrashing, not a
        // single guided recovery — see AgentToolErrorAssertions for why the cap is per-tool, not
        // just total.
        //
        // Total cap raised 2 -> 8 (2026-09-02), matching WholeFileRewriteAgentTests.AssertFixApplied
        // — see docs/current/project_modifymodifier_accessibility_footgun.md: 9/45 (20%) of this
        // variant's runs hit the same cross-tool self-correction false negative (a real error on one
        // tool, a guided retry on a DIFFERENT tool, then any third unrelated benign hiccup tripping
        // a tight total cap even though no single tool ever thrashed).
        // Per-tool cap raised 2 -> 4 (2026-09-04), matching AssertFixApplied — a legitimate
        // multi-step diagnosis (wrong fix -> compiler error -> adjusted fix -> still wrong ->
        // correct fix) can land 3 failed calls on ONE tool without being thrashing.
        AgentToolErrorAssertions.AssertWithinBudget(result, maxTotal: 8, maxPerTool: 4);
    }
}
