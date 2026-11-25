using RoslynSentinel.Common;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Renders the tool-side hint text (sentences and call fragments that name other tools) for the keys the Git engine
/// attaches to a result, and applies them to the result at the tool boundary.
/// </summary>
internal static class GitHintText
{
    /// <summary>The slot a key's rendered text goes into.</summary>
    internal static GitHintSlot SlotOf(GitHintKey key)
    {
        return key switch
        {
            GitHintKey.None => GitHintSlot.None,
            GitHintKey.PathNotFound
                or GitHintKey.IgnoredPath
                or GitHintKey.NothingToCommit
                or GitHintKey.OperationInProgress
                or GitHintKey.RefRequiredReset
                or GitHintKey.RefRequiredRevert
                or GitHintKey.RefRequiredCheckout
                or GitHintKey.RefRequiredStash
                or GitHintKey.RefRequiredTag
                or GitHintKey.RefRequiredWorktree
                or GitHintKey.AlreadyExists
                or GitHintKey.TargetNotFound
                or GitHintKey.InvalidName
                or GitHintKey.WorkingTreeDirty
                or GitHintKey.NoChanges
                or GitHintKey.StashConflict
                or GitHintKey.Refused
                or GitHintKey.HunkUnsupported
                or GitHintKey.HunkSelection
                or GitHintKey.IndexLocked
                or GitHintKey.HookRejected
                or GitHintKey.HunkStale
                or GitHintKey.PassActionExample
                or GitHintKey.ConfigActionUsage
                or GitHintKey.ConfigValueRequired
                or GitHintKey.StashRollbackUnverified
                or GitHintKey.WorktreeBranchNotFoundOptions
                or GitHintKey.WorktreeOnlyLinkedRemovable
                or GitHintKey.WorktreeRemoveCurrentSwitch
                or GitHintKey.GitDiffFailedRetry
                or GitHintKey.HunkApplyRejectedNextStep => GitHintSlot.ErrorDetail,
            GitHintKey.TagDeleteCall
                or GitHintKey.StashListCall
                or GitHintKey.GitStatusCall
                or GitHintKey.WorktreeListCall
                or GitHintKey.WorktreeRemoveDiscardCall
                or GitHintKey.GitDiffStagedAnyPathsCall
                or GitHintKey.GitStageAnyPathsCall
                or GitHintKey.GitHunksCall
                or GitHintKey.GitHunksPathCall
                or GitHintKey.GitStagePathCall
                or GitHintKey.GitAbortCall
                or GitHintKey.GitUnstageCall
                or GitHintKey.GitRemoteCheckoutCall
                or GitHintKey.GitDiffStagedCall
                or GitHintKey.TagCreateUndoCall
                or GitHintKey.GitRemoteBranchDeleteCall => GitHintSlot.Inline,
            GitHintKey.StashDiskNote
                or GitHintKey.WorktreeAddNote => GitHintSlot.NotePrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No slot is defined for this hint key.")
        };
    }

