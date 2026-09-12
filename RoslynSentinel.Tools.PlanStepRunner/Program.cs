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

        Directory.CreateDirectory(options.RunDir);
        Console.WriteLine($"Run directory: {options.RunDir}");

        // RunDir's own leaf is already the run's de facto human identifier throughout memory/docs
        // (e.g. "run 20260910-013550-398"), so it's reused as RunId rather than minting a second,
        // unrelated id for the same run.
        var runId = Path.GetFileName(options.RunDir);

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600)),
        };

        IStepBranchStrategy branchStrategy = options.BranchMode == BranchMode.Stacked
            ? new StackedBranchStrategy(options.Branch, "HEAD", stepFiles)
            : new SharedBranchStrategy(options.Branch, "HEAD");
        var git = new GitWorktreeManager(options.SourceRepo, branchStrategy, options.RunDir);

        foreach (var step in stepFiles)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Step {step.FileName} ===");

            git.EnsureBranchExists(step);

            if (options.Clean)
            {
                git.RemoveWorktreeIfExists(step.FileName);
            }

            var worktreePath = git.CreateWorktree(step);
            Console.WriteLine($"Worktree: {worktreePath}");

            var stepId = Path.GetFileNameWithoutExtension(step.FileName);
            var stepDir = Path.Combine(options.RunDir, stepId, "Logs");
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
                var outcome = await RunStepAsync(step, worktreePath, git, agentClient, options, loggerFactory, stepDir, runId, stepId);
                LogSummary(step, outcome);

                var buildOk = outcome.BuildErrorCount == 0;
                var shouldAdvance = outcome.Converged && !outcome.LooksBlocked && (buildOk || step.BuildOptional);

                if (!shouldAdvance)
                {
                    Console.WriteLine($"HALTING before commit — inspect the worktree at {worktreePath}");
                    Console.WriteLine($"  converged={outcome.Converged} blocked={outcome.LooksBlocked} buildErrors={outcome.BuildErrorCount} buildOptional={step.BuildOptional}");
                    return 1;
                }

                // Scope enforcement, deliberately independent of anything the model was told. A
                // read-only step's constraint used to live only in the step's prose, so when run
                // 20260910-013550-398's model never saw that prose it performed two other steps'
                // work and the runner was a turn-cap away from committing it under this step's
                // name — silently corrupting every step that followed.
                if (step.ReadOnly && outcome.TouchedPaths.Count > 0)
                {
                    Console.WriteLine(
                        $"HALTING — read-only step {step.FileName} modified {outcome.TouchedPaths.Count} path(s): " +
                        string.Join(", ", outcome.TouchedPaths));
                    Console.WriteLine($"  worktree left for inspection at {worktreePath}");
                    return 1;
                }

                git.CommitWorktree(worktreePath, $"Plan step {step.FileName}");

                // Cleanup failure after a successful commit isn't worth aborting the run over — the
                // step's work is already safely on the branch. Windows' path-length limit can make
                // this fail on deeply-nested build output; a leftover worktree is harmless (--clean
                // discards it on a later retry).
                var removeError = git.TryRemoveWorktree(worktreePath);
                if (removeError is not null)
                {
                    Console.WriteLine($"Warning: failed to remove worktree at {worktreePath} after commit — leaving it in place.");
                    Console.WriteLine($"  {removeError}");
                }
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
        GitWorktreeManager git,
        LmStudioAgentClient agentClient,
        RunnerOptions options,
        ILoggerFactory loggerFactory,
        string stepDir,
        string runId,
        string stepId)
    {
        var serverBinDir = Path.Combine(worktreePath, "bin-runner", "Advanced");
        await DotnetProcess.BuildAsync(
            Path.Combine(worktreePath, "RoslynSentinel.Server.Advanced", "RoslynSentinel.Server.Advanced.csproj"),
            serverBinDir);

        var serverExe = Path.Combine(serverBinDir, "RoslynSentinel.Server.Advanced.exe");
        var solutionPath = Path.Combine(worktreePath, "RoslynSentinel.slnx");

        // --transport is omitted deliberately (defaults to stdio) — PlanStepRunner is the one
        // calling the MCP tools itself (via ModelAgentRunner), never the model's own LM Studio
        // host directly, so stdio's built-in per-worktree child-process ownership is exactly
        // what's wanted here; no port to coordinate or collide with the user's own separately
        // running server. --solution is likewise omitted — Server.Advanced's own auto-load on
        // --solution is fire-and-forget/unawaited (see WarmupAndAutoLoadAdvanced), so it can't
        // be trusted to have finished before the first tool call arrives. LoadSolution is called
        // explicitly below instead, which blocks until the real load completes.
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "RoslynSentinel.Server.Advanced",
            Command = serverExe,
            // --testing puts ProjectDoc on docs/testing/ instead of docs/. Required here because
            // the worktree is a copy of this very repo, so the runner-specific plan files and the
            // production plans they mirror share basenames; without it ProjectDoc's basename
            // fallback silently answered with the production copy (run 20260910-013550-398 spent
            // all 60 turns implementing a plan it never asked for).
            Arguments = ["--base-repo-dir=" + worktreePath, "--include-tools=" + options.IncludeTools, "--testing", "--log-dir=" + stepDir, "--run-id=" + runId, "--step-id=" + stepId],
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
            // Bail after 3 identical consecutive tool failures rather than letting the turn cap
            // absorb the loop. Run 20260910-013550-398 spent its last 23 turns (~13 minutes)
            // re-issuing one failing ReplaceSnippet call and was reported as TurnCapExceeded.
            repeatedFailureLimit: 3,
            turnCap: options.TurnCap,
            wallClockCap: TimeSpan.FromMinutes(options.WallClockCapMinutes),
            logger: loggerFactory.CreateLogger<ModelAgentRunner>());

        // The step text is inlined into the prompt rather than referenced by path. Handing over a
        // path made the model re-resolve it through ProjectDoc, and in run 20260910-013550-398 that
        // lookup silently answered with a *different* step file — so the model spent the whole run
        // implementing a plan nobody asked for. Passing the content by value removes the lookup,
        // and with it any chance of substitution, regardless of how ProjectDoc resolves names.
        // step.Body already has any frontmatter stripped, so the model sees only the step itself.
        var userPrompt =
            "The solution is already loaded — do not call LoadSolution or ListWorkspaceSolutions, " +
            "go straight to reading/editing.\n" +
            "Implement the plan step below. It is reproduced here in full — do not look for it on " +
            "disk, and do not read any other plan step file.\n\n" +
            $"=== BEGIN PLAN STEP: {step.FileName} ===\n{step.Body}\n=== END PLAN STEP ===";

        var result = await runner.RunAsync(AgentSystemPrompts.CodingAgent, userPrompt, stepDir, CancellationToken.None, runId: runId);

        if (result.RepeatedFailure is { } repeatedFailure)
        {
            WriteBlockerDoc(options, step, repeatedFailure, stepDir);
        }

        var lastContent = result.Transcript.Turns.Count > 0
            ? result.Transcript.Turns[^1].ModelMessage.Content ?? ""
            : "";
        var looksBlocked = BlockedPhrases.Any(p => lastContent.Contains(p, StringComparison.OrdinalIgnoreCase));

        // Read what the model touched *before* the Build/RunTest snapshots below, so the runner's
        // own post-step tooling can't contribute paths that then look like the model's edits.
        var touchedPaths = git.GetDirtyPaths(worktreePath)
            .Where(p => !p.StartsWith(RunnerBuildOutputPrefix, StringComparison.Ordinal))
            .ToList();
        var unmentionedPaths = FindPathsNotMentionedInStep(step, touchedPaths);

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
            buildErrorCount, testText, stepDir, touchedPaths, unmentionedPaths);
    }

    /// <summary>The runner's own build output inside each worktree — never the model's doing.</summary>
    private const string RunnerBuildOutputPrefix = "bin-runner/";

    /// <summary>
    /// Files the step changed but never names. A warning signal only, deliberately never a halt:
    /// a step like "sweep call sites" legitimately edits files it doesn't enumerate, so gating
    /// advancement on this would halt correct runs. It exists to make an off-scope step visible in
    /// the summary instead of being discovered later in whatever it corrupted.
    /// </summary>
    private static List<string> FindPathsNotMentionedInStep(PlanStepFile step, IEnumerable<string> touchedPaths) =>
        touchedPaths
            .Where(p =>
            {
                var stem = Path.GetFileNameWithoutExtension(p);
                return stem.Length > 0 && !step.Body.Contains(stem, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

    /// <summary>
    /// Records a tripped repeated-failure breaker as a blocker doc in the source repo, matching the
    /// convention in docs/current/blockers/. Written by the harness rather than relied upon from
    /// the model: the working agreement is that a tool failure is a blocking finding to be written
    /// up, and run 20260910-013550-398's model simply didn't — it kept retrying instead.
    /// Deliberately targets the source repo, not the worktree, which is removed on success and
    /// otherwise left only for manual inspection.
    /// </summary>
    private static void WriteBlockerDoc(
        RunnerOptions options, PlanStepFile step, RepeatedFailureDetail failure, string stepDir)
    {
        try
        {
            var blockersDir = Path.Combine(options.SourceRepo, "docs", "current", "blockers");
            Directory.CreateDirectory(blockersDir);

            var slug = Slugify($"{failure.ToolName}-{Path.GetFileNameWithoutExtension(step.FileName)}");
            var path = Path.Combine(blockersDir, $"blocking_error_{slug}.md");

            var content =
                $"""
                # Repeated tool failure — `{failure.ToolName}` during plan step {step.FileName}

                **Written automatically by PlanStepRunner** when its repeated-failure breaker tripped.

                **Run directory:** `{options.RunDir}`
                **Transcript:** `{stepDir}`
                **Step:** `{step.FileName}` (readOnly={step.ReadOnly}, buildOptional={step.BuildOptional})

                ## What happened

                `{failure.ToolName}` failed {failure.FailureCount} consecutive times with the same
                failure signature across turns {failure.FirstTurn}–{failure.LastTurn}. The run was
                terminated rather than allowed to consume its remaining turn budget re-issuing the
                same call.

                **Signature:** `{failure.Signature}`

                ## Final failing call

                Arguments:

                ```json
                {failure.ArgumentsJson}
                ```

                Result:

                ```json
                {failure.ResultJson}
                ```

                ## Next steps

                Confirm whether this is a tool defect or a plan-content problem, then either fix the
                tool or amend the step. Delete this file once resolved.

                """;

            File.WriteAllText(path, content);
            Console.WriteLine($"Wrote blocker doc: {path}");
        }
        catch (Exception ex)
        {
            // The blocker doc is diagnostics, not the deliverable — failing to write it must not
            // mask the underlying breaker trip, which the caller reports through StepOutcome.
            Console.WriteLine($"WARNING — could not write blocker doc for {step.FileName}: {ex.Message}");
        }
    }

    private static string Slugify(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    private static readonly string[] BlockedPhrases =
    [
        "cannot complete", "can't complete", "unable to complete", "i am blocked", "i'm blocked",
        "cannot find", "could not find", "unable to proceed", "cannot proceed",
    ];

    // Build-optionality used to live here as a hardcoded name set (seeded from commit 5dfbbf8's
    // build-checkpoint work, and empty by the time it was removed). It now comes from each step's
    // own `buildOptional:` frontmatter, so the flag lives with the step whose Gate section
    // justifies it instead of in a list that silently goes stale when steps are renamed or merged.
    // No current step sets it: every step must build clean to advance, because the next step's
    // worktree is built from this one's committed tip. See PlanStepFile.BuildOptional.

    private static void LogSummary(PlanStepFile step, StepOutcome outcome)
    {
        Console.WriteLine(
            $"[{step.FileName}] converged={outcome.Converged} stopReason={outcome.StopReason} " +
            $"turns={outcome.TurnCount} blocked={outcome.LooksBlocked} buildErrors={outcome.BuildErrorCount} " +
            $"filesTouched={outcome.TouchedPaths.Count}");

        if (outcome.UnmentionedPaths.Count > 0)
        {
            Console.WriteLine(
                $"[{step.FileName}] WARNING — {outcome.UnmentionedPaths.Count} changed file(s) are not " +
                $"named anywhere in the step text: {string.Join(", ", outcome.UnmentionedPaths)}");
            Console.WriteLine(
                $"[{step.FileName}]   (not a halt — a step may legitimately touch call sites it doesn't " +
                "enumerate; review if the step looks off-scope.)");
        }

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

/// <param name="TouchedPaths">
/// Repo-relative paths the model changed, excluding the runner's own build output. Read before the
/// post-step build/test snapshots so it reflects the model's edits alone.
/// </param>
/// <param name="UnmentionedPaths">
/// Subset of <paramref name="TouchedPaths"/> whose file name never appears in the step text — a
/// possible scope violation, reported as a warning only.
/// </param>
internal sealed record StepOutcome(
    bool Converged, string StopReason, int TurnCount, bool LooksBlocked,
    int BuildErrorCount, string TestSnapshotJson, string TranscriptDir,
    IReadOnlyList<string> TouchedPaths, IReadOnlyList<string> UnmentionedPaths);
