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
                or GitHintKey.ActionRequired
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
            GitHintKey.ActionRequired => GitErrorDetails.ActionRequired,
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
