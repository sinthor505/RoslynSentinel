using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="IGitReader.FindCommitByTrailer"/> and <see cref="IGitReader.GetBoolConfig"/> through <see cref="Libgit2GitReader"/> and
/// <see cref="CliGitReader"/> on throwaway repos: both must agree. The CLI answer comes from git's own trailer parser
/// (<c>%(trailers:key=...)</c>), the libgit2 answer from <see cref="GitCommitTrailers.GetValues"/>, so this is also the check that the
/// hand-written parse matches git for the message shapes the Git tool writes and the ones it must not mistake for a trailer.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCommitTrailerReaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // (CLI answer, libgit2 answer) when neither finds a commit.
    private static readonly (string?, string?) NotFound = (null, null);

    private readonly CliGitReader _cli = new();
    private readonly Libgit2GitReader _lib = new();

    private string _root = null!;
    private string _repo = null!;
    private int _messageFiles;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-gittrailer-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        InitRepo(_repo);
        RunGit(_repo, "config", "core.autocrlf", "false");
        RunGit(_repo, "config", "user.name", "Test");
        RunGit(_repo, "config", "user.email", "test@example.com");
    }

    [TearDown]
    public void TearDown()
    {
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // An empty commit with exactly this message (verbatim cleanup, so the shape under test is what is stored).
    private string CommitWithMessage(string message)
    {
        var file = Path.Combine(_root, $"msg{_messageFiles++}.txt");
        File.WriteAllText(file, message);
        RunGit(_repo, "commit", "--quiet", "--allow-empty", "--cleanup=verbatim", "-F", file);
        return RunGit(_repo, "rev-parse", "HEAD");
    }

    private (string?, string?) Find(string value, int max = 50, string key = "Tool-Call-Id") =>
        (_cli.FindCommitByTrailer(_repo, key, value, max, Timeout), _lib.FindCommitByTrailer(_repo, key, value, max, Timeout));

    [Test]
    public void FindCommitByTrailer_MessageShapes_BothReadersAgreeAndMatchWhatGitCallsATrailer()
    {
        CommitWithMessage("plain message\n");
        var normal = CommitWithMessage("subject\n\nbody text\n\nTool-Call-Id: AAA111\nTool-Agent: agent-1\n");
        var mixedCase = CommitWithMessage("subject\n\ntool-call-id: BBB222\n");
        CommitWithMessage("Tool-Call-Id: CCC333\n");
        CommitWithMessage("subject\n\nTool-Call-Id: DDD444\n\nclosing paragraph, not a trailer block\n");
        var afterOtherTrailer = CommitWithMessage("subject\n\nCo-Authored-By: Pat <pat@example.com>\nTool-Call-Id: EEE555\n");
        var paddedValue = CommitWithMessage("subject\n\nTool-Call-Id:    FFF666   \n");
        var crlf = CommitWithMessage("subject\r\n\r\nTool-Call-Id: GGG777\r\n");

        var expectations = new (string Value, string? Sha)[]
        {
            ("AAA111", normal),
            ("BBB222", mixedCase),
            ("CCC333", null), // the subject paragraph is never a trailer block
            ("DDD444", null), // not in the last paragraph
            ("EEE555", afterOtherTrailer),
            ("FFF666", paddedValue),
            ("GGG777", crlf),
            ("NOPE", null),
        };

        Assert.Multiple(() =>
        {
            foreach (var (value, sha) in expectations)
            {
                var (cli, lib) = Find(value);
                Assert.That(cli, Is.EqualTo(sha), $"git CLI for {value}");
                Assert.That(lib, Is.EqualTo(sha), $"libgit2 for {value}");
            }
        });
    }

    [Test]
    public void FindCommitByTrailer_LimitsTheWalkToMaxCommits_NewestFirst()
    {
        var old = CommitWithMessage("old\n\nTool-Call-Id: OLD001\n");
        CommitWithMessage("filler 1\n");
        CommitWithMessage("filler 2\n");
        var newest = CommitWithMessage("new\n\nTool-Call-Id: NEW001\n");

        Assert.Multiple(() =>
        {
            Assert.That(Find("OLD001", max: 4), Is.EqualTo((old, old)), "inside the window.");
            Assert.That(Find("OLD001", max: 3), Is.EqualTo(NotFound), "four commits back is outside a window of three.");
            Assert.That(Find("NEW001", max: 1), Is.EqualTo((newest, newest)));
        });
    }

    [Test]
    public void FindCommitByTrailer_SameValueTwice_ReturnsTheNewest()
    {
        CommitWithMessage("first\n\nTool-Call-Id: DUP001\n");
        var second = CommitWithMessage("second\n\nTool-Call-Id: DUP001\n");

        Assert.That(Find("DUP001"), Is.EqualTo((second, second)));
    }

    [Test]
    public void FindCommitByTrailer_UnbornHeadAndNonRepository_AreNullForBothReaders()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        Assert.Multiple(() =>
        {
            Assert.That(Find("X"), Is.EqualTo(NotFound), "unborn HEAD");
            Assert.That(_cli.FindCommitByTrailer(plain, "Tool-Call-Id", "X", 50, Timeout), Is.Null);
            Assert.That(_lib.FindCommitByTrailer(plain, "Tool-Call-Id", "X", 50, Timeout), Is.Null);
        });
    }

    [Test]
    public void GetBoolConfig_BothReadersAgree_UnsetTrueFalseAndGitsOtherSpellings()
    {
        (bool?, bool?) Read() =>
            (_cli.GetBoolConfig(_repo, GitCommitTrailers.ConfigKey, Timeout), _lib.GetBoolConfig(_repo, GitCommitTrailers.ConfigKey, Timeout));

        Assert.That(Read(), Is.EqualTo(((bool?)null, (bool?)null)), "unset");
        foreach (var (text, expected) in new (string, bool?)[] { ("true", true), ("false", false), ("yes", true), ("off", false), ("1", true), ("0", false), ("2", true), ("maybe", null) })
        {
            RunGit(_repo, "config", GitCommitTrailers.ConfigKey, text);
            Assert.That(Read(), Is.EqualTo((expected, expected)), text);
        }
    }

    [Test]
    public void GetBoolConfig_NotABoolean_IsNullThroughTheCompositeAndTheCli()
    {
        RunGit(_repo, "config", GitCommitTrailers.ConfigKey, "maybe");
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });

        Assert.That(_cli.GetBoolConfig(_repo, GitCommitTrailers.ConfigKey, Timeout), Is.Null);
        Assert.That(composite.GetBoolConfig(_repo, GitCommitTrailers.ConfigKey, Timeout), Is.Null);
        Assert.That(composite.CliFallbackCount, Is.EqualTo(0), "a non-boolean value is a plain null from libgit2, not a failure that falls back to the CLI.");
    }

    [Test]
    public void Composite_ServesBothNewProbesFromLibgit2InTheNormalEnvironment()
    {
        RunGit(_repo, "config", GitCommitTrailers.ConfigKey, "true");
        var sha = CommitWithMessage("subject\n\nTool-Call-Id: ZZZ999\n");
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });

        Assert.That(composite.GetBoolConfig(_repo, GitCommitTrailers.ConfigKey, Timeout), Is.True);
        Assert.That(composite.FindCommitByTrailer(_repo, "Tool-Call-Id", "ZZZ999", 50, Timeout), Is.EqualTo(sha));
        Assert.That(composite.Libgit2Count, Is.EqualTo(2));
        Assert.That(composite.CliFallbackCount, Is.EqualTo(0), composite.FirstFallbackReason);
        Assert.That(composite.DeclineCount, Is.EqualTo(0), composite.LastDeclineReason);
    }
}

