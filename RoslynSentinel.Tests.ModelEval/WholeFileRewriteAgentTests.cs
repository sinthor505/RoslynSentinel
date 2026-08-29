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
        `{0}/FixtureHelpers/BlockEditHelpers.cs`. Your job is to apply that same fix pattern to
        `ConvertAbstractClassToInterface` in `BlockConverter.cs`.

        ## Steps

        1. Read `ConvertAbstractClassToInterface` in `{0}/FixtureHelpers/BlockConverter.cs`.
           Identify the exact call responsible for the whole-file-rewrite bug described above.

        2. Find the existing fix pattern already used elsewhere in this codebase for this same bug.
           Look at `{0}/FixtureHelpers/BlockEditHelpers.cs` — there is a public helper method there
           that solves exactly this problem by rewriting only the changed block instead of the
           whole file. Locate it and read its full source.

        3. Apply the same fix to `ConvertAbstractClassToInterface`:
           - Call the existing helper (`BlockEditHelpers.ReplaceBlockFormatted`) instead of
             `ReformatWholeFile`.
           - Update `ConvertAbstractClassToInterface` so it produces the same edit (still renaming
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
        - Don't invent a new helper method or a different fix approach — reuse
          `BlockEditHelpers.ReplaceBlockFormatted` as-is; don't rename it or change its behavior.
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
            client.Timeout = TimeSpan.FromSeconds(LlmOptions.TimeoutSeconds * 4);
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
        var runner = new ModelAgentRunner(_agentClient, _mcpClient, turnCap: 25, wallClockCap: TimeSpan.FromMinutes(10));
        var userPrompt = string.Format(UserPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        return await runner.RunAsync(SystemPrompt, userPrompt, _runDirectory, cancellationToken);
    }

    private void AssertFixApplied(AgentRunResult result)
    {
        var fixedPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs");
        Assert.That(File.Exists(fixedPath), Is.True, "BlockConverter.cs should still exist after the model's edits.");

        var fixedText = File.ReadAllText(fixedPath);

        Assert.That(fixedText, Does.Not.Contain("ReformatWholeFile("),
            $"The whole-file-rewrite call should be gone. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("ReplaceBlockFormatted"),
            $"The fix should call the existing BlockEditHelpers.ReplaceBlockFormatted helper. Transcript: {result.TranscriptPath}");

        // Unrelated methods must be byte-for-byte untouched — this is the actual bug signature
        // (whole-file reformat silently reindents code the model never meant to touch).
        Assert.That(fixedText, Does.Contain("public string UnrelatedMethodBefore( int    x , int y )"),
            $"UnrelatedMethodBefore's original (oddly-spaced) formatting should be untouched. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("public string UnrelatedMethodAfter(  string   s  )"),
            $"UnrelatedMethodAfter's original (oddly-spaced) formatting should be untouched. Transcript: {result.TranscriptPath}");

        var noErrorTools = result.Transcript.Turns.SelectMany(t => t.ToolCalls).Where(tc => tc.IsError).ToList();
        Assert.That(noErrorTools, Is.Empty,
            $"Expected no tool call to report failure (checking both IsError and body 'success' fields); " +
            $"{noErrorTools.Count} did: [{string.Join(", ", noErrorTools.Select(tc => tc.ToolName))}]. Transcript: {result.TranscriptPath}");
    }
}
