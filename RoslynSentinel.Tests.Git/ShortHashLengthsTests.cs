using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="ShortHashLengths"/>: git's abbreviation length is probed once per repository (keyed by the git common directory, so a linked worktree shares
/// the entry), refreshed on every <see cref="GitBackendWarmup.StartRepoProbe"/>, and a failing probe declines to the git CLI. Every probe here is an injected
/// counting function, so the tests assert how many times git would have been spawned.
/// </summary>
[TestFixture]
public class ShortHashLengthsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // ---- the cache itself (no git needed) ----

    [Test]
    public void Get_ProbesOncePerCommonDirectory_AndCachesTheResult()
    {
        var probes = 0;
        var lengths = new ShortHashLengths((_, _) => ++probes + 6);
        var commonA = Path.Combine(Path.GetTempPath(), "rs-shl-a", ".git");
        var commonB = Path.Combine(Path.GetTempPath(), "rs-shl-b", ".git");

        var first = lengths.Get(commonA, "irrelevant", Timeout);
        var again = lengths.Get(commonA, "another working directory", Timeout);
        var other = lengths.Get(commonB, "irrelevant", Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(7));
            Assert.That(again, Is.EqualTo(7), "the cached entry is returned without probing");
            Assert.That(other, Is.EqualTo(8), "a different repository is probed on its own");
            Assert.That(probes, Is.EqualTo(2));
            Assert.That(lengths.Peek(commonA), Is.EqualTo(7));
        });
    }

    [Test]
    public void Get_KeyIsNormalised_TrailingSeparatorAndOnWindowsCase()
    {
        var probes = 0;
        var lengths = new ShortHashLengths((_, _) => ++probes + 6);
        var common = Path.Combine(Path.GetTempPath(), "rs-shl-norm", ".git");

        lengths.Get(common, "wd", Timeout);
        lengths.Get(common + Path.DirectorySeparatorChar, "wd", Timeout);
        if (OperatingSystem.IsWindows())
        {
            lengths.Get(common.ToUpperInvariant(), "wd", Timeout);
        }

        Assert.That(probes, Is.EqualTo(1));
    }

    [Test]
    public void Get_FailingProbe_ThrowsNotSupported_AndIsNotCached()
    {
        var probes = 0;
        var lengths = new ShortHashLengths((_, _) =>
        {
            probes++;
            return null;
        });
        var common = Path.Combine(Path.GetTempPath(), "rs-shl-fail", ".git");

        Assert.Throws<NotSupportedException>(() => lengths.Get(common, "wd", Timeout));
        Assert.Throws<NotSupportedException>(() => lengths.Get(common, "wd", Timeout));

        Assert.That(probes, Is.EqualTo(2), "a failure is retried on the next use, never remembered");
    }

    [Test]
    public void Refresh_ReplacesTheEntry_AndAFailedRefreshDropsIt()
    {
        var next = new Queue<int?>([8, null, 9]);
        var lengths = new ShortHashLengths((_, _) => next.Dequeue());
        var common = Path.Combine(Path.GetTempPath(), "rs-shl-refresh", ".git");

        var first = lengths.Refresh(common, "wd", Timeout);
        var failed = lengths.Refresh(common, "wd", Timeout);
        var droppedPeek = lengths.Peek(common);
        var droppedSolution = lengths.SolutionRepositoryLength;
        var third = lengths.Refresh(common, "wd", Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(8));
            Assert.That(failed, Is.Null);
            Assert.That(droppedPeek, Is.Null, "a failed refresh removes the possibly stale entry");
            Assert.That(droppedSolution, Is.Null);
            Assert.That(third, Is.EqualTo(9));
            Assert.That(lengths.Peek(common), Is.EqualTo(9));
            Assert.That(lengths.SolutionRepositoryLength, Is.EqualTo(9));
        });
    }

    // ---- with real repositories ----

    [TestFixture]
    [Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
    public class WithRepositories
    {
        private string _root = null!;
        private string _repo = null!;

        [SetUp]
        public void SetUp()
        {
            if (!IsGitAvailable())
            {
                Assert.Ignore("git is not available on PATH.");
            }

            _root = Path.Combine(Path.GetTempPath(), "rs-shortlen-" + Guid.NewGuid().ToString("N"));
            _repo = Path.Combine(_root, "repo");
            InitRepo(_repo);
            RunGit(_repo, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(_repo, "a.txt"), "a\n");
            CommitAll(_repo, "first");
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

        private static FallbackGitReader Composite(ShortHashLengths lengths) =>
            new(new Libgit2GitReader(lengths), new CliGitReader(), _ => { });

        [Test]
        public void LinkedWorktreeSharesTheRepositorysEntry_OneProbeForBoth_AndAnotherRepositoryIsProbedOnItsOwn()
        {
            var probes = 0;
            // 9 is longer than git's own answer for this tiny repository, so a result of 9 proves the cached/injected value was used, not git's.
            var lengths = new ShortHashLengths((_, _) =>
            {
                Interlocked.Increment(ref probes);
                return 9;
            });
            var reader = new Libgit2GitReader(lengths);
            var worktree = Path.Combine(_root, "wt");
            RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
            var other = Path.Combine(_root, "other");
            InitRepo(other);
            File.WriteAllText(Path.Combine(other, "o.txt"), "o\n");
            CommitAll(other, "other first");

            var inPrimary = reader.GetHeadSha(_repo, shortForm: true, Timeout);
            var inWorktree = reader.GetHeadSha(worktree, shortForm: true, Timeout);
            var inPrimaryAgain = reader.GetHeadSha(_repo, shortForm: true, Timeout);
            var probesForOneRepository = probes;
            var inOther = reader.GetHeadSha(other, shortForm: true, Timeout);

            Assert.Multiple(() =>
            {
                Assert.That(inPrimary, Has.Length.EqualTo(9));
                Assert.That(inWorktree, Has.Length.EqualTo(9));
                Assert.That(inPrimaryAgain, Has.Length.EqualTo(9));
                Assert.That(probesForOneRepository, Is.EqualTo(1), "the worktree and the primary checkout share one probe (same git common directory)");
                Assert.That(inOther, Has.Length.EqualTo(9));
                Assert.That(probes, Is.EqualTo(2), "a repository reached by another path is probed lazily on first use, once");
            });
        }

        [Test]
        public void StartRepoProbe_RefreshesTheLengthEveryTime_AndTheReaderUsesTheNewOne()
        {
            var next = new Queue<int?>([8, 10]);
            var probes = 0;
            var lengths = new ShortHashLengths((_, _) =>
            {
                probes++;
                return next.Dequeue();
            });
            var reader = Composite(lengths);
            var warmup = new GitBackendWarmup(() => "v-test", repoProbe: null, reader, log: null, lengths);

            warmup.StartRepoProbe(_repo);
            var first = warmup.Report(TimeSpan.FromSeconds(30));
            var firstHead = new Libgit2GitReader(lengths).GetHeadSha(_repo, true, Timeout);
            warmup.StartRepoProbe(_repo);
            var second = warmup.Report(TimeSpan.FromSeconds(30));
            var secondHead = new Libgit2GitReader(lengths).GetHeadSha(_repo, true, Timeout);

            Assert.Multiple(() =>
            {
                Assert.That(first.RepoProbe, Is.EqualTo("ok"));
                Assert.That(first.ShortHashLength, Is.EqualTo(8));
                Assert.That(firstHead, Has.Length.EqualTo(8));
                Assert.That(second.RepoProbe, Is.EqualTo("ok"));
                Assert.That(second.ShortHashLength, Is.EqualTo(10));
                Assert.That(secondHead, Has.Length.EqualTo(10));
                Assert.That(probes, Is.EqualTo(2), "one probe per StartRepoProbe, none on the read path (the entry was cached)");
            });
        }

        [Test]
        public void StartRepoProbe_WhenTheLengthProbeFails_ReportsItAndTheReadsDeclineToTheCli()
        {
            var lengths = new ShortHashLengths((_, _) => null);
            var reader = Composite(lengths);
            var warmup = new GitBackendWarmup(() => "v-test", repoProbe: null, reader, log: null, lengths);

            warmup.StartRepoProbe(_repo);
            var block = warmup.Report(TimeSpan.FromSeconds(30));
            var viaComposite = reader.GetHeadSha(_repo, shortForm: true, Timeout);
            var viaCli = new CliGitReader().GetHeadSha(_repo, shortForm: true, Timeout);

            Assert.Multiple(() =>
            {
                Assert.That(block.RepoProbe, Does.StartWith("ok (short-hash length not probed"));
                Assert.That(block.ShortHashLength, Is.Null);
                Assert.That(viaComposite, Is.EqualTo(viaCli));
                Assert.That(reader.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
                Assert.That(reader.DeclineCount, Is.EqualTo(1));
                Assert.That(reader.LastDeclineReason, Is.Not.Null.And.Not.Empty);
                Assert.That(reader.CliFallbackCount, Is.Zero, "a failed length probe is a planned decline, not a failure");
                Assert.That(reader.FirstFallbackReason, Is.Null);
            });
        }

        [Test]
        public void ReadsAfterAFailedProbe_RetryTheProbe_AndServeFromLibgit2WhenItWorksAgain()
        {
            var works = false;
            var probes = 0;
            var lengths = new ShortHashLengths((_, _) =>
            {
                probes++;
                return works ? 7 : null;
            });
            var reader = Composite(lengths);

            var whileFailing = reader.GetHeadSha(_repo, shortForm: true, Timeout);
            works = true;
            var afterRecovery = reader.GetHeadSha(_repo, shortForm: true, Timeout);
            var libgit2Calls = reader.Libgit2Count;

            Assert.Multiple(() =>
            {
                Assert.That(whileFailing, Is.EqualTo(afterRecovery));
                Assert.That(reader.DeclineCount, Is.EqualTo(1), "only the call made while the probe failed went to the CLI");
                Assert.That(reader.CliFallbackCount, Is.Zero, "a failed length probe is a planned decline, not a failure");
                Assert.That(libgit2Calls, Is.EqualTo(1));
                Assert.That(probes, Is.EqualTo(2));
            });
        }
    }
}
