namespace RoslynSentinel.Tools.PlanStepRunner;

/// <summary>
/// Decides which git branch a step's worktree checks out, and what that branch is created from the
/// first time it's needed. Kept separate from <see cref="GitWorktreeManager"/> so the two branching
/// shapes below can share every other worktree/commit/cleanup operation instead of duplicating it.
/// </summary>
public interface IStepBranchStrategy
{
    /// <summary>Branch name <see cref="GitWorktreeManager.CreateWorktree"/> checks out for this step.</summary>
    string BranchFor(PlanStepFile step);

    /// <summary>Ref <see cref="BranchFor"/>'s branch is created from, the first time it doesn't already exist.</summary>
    string BaseRefFor(PlanStepFile step);
}

/// <summary>
/// Today's default: every step in the run shares one branch, created once off <paramref name="baseRef"/>
/// and advanced in place by each step's commit. Matches git's one-worktree-per-branch rule as long as
/// only one step runs at a time (the runner's current usage).
/// </summary>
public sealed class SharedBranchStrategy(string branch, string baseRef) : IStepBranchStrategy
{
    public string BranchFor(PlanStepFile step) => branch;

    public string BaseRefFor(PlanStepFile step) => baseRef;
}

/// <summary>
/// Each step gets its own branch, stacked off the previous step's branch tip
/// (baseRef -> 01-baseline -> 02-... -> 03-...). Removes the shared-branch mode's implicit
/// one-worktree-per-branch contention and keeps each step's history separately inspectable/resumable,
/// at the cost of leaving one branch behind per step instead of one per run.
/// </summary>
public sealed class StackedBranchStrategy(
    string branchPrefix,
    string baseRef,
    IReadOnlyList<PlanStepFile> orderedSteps) : IStepBranchStrategy
{
    public string BranchFor(PlanStepFile step) =>
        $"{branchPrefix}/{Path.GetFileNameWithoutExtension(step.FileName)}";

    public string BaseRefFor(PlanStepFile step)
    {
        var index = orderedSteps.ToList().IndexOf(step);
        if (index < 0)
        {
            throw new ArgumentException(
                $"Step '{step.FileName}' is not part of this strategy's ordered step list.", nameof(step));
        }

        return index == 0 ? baseRef : BranchFor(orderedSteps[index - 1]);
    }
}