    /// <summary>The rendered tool-side text for a key and its call facts.</summary>
    internal static string Render(GitHintKey key, GitHintFacts? facts)
    {
        return key switch
        {
            GitHintKey.PathNotFound => GitErrorDetails.PathNotFound,
            GitHintKey.IgnoredPath => GitErrorDetails.IgnoredPath,
            GitHintKey.NothingToCommit => GitErrorDetails.NothingToCommit,
            GitHintKey.OperationInProgress => GitErrorDetails.OperationInProgress,
            GitHintKey.RefRequiredReset => GitErrorDetails.RefRequiredReset,
            GitHintKey.RefRequiredRevert => GitErrorDetails.RefRequiredRevert,
            GitHintKey.RefRequiredCheckout => GitErrorDetails.RefRequiredCheckout,
            GitHintKey.RefRequiredStash => GitErrorDetails.RefRequiredStash,
            GitHintKey.RefRequiredTag => GitErrorDetails.RefRequiredTag,
            GitHintKey.RefRequiredWorktree => GitErrorDetails.RefRequiredWorktree,
            GitHintKey.AlreadyExists => GitErrorDetails.AlreadyExists,
            GitHintKey.TargetNotFound => GitErrorDetails.TargetNotFound,
            GitHintKey.InvalidName => GitErrorDetails.InvalidName,
            GitHintKey.WorkingTreeDirty => GitErrorDetails.WorkingTreeDirty,
            GitHintKey.NoChanges => GitErrorDetails.NoChanges,
            GitHintKey.StashConflict => GitErrorDetails.StashConflict,
            GitHintKey.Refused => GitErrorDetails.Refused,
            GitHintKey.HunkUnsupported => GitErrorDetails.HunkUnsupported,
            GitHintKey.HunkSelection => GitErrorDetails.HunkSelection,
            GitHintKey.IndexLocked => GitErrorDetails.IndexLocked(facts?.LockPath ?? string.Empty),
            GitHintKey.HookRejected => GitErrorDetails.HookRejected,
            GitHintKey.HunkStale => GitErrorDetails.HunkStale,
            GitHintKey.PassActionExample => $"Pass action, e.g. {ToolCallHint.Call("GitRemote", ("operation", facts?.Operation ?? string.Empty), ("action", "list"))}.",
            GitHintKey.ConfigActionUsage => $"Valid actions are get, set and unset; get takes configKey, set takes configKey and configValue, unset takes configKey. For example {ToolCallHint.Call("GitRemote", ("operation", "config"), ("action", "get"), ("configKey", "user.name"))}.",
            GitHintKey.ConfigValueRequired => $"Pass configValue with action=set; an empty string is a valid value. For example {ToolCallHint.Call("GitRemote", ("operation", "config"), ("action", "set"), ("configKey", "user.name"), ("configValue", "Ada"))}.",
            GitHintKey.TagDeleteCall => ToolCallHint.Call("GitRemote", ("operation", "tag"), ("action", "delete"), ("tagName", facts?.Name ?? string.Empty)),
            GitHintKey.StashListCall => ToolCallHint.Call("GitRemote", ("operation", "stash"), ("action", "list")),
            GitHintKey.GitStatusCall => ToolCallHint.Call("Git", ("operation", "status")),
            GitHintKey.StashRollbackUnverified => $"Call {ToolCallHint.Call("Git", ("operation", "status"))} to see the tree; {ToolCallHint.Call("GitRemote", ("operation", "stash"), ("action", "list"))} shows the stash entries.",
            GitHintKey.WorktreeBranchNotFoundOptions => $"Pass createBranch=true to create it (optionally with startPoint), or list the existing branches with {ToolCallHint.Call("GitRemote", ("operation", "branch"))}.",
            GitHintKey.WorktreeListCall => ToolCallHint.Call("GitRemote", ("operation", "worktree"), ("action", "list")),
            GitHintKey.WorktreeOnlyLinkedRemovable => $"Only linked worktrees can be removed; {ToolCallHint.Call("GitRemote", ("operation", "worktree"), ("action", "list"))} shows which entry has IsMain false.",
            GitHintKey.WorktreeRemoveCurrentSwitch => $"Call {ToolCallHint.Call("LoadSolution", ("solutionPath", "<path to .slnx>"))} on a solution in a different worktree, then remove this one from there.",
            GitHintKey.WorktreeRemoveDiscardCall => ToolCallHint.Call("GitRemote", ("operation", "worktree"), ("action", "remove"), ("worktreePath", facts?.Path ?? string.Empty), ("discardUncommittedChanges", "true")),
            GitHintKey.GitDiffStagedAnyPathsCall => ToolCallHint.Call("Git", ("operation", "diff"), ("ref", "staged"), ("paths", "...")),
            GitHintKey.GitDiffFailedRetry => $"Call {ToolCallHint.Call("Git", ("operation", "status"))} to check the repository state, then retry.",
            GitHintKey.GitStageAnyPathsCall => ToolCallHint.Call("Git", ("operation", "stage"), ("paths", "...")),
            GitHintKey.GitHunksCall => ToolCallHint.Call("Git", ("operation", "hunks")),
            GitHintKey.GitHunksPathCall => ToolCallHint.Call("Git", ("operation", "hunks"), ("paths", facts?.Path ?? string.Empty)),
            GitHintKey.GitStagePathCall => ToolCallHint.Call("Git", ("operation", "stage"), ("paths", facts?.Path ?? string.Empty)),
            GitHintKey.HunkApplyRejectedNextStep => $"Re-run {ToolCallHint.Call("Git", ("operation", "hunks"), ("paths", "<path>"))} and pick again, or stage the whole file with {ToolCallHint.Call("Git", ("operation", "stage"), ("paths", "<path>"))}.",
            GitHintKey.GitAbortCall => ToolCallHint.Call("Git", ("operation", "abort")),
            GitHintKey.GitUnstageCall => ToolCallHint.Call("Git", ("operation", "unstage")),
            GitHintKey.GitRemoteCheckoutCall => ToolCallHint.Call("GitRemote", ("operation", "checkout")),
            GitHintKey.GitDiffStagedCall => ToolCallHint.Call("Git", ("operation", "diff"), ("ref", "staged")),
            GitHintKey.TagCreateUndoCall => ToolCallHint.Call("GitRemote", ("operation", "tag"), ("action", "create"), ("tagName", facts?.Name ?? string.Empty), ("ref", facts?.Hash ?? string.Empty)),
            GitHintKey.StashDiskNote => $"Tracked files changed on disk; call {ToolCallHint.Call("LoadSolution", ("forceReload", "true"))} before editing C#.",
            GitHintKey.WorktreeAddNote => $"Use {ToolCallHint.Call("LoadSolution", ("solutionPath", "<path to .slnx>"))} on a .slnx inside {facts?.Path ?? string.Empty} to work in it; mutating Git operations always target the loaded solution's repo.",
            GitHintKey.GitRemoteBranchDeleteCall => ToolCallHint.Call("GitRemote", ("operation", "branch"), ("deleteBranch", "true")),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No text is defined for this hint key.")
        };
    }

