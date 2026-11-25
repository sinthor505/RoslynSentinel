namespace RoslynSentinel.Tools.Basic;

/// <summary>Specific ResultError.ErrorCode values for failed GitTool results. GitError stays the fallback.</summary>
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
    /// <summary>A ref (reset/revert ref, checkout branchName) was required and not supplied.</summary>
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
    /// <summary>git commit exited 1 and a blocking hook (pre-commit, prepare-commit-msg, commit-msg) exists, so the hook declined the commit; the hook's own output is in the error text. Not used when the index is empty and git printed its own nothing-to-commit status.</summary>
    public const string HookRejected = "GitHookRejected";
    /// <summary>git commit failed because another process holds the repository's index.lock; the error text carries git's own message.</summary>
    public const string IndexLocked = "GitIndexLocked";

    /// <summary>The missing-target kind (<see cref="MissingTargetKinds"/>) a not-found failure code names for the error-policy classifier, or null for every other code.</summary>
    public static string? MissingTargetKind(string? code) => code switch
    {
        PathNotFound => MissingTargetKinds.File,
        TargetNotFound => MissingTargetKinds.GitTarget,
        _ => null
    };

    /// <summary>Maps a GitTool failure code to the canonical status. Every GitTool code is a refusal or failure, so none maps to a non-error status; an unknown or fallback code is Failed.</summary>
    public static ResultStatus ToResultStatus(string? code) => code switch
    {
        // Not-found codes also carry a missing-target kind (MissingTargetKind) that GitTools records for the error-policy filter.
        PathNotFound or TargetNotFound => ResultStatus.NotFound,
        RefRequired or ActionRequired or InvalidName or HunkSelection or HunkStale => ResultStatus.InvalidInput,
        IgnoredPath or NothingToCommit or NoChanges or OperationInProgress or AlreadyExists or WorkingTreeDirty
            or StashConflict or Refused or HunkUnsupported => ResultStatus.NotApplicable,
        // HookRejected is NotApplicable, not Failed: the call was valid and git ran; a repository-side gate (the hook) declined it, like the other refusals above, and the caller can fix what the hook reports and retry.
        HookRejected => ResultStatus.NotApplicable,
        _ => ResultStatus.Failed
    };
}
