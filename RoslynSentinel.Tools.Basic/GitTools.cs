using System.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    [Description("Unified git tool: status, log, diff, show, staging, commit, revert, reset, branch, checkout, push, fetch, pull, abort. A conflicting pull/revert leaves the repo mid-merge/rebase/revert (status reports it as inProgress); operation=abort backs out of whichever is in progress and restores the pre-operation state.")]
    public async Task<SentinelCallToolResult<object>> Git(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Which git operation to run. abort: cancels the merge, rebase, cherry-pick, revert or am that a conflict left in progress (no other parameters; refused when nothing is in progress).")]
        GitOperation operation,
        [Description("log: number of commits to return (max 100).")]
        int count = 20,
        [Description("diff: \"working\", \"staged\", a commit hash, or a range. show: a single commit hash/ref. Prefer 'ref' for a commit/branch; 'target' stays accepted (an alias for ref on diff/show), and giving both with different values is refused.")]
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
        // operation=show (optional alias for ref/target - different values across them are refused); unused otherwise.
        [Description("Required for operation=revert: commit hash to revert. Also accepted by operation=show as an alias for ref/target (giving it together with a different ref or target is refused).")]
        string? commitHash = null,
        [Description("revert: true stages without committing; commit separately to finalize.")]
        bool noCommit = false,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: branchName is required for operation=checkout; optional for operation=branch (omit to list).
        [Description("branch/checkout: branch to create/delete/switch to (branch: omit to list all; checkout: required). log/reset: accepted as an alias for ref (reset has no default ref, so one of the two is required).")]
        string? branchName = null,
        [Description("branch: base ref for a new branch (default HEAD). checkout: base ref for a new branch, only valid together with createBranch=true (refused otherwise; ignored if the branch already exists).")]
        string? startPoint = null,
        [Description("branch: true deletes branchName instead of creating it (refuses if unmerged).")]
        bool deleteBranch = false,
        [Description("checkout: true creates branchName from startPoint (default HEAD) if it does not exist; if it already exists this is a plain checkout and the result reports createdNewBranch=false.")]
        bool createBranch = false,
        [Description("push/fetch/pull: the remote to operate on.")]
        string remoteName = "origin",
        [Description("push: true also sets upstream tracking.")]
        bool setUpstream = false,
        [Description("pull: true rebases instead of merging.")]
        bool rebase = false,
        [Description("commit: true amends HEAD instead of a new commit; message becomes optional (omit to keep HEAD's message). Refused when HEAD is already contained in the branch's upstream (force-push is not exposed); allowed when no upstream is configured.")]
        bool amend = false,
        [Description("reset: \"soft\" moves HEAD only (changes reappear staged). \"mixed\" (default) also resets the index (changes reappear unstaged). No \"hard\" mode - working tree is never discarded.")]
        GitResetMode? mode = null,
        [Description("status/log/diff/show only: an absolute path to a different repo/worktree. Mutating operations always stay scoped to the loaded solution.")]
        string? repoPath = null,
        [Description("status: most entries to list before the result is truncated to a 10-per-list sample plus full counts (default 50, valid 1-5000; out of range is refused). Untracked files are listed individually (new directories are expanded, ignored files excluded), so any listed path can be passed as-is to stage/commit files. Raise maxEntries to see every changed path.")]
        int maxEntries = 50,
        [Description("diff/show: true returns a --name-status file list (status letter + path per file) instead of the patch. Mutually exclusive with stat.")]
        bool nameOnly = false,
        [Description("diff/show: true returns --stat text (per-file change counts and a summary) instead of the patch. Mutually exclusive with nameOnly.")]
        bool stat = false,
        [Description("revert: which parent to keep when reverting a MERGE commit (git revert -m N): 1 is the branch that was merged into (almost always right), 2 the branch that was merged in. Required when commitHash is a merge commit (refused without it); refused for a non-merge commit. Only valid for operation=revert.")]
        int? mainline = null,
        [Description("log/show/diff/reset: the git ref to operate on (a branch, tag, commit hash, HEAD~1, ...). log: start ref. show: the commit to show. diff: what to diff the working tree against (or a range). reset: REQUIRED, the ref to move HEAD to (e.g. \"HEAD~1\" to undo the last commit). Aliases target (diff/show), commitHash (show) and branchName (log/reset) stay accepted; supplying ref together with an alias that has a different value is refused. Not supported for other operations.")]
        string? @ref = null,
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

        // `ref` is the single spelling for "a git ref"; target/commitHash/branchName stay accepted as
        // per-operation aliases. Different values across them are ambiguous, so they are refused
        // rather than letting one silently win (same policy as files/paths above).
        string? resolvedRef = null;
        if (operation is GitOperation.log or GitOperation.show or GitOperation.diff or GitOperation.reset)
        {
            (string Name, string? Value)[] aliases = operation switch
            {
                GitOperation.log or GitOperation.reset => [(nameof(branchName), branchName)],
                GitOperation.show => [(nameof(commitHash), commitHash), (nameof(target), target == "working" ? null : target)],
                _ => [(nameof(target), target == "working" ? null : target)],
            };
            resolvedRef = ResolveRef(@ref, aliases, out var refError);
            if (refError is not null)
            {
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: refError, Detail: null) };
            }
        }
        else if (!string.IsNullOrWhiteSpace(@ref))
        {
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"ref is only supported for log/show/diff/reset - operation '{operation}' takes its ref from another parameter (revert: commitHash; branch/checkout: branchName, startPoint). Omit ref.", Detail: null) };
        }

        if (mainline is not null && operation != GitOperation.revert)
        {
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"mainline is only supported for revert - operation '{operation}' does not use it. Omit mainline.", Detail: null) };
        }

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
            GitOperation.log => await _gitImpl.LogAsync(gitRoot, count, resolvedRef, resolvedPaths, cancellationToken),
            GitOperation.diff => await _gitImpl.DiffAsync(gitRoot, resolvedRef ?? target, resolvedPaths, maxBytes, nameOnly, stat, cancellationToken),
            GitOperation.show => await _gitImpl.ShowAsync(gitRoot, resolvedRef ?? target, resolvedPaths, maxBytes, nameOnly, stat, cancellationToken),
            GitOperation.stage or GitOperation.add => await _gitImpl.StageAsync(gitRoot, effectiveScope ?? GitStageScope.tracked, resolvedPaths, cancellationToken),
            GitOperation.unstage => await _gitImpl.UnstageAsync(gitRoot, resolvedPaths, cancellationToken),
            GitOperation.commit => await _gitImpl.CommitAsync(gitRoot, message, effectiveScope, resolvedPaths, amend, cancellationToken),
            GitOperation.revert => await _gitImpl.RevertAsync(gitRoot, commitHash, noCommit, mainline, cancellationToken),
            GitOperation.abort => await _gitImpl.AbortAsync(gitRoot, cancellationToken),
            GitOperation.reset => await _gitImpl.ResetAsync(gitRoot, resolvedRef, mode ?? GitResetMode.mixed, cancellationToken),
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
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ErrorCode: result.ErrorKind ?? GitErrorCodes.Fallback, Message: result.Error ?? "Unknown Git error", Detail: result.ErrorDetail) };
        }
    }

    /// <summary>
    /// Collapses <c>ref</c> and its per-operation aliases into one value. Returns null (no error)
    /// when none is supplied. When several are supplied with different values, returns null and an
    /// error naming every supplied parameter and its value; identical values are not a conflict.
    /// </summary>
    private static string? ResolveRef(string? refValue, (string Name, string? Value)[] aliases, out string? error)
    {
        var supplied = new List<(string Name, string Value)>();
        if (!string.IsNullOrWhiteSpace(refValue))
            supplied.Add(("ref", refValue.Trim()));
        foreach (var (name, value) in aliases)
        {
            if (!string.IsNullOrWhiteSpace(value))
                supplied.Add((name, value.Trim()));
        }

        if (supplied.Select(s => s.Value).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            var listed = string.Join(", ", supplied.Select(s => $"'{s.Name}' = '{s.Value}'"));
            error = $"Conflicting ref values were supplied ({listed}) - ref and these parameters are aliases for the same git ref and must agree. Pass just 'ref'.";
            return null;
        }

        error = null;
        return supplied.Count > 0 ? supplied[0].Value : null;
    }
}
