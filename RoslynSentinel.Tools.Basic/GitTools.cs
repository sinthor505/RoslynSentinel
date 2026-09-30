using System.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Tools.Basic;

[McpServerToolType]
public class GitTools
{
    private readonly IGitOperations _gitImpl;
    private readonly ILogger<GitTools> _logger;

    public GitTools(IWorkspaceManager workspaceManager,
        ILogger<GitTools> logger)
    {
        _logger = logger;
        _gitImpl = new GitImpl(workspaceManager, new NullLogger<GitImpl>());

    }

    [McpServerTool(Name = "Git")]
    [Produces(DataTag.Report)]
    [Description("Unified git tool: status, log, diff, show, staging, commit, revert, reset, branch, checkout, push, fetch, pull.")]
    public async Task<SentinelCallToolResult<object>> Git(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Which git operation to run.")]
        GitOperation operation,
        [Description("log: number of commits to return (max 100).")]
        int count = 20,
        [Description("diff: \"working\", \"staged\", a commit hash, or a range. show: a single commit hash/ref.")]
        string target = "working",
        [Description("diff/show/log: paths to restrict to (CSV string or JSON array).")]
        string? paths = null,
        [Description("diff/show: cap on the returned output, measured in UTF-8 bytes (min 1024, max 524288). Output past the cap is cut on a whole-character boundary and marked truncated.")]
        int maxBytes = 65536,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: message is required when operation=commit and amend=false; optional when amend=true (omit to keep HEAD's message); unused otherwise.
        [Description("Required for operation=commit unless amend=true (then omitting keeps HEAD's message).")]
        string? message = null,
        [Description("stage: \"tracked\" (default) stages modified/deleted tracked files only. \"all\" also stages untracked files. \"listed\" stages exactly files/paths. commit: omit to commit exactly what's staged; pass scope only to also stage before committing. files implies scope=listed; pass scope only to override or for tracked/all.")]
        GitStageScope? scope = null,
        [Description("stage/commit: paths to stage (CSV string or JSON array). files implies scope=listed; pass scope only to override or for tracked/all. Alias of paths - pass one, not both.")]
        string? files = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: commitHash is used when operation=revert (required) or
        // operation=show (optional alias for target - if both are set, commitHash wins); unused otherwise.
        [Description("Required for operation=revert: commit hash to revert. Also accepted by operation=show as an alias for target (commitHash wins if both are set).")]
        string? commitHash = null,
        [Description("revert: true stages without committing; commit separately to finalize.")]
        bool noCommit = false,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: branchName is required for operation=checkout; optional for operation=branch (omit to list).
        [Description("branch/checkout: branch to create/delete/switch to (branch: omit to list all; checkout: required). log: optional start ref. reset: ref to reset to (default HEAD~1).")]
        string? branchName = null,
        [Description("branch/checkout: base ref for a new branch (default HEAD).")]
        string? startPoint = null,
        [Description("branch: true deletes branchName instead of creating it (refuses if unmerged).")]
        bool deleteBranch = false,
        [Description("checkout: true creates branchName if missing, optionally from startPoint.")]
        bool createBranch = false,
        [Description("push/fetch/pull: the remote to operate on.")]
        string remoteName = "origin",
        [Description("push: true also sets upstream tracking.")]
        bool setUpstream = false,
        [Description("pull: true rebases instead of merging.")]
        bool rebase = false,
        [Description("commit: true amends HEAD instead of a new commit; message becomes optional (omit to keep HEAD's message).")]
        bool amend = false,
        [Description("reset: \"soft\" moves HEAD only (changes reappear staged). \"mixed\" (default) also resets the index (changes reappear unstaged). No \"hard\" mode - working tree is never discarded.")]
        GitResetMode? mode = null,
        [Description("status/log/diff/show only: an absolute path to a different repo/worktree. Mutating operations always stay scoped to the loaded solution.")]
        string? repoPath = null,
        [Description("status: most entries to list before the result is truncated to a 10-per-list sample plus full counts (default 50, valid 1-5000; out of range is refused). Raise it to see every changed path.")]
        int maxEntries = 50,
        [Description("diff/show: true returns a --name-status file list (status letter + path per file) instead of the patch. Mutually exclusive with stat.")]
        bool nameOnly = false,
        [Description("diff/show: true returns --stat text (per-file change counts and a summary) instead of the patch. Mutually exclusive with nameOnly.")]
        bool stat = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        var isReadOnlyOperation = operation is GitOperation.status or GitOperation.log or GitOperation.diff or GitOperation.show;
        if (!string.IsNullOrWhiteSpace(repoPath) && !isReadOnlyOperation)
        {
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"repoPath is only supported for status/log/diff/show - operation '{operation}' always targets the loaded solution's repo. Omit repoPath, or switch to a read-only operation.", Detail: null) };
        }

