using System.Text;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tools.Basic;

// ─── Result types ────────────────────────────────────────────────────────────
public record GitResult
{
    public bool IsError
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }

    /// <summary>Machine-readable cause of a failure, one of <see cref="GitErrorCodes"/>, when the cause is
    /// known. Null means unclassified: the Git tool then reports the fallback code GitError. Never
    /// serialized with a success payload.</summary>
    [JsonIgnore]
    public string? ErrorKind
    {
        get; set;
    }

    /// <summary>Short actionable next step for a classified failure (which call would work). Null when
    /// the failure is unclassified.</summary>
    [JsonIgnore]
    public string? ErrorDetail
    {
        get; set;
    }
}

/// <summary>Specific ResultError.ErrorCode values for failed Git results. GitError stays the fallback.</summary>
public static class GitErrorCodes
{
    /// <summary>Fallback when a failure has no more specific known cause.</summary>
    public const string Fallback = "GitError";
    /// <summary>A named path is missing from disk and the index, or resolves outside the repository.</summary>
    public const string PathNotFound = "GitPathNotFound";
    /// <summary>A named path is refused because .gitignore ignores it (the tool never force-adds).</summary>
    public const string IgnoredPath = "GitIgnoredPath";
    /// <summary>Commit refused because nothing (or nothing for the named paths) is staged.</summary>
    public const string NothingToCommit = "GitNothingToCommit";
    /// <summary>The operation failed and left (or found) a merge, rebase, revert, cherry-pick or am in progress.</summary>
    public const string OperationInProgress = "GitOperationInProgress";
    /// <summary>A ref (reset ref, revert commitHash, checkout branchName) was required and not supplied.</summary>
    public const string RefRequired = "GitRefRequired";
    /// <summary>action is missing, or not valid for this operation (tag, stash, worktree).</summary>
    public const string ActionRequired = "GitActionRequired";
    /// <summary>The named tag, branch, worktree or path already exists and the tool will not overwrite it.</summary>
    public const string AlreadyExists = "GitAlreadyExists";
    /// <summary>A tag, stash index, worktree, branch or ref does not exist.</summary>
    public const string TargetNotFound = "GitTargetNotFound";
    /// <summary>A tag name fails git check-ref-format.</summary>
    public const string InvalidName = "GitInvalidName";
    /// <summary>The operation needs a clean tracked tree (stash apply/pop) or a clean worktree (worktree remove) and the tree is dirty.</summary>
    public const string WorkingTreeDirty = "GitWorkingTreeDirty";
    /// <summary>Nothing to act on (stash push with no changes, hunk listing or staging with no unstaged changes).</summary>
    public const string NoChanges = "GitNoChanges";
    /// <summary>Applying a stash conflicted with the current tree. The attempt was rolled back and the entry kept.</summary>
    public const string StashConflict = "GitStashConflict";
    /// <summary>A safety rule refused the call (for example a worktree path inside the repository, or removing the main, current or locked worktree).</summary>
    public const string Refused = "GitRefused";
    /// <summary>The file kind is one hunk-level staging does not handle (untracked, new, deleted, binary, rename, mode-only).</summary>
    public const string HunkUnsupported = "GitHunkUnsupported";
    /// <summary>hunkIds or lineRange selects nothing, straddles a hunk, or is malformed.</summary>
    public const string HunkSelection = "GitHunkSelection";
    /// <summary>The hunkFingerprint no longer matches the file's current diff.</summary>
    public const string HunkStale = "GitHunkStale";
}

/// <summary>Next-step text paired with <see cref="GitErrorCodes"/> in ResultError.Detail.</summary>
internal static class GitErrorDetails
{
    internal const string PathNotFound = "Paths must be repo-relative and inside the repository root. Call Git(operation: status) to list changed and untracked paths, then retry with exact names.";
    internal const string IgnoredPath = "Remove the ignored paths from files, or un-ignore them in .gitignore, then retry. This tool never force-adds ignored files.";
    internal const string NothingToCommit = "Stage changes first with Git(operation: stage, files: \"...\"), or pass files/scope on the commit call. Call Git(operation: status) to see what changed.";
    internal const string OperationInProgress = "Call Git(operation: abort) to back out of the in-progress operation, or resolve the conflicts in the working tree and commit.";
    internal const string RefRequiredReset = "Pass ref, e.g. ref: \"HEAD~1\" to undo the last commit with the working tree kept, or call Git(operation: unstage) to unstage without moving HEAD.";
    internal const string RefRequiredRevert = "Pass commitHash, e.g. a hash taken from Git(operation: log).";
    internal const string RefRequiredCheckout = "Pass branchName, with createBranch=true if it does not exist yet. Git(operation: branch) lists existing branches.";
    internal const string RefRequiredStash = "Pass stashIndex (0 = newest). Git(operation: stash, action: list) shows every entry.";
    internal const string RefRequiredTag = "Pass tagName, e.g. Git(operation: tag, action: create, tagName: 'v1.0.0').";
    internal const string RefRequiredWorktree = "Pass worktreePath (absolute, outside the repo) and, for add, branchName.";
    internal const string ActionRequired = "Pass action with one of the values named in the message. Each operation documents its own actions.";
    internal const string AlreadyExists = "Use a different name or path, or inspect the existing entry first with Git(operation: tag, action: list), Git(operation: worktree, action: list) or Git(operation: branch). This tool never overwrites an existing tag, branch or worktree.";
    internal const string TargetNotFound = "Check the exact name with Git(operation: tag, action: list), Git(operation: stash, action: list), Git(operation: worktree, action: list) or Git(operation: branch), then retry.";
    internal const string InvalidName = "Use a name that git accepts: no spaces, no '..', '~', '^', ':' or trailing '.lock'. Then retry.";
    internal const string WorkingTreeDirty = "Commit them (Git(operation: commit)) or stash them (Git(operation: stash, action: push)) first; applying onto a dirty tree can mix the two sets of changes. Git(operation: status) lists the changed paths.";
    internal const string NoChanges = "Nothing to act on. Call Git(operation: status) to see what changed, then retry with the paths that have changes.";
    internal const string StashConflict = "Check out the commit or branch the stash was created on (Git(operation: checkout)), then apply again; Git(operation: stash, action: list) shows where each entry was made.";
    internal const string Refused = "Take the safe alternative the message names, then retry. Nothing was changed.";
    internal const string HunkUnsupported = "Stage the whole file with Git(operation: stage, files: '<path>') instead, or use a file kind that has text hunks.";
    internal const string HunkSelection = "Call Git(operation: hunks, files: '<path>') and pass hunkIds or lineRange exactly as it lists them.";
    internal const string HunkStale = "Re-run Git(operation: hunks, files: '<path>') and pick again with the new hunkFingerprint. Nothing was staged.";
}

public record GitRawResult
{
    public int ExitCode
    {
        get; set;
    }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
}

public record GitStatusEntry
{
    public string Status { get; set; } = "";
    public string Path { get; set; } = "";

    // Set only for renamed/copied entries: the path the file was renamed/copied FROM (Path is the new path).
    public string? OriginalPath
    {
        get; set;
    }
}

public record GitStatusResult : GitResult
{
    public string Branch { get; set; } = "";
    public bool IsClean
    {
        get; set;
    }
    public List<GitStatusEntry> Staged { get; set; } = [];
    public List<GitStatusEntry> Unstaged { get; set; } = [];
    public List<string> Untracked { get; set; } = [];
    // Set when the total entry count exceeds the caller's maxEntries (default 50); the lists above are
    // then capped to a sample of the first 10 entries each and the Total*/*ByStatus counts carry the
    // full picture. Re-call status with a larger maxEntries (max 5000) to get every entry.
    public bool IsTruncated
    {
        get; set;
    }
    public int? TotalStagedCount
    {
        get; set;
    }
    public int? TotalUnstagedCount
    {
        get; set;
    }
    public int? TotalUntrackedCount
    {
        get; set;
    }
    public Dictionary<string, int>? StagedByStatus
    {
        get; set;
    }
    public Dictionary<string, int>? UnstagedByStatus
    {
        get; set;
    }

    /// <summary>Which multi-step operation the repository is in the middle of: "merge", "rebase",
    /// "revert", "cherry-pick" or "am". Null when none is in progress. Set by status; when set,
    /// Git(operation: abort) backs out of it.</summary>
    public string? InProgress
    {
        get; set;
    }

    /// <summary>Set only by operation=abort: which in-progress operation was aborted.</summary>
    public string? AbortedOperation
    {
        get; set;
    }
}

public record GitCommitEntry
{
    public string Hash { get; set; } = "";
    public string ShortHash { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string Message { get; set; } = "";
}

public record GitLogResult : GitResult
{
    public List<GitCommitEntry> Commits { get; set; } = [];
}

public record GitDiffResult : GitResult
{
    public string Diff { get; set; } = "";
    public int FilesChanged
    {
        get; set;
    }

       public string? Warning
    {
        get; set;
    }
}

public record GitShowResult : GitResult
{
    public string Hash { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string Message { get; set; } = "";
    public string Diff { get; set; } = "";
    public int FilesChanged
    {
        get; set;
    }

       public string? Warning
    {
        get; set;
    }
}

public record GitCommitResult : GitResult
{
    public string CommitHash { get; set; } = "";

    // Sibling of CommitHash, always its literal .Length - a byte-counted answer next to the hash
    // so a caller never has to visually count/transcribe the hex string to know its length. See
    // docs/current/finding_git_commit_commithash_length_unconfirmed_no_source_mechanism.md: every
    // "41-character hash" report to date turned out to be an agent miscounting by eye, never an
    // actual defect - this field makes that whole category of doubt unnecessary to raise.
    public int CommitHashLength => CommitHash.Length;
    public string Message { get; set; } = "";

    public List<string> RemainingStaged { get; set; } = [];
}

public record GitRevertResult : GitResult
{
    public string CommitHash { get; set; } = "";

    // See CommitHashLength on GitCommitResult - same rationale, applied here since revert returns
    // a hash-typed field too (the finding doc's guardrail originally covered commit only).
    public int CommitHashLength => CommitHash.Length;
    public string Message { get; set; } = "";
    public bool PendingCommit
    {
        get; set;
    }
}

public record GitBranchEntry
{
    public string Name { get; set; } = "";
    public bool IsCurrent
    {
        get; set;
    }
    public bool IsRemote
    {
        get; set;
    }
}

public record GitBranchResult : GitResult
{
    public List<GitBranchEntry> Branches { get; set; } = [];
    public string? Created
    {
        get; set;
    }
    public string? Deleted
    {
        get; set;
    }
}// Added by AddTopLevelType (expected - used for diagnostics)
public record GitCheckoutResult : GitResult
{
    public string Branch { get; set; } = "";
    public bool CreatedNewBranch
    {
        get; set;
    }

    /// <summary>Set when the call did something other than what its parameters literally asked
    /// for, e.g. createBranch=true on a branch that already existed (plain checkout, startPoint
    /// ignored). Null otherwise.</summary>
    public string? Note
    {
        get; set;
    }
}// Added by AddTopLevelType (expected - used for diagnostics)
public record GitRemoteResult : GitResult
{
    public string Operation { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>One tag in a tag list.</summary>
public record GitTagEntry
{
    public string Name { get; set; } = "";

    /// <summary>"lightweight" or "annotated".</summary>
    public string Kind { get; set; } = "";

    /// <summary>Abbreviated hash (7+ characters) of the commit the tag points at (peeled for annotated tags).</summary>
    public string TargetHash { get; set; } = "";

    /// <summary>Annotation subject for an annotated tag, otherwise the tagged commit's subject.</summary>
    public string Subject { get; set; } = "";
}

/// <summary>Result of operation=tag. Which fields are set depends on the action: list sets Tags,
/// TotalCount and IsTruncated; create sets Created, TargetHash and Annotated; delete sets Deleted,
/// PreviousTargetHash and Note.</summary>
public record GitTagResult : GitResult
{
    public string Action { get; set; } = "";
    public List<GitTagEntry> Tags { get; set; } = [];
    public int? TotalCount
    {
        get; set;
    }
    public bool IsTruncated
    {
        get; set;
    }
    public string? Created
    {
        get; set;
    }
    public string? Deleted
    {
        get; set;
    }
    public string? TargetHash
    {
        get; set;
    }
    public string? PreviousTargetHash
    {
        get; set;
    }
    public bool? Annotated
    {
        get; set;
    }
    public string? Note
    {
        get; set;
    }
}

/// <summary>One entry in a stash list.</summary>
public record GitStashEntry
{
    /// <summary>The N of stash@{N}; 0 is the newest entry.</summary>
    public int Index
    {
        get; set;
    }

    /// <summary>Abbreviated hash of the stash commit.</summary>
    public string Hash { get; set; } = "";

    /// <summary>Branch the stash was made on, when the entry's subject names one.</summary>
    public string? Branch
    {
        get; set;
    }

    /// <summary>The label given at push time, or git's default "&lt;hash&gt; &lt;subject&gt;" of the commit stashed on.</summary>
    public string Message { get; set; } = "";

    /// <summary>ISO 8601 commit date of the stash.</summary>
    public string Date { get; set; } = "";
}

/// <summary>Result of operation=stash. Which fields are set depends on the action: list sets Entries,
/// TotalCount and IsTruncated; push sets Created, Message, Note and Status; apply and pop set
/// AppliedIndex, Note and Status.</summary>
public record GitStashResult : GitResult
{
    public string Action { get; set; } = "";
    public List<GitStashEntry> Entries { get; set; } = [];
    public int? TotalCount
    {
        get; set;
    }
    public bool IsTruncated
    {
        get; set;
    }
    public bool? Created
    {
        get; set;
    }
    public int? AppliedIndex
    {
        get; set;
    }
    public string? Message
    {
        get; set;
    }
    public string? Note
    {
        get; set;
    }
    public GitStatusResult? Status
    {
        get; set;
    }
}

/// <summary>One worktree in a worktree list (parsed from git worktree list --porcelain).</summary>
public record GitWorktreeEntry
{
    /// <summary>Absolute path of the worktree, normalized to the platform separator.</summary>
    public string Path { get; set; } = "";

    /// <summary>Abbreviated HEAD commit hash, when git reports one.</summary>
    public string? Head
    {
        get; set;
    }

    /// <summary>Short name of the checked-out branch; null for a detached HEAD.</summary>
    public string? Branch
    {
        get; set;
    }

    /// <summary>True for the main worktree (the first entry git lists).</summary>
    public bool IsMain
    {
        get; set;
    }

    /// <summary>True when this is the worktree the Git tool is operating in (the loaded solution's repository).</summary>
    public bool IsCurrent
    {
        get; set;
    }
    public bool IsDetached
    {
        get; set;
    }
    public bool IsLocked
    {
        get; set;
    }

    /// <summary>True when git reports the worktree directory as missing or stale (git worktree prune would remove it).</summary>
    public bool IsPrunable
    {
        get; set;
    }
}

/// <summary>One zero-context hunk of a file's unstaged diff, as listed by operation=hunks.</summary>
public record GitHunkEntry
{
    /// <summary>1-based hunk id; pass it in hunkIds when staging.</summary>
    public int Index { get; set; }
    public int OldStart { get; set; }
    public int OldCount { get; set; }
    public int NewStart { get; set; }
    public int NewCount { get; set; }

    /// <summary>Text after the second @@ in the hunk header (git's function-context hint); may be empty.</summary>
    public string Header { get; set; } = "";
    public int Added { get; set; }
    public int Removed { get; set; }

    /// <summary>The first few changed lines (+/-), decoded as UTF-8 and truncated.</summary>
    public string Preview { get; set; } = "";
}

/// <summary>Result of operation=hunks: the unstaged hunks of one file. Fingerprint covers the whole file diff,
/// not only the listed hunks, and must be passed back when staging.</summary>
public record GitHunksResult : GitResult
{
    public string Path { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int TotalHunks { get; set; }
    public bool IsTruncated { get; set; }
    public List<GitHunkEntry> Hunks { get; set; } = [];
    public string? Note { get; set; }
}

/// <summary>Result of staging selected hunks of one file. The inherited GitStatusResult members are the
/// repository status after the stage (copied from a StatusAsync result), so the caller sees the new index
/// state without a second call. Only the index changes; the working tree is never touched.</summary>
public record GitStageHunksResult : GitStatusResult
{
    public string Path { get; set; } = "";

    /// <summary>The hunks that were staged, as numbered in the listing the selection was made from.</summary>
    public List<GitHunkEntry> StagedHunks { get; set; } = [];

    /// <summary>How many unstaged hunks the file still has after the stage.</summary>
    public int RemainingUnstagedHunks { get; set; }

    /// <summary>Set when the remaining count is not what the selection predicts; verify with a staged diff.</summary>
    public string? Warning { get; set; }
}

/// <summary>Result of operation=worktree. Which fields are set depends on the action: list sets Worktrees;
/// add sets Added, Branch and Note; remove sets Removed, Branch, Note and, when uncommitted changes were
/// discarded, DiscardedPaths.</summary>
public record GitWorktreeResult : GitResult
{
    public string Action { get; set; } = "";
    public List<GitWorktreeEntry> Worktrees { get; set; } = [];
    public string? Added
    {
        get; set;
    }
    public string? Removed
    {
        get; set;
    }
    public string? Branch
    {
        get; set;
    }
    public List<string>? DiscardedPaths
    {
        get; set;
    }
    public string? Note
    {
        get; set;
    }
}

// ── Operation implementations ─────────────────────────────────────────────

public class GitImpl : IGitOperations
{
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger<GitImpl> _logger;

    /// <summary>
    /// Bound on how long a single git subprocess invocation may run. Applied whenever no caller-
    /// supplied CancellationToken already carries a shorter deadline -> MCP tool calls that omit
    /// cancellationToken get CancellationToken.None here, which would otherwise let a hung git
    /// process (see docs/TODO.md's "Git(operation: status) hung indefinitely" entry) block forever.
    /// </summary>
    private static readonly TimeSpan GitProcessTimeout = TimeSpan.FromSeconds(30);

    // Resolved once and reused: repeatedly letting CreateProcess re-resolve the bare "git" name
    // through PATH on every call is themselves a plausible source of the intermittent 30s stalls
    // documented in docs/current/blockers/blocking_error_git_status_timeout.md (first hang is always
    // the process-spawn itself, not anything git does once running). Falls back to "git" if PATH
    // resolution fails here, preserving prior behavior.
    private static readonly string GitExecutablePath = ResolveGitExecutablePath();

    public GitImpl(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _logger = new NullLogger<GitImpl>();
    }

    public GitImpl(ISolutionProvider workspaceManager, ILogger<GitImpl> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    /// <summary>
    /// Refusal for a tag, stash or worktree call whose action is missing or not valid for that
    /// operation. Runs nothing, so the message says nothing was changed.
    /// </summary>
    internal static T ActionRefused<T>(string operation, GitAction? action, string validList) where T : GitResult, new()
    {
        var got = action is null ? "none" : action.Value.ToString();
        return new T
        {
            IsError = true,
            ErrorKind = GitErrorCodes.ActionRequired,
            ErrorDetail = $"Pass action, e.g. Git(operation: {operation}, action: list).",
            Error = $"operation={operation} needs action to be one of: {validList} (got {got}). Nothing was changed."
        };
    }

    /// <summary>
    /// Catch-all for an unexpected exception in a new Git operation. Logs the full exception and
    /// returns a fixed message, so no exception text or internal path reaches the caller.
    /// </summary>
    internal T UnexpectedFailure<T>(Exception ex, string what) where T : GitResult, new()
    {
        _logger.LogError(ex, "Git {What} failed unexpectedly", what);
        return new T
        {
            IsError = true,
            Error = $"Git {what} failed unexpectedly; the server log has details. Nothing further was changed."
        };
    }

    private static GitTagResult TagFailure(string action, string kind, string error, string detail) => new()
    {
        Action = action,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        ErrorDetail = detail
    };

    /// <summary>
    /// Lists, creates or deletes a local tag. Never moves or overwrites an existing tag, never signs,
    /// never pushes, and deletes exactly one named tag (no patterns, no -f).
    /// </summary>
    public async Task<GitTagResult> TagAsync(
        string gitRoot, GitAction? action, string? tagName, string? refName, string? message, int count, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case GitAction.list:
                    return await TagListAsync(gitRoot, count, cancellationToken);
                case GitAction.create:
                    return await TagCreateAsync(gitRoot, tagName, refName, message, cancellationToken);
                case GitAction.delete:
                    return await TagDeleteAsync(gitRoot, tagName, cancellationToken);
                default:
                    return ActionRefused<GitTagResult>("tag", action, "list, create, delete");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitTagResult>(ex, "tag");
        }
    }

    private async Task<GitTagResult> TagListAsync(string gitRoot, int count, CancellationToken cancellationToken)
    {
        // Annotated tags have objecttype=tag and a peeled *objectname; lightweight tags have an empty
        // *objectname and their objectname is the commit itself.
        var raw = await RunGitAsync(gitRoot,
            ["for-each-ref", "--sort=-creatordate",
             "--format=%(refname:short)%09%(objecttype)%09%(*objectname:short)%09%(objectname:short)%09%(contents:subject)",
             "refs/tags"], cancellationToken);
        if (raw.ExitCode != 0)
            return new GitTagResult { Action = "list", IsError = true, Error = $"git tag list failed: {CleanGitStderr(raw.Stderr)}" };

        var all = new List<GitTagEntry>();
        foreach (var rawLine in raw.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('\t', 5);
            if (parts.Length < 4) continue;
            all.Add(new GitTagEntry
            {
                Name = parts[0],
                Kind = parts[1] == "tag" ? "annotated" : "lightweight",
                TargetHash = parts[2].Length > 0 ? parts[2] : parts[3],
                Subject = parts.Length > 4 ? parts[4] : "",
            });
        }

        var take = Math.Clamp(count, 1, 100);
        return new GitTagResult
        {
            Action = "list",
            IsError = false,
            Tags = all.Take(take).ToList(),
            TotalCount = all.Count,
            IsTruncated = all.Count > take,
        };
    }

    private async Task<GitTagResult> TagCreateAsync(
        string gitRoot, string? tagName, string? refName, string? message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return TagFailure("create", GitErrorCodes.RefRequired,
                "Git tag create needs tagName. Nothing was changed.", GitErrorDetails.RefRequiredTag);
        var name = tagName.Trim();

        // A leading '-' passes check-ref-format but would be read as an option by git tag.
        var formatOk = !name.StartsWith('-');
        if (formatOk)
        {
            var fmt = await RunGitAsync(gitRoot, ["check-ref-format", $"refs/tags/{name}"], cancellationToken);
            formatOk = fmt.ExitCode == 0;
        }
        if (!formatOk)
            return TagFailure("create", GitErrorCodes.InvalidName,
                $"'{name}' is not a valid tag name (no spaces, '..', '~', '^', ':', leading '-' or trailing '.lock'). Nothing was changed.",
                GitErrorDetails.InvalidName);

        var existing = await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"refs/tags/{name}"], cancellationToken);
        if (existing.ExitCode == 0)
        {
            var existingShort = await RunGitAsync(gitRoot, ["rev-parse", "--short", "--verify", "--quiet", $"refs/tags/{name}^{{commit}}"], cancellationToken);
            var existingHash = existingShort.ExitCode == 0 ? existingShort.Stdout.Trim() : existing.Stdout.Trim();
            return TagFailure("create", GitErrorCodes.AlreadyExists,
                $"Tag '{name}' already exists (points at {existingHash}). This tool never overwrites or moves a tag. Use a new name, or delete it first (Git(operation: tag, action: delete, tagName: '{name}')) and recreate. Nothing was changed.",
                GitErrorDetails.AlreadyExists);
        }

        var target = string.IsNullOrWhiteSpace(refName) ? "HEAD" : refName.Trim();
        // A ref starting with '-' could be read as an option, so it is treated as unresolvable.
        var resolved = target.StartsWith('-')
            ? new GitRawResult { ExitCode = 1 }
            : await RunGitAsync(gitRoot, ["rev-parse", "--short", "--verify", "--quiet", $"{target}^{{commit}}"], cancellationToken);
        var targetHash = resolved.Stdout.Trim();
        if (resolved.ExitCode != 0 || targetHash.Length == 0)
            return TagFailure("create", GitErrorCodes.TargetNotFound,
                $"Ref '{target}' does not resolve to a commit. Nothing was changed.",
                GitErrorDetails.TargetNotFound);

        var annotated = !string.IsNullOrWhiteSpace(message);
        string[] createArgs = annotated
            ? ["tag", "-a", "-m", message!, name, targetHash]
            : ["tag", name, targetHash];
        var created = await RunGitAsync(gitRoot, createArgs, cancellationToken);
        if (created.ExitCode != 0)
            return new GitTagResult
            {
                Action = "create",
                IsError = true,
                Error = $"git tag failed: {CleanGitStderr(created.Stderr)}. Nothing was changed."
            };

        return new GitTagResult
        {
            Action = "create",
            IsError = false,
            Created = name,
            TargetHash = targetHash,
            Annotated = annotated,
        };
    }

    private async Task<GitTagResult> TagDeleteAsync(string gitRoot, string? tagName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return TagFailure("delete", GitErrorCodes.RefRequired,
                "Git tag delete needs tagName. Nothing was changed.", GitErrorDetails.RefRequiredTag);
        var name = tagName.Trim();

        var existing = await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"refs/tags/{name}"], cancellationToken);
        if (existing.ExitCode != 0)
        {
            var names = await RunGitAsync(gitRoot, ["for-each-ref", "--format=%(refname:short)", "refs/tags"], cancellationToken);
            var known = names.ExitCode == 0
                ? names.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(n => n.TrimEnd('\r')).Where(n => n.Length > 0).ToList()
                : [];
            var listed = known.Count == 0
                ? "The repository has no tags."
                : $"Existing tags: {string.Join(", ", known.Take(10))}{(known.Count > 10 ? $" and {known.Count - 10} more" : "")}.";
            return TagFailure("delete", GitErrorCodes.TargetNotFound,
                $"Tag '{name}' does not exist. {listed} Nothing was changed.",
                GitErrorDetails.TargetNotFound);
        }

