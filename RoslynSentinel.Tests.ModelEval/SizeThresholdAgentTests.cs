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
/// Runs the same whole-file-rewrite fix task as <see cref="WholeFileRewriteAgentTests"/> against
/// <see cref="SizeGraduatedReproducer"/> variants of increasing padding-method count, to find where a
/// local model's <c>ApplyDiff</c> success rate drops off as the target file (and thus the diff
/// payload the model must reproduce correctly) grows. Not run by default — sizes and per-size repeat
/// count are read from ROSLYNSENTINEL_MODELEVAL_SIZES / ROSLYNSENTINEL_MODELEVAL_REPEATS so an
/// overnight/background run can sweep a wide range without recompiling. Every run's transcript and a
/// running CSV summary are written to disk so results survive independently of the test's own
/// pass/fail (a size flipping from reliable to unreliable is the expected, useful outcome here, not
/// a bug in this harness).
/// </summary>
[TestFixture]
public class SizeThresholdAgentTests
{
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

        - Don't touch any `UnrelatedMethod*` method in the file — they are explicitly out of scope
          for this task, must all remain present, and must keep their exact original formatting.
        - Don't invent a new helper method or a different fix approach — reuse the
          `ReplaceBlockFormatted` logic as-is; don't rename it or change its behavior.
        - Don't modify `BlockEditHelpers.cs` — it's reference only; leave it exactly as-is.
        - Preserve the original method's behavior (same inputs/outputs) — only the rewrite
          mechanism should change.
        """;

    /// <summary>
    /// A/B variant of <see cref="UserPromptTemplate"/> that splits step 3 into two explicit,
    /// separately-verified sub-steps (copy the helper in first; only then rewire the call) instead
    /// of one combined instruction. Targets the dominant failure mode found by
    /// <c>Model_SizeThresholdSweep</c>: the model calling <c>ReplaceBlockFormatted</c> before it
    /// has actually copied the method's source into <c>BlockConverter.cs</c>, producing a CS0103
    /// that it then either self-corrects (recoverable) or papers over by illegally making the
    /// original helper <c>public</c> (a constraint violation). See
    /// docs/current/blockers/finding_applydiff_size_threshold_local_model.md.
    /// </summary>
    private const string TwoStepUserPromptTemplate = """
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

        3. Bring the helper into `BlockConverter.cs` as its own separate edit, done BEFORE step 4.
           Do not touch `ConvertAbstractClassToInterface` yet.
           - Copy the `ReplaceBlockFormatted` method's full source into `BlockConverter.cs` — this
             class doesn't have it yet, and the original in `BlockEditHelpers.cs` is private, so it
             can't be called cross-file; a local copy is required.
           - Add whatever `using` directive the copied method needs to compile.
           - Verify `BlockConverter.cs` compiles now, with the new method present but not yet
             called from anywhere. It's fine (and expected) for the whole-file-rewrite bug to still
             be there at this point — you haven't fixed it yet, you've only made the helper
             available locally. Don't move on to step 4 until this build succeeds.

        4. Only now, as a second separate edit, rewire the bug fix:
           - Update `ConvertAbstractClassToInterface` so it calls the `ReplaceBlockFormatted`
             method you just copied into this file, instead of `ReformatWholeFile`, producing the
             same edit (still renaming `public abstract class {{className}}` to
             `public interface I{{className}}`), but formatting only that change.
           - Verify this change compiles too, using an MCP tool (you have no terminal). Scope the
             build to just the `ContosoOrders.Core` project rather than the whole solution.

        5. Confirm the fix: re-read `ConvertAbstractClassToInterface` and check that the
           whole-file-rewrite call is gone and the helper is being used instead.

        6. Report what you changed and the verification result.

        ## Constraints