        var gitRoot = _gitImpl.TryGetGitRoot(out var rootError, isReadOnlyOperation ? repoPath : null);
        if (gitRoot is null)
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ErrorCode: "GitRootNotFound", Message: rootError, Detail: null) };

        // `files` and `paths` name the same concept; `paths` is the git-native spelling that `diff`
        // already used, so `files` stays an accepted alias rather than breaking callers. Setting
        // both is ambiguous, so it is refused instead of letting one silently win.
        if (!string.IsNullOrWhiteSpace(files) && !string.IsNullOrWhiteSpace(paths))
        {
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: "Both 'files' and 'paths' were supplied - they are aliases for the same list and must not be combined. Pass just one of them (either spelling is accepted).", Detail: null) };
        }
        var resolvedPaths = !string.IsNullOrWhiteSpace(files) ? files : paths;

        // Gap A/B: when files are supplied and scope is null, infer scope=listed for stage/add/commit.
        // However, explicit scope combined with files for all/tracked is an error.
        GitStageScope? effectiveScope = scope;
        if ((operation == GitOperation.stage || operation == GitOperation.add || operation == GitOperation.commit) &&
            !string.IsNullOrWhiteSpace(resolvedPaths) && scope == null)
        {
            effectiveScope = GitStageScope.listed;
        }

        // For stage/commit with explicit all/tracked scope and a file list, that's an error
        if ((operation == GitOperation.stage || operation == GitOperation.add || operation == GitOperation.commit) &&
            !string.IsNullOrWhiteSpace(resolvedPaths) && scope != null && scope != GitStageScope.listed)
        {
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"You passed both a file list and scope=\"{scope}\", which is ambiguous. scope=\"{scope}\" ignores the file list and stages/commits by scope instead. Pass scope=\"listed\" to stage/commit exactly the files you named, or drop the file list to use scope=\"{scope}\".", Detail: null) };
        }

        GitResult result = operation switch
        {
            GitOperation.status => await _gitImpl.StatusAsync(gitRoot, maxEntries, cancellationToken),
            GitOperation.log => await _gitImpl.LogAsync(gitRoot, count, branchName, resolvedPaths, cancellationToken),
            GitOperation.diff => await _gitImpl.DiffAsync(gitRoot, target, resolvedPaths, maxBytes, nameOnly, stat, cancellationToken),
            GitOperation.show => await _gitImpl.ShowAsync(gitRoot, !string.IsNullOrWhiteSpace(commitHash) ? commitHash : target, resolvedPaths, maxBytes, nameOnly, stat, cancellationToken),
            GitOperation.stage or GitOperation.add => await _gitImpl.StageAsync(gitRoot, effectiveScope ?? GitStageScope.tracked, resolvedPaths, cancellationToken),
            GitOperation.unstage => await _gitImpl.UnstageAsync(gitRoot, resolvedPaths, cancellationToken),
            GitOperation.commit => await _gitImpl.CommitAsync(gitRoot, message, effectiveScope, resolvedPaths, amend, cancellationToken),
            GitOperation.revert => await _gitImpl.RevertAsync(gitRoot, commitHash, noCommit, cancellationToken),
            GitOperation.reset => await _gitImpl.ResetAsync(gitRoot, branchName, mode ?? GitResetMode.mixed, cancellationToken),
            GitOperation.branch => await _gitImpl.BranchAsync(gitRoot, branchName, startPoint, deleteBranch, cancellationToken),
            GitOperation.checkout => await _gitImpl.CheckoutAsync(gitRoot, branchName, createBranch, startPoint, cancellationToken),
            GitOperation.push => await _gitImpl.PushAsync(gitRoot, remoteName, setUpstream, cancellationToken),
            GitOperation.fetch => await _gitImpl.FetchAsync(gitRoot, remoteName, cancellationToken),
            GitOperation.pull => await _gitImpl.PullAsync(gitRoot, remoteName, rebase, cancellationToken),
            _ => new GitResult { Success = false, Error = $"Unknown operation '{operation}'." }
        };

        if (((GitResult)result).Success)
        {
            return new SentinelCallToolResult<object> { IsSuccess = true, SuccessData = result };
        }
        else
        {
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ErrorCode: "GitError", Message: ((GitResult)result)?.Error ?? "Unknown Git error") };
        }
    }
}