    /// <summary>Renders every hint on <paramref name="result"/> into its slot and returns the same result.</summary>
    internal static T Apply<T>(T result) where T : GitResult
    {
        var hints = result.Hints;
        if (hints is null || hints.Count == 0)
        {
            return result;
        }

        Dictionary<GitHintKey, GitHintFacts?>? inlineFacts = null;
        foreach (var hint in hints)
        {
            switch (SlotOf(hint.Key))
            {
                case GitHintSlot.ErrorDetail:
                    result.ErrorDetail = Render(hint.Key, hint.Facts);
                    break;
                case GitHintSlot.ErrorReplace:
                    result.Error = Render(hint.Key, hint.Facts);
                    break;
                case GitHintSlot.NotePrefix:
                    // A result type without a Note has nothing to join onto; the hint is dropped.
                    if (TryGetNote(result, out var prefixedNote))
                    {
                        var sentence = Render(hint.Key, hint.Facts);
                        SetNote(result, string.IsNullOrEmpty(prefixedNote) ? sentence : sentence + " " + prefixedNote);
                    }
                    break;
                case GitHintSlot.NoteSuffix:
                    if (TryGetNote(result, out var suffixedNote))
                    {
                        var sentence = Render(hint.Key, hint.Facts);
                        SetNote(result, string.IsNullOrEmpty(suffixedNote) ? sentence : suffixedNote + " " + sentence);
                    }
                    break;
                case GitHintSlot.Inline:
                    inlineFacts ??= new Dictionary<GitHintKey, GitHintFacts?>();
                    inlineFacts.TryAdd(hint.Key, hint.Facts); // the first hint with a key supplies its facts
                    break;
                default:
                    break;
            }
        }

        if (inlineFacts is not null)
        {
            result.Error = ReplaceTokens(result.Error, inlineFacts);
            result.ErrorDetail = ReplaceTokens(result.ErrorDetail, inlineFacts);
            if (TryGetNote(result, out var note))
            {
                SetNote(result, ReplaceTokens(note, inlineFacts));
            }

            // GitStageHunksResult.Warning carries a tool call too; it is the only Warning that does (Inline slot only).
            if (result is GitStageHunksResult stageResult)
            {
                stageResult.Warning = ReplaceTokens(stageResult.Warning, inlineFacts);
            }
        }

        return result;
    }