        - Don't touch any `UnrelatedMethod*` method in the file — they are explicitly out of scope
          for this task, must all remain present, and must keep their exact original formatting.
        - Don't invent a new helper method or a different fix approach — reuse the
          `ReplaceBlockFormatted` logic as-is; don't rename it or change its behavior.
        - Don't modify `BlockEditHelpers.cs` — it's reference only; leave it exactly as-is.
        - Preserve the original method's behavior (same inputs/outputs) — only the rewrite
          mechanism should change.
        """;

    // "Refactor" (not "Refactoring") and "Workspace" are the exact mode strings
    // AddRoslynSentinelToolsBasic checks — see WholeFileRewriteAgentTests.cs's ActiveModes comment
    // for why Basic is used instead of Advanced here.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Refactor", "Workspace",
    };

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private LmStudioAgentClient _agentClient = null!;
    private string _runDirectory = null!;
    private int _unrelatedMethodCount;

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
            "SizeThreshold",
            $"n{_unrelatedMethodCount}",
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

        // dotnet test's console logger block-buffers stdout when it's redirected to a file, so
        // ModelAgentRunner's per-turn logging is invisible until the whole test process exits —
        // this file sink writes+flushes independently so a run can be tailed live.
        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(_runDirectory, "agent.log")));

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
        await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, TestContext.CurrentContext.CancellationToken);

        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs"),
            SizeGraduatedReproducer.HelperFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs"),
            SizeGraduatedReproducer.BuildBuggyFileContent(_unrelatedMethodCount),
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "Shape.cs"),
            SizeGraduatedReproducer.TargetAbstractClassFileContent,
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

    /// <summary>
    /// Sweeps ROSLYNSENTINEL_MODELEVAL_SIZES (comma-separated unrelated-method counts, default
    /// "0,5,15,30,60") x ROSLYNSENTINEL_MODELEVAL_REPEATS (default 3) real model runs, appending one
    /// CSV row per run to TestResults/model-eval/SizeThreshold/results.csv as it goes — so a partial
    /// overnight run still leaves usable data if interrupted. Never asserts pass/fail itself (a size
    /// where the model starts failing is the useful signal, not a test bug); only fails if every
    /// single run across every size errored out at the harness level (misconfiguration).
    /// </summary>
    [Test]
    [Explicit("Slow (many real model runs); run manually or via the overnight sweep loop.")]
    public async Task Model_SizeThresholdSweep()
    {
        var sizes = (Environment.GetEnvironmentVariable("ROSLYNSENTINEL_MODELEVAL_SIZES") ?? "0,5,15,30,60")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToList();
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("ROSLYNSENTINEL_MODELEVAL_REPEATS"), out var r) ? r : 3;

        var summaryDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "model-eval", "SizeThreshold");
        Directory.CreateDirectory(summaryDir);
        // "TwoStep" selects TwoStepUserPromptTemplate for the batch-4 A/B experiment (see doc
        // comment on that const); anything else (including unset) uses the original single-step
        // UserPromptTemplate that produced the batch 1-3 data.
        var promptVariant = Environment.GetEnvironmentVariable("ROSLYNSENTINEL_MODELEVAL_PROMPT_VARIANT") ?? "SingleStep";

        var csvPath = Path.Combine(summaryDir, "results.csv");
        if (!File.Exists(csvPath))
        {
            await File.WriteAllTextAsync(csvPath, "timestampUtc,promptVariant,unrelatedMethodCount,fileSizeChars,run,converged,fixCorrect,stopReason,turnCount,applyDiffErrorCount,transcriptPath\n");
        }

        var harnessFailures = 0;
        var totalRuns = 0;

        foreach (var size in sizes)
        {
            _unrelatedMethodCount = size;
            var fileSizeChars = SizeGraduatedReproducer.BuildBuggyFileContent(size).Length;

            for (var i = 0; i < repeats; i++)
            {
                await TearDown();
                await SetUp();
                totalRuns++;

                AgentRunResult? result = null;
                var applyDiffErrorCount = 0;
                var fixCorrect = false;
                try
                {
                    result = await RunOnceAsync(promptVariant, TestContext.CurrentContext.CancellationToken);
                    applyDiffErrorCount = result.Transcript.Turns
                        .SelectMany(t => t.ToolCalls)
                        .Count(tc => tc.ToolName == "ApplyDiff" && tc.IsError);

                    if (result.Converged)
                    {
                        try
                        {
                            AssertFixApplied(result);
                            fixCorrect = true;
                        }
                        catch (AssertionException)
                        {
                            // Converged but produced a wrong/incomplete fix — recorded as fixCorrect=false below.
                        }
                    }
                }
                catch (Exception ex)
                {
                    harnessFailures++;
                    TestContext.Progress.WriteLine($"[size={size} run={i}] Harness-level exception: {ex}");
                }

                var row = string.Join(',', new[]
                {
                    DateTime.UtcNow.ToString("O"),
                    promptVariant,
                    size.ToString(),
                    fileSizeChars.ToString(),
                    i.ToString(),
                    (result?.Converged ?? false).ToString(),
                    fixCorrect.ToString(),
                    result?.StopReason.ToString() ?? "HarnessException",
                    (result?.TurnCount ?? 0).ToString(),
                    applyDiffErrorCount.ToString(),
                    result?.TranscriptPath ?? "",
                });
                await File.AppendAllTextAsync(csvPath, row + "\n");
                TestContext.Progress.WriteLine($"[size={size} chars={fileSizeChars} run={i}] converged={result?.Converged} fixCorrect={fixCorrect} applyDiffErrors={applyDiffErrorCount} stopReason={result?.StopReason}");
            }
        }

        TestContext.Progress.WriteLine($"Sweep complete: {totalRuns} runs across {sizes.Count} sizes. Results: {csvPath}");
        Assert.That(harnessFailures, Is.LessThan(totalRuns), "Every single run failed at the harness level (not a model failure) — check LM Studio reachability/config before trusting these results.");
    }

    private async Task<AgentRunResult> RunOnceAsync(string promptVariant, CancellationToken cancellationToken)
    {
        var runner = new ModelAgentRunner(
            _agentClient, _mcpClient, turnCap: 40, wallClockCap: TimeSpan.FromMinutes(30),
            logger: _host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var template = promptVariant == "TwoStep" ? TwoStepUserPromptTemplate : UserPromptTemplate;
        var userPrompt = string.Format(template, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        return await runner.RunAsync(AgentSystemPrompts.CodingAgent, userPrompt, _runDirectory, cancellationToken);
    }

    private void AssertFixApplied(AgentRunResult result)
    {
        var fixedPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs");
        Assert.That(File.Exists(fixedPath), Is.True, "BlockConverter.cs should still exist after the model's edits.");

        var fixedText = File.ReadAllText(fixedPath);

        // Only the *call* to ReformatWholeFile needs to be gone — see the matching comment in
        // WholeFileRewriteAgentTests.AssertFixApplied for why checking the bare substring
        // (which also matches the now-unused method's own definition) is wrong.
        Assert.That(fixedText, Does.Not.Contain("return ReformatWholeFile("),
            $"ConvertAbstractClassToInterface should no longer call ReformatWholeFile. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("ReplaceBlockFormatted"),
            $"The fix should bring a ReplaceBlockFormatted helper into BlockConverter.cs itself. Transcript: {result.TranscriptPath}");

        var helperPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs");
        var helperText = File.ReadAllText(helperPath);
        Assert.That(helperText, Does.Contain("private static string ReplaceBlockFormatted"),
            $"BlockEditHelpers.cs is reference-only and should be untouched by the model. Transcript: {result.TranscriptPath}");

        for (var i = 0; i < _unrelatedMethodCount; i++)
        {
            Assert.That(fixedText, Does.Contain($"return $\"unrelated-{i}-{{value}}\";"),
                $"UnrelatedMethod{i} should be present with its original body (byte-for-byte). Transcript: {result.TranscriptPath}");
        }

        // Deliberately NOT asserting on transcript-level tool errors here: this method measures
        // whether the FINAL file is correct, not whether the model made a mistake en route. A
        // failed ApplyDiff attempt followed by a successful self-correction (observed repeatedly
        // at larger sizes — the model tries to call the helper before copying it in, gets a
        // CS0103, rereads, and fixes it) is exactly the size-correlated reliability signal this
        // sweep exists to measure via applyDiffErrorCount in the CSV — it must not also flip
        // fixCorrect to false, or "eventually got it right" and "never made a mistake" collapse
        // into the same bucket and the threshold data becomes meaningless.
        //
        // The helperText check above is a different thing and stays: it's not about whether a
        // mistake happened, it's a final-state invariant (BlockEditHelpers.cs must be untouched).
        // It has already caught a real failure mode distinct from the CS0103-and-recover pattern —
        // the model reverting the helper to `public` and calling it cross-file instead of copying
        // it in, papering over the same mistake by breaking an explicit constraint instead of
        // fixing it. See docs/current/blockers/finding_applydiff_size_threshold_local_model.md.
    }
}
