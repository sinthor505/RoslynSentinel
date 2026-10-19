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

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "Git")]
    [Produces(DataTag.Report)]
    [Description("Unified git tool. A conflicting pull/revert leaves the repo mid-operation (status shows inProgress); abort backs out. stage can take hunkIds+hunkFingerprint or lineRange to stage part of one file.")]
    public async Task<SentinelCallToolResult<object>> GitTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Requirements. abort: cancels an in-progress merge/rebase/cherry-pick/revert/am (no other params). reset: ref. revert: ref (the commit). commit: message unless amend. tag: action; create needs tagName. stash: action; apply/pop need stashIndex. worktree: action; add needs worktreePath+branchName; remove needs worktreePath. hunks: one path; lists its unstaged hunks with ids and a fingerprint for stage's hunkIds.")]
        GitOperation operation,
        [Description("log / tag list: number of entries (max 100). hunks: max hunks listed.")]
        int count = 20,
        [Description("log/diff/show/hunks/stage/unstage/commit/stash push: paths (CSV or JSON array). stage/commit: implies scope=listed. hunks, hunkIds, follow: exactly one path.")]
        string? paths = null,
        [Description("diff/show: cap on returned output in UTF-8 bytes (min 1024, max 524288).")]
        int maxBytes = 65536,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: message is required when operation=commit and amend=false; optional when amend=true (omit to keep HEAD's message); unused otherwise.
        [Description("commit: the message (omit with amend to keep HEAD's). tag create: makes an annotated tag. stash push: entry label.")]
        string? message = null,
        [Description("stage: tracked (default) | all (adds untracked) | listed (exactly paths). commit: omit to commit what is staged; scope or paths stages first.")]
        GitStageScope? scope = null,
        [Description("revert: true stages without committing.")]
        bool noCommit = false,
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
        [Description("commit: true amends HEAD instead of a new commit; message becomes optional (omit to keep HEAD's message).")]
        bool amend = false,
        [Description("reset: \"soft\" moves HEAD only (changes stay staged); \"mixed\" (default) also resets the index (unstaged). No \"hard\".")]
        GitResetMode? mode = null,
        [Description("status/log/diff/show/hunks (and list actions) only: absolute path to a different repo/worktree.")]
        string? repoPath = null,
        [Description("status: max entries listed before truncating to a 10-per-list sample plus full counts (1-5000). Untracked files are listed individually; pass any listed path as-is to stage/commit paths.")]
        int maxEntries = 50,
        [Description("diff/show: true returns a name-status file list instead of the patch. Mutually exclusive with stat.")]
        bool nameOnly = false,
        [Description("diff/show: true returns --stat text instead of the patch. Mutually exclusive with nameOnly.")]
        bool stat = false,
        [Description("revert: parent to keep when reverting a MERGE commit (git revert -m N): 1 = the branch merged into (almost always right), 2 = the branch merged in. Required for a merge commit.")]
        int? mainline = null,
        [Description("A git ref (branch, tag, hash, HEAD~1). log: start ref. show: the commit. diff: working|unstaged|staged, a ref to diff the working tree against, or refA..refB (default: working tree). reset: ref to move HEAD to. revert: commit to revert. tag create: commit to tag (default HEAD).")]
        string? @ref = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: action is required when operation=tag, stash or worktree; refused for every other operation.
        [Description("tag: list|create|delete. stash: list|push|apply|pop. worktree: list|add|remove.")]
        GitAction? action = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: tagName is required for tag create/delete; unused otherwise.
        [Description("tag create/delete: the tag name, e.g. v1.2.0.")]
        string? tagName = null,
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
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: hunkIds needs hunkFingerprint and exactly one path in paths; alternative to lineRange; refused for every operation except stage/add.
        [Description("stage: hunk ids from Git(operation: hunks), e.g. \"1,3\". Needs hunkFingerprint and exactly one path.")]
        string? hunkIds = null,
        [Description("stage: fingerprint from the hunk list (GitHunkStale if the file changed since).")]
        string? hunkFingerprint = null,
        [Description("stage: stage the hunks wholly inside these new-file lines, e.g. \"40-80\" or \"52\". Alternative to hunkIds.")]
        string? lineRange = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: pickaxe is optional for operation=log; refused for every other operation.
        [Description("log: -S text search")]
        string? pickaxe = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: pickaxeRegex requires pickaxe and operation=log; refused otherwise.
        [Description("log: pickaxe is a regex (-G)")]
        bool pickaxeRegex = false,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: follow needs operation=log and exactly one path in paths; refused for every other operation.
        [Description("log: follow renames (one path)")]
        bool follow = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        var isReadOnlyOperation = operation is GitOperation.status or GitOperation.log or GitOperation.diff or GitOperation.show or GitOperation.hunks
            || (operation is GitOperation.tag or GitOperation.stash or GitOperation.worktree && action == GitAction.list);
        if (!string.IsNullOrWhiteSpace(repoPath) && !isReadOnlyOperation)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"repoPath is only supported for status/log/diff/show/hunks (and list actions) - operation '{operation}' always targets the loaded solution's repo. Omit repoPath, or switch to a read-only operation.", Detail: null) };
        }

        var gitRoot = _gitImpl.TryGetGitRoot(out var rootError, isReadOnlyOperation ? repoPath : null);
        if (gitRoot is null)
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "GitRootNotFound", Message: rootError, Detail: null) };

        // `ref` is the single spelling for "a git ref" (target/commitHash/files are renamed to ref/paths
        // by ToolArgumentValidator.ParameterAliases before binding); branchName stays an accepted
        // log/reset alias. Different values across ref and branchName are ambiguous, so they are
        // refused rather than letting one silently win.
        string? resolvedRef = null;
        if (operation is GitOperation.log or GitOperation.show or GitOperation.diff or GitOperation.reset or GitOperation.tag or GitOperation.revert)
        {
            (string Name, string? Value)[] aliases = operation is GitOperation.log or GitOperation.reset
                ? [(nameof(branchName), branchName)]
                : [];
            resolvedRef = ResolveRef(@ref, aliases, out var refError);
            if (refError is not null)
            {
                return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: refError, Detail: null) };
            }
        }
        else if (!string.IsNullOrWhiteSpace(@ref))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"ref is only supported for log/show/diff/reset/tag/revert - operation '{operation}' takes its ref from another parameter (branch/checkout: branchName, startPoint). Omit ref.", Detail: null) };
        }

        if (mainline is not null && operation != GitOperation.revert)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"mainline is only supported for revert - operation '{operation}' does not use it. Omit mainline.", Detail: null) };
        }

        if ((!string.IsNullOrWhiteSpace(pickaxe) || pickaxeRegex || follow) && operation != GitOperation.log)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"pickaxe, pickaxeRegex and follow are only supported for log - operation '{operation}' does not use them. Omit them.", Detail: null) };
        }

        if (pickaxeRegex && string.IsNullOrWhiteSpace(pickaxe))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: "pickaxeRegex only goes with pickaxe. Pass the regular expression in pickaxe, or omit pickaxeRegex.", Detail: null) };
        }

        if (action is not null && operation is not (GitOperation.tag or GitOperation.stash or GitOperation.worktree))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"action is only supported for tag/stash/worktree - operation '{operation}' does not use it. Omit action.", Detail: null) };
        }

        if (!string.IsNullOrWhiteSpace(worktreePath) && operation != GitOperation.worktree)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"worktreePath is only supported for worktree (action: add or remove) - operation '{operation}' does not use it. Omit worktreePath.", Detail: null) };
        }

        if (discardUncommittedChanges && !(operation == GitOperation.worktree && action == GitAction.remove))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: "discardUncommittedChanges is only supported for operation=worktree with action=remove. Omit it.", Detail: null) };
        }

        if (stashIndex is not null && operation != GitOperation.stash)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"stashIndex is only supported for stash - operation '{operation}' does not use it. Omit stashIndex.", Detail: null) };
        }

        if (includeUntracked && operation != GitOperation.stash)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"includeUntracked is only supported for stash (action: push) - operation '{operation}' does not use it. Omit includeUntracked.", Detail: null) };
        }

        var hasHunkParams = !string.IsNullOrWhiteSpace(hunkIds) || !string.IsNullOrWhiteSpace(hunkFingerprint) || !string.IsNullOrWhiteSpace(lineRange);
        if (hasHunkParams && operation is not (GitOperation.stage or GitOperation.add))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"hunkIds, hunkFingerprint and lineRange are only supported for stage - operation '{operation}' does not use them. Omit them.", Detail: null) };
        }

        if (!string.IsNullOrWhiteSpace(hunkFingerprint) && string.IsNullOrWhiteSpace(hunkIds))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: "hunkFingerprint only goes with hunkIds. Pass hunkIds (from Git(operation: hunks)) with it, or use lineRange without a fingerprint.", Detail: null) };
        }

        if (hasHunkParams && scope != null && scope != GitStageScope.listed)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"You passed hunk parameters (hunkIds/hunkFingerprint/lineRange) together with scope=\"{scope}\", which is ambiguous: hunk staging works on exactly one listed file. Drop scope, or pass scope=\"listed\" with paths.", Detail: null) };
        }

        // Gap A/B: when paths are supplied and scope is null, infer scope=listed for stage/add/commit.
        // However, explicit scope combined with files for all/tracked is an error.
        GitStageScope? effectiveScope = scope;
        if ((operation == GitOperation.stage || operation == GitOperation.add || operation == GitOperation.commit) &&
            !string.IsNullOrWhiteSpace(paths) && scope == null)
        {
            effectiveScope = GitStageScope.listed;
        }

        // For stage/commit with explicit all/tracked scope and a file list, that's an error
        if ((operation == GitOperation.stage || operation == GitOperation.add || operation == GitOperation.commit) &&
            !string.IsNullOrWhiteSpace(paths) && scope != null && scope != GitStageScope.listed)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"You passed both a file list and scope=\"{scope}\", which is ambiguous. scope=\"{scope}\" ignores the file list and stages/commits by scope instead. Pass scope=\"listed\" to stage/commit exactly the files you named, or drop the file list to use scope=\"{scope}\".", Detail: null) };
        }

        GitResult result = operation switch
        {
            GitOperation.status => await _gitImpl.StatusAsync(gitRoot, maxEntries, cancellationToken),
            GitOperation.log => await _gitImpl.LogAsync(gitRoot, count, resolvedRef, paths, pickaxe, pickaxeRegex, follow, cancellationToken),
            GitOperation.diff => await _gitImpl.DiffAsync(gitRoot, resolvedRef ?? "working", paths, maxBytes, nameOnly, stat, cancellationToken),
            GitOperation.show => await _gitImpl.ShowAsync(gitRoot, resolvedRef ?? "HEAD", paths, maxBytes, nameOnly, stat, cancellationToken),
            GitOperation.stage or GitOperation.add when hasHunkParams => await _gitImpl.StageHunksAsync(gitRoot, paths, hunkIds, hunkFingerprint, lineRange, cancellationToken),
            GitOperation.stage or GitOperation.add => await _gitImpl.StageAsync(gitRoot, effectiveScope ?? GitStageScope.tracked, paths, cancellationToken),
            GitOperation.unstage => await _gitImpl.UnstageAsync(gitRoot, paths, cancellationToken),
            GitOperation.commit => await _gitImpl.CommitAsync(gitRoot, message, effectiveScope, paths, amend, cancellationToken),
            GitOperation.revert => await _gitImpl.RevertAsync(gitRoot, resolvedRef, noCommit, mainline, cancellationToken),
            GitOperation.abort => await _gitImpl.AbortAsync(gitRoot, cancellationToken),
            GitOperation.reset => await _gitImpl.ResetAsync(gitRoot, resolvedRef, mode ?? GitResetMode.mixed, cancellationToken),
            GitOperation.branch => await _gitImpl.BranchAsync(gitRoot, branchName, startPoint, deleteBranch, cancellationToken),
            GitOperation.checkout => await _gitImpl.CheckoutAsync(gitRoot, branchName, createBranch, startPoint, cancellationToken),
            GitOperation.push => await _gitImpl.PushAsync(gitRoot, remoteName, setUpstream, cancellationToken),
            GitOperation.fetch => await _gitImpl.FetchAsync(gitRoot, remoteName, cancellationToken),
            GitOperation.pull => await _gitImpl.PullAsync(gitRoot, remoteName, rebase, cancellationToken),
            GitOperation.tag => await _gitImpl.TagAsync(gitRoot, action, tagName, resolvedRef, message, count, cancellationToken),
            GitOperation.stash => await _gitImpl.StashAsync(gitRoot, action, message, includeUntracked, paths, stashIndex, count, cancellationToken),
            GitOperation.worktree => await _gitImpl.WorktreeAsync(gitRoot, action, worktreePath, branchName, createBranch, startPoint, discardUncommittedChanges, cancellationToken),
            GitOperation.hunks => await _gitImpl.HunksAsync(gitRoot, paths, count, cancellationToken),
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