    /// <summary>Replaces every well-formed token whose key has an entry in <paramref name="facts"/>; any other text, including a token with no matching hint, stays as is.</summary>
    private static string? ReplaceTokens(string? text, Dictionary<GitHintKey, GitHintFacts?> facts)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var copiedUpTo = 0;
        var index = text.IndexOf('[');
        while (index >= 0)
        {
            if (GitHintTokens.TryRead(text, index, out var key, out var length) && facts.TryGetValue(key, out var keyFacts))
            {
                builder.Append(text, copiedUpTo, index - copiedUpTo).Append(Render(key, keyFacts));
                copiedUpTo = index + length;
                index = text.IndexOf('[', copiedUpTo);
            }
            else
            {
                index = text.IndexOf('[', index + 1);
            }
        }

        return builder.Append(text, copiedUpTo, text.Length - copiedUpTo).ToString();
    }

    /// <summary>Reads the Note of the seven result types that have one; false for every other type.</summary>
    private static bool TryGetNote(GitResult result, out string? note)
    {
        switch (result)
        {
            case GitCheckoutResult r:
                note = r.Note;
                return true;
            case GitTagResult r:
                note = r.Note;
                return true;
            case GitConfigResult r:
                note = r.Note;
                return true;
            case GitStashResult r:
                note = r.Note;
                return true;
            case GitHunksResult r:
                note = r.Note;
                return true;
            case GitStageHunksResult r:
                note = r.Note;
                return true;
            case GitWorktreeResult r:
                note = r.Note;
                return true;
            default:
                note = null;
                return false;
        }
    }

    /// <summary>Writes the Note of the seven result types that have one; a no-op for every other type.</summary>
    private static void SetNote(GitResult result, string? note)
    {
        switch (result)
        {
            case GitCheckoutResult r:
                r.Note = note;
                break;
            case GitTagResult r:
                r.Note = note;
                break;
            case GitConfigResult r:
                r.Note = note;
                break;
            case GitStashResult r:
                r.Note = note;
                break;
            case GitHunksResult r:
                r.Note = note;
                break;
            case GitStageHunksResult r:
                r.Note = note;
                break;
            case GitWorktreeResult r:
                r.Note = note;
                break;
            default:
                break;
        }
    }

    /// <summary>The tool-side advice appended to the engine's factual text for a root-lookup failure.</summary>
    internal static string RenderRootFailure(GitRootFailure failure)
    {
        return failure == GitRootFailure.NoRepositoryFound
            ? "If the repository is elsewhere, call " + ToolCallHint.Call("LoadSolution", ("solutionPath", "<path to .slnx>")) + " with a solution inside it first."
            : string.Empty;
    }

    /// <summary>The engine's factual root-lookup error with the rendered advice (if any) appended after one separating space.</summary>
    internal static string JoinRootFailure(string error, GitRootFailure failure)
    {
        var advice = RenderRootFailure(failure);
        return advice.Length == 0 ? error : error + " " + advice;
    }
}

