namespace RoslynSentinel.Server.Basic;

public interface IGitOperations
{
    Task<GitBranchResult> BranchAsync(string gitRoot, string? branchName, string? startPoint, bool deleteBranch, CancellationToken cancellationToken);
    Task<GitCheckoutResult> CheckoutAsync(string gitRoot, string? branchName, bool createBranch, string? startPoint, CancellationToken cancellationToken);
    Task<GitCommitResult> CommitAsync(string gitRoot, string? message, GitStageScope? scope, string? paths, bool amend, CancellationToken cancellationToken);
    Task<GitDiffResult> DiffAsync(string gitRoot, string target, string? paths, int maxBytes, CancellationToken cancellationToken);
    Task<GitRemoteResult> FetchAsync(string gitRoot, string remoteName, CancellationToken cancellationToken);
    Task<GitLogResult> LogAsync(string gitRoot, int count, string? refName, string? paths, CancellationToken cancellationToken);
    Task<GitRemoteResult> PullAsync(string gitRoot, string remoteName, bool rebase, CancellationToken cancellationToken);
    Task<GitRemoteResult> PushAsync(string gitRoot, string remoteName, bool setUpstream, CancellationToken cancellationToken);
    Task<GitStatusResult> ResetAsync(string gitRoot, string? refName, GitResetMode mode, CancellationToken cancellationToken);
    Task<GitRevertResult> RevertAsync(string gitRoot, string? commitHash, bool noCommit, CancellationToken cancellationToken);
    Task<GitShowResult> ShowAsync(string gitRoot, string target, string? paths, int maxBytes, CancellationToken cancellationToken);
    Task<GitStatusResult> StageAsync(string gitRoot, GitStageScope scope, string? paths, CancellationToken cancellationToken);
    Task<GitStatusResult> StatusAsync(string gitRoot, CancellationToken cancellationToken);
    string? TryGetGitRoot(out string error, string? repoPath = null);
    Task<GitStatusResult> UnstageAsync(string gitRoot, string? paths, CancellationToken cancellationToken);
}