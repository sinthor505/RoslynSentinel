namespace RoslynSentinel.Common;

/// <summary>
/// Reads which multi-step git operation a repository is in the middle of from the repository's own state files.
/// Shared by <see cref="CliGitReader"/>, <see cref="Libgit2GitReader"/> and the Git tool so all of them answer from the
/// same checks; only how the git directory is found differs (<c>rev-parse --absolute-git-dir</c> or libgit2).
/// </summary>
internal static class GitOperationState
{
    /// <summary>
    /// "rebase" (rebase-merge/ or rebase-apply/), "am" (rebase-apply/applying), "cherry-pick" (CHERRY_PICK_HEAD),
    /// "revert" (REVERT_HEAD) or "merge" (MERGE_HEAD); null when nothing is in progress or the directory is missing.
    /// Rebase is checked first because a conflicted rebase also writes CHERRY_PICK_HEAD. <paramref name="gitDirectory"/>
    /// must be the per-worktree git directory (for a linked worktree, where <c>.git</c> is a file, the directory it points
    /// to), which is what <c>git rev-parse --absolute-git-dir</c> prints and libgit2's <c>Repository.Info.Path</c> holds.
    /// </summary>
    internal static string? Detect(string gitDirectory)
    {
        if (gitDirectory.Length == 0 || !Directory.Exists(gitDirectory))
        {
            return null;
        }

        if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")))
        {
            return "rebase";
        }

        if (Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")))
        {
            return File.Exists(Path.Combine(gitDirectory, "rebase-apply", "applying")) ? "am" : "rebase";
        }

        if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD")))
        {
            return "cherry-pick";
        }

        if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD")))
        {
            return "revert";
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
        {
            return "merge";
        }

        return null;
    }
}
