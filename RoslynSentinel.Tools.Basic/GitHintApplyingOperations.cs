namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Decorates an <see cref="IGitOperations"/> so every result passes through <see cref="GitHintText.Apply{T}"/>, which renders
/// the hint keys the engine set on the result. Every member forwards to the inner instance; TryGetGitRoot forwards unchanged
/// because the tool renders root failures itself.
/// </summary>
internal sealed class GitHintApplyingOperations(IGitOperations inner) : IGitOperations
{
    public async Task<GitStatusResult> AbortAsync(string gitRoot, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.AbortAsync(gitRoot, cancellationToken));

    public async Task<GitBranchResult> BranchAsync(string gitRoot, string? branchName, string? startPoint, bool deleteBranch, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.BranchAsync(gitRoot, branchName, startPoint, deleteBranch, cancellationToken));

    public async Task<GitCheckoutResult> CheckoutAsync(string gitRoot, string? branchName, bool createBranch, string? startPoint, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.CheckoutAsync(gitRoot, branchName, createBranch, startPoint, cancellationToken));

    public async Task<GitCommitResult> CommitAsync(string gitRoot, string? message, GitStageScope? scope, string? paths, bool amend, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.CommitAsync(gitRoot, message, scope, paths, amend, cancellationToken));

    public async Task<GitConfigResult> ConfigAsync(string gitRoot, GitAction? action, string? key, string? value, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.ConfigAsync(gitRoot, action, key, value, cancellationToken));

    public async Task<GitDiffResult> DiffAsync(string gitRoot, string target, string? paths, int maxBytes, bool nameOnly, bool stat, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.DiffAsync(gitRoot, target, paths, maxBytes, nameOnly, stat, cancellationToken));

    public async Task<GitRemoteResult> FetchAsync(string gitRoot, string remoteName, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.FetchAsync(gitRoot, remoteName, cancellationToken));

    public async Task<GitHunksResult> HunksAsync(string gitRoot, string? paths, int count, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.HunksAsync(gitRoot, paths, count, cancellationToken));

    public async Task<GitLogResult> LogAsync(string gitRoot, int count, string? refName, string? paths, string? pickaxe, bool pickaxeRegex, bool follow, CancellationToken cancellationToken, bool nameOnly = false)
        => GitHintText.Apply(await inner.LogAsync(gitRoot, count, refName, paths, pickaxe, pickaxeRegex, follow, cancellationToken, nameOnly));

    public async Task<GitRemoteResult> PullAsync(string gitRoot, string remoteName, bool rebase, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.PullAsync(gitRoot, remoteName, rebase, cancellationToken));

    public async Task<GitRemoteResult> PushAsync(string gitRoot, string remoteName, bool setUpstream, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.PushAsync(gitRoot, remoteName, setUpstream, cancellationToken));

    public async Task<GitStatusResult> ResetAsync(string gitRoot, string? refName, GitResetMode mode, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.ResetAsync(gitRoot, refName, mode, cancellationToken));

    public async Task<GitRevertResult> RevertAsync(string gitRoot, string? commitHash, bool noCommit, int? mainline, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.RevertAsync(gitRoot, commitHash, noCommit, mainline, cancellationToken));

    public async Task<GitShowResult> ShowAsync(string gitRoot, string target, string? paths, int maxBytes, bool nameOnly, bool stat, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.ShowAsync(gitRoot, target, paths, maxBytes, nameOnly, stat, cancellationToken));

    public async Task<GitStatusResult> StageAsync(string gitRoot, GitStageScope scope, string? paths, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.StageAsync(gitRoot, scope, paths, cancellationToken));

    public async Task<GitStageHunksResult> StageHunksAsync(string gitRoot, string? paths, string? hunkIds, string? hunkFingerprint, string? lineRange, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.StageHunksAsync(gitRoot, paths, hunkIds, hunkFingerprint, lineRange, cancellationToken));

    public async Task<GitStashResult> StashAsync(string gitRoot, GitAction? action, string? message, bool includeUntracked, string? paths, int? stashIndex, int count, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.StashAsync(gitRoot, action, message, includeUntracked, paths, stashIndex, count, cancellationToken));

    public async Task<GitStatusResult> StatusAsync(string gitRoot, int maxEntries, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.StatusAsync(gitRoot, maxEntries, cancellationToken));

    public async Task<GitTagResult> TagAsync(string gitRoot, GitAction? action, string? tagName, string? refName, string? message, int count, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.TagAsync(gitRoot, action, tagName, refName, message, count, cancellationToken));

    [Obsolete("Use the GitRootFailure overload")]
    public string? TryGetGitRoot(out string error, string? repoPath = null)
        => inner.TryGetGitRoot(out error, repoPath);

    public string? TryGetGitRoot(out string error, out GitRootFailure failure, string? repoPath = null)
        => inner.TryGetGitRoot(out error, out failure, repoPath);

    public async Task<GitStatusResult> UnstageAsync(string gitRoot, string? paths, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.UnstageAsync(gitRoot, paths, cancellationToken));

    public async Task<GitWorktreeResult> WorktreeAsync(string gitRoot, GitAction? action, string? worktreePath, string? branchName, bool createBranch, string? startPoint, bool discardUncommittedChanges, CancellationToken cancellationToken)
        => GitHintText.Apply(await inner.WorktreeAsync(gitRoot, action, worktreePath, branchName, createBranch, startPoint, discardUncommittedChanges, cancellationToken));
}
