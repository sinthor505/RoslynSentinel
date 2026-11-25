using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Pins the rendered text of Git hint sites that no scenario test reaches ("renderer-pinned only", plan Step 2f):
/// each expected string is the original engine expression copied before the site moved onto a hint key, so a drift
/// in <see cref="GitHintText"/> or in the engine prose around an Inline token shows here. The engine-to-facts wiring is
/// verified by reading the diff, not by these tests.
/// </summary>
[TestFixture]
[Category("GitHint")]
public class GitHintRenderLiteralTests
{
    [Test]
    public void StashRestore_CountMismatchNote_RendersOriginalText()
    {
        // Original: $" Expected {expected} stash entries afterwards but found {entriesAfter}; check {call}."
        const int expected = 1;
        const int entriesAfter = 2;
        var original = $" Expected {expected} stash entries afterwards but found {entriesAfter}; check {ToolCallHint.Call("GitRemote", ("operation", "stash"), ("action", "list"))}.";
        var result = new GitStashResult
        {
            Action = "pop",
            Note = $" Expected {expected} stash entries afterwards but found {entriesAfter}; check {GitHintTokens.Inline(GitHintKey.StashListCall)}.",
            Hints = [new GitHint(GitHintKey.StashListCall)]
        };

        var applied = GitHintText.Apply(result);

        Assert.That(applied.Note, Is.EqualTo(original));
    }

    [Test]
    public void StashRestore_UnverifiedCleanError_RendersOriginalText()
    {
        // Original: $"git stash {action} failed: {stderr}. The tree could not be verified clean afterwards" + ""
        //   + $"; stash entries: {entriesNow} (was {entriesBefore}). The stash entry was kept. Run {call} before editing."
        const string action = "apply";
        const string stderr = "boom";
        const int entriesNow = 2;
        const int entriesBefore = 3;
        var original = $"git stash {action} failed: {stderr}. The tree could not be verified clean afterwards" +
                       "" +
                       $"; stash entries: {entriesNow} (was {entriesBefore}). The stash entry was kept. Run {ToolCallHint.Call("Git", ("operation", "status"))} before editing.";
        var result = new GitStashResult
        {
            Action = action,
            IsError = true,
            Error = $"git stash {action} failed: {stderr}. The tree could not be verified clean afterwards" +
                    "" +
                    $"; stash entries: {entriesNow} (was {entriesBefore}). The stash entry was kept. Run {GitHintTokens.Inline(GitHintKey.GitStatusCall)} before editing.",
            Hints = [new GitHint(GitHintKey.GitStatusCall)]
        };

        var applied = GitHintText.Apply(result);

        Assert.That(applied.Error, Is.EqualTo(original));
    }

    [Test]
    public void StashRollbackConflict_UnverifiedRollback_RendersOriginalErrorAndDetail()
    {
        // Original Error: $"Applying stash@{{{stashIndex}}} conflicts, and the rollback could not be verified ({string.Join("; ", failed)}). "
        //   + $"The stash entry was kept. Run {call} before editing."
        // Original ErrorDetail: $"Call {status call} to see the tree; {stash list call} shows the stash entries."
        const int stashIndex = 0;
        var failed = new List<string> { "git reset --merge exited 1", "stash entry count is 2, expected 3" };
        var originalError = $"Applying stash@{{{stashIndex}}} conflicts, and the rollback could not be verified ({string.Join("; ", failed)}). " +
                            $"The stash entry was kept. Run {ToolCallHint.Call("Git", ("operation", "status"))} before editing.";
        var originalDetail = $"Call {ToolCallHint.Call("Git", ("operation", "status"))} to see the tree; {ToolCallHint.Call("GitRemote", ("operation", "stash"), ("action", "list"))} shows the stash entries.";
        var result = new GitStashResult
        {
            Action = "apply",
            IsError = true,
            Error = $"Applying stash@{{{stashIndex}}} conflicts, and the rollback could not be verified ({string.Join("; ", failed)}). " +
                    $"The stash entry was kept. Run {GitHintTokens.Inline(GitHintKey.GitStatusCall)} before editing.",
            Hints = [new GitHint(GitHintKey.StashRollbackUnverified), new GitHint(GitHintKey.GitStatusCall)]
        };

        var applied = GitHintText.Apply(result);

        Assert.Multiple(() =>
        {
            Assert.That(applied.Error, Is.EqualTo(originalError));
            Assert.That(applied.ErrorDetail, Is.EqualTo(originalDetail));
            Assert.That(GitHintText.Render(GitHintKey.StashRollbackUnverified, null), Is.EqualTo(originalDetail));
        });
    }

