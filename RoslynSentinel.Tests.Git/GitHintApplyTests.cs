using RoslynSentinel.Git;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Pins <see cref="GitHintText"/>: every defined key has a slot and a text, <c>Apply</c> leaves a result without hints
/// untouched and renders ErrorDetail-slot keys into ErrorDetail only. Only ErrorDetail-slot keys exist so far
/// (<see cref="GitHintText.SlotOf"/> maps all 23 table keys there), so the ErrorReplace, NotePrefix, NoteSuffix and Inline
/// joins cannot be reached through <c>Apply</c> yet; the Inline assertion below is written over every Inline-slot key and
/// becomes live when a later step adds one. TODO cover ErrorReplace/NotePrefix/NoteSuffix/Inline joins once Steps 6-7 add keys with those slots.
/// </summary>
[TestFixture]
[Category("GitHint")]
public class GitHintApplyTests
{
    private const string LockPath = "C:/repo/.git/index.lock";

    private static IEnumerable<GitHintKey> DefinedKeys() => Enum.GetValues<GitHintKey>().Where(k => k != GitHintKey.None);

    // The 22 keys that mirror GitErrorDetails (Step 1); keys added by Steps 6-7 are not table keys.
    private static IEnumerable<GitHintKey> TableKeys() => DefinedKeys().Where(k => k <= GitHintKey.HunkStale);

    private static GitHintFacts SomeFacts() => new() { LockPath = LockPath, Name = "n", Path = "p" };

