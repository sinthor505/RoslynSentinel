using RoslynSentinel.Common;

namespace RoslynSentinel.Utilities.PlanStepRunner;

/// <summary>
/// How each step's worktree branch relates to the others -> see <see cref="IStepBranchStrategy"/>
/// for what each mode actually does.
/// </summary>
public enum BranchMode
{
    /// <summary>All steps share one branch (today's default). --branch names that shared branch.</summary>
    Shared,

    /// <summary>Each step gets its own branch, stacked off the previous step's tip. --branch is used as the branch-name prefix.</summary>
    Stacked,
}

/// <summary>
/// The runner's own command-line options (--plan-dir, --repo, --run-dir, ...), declared as startup settings
/// (kept separate from LlmOptions' --llm-* flags it shares the argv with).
/// </summary>
[StartupSettingGroup("PlanStepRunner")]
public sealed class RunnerOptions
{
    public required string PlanDir
    {
        get; init;
    }
    public required string SourceRepo
    {
        get; init;
    }
    public required string Branch
    {
        get; init;
    }
    public required BranchMode BranchMode
    {
        get; init;
    }
    public required string RunDir
    {
        get; init;
    }
    public required int StartStep
    {
        get; init;
    }
    public required int EndStep
    {
        get; init;
    }
    public required int TurnCap
    {
        get; init;
    }
    public required int WallClockCapMinutes
    {
        get; init;
    }
    public required string IncludeTools
    {
        get; init;
    }
    public required bool Clean
    {
        get; init;
    }

    /// <summary>
    /// Default server tool classes for --include-tools: the classes behind
    /// AgentToolAllowLists.PlanStepRunnerSurface (the model sees only that list; the classes are
    /// exactly those that declare those tools). LoadSolution, Build and RunTest also come from
    /// WorkspaceTools.
    /// </summary>
    public const string DefaultIncludeTools = "WorkspaceTools,WorkspaceReadNavigationTools,WholeFileWriteTools,RefactoringStructuralTools,RefactoringSignatureTools,RefactoringExtractionDocsTools,CoreRefactoringTools";

    // --include-tools is also declared by the server's ServerHostSettings (scope Server); the scopes are disjoint.
    /// <summary>Path to the plan step files directory (required).</summary>
    public static readonly StartupSetting<string?> PlanDirSetting = StartupSetting.String(
        "--plan-dir", null, null,
        "Path to the plan step files directory (the runner-specific copy whose prompts omit the load-solution step).",
        SettingScope.PlanStepRunner);

    /// <summary>The git repo to branch and worktree from (required).</summary>
    public static readonly StartupSetting<string?> SourceRepoSetting = StartupSetting.String(
        "--repo", null, null,
        "The git repo to branch and worktree from.",
        SettingScope.PlanStepRunner);

    /// <summary>Run folder; null means a per-run folder under the scratch root.</summary>
    public static readonly StartupSetting<string?> RunDirSetting = StartupSetting.String(
        "--run-dir", null, null,
        "Run folder; unset means a per-run folder under the scratch root.",
        SettingScope.PlanStepRunner);

    /// <summary>Branch name or prefix; null means a per-run name derived from the run folder.</summary>
    public static readonly StartupSetting<string?> BranchSetting = StartupSetting.String(
        "--branch", null, null,
        "Branch name (shared mode) or branch-name prefix (stacked mode); unset means a per-run name derived from the run folder.",
        SettingScope.PlanStepRunner);

    /// <summary>How each step's worktree branch relates to the others.</summary>
    public static readonly StartupSetting<BranchMode> BranchModeSetting = StartupSetting.Enum(
        "--branch-mode", null, BranchMode.Shared,
        "How step branches relate: shared (one branch for all steps) or stacked (one branch per step, stacked off the previous tip).",
        SettingScope.PlanStepRunner);

    /// <summary>First plan step to run.</summary>
    public static readonly StartupSetting<int> StartStepSetting = StartupSetting.Int(
        "--start-step", null, 0,
        "First plan step number to run.",
        SettingScope.PlanStepRunner);

    /// <summary>Last plan step to run.</summary>
    public static readonly StartupSetting<int> EndStepSetting = StartupSetting.Int(
        "--end-step", null, int.MaxValue,
        "Last plan step number to run.",
        SettingScope.PlanStepRunner);

    /// <summary>Per-step turn cap.</summary>
    public static readonly StartupSetting<int> TurnCapSetting = StartupSetting.Int(
        "--turn-cap", null, 40,
        "Maximum model turns per step.",
        SettingScope.PlanStepRunner);

    /// <summary>Per-step wall-clock cap in minutes.</summary>
    public static readonly StartupSetting<int> WallClockCapMinutesSetting = StartupSetting.Int(
        "--wall-clock-cap-minutes", null, 30,
        "Maximum wall-clock minutes per step.",
        SettingScope.PlanStepRunner);

