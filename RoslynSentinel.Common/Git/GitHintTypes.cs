namespace RoslynSentinel.Common;

/// <summary>
/// Names a tool-side hint text (a next-step sentence or call fragment that names other tools). The Git engine sets
/// keys on a result; the tool boundary renders them. Members mirror the entries of the Git error-detail table; the
/// slice that moves a further hint site onto a key adds that site's member.
/// </summary>
internal enum GitHintKey
{
    None = 0,
    PathNotFound,
    IgnoredPath,
    NothingToCommit,
    OperationInProgress,
    RefRequiredReset,
    RefRequiredRevert,
    RefRequiredCheckout,
    RefRequiredStash,
    RefRequiredTag,
    RefRequiredWorktree,
    AlreadyExists,
    TargetNotFound,
    InvalidName,
    WorkingTreeDirty,
    NoChanges,
    StashConflict,
    Refused,
    HunkUnsupported,
    HunkSelection,
    IndexLocked,
    HookRejected,
    HunkStale,

    // Step 6a-6d: inline error texts (ErrorDetail = a whole sentence, Inline = a call fragment inside engine text).
    /// <summary>ErrorDetail "Pass action, e.g. {call}."; facts: Operation.</summary>
    PassActionExample,

    /// <summary>ErrorDetail: valid config actions and the example call.</summary>
    ConfigActionUsage,

    /// <summary>ErrorDetail: config set needs configValue, with the example call.</summary>
    ConfigValueRequired,

    /// <summary>Inline: the tag delete call; facts: Name.</summary>
    TagDeleteCall,

    /// <summary>Inline: the stash list call.</summary>
    StashListCall,

    /// <summary>Inline: the Git status call.</summary>
    GitStatusCall,

    /// <summary>ErrorDetail: check the tree and the stash entries after an unverified rollback.</summary>
    StashRollbackUnverified,

    /// <summary>ErrorDetail: worktree add for a missing branch (createBranch or list branches).</summary>
    WorktreeBranchNotFoundOptions,

    /// <summary>Inline: the worktree list call.</summary>
    WorktreeListCall,

    /// <summary>ErrorDetail: only linked worktrees can be removed; the list call shows IsMain.</summary>
    WorktreeOnlyLinkedRemovable,

    /// <summary>ErrorDetail: load a solution in a different worktree, then remove the current one.</summary>
    WorktreeRemoveCurrentSwitch,

    /// <summary>Inline: the worktree remove call with discardUncommittedChanges; facts: Path.</summary>
    WorktreeRemoveDiscardCall,

    // Step 6e-6h: hunk, stage, in-progress, commit and checkout texts.
    /// <summary>Inline: the staged-diff call with a placeholder paths value.</summary>
    GitDiffStagedAnyPathsCall,

    /// <summary>ErrorDetail: check the repository state with the status call, then retry (git diff failed).</summary>
    GitDiffFailedRetry,

    /// <summary>Inline: the stage call with a placeholder paths value.</summary>
    GitStageAnyPathsCall,

    /// <summary>Inline: the hunks call with no paths.</summary>
    GitHunksCall,

    /// <summary>Inline: the hunks call for one path; facts: Path.</summary>
    GitHunksPathCall,

    /// <summary>Inline: the stage call for one path; facts: Path.</summary>
    GitStagePathCall,

    /// <summary>ErrorDetail: after a rejected git apply, re-run the hunks call or stage the whole file.</summary>
    HunkApplyRejectedNextStep,

    /// <summary>Inline: the abort call.</summary>
    GitAbortCall,

    /// <summary>Inline: the unstage call.</summary>
    GitUnstageCall,

    /// <summary>Inline: the GitRemote checkout call.</summary>
    GitRemoteCheckoutCall,

    // Step 6g Warning extension and Step 7a-7b: notes and the stage-hunks warning.
    /// <summary>Inline: the staged-diff call with no paths (used in the stage-hunks Warning).</summary>
    GitDiffStagedCall,

    /// <summary>Inline: the tag create call that restores a deleted tag; facts: Name, Hash.</summary>
    TagCreateUndoCall,

    /// <summary>NotePrefix: tracked files changed on disk after a stash push, pop or apply; call LoadSolution with forceReload before editing C#.</summary>
    StashDiskNote,

    /// <summary>NotePrefix: after worktree add, how to work in the new worktree through LoadSolution; facts: Path. The engine Note is empty.</summary>
    WorktreeAddNote,

    /// <summary>Inline: the GitRemote branch call with deleteBranch.</summary>
    GitRemoteBranchDeleteCall
}

/// <summary>Where the rendered text of a <see cref="GitHintKey"/> goes on a result.</summary>
internal enum GitHintSlot
{
    None = 0,

    /// <summary>Sets ErrorDetail from the key: a whole tool-side sentence.</summary>
    ErrorDetail,

    /// <summary>Error is replaced by the rendered text (the engine left Error empty).</summary>
    ErrorReplace,

    /// <summary>A whole tool-side sentence placed before the engine Note.</summary>
    NotePrefix,

    /// <summary>A whole tool-side sentence placed after the engine Note.</summary>
    NoteSuffix,

    /// <summary>A placeholder token (see <see cref="GitHintTokens"/>) replaced by the rendered call fragment inside engine Error, ErrorDetail or Note text.</summary>
    Inline
}

/// <summary>The call-specific facts a hint text may need. Every property is optional; a key reads only the ones it needs.</summary>
internal sealed record GitHintFacts
{
    public string? Name { get; init; }
    public string? Hash { get; init; }
    public string? Path { get; init; }
    public string? Branch { get; init; }
    public string? Operation { get; init; }
    public string? Action { get; init; }
    public string? LockPath { get; init; }
    public string? SpanText { get; init; }
    public int? Count { get; init; }
    public int? CountBefore { get; init; }
}

/// <summary>A hint the engine attached to a result: which text to render and the facts it needs.</summary>
internal sealed record GitHint(GitHintKey Key, GitHintFacts? Facts = null);

/// <summary>Builds and scans the inline placeholder token <c>[[hint:Key]]</c> that an engine text carries for an <see cref="GitHintSlot.Inline"/> hint.</summary>
internal static class GitHintTokens
{
    private const string Prefix = "[[hint:";
    private const string Suffix = "]]";

    /// <summary>The placeholder token for <paramref name="key"/>.</summary>
    public static string Inline(GitHintKey key) => Prefix + key + Suffix;

    /// <summary>
    /// Reads a token that begins exactly at <paramref name="start"/> in <paramref name="text"/>. Returns false (key None,
    /// length 0) when there is no well-formed token with a known, non-None key at that position.
    /// </summary>
    public static bool TryRead(string text, int start, out GitHintKey key, out int length)
    {
        key = GitHintKey.None;
        length = 0;
        if (start < 0 || start >= text.Length || string.CompareOrdinal(text, start, Prefix, 0, Prefix.Length) != 0)
        {
            return false;
        }

        var nameStart = start + Prefix.Length;
        var end = text.IndexOf(Suffix, nameStart, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        var name = text.Substring(nameStart, end - nameStart);
        if (name.Length == 0 || !char.IsLetter(name[0])
            || !Enum.TryParse(name, ignoreCase: false, out GitHintKey parsed)
            || parsed == GitHintKey.None
            || !Enum.IsDefined(parsed))
        {
            return false;
        }

        key = parsed;
        length = end + Suffix.Length - start;
        return true;
    }
}