    [Test]
    public void SlotOf_EveryDefinedKeyHasASlot_AndNoneHasNoSlot()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GitHintText.SlotOf(GitHintKey.None), Is.EqualTo(GitHintSlot.None));
            foreach (var key in DefinedKeys())
            {
                Assert.That(GitHintText.SlotOf(key), Is.Not.EqualTo(GitHintSlot.None), $"{key} must map to a slot");
            }
        });
    }

    [Test]
    public void SlotOf_TableKeys_AreErrorDetail()
    {
        Assert.Multiple(() =>
        {
            foreach (var key in TableKeys())
            {
                Assert.That(GitHintText.SlotOf(key), Is.EqualTo(GitHintSlot.ErrorDetail), $"{key} is a table key");
            }
        });
    }

    [Test]
    public void SlotOf_UndefinedKey_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GitHintText.SlotOf((GitHintKey)9999));
    }

    [Test]
    public void Render_NoneAndUndefinedKey_Throw()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => GitHintText.Render(GitHintKey.None, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => GitHintText.Render((GitHintKey)9999, null));
        });
    }

    [Test]
    public void Render_EveryDefinedKey_ReturnsNonEmptyText()
    {
        Assert.Multiple(() =>
        {
            foreach (var key in DefinedKeys())
            {
                Assert.That(GitHintText.Render(key, SomeFacts()), Is.Not.Empty, $"{key} must render text");
            }
        });
    }

    [Test]
    public void Render_TableKeys_EqualTheMatchingGitErrorDetailsText()
    {
        var expected = new Dictionary<GitHintKey, string>
        {
            [GitHintKey.PathNotFound] = GitErrorDetails.PathNotFound,
            [GitHintKey.IgnoredPath] = GitErrorDetails.IgnoredPath,
            [GitHintKey.NothingToCommit] = GitErrorDetails.NothingToCommit,
            [GitHintKey.OperationInProgress] = GitErrorDetails.OperationInProgress,
            [GitHintKey.RefRequiredReset] = GitErrorDetails.RefRequiredReset,
            [GitHintKey.RefRequiredRevert] = GitErrorDetails.RefRequiredRevert,
            [GitHintKey.RefRequiredCheckout] = GitErrorDetails.RefRequiredCheckout,
            [GitHintKey.RefRequiredStash] = GitErrorDetails.RefRequiredStash,
            [GitHintKey.RefRequiredTag] = GitErrorDetails.RefRequiredTag,
            [GitHintKey.RefRequiredWorktree] = GitErrorDetails.RefRequiredWorktree,
            [GitHintKey.AlreadyExists] = GitErrorDetails.AlreadyExists,
            [GitHintKey.TargetNotFound] = GitErrorDetails.TargetNotFound,
            [GitHintKey.InvalidName] = GitErrorDetails.InvalidName,
            [GitHintKey.WorkingTreeDirty] = GitErrorDetails.WorkingTreeDirty,
            [GitHintKey.NoChanges] = GitErrorDetails.NoChanges,
            [GitHintKey.StashConflict] = GitErrorDetails.StashConflict,
            [GitHintKey.Refused] = GitErrorDetails.Refused,
            [GitHintKey.HunkUnsupported] = GitErrorDetails.HunkUnsupported,
            [GitHintKey.HunkSelection] = GitErrorDetails.HunkSelection,
            [GitHintKey.IndexLocked] = GitErrorDetails.IndexLocked(LockPath),
            [GitHintKey.HookRejected] = GitErrorDetails.HookRejected,
            [GitHintKey.HunkStale] = GitErrorDetails.HunkStale
        };

        Assert.Multiple(() =>
        {
            Assert.That(expected.Keys, Is.EquivalentTo(TableKeys()), "every table key is compared");
            foreach (var (key, text) in expected)
            {
                Assert.That(GitHintText.Render(key, SomeFacts()), Is.EqualTo(text), $"{key}");
            }
        });
    }

    [Test]
    public void Render_IndexLocked_WithoutFacts_UsesEmptyLockPath()
    {
        Assert.That(GitHintText.Render(GitHintKey.IndexLocked, null), Is.EqualTo(GitErrorDetails.IndexLocked(string.Empty)));
    }

    [Test]
    public void Apply_NullHints_ReturnsSameInstanceUnchanged()
    {
        var result = new GitTagResult { IsError = true, Error = "engine error", Note = "engine note" };

        var applied = GitHintText.Apply(result);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.SameAs(result));
            Assert.That(applied.Error, Is.EqualTo("engine error"));
            Assert.That(applied.ErrorDetail, Is.Null);
            Assert.That(applied.Note, Is.EqualTo("engine note"));
        });
    }

    [Test]
    public void Apply_EmptyHints_ReturnsSameInstanceUnchanged()
    {
        var result = new GitResult { IsError = true, Error = "engine error", Hints = new List<GitHint>() };

        var applied = GitHintText.Apply(result);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.SameAs(result));
            Assert.That(applied.Error, Is.EqualTo("engine error"));
            Assert.That(applied.ErrorDetail, Is.Null);
        });
    }

    [Test]
    public void Apply_ErrorDetailSlot_SetsErrorDetailOnly()
    {
        var result = new GitTagResult { IsError = true, Error = "engine error", Note = "engine note" };
        result.Hints = new[] { new GitHint(GitHintKey.NothingToCommit) };

        var applied = GitHintText.Apply(result);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.SameAs(result));
            Assert.That(applied.ErrorDetail, Is.EqualTo(GitErrorDetails.NothingToCommit));
            Assert.That(applied.Error, Is.EqualTo("engine error"));
            Assert.That(applied.Note, Is.EqualTo("engine note"));
        });
    }

    [Test]
    public void Apply_IndexLocked_RendersLockPathFromFacts()
    {
        var result = new GitCommitResult { IsError = true, Error = "engine error" };
        result.Hints = new[] { new GitHint(GitHintKey.IndexLocked, new GitHintFacts { LockPath = LockPath }) };

        var applied = GitHintText.Apply(result);

        Assert.That(applied.ErrorDetail, Is.EqualTo(GitErrorDetails.IndexLocked(LockPath)));
    }

    [Test]
    public void Apply_SeveralErrorDetailHints_LastInListOrderWins()
    {
        var result = new GitResult { IsError = true, Error = "engine error" };
        result.Hints = new[] { new GitHint(GitHintKey.NoChanges), new GitHint(GitHintKey.PathNotFound) };

        var applied = GitHintText.Apply(result);

        Assert.That(applied.ErrorDetail, Is.EqualTo(GitErrorDetails.PathNotFound));
    }

    [Test]
    public void Apply_InlineSlotKeys_LeaveNoTokenBehind_AndOtherSlotsLeaveTokensAlone()
    {
        Assert.Multiple(() =>
        {
            foreach (var key in DefinedKeys())
            {
                var token = GitHintTokens.Inline(key);
                var result = new GitResult { IsError = true, Error = "before " + token + " after" };
                result.Hints = new[] { new GitHint(key, SomeFacts()) };

                var applied = GitHintText.Apply(result);

                if (GitHintText.SlotOf(key) == GitHintSlot.Inline)
                {
                    Assert.That(applied.Error, Is.EqualTo("before " + GitHintText.Render(key, SomeFacts()) + " after"), $"{key} token is replaced in place");
                    Assert.That(applied.Error, Does.Not.Contain(token), $"{key} leaves no token");
                }
                else
                {
                    Assert.That(applied.Error, Is.EqualTo("before " + token + " after"), $"{key} is not an Inline key, so its token is untouched");
                }
            }
        });
    }

    [Test]
    public void Apply_InlineSlotKeys_AreReplacedInStageHunksWarning_AndLeaveNoTokenBehind()
    {
        Assert.Multiple(() =>
        {
            foreach (var key in DefinedKeys().Where(k => GitHintText.SlotOf(k) == GitHintSlot.Inline))
            {
                var token = GitHintTokens.Inline(key);
                var result = new GitStageHunksResult { Warning = "before " + token + " after" };
                result.Hints = new[] { new GitHint(key, SomeFacts()) };

                var applied = GitHintText.Apply(result);

                Assert.That(applied.Warning, Is.EqualTo("before " + GitHintText.Render(key, SomeFacts()) + " after"), $"{key} token is replaced in Warning");
                Assert.That(applied.Warning, Does.Not.Contain(token), $"{key} leaves no token in Warning");
            }
        });
    }

    [Test]
    public void RenderRootFailure_NoRepositoryFound_NamesLoadSolution()
    {
        var text = GitHintText.RenderRootFailure(GitRootFailure.NoRepositoryFound);

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.StartWith("If the repository is elsewhere, call "));
            Assert.That(text, Does.Contain("LoadSolution"));
            Assert.That(text, Does.EndWith(" with a solution inside it first."));
        });
    }

    [Test]
    public void RenderRootFailure_EveryOtherFailure_IsEmpty()
    {
        Assert.Multiple(() =>
        {
            foreach (var failure in Enum.GetValues<GitRootFailure>().Where(f => f != GitRootFailure.NoRepositoryFound))
            {
                Assert.That(GitHintText.RenderRootFailure(failure), Is.Empty, $"{failure}");
            }
        });
    }
}