    /// <summary>Server tool classes the child server exposes to the model.</summary>
    public static readonly StartupSetting<string?> IncludeToolsSetting = StartupSetting.String(
        "--include-tools", null, DefaultIncludeTools,
        "Comma-separated server tool classes the child server exposes to the model.",
        SettingScope.PlanStepRunner);

    /// <summary>Clean flag.</summary>
    public static readonly StartupSetting<bool> CleanSetting = StartupSetting.Bool(
        "--clean", null, false,
        "Clean the run folder before starting.",
        SettingScope.PlanStepRunner);

    public static RunnerOptions Parse(string[] args)
    {
        // Each setting is read statelessly (no registry state touched); every Invalid value is collected
        // and reported in one ArgumentException built with DescribeProblems.
        var parsedArgs = StartupArgs.Parse(args);
        var getEnv = StartupSettings.EnvironmentReader;
        var planDirRead = PlanDirSetting.Read(parsedArgs, getEnv);
        var sourceRepoRead = SourceRepoSetting.Read(parsedArgs, getEnv);
        var runDirRead = RunDirSetting.Read(parsedArgs, getEnv);
        var branchRead = BranchSetting.Read(parsedArgs, getEnv);
        var branchModeRead = BranchModeSetting.Read(parsedArgs, getEnv);
        var startStepRead = StartStepSetting.Read(parsedArgs, getEnv);
        var endStepRead = EndStepSetting.Read(parsedArgs, getEnv);
        var turnCapRead = TurnCapSetting.Read(parsedArgs, getEnv);
        var wallClockCapRead = WallClockCapMinutesSetting.Read(parsedArgs, getEnv);
        var includeToolsRead = IncludeToolsSetting.Read(parsedArgs, getEnv);
        var cleanRead = CleanSetting.Read(parsedArgs, getEnv);

        var problems = new[]
        {
            planDirRead.Problem, sourceRepoRead.Problem, runDirRead.Problem, branchRead.Problem,
            branchModeRead.Problem, startStepRead.Problem, endStepRead.Problem, turnCapRead.Problem,
            wallClockCapRead.Problem, includeToolsRead.Problem, cleanRead.Problem,
        }.OfType<ConfigurationProblem>().ToList();
        if (problems.Count > 0)
        {
            throw new ArgumentException(StartupSettings.DescribeProblems(problems));
        }

        var planDir = planDirRead.Value
            ?? throw new ArgumentException("--plan-dir is required (path to plan-eval-defect-remediation-v2-steps-runner - " +
                "the runner-specific copy whose prompts omit the load-solution step the runner already does itself).");
        var sourceRepo = sourceRepoRead.Value
            ?? throw new ArgumentException("--repo is required (the git repo to branch/worktree from, e.g. the RoslynSentinel checkout).");

        // Defaults to a per-run folder under the scratch root (ROSLYNSENTINEL_SCRATCH_ROOT, else a sibling
        // folder RoslynSentinel-ScratchDir of the repo), never inside the repo: a worktree left under the
        // repo shows up in `git status`/diffs/searches as a spurious branched copy of the repo.
        var runDir = runDirRead.Value
            ?? DefaultRunDir(sourceRepo);

        // Defaults to a per-run branch (named after the run folder's own timestamp) rather than
        // one fixed shared name -> git only allows one worktree to have a given branch checked out
        // at a time, so two runs (or an old halted run left over from a previous invocation)
        // sharing a branch would contend for it even though their run folders are otherwise fully
        // independent. Deriving the default from RunDir's own leaf name means -ExistingRun (which
        // recomputes the same RunDir from a timestamp) naturally resumes onto the same branch too,
        // without needing to pass --branch explicitly.
        var branch = branchRead.Value ?? ("eval-defect-remediation-v2-auto-" + Path.GetFileName(runDir));

        var startStep = startStepRead.Value;
        var endStep = endStepRead.Value;
        if (startStep > endStep)
        {
            throw new ArgumentException($"--start-step ({startStep}) must be <= --end-step ({endStep}).");
        }

        return new RunnerOptions
        {
            PlanDir = planDir,
            SourceRepo = sourceRepo,
            Branch = branch,
            BranchMode = branchModeRead.Value,
            RunDir = runDir,
            StartStep = startStep,
            EndStep = endStep,
            TurnCap = turnCapRead.Value,
            WallClockCapMinutes = wallClockCapRead.Value,
            IncludeTools = includeToolsRead.Value!,
            Clean = cleanRead.Value,
        };
    }

    /// <summary>
    /// Maps the scratch-root failure (a relative ROSLYNSENTINEL_SCRATCH_ROOT, whose message already names the variable)
    /// to ArgumentException so Program.Main prints it and returns 1 instead of an unhandled exception.
    /// </summary>
    private static string DefaultRunDir(string sourceRepo)
    {
        try
        {
            return Path.Combine(ScratchRoot.TestRunsRoot(sourceRepo), "PlanStepRunner", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }
    }
}
