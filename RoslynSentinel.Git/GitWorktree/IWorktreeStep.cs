namespace RoslynSentinel.Common.GitWorktree;

/// <summary>
/// The minimal view of "a unit of work that gets its own worktree" that
/// <see cref="GitWorktreeManager"/> and <see cref="IStepBranchStrategy"/> need: just a bare file name,
/// from which the per-step worktree folder and branch name are derived. Exists so the worktree code
/// can live in Common without depending on PlanStepRunner's own step-file type (which stays in
/// RoslynSentinel.Utilities.PlanStepRunner and implements this interface), and so a caller with no
/// step-file concept (a single-shot SubAgent call) can supply a trivial implementation.
/// </summary>
public interface IWorktreeStep
{
    /// <summary>Bare file name, e.g. "01-baseline.md". Its name-without-extension names the worktree folder.</summary>
    string FileName { get; }
}
