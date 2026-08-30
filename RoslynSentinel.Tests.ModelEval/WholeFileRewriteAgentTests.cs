using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Tests.ModelEval.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Drives a real LM Studio model through a real in-process MCP server (same harness construction as
/// RoslynSentinel.Tests.Advanced's McpTasksHarness* tests) against the
/// <see cref="WholeFileRewriteReproducer"/> fixture, replacing the manual copy/paste-into-LM-Studio
/// workflow previously used for plan-9b-model-test-step2.md. Requires an LM Studio server reachable
/// at ROSLYNSENTINEL_LLM_BASE_URL (default http://localhost:1234/v1) with ROSLYNSENTINEL_LLM_MODEL
/// set to a loaded, tool-calling-capable model — tests are skipped (not failed) if LM Studio isn't
/// reachable, since this project exercises a real external model, not a mocked one.
/// </summary>
[TestFixture]
public class WholeFileRewriteAgentTests
{
    private const string SystemPrompt = """
        You have access to RoslynSentinel MCP tools only — no terminal/bash access. Use only those
        tools for every step, including verifying your fix compiles.
        """;

    private const string UserPromptTemplate = """
        # Task: Fix a whole-file-rewrite bug in FixtureHelpers/BlockConverter.cs (level 2)

        This gives you less detail than a fully-scripted plan. You are told *what* to do and
        *which tool* to use, but not the exact parameters or exact code — work those out yourself
        from what the tools return. If a tool call fails, stop and report the exact error message
        rather than guessing around it.

        ## Background

        `{0}/FixtureHelpers/BlockConverter.cs` has a bug: `ConvertAbstractClassToInterface` builds
        its replacement text and then calls a private `ReformatWholeFile` helper that rewrites the
        ENTIRE file's text — not just the part that changed. This silently reformats unrelated code
        in the same file.

        This exact bug was already fixed, using the same fix pattern, in the sibling file
        `{0}/FixtureHelpers/BlockEditHelpers.cs` — but the helper there is private, so
        `BlockConverter.cs` can't call it directly. Your job is to apply that same fix pattern to
        `ConvertAbstractClassToInterface` in `BlockConverter.cs`.

        ## Steps

        1. Read `ConvertAbstractClassToInterface` in `{0}/FixtureHelpers/BlockConverter.cs`.
           Identify the exact call responsible for the whole-file-rewrite bug described above.

        2. Find the existing fix pattern already used elsewhere in this codebase for this same bug.
           Look at `{0}/FixtureHelpers/BlockEditHelpers.cs` — there is a private helper method
           there that solves exactly this problem by rewriting only the changed block instead of
           the whole file. Locate it and read its full source.

        3. Apply the same fix to `ConvertAbstractClassToInterface`:
           - Bring the helper method (`ReplaceBlockFormatted`) into `BlockConverter.cs` — this
             class doesn't have it yet, and the original in `BlockEditHelpers.cs` is private, so
             it can't be called cross-file.
           - Add whatever `using` directive the helper needs to compile.
           - Update `ConvertAbstractClassToInterface` so it uses the helper instead of
             `ReformatWholeFile`, producing the same edit (still renaming
             `public abstract class {{className}}` to `public interface I{{className}}`), but
             formatting only that change.

        4. Verify your change compiles, using an MCP tool (you have no terminal). Scope the build
           to just the `ContosoOrders.Core` project rather than the whole solution.

        5. Confirm the fix: re-read `ConvertAbstractClassToInterface` and check that the
           whole-file-rewrite call is gone and the helper is being used instead.

        6. Report what you changed and the verification result.

        ## Constraints

        - Don't touch `UnrelatedMethodBefore` or `UnrelatedMethodAfter` in the file — they are
          explicitly out of scope for this task.
        - Don't invent a new helper method or a different fix approach — reuse the
          `ReplaceBlockFormatted` logic as-is; don't rename it or change its behavior.
        - Don't modify `BlockEditHelpers.cs` — it's reference only; leave it exactly as-is.
        - Preserve the original method's behavior (same inputs/outputs) — only the rewrite
          mechanism should change.
        """;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Generation", "Refactoring", "Workspace",
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
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = RoslynSentinelTaskTools.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddHttpClient<LmStudioAgentClient>(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            // A slow local GPU (e.g. a GTX 1080) can take several minutes per completion on
            // larger prompts/diffs — floor well above LlmOptions.TimeoutSeconds's default-30s*4
            // so per-turn latency alone never trips the HTTP timeout ahead of the runner's own
            // wall-clock cap.
            client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
        });
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

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

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
    }

    [TearDown]
    public async Task TearDown()
    {
        // SetUp calls Assert.Ignore (throwing) before these are assigned when LM Studio isn't
        // configured — TearDown still runs in that case, so guard rather than NRE.
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
    }

    [Test]
    public async Task Model_FixesWholeFileRewriteBug_UsingExistingHelperPattern()
    {
        var result = await RunOnceAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        AssertFixApplied(result);
    }

    /// <summary>
    /// Runs the same prompt N times against a fresh fixture each time to check how consistently the
    /// model reproduces a correct fix — the "loop for consistency" use case the harness exists for.
    /// Reports a pass-rate summary; do not assert 100% since small local models are not fully
    /// deterministic even at low temperature.
    /// </summary>
    [Test]
    [Explicit("Slow (N real model runs); run manually via `dotnet test --filter ConsistencyCheck`.")]
    public async Task Model_FixesWholeFileRewriteBug_ConsistencyCheck()
    {
        const int runs = 5;
        var passCount = 0;
        var turnCounts = new List<int>();

        for (var i = 0; i < runs; i++)
        {
            if (i > 0)
            {
                await TearDown();
                await SetUp();
            }

            var result = await RunOnceAsync(TestContext.CurrentContext.CancellationToken);
            turnCounts.Add(result.TurnCount);

            if (result.Converged)
            {
                try
                {
                    AssertFixApplied(result);
                    passCount++;
                }
                catch (AssertionException)
                {
                    // Counted as a failed run below; transcript already on disk for inspection.
                }
            }
        }

        TestContext.Out.WriteLine($"Pass rate: {passCount}/{runs}. Turn counts: [{string.Join(", ", turnCounts)}]");
        Assert.That(passCount, Is.GreaterThan(0), $"Model never succeeded across {runs} runs — see per-run transcripts under {_runDirectory}/../");
    }

    private async Task<AgentRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        // Generous caps for a slow local GPU (e.g. a GTX 1080): individual turns have been
        // observed to take 1-2 minutes, so a 10-minute cap can cut off a run mid-recovery
        // before the model genuinely gets stuck. 40 turns / 30 minutes gives real room to
        // either converge or fail on its own rather than on an artificial clock.
        var runner = new ModelAgentRunner(_agentClient, _mcpClient, turnCap: 40, wallClockCap: TimeSpan.FromMinutes(30));
        var userPrompt = string.Format(UserPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        return await runner.RunAsync(SystemPrompt, userPrompt, _runDirectory, cancellationToken);
    }

    private void AssertFixApplied(AgentRunResult result)
    {
        var fixedPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs");
        Assert.That(File.Exists(fixedPath), Is.True, "BlockConverter.cs should still exist after the model's edits.");

        var fixedText = File.ReadAllText(fixedPath);

        // Only the *call* to ReformatWholeFile needs to be gone — the prompt never asks the
        // model to delete the now-unused method definition (matching plan-9b-step2.md step 4,
        // which only asks to stop calling the whole-file rewrite), so leaving
        // "private static string ReformatWholeFile(...)" as dead code is a valid fix, not a
        // failure. Checking for the bare substring here previously false-failed several runs
        // that fixed the bug correctly but left the dead method in place.
        Assert.That(fixedText, Does.Not.Contain("return ReformatWholeFile("),
            $"ConvertAbstractClassToInterface should no longer call ReformatWholeFile. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("ReplaceBlockFormatted"),
            $"The fix should bring a ReplaceBlockFormatted helper into BlockConverter.cs itself " +
            $"(the original in BlockEditHelpers.cs is private and unreachable cross-file). Transcript: {result.TranscriptPath}");

        var helperPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs");
        var helperText = File.ReadAllText(helperPath);
        Assert.That(helperText, Does.Contain("private static string ReplaceBlockFormatted"),
            $"BlockEditHelpers.cs is reference-only and should be untouched by the model. Transcript: {result.TranscriptPath}");

        // Unrelated methods must be byte-for-byte untouched — this is the actual bug signature
        // (whole-file reformat silently reindents code the model never meant to touch).
        Assert.That(fixedText, Does.Contain("public string UnrelatedMethodBefore( int    x , int y )"),
            $"UnrelatedMethodBefore's original (oddly-spaced) formatting should be untouched. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("public string UnrelatedMethodAfter(  string   s  )"),
            $"UnrelatedMethodAfter's original (oddly-spaced) formatting should be untouched. Transcript: {result.TranscriptPath}");

        // Allow one failed tool call: a model that tries an edit, gets a real compiler/validation
        // error back, and self-corrects on its next attempt is the intended agentic loop (see the
        // self-correction-loop research this test's fixture was built to exercise), not a defect.
        // More than one error suggests the model is thrashing rather than converging.
        var errorTools = result.Transcript.Turns.SelectMany(t => t.ToolCalls).Where(tc => tc.IsError).ToList();
        Assert.That(errorTools.Count, Is.LessThanOrEqualTo(1),
            $"Expected at most 1 failed tool call (one self-correction retry); " +
            $"{errorTools.Count} occurred: [{string.Join(", ", errorTools.Select(tc => tc.ToolName))}]. Transcript: {result.TranscriptPath}");
    }
}