/// <summary>The pure trailer helpers; no process is started, so these also run in the fast suite.</summary>
[TestFixture]
public class GitCommitTrailersTests
{
    [Test]
    public void GetValues_OnlyTheLastParagraphAfterAnotherParagraphCounts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GitCommitTrailers.GetValues("s\n\nb\n\nTool-Call-Id: a1\nTool-Agent: g\n", "Tool-Call-Id"), Is.EqualTo(new[] { "a1" }));
            Assert.That(GitCommitTrailers.GetValues("s\n\nb\n\nTool-Call-Id: a1\nTool-Agent: g\n", "tool-agent"), Is.EqualTo(new[] { "g" }), "keys are case-insensitive.");
            Assert.That(GitCommitTrailers.GetValues("Tool-Call-Id: a1\n", "Tool-Call-Id"), Is.Empty, "the subject paragraph is not a trailer block.");
            Assert.That(GitCommitTrailers.GetValues("s\n\nTool-Call-Id: a1\n\nmore text\n", "Tool-Call-Id"), Is.Empty, "not the last paragraph.");
            Assert.That(GitCommitTrailers.GetValues("s\r\n\r\nTool-Call-Id: a1\r\n", "Tool-Call-Id"), Is.EqualTo(new[] { "a1" }), "CRLF.");
            Assert.That(GitCommitTrailers.GetValues("", "Tool-Call-Id"), Is.Empty);
        });
    }

    [Test]
    public void StripOwnTrailers_RemovesOnlyTheTwoKeys_AndLeavesEverythingElseUntouched()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GitCommitTrailers.StripOwnTrailers("s\n\nb\n\nCo-Authored-By: P <p@e.c>\nTool-Call-Id: a1\nTool-Agent: g\n"),
                Is.EqualTo("s\n\nb\n\nCo-Authored-By: P <p@e.c>\n"));
            Assert.That(GitCommitTrailers.StripOwnTrailers("s\n\nb\n\nTool-Call-Id: a1\nTool-Agent: g\n"), Is.EqualTo("s\n\nb\n"), "an emptied block takes its separator with it.");
            Assert.That(GitCommitTrailers.StripOwnTrailers("s\r\n\r\nb\r\n\r\nTool-Call-Id: a1\r\n"), Is.EqualTo("s\r\n\r\nb\r\n"), "CRLF survives.");
            Assert.That(GitCommitTrailers.StripOwnTrailers("s\n\nb\n\nCo-Authored-By: P <p@e.c>\n"), Is.EqualTo("s\n\nb\n\nCo-Authored-By: P <p@e.c>\n"), "nothing to strip.");
            Assert.That(GitCommitTrailers.StripOwnTrailers("Tool-Call-Id: subject only\n"), Is.EqualTo("Tool-Call-Id: subject only\n"), "a subject is never stripped.");
        });
    }

    [Test]
    public void TrailerArgs_AgentOnlyWhenPresent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GitCommitTrailers.TrailerArgs("id1", null), Is.EqualTo(new[] { "--trailer", "Tool-Call-Id: id1" }));
            Assert.That(GitCommitTrailers.TrailerArgs("id1", "  "), Is.EqualTo(new[] { "--trailer", "Tool-Call-Id: id1" }));
            Assert.That(GitCommitTrailers.TrailerArgs("id1", "agent-9"), Is.EqualTo(new[] { "--trailer", "Tool-Call-Id: id1", "--trailer", "Tool-Agent: agent-9" }));
        });
    }

    [Test]
    public void IsTrailerOptionUnsupported_IsAUsageErrorNamingTheOption()
    {
        const string oldGit = "error: unknown option `trailer'\nusage: git commit [-a | --interactive | --patch] [-s] [-v] [-u[<mode>]] [--amend]\n";
        Assert.Multiple(() =>
        {
            Assert.That(GitCommitTrailers.IsTrailerOptionUnsupported(129, oldGit), Is.True, "git older than 2.32.");
            Assert.That(GitCommitTrailers.IsTrailerOptionUnsupported(129, "error: unknown option `bogus'\n"), Is.False, "another unknown option.");
            Assert.That(GitCommitTrailers.IsTrailerOptionUnsupported(1, "trailer"), Is.False, "an ordinary commit failure is not a usage error.");
            Assert.That(GitCommitTrailers.IsTrailerOptionUnsupported(0, ""), Is.False);
        });
    }
}
