using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.ModelEval.AgentLoop;

namespace RoslynSentinel.Tools.PlanStepRunner;

/// <summary>
/// Drives the plan-eval-defect-remediation-v2-steps step files one at a time, each in its own
/// fresh git worktree, against a real LM Studio model — the automated version of manually
/// starting a new LM Studio chat per step with "Load the solution. Review &lt;step&gt;.md.
/// Implement the plan."
///
/// Each step: create a worktree off the running branch tip, `dotnet build` that worktree's own
/// RoslynSentinel.Server.Advanced (never build.ps1 — see DotnetProcess's remarks on why), launch
/// it over stdio, LoadSolution, run the step's prompt to convergence via ModelAgentRunner, log a
/// build+test snapshot, then either commit-and-advance or halt and leave the worktree for
/// inspection.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        LlmOptions.Configure(args);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Console.Error.WriteLine(
                "The LLM model must be set via --llm-model or ROSLYNSENTINEL_LLM_MODEL.");
            return 1;
        }

        RunnerOptions options;
        try
        {
            options = RunnerOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var stepFiles = PlanStepFile.LoadRange(options.PlanDir, options.StartStep, options.EndStep);
        Console.WriteLine($"Running {stepFiles.Count} step(s): {string.Join(", ", stepFiles.Select(s => s.FileName))}");

        var batchDir = Path.Combine(options.OutDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(batchDir);
        Console.WriteLine($"Batch output: {batchDir}");

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600)),
        };

        var git = new GitWorktreeManager(options.SourceRepo, options.Branch, options.WorktreeRoot);
        git.EnsureBranchExists();

        foreach (var step in stepFiles)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Step {step.FileName} ===");

            var worktreePath = git.CreateWorktree(step.FileName);
            Console.WriteLine($"Worktree: {worktreePath}");

            var stepDir = Path.Combine(batchDir, Path.GetFileNameWithoutExtension(step.FileName));
            Directory.CreateDirectory(stepDir);

            using var loggerFactory = LoggerFactory.Create(b =>
            {
                b.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                });
                b.AddProvider(new FlushingFileLoggerProvider(Path.Combine(stepDir, "agent.log")));
            });
            var agentClient = new LmStudioAgentClient(httpClient, loggerFactory.CreateLogger<LmStudioAgentClient>());

            try
            {
                var outcome = await RunStepAsync(step, worktreePath, agentClient, options, loggerFactory, stepDir);
                LogSummary(step, outcome);

                var buildOptional = KnownBuildOptionalSteps.Contains(step.FileName);
                var buildOk = outcome.BuildErrorCount == 0;
                var shouldAdvance = outcome.Converged && !outcome.LooksBlocked && (buildOk || buildOptional);

                if (!shouldAdvance)
                {
                    Console.WriteLine($"HALTING before commit — inspect the worktree at {worktreePath}");
                    Console.WriteLine($"  converged={outcome.Converged} blocked={outcome.LooksBlocked} buildErrors={outcome.BuildErrorCount} buildOptional={buildOptional}");
                    return 1;
                }

                git.CommitWorktree(worktreePath, $"Plan step {step.FileName}");
                git.RemoveWorktree(worktreePath);
            }
            catch (Exception)
            {
                Console.WriteLine($"HALTING on exception — worktree left in place at {worktreePath}");
                throw;
            }
        }

        Console.WriteLine();
        Console.WriteLine("All requested steps completed.");
        return 0;
    }

    private static async Task<StepOutcome> RunStepAsync(
        PlanStepFile step,
        string worktreePath,
        LmStudioAgentClient agentClient,
        RunnerOptions options,
        ILoggerFactory loggerFactory,
        string stepDir)
    {
        var serverBinDir = Path.Combine(worktreePath, "bin-runner", "Advanced");
        await DotnetProcess.BuildAsync(
            Path.Combine(worktreePath, "RoslynSentinel.Server.Advanced", "RoslynSentinel.Server.Advanced.csproj"),
            serverBinDir);

        var serverExe = Path.Combine(serverBinDir, "RoslynSentinel.Server.Advanced.exe");
        var solutionPath = Path.Combine(worktreePath, "RoslynSentinel.slnx");

        // --transport is omitted deliberately (defaults to stdio) and --solution is likewise
        // omitted — Server.Advanced's own auto-load on --solution is fire-and-forget/unawaited
        // (see WarmupAndAutoLoadAdvanced), so it can't be trusted to have finished before the
        // first tool call arrives. LoadSolution is called explicitly below instead, which blocks
        // until the real load completes.
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "RoslynSentinel.Server.Advanced",
            Command = serverExe,
            WorkingDirectory = worktreePath,
        });

        await using var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);

        await mcpClient.CallToolAsync(
            "LoadSolution",
            new Dictionary<string, object?>
            {
                ["reason"] = "PlanStepRunner: loading solution before running the model's step.",
                ["solutionPath"] = solutionPath,
            },
            progress: null, options: null, cancellationToken: CancellationToken.None);

        var runner = new ModelAgentRunner(
            agentClient, mcpClient,
            turnCap: options.TurnCap,
            wallClockCap: TimeSpan.FromMinutes(options.WallClockCapMinutes),
            logger: loggerFactory.CreateLogger<ModelAgentRunner>());

        var userPrompt =
            "The solution is already loaded — do not call LoadSolution or ListWorkspaceSolutions, " +
            "go straight to reading/editing.\n" +
            $"Review the planning doc `{step.FilePath}`.\n" +
            "Implement the plan.";

        var result = await runner.RunAsync(AgentSystemPrompts.CodingAgent, userPrompt, stepDir, CancellationToken.None);

        var lastContent = result.Transcript.Turns.Count > 0
            ? result.Transcript.Turns[^1].ModelMessage.Content ?? ""
            : "";
        var looksBlocked = BlockedPhrases.Any(p => lastContent.Contains(p, StringComparison.OrdinalIgnoreCase));

        var buildResult = await mcpClient.CallToolAsync(
            "Build",
            new Dictionary<string, object?>
            {
                ["reason"] = "PlanStepRunner: post-step build snapshot.",
                ["level"] = "fullBuild",
            },
            progress: null, options: null, cancellationToken: CancellationToken.None);
        var buildText = ToolResultText(buildResult);
        var buildErrorCount = ExtractIntField(buildText, "\"errorCount\"") ?? (ContainsFailureMarker(buildText) ? 1 : 0);

        var testResult = await mcpClient.CallToolAsync(
            "RunTest",
            new Dictionary<string, object?>
            {
                ["reason"] = "PlanStepRunner: post-step test snapshot.",
            },
            progress: null, options: null, cancellationToken: CancellationToken.None);
        var testText = ToolResultText(testResult);

        return new StepOutcome(
            result.Converged, result.StopReason.ToString(), result.TurnCount, looksBlocked,
            buildErrorCount, testText, stepDir);
    }

    private static readonly string[] BlockedPhrases =
    [
        "cannot complete", "can't complete", "unable to complete", "i am blocked", "i'm blocked",
        "cannot find", "could not find", "unable to proceed", "cannot proceed",
    ];

    // Originally seeded from commit 5dfbbf8 "Add build-checkpoint instructions at known-broken
    // plan steps" (02-phase1-types.md, 09-phase3-directivekind.md, 12-phase3-diffhunkanalyzer.md
    // under the old, unmerged step numbering). Of those three, only 02-phase1-types.md's own Gate
    // section ever actually left the solution non-compiling on purpose — the other two always
    // required a clean build to advance despite being on this list. 02-phase1-types.md was since
    // merged into 02-phase1-types-and-engine-fix.md, whose own Gate now restores a clean build by
    // the end of the same step (the merge that folded step 1.2's engine fix into step 1.1) — so no
    // current step is build-optional. Every step requires a clean build to advance, since the
    // next step's worktree is built from this one's committed tip. Re-populate this if a future
    // plan revision reintroduces a step whose own Gate section explicitly documents leaving the
    // solution non-compiling on purpose.
    private static readonly HashSet<string> KnownBuildOptionalSteps = new(StringComparer.OrdinalIgnoreCase);

    private static void LogSummary(PlanStepFile step, StepOutcome outcome)
    {
        Console.WriteLine(
            $"[{step.FileName}] converged={outcome.Converged} stopReason={outcome.StopReason} " +
            $"turns={outcome.TurnCount} blocked={outcome.LooksBlocked} buildErrors={outcome.BuildErrorCount}");
        Console.WriteLine($"[{step.FileName}] transcript: {outcome.TranscriptDir}");
    }

    private static string ToolResultText(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text).FirstOrDefault() ?? "";

    private static bool ContainsFailureMarker(string json) =>
        json.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase);

    private static int? ExtractIntField(string json, string fieldNameWithQuotes)
    {
        var idx = json.IndexOf(fieldNameWithQuotes, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var colonIdx = json.IndexOf(':', idx);
        if (colonIdx < 0)
        {
            return null;
        }

        var numStart = colonIdx + 1;
        while (numStart < json.Length && (json[numStart] == ' ' || json[numStart] == '\t'))
        {
            numStart++;
        }

        var numEnd = numStart;
        while (numEnd < json.Length && char.IsDigit(json[numEnd]))
        {
            numEnd++;
        }

        return numEnd > numStart && int.TryParse(json[numStart..numEnd], out var value) ? value : null;
    }
}

internal sealed record StepOutcome(
    bool Converged, string StopReason, int TurnCount, bool LooksBlocked,
    int BuildErrorCount, string TestSnapshotJson, string TranscriptDir);
