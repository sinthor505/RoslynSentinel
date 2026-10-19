using System.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Branch, checkout, remote (push/fetch/pull), tag, stash and worktree operations, split out of the
/// Git tool so the always-loaded Git schema stays small. Delegates to the same GitImpl methods.
/// Deliberately NOT marked [UnrecoverableBreaker]: only the read-oriented Git tool is allowed while
/// the unrecoverable breaker is tripped.
/// </summary>
[McpServerToolType]
public class GitRemoteTools
{
    private readonly IGitOperations _gitImpl;
    private readonly ILogger<GitRemoteTools> _logger;

    public GitRemoteTools(IWorkspaceManager workspaceManager,
        ILogger<GitRemoteTools> logger)
    {
        _logger = logger;
        _gitImpl = new GitImpl(workspaceManager, new NullLogger<GitImpl>());
    }

    [McpServerTool(Name = "GitRemote")]
    [Produces(DataTag.Report)]
    [Description("Git branch, checkout, push, fetch, pull, tag, stash and worktree. status, log, diff, commit, stage and the rest are the Git tool.")]
    public async Task<SentinelCallToolResult<object>> GitRemoteTool(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    [Description("Requirements. branch: omit branchName to list, else creates it from startPoint (deleteBranch=true deletes it). checkout: branchName required; createBranch=true creates it from startPoint if missing. push/fetch/pull: remoteName (default origin). tag: action; create/delete need tagName. stash: action; apply/pop need stashIndex. worktree: action; add needs worktreePath+branchName; remove needs worktreePath.")]
        GitRemoteOperation operation,
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: action is required when operation=tag, stash or worktree; refused for every other operation.
    [Description("tag: list|create|delete. stash: list|push|apply|pop. worktree: list|add|remove.")]
        GitAction? action = null,
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: branchName is required for operation=checkout; optional for operation=branch (omit to list).
    [Description("branch/checkout/worktree add: branch to create/delete/switch to/use in the new worktree (branch: omit to list; checkout, worktree add: required).")]
        string? branchName = null,
    [Description("Base ref for a new branch (default HEAD). checkout and worktree add: only with createBranch=true.")]
        string? startPoint = null,
    [Description("branch: true deletes branchName instead of creating it.")]
        bool deleteBranch = false,
    [Description("checkout: true creates branchName from startPoint if missing (else plain checkout). worktree add: true creates branchName (must not exist); false checks out an existing one.")]
        bool createBranch = false,
    [Description("push/fetch/pull: the remote to operate on.")]
        string remoteName = "origin",
    [Description("push: true also sets upstream tracking.")]
        bool setUpstream = false,
    [Description("pull: true rebases instead of merging.")]
        bool rebase = false,
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: tagName is required for tag create/delete; unused otherwise.
    [Description("tag create/delete: the tag name, e.g. v1.2.0.")]
        string? tagName = null,
    [Description("tag create: commit to tag (default HEAD).")]
        string? @ref = null,
    [Description("tag create: makes an annotated tag. stash push: entry label.")]
        string? message = null,
    [Description("tag list: number of entries (max 100).")]
        int count = 20,
    [Description("stash push: paths to stash (CSV or JSON array); omit for everything tracked.")]
        string? paths = null,
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: stashIndex is required for stash apply/pop (no default); unused otherwise.
    [Description("stash apply/pop: which entry; 0 is the newest. Required, there is no default. See action: list.")]
        int? stashIndex = null,
    [Description("stash push: also stash untracked (not ignored) files. Default false: untracked files stay in the working tree.")]
        bool includeUntracked = false,
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: worktreePath is required for worktree add/remove; unused otherwise.
    [Description("worktree add/remove: absolute path of the worktree. For add it must be OUTSIDE the repository root.")]
        string? worktreePath = null,
    [Description("worktree remove only: true removes a worktree with uncommitted or untracked files, permanently discarding them (--force). Default false: a dirty worktree is refused, listing changed paths. Main, current and locked worktrees are always refused.")]
        bool discardUncommittedChanges = false,
    [Description("list actions only: absolute path to a different repo/worktree.")]
        string? repoPath = null,
    CancellationToken cancellationToken = default)
    {
        var isReadOnlyOperation = operation is GitRemoteOperation.tag or GitRemoteOperation.stash or GitRemoteOperation.worktree && action == GitAction.list;
        if (!string.IsNullOrWhiteSpace(repoPath) && !isReadOnlyOperation)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"repoPath is only supported for list actions (tag/stash/worktree) - operation '{operation}' always targets the loaded solution's repo. Omit repoPath, or use action: list.", Detail: null) };
        }

        var gitRoot = _gitImpl.TryGetGitRoot(out var rootError, isReadOnlyOperation ? repoPath : null);
        if (gitRoot is null)
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "GitRootNotFound", Message: rootError, Detail: null) };

        string? resolvedRef = string.IsNullOrWhiteSpace(@ref) ? null : @ref.Trim();
        if (resolvedRef is not null && operation != GitRemoteOperation.tag)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"ref is only supported for tag create - operation '{operation}' takes its ref from another parameter (branch/checkout: startPoint). Omit ref.", Detail: null) };
        }

        if (action is not null && operation is not (GitRemoteOperation.tag or GitRemoteOperation.stash or GitRemoteOperation.worktree))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"action is only supported for tag/stash/worktree - operation '{operation}' does not use it. Omit action.", Detail: null) };
        }

        if (!string.IsNullOrWhiteSpace(worktreePath) && operation != GitRemoteOperation.worktree)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"worktreePath is only supported for worktree (action: add or remove) - operation '{operation}' does not use it. Omit worktreePath.", Detail: null) };
        }

        if (discardUncommittedChanges && !(operation == GitRemoteOperation.worktree && action == GitAction.remove))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: "discardUncommittedChanges is only supported for operation=worktree with action=remove. Omit it.", Detail: null) };
        }

        if (stashIndex is not null && operation != GitRemoteOperation.stash)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"stashIndex is only supported for stash - operation '{operation}' does not use it. Omit stashIndex.", Detail: null) };
        }

        if (includeUntracked && operation != GitRemoteOperation.stash)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"includeUntracked is only supported for stash (action: push) - operation '{operation}' does not use it. Omit includeUntracked.", Detail: null) };
        }

        GitResult result = operation switch
        {
            GitRemoteOperation.branch => await _gitImpl.BranchAsync(gitRoot, branchName, startPoint, deleteBranch, cancellationToken),
            GitRemoteOperation.checkout => await _gitImpl.CheckoutAsync(gitRoot, branchName, createBranch, startPoint, cancellationToken),
            GitRemoteOperation.push => await _gitImpl.PushAsync(gitRoot, remoteName, setUpstream, cancellationToken),
            GitRemoteOperation.fetch => await _gitImpl.FetchAsync(gitRoot, remoteName, cancellationToken),
            GitRemoteOperation.pull => await _gitImpl.PullAsync(gitRoot, remoteName, rebase, cancellationToken),
            GitRemoteOperation.tag => await _gitImpl.TagAsync(gitRoot, action, tagName, resolvedRef, message, count, cancellationToken),
            GitRemoteOperation.stash => await _gitImpl.StashAsync(gitRoot, action, message, includeUntracked, paths, stashIndex, count, cancellationToken),
            GitRemoteOperation.worktree => await _gitImpl.WorktreeAsync(gitRoot, action, worktreePath, branchName, createBranch, startPoint, discardUncommittedChanges, cancellationToken),
            _ => new GitResult { IsError = true, Error = $"Unknown operation '{operation}'." }
        };

        if (!((GitResult)result).IsError)
        {
            return new SentinelCallToolResult<object> { IsError = false, SuccessData = result };
        }
        else
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: result.ErrorKind ?? GitErrorCodes.Fallback, Message: result.Error ?? "Unknown Git error", Detail: result.ErrorDetail) };
        }
    }
}