    [Test]
    public void WorktreeRemove_RemoveFailedError_RendersOriginalText()
    {
        // Original (stillThere false): $"git worktree remove failed: {stderr}. " + $"Check its state with {call}."
        const string stderr = "boom";
        var original = $"git worktree remove failed: {stderr}. " +
                       $"Check its state with {ToolCallHint.Call("GitRemote", ("operation", "worktree"), ("action", "list"))}.";
        var result = new GitWorktreeResult
        {
            Action = "remove",
            IsError = true,
            Error = $"git worktree remove failed: {stderr}. " + $"Check its state with {GitHintTokens.Inline(GitHintKey.WorktreeListCall)}.",
            Hints = [new GitHint(GitHintKey.WorktreeListCall)]
        };

        var applied = GitHintText.Apply(result);

        Assert.That(applied.Error, Is.EqualTo(original));
    }

    [Test]
    public void LoadDiff_GitDiffFailedDetail_RendersOriginalText()
    {
        // Original ErrorDetail (LoadDiffForPathAsync, git diff exit code not 0):
        //   $"Call {ToolCallHint.Call("Git", ("operation", "status"))} to check the repository state, then retry."
        // Original Error: $"git diff failed: {CleanGitStderr(raw.Stderr)}. Nothing was changed." (engine text, unchanged)
        var original = $"Call {ToolCallHint.Call("Git", ("operation", "status"))} to check the repository state, then retry.";
        var result = new GitHunksResult
        {
            Path = "a.txt",
            IsError = true,
            Error = "git diff failed: boom. Nothing was changed.",
            Hints = [new GitHint(GitHintKey.GitDiffFailedRetry)]
        };

        var applied = GitHintText.Apply(result);

        Assert.Multiple(() =>
        {
            Assert.That(applied.ErrorDetail, Is.EqualTo(original));
            Assert.That(GitHintText.Render(GitHintKey.GitDiffFailedRetry, null), Is.EqualTo(original));
        });
    }

    [Test]
    public void StageHunks_ApplyRejectedDetail_RendersOriginalText()
    {
        // Original ErrorDetail (StageHunksAsync, git apply exit code not 0):
        //   $"Re-run {ToolCallHint.Call("Git", ("operation", "hunks"), ("paths", "<path>"))} and pick again, or stage the whole file with {ToolCallHint.Call("Git", ("operation", "stage"), ("paths", "<path>"))}."
        var original = $"Re-run {ToolCallHint.Call("Git", ("operation", "hunks"), ("paths", "<path>"))} and pick again, or stage the whole file with {ToolCallHint.Call("Git", ("operation", "stage"), ("paths", "<path>"))}.";
        var result = new GitStageHunksResult
        {
            Path = "a.txt",
            IsError = true,
            Error = "git apply rejected the selected hunks: boom. Nothing was staged.",
            Hints = [new GitHint(GitHintKey.HunkApplyRejectedNextStep)]
        };

        var applied = GitHintText.Apply(result);

        Assert.Multiple(() =>
        {
            Assert.That(applied.ErrorDetail, Is.EqualTo(original));
            Assert.That(GitHintText.Render(GitHintKey.HunkApplyRejectedNextStep, null), Is.EqualTo(original));
        });
    }
}
