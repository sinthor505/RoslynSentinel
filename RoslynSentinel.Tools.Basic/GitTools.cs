using System.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel;

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
    [Description("Unified git tool. A conflicting pull/revert leaves the repo mid-merge/rebase/revert (status reports it as inProgress); operation=abort backs out of whichever is in progress and restores the pre-operation state. tag/stash/worktree take an action. stage accepts hunkIds+hunkFingerprint or lineRange to stage part of one file.")]
    public async Task<SentinelCallToolResult<object>> Git(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("abort: cancels the merge, rebase, cherry-pick, revert or am that a conflict left in progress (no other parameters). tag: needs action (list|create|delete); create needs tagName. stash: needs action (list|push|apply|pop); apply/pop need stashIndex. worktree: needs action (list|add|remove); add needs worktreePath and branchName; remove needs worktreePath. hunks: list the unstaged hunks of one file (files: one path) with ids and a fingerprint, to feed stage's hunkIds.")]
        GitOperation operation,
        [Description("log / tag list: number of entries (max 100). hunks: max hunks listed.")]
        int count = 20,
        [Description("diff: \"working\" (alias \"unstaged\"), \"staged\", a commit hash, branch or tag, or a range refA..refB. show: a commit hash/ref. Prefer 'ref'.")]
        string target = "working",
        [Description("diff/show/log: paths to restrict to (CSV string or JSON array).")]
        string? paths = null,
        [Description("diff/show: cap on returned output in UTF-8 bytes (min 1024, max 524288).")]
        int maxBytes = 65536,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: message is required when operation=commit and amend=false; optional when amend=true (omit to keep HEAD's message); unused otherwise.
        [Description("Required for operation=commit unless amend=true (then omitting keeps HEAD's message). tag create: when set makes an annotated tag. stash push: label for the entry.")]
        string? message = null,
        [Description("stage: \"tracked\" (default) stages modified/deleted tracked files only; \"all\" also stages untracked files; \"listed\" stages exactly files/paths. commit: omit to commit exactly what's staged; pass scope only to also stage first. files implies scope=listed.")]
        GitStageScope? scope = null,
        [Description("stage/commit: paths to stage (CSV string or JSON array); implies scope=listed.")]
        string? files = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: commitHash is used when operation=revert (required) or
        // operation=show (optional alias for ref/target - different values across them are refused); unused otherwise.
        [Description("Required for revert: the commit hash to revert.")]
        string? commitHash = null,
        [Description("revert: true stages without committing; commit separately to finalize.")]
        bool noCommit = false,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: branchName is required for operation=checkout; optional for operation=branch (omit to list).
        [Description("branch/checkout/worktree add: branch to create/delete/switch to/check out in the new worktree (branch: omit to list all; checkout and worktree add: required).")]
        string? branchName = null,
        [Description("Base ref for a new branch (default HEAD). checkout and worktree add: only with createBranch=true.")]
        string? startPoint = null,
        [Description("branch: true deletes branchName instead of creating it.")]
        bool deleteBranch = false,
        [Description("checkout: true creates branchName from startPoint if it does not exist (otherwise a plain checkout). worktree add: true creates branchName (it must not exist yet); false checks out an existing branchName.")]
        bool createBranch = false,
        [Description("push/fetch/pull: the remote to operate on.")]
        string remoteName = "origin",
        [Description("push: true also sets upstream tracking.")]
        bool setUpstream = false,
        [Description("pull: true rebases instead of merging.")]
        bool rebase = false,
        [Description("commit: true amends HEAD instead of a new commit; message becomes optional (omit to keep HEAD's message).")]
        bool amend = false,
        [Description("reset: \"soft\" moves HEAD only (changes stay staged); \"mixed\" (default) also resets the index (changes become unstaged). No \"hard\" mode.")]
        GitResetMode? mode = null,
        [Description("status/log/diff/show/hunks (and list actions) only: absolute path to a different repo/worktree.")]
        string? repoPath = null,
        [Description("status: most entries to list before truncating to a 10-per-list sample plus full counts (valid 1-5000). Untracked files are listed individually, so any listed path can be passed as-is to stage/commit files.")]
        int maxEntries = 50,
        [Description("diff/show: true returns a name-status file list instead of the patch. Mutually exclusive with stat.")]
        bool nameOnly = false,
        [Description("diff/show: true returns --stat text instead of the patch. Mutually exclusive with nameOnly.")]
        bool stat = false,
        [Description("revert: parent to keep when reverting a MERGE commit (git revert -m N): 1 = the branch merged into (almost always right), 2 = the branch merged in. Required for a merge commit.")]
        int? mainline = null,
        [Description("log/show/diff/reset/tag: the git ref (a branch, tag, commit hash, HEAD~1, ...). log: start ref. show: the commit to show. diff: what to diff the working tree against (or a range). reset: REQUIRED, the ref to move HEAD to (e.g. \"HEAD~1\" to undo the last commit). tag create: the commit to tag (default HEAD).")]
        string? @ref = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: action is required when operation=tag, stash or worktree; refused for every other operation.
        [Description("tag/stash/worktree: REQUIRED. tag: list | create | delete. stash: list | push | apply | pop. worktree: list | add | remove. Refused for every other operation.")]
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
        [Description("worktree remove only: true allows removing a worktree that has uncommitted or untracked files, permanently discarding them (git worktree remove --force). Default false: a dirty worktree is refused with the list of changed paths. The main, current and locked worktrees are refused regardless.")]
        bool discardUncommittedChanges = false,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: hunkIds needs hunkFingerprint and exactly one path in files; alternative to lineRange; refused for every operation except stage/add.
        [Description("stage: hunk ids from Git(operation: hunks), e.g. \"1,3\". Needs files (exactly one path) and hunkFingerprint.")]
        string? hunkIds = null,
        [Description("stage: the fingerprint returned with the hunk list. Refuses with GitHunkStale if the file changed since.")]
        string? hunkFingerprint = null,
        [Description("stage: stage the hunks wholly inside these new-file lines, e.g. \"40-80\" or \"52\". Alternative to hunkIds.")]
        string? lineRange = null,
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

        // `files` and `paths` name the same concept; `paths` is the git-native spelling that `diff`
        // already used, so `files` stays an accepted alias rather than breaking callers. Setting
        // both is ambiguous, so it is refused instead of letting one silently win.
        if (!string.IsNullOrWhiteSpace(files) && !string.IsNullOrWhiteSpace(paths))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: "Both 'files' and 'paths' were supplied - they are aliases for the same list and must not be combined. Pass just one of them (either spelling is accepted).", Detail: null) };
        }
        var resolvedPaths = !string.IsNullOrWhiteSpace(files) ? files : paths;

        // `ref` is the single spelling for "a git ref"; target/commitHash/branchName stay accepted as
        // per-operation aliases. Different values across them are ambiguous, so they are refused
        // rather than letting one silently win (same policy as files/paths above).
        string? resolvedRef = null;
        if (operation is GitOperation.log or GitOperation.show or GitOperation.diff or GitOperation.reset or GitOperation.tag)
        {
            (string Name, string? Value)[] aliases = operation switch
            {
                GitOperation.tag => [],
                GitOperation.log or GitOperation.reset => [(nameof(branchName), branchName)],
                GitOperation.show => [(nameof(commitHash), commitHash), (nameof(target), target == "working" ? null : target)],
                _ => [(nameof(target), target == "working" ? null : target)],
            };
            resolvedRef = ResolveRef(@ref, aliases, out var refError);
            if (refError is not null)
            {
                return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: refError, Detail: null) };
            }
        }
        else if (!string.IsNullOrWhiteSpace(@ref))
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"ref is only supported for log/show/diff/reset/tag - operation '{operation}' takes its ref from another parameter (revert: commitHash; branch/checkout: branchName, startPoint). Omit ref.", Detail: null) };
        }

        if (mainline is not null && operation != GitOperation.revert)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"mainline is only supported for revert - operation '{operation}' does not use it. Omit mainline.", Detail: null) };
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
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"You passed hunk parameters (hunkIds/hunkFingerprint/lineRange) together with scope=\"{scope}\", which is ambiguous: hunk staging works on exactly one listed file. Drop scope, or pass scope=\"listed\" with files.", Detail: null) };
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
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ErrorCode: "InvalidArguments", Message: $"You passed both a file list and scope=\"{scope}\", which is ambiguous. scope=\"{scope}\" ignores the file list and stages/commits by scope instead. Pass scope=\"listed\" to stage/commit exactly the files you named, or drop the file list to use scope=\"{scope}\".", Detail: null) };
        }

        GitResult result = operation switch
        {
            GitOperation.status => await _gitImpl.StatusAsync(gitRoot, maxEntries, cancellationToken),
            GitOperation.log => await _gitImpl.LogAsync(gitRoot, count, resolvedRef, resolvedPaths, cancellationToken),
            GitOperation.diff => await _gitImpl.DiffAsync(gitRoot, resolvedRef ?? target, resolvedPaths, maxBytes, nameOnly, stat, cancellationToken),
            GitOperation.show => await _gitImpl.ShowAsync(gitRoot, resolvedRef ?? target, resolvedPaths, maxBytes, nameOnly, stat, cancellationToken),
            GitOperation.stage or GitOperation.add when hasHunkParams => await _gitImpl.StageHunksAsync(gitRoot, resolvedPaths, hunkIds, hunkFingerprint, lineRange, cancellationToken),
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
            GitOperation.tag => await _gitImpl.TagAsync(gitRoot, action, tagName, resolvedRef, message, count, cancellationToken),
            GitOperation.stash => await _gitImpl.StashAsync(gitRoot, action, message, includeUntracked, resolvedPaths, stashIndex, count, cancellationToken),
            GitOperation.worktree => await _gitImpl.WorktreeAsync(gitRoot, action, worktreePath, branchName, createBranch, startPoint, discardUncommittedChanges, cancellationToken),
            GitOperation.hunks => await _gitImpl.HunksAsync(gitRoot, resolvedPaths, count, cancellationToken),
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