        var kindRaw = await RunGitAsync(gitRoot, ["for-each-ref", "--format=%(objecttype)", $"refs/tags/{name}"], cancellationToken);
        var wasAnnotated = kindRaw.ExitCode == 0 && kindRaw.Stdout.Trim() == "tag";
        var prevRaw = await RunGitAsync(gitRoot, ["rev-parse", "--short", "--verify", "--quiet", $"refs/tags/{name}^{{commit}}"], cancellationToken);
        var previous = prevRaw.ExitCode == 0 ? prevRaw.Stdout.Trim() : "";

        // Exactly one literal name: never -f and never a pattern.
        var deleted = await RunGitAsync(gitRoot, ["tag", "-d", name], cancellationToken);
        if (deleted.ExitCode != 0)
            return new GitTagResult
            {
                Action = "delete",
                IsError = true,
                Error = $"git tag -d failed: {CleanGitStderr(deleted.Stderr)}. Nothing was changed."
            };

        var note = $"Deleted locally only. To undo: Git(operation: tag, action: create, tagName: '{name}', ref: '{previous}').";
        if (wasAnnotated)
            note += " The tag was annotated; its message and tagger are not restored by that call (pass message to make the recreated tag annotated).";
        return new GitTagResult
        {
            Action = "delete",
            IsError = false,
            Deleted = name,
            PreviousTargetHash = previous,
            Note = note,
        };
    }

    /// <summary>
    /// Lists, pushes, applies or pops a stash entry. Never drops, clears, uses --index, --keep-index or
    /// --all, and never stashes patches. A conflicted apply/pop is rolled back (see StashRollbackConflictAsync).
    /// </summary>
    public async Task<GitStashResult> StashAsync(
        string gitRoot, GitAction? action, string? message, bool includeUntracked, string? paths, int? stashIndex, int count, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case GitAction.list:
                    return await StashListAsync(gitRoot, count, cancellationToken);
                case GitAction.push:
                    return await StashPushAsync(gitRoot, message, includeUntracked, paths, cancellationToken);
                case GitAction.apply:
                    return await StashRestoreAsync(gitRoot, false, stashIndex, cancellationToken);
                case GitAction.pop:
                    return await StashRestoreAsync(gitRoot, true, stashIndex, cancellationToken);
                default:
                    return ActionRefused<GitStashResult>("stash", action, "list, push, apply, pop");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitStashResult>(ex, "stash");
        }
    }

    private static GitStashResult StashFailure(string action, string kind, string error, string detail) => new()
    {
        Action = action,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        ErrorDetail = detail
    };

    private async Task<GitStashResult> StashRestoreAsync(
    string gitRoot, bool pop, int? stashIndex, CancellationToken cancellationToken)
    {
        var action = pop ? "pop" : "apply";
        if (stashIndex is null)
            return StashFailure(action, GitErrorCodes.RefRequired,
                $"Git stash {action} needs stashIndex (0 = newest). Nothing was changed.", GitErrorDetails.RefRequiredStash);
        var index = stashIndex.Value;

        var entriesBefore = await StashCountAsync(gitRoot, cancellationToken);
        if (entriesBefore < 0)
            return new GitStashResult { Action = action, IsError = true, Error = "Could not read the stash list. Nothing was changed." };
        if (index < 0 || index >= entriesBefore)
            return StashFailure(action, GitErrorCodes.TargetNotFound,
                $"stash@{{{index}}} does not exist: the stash has {entriesBefore} {(entriesBefore == 1 ? "entry" : "entries")}. Nothing was changed.",
                GitErrorDetails.TargetNotFound);

        // Precondition: no tracked changes, staged or unstaged. The D-18 rollback below relies on it.
        var dirty = await TrackedChangePathsAsync(gitRoot, cancellationToken);
        if (dirty is null)
            return new GitStashResult { Action = action, IsError = true, Error = "Could not read the working tree status. Nothing was changed." };
        if (dirty.Count > 0)
            return StashFailure(action, GitErrorCodes.WorkingTreeDirty,
                $"Cannot {action} stash@{{{index}}}: the tracked tree has uncommitted changes in: {FormatPathSample(dirty)}. Nothing was changed.",
                GitErrorDetails.WorkingTreeDirty);

        // Deliberately no --index: staged state is not restored.
        var raw = await RunGitAsync(gitRoot, ["stash", action, $"stash@{{{index}}}"], cancellationToken);
        if (raw.ExitCode == 0)
        {
            var entriesAfter = await StashCountAsync(gitRoot, cancellationToken);
            var expected = pop ? entriesBefore - 1 : entriesBefore;
            var note = StashDiskNote + " Staged state is not restored (--index is not used).";
            note += entriesAfter == expected
                ? (pop ? $" stash@{{{index}}} was applied and dropped; {entriesAfter} stash {(entriesAfter == 1 ? "entry" : "entries")} left."
                       : $" stash@{{{index}}} was applied and kept; {entriesAfter} stash {(entriesAfter == 1 ? "entry" : "entries")} total.")
                : $" Expected {expected} stash entries afterwards but found {entriesAfter}; check Git(operation: stash, action: list).";
            return new GitStashResult
            {
                Action = action,
                IsError = false,
                AppliedIndex = index,
                Note = note,
                Status = await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken),
            };
        }

        var unmerged = await UnmergedPathsAsync(gitRoot, cancellationToken);
        if (unmerged is { Count: > 0 })
            return await StashRollbackConflictAsync(gitRoot, action, index, entriesBefore, unmerged, cancellationToken);

