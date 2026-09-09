namespace RoslynSentinel.Tools.PlanStepRunner;

/// <summary>Parsed --plan-runner-* command-line options (kept separate from LlmOptions' --llm-* flags it shares the argv with).</summary>
public sealed class RunnerOptions
{
    public required string PlanDir { get; init; }
    public required string SourceRepo { get; init; }
    public required string Branch { get; init; }
    public required string RunDir { get; init; }
    public required int StartStep { get; init; }
    public required int EndStep { get; init; }
    public required int TurnCap { get; init; }
    public required int WallClockCapMinutes { get; init; }
    public required string IncludeTools { get; init; }
    public required bool Clean { get; init; }

    public static RunnerOptions Parse(string[] args)
    {
        var planDir = GetArg(args, "--plan-dir")
            ?? throw new ArgumentException("--plan-dir is required (path to plan-eval-defect-remediation-v2-steps-runner — " +
                "the runner-specific copy whose prompts omit the load-solution step the runner already does itself).");
        var sourceRepo = GetArg(args, "--repo")
            ?? throw new ArgumentException("--repo is required (the git repo to branch/worktree from, e.g. the RoslynSentinel checkout).");

        var branch = GetArg(args, "--branch") ?? "eval-defect-remediation-v2-auto";
        var runDir = GetArg(args, "--run-dir")
            ?? Path.Combine(sourceRepo, "PlanStepRunner", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        var startStep = int.TryParse(GetArg(args, "--start-step"), out var s) ? s : 0;
        var endStep = int.TryParse(GetArg(args, "--end-step"), out var e) ? e : int.MaxValue;
        var turnCap = int.TryParse(GetArg(args, "--turn-cap"), out var tc) ? tc : 40;
        var wallClockCapMinutes = int.TryParse(GetArg(args, "--wall-clock-cap-minutes"), out var wc) ? wc : 30;
        var includeTools = GetArg(args, "--include-tools")
            ?? "SentinelWorkspaceTools,SentinelSymbolTools,SentinelRefactoringTools,SentinelDocumentationTools,SentinelCommentingTools,SentinelAdvancedRefactoringTools";
        var clean = HasFlag(args, "--clean");

        if (startStep > endStep)
        {
            throw new ArgumentException($"--start-step ({startStep}) must be <= --end-step ({endStep}).");
        }

        return new RunnerOptions
        {
            PlanDir = planDir,
            SourceRepo = sourceRepo,
            Branch = branch,
            RunDir = runDir,
            StartStep = startStep,
            EndStep = endStep,
            TurnCap = turnCap,
            WallClockCapMinutes = wallClockCapMinutes,
            IncludeTools = includeTools,
            Clean = clean,
        };
    }

    private static string? GetArg(string[] args, string flag)
    {
        var inlinePrefix = flag + "=";
        var inline = args.FirstOrDefault(a => a.StartsWith(inlinePrefix, StringComparison.Ordinal));
        if (inline is not null)
        {
            return inline[inlinePrefix.Length..];
        }

        var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.Ordinal) || a.Equals(flag + "=true", StringComparison.OrdinalIgnoreCase));
}
