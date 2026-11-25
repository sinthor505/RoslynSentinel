using System.Text;
using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Tools.Basic.GitCommitMessageTestSupport;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The configuration declines of <see cref="GitCommitMessage.FormatForRepository"/> (step 5c-3 of proposal_libgit2sharp_in_process_git.md). Each decline test also commits with the real git
/// under the same configuration to show the decline is needed (git's result differs from the port's or git refuses), so a decline cannot rot into a no-op. The real global config is read, not written.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCommitMessageConfigTests
{
    private const string CommentedMessage = "subject\n\n# a comment line\nbody";

    private string _root = null!;
    private string _repository = null!;

    [SetUp]
    public void SetUp()
    {
        if (!RoslynSentinel.Tests.Battery.Basic.GitTestRepo.IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = GitHookTestSupport.NewRoot("rs-commitmsg-cfg-");
        _repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repository);
        Git("init", "--quiet");
    }

    [TearDown]
    public void TearDown() => GitHookTestSupport.DeleteTreeWithRetries(_root);

    private void Git(params string[] args)
    {
        var result = RunGitBytes(_repository, args);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error.Trim()}");
        }
    }

    private static string? Text(byte[]? bytes) => bytes is null ? null : Encoding.UTF8.GetString(bytes);

    [Test]
    public void CleanRepository_FormatsLikeGit()
    {
        var formatted = GitCommitMessage.FormatForRepository(_repository, CommentedMessage, "0123456789ab", "agent");
        var real = CommitWithRealGit(_repository, CommentedMessage, "0123456789ab", "agent");

        Assert.That(formatted.IsFormatted, Is.True, formatted.Detail);
        Assert.That(formatted.Message, Is.EqualTo(Text(real.Message)));
    }

    [Test]
    public void NotARepository_Declines()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        var result = GitCommitMessage.FormatForRepository(plain, "subject");

        Assert.That(result.Outcome, Is.EqualTo(CommitMessageOutcome.Declined));
        Assert.That(result.Detail, Does.Contain("not inside a git repository"));
    }

    [TestCase("commit.cleanup", "strip")]
    [TestCase("commit.cleanup", "verbatim")]
    [TestCase("core.commentChar", ";")]
    [TestCase("core.commentString", "//")]
    [TestCase("i18n.commitEncoding", "ISO-8859-1")]
    public void MessageConfig_Declines(string key, string value)
    {
        Git("config", key, value);

        var result = GitCommitMessage.FormatForRepository(_repository, CommentedMessage);

        Assert.That(result.Outcome, Is.EqualTo(CommitMessageOutcome.Declined), "the port must not format under " + key);
        Assert.That(result.Detail, Does.Contain(key.ToLowerInvariant()).IgnoreCase);
    }

    [Test]
    public void CommitCleanupStrip_ChangesWhatGitStores()
    {
        // Why commit.cleanup declines: with strip, git removes the # lines the default mode keeps.
        Git("config", "commit.cleanup", "strip");

        var real = CommitWithRealGit(_repository, CommentedMessage, null, null);

        Assert.That(real.ExitCode, Is.EqualTo(0), real.Error);
        Assert.That(Text(real.Message), Is.Not.EqualTo(GitCommitMessage.Format(CommentedMessage).Message));
    }

    [Test]
    public void CommitTemplate_IsIgnoredForDashM_SoNoDeclineIsNeeded()
    {
        var template = Path.Combine(_root, "template.txt");
        File.WriteAllText(template, "Describe the change here\n", new UTF8Encoding(false));
        Git("config", "commit.template", template.Replace('\\', '/'));
        var problems = new List<string>();

        // A normal message, the template text itself, and a message that starts with the template: git stores each as given (cleaned), never merges or refuses.
        foreach (var message in new[] { "subject\n\nbody", "Describe the change here", "Describe the change here\n\nplus detail" })
        {
            foreach (var callId in new string?[] { null, "0123456789ab" })
            {
                var formatted = GitCommitMessage.FormatForRepository(_repository, message, callId);
                var real = CommitWithRealGit(_repository, message, callId, null);
                if (!formatted.IsFormatted || real.ExitCode != 0 || formatted.Message != Text(real.Message))
                {
                    problems.Add($"{Show(message)} callId {callId}: port {formatted.Outcome} {Show(formatted.Message)} ({formatted.Detail}); git exit {real.ExitCode} {Show(Text(real.Message))} {real.Error}");
                }
            }
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [TestCase("trailer.where", "start")]
    [TestCase("trailer.separators", ":=")]
    [TestCase("trailer.ifexists", "add")]
    [TestCase("trailer.Co-Authored-By.where", "end")]
    public void TrailerConfig_DeclinesWithTrailers_NotWithout(string key, string value)
    {
        Git("config", key, value);
        const string message = "subject\n\nbody\n\nCo-Authored-By: A <a@b.c>";

        var withTrailers = GitCommitMessage.FormatForRepository(_repository, message, "0123456789ab");
        var withoutTrailers = GitCommitMessage.FormatForRepository(_repository, message);

        Assert.That(withTrailers.Outcome, Is.EqualTo(CommitMessageOutcome.Declined), "the port must not add trailers under " + key);
        Assert.That(withoutTrailers.IsFormatted, Is.True, withoutTrailers.Detail);
        Assert.That(withoutTrailers.Message, Is.EqualTo(Text(CommitWithRealGit(_repository, message, null, null).Message)));
    }

    [Test]
    public void TrailerWhereStart_ChangesWhatGitStores()
    {
        // Why trailer.* declines: where=start puts the new trailer first.
        Git("config", "trailer.where", "start");
        const string message = "subject\n\nbody\n\nCo-Authored-By: A <a@b.c>";

        var real = CommitWithRealGit(_repository, message, "0123456789ab", null);

        Assert.That(real.ExitCode, Is.EqualTo(0), real.Error);
        Assert.That(Text(real.Message), Is.Not.EqualTo(GitCommitMessage.Format(message, "0123456789ab").Message));
    }

    [Test]
    public void LinkedWorktree_SeesTheMainRepositoryConfig()
    {
        // A linked worktree's own config misses the main repository's: the decline must still fire (commit.cleanup lives in <commondir>/config).
        Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "--quiet", "-m", "first", "--no-verify");
        var worktree = Path.Combine(_root, "linked");
        Git("worktree", "add", "--quiet", worktree, "-b", "linked-branch");
        Git("config", "commit.cleanup", "strip");

        var result = GitCommitMessage.FormatForRepository(worktree, CommentedMessage);

        Assert.That(result.Outcome, Is.EqualTo(CommitMessageOutcome.Declined), result.Message);
    }
}