        // Non-zero exit without unmerged paths (for example an untracked file in the way). git may
        // still have applied part of the stash, so claim "Nothing was changed" only after verifying.
        var trackedNow = await TrackedChangePathsAsync(gitRoot, cancellationToken);
        var entriesNow = await StashCountAsync(gitRoot, cancellationToken);
        var stderr = CleanGitStderr(raw.Stderr);
        GitStashResult failure;
        if (unmerged is not null && trackedNow is { Count: 0 } && entriesNow == entriesBefore)
            failure = new GitStashResult
            {
                Action = action,
                IsError = true,
                Error = $"git stash {action} failed: {stderr}. Nothing was changed."
            };
        else
            failure = new GitStashResult
            {
                Action = action,
                IsError = true,
                Error = $"git stash {action} failed: {stderr}. The tree could not be verified clean afterwards" +
                        (trackedNow is { Count: > 0 } ? $" (tracked changes in: {FormatPathSample(trackedNow)})" : "") +
                        $"; stash entries: {entriesNow} (was {entriesBefore}). The stash entry was kept. Run Git(operation: status) before editing."
            };
        return await AppendInProgressAsync(failure, gitRoot, cancellationToken);
    }

    /// <summary>
    /// D-18: rolls back a conflicted stash apply/pop and verifies the result structurally. Reports a
    /// clean rollback only when the tracked tree is clean, no path is unmerged and the stash entry
    /// count is unchanged; otherwise says exactly which check failed.
    /// </summary>
    private async Task<GitStashResult> StashRollbackConflictAsync(
        string gitRoot, string action, int stashIndex, int entriesBefore, List<string> conflicted, CancellationToken cancellationToken)
    {
        // Safe only because the precondition in the caller guarantees a clean tracked tree: reset --merge
        // removes the conflicted stash application and nothing else. This is the only reset form the
        // tool runs internally; it is not exposed as an operation.
        var reset = await RunGitAsync(gitRoot, ["reset", "--merge"], cancellationToken);

        var failed = new List<string>();
        if (reset.ExitCode != 0)
            failed.Add($"git reset --merge exited {reset.ExitCode}");
        var trackedAfter = await TrackedChangePathsAsync(gitRoot, cancellationToken);
        if (trackedAfter is null)
            failed.Add("git status could not be read");
        else if (trackedAfter.Count > 0)
            failed.Add($"tracked changes remain in: {FormatPathSample(trackedAfter)}");
        var unmergedAfter = await UnmergedPathsAsync(gitRoot, cancellationToken);
        if (unmergedAfter is null)
            failed.Add("unmerged paths could not be read");
        else if (unmergedAfter.Count > 0)
            failed.Add($"unmerged paths remain: {FormatPathSample(unmergedAfter)}");
        var entriesAfter = await StashCountAsync(gitRoot, cancellationToken);
        if (entriesAfter != entriesBefore)
            failed.Add($"stash entry count is {entriesAfter}, expected {entriesBefore}");

        if (failed.Count == 0)
            return StashFailure(action, GitErrorCodes.StashConflict,
                $"Applying stash@{{{stashIndex}}} conflicts with the current tree in: {FormatPathSample(conflicted)}. " +
                $"The attempt was rolled back (git reset --merge): the tracked tree is clean again and stash@{{{stashIndex}}} was kept. Nothing was changed.",
                GitErrorDetails.StashConflict);

        return StashFailure(action, GitErrorCodes.StashConflict,
            $"Applying stash@{{{stashIndex}}} conflicts, and the rollback could not be verified ({string.Join("; ", failed)}). " +
            "The stash entry was kept. Run Git(operation: status) before editing.",
            "Call Git(operation: status) to see the tree; Git(operation: stash, action: list) shows the stash entries.");
    }

    private async Task<GitStashResult> StashPushAsync(
    string gitRoot, string? message, bool includeUntracked, string? paths, CancellationToken cancellationToken)
    {
        var pathList = new List<string>();
        if (!string.IsNullOrWhiteSpace(paths))
        {
            var parsed = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
            if (pathsError != null)
                return new GitStashResult { Action = "push", IsError = true, Error = $"{pathsError}. Nothing was changed." };
            foreach (var p in parsed!)
            {
                var pathError = ValidateRepoRoot(gitRoot, p);
                if (pathError != null)
                    return StashFailure("push", GitErrorCodes.PathNotFound, $"{pathError}. Nothing was changed.", GitErrorDetails.PathNotFound);
                pathList.Add(p);
            }
        }

        // Structural precondition. git itself exits 0 with "No local changes to save", so its exit
        // code cannot be trusted to say whether an entry was created.
        string[] statusArgs = pathList.Count > 0
            ? ["--literal-pathspecs", "-c", "core.quotePath=false", "status", "--porcelain",
               includeUntracked ? "--untracked-files=normal" : "--untracked-files=no", "--", .. pathList]
            : ["-c", "core.quotePath=false", "status", "--porcelain",
               includeUntracked ? "--untracked-files=normal" : "--untracked-files=no"];
        var statusRaw = await RunGitAsync(gitRoot, statusArgs, cancellationToken);
        if (statusRaw.ExitCode != 0)
            return await AppendInProgressAsync(new GitStashResult
            {
                Action = "push",
                IsError = true,
                Error = $"git status failed: {CleanGitStderr(statusRaw.Stderr)}. Nothing was changed."
            }, gitRoot, cancellationToken);
        if (statusRaw.Stdout.Trim().Length == 0)
            return StashFailure("push", GitErrorCodes.NoChanges,
                $"Nothing to stash: no modified tracked files{(includeUntracked ? ", and no untracked files" : "")}{(pathList.Count > 0 ? " among the named paths" : "")}. Nothing was changed.",
                GitErrorDetails.NoChanges);

        var before = (await RunGitAsync(gitRoot, ["rev-parse", "-q", "--verify", "refs/stash"], cancellationToken)).Stdout.Trim();

        // --literal-pathspecs only when the caller named paths: git (2.55, verified) runs stash -u's internal
        // clean of the untracked files with a magic pathspec of its own, and with the flag set and no caller
        // paths that clean removes nothing, leaving the files both in the stash and in the working tree.
        var pushArgs = pathList.Count > 0
            ? new List<string> { "--literal-pathspecs", "stash", "push" }
            : new List<string> { "stash", "push" };
        if (includeUntracked) pushArgs.Add("--include-untracked");
        if (!string.IsNullOrWhiteSpace(message)) { pushArgs.Add("-m"); pushArgs.Add(message); }
        if (pathList.Count > 0) { pushArgs.Add("--"); pushArgs.AddRange(pathList); }
        var pushed = await RunGitAsync(gitRoot, [.. pushArgs], cancellationToken);
        if (pushed.ExitCode != 0)
        {
            if (pushed.Stderr.Contains("did not match any file(s)"))
                return StashFailure("push", GitErrorCodes.PathNotFound,
                    $"{CleanGitStderr(pushed.Stderr)}. Nothing was changed.", GitErrorDetails.PathNotFound);
            return await AppendInProgressAsync(new GitStashResult
            {
                Action = "push",
                IsError = true,
                Error = $"git stash push failed: {CleanGitStderr(pushed.Stderr)}. Nothing was changed."
            }, gitRoot, cancellationToken);
        }

        var after = (await RunGitAsync(gitRoot, ["rev-parse", "-q", "--verify", "refs/stash"], cancellationToken)).Stdout.Trim();
        if (after.Length == 0 || after == before)
            return StashFailure("push", GitErrorCodes.NoChanges,
                "Nothing was stashed: git reported success but no stash entry was created. Nothing was changed.",
                GitErrorDetails.NoChanges);

        var top = await StashListAsync(gitRoot, 1, cancellationToken);
        return new GitStashResult
        {
            Action = "push",
            IsError = false,
            Created = true,
            Message = top.Entries.FirstOrDefault()?.Message ?? message,
            Note = StashDiskNote + " Staged changes come back unstaged on pop/apply (--index is not used).",
            Status = await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken),
        };
    }

    private const string StashDiskNote = "Tracked files changed on disk; call LoadSolution(forceReload: true) before editing C#.";

    private async Task<GitStashResult> StashListAsync(string gitRoot, int count, CancellationToken cancellationToken)
    {
        // %gd = stash@{N}, %gs = reflog subject ("On <branch>: <label>" or "WIP on <branch>: <hash> <subject>").
        var raw = await RunGitAsync(gitRoot,
            ["stash", "list", "--format=%gd%x09%h%x09%cI%x09%gs"], cancellationToken);
        if (raw.ExitCode != 0)
            return new GitStashResult { Action = "list", IsError = true, Error = $"git stash list failed: {CleanGitStderr(raw.Stderr)}" };

        var all = new List<GitStashEntry>();
        foreach (var rawLine in raw.Stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('\t', 4);
            if (parts.Length < 4) continue;
            var refMatch = System.Text.RegularExpressions.Regex.Match(parts[0], @"^stash@\{(\d+)\}$");
            if (!refMatch.Success) continue;

            string? branch = null;
            var message = parts[3];
            var subject = System.Text.RegularExpressions.Regex.Match(message, @"^(?:WIP on|On) ([^:]+): (.*)$");
            if (subject.Success)
            {
                branch = subject.Groups[1].Value;
                message = subject.Groups[2].Value;
            }
            all.Add(new GitStashEntry
            {
                Index = int.Parse(refMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                Hash = parts[1],
                Date = parts[2],
                Branch = branch,
                Message = message,
            });
        }

        var take = Math.Clamp(count, 1, 100);
        return new GitStashResult
        {
            Action = "list",
            IsError = false,
            Entries = all.Take(take).ToList(),
            TotalCount = all.Count,
            IsTruncated = all.Count > take,
        };
    }

    /// <summary>Up to 10 paths, comma-separated, with a "(+N more)" suffix when there are more.</summary>
    private static string FormatPathSample(IReadOnlyList<string> paths)
    {
        var shown = string.Join(", ", paths.Take(10));
        return paths.Count > 10 ? $"{shown} (+{paths.Count - 10} more)" : shown;
    }

    /// <summary>Paths with unresolved merge conflicts (git diff --diff-filter=U), or null when git could not be asked.</summary>
    private async Task<List<string>?> UnmergedPathsAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var raw = await RunGitAsync(gitRoot,
            ["-c", "core.quotePath=false", "diff", "--name-only", "--diff-filter=U"], cancellationToken);
        if (raw.ExitCode != 0)
            return null;
        return raw.Stdout.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>Paths with staged or unstaged changes to tracked files (untracked files ignored), or null when
    /// git could not be asked. Empty means the tracked tree is clean.</summary>
    private async Task<List<string>?> TrackedChangePathsAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var raw = await RunGitAsync(gitRoot,
            ["-c", "core.quotePath=false", "status", "--porcelain", "--untracked-files=no"], cancellationToken);
        if (raw.ExitCode != 0)
            return null;
        return raw.Stdout.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 3)
            .Select(l => l[3..])
            .ToList();
    }

    /// <summary>Number of stash entries, or -1 when git could not be asked.</summary>
    private async Task<int> StashCountAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var raw = await RunGitAsync(gitRoot, ["stash", "list", "--format=%gd"], cancellationToken);
        if (raw.ExitCode != 0)
            return -1;
        return raw.Stdout.Split('\n').Count(l => l.Trim().Length > 0);
    }

    /// <summary>
    /// Lists, adds or removes a linked worktree. Never prunes, locks, unlocks or moves, never detaches HEAD,
    /// and removes a dirty worktree only behind the explicit discardUncommittedChanges flag (D-20).
    /// </summary>
    public async Task<GitWorktreeResult> WorktreeAsync(
        string gitRoot, GitAction? action, string? worktreePath, string? branchName, bool createBranch, string? startPoint,
        bool discardUncommittedChanges, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case GitAction.list:
                    var (entries, listError) = await ListWorktreesAsync(gitRoot, cancellationToken);
                    return entries is null
                        ? new GitWorktreeResult { Action = "list", IsError = true, Error = $"git worktree list failed: {listError}" }
                        : new GitWorktreeResult { Action = "list", IsError = false, Worktrees = entries };
                case GitAction.add:
                    return await WorktreeAddAsync(gitRoot, worktreePath, branchName, createBranch, startPoint, cancellationToken);
                case GitAction.remove:
                    return await WorktreeRemoveAsync(gitRoot, worktreePath, discardUncommittedChanges, cancellationToken);
                default:
                    return ActionRefused<GitWorktreeResult>("worktree", action, "list, add, remove");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitWorktreeResult>(ex, "worktree");
        }
    }

    private static GitWorktreeResult WorktreeFailure(string action, string kind, string error, string detail) => new()
    {
        Action = action,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        ErrorDetail = detail
    };

    private static class WorktreeNativePaths
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
        internal static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);
    }

    /// <summary>
    /// Expands Windows 8.3 short names (for example C:\Users\ADMINI~1) in the part of the path that exists.
    /// git reports worktree paths in long form, so a path built from a short-form temp directory would
    /// otherwise never compare equal to git's. Returns the input unchanged off Windows or on any failure.
    /// </summary>
    private static string ExpandShortNames(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
            return fullPath;
        try
        {
            var existing = fullPath;
            var tail = "";
            while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing) && !File.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent))
                    return fullPath;
                tail = Path.Combine(Path.GetFileName(existing), tail);
                existing = parent;
            }
            if (string.IsNullOrEmpty(existing))
                return fullPath;
            var buffer = new StringBuilder(1024);
            var length = WorktreeNativePaths.GetLongPathName(existing, buffer, (uint)buffer.Capacity);
            if (length == 0 || length >= buffer.Capacity)
                return fullPath;
            var expanded = buffer.ToString();
            return tail.Length == 0 ? expanded : Path.Combine(expanded, tail);
        }
        catch
        {
            return fullPath;
        }
    }

    /// <summary>
    /// Canonical form used to compare worktree paths: full path, platform separator, 8.3 short names
    /// expanded, no trailing separator. Compare results with OrdinalIgnoreCase.
    /// </summary>
    private static string NormalizeWorktreePath(string path)
    {
        var full = Path.GetFullPath(path.Trim()).Replace('/', Path.DirectorySeparatorChar);
        full = ExpandShortNames(full);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    /// <summary>
    /// Parses git worktree list --porcelain: blocks separated by a blank line, each starting with
    /// "worktree &lt;path&gt;" followed by HEAD, branch | detached, and optional locked / prunable lines.
    /// The first block is the main worktree. Parsed structurally; git's localizable messages are never matched.
    /// Returns (null, stderr) when git could not be asked.
    /// </summary>
    private async Task<(List<GitWorktreeEntry>? Entries, string? Error)> ListWorktreesAsync(
        string gitRoot, CancellationToken cancellationToken)
    {
        var raw = await RunGitAsync(gitRoot, ["worktree", "list", "--porcelain"], cancellationToken);
        if (raw.ExitCode != 0)
            return (null, CleanGitStderr(raw.Stderr));

        var current = NormalizeWorktreePath(gitRoot);
        var entries = new List<GitWorktreeEntry>();
        GitWorktreeEntry? block = null;
        foreach (var rawLine in raw.Stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                block = null;
                continue;
            }
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                var path = NormalizeWorktreePath(line["worktree ".Length..]);
                block = new GitWorktreeEntry
                {
                    Path = path,
                    IsMain = entries.Count == 0,
                    IsCurrent = path.Equals(current, StringComparison.OrdinalIgnoreCase),
                };
                entries.Add(block);
            }
            else if (block is null)
            {
                continue;
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                var head = line["HEAD ".Length..];
                block.Head = head.Length > 8 ? head[..8] : head;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var branch = line["branch ".Length..];
                block.Branch = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
            }
            else if (line == "detached")
            {
                block.IsDetached = true;
            }
            else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
            {
                block.IsLocked = true;
            }
            else if (line == "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                block.IsPrunable = true;
            }
        }
        return (entries, null);
    }

    private async Task<GitWorktreeResult> WorktreeAddAsync(
    string gitRoot, string? worktreePath, string? branchName, bool createBranch, string? startPoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || string.IsNullOrWhiteSpace(branchName))
        {
            var missing = string.IsNullOrWhiteSpace(worktreePath) && string.IsNullOrWhiteSpace(branchName) ? "worktreePath and branchName"
                : string.IsNullOrWhiteSpace(worktreePath) ? "worktreePath" : "branchName";
            return WorktreeFailure("add", GitErrorCodes.RefRequired,
                $"Git worktree add needs {missing}. Nothing was changed.", GitErrorDetails.RefRequiredWorktree);
        }
        var path = worktreePath.Trim();
        var branch = branchName.Trim();

        // (a) absolute path. Fully qualified, so "\foo" or "C:foo" cannot resolve against the server's cwd.
        if (!Path.IsPathFullyQualified(path))
            return WorktreeFailure("add", GitErrorCodes.Refused,
                $"worktreePath must be an absolute path (got '{path}'). Nothing was changed.",
                "Pass a full path outside the repository, for example a sibling directory of the repository root.");

        var repoRoot = NormalizeWorktreePath(gitRoot);
        var target = NormalizeWorktreePath(path);

        // (b) outside the repository root. ValidateRepoRoot returns null when the path is inside (or equal to) the root.
        if (ValidateRepoRoot(repoRoot, target) is null)
        {
            var parent = Path.GetDirectoryName(repoRoot);
            var example = parent is null
                ? ""
                : $" e.g. '{Path.Combine(parent, $"{Path.GetFileName(repoRoot)}-{branch.Replace('/', '-')}")}'";
            return WorktreeFailure("add", GitErrorCodes.Refused,
                "A worktree inside the repository would be compiled into the loaded solution and shows up in this repo's diffs (the Worktree/ trap). " +
                $"Use a sibling directory,{(example.Length > 0 ? example : " outside the repository root")}. Nothing was changed.",
                GitErrorDetails.Refused);
        }

        // startPoint only means something when a branch is being created; dropping it silently would
        // leave the caller believing the worktree was based on it (same rule as CheckoutAsync).
        if (!createBranch && !string.IsNullOrWhiteSpace(startPoint))
            return WorktreeFailure("add", GitErrorCodes.Refused,
                $"startPoint '{startPoint}' was supplied without createBranch=true, so it would be ignored. " +
                "Pass createBranch=true to create the branch from it, or omit startPoint to add a worktree for the existing branch. Nothing was changed.",
                GitErrorDetails.Refused);

        // A leading '-' would be read by git as an option.
        var nameOk = !branch.StartsWith('-');
        if (nameOk && createBranch)
        {
            var fmt = await RunGitAsync(gitRoot, ["check-ref-format", $"refs/heads/{branch}"], cancellationToken);
            nameOk = fmt.ExitCode == 0;
        }
        if (!nameOk)
            return WorktreeFailure("add", GitErrorCodes.InvalidName,
                $"'{branch}' is not a valid branch name (no spaces, '..', '~', '^', ':', leading '-' or trailing '.lock'). Nothing was changed.",
                GitErrorDetails.InvalidName);

        // (c) the target path must be free: absent, or an existing empty directory.
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            return WorktreeFailure("add", GitErrorCodes.AlreadyExists,
                $"Path '{path}' already exists and is not empty; a worktree cannot be added there. Nothing was changed.",
                GitErrorDetails.AlreadyExists);

        var exists = (await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken)).ExitCode == 0;
        if (!createBranch && !exists)
            return WorktreeFailure("add", GitErrorCodes.TargetNotFound,
                $"Branch '{branch}' does not exist. Nothing was changed.",
                "Pass createBranch=true to create it (optionally with startPoint), or list the existing branches with Git(operation: branch).");
        if (createBranch && exists)
            return WorktreeFailure("add", GitErrorCodes.AlreadyExists,
                $"Branch '{branch}' already exists, and this tool does not silently reuse it for a new worktree. " +
                "Pass createBranch=false to check the existing branch out in the new worktree, or choose a new branch name. Nothing was changed.",
                GitErrorDetails.AlreadyExists);

        string? baseRef = null;
        if (createBranch)
        {
            baseRef = string.IsNullOrWhiteSpace(startPoint) ? "HEAD" : startPoint.Trim();
            // A start point starting with '-' could be read as an option, so it is treated as unresolvable.
            var resolved = baseRef.StartsWith('-')
                ? new GitRawResult { ExitCode = 1 }
                : await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"{baseRef}^{{commit}}"], cancellationToken);
            if (resolved.ExitCode != 0)
                return WorktreeFailure("add", GitErrorCodes.TargetNotFound,
                    $"startPoint '{baseRef}' does not resolve to a commit. Nothing was changed.", GitErrorDetails.TargetNotFound);
        }

        var (entries, listError) = await ListWorktreesAsync(gitRoot, cancellationToken);
        if (entries is null)
            return new GitWorktreeResult { Action = "add", IsError = true, Error = $"git worktree list failed: {listError}. Nothing was changed." };

        // (f) git allows a branch to be checked out in one worktree only.
        if (!createBranch)
        {
            var holder = entries.FirstOrDefault(e => string.Equals(e.Branch, branch, StringComparison.Ordinal));
            if (holder is not null)
                return WorktreeFailure("add", GitErrorCodes.AlreadyExists,
                    $"Branch '{branch}' is already checked out in the worktree at '{holder.Path}'; a branch can be checked out in only one worktree. Nothing was changed.",
                    GitErrorDetails.AlreadyExists);
        }

        string[] args = createBranch
            ? ["-c", "core.longpaths=true", "worktree", "add", "-b", branch, target, baseRef!]
            : ["-c", "core.longpaths=true", "worktree", "add", target, branch];
        var added = await RunGitAsync(gitRoot, args, cancellationToken);
        if (added.ExitCode != 0)
        {
            // Claim "Nothing was changed" only when git did not register the path.
            var (after, _) = await ListWorktreesAsync(gitRoot, cancellationToken);
            var registered = after is null || after.Any(e => string.Equals(e.Path, target, StringComparison.OrdinalIgnoreCase));
            return new GitWorktreeResult
            {
                Action = "add",
                IsError = true,
                Error = $"git worktree add failed: {CleanGitStderr(added.Stderr)}. " +
                        (registered ? "The worktree may have been partly created; check Git(operation: worktree, action: list)." : "Nothing was changed.")
            };
        }

        return new GitWorktreeResult
        {
            Action = "add",
            IsError = false,
            Added = path,
            Branch = branch,
            Note = $"Use LoadSolution on a .slnx inside {path} to work in it; mutating Git operations always target the loaded solution's repo.",
        };
    }

    /// <summary>Up to 10 paths, comma-separated, then "and N more" when there are more.</summary>
    private static string FormatWorktreePaths(IReadOnlyList<string> paths)
    {
        var shown = string.Join(", ", paths.Take(10));
        return paths.Count > 10 ? $"{shown} and {paths.Count - 10} more" : shown;
    }

    /// <summary>
    /// D-20: removes a linked worktree. The main, current and locked worktrees are always refused. A
    /// worktree with modified or untracked files is removed (git worktree remove --force) only when
    /// discardUncommittedChanges is true; otherwise the changed paths are listed and nothing is removed.
    /// --force is never used on a clean worktree.
    /// </summary>
    private async Task<GitWorktreeResult> WorktreeRemoveAsync(
        string gitRoot, string? worktreePath, bool discardUncommittedChanges, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            return WorktreeFailure("remove", GitErrorCodes.RefRequired,
                "Git worktree remove needs worktreePath. Nothing was removed.", GitErrorDetails.RefRequiredWorktree);
        var path = worktreePath.Trim();

        var (entries, listError) = await ListWorktreesAsync(gitRoot, cancellationToken);
        if (entries is null)
            return new GitWorktreeResult { Action = "remove", IsError = true, Error = $"git worktree list failed: {listError}. Nothing was removed." };

        // A relative path is never resolved against the server's working directory.
        var entry = Path.IsPathFullyQualified(path)
            ? entries.FirstOrDefault(e => string.Equals(e.Path, NormalizeWorktreePath(path), StringComparison.OrdinalIgnoreCase))
            : null;
        if (entry is null)
            return WorktreeFailure("remove", GitErrorCodes.TargetNotFound,
                $"No worktree at '{path}'. Known worktrees: {FormatWorktreePaths(entries.Select(e => e.Path).ToList())}. Nothing was removed.",
                GitErrorDetails.TargetNotFound);

        if (entry.IsMain)
            return WorktreeFailure("remove", GitErrorCodes.Refused,
                $"Worktree '{path}' is the main worktree; this tool never removes it. Nothing was removed.",
                "Only linked worktrees can be removed; Git(operation: worktree, action: list) shows which entry has IsMain false.");
        if (entry.IsCurrent)
            return WorktreeFailure("remove", GitErrorCodes.Refused,
                $"Worktree '{path}' is the worktree this Git tool is operating in (the loaded solution's repository); removing it would delete the directory the server is working in. Nothing was removed.",
                "Call LoadSolution on a solution in a different worktree, then remove this one from there.");
        if (entry.IsLocked)
            return WorktreeFailure("remove", GitErrorCodes.Refused,
                $"Worktree '{path}' is locked. Nothing was removed.",
                "unlock it first with git worktree unlock in a shell; this tool does not unlock");

        // Dirty check, run inside that worktree. A missing directory (prunable entry) has nothing to lose.
        var changed = new List<string>();
        if (Directory.Exists(entry.Path))
        {
            var status = await RunGitAsync(entry.Path,
                ["-c", "core.quotePath=false", "status", "--porcelain", "--untracked-files=all"], cancellationToken);
            if (status.ExitCode != 0)
                return new GitWorktreeResult
                {
                    Action = "remove",
                    IsError = true,
                    Error = $"Could not read the status of worktree '{path}': {CleanGitStderr(status.Stderr)}. Nothing was removed."
                };
            changed = status.Stdout.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 3)
                .Select(l => l[3..])
                .ToList();
        }

        if (changed.Count > 0 && !discardUncommittedChanges)
            return WorktreeFailure("remove", GitErrorCodes.WorkingTreeDirty,
                $"Worktree '{path}' has uncommitted changes ({changed.Count} path(s): {FormatWorktreePaths(changed)}). Nothing was removed. " +
                $"To delete it anyway and permanently lose those changes, call Git(operation: worktree, action: remove, worktreePath: '{path}', discardUncommittedChanges: true). " +
                "To keep them, commit or stash inside that worktree first.",
                "discardUncommittedChanges: true deletes modified and untracked files in that worktree; they cannot be recovered. Committed work and the branch are not affected.");

        // --force only for a worktree verified dirty above; a clean worktree never gets it.
        var force = changed.Count > 0;
        string[] removeArgs = force
            ? ["-c", "core.longpaths=true", "worktree", "remove", "--force", entry.Path]
            : ["-c", "core.longpaths=true", "worktree", "remove", entry.Path];
        var removed = await RunGitAsync(gitRoot, removeArgs, cancellationToken);
        if (removed.ExitCode != 0)
        {
            var (after, _) = await ListWorktreesAsync(gitRoot, cancellationToken);
            var stillThere = after is not null && after.Any(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
                             && Directory.Exists(entry.Path);
            return new GitWorktreeResult
            {
                Action = "remove",
                IsError = true,
                Error = $"git worktree remove failed: {CleanGitStderr(removed.Stderr)}. " +
                        (stillThere ? "The worktree was not removed." : "Check its state with Git(operation: worktree, action: list).")
            };
        }

        var kept = entry.Branch is null
            ? "The worktree was on a detached HEAD, so there is no branch to delete."
            : $"The branch '{entry.Branch}' was kept; delete it with Git(operation: branch, deleteBranch: true) once merged.";
        return new GitWorktreeResult
        {
            Action = "remove",
            IsError = false,
            Removed = path,
            Branch = entry.Branch,
            DiscardedPaths = force ? changed : null,
            Note = force
                ? $"Removed '{path}' and discarded uncommitted changes in {changed.Count} path(s). They are not recoverable. {kept}"
                : kept,
        };
    }

    /// <summary>
    /// Finds the git repository root. Git operations read the working tree, not the Roslyn
    /// compilation, so this deliberately does NOT require a loaded solution: it falls back to
    /// walking up from the server's own base/working directory when nothing is loaded. When a
    /// solution IS loaded, its directory is tried FIRST -> a loaded solution reflects the
    /// worktree/repo the caller most recently pointed the server at via LoadSolution, whereas the
    /// server's base directory and process working directory are fixed at process launch and
    /// never change afterward. Trying those first meant that once LoadSolution pointed the
    /// workspace at a git worktree, status/diff/commit still silently resolved against whatever
    /// repo the server binary happened to be launched from/inside (almost always the primary
    /// checkout) instead of the worktree -> a plausible, well-formed, wrong answer with no error.
    /// See docs/current/blockers/blocking_error_git_tool_commit_reports_clean_tree_worktree.md.
    /// </summary>
    public string? TryGetGitRoot(out string error, string? repoPath = null)
    {
        // 0. An explicit repoPath override (status/log/diff/show only - see Git's own parameter
        //    description). Takes priority over everything else: a caller who names a specific
        //    worktree meant that one, not whatever the loaded solution happens to point at.
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            var fromRepoPath = FindRepositoryRoot(repoPath);
            if (fromRepoPath is not null)
            {
                error = "";
                return fromRepoPath;
            }

            error = $"repoPath '{repoPath}' does not resolve to a git repository or worktree (no .git entry found walking up from it). Double-check the path - it must be a real directory that is itself inside a git working tree.";
            return null;
        }

        // 1. The loaded solution's directory, if there is one. This is the caller's most recent
        //    explicit signal of which repo/worktree they mean, via LoadSolution.
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        if (solutionRoot is not null)
        {
            var fromSolution = FindRepositoryRoot(solutionRoot);
            if (fromSolution is not null)
            {
                error = "";
                return fromSolution;
            }
        }

        // 2. Walk up from the server's base directory. Common case when no solution is loaded
        //    yet: the server runs from a bin/ folder inside the repo it is operating on.
        var fromBaseDirectory = FindRepositoryRoot(AppContext.BaseDirectory);
        if (fromBaseDirectory is not null)
        {
            error = "";
            return fromBaseDirectory;
        }

        // 3. Walk up from the current working directory -> covers a server whose binaries are
        //    deployed outside the repo but which was launched from inside it.
        var fromCurrentDirectory = FindRepositoryRoot(Directory.GetCurrentDirectory());
        if (fromCurrentDirectory is not null)
        {
            error = "";
            return fromCurrentDirectory;
        }

        if (solutionRoot is not null)
        {
            error = $"No git repository found. Searched upward from the loaded solution's directory ('{solutionRoot}'), the server's base directory, and the working directory without finding a .git entry. Git operations need a git working tree, not a loaded solution.";
            return null;
        }

        error = "No git repository found. Searched upward from the server's base directory and working directory without finding a .git entry, and no solution is loaded to search from either. If the repository is elsewhere, call LoadSolution with a solution inside it first.";
        return null;
    }
       /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for a <c>.git</c> entry. Accepts a
    /// <c>.git</c> <b>file</b> as well as a directory: linked worktrees and submodules use a
    /// <c>.git</c> file holding a gitdir pointer, and a directory-only check silently walks past
    /// them into the parent repository -> which is exactly how a PlanStepRunner harness clone would
    /// end up having git operations aimed at the wrong repo.
    /// </summary>
    private static string? FindRepositoryRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        string? dir;
        try
        {
            dir = Path.GetFullPath(startDirectory);
        }
        catch (Exception)
        {
            // A malformed or over-long path is not something the caller can act on -> treat it as
            // "nothing found here" so the next candidate directory still gets tried.
            return null;
        }

        while (!string.IsNullOrEmpty(dir))
        {
            var gitEntry = Path.Combine(dir, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                return dir;

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }

        return null;
    }
    private async Task<GitRawResult> RunGitAsync(
        string gitRoot, string[] args, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = GitExecutablePath,
            WorkingDirectory = gitRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Without these, Process decodes the redirected pipes using Console.OutputEncoding,
            // which on Windows defaults to the legacy OS codepage, not UTF-8. Git writes UTF-8 to
            // stdout/stderr regardless of that codepage, so every non-ASCII multi-byte character
            // (e.g. an em dash in a diffed file) gets decoded one byte at a time as separate
            // legacy-codepage characters and re-encoded into mojibake like "ΓÇö" - see
            // docs/current/blockers/blocking_error_git_diff_mojibake_display.md. Forcing UTF-8 here
            // makes decoding match what git actually wrote.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Non-interactive by construction: stdin is closed and there is no console, so a credential
        // prompt can never be answered and would otherwise hang until GitProcessTimeout kills it.
        // GIT_TERMINAL_PROMPT=0 makes git itself fail fast ("terminal prompts disabled");
        // GCM_INTERACTIVE=never does the same for Git Credential Manager, which can otherwise pop
        // a GUI prompt that nobody is there to answer.
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "never";
        // Belt-and-braces: also tell git explicitly to treat commit/log text as UTF-8, in case a
        // repo-level i18n.* config otherwise changes how git itself encodes that text before it
        // ever reaches the pipe.
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("i18n.logOutputEncoding=utf-8");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("i18n.commitEncoding=utf-8");
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        // A caller-supplied token (if any) can still cancel earlier than GitProcessTimeout -> this
        // only adds a ceiling for the common case (MCP call omits cancellationToken, so this method
        // otherwise gets CancellationToken.None and would wait on a hung git process forever).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GitProcessTimeout);

        process.Start();
        // Close stdin immediately: an inherited/open but undrained stdin handle is a known cause of
        // a spawned console child stalling indefinitely waiting for input it will never receive,
        // even for commands (like rev-parse) that never read from it.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillGitProcess(process);
            // Full detail (args, timeout value, TODO cross-reference) is server-side only, via the
            // logger call below -> every caller's catch block folds the thrown exception's Message
            // straight into the ResultError text returned to the agent, so that Message must stay
            // a plain, self-contained statement with no internal paths/docs the agent can't open.
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(
                    "git {Args} did not exit within {TimeoutSeconds}s and was killed. Not a normal git " +
                    "failure - see docs/TODO.md's 'Git(operation: status) hung indefinitely' entry.",
                    string.Join(' ', args), GitProcessTimeout.TotalSeconds);
            }
            throw new TimeoutException($"Git operation timed out after {GitProcessTimeout.TotalSeconds:0}s and was cancelled.");
        }

        return new GitRawResult
        {
            ExitCode = process.ExitCode,
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString()
        };
    }

    // Byte-exact sibling of RunGitAsync, for output whose line endings must survive untouched (a diff that
    // will be re-applied as a patch). Its process setup is DUPLICATED from RunGitAsync on purpose: RunGitAsync
    // reads stdout through OutputDataReceived, which splits on line breaks, drops each CR/LF and re-adds
    // Environment.NewLine via AppendLine, so every line ending in a diff would be rewritten (CRLF on Windows).
    // Here stdout is copied raw from the pipe's BaseStream and no StandardOutputEncoding is set; callers decode
    // with Encoding.Latin1 so one char is one byte. Duplicated rather than refactored because every Git test
    // depends on RunGitAsync. Follow-up: extract the shared process setup into one helper used by both.
    private async Task<(int ExitCode, byte[] Stdout, string Stderr)> RunGitBytesAsync(
        string gitRoot, string[] args, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = GitExecutablePath,
            WorkingDirectory = gitRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Same non-interactive setup as RunGitAsync: fail fast instead of waiting on a credential prompt.
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "never";
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("i18n.logOutputEncoding=utf-8");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("i18n.commitEncoding=utf-8");
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GitProcessTimeout);

        process.Start();
        process.StandardInput.Close();
        process.BeginErrorReadLine();
        using var stdout = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await copyTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillGitProcess(process);
            try { await copyTask; } catch (Exception) { /* the copy is abandoned with the killed process */ }
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(
                    "git {Args} did not exit within {TimeoutSeconds}s and was killed (byte-exact runner).",
                    string.Join(' ', args), GitProcessTimeout.TotalSeconds);
            }
            throw new TimeoutException($"Git operation timed out after {GitProcessTimeout.TotalSeconds:0}s and was cancelled.");
        }

        return (process.ExitCode, stdout.ToArray(), stderr.ToString());
    }

    private static FileDiffLoad NoUnstagedChangesLoad(string path) => new(path, null, GitErrorCodes.NoChanges,
    $"No unstaged changes in '{path}'. Already staged? Git(operation: diff, target: staged, files: ...). Nothing was changed.",
    GitErrorDetails.NoChanges);

    /// <summary>Outcome of loading one file's unstaged diff: Diff is set on success; otherwise ErrorKind, Error and
    /// ErrorDetail describe the classified failure. Shared by operation=hunks and the hunk stage.</summary>
    private sealed record FileDiffLoad(
        string Path, GitFileDiff? Diff, string? ErrorKind = null, string? Error = null, string? ErrorDetail = null);

    // Runs the zero-context unstaged diff of one already-classified tracked path through the byte-exact runner,
    // parses it and applies the kind refusals. Success carries the parsed diff; every failure is classified.
    private async Task<FileDiffLoad> LoadDiffForPathAsync(string gitRoot, string path, CancellationToken cancellationToken)
    {
        // Byte-exact runner: the fingerprint and any patch later built from this text must see the file's real line endings.
        var raw = await RunGitBytesAsync(gitRoot,
            ["-c", "core.quotePath=false", "--literal-pathspecs", "diff", "--no-color", "--no-ext-diff", "--no-renames", "-U0", "--", path],
            cancellationToken);
        if (raw.ExitCode != 0)
        {
            return new FileDiffLoad(path, null, GitErrorCodes.Fallback,
                $"git diff failed: {CleanGitStderr(raw.Stderr)}. Nothing was changed.", "Call Git(operation: status) to check the repository state, then retry.");
        }
        if (raw.Stdout.Length == 0)
            return NoUnstagedChangesLoad(path);

        var diff = GitHunkParser.Parse(Encoding.Latin1.GetString(raw.Stdout));
        // Kind checks come before IsRenameOrModeOnly: that flag is also true for a binary or new-file diff with no @@ lines.
        string? unsupported = diff switch
        {
            { IsBinary: true } => "is a binary file",
            { IsNewFile: true } => "is a new file (added with intent-to-add)",
            { IsDeletedFile: true } => "is deleted in the working tree",
            { IsRenameOrModeOnly: true } => "has only a mode or rename change",
            _ => null
        };
        if (unsupported is not null)
        {
            return new FileDiffLoad(path, null, GitErrorCodes.HunkUnsupported,
                $"'{path}' {unsupported}, so it has no text hunks to choose from. Nothing was changed.", GitErrorDetails.HunkUnsupported);
        }
        if (diff.Hunks.Count == 0)
            return NoUnstagedChangesLoad(path);

        return new FileDiffLoad(path, diff);
    }

    // Shared by operation=hunks and the hunk stage: validates that paths names exactly one tracked, existing,
    // non-deleted file, then loads and parses its unstaged diff. Never throws for a classified refusal.
    private async Task<FileDiffLoad> LoadFileDiffAsync(string gitRoot, string? paths, CancellationToken cancellationToken)
    {
        var list = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var listError);
        if (list is null)
            return new FileDiffLoad("", null, GitErrorCodes.HunkSelection, $"{listError} Nothing was changed.", GitErrorDetails.HunkSelection);
        if (list.Length == 0)
        {
            return new FileDiffLoad("", null, GitErrorCodes.RefRequired,
                "operation=hunks needs files naming exactly one file. Nothing was changed.",
                "Pass files: '<one repo-relative path>'");
        }
        if (list.Length > 1)
        {
            return new FileDiffLoad("", null, GitErrorCodes.HunkSelection,
                $"hunks works on one file at a time; got {list.Length}. Nothing was changed.", GitErrorDetails.HunkSelection);
        }

        var pathError = ValidateRepoRoot(gitRoot, list[0]);
        if (pathError is not null)
            return new FileDiffLoad(list[0], null, GitErrorCodes.PathNotFound, $"{pathError}. Nothing was changed.", GitErrorDetails.PathNotFound);

        var classified = (await ClassifyPathsAsync(gitRoot, [list[0]], cancellationToken))[0];
        var path = classified.Path;
        switch (classified.Classification)
        {
            case PathClassification.Untracked:
                var isDirectory = Directory.Exists(Path.Combine(gitRoot, path));
                return new FileDiffLoad(path, null, GitErrorCodes.HunkUnsupported,
                    isDirectory
                        ? $"'{path}' is a directory; hunks works on one tracked file at a time. Nothing was changed."
                        : $"'{path}' is untracked: untracked files have no hunks to choose from; stage the whole file with Git(operation: stage, files: ...). Nothing was changed.",
                    GitErrorDetails.HunkUnsupported);
            case PathClassification.Missing:
                return new FileDiffLoad(path, null, GitErrorCodes.PathNotFound,
                    $"'{path}' is not tracked and does not exist on disk. Nothing was changed.", GitErrorDetails.PathNotFound);
            case PathClassification.HeadOnly:
                return new FileDiffLoad(path, null, GitErrorCodes.HunkUnsupported,
                    $"'{path}' is deleted (tracked in HEAD, gone from the index), so it has no hunks to choose from. Nothing was changed.",
                    GitErrorDetails.HunkUnsupported);
        }

        return await LoadDiffForPathAsync(gitRoot, path, cancellationToken);
    }

    private static GitHunkEntry ToHunkEntry(GitHunk h) => new()
    {
        Index = h.Index,
        OldStart = h.OldStart,
        OldCount = h.OldCount,
        NewStart = h.NewStart,
        NewCount = h.NewCount,
        Header = h.Header,
        Added = h.NewCount,
        Removed = h.OldCount,
        Preview = GitHunkParser.PreviewLines(h, 6, 120)
    };

    private static GitHunksResult BuildHunksResult(string path, GitFileDiff diff, int count)
    {
        var limit = Math.Clamp(count, 1, 100);
        return new GitHunksResult
        {
            Path = path,
            Fingerprint = diff.Fingerprint,
            TotalHunks = diff.Hunks.Count,
            IsTruncated = diff.Hunks.Count > limit,
            Hunks = diff.Hunks.Take(limit).Select(ToHunkEntry).ToList(),
            Note = $"Pass hunkIds with this hunkFingerprint to Git(operation: stage, files: '{path}'), or lineRange. " +
                   "Hunks are zero-context: adjacent edits are separate hunks."
        };
    }

    // The new-file lines a hunk occupies, using the same rule as GitHunkParser.SelectByNewLineRange.
    private static (int Start, int End) HunkSpan(GitHunk h)
    {
        var start = Math.Max(h.NewStart, 1);
        return (start, start + Math.Max(h.NewCount, 1) - 1);
    }

    private static string SpanText(int start, int end) => start == end ? start.ToString() : $"{start}-{end}";

    private static string DescribeHunkSpans(GitFileDiff diff) =>
    "Hunks (id: new-file lines): " + string.Join("; ", diff.Hunks.Select(h =>
    {
        var (start, end) = HunkSpan(h);
        return $"{h.Index}: {SpanText(start, end)}";
    })) + ".";

    // Resolves hunkIds (guarded by the fingerprint) or lineRange to hunk indexes. Returns null on success with
    // the ascending indexes in `selected`; otherwise the classified refusal. Exactly one of the two is non-blank.
    private static (string Kind, string Error, string Detail)? SelectHunks(
        GitFileDiff diff, string path, string? hunkIds, string? hunkFingerprint, string? lineRange, out List<int> selected)
    {
        selected = [];
        if (!string.IsNullOrWhiteSpace(hunkIds))
        {
            if (string.IsNullOrWhiteSpace(hunkFingerprint))
            {
                return (GitErrorCodes.HunkSelection,
                    "hunkIds needs the hunkFingerprint returned by Git(operation: hunks). Nothing was staged.", GitErrorDetails.HunkSelection);
            }
            var given = hunkFingerprint.Trim();
            if (!string.Equals(given, diff.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return (GitErrorCodes.HunkStale,
                    $"'{path}' changed since the hunks were listed (fingerprint {given} vs {diff.Fingerprint}). " +
                    $"Re-run Git(operation: hunks, files: '{path}') and pick again. Nothing was staged.", GitErrorDetails.HunkStale);
            }
            if (!GitHunkParser.TryParseHunkIds(hunkIds, diff.Hunks.Count, out selected, out var idError))
                return (GitErrorCodes.HunkSelection, $"{idError} {DescribeHunkSpans(diff)} Nothing was staged.", GitErrorDetails.HunkSelection);
            return null;
        }

        if (!GitHunkParser.TryParseLineRange(lineRange, out var from, out var to, out var rangeError))
            return (GitErrorCodes.HunkSelection, $"{rangeError} {DescribeHunkSpans(diff)} Nothing was staged.", GitErrorDetails.HunkSelection);

        selected = GitHunkParser.SelectByNewLineRange(diff, from, to, out var straddling);
        if (straddling.Count > 0)
        {
            var spans = straddling.Select(HunkSpan).ToList();
            var lo = Math.Min(from, spans.Min(s => s.Start));
            var hi = Math.Max(to, spans.Max(s => s.End));
            var parts = straddling.Select(h =>
            {
                var (start, end) = HunkSpan(h);
                return $"Hunk {h.Index} spans lines {SpanText(start, end)} but only partly lies in {SpanText(from, to)}.";
            });
            selected = [];
            return (GitErrorCodes.HunkSelection,
                $"{string.Join(' ', parts)} Widen the range to {SpanText(lo, hi)}, or use hunkIds. {DescribeHunkSpans(diff)} Nothing was staged.",
                GitErrorDetails.HunkSelection);
        }
        if (selected.Count == 0)
        {
            return (GitErrorCodes.HunkSelection,
                $"No hunk lies wholly inside lines {SpanText(from, to)}. {DescribeHunkSpans(diff)} Nothing was staged.",
                GitErrorDetails.HunkSelection);
        }
        return null;
    }

    private static GitStageHunksResult StageHunksFailure(string path, string kind, string error, string detail) => new()
    {
        Path = path,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        ErrorDetail = detail
    };

    // Runs after git apply succeeded: re-reads the unstaged diff to count what is left, and copies the repository
    // status (from StatusAsync) into the inherited GitStatusResult members. Never turns a successful stage into an error.
    private async Task<GitStageHunksResult> BuildStageHunksResultAsync(
        string gitRoot, string? paths, string path, GitFileDiff before, List<int> selected, CancellationToken cancellationToken)
    {
        var expected = before.Hunks.Count - selected.Count;
        var after = await LoadFileDiffAsync(gitRoot, paths, cancellationToken);
        int? remaining = after.Diff is not null ? after.Diff.Hunks.Count
            : after.ErrorKind == GitErrorCodes.NoChanges ? 0
            : null;
        var warnings = new List<string>();
        if (remaining != expected)
        {
            warnings.Add($"Staged {selected.Count} hunk(s) but the remaining count is {remaining?.ToString() ?? "unknown"} " +
                         $"(expected {expected}); verify with Git(operation: diff, target: staged).");
        }

        var status = await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
        if (status.IsError)
            warnings.Add($"The hunks were staged but the status could not be read: {status.Error}");

        return new GitStageHunksResult
        {
            Path = path,
            StagedHunks = selected.Select(i => ToHunkEntry(before.Hunks[i - 1])).ToList(),
            RemainingUnstagedHunks = remaining ?? expected,
            Warning = warnings.Count == 0 ? null : string.Join(' ', warnings),
            Branch = status.Branch,
            IsClean = status.IsClean,
            Staged = status.Staged,
            Unstaged = status.Unstaged,
            Untracked = status.Untracked,
            IsTruncated = status.IsTruncated,
            TotalStagedCount = status.TotalStagedCount,
            TotalUnstagedCount = status.TotalUnstagedCount,
            TotalUntrackedCount = status.TotalUntrackedCount,
            StagedByStatus = status.StagedByStatus,
            UnstagedByStatus = status.UnstagedByStatus,
            InProgress = status.InProgress,
            AbortedOperation = status.AbortedOperation,
        };
    }

    /// <summary>
    /// Stages only the selected hunks of exactly one tracked text file into the index, by hunkIds (guarded by the
    /// fingerprint from operation=hunks) or by a new-file lineRange. Only the index changes; the working tree is
    /// never touched. git apply is all-or-nothing, so a rejected patch stages nothing.
    /// </summary>
    public async Task<GitStageHunksResult> StageHunksAsync(
        string gitRoot, string? paths, string? hunkIds, string? hunkFingerprint, string? lineRange,
        CancellationToken cancellationToken)
    {
        string? patchFile = null;
        try
        {
            if (string.IsNullOrWhiteSpace(hunkIds) == string.IsNullOrWhiteSpace(lineRange))
            {
                return StageHunksFailure("", GitErrorCodes.HunkSelection,
                    "Pass hunkIds (with hunkFingerprint) or lineRange, not both. Nothing was staged.", GitErrorDetails.HunkSelection);
            }

            var load = await LoadFileDiffAsync(gitRoot, paths, cancellationToken);
            if (load.Diff is null)
                return StageHunksFailure(load.Path, load.ErrorKind ?? GitErrorCodes.Fallback, load.Error ?? "", load.ErrorDetail ?? "");

            var rejection = SelectHunks(load.Diff, load.Path, hunkIds, hunkFingerprint, lineRange, out var selected);
            if (rejection is { } refused)
                return StageHunksFailure(load.Path, refused.Kind, refused.Error, refused.Detail);

            // Outside the repo, so the workspace file watcher never sees it. Latin-1 keeps every byte of the
            // patch (including CR) exactly as git printed it.
            patchFile = Path.Combine(Path.GetTempPath(), "roslynsentinel-hunk-" + Guid.NewGuid().ToString("N") + ".patch");
            await File.WriteAllBytesAsync(patchFile, Encoding.Latin1.GetBytes(GitHunkParser.BuildPatch(load.Diff, selected)), cancellationToken);

            var apply = await RunGitAsync(gitRoot,
                ["apply", "--cached", "--unidiff-zero", "--whitespace=nowarn", patchFile], cancellationToken);
            if (apply.ExitCode != 0)
            {
                return StageHunksFailure(load.Path, GitErrorCodes.Fallback,
                    $"git apply rejected the selected hunks: {CleanGitStderr(apply.Stderr)}. Nothing was staged.",
                    "Re-run Git(operation: hunks, files: '<path>') and pick again, or stage the whole file with Git(operation: stage, files: '<path>').");
            }

            return await BuildStageHunksResultAsync(gitRoot, paths, load.Path, load.Diff, selected, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitStageHunksResult>(ex, "stage hunks");
        }
        finally
        {
            if (patchFile is not null)
            {
                try
                {
                    File.Delete(patchFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not delete temporary hunk patch file {PatchFile}", patchFile);
                }
            }
        }
    }

    private static string ResolveGitExecutablePath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';')
            : [""];

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, "git" + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Fall back to bare "git" (prior behavior) so a PATH this scan couldn't see (e.g. an App
        // Paths registry redirect) still has a chance to work rather than failing outright.
        return "git";
    }

    private static void TryKillGitProcess(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort -> the process may have exited between the check and the kill, or the
            // handle may already be invalid. Nothing more useful to do here.
        }
    }

       /// <summary>
    /// Best-effort check that <paramref name="text"/> decoded cleanly as UTF-8, for surfacing a
    /// warning rather than silently returning corrupted diff text - see
    /// docs/current/blockers/blocking_error_git_diff_mojibake_display.md. Two independent signals:
    /// U+FFFD (the .NET/UTF-8 decoder's own "this byte sequence was invalid" replacement character),
    /// and the specific C1-control-block run (U+0080-U+009F) that appears when UTF-8 multi-byte
    /// sequences get mis-decoded one byte at a time through a Windows-125x-family legacy codepage
    /// instead (the "ΓÇö"-style mojibake the linked blocker documented). Neither check requires
    /// knowing what the correct text should have been - both are shapes that well-formed text
    /// essentially never contains on their own.
    /// </summary>
    private static string? DetectDecodeCorruption(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Contains('�'))
        {
            return "Output contains U+FFFD (Unicode replacement character), meaning some bytes " +
                   "from git could not be decoded as UTF-8 and were lost. Non-ASCII content in this " +
                   "result may be corrupted or missing - re-check the source file directly (ReadFile/" +
                   "GetFileOutline) rather than trusting this text for anything non-ASCII.";
        }

        var suspiciousRunLength = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\u0080' and <= '\u009F')
            {
                suspiciousRunLength++;
                if (suspiciousRunLength >= 2)
                {
                    return "Output contains a run of C1 control characters (U+0080-U+009F), the " +
                           "signature of a UTF-8 multi-byte sequence mis-decoded one byte at a time " +
                           "through a legacy codepage (e.g. an em dash showing up as ΓÇö-style " +
                           "mojibake). Non-ASCII content in this result is likely corrupted - re-check " +
                           "the source file directly (ReadFile/GetFileOutline) rather than trusting this text.";
                }
            }
            else
            {
                suspiciousRunLength = 0;
            }
        }

        return null;
    }

    private static string StatusLabel(char code) => code switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'U' => "conflict",
        _ => code.ToString()
    };

    /// <summary>Default for the status <c>maxEntries</c> parameter (the long-standing 50-entry threshold).</summary>
    public const int DefaultStatusMaxEntries = 50;

    /// <summary>Upper bound accepted for the status <c>maxEntries</c> parameter.</summary>
    public const int MaxStatusMaxEntries = 5000;

    /// <summary>
    /// True for the seven unmerged XY pairs porcelain v1 reports (DD, AU, UD, UA, DU, AA, UU).
    /// "A" or "D" alone mean staged add/delete, so only these exact pairs (every one of which
    /// contains a U, or is AA or DD) are conflicts - AA and DD must not read as staged add/delete.
    /// </summary>
    private static bool IsConflictPair(char x, char y) =>
        x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');

    /// <summary>
    /// Detects a multi-step operation left half-finished by a conflict, from the repository's own
    /// state files: "rebase" (rebase-merge/ or rebase-apply/), "am" (rebase-apply/applying),
    /// "cherry-pick" (CHERRY_PICK_HEAD), "revert" (REVERT_HEAD), "merge" (MERGE_HEAD). Returns null
    /// when nothing is in progress. Rebase is checked first because a conflicted rebase also writes
    /// CHERRY_PICK_HEAD. The git dir comes from rev-parse (not gitRoot + ".git") so linked worktrees,
    /// where .git is a file and the state files live in the per-worktree git dir, resolve correctly.
    /// </summary>
    private async Task<string?> DetectInProgressAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var dirRaw = await RunGitAsync(gitRoot, ["rev-parse", "--absolute-git-dir"], cancellationToken);
        if (dirRaw.ExitCode != 0)
            return null;

        var gitDir = dirRaw.Stdout.Trim();
        if (gitDir.Length == 0 || !Directory.Exists(gitDir))
            return null;

        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")))
            return "rebase";
        if (Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            return File.Exists(Path.Combine(gitDir, "rebase-apply", "applying")) ? "am" : "rebase";
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            return "cherry-pick";
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
            return "revert";
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
            return "merge";
        return null;
    }

    /// <summary>
    /// Text to append to a failed pull/revert/commit when the failure left the repository
    /// mid-operation (a conflict): names the state and the way out. Empty when nothing is in
    /// progress, and on any detection failure - it is advice, never a reason to mask the real error.
    /// </summary>
    private async Task<string> InProgressAdviceAsync(string gitRoot, CancellationToken cancellationToken)
    {
        string? op;
        try
        {
            op = await DetectInProgressAsync(gitRoot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }

        return op switch
        {
            null => "",
            "rebase" or "am" => $" Repository is mid-{op}. This tool cannot continue it; call Git(operation: abort) to back out (the branch returns to its pre-{op} state), or finish it in the shell.",
            _ => $" Repository is mid-{op}. Resolve the conflicts and commit, or call Git(operation: abort) to back out.",
        };
    }

    /// <summary>
    /// Appends the mid-operation advice to a failed <paramref name="result"/> and, when the repository
    /// really is mid-operation, classifies it as <see cref="GitErrorCodes.OperationInProgress"/> unless a
    /// more specific kind was already set. No-op when nothing is in progress.
    /// </summary>
    private async Task<T> AppendInProgressAsync<T>(T result, string gitRoot, CancellationToken cancellationToken) where T : GitResult
    {
        var advice = await InProgressAdviceAsync(gitRoot, cancellationToken);
        if (advice.Length == 0)
            return result;

        result.Error += advice;
        result.ErrorKind ??= GitErrorCodes.OperationInProgress;
        result.ErrorDetail ??= GitErrorDetails.OperationInProgress;
        return result;
    }

    /// <summary>
    /// True when the index holds nothing to commit: for no paths, the whole index equals HEAD; for named
    /// paths, those paths do. Structural (git diff --cached --quiet) rather than matching git's
    /// localizable "nothing to commit" text.
    /// </summary>
    private async Task<bool> NothingStagedAsync(string gitRoot, string? paths, CancellationToken cancellationToken)
    {
        string[] args = ["--literal-pathspecs", "diff", "--cached", "--quiet"];
        if (!string.IsNullOrWhiteSpace(paths))
        {
            var list = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var parseError);
            if (parseError != null || list == null)
                return false;
            args = [.. args, "--", .. list];
        }

        var raw = await RunGitAsync(gitRoot, args, cancellationToken);
        return raw.ExitCode == 0;
    }

    public async Task<GitStatusResult> StatusAsync(string gitRoot, int maxEntries, CancellationToken cancellationToken)
    {
        if (maxEntries < 1 || maxEntries > MaxStatusMaxEntries)
        {
            return new GitStatusResult
            {
                IsError = true,
                Error = $"maxEntries must be between 1 and {MaxStatusMaxEntries} (got {maxEntries}). " +
                        $"Retry with a value in that range, e.g. maxEntries: {DefaultStatusMaxEntries}. Nothing was changed.",
            };
        }

        try
        {
            var branchRaw = await RunGitAsync(gitRoot,
                ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
            var branch = branchRaw.ExitCode == 0 ? branchRaw.Stdout.Trim() : "unknown";

            // core.quotePath=false keeps non-ASCII paths verbatim (not "\303\251"-style escapes) and
            // -z makes each record NUL-terminated with no quoting at all, so spaces and any other
            // character round-trip into the `files` parameter unchanged.
            var statusRaw = await RunGitAsync(gitRoot,
                ["-c", "core.quotePath=false", "status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
            if (statusRaw.ExitCode != 0)
                return new GitStatusResult { IsError = true, Branch = branch, Error = CleanGitStderr(statusRaw.Stderr) };

            var staged = new List<GitStatusEntry>();
            var unstaged = new List<GitStatusEntry>();
            var untracked = new List<string>();

            // Records are "XY <path>" separated by NUL. For a rename/copy (R/C on either side) the
            // NEXT record is the original path (note: -z reverses the "from -> to" order of the
            // non-z format). RunGitAsync appends a line terminator after the last NUL, which leaves
            // a short trailing fragment that the length check below skips.
            var records = statusRaw.Stdout.Split('\0');
            for (var i = 0; i < records.Length; i++)
            {
                var record = records[i];
                if (record.Length < 4) continue;
                var x = record[0];
                var y = record[1];
                var path = record[3..];

                string? originalPath = null;
                if (x is 'R' or 'C' || y is 'R' or 'C')
                {
                    if (i + 1 < records.Length)
                        originalPath = records[++i];
                }

                if (x == '?' && y == '?')
                {
                    untracked.Add(path);
                    continue;
                }

                if (IsConflictPair(x, y))
                {
                    // Both sides are labelled "conflict": AA/DD would otherwise read as a staged
                    // add/delete, which is the opposite of what an unmerged path is.
                    staged.Add(new GitStatusEntry { Status = "conflict", Path = path });
                    unstaged.Add(new GitStatusEntry { Status = "conflict", Path = path });
                    continue;
                }

                if (x != ' ' && x != '?')
                {
                    staged.Add(new GitStatusEntry
                    {
                        Status = StatusLabel(x),
                        Path = path,
                        OriginalPath = x is 'R' or 'C' ? originalPath : null,
                    });
                }
                if (y != ' ' && y != '?')
                {
                    unstaged.Add(new GitStatusEntry
                    {
                        Status = StatusLabel(y),
                        Path = path,
                        OriginalPath = y is 'R' or 'C' ? originalPath : null,
                    });
                }
            }

            bool isClean = staged.Count == 0 && unstaged.Count == 0 && untracked.Count == 0;
            int total = staged.Count + unstaged.Count + untracked.Count;
            var inProgress = await DetectInProgressAsync(gitRoot, cancellationToken);
            var sampleSize = Math.Min(10, maxEntries);

            if (total > maxEntries)
            {
                return new GitStatusResult
                {
                    IsError = false,
                    Branch = branch,
                    IsClean = isClean,
                    IsTruncated = true,
                    InProgress = inProgress,
                    TotalStagedCount = staged.Count,
                    TotalUnstagedCount = unstaged.Count,
                    TotalUntrackedCount = untracked.Count,
                    StagedByStatus = staged.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()),
                    UnstagedByStatus = unstaged.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()),
                    Staged = staged.Take(sampleSize).ToList(),
                    Unstaged = unstaged.Take(sampleSize).ToList(),
                    Untracked = untracked.Take(sampleSize).ToList(),
                };
            }

            return new GitStatusResult
            {
                IsError = false,
                Branch = branch,
                IsClean = isClean,
                InProgress = inProgress,
                Staged = staged,
                Unstaged = unstaged,
                Untracked = untracked,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git status failed");
            return new GitStatusResult { IsError = true, Error = $"Git status failed: {ex.Message}" };
        }
    }

    public async Task<GitLogResult> LogAsync(
        string gitRoot, int count, string? refName, string? paths, CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 100);
        try
        {
            // Unit separator (ASCII 31) delimits fields; record separator (ASCII 30) delimits
            // commits -> %B (full body) can itself contain embedded newlines, so a plain '\n' split
            // is not safe once the body is multi-line.
            const string fieldSep = "\x1f";
            const string recordSep = "\x1e";
            var format = $"%H{fieldSep}%h{fieldSep}%an{fieldSep}%aI{fieldSep}%B{recordSep}";

            var args = new List<string> { "log", $"--max-count={count}", $"--format={format}" };
            if (!string.IsNullOrWhiteSpace(refName))
                args.Add(refName);
            if (!string.IsNullOrWhiteSpace(paths))
            {
                var parsedPaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitLogResult { IsError = true, Error = pathsError };

                args.Add("--");
                args.AddRange(parsedPaths!);
            }

            var logRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);

            if (logRaw.ExitCode != 0)
                return new GitLogResult { IsError = true, Error = CleanGitStderr(logRaw.Stderr) };

            var commits = new List<GitCommitEntry>();
            foreach (var record in logRaw.Stdout.Split(recordSep, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmedRecord = record.TrimStart('\n', '\r');
                var parts = trimmedRecord.Split(fieldSep);
                if (parts.Length < 5) continue;
                commits.Add(new GitCommitEntry
                {
                    Hash = parts[0],
                    ShortHash = parts[1],
                    Author = parts[2],
                    Date = parts[3],
                    Message = parts[4].Trim('\n', '\r'),
                });
            }

            return new GitLogResult { IsError = false, Commits = commits };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git log failed");
            return new GitLogResult { IsError = true, Error = $"Git log failed: {ex.Message}" };
        }
    }

    // The well-known, always-valid hash of git's empty tree object - same in every repository,
    // since it depends only on the (fixed) serialization of "a tree with zero entries." Used as
    // the diff base for a commit that has no parent (git's own `git diff <hash>^ <hash>` fails
    // with "unknown revision" in exactly that case, since `^` has nothing to resolve to).
    private const string EmptyTreeHash = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>
    /// Caps <paramref name="text"/> at <paramref name="maxBytes"/> UTF-8 bytes (not chars), cutting
    /// only on a whole-character boundary so a surrogate pair is never split, and appends a
    /// truncation marker when anything was dropped.
    /// </summary>
    private static string CapToUtf8Bytes(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        var bytes = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maxBytes)
                break;
            bytes += runeBytes;
            chars += rune.Utf16SequenceLength;
        }

        return text[..chars] + $"\n... (truncated at {maxBytes} bytes)";
    }

    /// <summary>
    /// Returns a refusal message when both output-format flags are set, else null. They pick
    /// different git output formats (<c>--name-status</c> vs <c>--stat</c>) and cannot be combined.
    /// </summary>
    private static string? ValidateDiffFormatFlags(string operationName, bool nameOnly, bool stat) =>
        nameOnly && stat
            ? $"Git {operationName}: both 'nameOnly' and 'stat' were set, but they select different output formats " +
              "(nameOnly = a --name-status file list, stat = --stat text). Pass only one of them, or neither for the full patch. Nothing was run."
            : null;

    /// <summary>
    /// Counts the files a diff-family output covers, per the format that was requested: one line
    /// per file for <c>--name-status</c>, the trailing "N files changed" summary for <c>--stat</c>,
    /// and the number of "diff --git" headers for a full patch.
    /// </summary>
    private static int CountChangedFiles(string output, bool nameOnly, bool stat)
    {
        if (nameOnly)
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

        if (stat)
        {
            var summary = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            var match = summary is null
                ? null
                : System.Text.RegularExpressions.Regex.Match(summary, @"(\d+) files? changed");
            return match is { Success: true } ? int.Parse(match.Groups[1].Value) : 0;
        }

        return output.Split('\n').Count(l => l.StartsWith("diff --git", StringComparison.Ordinal));
    }

    public async Task<GitDiffResult> DiffAsync(
        string gitRoot, string target, string? paths, int maxBytes, bool nameOnly, bool stat, CancellationToken cancellationToken)
    {
        maxBytes = Math.Clamp(maxBytes, 1024, 524288);
        var formatError = ValidateDiffFormatFlags("diff", nameOnly, stat);
        if (formatError is not null)
            return new GitDiffResult { IsError = true, Error = formatError };

        try
        {
            // core.quotePath=false so non-ASCII paths in headers and file lists are not octal-escaped.
            var args = new List<string> { "-c", "core.quotePath=false", "diff" };
            if (nameOnly)
                args.Add("--name-status");
            else if (stat)
                args.Add("--stat=200");

            // unstaged is an alias of working (uncommitted changes vs the index)
            if (string.Equals(target, "unstaged", StringComparison.OrdinalIgnoreCase))
                target = "working";

            if (target == "staged")
            {
                args.Add("--cached");
            }
            else if (target.Contains("...", StringComparison.Ordinal) || target.Contains("..", StringComparison.Ordinal))
            {
                // A caller-supplied range (refA..refB or refA...refB) - pass straight through as a
                // single arg, exactly like the git CLI accepts it, rather than trying to split and
                // reinterpret it ourselves.
                args.Add(target);
            }
            else if (target != "working")
            {
                // Diff the working tree against an arbitrary single ref (HEAD, a branch, a SHA) -
                // equivalent to `git diff <target>`. This is NOT "what did this commit change"
                // (that is ShowAsync's job, diffing the commit against its parent) - conflating the
                // two here previously made `target: "HEAD"` return the last commit's diff instead of
                // the working tree's uncommitted changes against HEAD.
                args.Add(target);
            }

            if (!string.IsNullOrWhiteSpace(paths))
            {
                var parsedPaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitDiffResult { IsError = true, Error = pathsError };

                args.Add("--");
                args.AddRange(parsedPaths!);
            }

            var diffRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);

            if (diffRaw.ExitCode != 0)
            {
                var cleaned = CleanGitStderr(diffRaw.Stderr);
                if (diffRaw.Stderr.Contains("bad revision", StringComparison.OrdinalIgnoreCase) ||
                    diffRaw.Stderr.Contains("unknown revision", StringComparison.OrdinalIgnoreCase) ||
                    diffRaw.Stderr.Contains("ambiguous argument", StringComparison.OrdinalIgnoreCase))
                {
                    cleaned += " Valid diff targets: working (alias: unstaged) = uncommitted changes vs the index; staged = index vs HEAD; a commit, branch or tag = working tree vs that ref; refA..refB or refA...refB = a range.";
                }
                return new GitDiffResult { IsError = true, Error = cleaned };
            }

            var filesChanged = CountChangedFiles(diffRaw.Stdout, nameOnly, stat);
            var diff = CapToUtf8Bytes(diffRaw.Stdout, maxBytes);

            return new GitDiffResult { IsError = false, Diff = diff, FilesChanged = filesChanged, Warning = DetectDecodeCorruption(diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git diff failed (target={Target})", target);
            return new GitDiffResult { IsError = true, Error = $"Git diff failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Shows one commit's metadata (hash/author/date/full body) plus the diff it introduced -
    /// equivalent to <c>git show <target></c>. Falls back to the empty tree for a diff base
    /// when <paramref name="target"/> has no parent (a repo's first commit), same as DiffAsync.
    /// </summary>
    public async Task<GitShowResult> ShowAsync(
        string gitRoot, string target, string? paths, int maxBytes, bool nameOnly, bool stat, CancellationToken cancellationToken)
    {
        maxBytes = Math.Clamp(maxBytes, 1024, 524288);
        var formatError = ValidateDiffFormatFlags("show", nameOnly, stat);
        if (formatError is not null)
            return new GitShowResult { IsError = true, Error = formatError };

        try
        {
            const string fieldSep = "\x1f";
            var format = $"%H{fieldSep}%h{fieldSep}%an{fieldSep}%aI{fieldSep}%B";

            var metaRaw = await RunGitAsync(
                gitRoot, ["show", $"--format={format}", "--no-patch", target], cancellationToken);
            if (metaRaw.ExitCode != 0)
                return new GitShowResult { IsError = true, Error = CleanGitStderr(metaRaw.Stderr) };

            var metaParts = metaRaw.Stdout.TrimEnd('\n', '\r').Split(fieldSep);
            if (metaParts.Length < 5)
                return new GitShowResult { IsError = true, Error = $"Could not parse commit metadata for '{target}'." };

            var revParseRaw = await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"{target}^"], cancellationToken);
            // core.quotePath=false so non-ASCII paths in headers and file lists are not octal-escaped.
            var diffArgs = new List<string> { "-c", "core.quotePath=false", "diff" };
            if (nameOnly)
                diffArgs.Add("--name-status");
            else if (stat)
                diffArgs.Add("--stat=200");
            diffArgs.Add(revParseRaw.ExitCode == 0 ? $"{target}^" : EmptyTreeHash);
            diffArgs.Add(target);
            if (!string.IsNullOrWhiteSpace(paths))
            {
                var parsedPaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitShowResult { IsError = true, Error = pathsError };

                diffArgs.Add("--");
                diffArgs.AddRange(parsedPaths!);
            }

            var diffRaw = await RunGitAsync(gitRoot, [.. diffArgs], cancellationToken);
            if (diffRaw.ExitCode != 0)
                return new GitShowResult { IsError = true, Error = CleanGitStderr(diffRaw.Stderr) };

            var filesChanged = CountChangedFiles(diffRaw.Stdout, nameOnly, stat);
            var diff = CapToUtf8Bytes(diffRaw.Stdout, maxBytes);

            return new GitShowResult
            {
                IsError = false,
                Hash = metaParts[0],
                Author = metaParts[2],
                Date = metaParts[3],
                Message = metaParts[4].Trim('\n', '\r'),
                Diff = diff,
                FilesChanged = filesChanged,
                Warning = DetectDecodeCorruption(diff) ?? DetectDecodeCorruption(metaParts[4]),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git show failed (target={Target})", target);
            return new GitShowResult { IsError = true, Error = $"Git show failed: {ex.Message}" };
        }
    }

    private static GitHunksResult HunksFailure(string path, string kind, string error, string detail) => new()
    {
        Path = path,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        ErrorDetail = detail
    };

    /// <summary>
    /// Lists the unstaged hunks of exactly one tracked text file (zero-context diff), each with a 1-based id, line
    /// numbers and a short preview, plus a fingerprint of the whole file diff. Read-only: nothing is staged.
    /// </summary>
    public async Task<GitHunksResult> HunksAsync(
        string gitRoot, string? paths, int count, CancellationToken cancellationToken)
    {
        try
        {
            var load = await LoadFileDiffAsync(gitRoot, paths, cancellationToken);
            if (load.Diff is null)
                return HunksFailure(load.Path, load.ErrorKind ?? GitErrorCodes.Fallback, load.Error ?? "", load.ErrorDetail ?? "");

            return BuildHunksResult(load.Path, load.Diff, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitHunksResult>(ex, "hunks");
        }
    }

    private enum PathClassification
    {
        /// <summary>Not tracked, not in the index, not on disk.</summary>
        Missing,
        /// <summary>Tracked and currently in the index.</summary>
        InIndex,
        /// <summary>Tracked in HEAD but not in the index (deletion staged or about to be staged).</summary>
        HeadOnly,
        /// <summary>Untracked but exists on disk.</summary>
        Untracked
    }

    private record ClassifiedPath(string Path, PathClassification Classification);

    /// <summary>
    /// Validates that a path resolves within the repo root. Returns null if valid, or an error message
    /// naming the path if it resolves outside.
    /// </summary>
    private static string? ValidateRepoRoot(string gitRoot, string path)
    {
        try
        {
            var repoPath = new DirectoryInfo(gitRoot).FullName.TrimEnd(Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(gitRoot, path));
            var normRepoPath = repoPath + Path.DirectorySeparatorChar;

            // Compare: path must start with repo root (with trailing separator) or equal repo root exactly
            if (!fullPath.StartsWith(normRepoPath, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.Equals(repoPath, StringComparison.OrdinalIgnoreCase))
            {
                return $"Path resolves outside the repository root: {path}";
            }

            return null;
        }
        catch
        {
            return $"Invalid path: {path}";
        }
    }

    /// <summary>
    /// Parses the ignored-paths advisory from git stderr when `git add` exits with code 1.
    /// Extracts paths between "The following paths are ignored" and the next hint/error line.
    /// Returns a list of ignored paths, or empty if the pattern is not found.
    /// </summary>
    private static List<string> ParseIgnoredPaths(string stderr)
    {
        var ignoredPaths = new List<string>();
        var lines = stderr.Split('\n');
        var inIgnoredBlock = false;

        foreach (var line in lines)
        {
            if (line.Contains("The following paths are ignored"))
            {
                inIgnoredBlock = true;
                continue;
            }

            if (inIgnoredBlock)
            {
                // The block ends at the first blank or hint line. Git prints each ignored path
                // unindented (verified on git 2.55), so any other line here is a path.
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("hint:"))
                    break;

                ignoredPaths.Add(line.Trim());
            }
        }

        return ignoredPaths;
    }

    /// <summary>
    /// Cleans git stderr output by removing noisy CRLF warning lines and capping total length.
    /// Removes lines matching:
    /// - "warning: ... LF will be replaced by CRLF ..."
    /// - "The file will have its original line endings ..."
    /// Caps output at approximately 2000 characters with a "(N more lines omitted)" suffix.
    /// </summary>
    private static string CleanGitStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return stderr;

        // RunGitAsync rebuilds stderr with AppendLine (CRLF on Windows); drop the CR of each line so
        // none survives into the joined message.
        var lines = stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var cleaned = new List<string>();

        foreach (var line in lines)
        {
            // Skip CRLF warning lines
            if (line.Contains("warning:") && line.Contains("LF will be replaced by CRLF"))
                continue;
            if (line.Contains("The file will have its original line endings"))
                continue;

            cleaned.Add(line);
        }

        var result = string.Join("\n", cleaned).Trim();

        // Cap at ~2000 characters
        const int maxChars = 2000;
        if (result.Length > maxChars)
        {
            // Count how many lines we're omitting
            var truncated = result.Substring(0, maxChars);
            var remainingText = result.Substring(maxChars);
            var omittedLines = remainingText.Count(c => c == '\n');
            result = truncated + $"\n({omittedLines} more lines omitted)";
        }

        return result;
    }

    /// <summary>
    /// Classifies a list of paths by their git and filesystem state. Uses git ls-files (index),
    /// git ls-tree (HEAD), and disk existence to determine whether each path is:
    /// - Missing: not tracked, not indexed, not on disk
    /// - InIndex: currently in the staging index
    /// - HeadOnly: tracked in HEAD but not in the current index (deletion candidate)
    /// - Untracked: untracked but exists on disk
    ///
    /// Paths are normalized to forward slashes for git compatibility. Normalizes input from the
    /// caller's repo-relative paths.
    /// </summary>
    private async Task<List<ClassifiedPath>> ClassifyPathsAsync(
        string gitRoot, List<string> filePaths, CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0)
            return [];

        // Normalize paths: \ to / for git, and remove any trailing slashes.
        var normalized = filePaths
            .Select(p => p.Replace('\\', '/').TrimEnd('/'))
            .ToList();

        // Check index state: git --literal-pathspecs ls-files -z --cached -- <paths>
        var lsFilesBase = new[] { "--literal-pathspecs", "ls-files", "-z", "--cached", "--" };
        var lsFilesArgs = lsFilesBase.Concat(normalized).ToArray();
        var lsFilesRaw = await RunGitAsync(gitRoot, lsFilesArgs, cancellationToken);

        var inIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lsFilesRaw.ExitCode == 0 && lsFilesRaw.Stdout.Length > 0)
        {
            var indexPaths = lsFilesRaw.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in indexPaths)
                inIndex.Add(p);
        }

        // Check HEAD state: git --literal-pathspecs ls-tree -r -z --name-only HEAD -- <paths>
        // For an unborn HEAD (no commits), this will fail, which we treat as empty.
        var lsTreeBase = new[] { "--literal-pathspecs", "ls-tree", "-r", "-z", "--name-only", "HEAD", "--" };
        var lsTreeArgs = lsTreeBase.Concat(normalized).ToArray();
        var lsTreeRaw = await RunGitAsync(gitRoot, lsTreeArgs, cancellationToken);

        var inHead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lsTreeRaw.ExitCode == 0 && lsTreeRaw.Stdout.Length > 0)
        {
            var headPaths = lsTreeRaw.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in headPaths)
                inHead.Add(p);
        }

        // Classify each path
        var result = new List<ClassifiedPath>();
        foreach (var path in normalized)
        {
            var fullPath = Path.Combine(gitRoot, path);
            var fileExists = File.Exists(fullPath);
            var dirExists = Directory.Exists(fullPath);
            var diskExists = fileExists || dirExists;

            // A tracked directory deleted from disk never appears as an entry itself; ls-files and
            // ls-tree list the files beneath it. Match on the "<path>/" prefix, but only when the
            // path is gone from disk: a directory that still exists keeps its previous handling
            // (plain git add, which also picks up new untracked files inside it).
            var prefix = path + "/";
            var inIndexState = inIndex.Contains(path)
                || (!diskExists && inIndex.Any(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            var inHeadState = inHead.Contains(path)
                || (!diskExists && inHead.Any(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

            var classification = (inIndexState, inHeadState, diskExists) switch
            {
                (true, _, _) => PathClassification.InIndex,
                (false, true, false) => PathClassification.HeadOnly,
                (false, false, true) => PathClassification.Untracked,
                _ => PathClassification.Missing
            };

            result.Add(new ClassifiedPath(path, classification));
        }

        return result;
    }

    /// <summary>
    /// Returns those of <paramref name="paths"/> that .gitignore rules would make <c>git add</c>
    /// refuse (git check-ignore, exit 0 = at least one ignored, 1 = none). Checked BEFORE adding
    /// because <c>git add a.txt ignored/b.txt</c> stages a.txt and only then exits 1 with the
    /// advisory, so detecting the advisory afterwards cannot honour "Nothing was staged." Also names
    /// the paths the caller listed, whereas the advisory names the ignored ancestor directory.
    /// </summary>
    private async Task<List<string>> FindIgnoredPathsAsync(
        string gitRoot, List<string> paths, CancellationToken cancellationToken)
    {
        var ignored = new List<string>();
        foreach (var path in paths)
        {
            var raw = await RunGitAsync(gitRoot, ["check-ignore", "-q", "--", path], cancellationToken);
            if (raw.ExitCode == 0)
                ignored.Add(path);
        }

        return ignored;
    }

    /// <summary>
    /// Stages files according to <paramref name="scope"/>. Scope and path list are one decision
    /// rather than two independent inputs: the former <c>stageAll</c> boolean could override an
    /// explicit file list and run <c>git add -A</c>, quietly staging unrelated untracked files.
    /// Every mismatch between scope and paths is refused up front, naming the conflict and the
    /// call that would have worked.
    /// </summary>
    public async Task<GitStatusResult> StageAsync(
        string gitRoot, GitStageScope scope, string? paths, CancellationToken cancellationToken)
    {
        var hasPaths = !string.IsNullOrWhiteSpace(paths);

        if (scope != GitStageScope.listed && hasPaths)
        {
            return new GitStatusResult
            {
                IsError = true,
                Error = $"You named files to stage but passed scope=\"{scope}\", which ignores them. " +
                        "Pass scope=\"listed\" to stage exactly the files you named (untracked ones " +
                        $"included), or drop the file list to stage by scope=\"{scope}\". Nothing was staged."
            };
        }

        if (scope == GitStageScope.listed && !hasPaths)
        {
            return new GitStatusResult
            {
                IsError = true,
                Error = "scope=\"listed\" stages exactly the files you name, but no files were given. " +
                        "Pass files (or paths) as a comma-separated list of repo-relative paths, e.g. " +
                        "files: \"src/Foo.cs,docs/notes.md\". To stage without naming files use " +
                        "scope=\"tracked\" (already-tracked changes only) or scope=\"all\" (everything, " +
                        "untracked included). Nothing was staged."
            };
        }

        try
        {
            string[] stageArgs;
            switch (scope)
            {
                case GitStageScope.all:
                    stageArgs = ["add", "-A"];
                    break;
                case GitStageScope.listed:
                    // For scope=listed, classify the paths first to handle deletions (HeadOnly)
                    // and ignored files (Untracked under .gitignore) correctly.
                    var filePaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                    if (pathsError != null)
                        return new GitStatusResult { IsError = true, Error = pathsError };

                    // Validate that paths stay within repo
                    foreach (var p in filePaths!)
                    {
                        var error = ValidateRepoRoot(gitRoot, p);
                        if (error != null)
                        {
                            return new GitStatusResult
                            {
                                IsError = true,
                                ErrorKind = GitErrorCodes.PathNotFound,
                                ErrorDetail = GitErrorDetails.PathNotFound,
                                Error = $"{error}. Nothing was staged."
                            };
                        }
                    }

                    // Classify paths
                    var classified = await ClassifyPathsAsync(gitRoot, filePaths!.ToList(), cancellationToken);

                    // Check for missing paths
                    var missing = classified.Where(cp => cp.Classification == PathClassification.Missing).ToList();
                    if (missing.Count > 0)
                    {
                        var missingList = string.Join(", ", missing.Select(m => $"\"{m.Path}\""));
                        return new GitStatusResult
                        {
                            IsError = true,
                            ErrorKind = GitErrorCodes.PathNotFound,
                            ErrorDetail = GitErrorDetails.PathNotFound,
                            Error = $"The following paths are not tracked and do not exist on disk: {missingList}. Nothing was staged."
                        };
                    }

                    // Separate paths by classification for staged staging
                    var inIndex = classified.Where(cp => cp.Classification == PathClassification.InIndex).Select(cp => cp.Path).ToList();
                    var headOnly = classified.Where(cp => cp.Classification == PathClassification.HeadOnly).Select(cp => cp.Path).ToList();
                    var untracked = classified.Where(cp => cp.Classification == PathClassification.Untracked).Select(cp => cp.Path).ToList();

                    // Stage InIndex paths using git --literal-pathspecs add -u -- <paths>
                    if (inIndex.Count > 0)
                    {
                        var addUBase = new[] { "--literal-pathspecs", "add", "-u", "--" };
                        var addUArgs = addUBase.Concat(inIndex).ToArray();
                        var addURaw = await RunGitAsync(gitRoot, addUArgs, cancellationToken);
                        if (addURaw.ExitCode != 0)
                        {
                            return new GitStatusResult
                            {
                                IsError = true,
                                Error = $"git add -u failed: {CleanGitStderr(addURaw.Stderr)}"
                            };
                        }
                    }

                    // Stage HeadOnly paths - these are deletions already staged or staged here via the pathspec
                    // We skip them and report them as already staged (no action needed)
                    // They will be committed if included in the pathspec

                    // Stage Untracked paths using plain git --literal-pathspecs add -- <paths>
                    if (untracked.Count > 0)
                    {
                        // Refuse before mutating anything: git add stages the non-ignored paths of a
                        // mixed list and only then exits 1, so a post-hoc check cannot promise atomicity.
                        var ignoredListed = await FindIgnoredPathsAsync(gitRoot, untracked, cancellationToken);
                        if (ignoredListed.Count > 0)
                        {
                            var ignoredNames = string.Join(", ", ignoredListed.Select(p => $"\"{p}\""));
                            return new GitStatusResult
                            {
                                IsError = true,
                                ErrorKind = GitErrorCodes.IgnoredPath,
                                ErrorDetail = GitErrorDetails.IgnoredPath,
                                Error = $"The following paths are ignored by .gitignore: {ignoredNames}. The tool has no -f flag to force-add them. Remove them from the list or un-ignore them in .gitignore to stage them. Nothing was staged."
                            };
                        }

                        var addBase = new[] { "--literal-pathspecs", "add", "--" };
                        var addArgs = addBase.Concat(untracked).ToArray();
                        var addRaw = await RunGitAsync(gitRoot, addArgs, cancellationToken);

                        // Backstop: check for ignored-path advisory in stderr
                        if (addRaw.ExitCode != 0 && addRaw.Stderr.Contains("The following paths are ignored"))
                        {
                            var ignoredPaths = ParseIgnoredPaths(addRaw.Stderr);
                            if (ignoredPaths.Count > 0)
                            {
                                var ignoredList = string.Join(", ", ignoredPaths.Select(p => $"\"{p}\""));
                                return new GitStatusResult
                                {
                                    IsError = true,
                                    ErrorKind = GitErrorCodes.IgnoredPath,
                                    ErrorDetail = GitErrorDetails.IgnoredPath,
                                    Error = $"The following paths are ignored by .gitignore: {ignoredList}. The tool has no -f flag to force-add them. Remove them from the list or un-ignore them in .gitignore to stage them. Nothing was staged."
                                };
                            }
                        }

                        if (addRaw.ExitCode != 0)
                        {
                            return new GitStatusResult
                            {
                                IsError = true,
                                Error = $"git add failed: {CleanGitStderr(addRaw.Stderr)}"
                            };
                        }
                    }

                    return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
                case GitStageScope.tracked:
                default:
                    stageArgs = ["--literal-pathspecs", "add", "-u"];
                    var trackedRaw = await RunGitAsync(gitRoot, stageArgs, cancellationToken);
                    if (trackedRaw.ExitCode != 0)
                        return new GitStatusResult { IsError = true, Error = $"git add failed: {CleanGitStderr(trackedRaw.Stderr)}" };
                    return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
            }

            var stageRaw = await RunGitAsync(gitRoot, stageArgs, cancellationToken);
            if (stageRaw.ExitCode != 0)
                return new GitStatusResult { IsError = true, Error = $"git add failed: {CleanGitStderr(stageRaw.Stderr)}" };

            return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git stage failed");
            return new GitStatusResult { IsError = true, Error = $"Git stage failed: {ex.Message}" };
        }
    }
    /// <summary>
    /// Removes files from the index (<c>git reset</c>), leaving the working tree untouched. The
    /// tool could previously stage but never un-stage, so any mis-stage forced a shell fallback ->
    /// a state the tool could create but not exit. With no paths this resets the whole index; with
    /// paths it un-stages only those.
    /// </summary>
    public async Task<GitStatusResult> UnstageAsync(
        string gitRoot, string? paths, CancellationToken cancellationToken)
    {
        try
        {
            string[] resetArgs;
            if (string.IsNullOrWhiteSpace(paths))
            {
                // Whole index. Deliberately a plain `git reset` (mixed, no ref): it un-stages
                // everything while leaving every working-tree edit on disk, so this can never
                // destroy uncommitted work the way `reset --hard` would.
                resetArgs = ["reset"];
            }
            else
            {
                var filePaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitStatusResult { IsError = true, Error = pathsError };
                resetArgs = ["reset", "--", .. filePaths!];
            }

            var resetRaw = await RunGitAsync(gitRoot, resetArgs, cancellationToken);

            // `git reset` exits 1 when the index still differs from HEAD after the reset, which is
            // the normal outcome here, not a failure. Treat stderr content as the real signal
            // rather than trusting the exit code alone.
            var resetErrText = CleanGitStderr(resetRaw.Stderr);
            if (resetRaw.ExitCode != 0 && resetErrText.Length > 0)
                return new GitStatusResult { IsError = true, Error = $"git reset failed: {resetErrText}" };

            return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git unstage failed");
            return new GitStatusResult { IsError = true, Error = $"Git unstage failed: {ex.Message}" };
        }
    }
    public async Task<GitCommitResult> CommitAsync(
        string gitRoot, string? message, GitStageScope? scope, string? paths, bool amend, CancellationToken cancellationToken)
    {
        // Amend keeps HEAD's message when none is supplied (git commit --amend --no-edit);
        // a plain commit has no prior message to fall back on, so it stays required.
        if (string.IsNullOrWhiteSpace(message) && !amend)
        {
            return new GitCommitResult
            {
                IsError = true,
                Error = "message is required for operation=commit. Pass message: \"<what this commit does>\". Nothing was committed."
            };
        }

        try
        {
            // Refuse before anything is staged or rewritten. Force-push is not exposed, so amending
            // a commit the upstream already has would leave local and remote diverged with no way
            // to reconcile them through this tool. No upstream configured means nothing is known
            // to be published, so the amend is allowed.
            if (amend)
            {
                var upstreamRaw = await RunGitAsync(gitRoot,
                    ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], cancellationToken);
                if (upstreamRaw.ExitCode == 0 && !string.IsNullOrWhiteSpace(upstreamRaw.Stdout))
                {
                    var upstreamName = upstreamRaw.Stdout.Trim();
                    // Exit 0 = HEAD is an ancestor of (or equal to) the upstream tip = already pushed.
                    var containedRaw = await RunGitAsync(gitRoot,
                        ["merge-base", "--is-ancestor", "HEAD", "@{upstream}"], cancellationToken);
                    if (containedRaw.ExitCode == 0)
                    {
                        return new GitCommitResult
                        {
                            IsError = true,
                            Error = $"HEAD is already contained in its upstream '{upstreamName}', so amending it would rewrite published history, " +
                                    "and this tool does not expose force-push (the branch would diverge from the remote). " +
                                    "Make a new commit instead, or use operation=revert to undo the pushed commit. Nothing was amended."
                        };
                    }
                }
            }

            // For scope=listed, classify paths first to determine which ones need pre-staging
            // (untracked only) vs those already in the index.
            bool stagedUntrackedFiles = false;
            if (scope == GitStageScope.listed && !string.IsNullOrWhiteSpace(paths))
            {
                var filePaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitCommitResult { IsError = true, Error = pathsError };

                // Validate that paths stay within repo
                foreach (var p in filePaths!)
                {
                    var error = ValidateRepoRoot(gitRoot, p);
                    if (error != null)
                    {
                        return new GitCommitResult
                        {
                            IsError = true,
                            ErrorKind = GitErrorCodes.PathNotFound,
                            ErrorDetail = GitErrorDetails.PathNotFound,
                            Error = $"{error}. Nothing was committed."
                        };
                    }
                }

                // Classify paths
                var classified = await ClassifyPathsAsync(gitRoot, filePaths!.ToList(), cancellationToken);

                // Check for missing paths - reject early
                var missing = classified.Where(cp => cp.Classification == PathClassification.Missing).ToList();
                if (missing.Count > 0)
                {
                    var missingList = string.Join(", ", missing.Select(m => $"\"{m.Path}\""));
                    return new GitCommitResult
                    {
                        IsError = true,
                        ErrorKind = GitErrorCodes.PathNotFound,
                        ErrorDetail = GitErrorDetails.PathNotFound,
                        Error = $"The following paths are not tracked and do not exist on disk: {missingList}. Nothing was committed."
                    };
                }

                // Pre-stage only Untracked paths (they must be in the index for the pathspec to match)
                var untracked = classified.Where(cp => cp.Classification == PathClassification.Untracked).Select(cp => cp.Path).ToList();
                if (untracked.Count > 0)
                {
                    // Refuse before mutating anything (see StageAsync): a mixed list would otherwise
                    // have its non-ignored paths staged before git add exits 1.
                    var ignoredListed = await FindIgnoredPathsAsync(gitRoot, untracked, cancellationToken);
                    if (ignoredListed.Count > 0)
                    {
                        var ignoredNames = string.Join(", ", ignoredListed.Select(p => $"\"{p}\""));
                        return new GitCommitResult
                        {
                            IsError = true,
                            ErrorKind = GitErrorCodes.IgnoredPath,
                            ErrorDetail = GitErrorDetails.IgnoredPath,
                            Error = $"The following paths are ignored by .gitignore: {ignoredNames}. The tool has no -f flag to force-add them. Remove them from the list or un-ignore them in .gitignore to commit them. Nothing was committed."
                        };
                    }

                    var addBase = new[] { "--literal-pathspecs", "add", "--" };
                    var addArgs = addBase.Concat(untracked).ToArray();
                    var addRaw = await RunGitAsync(gitRoot, addArgs, cancellationToken);

                    // Backstop: check for ignored-path advisory
                    if (addRaw.ExitCode != 0 && addRaw.Stderr.Contains("The following paths are ignored"))
                    {
                        var ignoredPaths = ParseIgnoredPaths(addRaw.Stderr);
                        if (ignoredPaths.Count > 0)
                        {
                            var ignoredList = string.Join(", ", ignoredPaths.Select(p => $"\"{p}\""));
                            return new GitCommitResult
                            {
                                IsError = true,
                                ErrorKind = GitErrorCodes.IgnoredPath,
                                ErrorDetail = GitErrorDetails.IgnoredPath,
                                Error = $"The following paths are ignored by .gitignore: {ignoredList}. The tool has no -f flag to force-add them. Remove them from the list or un-ignore them in .gitignore to commit them. Nothing was committed."
                            };
                        }
                    }

                    if (addRaw.ExitCode != 0)
                    {
                        return new GitCommitResult
                        {
                            IsError = true,
                            Error = $"git add failed: {CleanGitStderr(addRaw.Stderr)}"
                        };
                    }

                    stagedUntrackedFiles = true;
                }
            }
            else if (scope is { } explicitScope && scope != GitStageScope.listed)
            {
                // For scope=all or scope=tracked, pre-stage as requested
                var stageResult = await StageAsync(gitRoot, explicitScope, paths, cancellationToken);
                if (stageResult.IsError)
                    return new GitCommitResult { IsError = true, Error = stageResult.Error };
            }

            // Build commit arguments
            var messageArgs = (amend, HasMessage: !string.IsNullOrWhiteSpace(message)) switch
            {
                (amend: true, HasMessage: true) => new[] { "--amend", "-m", message! },
                (amend: true, HasMessage: false) => new[] { "--amend", "--no-edit" },
                _ => new[] { "-m", message! }
            };

            string[] commitArgs;
            if (!string.IsNullOrWhiteSpace(paths))
            {
                // When specific files were named, restrict the commit to those paths via --only
                var filePaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out _);
                if (filePaths != null && filePaths.Length > 0)
                {
                    var commitBase = new[] { "--literal-pathspecs", "commit" }.Concat(messageArgs).Concat(new[] { "--only", "--" }).ToArray();
                    commitArgs = commitBase.Concat(filePaths).ToArray();
                }
                else
                {
                    commitArgs = new[] { "commit" }.Concat(messageArgs).ToArray();
                }
            }
            else
            {
                commitArgs = new[] { "commit" }.Concat(messageArgs).ToArray();
            }

            var commitRaw = await RunGitAsync(gitRoot, commitArgs, cancellationToken);
            if (commitRaw.ExitCode != 0)
            {
                var detail = string.Join("\n", new[] { commitRaw.Stdout.Trim(), CleanGitStderr(commitRaw.Stderr) }
                    .Where(s => !string.IsNullOrEmpty(s)));
                var errorText = string.IsNullOrEmpty(detail)
                    ? $"git commit exited with code {commitRaw.ExitCode}"
                    : detail;

                // If we staged untracked files and the commit failed, provide recovery info
                string failureNote = "";
                if (stagedUntrackedFiles)
                {
                    var countRaw = await RunGitAsync(gitRoot, new[] { "-c", "core.quotePath=false", "diff", "--cached", "--name-only" }, cancellationToken);
                    var stagedCount = countRaw.ExitCode == 0 ? countRaw.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
                    failureNote = $" Staging already ran; the index now holds {stagedCount} staged paths. Call Git(operation: unstage) to undo.";
                }

                var failed = new GitCommitResult { IsError = true, Error = $"git commit failed: {errorText}{failureNote}" };

                // Classify structurally, not from git's localizable text. Amend is skipped (an amend
                // with nothing staged is legal), as is a path list that was not classified up front
                // (an unknown pathspec would otherwise also look like "nothing staged").
                var pathsWereClassified = string.IsNullOrWhiteSpace(paths) || scope == GitStageScope.listed;
                if (!amend && pathsWereClassified && await NothingStagedAsync(gitRoot, paths, cancellationToken))
                {
                    failed.ErrorKind = GitErrorCodes.NothingToCommit;
                    failed.ErrorDetail = GitErrorDetails.NothingToCommit;
                }

                return await AppendInProgressAsync(failed, gitRoot, cancellationToken);
            }

            var hashRaw = await RunGitAsync(gitRoot, new[] { "rev-parse", "HEAD" }, cancellationToken);
            var hash = hashRaw.ExitCode == 0 ? hashRaw.Stdout.Trim() : "";

            // Sanity check, not a correctness fix: a SHA-1 hash is always exactly 40 hex
            // characters. This guards against a suspected (unconfirmed, see
            // docs/current/finding_git_commit_commithash_length_unconfirmed_no_source_mechanism.md)
            // response-length anomaly reported across multiple sessions - if this ever fires, it
            // proves the corruption happens at or before this point rather than in a later
            // serialization/transport layer, which the original investigation could not determine.
            if (hash.Length > 0 && (hash.Length != 40 || !hash.All(Uri.IsHexDigit)))
            {
                _logger.LogWarning(
                    "git rev-parse HEAD returned a value that is not a well-formed 40-character SHA-1 hash: " +
                    "length={Length}, value={Hash}. Returning it to the caller as-is for visibility.",
                    hash.Length, hash);
            }

            // message can be null here (amend --no-edit kept HEAD's existing message), so read
            // back the commit's actual message rather than echoing the (possibly absent) input.
            var msgRaw = await RunGitAsync(gitRoot, new[] { "log", "-1", "--format=%B" }, cancellationToken);
            var finalMessage = msgRaw.ExitCode == 0 ? msgRaw.Stdout.Trim() : message ?? "";

            // Populate RemainingStaged: list of paths still in the index after the commit
            // core.quotePath=false so non-ASCII paths come back verbatim and can be passed to `files`.
            var remainingRaw = await RunGitAsync(gitRoot, new[] { "-c", "core.quotePath=false", "diff", "--cached", "--name-only" }, cancellationToken);
            var remainingStaged = new List<string>();
            if (remainingRaw.ExitCode == 0 && !string.IsNullOrWhiteSpace(remainingRaw.Stdout))
            {
                // RunGitAsync rebuilds stdout with AppendLine (CRLF on Windows), so split on both
                // separators; splitting on LF alone left a trailing CR on every entry but the last.
                remainingStaged = remainingRaw.Stdout
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
            }

            return new GitCommitResult { IsError = false, CommitHash = hash, Message = finalMessage, RemainingStaged = remainingStaged };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git commit failed");
            return new GitCommitResult { IsError = true, Error = $"Git commit failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Backs out of whichever multi-step operation a conflict left half-finished (merge, rebase,
    /// cherry-pick, revert or am) by running its own <c>--abort</c>, which restores the pre-operation
    /// state. Nothing in progress is an error, not a silent success, so a caller never believes a
    /// back-out happened when it did not. Returns the status after the abort.
    /// </summary>
    public async Task<GitStatusResult> AbortAsync(string gitRoot, CancellationToken cancellationToken)
    {
        try
        {
            var op = await DetectInProgressAsync(gitRoot, cancellationToken);
            if (op is null)
            {
                return new GitStatusResult
                {
                    IsError = true,
                    Error = "Nothing to abort: no merge, rebase, cherry-pick, revert or am is in progress. Call status to see the current state. Nothing was changed."
                };
            }

            var abortRaw = await RunGitAsync(gitRoot, [op, "--abort"], cancellationToken);
            if (abortRaw.ExitCode != 0)
            {
                var detail = string.Join("\n", new[] { abortRaw.Stdout.Trim(), CleanGitStderr(abortRaw.Stderr) }.Where(s => s.Length > 0));
                return new GitStatusResult { IsError = true, Error = $"git {op} --abort failed: {detail} The repository is still mid-{op}." };
            }

            var status = await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
            status.AbortedOperation = op;
            return status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git abort failed");
            return new GitStatusResult { IsError = true, Error = $"Git abort failed: {ex.Message}" };
        }
    }

    public async Task<GitRevertResult> RevertAsync(
        string gitRoot, string? commitHash, bool noCommit, int? mainline, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commitHash))
            return new GitRevertResult { IsError = true, ErrorKind = GitErrorCodes.RefRequired, ErrorDetail = GitErrorDetails.RefRequiredRevert, Error = "commitHash is required for operation=revert." };

        if (mainline is < 1)
            return new GitRevertResult { IsError = true, Error = $"mainline must be 1 or greater (got {mainline}); 1 is the parent that was merged into. Nothing was reverted." };

        try
        {
            // Count the commit's parents up front so a merge commit gets an error that names the
            // `mainline` parameter, rather than git's "commit X is a merge but no -m option was given".
            var parentsRaw = await RunGitAsync(gitRoot, ["rev-list", "--parents", "-n", "1", commitHash, "--"], cancellationToken);
            if (parentsRaw.ExitCode != 0)
                return new GitRevertResult { IsError = true, CommitHash = commitHash, Error = $"Cannot resolve commitHash '{commitHash}': {CleanGitStderr(parentsRaw.Stderr)} Nothing was reverted." };

            var parentCount = parentsRaw.Stdout.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length - 1;
            if (parentCount > 1 && mainline is null)
                return new GitRevertResult { IsError = true, CommitHash = commitHash, Error = $"Commit '{commitHash}' is a merge commit with {parentCount} parents. Pass mainline: 1 (the branch merged into; almost always right) to {parentCount} to say which parent to keep. Nothing was reverted." };
            if (mainline is { } requestedMainline)
            {
                if (parentCount <= 1)
                    return new GitRevertResult { IsError = true, CommitHash = commitHash, Error = $"mainline was supplied but commit '{commitHash}' is not a merge commit. Omit mainline. Nothing was reverted." };
                if (requestedMainline > parentCount)
                    return new GitRevertResult { IsError = true, CommitHash = commitHash, Error = $"mainline {requestedMainline} is out of range: commit '{commitHash}' has {parentCount} parents. Nothing was reverted." };
            }

            var args = new List<string> { "revert", "--no-edit" };
            if (noCommit)
                args.Add("--no-commit");
            if (mainline is { } parentNumber)
            {
                args.Add("-m");
                args.Add(parentNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            args.Add(commitHash);

            var revertRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);

            if (revertRaw.ExitCode != 0)
                return await AppendInProgressAsync(new GitRevertResult { IsError = true, CommitHash = commitHash, Error = CleanGitStderr(revertRaw.Stderr) }, gitRoot, cancellationToken);

            string newHash = "";
            if (!noCommit)
            {
                var hashRaw = await RunGitAsync(gitRoot, ["rev-parse", "HEAD"], cancellationToken);
                newHash = hashRaw.ExitCode == 0 ? hashRaw.Stdout.Trim() : "";
            }

            return new GitRevertResult
            {
                IsError = false,
                CommitHash = newHash,
                Message = noCommit ? "Revert staged - call commit to finalise." : revertRaw.Stdout.Trim(),
                PendingCommit = noCommit,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git revert failed (hash={Hash})", commitHash);
            return new GitRevertResult { IsError = true, Error = $"Git revert failed: {ex.Message}" };
        }
    }

       /// <summary>
    /// Moves HEAD/the current branch to <paramref name="refName"/> without ever touching the working
    /// tree, so this can never discard uncommitted edits the way <c>git reset --hard</c> would -- only
    /// <see cref="GitResetMode.soft"/> and <see cref="GitResetMode.mixed"/> are exposed, matching
    /// <see cref="UnstageAsync"/>'s existing precedent of never surfacing a destructive git mode.
    /// </summary>
    public async Task<GitStatusResult> ResetAsync(
        string gitRoot, string? refName, GitResetMode mode, CancellationToken cancellationToken)
    {
        // No default ref: a silent HEAD~1 meant a bare reset call quietly undid the last commit.
        if (string.IsNullOrWhiteSpace(refName))
        {
            return new GitStatusResult
            {
                IsError = true,
                ErrorKind = GitErrorCodes.RefRequired,
                ErrorDetail = GitErrorDetails.RefRequiredReset,
                Error = "reset needs an explicit ref, e.g. ref: \"HEAD~1\" to undo the last commit (working tree kept). " +
                        "To unstage without moving HEAD use operation=unstage. Nothing was reset."
            };
        }

        var target = refName;

        try
        {
            var modeFlag = mode == GitResetMode.soft ? "--soft" : "--mixed";
            var resetRaw = await RunGitAsync(gitRoot, ["reset", modeFlag, target], cancellationToken);

            if (resetRaw.ExitCode != 0)
                return new GitStatusResult { IsError = true, Error = $"git reset failed: {CleanGitStderr(resetRaw.Stderr)}" };

            return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git reset failed (ref={Ref}, mode={Mode})", target, mode);
            return new GitStatusResult { IsError = true, Error = $"Git reset failed: {ex.Message}" };
        }
    }

       /// Lists branches (default), or creates/deletes one when <paramref name="branchName"/> is
    /// given. Creation is a plain, non-destructive <c>git branch <name> [startPoint]</c> -> it
    /// does not switch to the new branch; use <c>checkout</c> for that, optionally with
    /// <c>createBranch=true</c> to do both in one call. Deletion uses the safe <c>-d</c> form
    /// (refuses an unmerged branch) rather than <c>-D</c>, so a mistaken delete can't silently
    /// discard commits.
    /// </summary>
    public async Task<GitBranchResult> BranchAsync(
        string gitRoot, string? branchName, string? startPoint, bool deleteBranch, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                var listRaw = await RunGitAsync(gitRoot,
                    ["branch", "--list", "--all"], cancellationToken);
                if (listRaw.ExitCode != 0)
                    return new GitBranchResult { IsError = true, Error = CleanGitStderr(listRaw.Stderr) };

                var branches = new List<GitBranchEntry>();
                foreach (var rawLine in listRaw.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    var isCurrent = line.StartsWith("* ", StringComparison.Ordinal);
                    var name = (isCurrent ? line[2..] : line[2..]).Trim();
                    if (name.Contains("->", StringComparison.Ordinal)) continue; // e.g. "remotes/origin/HEAD -> origin/main"
                    var isRemote = name.StartsWith("remotes/", StringComparison.Ordinal);
                    branches.Add(new GitBranchEntry
                    {
                        Name = isRemote ? name["remotes/".Length..] : name,
                        IsCurrent = isCurrent,
                        IsRemote = isRemote,
                    });
                }

                return new GitBranchResult { IsError = false, Branches = branches };
            }

            if (deleteBranch)
            {
                var delRaw = await RunGitAsync(gitRoot,
                    ["branch", "-d", branchName], cancellationToken);
                if (delRaw.ExitCode != 0)
                    return new GitBranchResult
                    {
                        IsError = true,
                        Error = $"git branch -d failed: {CleanGitStderr(delRaw.Stderr)}. If the branch truly should be discarded unmerged, this tool deliberately does not expose -D; use the shell as a documented exception."
                    };

                return new GitBranchResult { IsError = false, Deleted = branchName };
            }

            string[] createArgs = string.IsNullOrWhiteSpace(startPoint)
                ? ["branch", branchName]
                : ["branch", branchName, startPoint];
            var createRaw = await RunGitAsync(gitRoot, createArgs, cancellationToken);
            if (createRaw.ExitCode != 0)
                return new GitBranchResult { IsError = true, Error = $"git branch failed: {CleanGitStderr(createRaw.Stderr)}" };

            return new GitBranchResult { IsError = false, Created = branchName };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git branch failed");
            return new GitBranchResult { IsError = true, Error = $"Git branch failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Switches to <paramref name="branchName"/>, optionally creating it first
    /// (<c>git checkout -b</c>) when <paramref name="createBranch"/> is set and it doesn't already
    /// exist. Uses plain <c>checkout</c> rather than <c>switch</c> for compatibility with older git.
    /// </summary>
    public async Task<GitCheckoutResult> CheckoutAsync(
        string gitRoot, string? branchName, bool createBranch, string? startPoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return new GitCheckoutResult
            {
                IsError = true,
                ErrorKind = GitErrorCodes.RefRequired,
                ErrorDetail = GitErrorDetails.RefRequiredCheckout,
                Error = "branchName is required for operation=checkout. Pass the branch to switch to, and createBranch=true if it doesn't exist yet."
            };
        }

        // startPoint only means something when a branch is being created. Silently dropping it
        // would leave the caller believing the branch was based on it.
        if (!createBranch && !string.IsNullOrWhiteSpace(startPoint))
        {
            return new GitCheckoutResult
            {
                IsError = true,
                Branch = branchName,
                Error = $"startPoint '{startPoint}' was supplied without createBranch=true, so it would be ignored. " +
                        "Pass createBranch=true to create the branch from it, or omit startPoint to switch to an existing branch. Nothing was checked out."
            };
        }

        try
        {
            // createBranch on a branch that already exists is a plain checkout (idempotent for
            // retries); only a genuinely missing branch is created with -b. CreatedNewBranch
            // reports which of the two actually happened.
            var createNew = false;
            string? note = null;
            if (createBranch)
            {
                var existsRaw = await RunGitAsync(gitRoot,
                    ["rev-parse", "--verify", "--quiet", $"refs/heads/{branchName}"], cancellationToken);
                if (existsRaw.ExitCode == 0)
                {
                    note = $"Branch '{branchName}' already existed, so it was checked out as-is rather than created" +
                           (string.IsNullOrWhiteSpace(startPoint) ? "." : $"; startPoint '{startPoint}' was ignored.");
                }
                else
                {
                    createNew = true;
                }
            }

            // Trailing "--" on the plain form: a branch that shares its name with a file or
            // directory is otherwise ambiguous, and git refuses ("ambiguous argument").
            List<string> args = createNew
                ? string.IsNullOrWhiteSpace(startPoint)
                    ? ["checkout", "-b", branchName]
                    : ["checkout", "-b", branchName, startPoint]
                : ["checkout", branchName, "--"];

            var checkoutRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);
            if (checkoutRaw.ExitCode != 0)
                return new GitCheckoutResult { IsError = true, Branch = branchName, Error = CleanGitStderr(checkoutRaw.Stderr) };

            return new GitCheckoutResult { IsError = false, Branch = branchName, CreatedNewBranch = createNew, Note = note };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git checkout failed (branch={Branch})", branchName);
            return new GitCheckoutResult { IsError = true, Branch = branchName, Error = $"Git checkout failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Pushes the current branch to <paramref name="remoteName"/>. Never forces -> there is no
    /// exposed <c>--force</c>/<c>--force-with-lease</c>, since a wrong force-push is destructive to
    /// shared history and this tool has no confirmation gate for it yet.
    /// </summary>
    public async Task<GitRemoteResult> PushAsync(
        string gitRoot, string remoteName, bool setUpstream, CancellationToken cancellationToken)
    {
        try
        {
            var branchRaw = await RunGitAsync(gitRoot,
                ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
            var branch = branchRaw.ExitCode == 0 ? branchRaw.Stdout.Trim() : null;

            List<string> args = ["push"];
            if (setUpstream)
                args.Add("-u");
            args.Add(remoteName);
            if (!string.IsNullOrEmpty(branch))
                args.Add(branch);

            var pushRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);
            var detail = string.Join("\n", new[] { pushRaw.Stdout.Trim(), CleanGitStderr(pushRaw.Stderr) }.Where(s => s.Length > 0));
            if (pushRaw.ExitCode != 0)
                return new GitRemoteResult { IsError = true, Operation = "push", Error = detail.Length > 0 ? detail : $"git push exited with code {pushRaw.ExitCode}" };

            return new GitRemoteResult { IsError = false, Operation = "push", Detail = detail };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git push failed (remote={Remote})", remoteName);
            return new GitRemoteResult { IsError = true, Operation = "push", Error = $"Git push failed: {ex.Message}" };
        }
    }

    public async Task<GitRemoteResult> FetchAsync(
        string gitRoot, string remoteName, CancellationToken cancellationToken)
    {
        try
        {
            var fetchRaw = await RunGitAsync(gitRoot, ["fetch", remoteName], cancellationToken);
            var detail = string.Join("\n", new[] { fetchRaw.Stdout.Trim(), CleanGitStderr(fetchRaw.Stderr) }.Where(s => s.Length > 0));
            if (fetchRaw.ExitCode != 0)
                return new GitRemoteResult { IsError = true, Operation = "fetch", Error = detail.Length > 0 ? detail : $"git fetch exited with code {fetchRaw.ExitCode}" };

            return new GitRemoteResult { IsError = false, Operation = "fetch", Detail = detail };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git fetch failed (remote={Remote})", remoteName);
            return new GitRemoteResult { IsError = true, Operation = "fetch", Error = $"Git fetch failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Pulls the current branch from <paramref name="remoteName"/> - a plain merge (git's default)
    /// unless <paramref name="rebase"/> is set. No <c>--force</c> exposed -> pull never force-rewrites
    /// remote-tracking state.
    /// </summary>
    public async Task<GitRemoteResult> PullAsync(
        string gitRoot, string remoteName, bool rebase, CancellationToken cancellationToken)
    {
        try
        {
            // Always state the strategy: with neither --rebase nor --no-rebase, git 2.27+ refuses a
            // pull of divergent branches ("Need to specify how to reconcile divergent branches")
            // unless pull.rebase/pull.ff is configured, which the caller has no way to see or fix.
            var args = new List<string> { "pull", rebase ? "--rebase" : "--no-rebase", remoteName };
            var pullRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);
            var detail = string.Join("\n", new[] { pullRaw.Stdout.Trim(), CleanGitStderr(pullRaw.Stderr) }.Where(s => s.Length > 0));
            if (pullRaw.ExitCode != 0)
                return await AppendInProgressAsync(new GitRemoteResult { IsError = true, Operation = "pull", Error = detail.Length > 0 ? detail : $"git pull exited with code {pullRaw.ExitCode}" }, gitRoot, cancellationToken);

            return new GitRemoteResult { IsError = false, Operation = "pull", Detail = detail };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git pull failed (remote={Remote})", remoteName);
            return new GitRemoteResult { IsError = true, Operation = "pull", Error = $"Git pull failed: {ex.Message}" };
        }
    }
}