/// <summary>Next-step text paired with <see cref="GitErrorCodes"/> in ResultError.Detail; the text home <see cref="GitHintText.Render"/> delegates to.</summary>
internal static class GitErrorDetails
{
    internal static string PathNotFound => $"Paths must be repo-relative and inside the repository root. Call {ToolCallHint.Call("Git", ("operation", "status"))} to list changed and untracked paths, then retry with exact names.";
    internal const string IgnoredPath = "Remove the ignored entries from paths, or un-ignore them in .gitignore, then retry. This tool never force-adds ignored files.";
    internal static string NothingToCommit => $"Stage changes first with {ToolCallHint.Call("Git", ("operation", "stage"), ("paths", "..."))}, or pass paths/scope on the commit call. Call {ToolCallHint.Call("Git", ("operation", "status"))} to see what changed.";
    internal static string OperationInProgress => $"Call {ToolCallHint.Call("Git", ("operation", "abort"))} to back out of the in-progress operation, or resolve the conflicts in the working tree and commit.";
    internal static string RefRequiredReset => $"Pass ref, e.g. ref: \"HEAD~1\" to undo the last commit with the working tree kept, or call {ToolCallHint.Call("Git", ("operation", "unstage"))} to unstage without moving HEAD.";
    internal static string RefRequiredRevert => $"Pass ref, e.g. a hash taken from {ToolCallHint.Call("Git", ("operation", "log"))}.";
    internal static string RefRequiredCheckout => $"Pass branchName, with createBranch=true if it does not exist yet. {ToolCallHint.Call("GitRemote", ("operation", "branch"))} lists existing branches.";
    internal static string RefRequiredStash => $"Pass stashIndex (0 = newest). {ToolCallHint.Call("GitRemote", ("operation", "stash"), ("action", "list"))} shows every entry.";
    internal static string RefRequiredTag => $"Pass tagName, e.g. {ToolCallHint.Call("GitRemote", ("operation", "tag"), ("action", "create"), ("tagName", "v1.0.0"))}.";
    internal const string RefRequiredWorktree = "Pass worktreePath (absolute, outside the repo) and, for add, branchName.";
    internal static string AlreadyExists => $"Use a different name or path, or inspect the existing entry first with {ToolCallHint.Call("GitRemote", ("operation", "tag"), ("action", "list"))}, {ToolCallHint.Call("GitRemote", ("operation", "worktree"), ("action", "list"))} or {ToolCallHint.Call("GitRemote", ("operation", "branch"))}. This tool never overwrites an existing tag, branch or worktree.";
    internal static string TargetNotFound => $"Check the exact name with {ToolCallHint.Call("GitRemote", ("operation", "tag"), ("action", "list"))}, {ToolCallHint.Call("GitRemote", ("operation", "stash"), ("action", "list"))}, {ToolCallHint.Call("GitRemote", ("operation", "worktree"), ("action", "list"))} or {ToolCallHint.Call("GitRemote", ("operation", "branch"))}, then retry.";
    internal const string InvalidName = "Use a name that git accepts: no spaces, no '..', '~', '^', ':' or trailing '.lock'. Then retry.";
    internal static string WorkingTreeDirty => $"Commit them ({ToolCallHint.Call("Git", ("operation", "commit"))}) or stash them ({ToolCallHint.Call("GitRemote", ("operation", "stash"), ("action", "push"))}) first; applying onto a dirty tree can mix the two sets of changes. {ToolCallHint.Call("Git", ("operation", "status"))} lists the changed paths.";
    internal static string NoChanges => $"Nothing to act on. Call {ToolCallHint.Call("Git", ("operation", "status"))} to see what changed, then retry with the paths that have changes.";
    internal static string StashConflict => $"Check out the commit or branch the stash was created on ({ToolCallHint.Call("GitRemote", ("operation", "checkout"))}), then apply again; {ToolCallHint.Call("GitRemote", ("operation", "stash"), ("action", "list"))} shows where each entry was made.";
    internal const string Refused = "Take the safe alternative the message names, then retry. Nothing was changed.";
    internal static string HunkUnsupported => $"Stage the whole file with {ToolCallHint.Call("Git", ("operation", "stage"), ("paths", "<path>"))} instead, or use a file kind that has text hunks.";
    internal static string HunkSelection => $"Call {ToolCallHint.Call("Git", ("operation", "hunks"), ("paths", "<path>"))} and pass hunkIds or lineRange exactly as it lists them.";
    internal static string IndexLocked(string lockPath) => $"Another git process holds {lockPath}. Wait for it to finish and retry the commit; if no git process is running, the lock is stale and deleting that file clears it.";
    internal const string HookRejected = "The text above ends with the hook's own output. Fix what the hook reports and commit again; the hooks live in .git/hooks, or in the directory named by core.hooksPath.";
    internal static string HunkStale => $"Re-run {ToolCallHint.Call("Git", ("operation", "hunks"), ("paths", "<path>"))} and pick again with the new hunkFingerprint. Nothing was staged.";
}
