using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;
using static RoslynSentinel.Tests.Tools.Basic.CasCommitTestSupport;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="Libgit2GitReader.CommitWithCompareAndSwap"/> (step 5c-1 of the in-process git proposal): the compare-and-swap commit primitive. Pinned here: a lost race returns
/// BranchMoved and moves nothing; a held index.lock returns LockContention and is left alone; racing committers (in-process against in-process, and in-process against CLI
/// <c>git commit</c> children) lose no commit and leave an fsck-clean repository; the trees, reflog entries and the root and detached cases equal what the git CLI writes in a twin
/// repository; every planned decline is a typed result.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCasCommitTests
{
    private const int RaceIterations = 25;

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-cas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => DeleteTree(_root);

    private static CasCommitResult CommitAll(string directory, string message) =>
        Libgit2GitReader.CommitWithCompareAndSwap(directory, message, CasCommitTreeSource.WholeIndex);

    private static CasCommitResult CommitPaths(string directory, string message, params string[] paths) =>
        Libgit2GitReader.CommitWithCompareAndSwap(directory, message, CasCommitTreeSource.HeadPlusPaths, paths);

    private static void AssertNoLockLeft(string directory) =>
        Assert.That(File.Exists(IndexLockPath(directory)), Is.False, "index.lock was left behind.");

    private static void AssertDeclined(CasCommitResult result, string detailFragment)
    {
        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Declined), result.ToString());
        Assert.That(result.Detail, Does.Contain(detailFragment).IgnoreCase);
    }

    // ---- 1. compare-and-swap mismatch ----

    [Test]
    public void BranchMovedBetweenCommitAndRefUpdate_ReturnsBranchMoved_NothingLost_LockReleased()
    {
        var dir = NewRepo(_root, "mismatch");
        var first = Tip(dir);
        Write(dir, "b.txt", "b\n");
        RunGit(dir, "add", "b.txt");

        string? other = null;
        var result = Libgit2GitReader.CommitWithCompareAndSwap(dir, "ours\n", CasCommitTreeSource.WholeIndex, betweenCommitAndRefUpdate: () =>
        {
            // commit-tree and update-ref take no index.lock, which this call holds.
            other = RunGit(dir, "commit-tree", TreeOfHead(dir), "-p", first, "-m", "other");
            RunGit(dir, "update-ref", "refs/heads/master", other, first);
        });

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.BranchMoved), result.ToString());
        Assert.That(result.ExpectedTip, Is.EqualTo(first));
        Assert.That(result.ActualTip, Is.EqualTo(other));
        Assert.That(result.CommitId, Is.Null);
        Assert.That(result.Detail, Does.Contain("does not match"), "libgit2's own text is kept in Detail.");
        Assert.That(Tip(dir), Is.EqualTo(other), "the other committer's commit must stay the tip.");
        Assert.That(CommitCount(dir), Is.EqualTo(2));
        Assert.That(RunGit(dir, "diff", "--cached", "--name-only"), Is.EqualTo("b.txt"), "the index is not rewritten.");
        AssertNoLockLeft(dir);
        AssertFsckClean(dir);
    }

    [Test]
    public void UnbornBranchCreatedBetweenCommitAndRefUpdate_ReturnsBranchMoved()
    {
        var dir = NewRepo(_root, "unborn-mismatch", withInitialCommit: false);
        Write(dir, "a.txt", "a\n");
        RunGit(dir, "add", "a.txt");

        string? other = null;
        var result = Libgit2GitReader.CommitWithCompareAndSwap(dir, "ours\n", CasCommitTreeSource.WholeIndex, betweenCommitAndRefUpdate: () =>
        {
            // write-tree would need the index.lock this call holds, so the empty tree is written as an object instead.
            other = RunGit(dir, "commit-tree", RunGit(dir, "hash-object", "-t", "tree", "-w", "--stdin"), "-m", "other");
            RunGit(dir, "update-ref", "refs/heads/master", other);
        });

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.BranchMoved), "an all-zero expected id means 'must not exist': " + result);
        Assert.That(result.ExpectedTip, Is.Null);
        Assert.That(result.ActualTip, Is.EqualTo(other));
        Assert.That(Tip(dir), Is.EqualTo(other));
        Assert.That(CommitCount(dir), Is.EqualTo(1));
        AssertNoLockLeft(dir);
    }

    // ---- 2. lock contention ----

    [Test]
    public void HeldIndexLock_ReturnsLockContention_AndLeavesTheForeignLockAlone()
    {
        var dir = NewRepo(_root, "locked");
        var first = Tip(dir);
        Write(dir, "b.txt", "b\n");
        RunGit(dir, "add", "b.txt");
        File.WriteAllText(IndexLockPath(dir), "held-by-someone-else");

        var result = CommitAll(dir, "ours\n");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.LockContention), result.ToString());
        Assert.That(File.ReadAllText(IndexLockPath(dir)), Is.EqualTo("held-by-someone-else"), "the foreign lock must be untouched.");
        Assert.That(Tip(dir), Is.EqualTo(first));
        File.Delete(IndexLockPath(dir));
    }

    // ---- 3. concurrency ----

    [Test]
    public void TwoInProcessCommittersRacing_LoseNothing_FsckClean()
    {
        var dir = NewRepo(_root, "race-inproc");
        foreach (var prefix in new[] { "a", "b" })
        {
            for (var i = 0; i < RaceIterations; i++)
            {
                Write(dir, $"{prefix}-{i}.txt", $"{prefix}{i}\n");
            }
        }

        RunGit(dir, "add", "-A");
        var initialCount = CommitCount(dir);

        var barrier = new Barrier(2);
        var committed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var losers = 0;
        Task Worker(string prefix) => Task.Run(() =>
        {
            for (var i = 0; i < RaceIterations; i++)
            {
                Assert.That(barrier.SignalAndWait(TimeSpan.FromSeconds(60)), Is.True, "the other worker did not reach the barrier.");
                for (var attempt = 0; ; attempt++)
                {
                    Assert.That(attempt, Is.LessThan(2000), $"{prefix}-{i} never committed.");
                    var result = CommitPaths(dir, $"{prefix}{i}\n", $"{prefix}-{i}.txt");
                    if (result.IsCommitted)
                    {
                        committed.Add(result.CommitId!);
                        break;
                    }

                    // A loser is BranchMoved (the other side won the swap) or LockContention (it holds index.lock): retried against the new tip.
                    Assert.That(result.Outcome, Is.AnyOf(CasCommitOutcome.BranchMoved, CasCommitOutcome.LockContention), result.ToString());
                    Interlocked.Increment(ref losers);
                    Thread.Sleep(1);
                }
            }
        });

        Task.WaitAll(Worker("a"), Worker("b"));

        TestContext.Out.WriteLine($"in-process race: {committed.Count} committed, {losers} lost attempts retried.");
        Assert.That(committed.Count, Is.EqualTo(2 * RaceIterations));
        Assert.That(CommitCount(dir), Is.EqualTo(initialCount + 2 * RaceIterations));
        var reachable = RunGit(dir, "rev-list", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        Assert.That(committed, Is.All.Matches<string>(reachable.Contains), "every Committed hash must be reachable from the tip.");
        Assert.That(RunGit(dir, "ls-tree", "-r", "--name-only", "HEAD").Split('\n').Length, Is.EqualTo(1 + 2 * RaceIterations));
        Assert.That(Reflog(dir, "HEAD"), Has.Length.EqualTo(CommitCount(dir)), "HEAD's reflog gets one entry per commit.");
        Assert.That(Reflog(dir, "refs/heads/master"), Has.Length.EqualTo(CommitCount(dir)));
        AssertNoLockLeft(dir);
        AssertFsckClean(dir);
    }

    [Test]
    public void InProcessCommitsRacingCliCommits_LoseNothing_FsckClean()
    {
        var dir = NewRepo(_root, "race-cli");
        for (var i = 0; i < RaceIterations; i++)
        {
            Write(dir, $"ours-{i}.txt", $"{i}\n");
        }

        // Staged in the real index; the CLI side commits with --only and no path (HEAD's tree), so it never sweeps these into its own commits.
        RunGit(dir, "add", "-A");
        var initialCount = CommitCount(dir);

        var cliSuccesses = 0;
        var cliFailures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var cli = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < RaceIterations; i++)
            {
                var (exitCode, _, stdErr) = RunGitRaw(dir, "commit", "--quiet", "--allow-empty", "--only", "-m", $"cli-{i}");
                if (exitCode == 0)
                {
                    Interlocked.Increment(ref cliSuccesses);
                }
                else
                {
                    cliFailures.Add(stdErr);
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var committed = new List<string>();
        var losers = new Dictionary<CasCommitOutcome, int>();
        var inProcess = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < RaceIterations; i++)
            {
                for (var attempt = 0; ; attempt++)
                {
                    Assert.That(attempt, Is.LessThan(2000), $"ours-{i} never committed.");
                    var result = CommitPaths(dir, $"ours-{i}\n", $"ours-{i}.txt");
                    if (result.IsCommitted)
                    {
                        committed.Add(result.CommitId!);
                        break;
                    }

                    Assert.That(result.Outcome, Is.AnyOf(CasCommitOutcome.BranchMoved, CasCommitOutcome.LockContention), result.ToString());
                    losers[result.Outcome] = losers.GetValueOrDefault(result.Outcome) + 1;
                    Thread.Sleep(1);
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Task.WaitAll(cli, inProcess);

        // A losing CLI commit must fail the way git fails against another committer (a held index.lock, or the ref moved under it), never any other way.
        Assert.That(cliFailures, Is.All.Matches<string>(text => text.Contains(".lock") || text.Contains("cannot lock ref")), string.Join(" || ", cliFailures));
        TestContext.Out.WriteLine($"mixed race: in-process {committed.Count} committed (losers: {string.Join(", ", losers.Select(p => $"{p.Key}={p.Value}"))}), cli {cliSuccesses}/{RaceIterations} committed, {cliFailures.Count} lost.");
        Assert.That(committed, Has.Count.EqualTo(RaceIterations));
        var expectedTotal = initialCount + RaceIterations + cliSuccesses;
        Assert.That(CommitCount(dir), Is.EqualTo(expectedTotal), "total commits must be the initial ones plus the successes of both sides.");
        var reachable = RunGit(dir, "rev-list", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        Assert.That(committed, Is.All.Matches<string>(reachable.Contains));
        Assert.That(Reflog(dir, "HEAD"), Has.Length.EqualTo(expectedTotal));
        Assert.That(Reflog(dir, "refs/heads/master"), Has.Length.EqualTo(expectedTotal));
        AssertNoLockLeft(dir);
        AssertFsckClean(dir);
    }

    // ---- 4, 5, 6. root commit, detached HEAD, reflog text equal to the CLI's ----

    [Test]
    public void UnbornBranch_WritesARootCommit()
    {
        var dir = NewRepo(_root, "root", withInitialCommit: false);
        Write(dir, "a.txt", "a\n");
        RunGit(dir, "add", "a.txt");

        var result = CommitAll(dir, "root commit\n");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        Assert.That(Tip(dir), Is.EqualTo(result.CommitId));
        Assert.That(CommitCount(dir), Is.EqualTo(1));
        Assert.That(RunGit(dir, "log", "-1", "--format=%P"), Is.Empty, "a root commit has no parent.");
        Assert.That(RunGit(dir, "symbolic-ref", "HEAD"), Is.EqualTo("refs/heads/master"));
        AssertNoLockLeft(dir);
        AssertFsckClean(dir);
    }

    [Test]
    public void DetachedHead_MovesHeadOnly()
    {
        var dir = NewRepo(_root, "detached");
        var first = Tip(dir);
        RunGit(dir, "checkout", "--quiet", "--detach");
        Write(dir, "b.txt", "b\n");
        RunGit(dir, "add", "b.txt");

        var result = CommitAll(dir, "detached commit\n");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        Assert.That(Tip(dir), Is.EqualTo(result.CommitId));
        Assert.That(Resolve(dir, "refs/heads/master"), Is.EqualTo(first), "the branch must not move while HEAD is detached.");
        Assert.That(RunGit(dir, "log", "-1", "--format=%P"), Is.EqualTo(first));
        AssertNoLockLeft(dir);
        AssertFsckClean(dir);
    }

    [TestCase("normal")]
    [TestCase("initial")]
    [TestCase("detached")]
    [TestCase("multiline")]
    public void ReflogEntries_EqualWhatTheCliWritesForTheSameCommitKind(string kind)
    {
        var message = kind == "multiline" ? "first line\nsecond line\n\nbody\n" : "subject line\n";
        var cliMessage = message.TrimEnd('\n');
        string[] references = kind == "detached" ? ["HEAD"] : ["HEAD", "refs/heads/master"];

        string Arrange(string name)
        {
            var dir = NewRepo(_root, name, withInitialCommit: kind != "initial");
            if (kind == "detached")
            {
                RunGit(dir, "checkout", "--quiet", "--detach");
            }

            Write(dir, "b.txt", "b\n");
            RunGit(dir, "add", "b.txt");
            return dir;
        }

        var ours = Arrange("reflog-ours");
        var twin = Arrange("reflog-twin");

        var result = CommitAll(ours, message);
        RunGit(twin, "commit", "--quiet", "-m", cliMessage);

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        foreach (var reference in references)
        {
            Assert.That(Reflog(ours, reference)[0], Is.EqualTo(Reflog(twin, reference)[0]), $"newest reflog entry of {reference} (identity|subject).");
            Assert.That(Reflog(ours, reference), Has.Length.EqualTo(Reflog(twin, reference).Length), $"reflog length of {reference}.");
        }

        AssertSameHeadTree(ours, twin);
    }

    [Test]
    public void Message_IsStoredByteForByte()
    {
        var dir = NewRepo(_root, "message");
        Write(dir, "b.txt", "b\n");
        RunGit(dir, "add", "b.txt");
        const string message = "subject ü\n\nbody line 1\n\n  indented\nno final newline";

        var result = CommitAll(dir, message);

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        var raw = RunGitRaw(dir, "cat-file", "commit", result.CommitId!).StdOut;
        Assert.That(raw.Substring(raw.IndexOf("\n\n", StringComparison.Ordinal) + 2), Is.EqualTo(message), "no cleanup, no added newline.");
    }

    // ---- 7. HeadPlusPaths and WholeIndex trees equal the CLI's ----

    private static void ArrangeBaseline(string dir)
    {
        Write(dir, "keep.txt", "keep\n");
        Write(dir, "mod.txt", "mod v1\n");
        Write(dir, "del.txt", "del\n");
        Write(dir, "exe.sh", "#!/bin/sh\n");
        Write(dir, "sp ace/ünï.txt", "unicode v1\n");
        Write(dir, "dir/last.txt", "last\n");
        Write(dir, "other.txt", "other v1\n");
        GitTestRepo.CommitAll(dir, "baseline");
    }

    // stageModeChange: git commit --only cannot be compared on a core.filemode=false repository (it takes the mode from HEAD), so only the whole-index test stages one.
    private static void ArrangeStagedChanges(string dir, bool stageModeChange)
    {
        Write(dir, "mod.txt", "mod v2\n");
        Write(dir, "new.txt", "new\n");
        Write(dir, "sp ace/ünï.txt", "unicode v2\n");
        Write(dir, "other.txt", "other v2\n");
        Write(dir, "empty.txt", string.Empty);
        RunGit(dir, "add", "mod.txt", "new.txt", "sp ace/ünï.txt", "other.txt", "empty.txt");
        RunGit(dir, "rm", "--quiet", "del.txt", "dir/last.txt");
        if (stageModeChange)
        {
            RunGit(dir, "update-index", "--chmod=+x", "exe.sh");
        }
    }

    private static readonly string[] ListedPaths = ["mod.txt", "new.txt", "del.txt", "exe.sh", "sp ace/ünï.txt", "dir/last.txt", "empty.txt"];

    [Test]
    public void HeadPlusPaths_TreeEqualsGitCommitOnly_AddModifyDeleteUnicodeEmptyFileAndEmptiedDirectory()
    {
        var ours = NewRepo(_root, "paths-ours");
        var twin = NewRepo(_root, "paths-twin");
        foreach (var dir in new[] { ours, twin })
        {
            ArrangeBaseline(dir);
            ArrangeStagedChanges(dir, stageModeChange: false);
        }

        var result = CommitPaths(ours, "partial\n", ListedPaths);
        RunGit(twin, ["--literal-pathspecs", "commit", "--quiet", "-m", "partial", "--only", "--", .. ListedPaths]);

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        AssertSameHeadTree(ours, twin);
        Assert.That(RunGit(ours, "ls-tree", "HEAD", "other.txt"), Is.EqualTo(RunGit(ours, "ls-tree", "HEAD~1", "other.txt")), "an unlisted staged change must not be committed.");
        Assert.That(RunGit(ours, "ls-tree", "-r", "--name-only", "HEAD"), Does.Not.Contain("dir/last.txt"));
        Assert.That(RunGit(ours, "status", "--porcelain"), Is.EqualTo(RunGit(twin, "status", "--porcelain")), "git status afterwards matches the CLI's.");
        AssertNoLockLeft(ours);
        AssertFsckClean(ours);
    }

    [Test]
    public void HeadPlusPaths_ADirectoryPathCoversEverythingUnderIt()
    {
        var ours = NewRepo(_root, "dirpath-ours");
        var twin = NewRepo(_root, "dirpath-twin");
        foreach (var dir in new[] { ours, twin })
        {
            Write(dir, "d/one.txt", "1\n");
            Write(dir, "d/sub/two.txt", "2\n");
            Write(dir, "z.txt", "z\n");
            GitTestRepo.CommitAll(dir, "baseline");
            Write(dir, "d/one.txt", "1b\n");
            Write(dir, "d/sub/three.txt", "3\n");
            Write(dir, "z.txt", "zb\n");
            RunGit(dir, "add", "-A");
            RunGit(dir, "rm", "--quiet", "d/sub/two.txt");
        }

        var result = CommitPaths(ours, "dir\n", "d");
        RunGit(twin, "--literal-pathspecs", "commit", "--quiet", "-m", "dir", "--only", "--", "d");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        AssertSameHeadTree(ours, twin);
    }

    [Test]
    public void WholeIndex_TreeEqualsGitCommit()
    {
        var ours = NewRepo(_root, "whole-ours");
        var twin = NewRepo(_root, "whole-twin");
        foreach (var dir in new[] { ours, twin })
        {
            ArrangeBaseline(dir);
            ArrangeStagedChanges(dir, stageModeChange: true);
        }

        var result = CommitAll(ours, "everything\n");
        RunGit(twin, "commit", "--quiet", "-m", "everything");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        AssertSameHeadTree(ours, twin);
        Assert.That(RunGit(ours, "status", "--porcelain"), Is.EqualTo(RunGit(twin, "status", "--porcelain")));
        AssertFsckClean(ours);
    }

    [Test]
    public void WholeIndex_WithAGitlinkEntry_CommitsIt()
    {
        var ours = NewRepo(_root, "gitlink-ours");
        var twin = NewRepo(_root, "gitlink-twin");
        foreach (var dir in new[] { ours, twin })
        {
            RunGit(dir, "update-index", "--add", "--cacheinfo", "160000,1111111111111111111111111111111111111111,sub");
        }

        var result = CommitAll(ours, "gitlink\n");
        RunGit(twin, "commit", "--quiet", "-m", "gitlink");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        Assert.That(RunGit(ours, "ls-tree", "HEAD", "sub"), Does.StartWith("160000 commit"));
        AssertSameHeadTree(ours, twin);
    }

    // ---- 7b. declines ----

    [Test]
    public void HeadPlusPaths_Declines_ForAGitlinkAnIntentToAddEntryAndUnstagedWorkingTreeChanges()
    {
        var dir = NewRepo(_root, "decline-paths");
        var first = Tip(dir);
        RunGit(dir, "update-index", "--add", "--cacheinfo", $"160000,{first},sub");
        AssertDeclined(CommitPaths(dir, "x\n", "sub"), "gitlink");
        RunGit(dir, "rm", "--quiet", "--cached", "sub");

        // Working tree differs from the index at a listed path: git commit --only would commit the working tree, so the caller stages first.
        Write(dir, "a.txt", "changed in the working tree only\n");
        AssertDeclined(CommitPaths(dir, "x\n", "a.txt"), "stage it first");
        RunGit(dir, "checkout", "--quiet", "--", "a.txt");

        Write(dir, "ita.txt", "intent\n");
        RunGit(dir, "add", "-N", "ita.txt");
        AssertDeclined(CommitPaths(dir, "x\n", "ita.txt"), "intent-to-add");
        AssertDeclined(CommitAll(dir, "x\n"), "intent-to-add");

        Assert.That(Tip(dir), Is.EqualTo(first));
        AssertNoLockLeft(dir);
    }

    [Test]
    public void HeadPlusPaths_Declines_ForAStagedModeChange_WhenFilemodeIsOff()
    {
        var dir = NewRepo(_root, "decline-filemode");
        RunGit(dir, "config", "core.filemode", "false");
        var first = Tip(dir);
        Write(dir, "tool.sh", "#!/bin/sh\n");
        RunGit(dir, "add", "tool.sh");
        RunGit(dir, "update-index", "--chmod=+x", "tool.sh");

        // git commit --only would commit tool.sh as 100644 (no executable bit is visible in the working tree), the index says 100755.
        AssertDeclined(CommitPaths(dir, "x\n", "tool.sh"), "core.filemode");
        Assert.That(CommitAll(dir, "whole index keeps the staged mode\n").Outcome, Is.EqualTo(CasCommitOutcome.Committed));
        Assert.That(RunGit(dir, "ls-tree", "HEAD", "tool.sh"), Does.StartWith("100755 blob"));
        Assert.That(Tip(dir), Is.Not.EqualTo(first));
    }

    [Test]
    public void HeadPlusPaths_ADifferentUnlistedWorkingTreeChange_DoesNotDecline()
    {
        var dir = NewRepo(_root, "unlisted-change");
        Write(dir, "b.txt", "b\n");
        RunGit(dir, "add", "b.txt");
        Write(dir, "a.txt", "unrelated unstaged edit\n");

        var result = CommitPaths(dir, "b only\n", "b.txt");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        Assert.That(RunGit(dir, "ls-tree", "-r", "--name-only", "HEAD").Split('\n'), Is.EquivalentTo(new[] { "a.txt", "b.txt" }));
    }

    [Test]
    public void HeadPlusPaths_Declines_WhenAListedFileDiffersInTheWorkingTree()
    {
        var dir = NewRepo(_root, "decline-listed-file");
        Write(dir, "a.txt", "committed\n");
        GitTestRepo.CommitAll(dir, "baseline");
        Write(dir, "a.txt", "staged\n");
        RunGit(dir, "add", "a.txt");
        Write(dir, "a.txt", "changed in the working tree only\n");

        AssertDeclined(CommitPaths(dir, "x\n", "a.txt"), "differs between the working tree and the index");
    }

    [Test]
    public void HeadPlusPaths_Declines_WhenAFileUnderAListedDirectoryDiffersInTheWorkingTree()
    {
        var dir = NewRepo(_root, "decline-listed-dir");
        Write(dir, "src/b.txt", "committed\n");
        GitTestRepo.CommitAll(dir, "baseline");
        Write(dir, "src/b.txt", "changed in the working tree only\n");

        AssertDeclined(CommitPaths(dir, "x\n", "src"), "differs between the working tree and the index");
    }

    [Test]
    public void HeadPlusPaths_IgnoresAWorkingTreeChange_InASiblingSharingTheListedPrefix()
    {
        var dir = NewRepo(_root, "sibling-prefix");
        Write(dir, "src/a.txt", "v1\n");
        Write(dir, "src/a.txt.bak", "bak v1\n");
        GitTestRepo.CommitAll(dir, "baseline");
        Write(dir, "src/a.txt", "v2\n");
        RunGit(dir, "add", "src/a.txt");
        Write(dir, "src/a.txt.bak", "bak changed in the working tree only\n");

        var result = CommitPaths(dir, "a only\n", "src/a.txt");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        AssertNoLockLeft(dir);
    }

    [Test]
    public void HeadPlusPaths_Declines_WhenAListedPathWithGlobCharactersDiffersInTheWorkingTree()
    {
        var dir = NewRepo(_root, "decline-glob-literal");
        Write(dir, "f[1].txt", "committed\n");
        GitTestRepo.CommitAll(dir, "baseline");
        Write(dir, "f[1].txt", "changed in the working tree only\n");

        AssertDeclined(CommitPaths(dir, "x\n", "f[1].txt"), "differs between the working tree and the index");
    }

    [Test]
    public void HeadPlusPaths_Declines_ForBadPaths()
    {
        var dir = NewRepo(_root, "bad-paths");
        Assert.Multiple(() =>
        {
            AssertDeclined(Libgit2GitReader.CommitWithCompareAndSwap(dir, "x\n", CasCommitTreeSource.HeadPlusPaths, null), "at least one path");
            AssertDeclined(CommitPaths(dir, "x\n"), "at least one path");
            AssertDeclined(CommitPaths(dir, "x\n", "./a.txt"), "clean repo-relative");
            AssertDeclined(CommitPaths(dir, "x\n", "a//b"), "clean repo-relative");
            AssertDeclined(CommitPaths(dir, "x\n", "nothing-here.txt"), "matches nothing");
        });
        AssertNoLockLeft(dir);
    }

    [Test]
    public void UnmergedEntries_AndAnInProgressMerge_AreDeclined()
    {
        var dir = NewRepo(_root, "decline-merge");
        var first = Tip(dir);
        RunGit(dir, "checkout", "--quiet", "-b", "side");
        Write(dir, "a.txt", "side\n");
        GitTestRepo.CommitAll(dir, "side");
        RunGit(dir, "checkout", "--quiet", "master");
        Write(dir, "a.txt", "master\n");
        GitTestRepo.CommitAll(dir, "master edit");
        var tipBefore = Tip(dir);
        Assert.That(RunGitRaw(dir, "merge", "side").ExitCode, Is.Not.Zero, "the merge must conflict.");

        AssertDeclined(CommitAll(dir, "x\n"), "merge is in progress");

        // Without MERGE_HEAD the conflicted entry itself is what is declined.
        File.Delete(Path.Combine(dir, ".git", "MERGE_HEAD"));
        AssertDeclined(CommitAll(dir, "x\n"), "unmerged");
        AssertDeclined(CommitPaths(dir, "x\n", "a.txt"), "unmerged");

        Assert.That(Tip(dir), Is.EqualTo(tipBefore));
        Assert.That(tipBefore, Is.Not.EqualTo(first));
        AssertNoLockLeft(dir);
    }

    [Test]
    public void IdentityGitWouldTreatDifferently_IsDeclined()
    {
        var dir = NewRepo(_root, "decline-identity");
        Write(dir, "b.txt", "b\n");
        RunGit(dir, "add", "b.txt");
        var first = Tip(dir);

        RunGit(dir, "config", "author.name", "Someone Else");
        AssertDeclined(CommitAll(dir, "x\n"), "author.name");
        RunGit(dir, "config", "--unset", "author.name");

        RunGit(dir, "config", "user.name", "Bad <Name>");
        AssertDeclined(CommitAll(dir, "x\n"), "characters git strips");
        RunGit(dir, "config", "user.name", "Test User");

        Assert.That(CommitAll(dir, "x\n").Outcome, Is.EqualTo(CasCommitOutcome.Committed), "the control with a plain identity commits.");
        Assert.That(Tip(dir), Is.Not.EqualTo(first));
    }

    [Test]
    public void MessageWithANulByte_IsDeclined()
    {
        var dir = NewRepo(_root, "decline-nul");
        Write(dir, "b.txt", "b\n");
        RunGit(dir, "add", "b.txt");

        AssertDeclined(CommitAll(dir, "a\0b\n"), "NUL");
        AssertNoLockLeft(dir);
    }

    // ---- 8. nothing to commit ----

    [Test]
    public void NothingStaged_ReturnsNothingToCommit_AndTouchesNothing()
    {
        var dir = NewRepo(_root, "nothing");
        var first = Tip(dir);

        var whole = CommitAll(dir, "x\n");
        var listed = CommitPaths(dir, "x\n", "a.txt");

        Assert.That(whole.Outcome, Is.EqualTo(CasCommitOutcome.NothingToCommit), whole.ToString());
        Assert.That(listed.Outcome, Is.EqualTo(CasCommitOutcome.NothingToCommit), listed.ToString());
        Assert.That(Tip(dir), Is.EqualTo(first));
        Assert.That(Reflog(dir, "HEAD"), Has.Length.EqualTo(1));
        AssertNoLockLeft(dir);
    }

    [Test]
    public void UnbornBranchWithAnEmptyIndex_ReturnsNothingToCommit()
    {
        var dir = NewRepo(_root, "nothing-unborn", withInitialCommit: false);

        var result = CommitAll(dir, "x\n");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.NothingToCommit), result.ToString());
        Assert.That(RunGitRaw(dir, "rev-parse", "--verify", "--quiet", "refs/heads/master").ExitCode, Is.Not.Zero, "no branch may be created.");
        AssertNoLockLeft(dir);
    }

    // ---- 9. linked worktree ----

    [Test]
    public void LinkedWorktree_UsesItsOwnIndexLock_HeadAndBranch_AndWritesTheCliReflog()
    {
        string Arrange(string mainName, string worktreeName, out string main)
        {
            main = NewRepo(_root, mainName);
            var worktree = Path.Combine(_root, worktreeName);
            RunGit(main, "worktree", "add", "--quiet", "-b", "wtbranch", worktree);
            Write(worktree, "w.txt", "w\n");
            RunGit(worktree, "add", "w.txt");
            return worktree;
        }

        var wt = Arrange("wt-main", "wt-linked", out var main);
        var twinWt = Arrange("wt-main-twin", "wt-linked-twin", out var twinMain);
        var masterBefore = Resolve(main, "refs/heads/master");

        // The worktree's own index.lock (under .git/worktrees/<name>) blocks it; the main working tree's lock does not.
        var worktreeLock = Path.Combine(main, ".git", "worktrees", "wt-linked", "index.lock");
        File.WriteAllText(worktreeLock, "held");
        Assert.That(CommitAll(wt, "w\n").Outcome, Is.EqualTo(CasCommitOutcome.LockContention));
        File.Delete(worktreeLock);

        File.WriteAllText(IndexLockPath(main), "held");
        var result = CommitAll(wt, "w\n");
        File.Delete(IndexLockPath(main));
        RunGit(twinWt, "commit", "--quiet", "-m", "w");

        Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Committed), result.ToString());
        Assert.That(Resolve(main, "refs/heads/wtbranch"), Is.EqualTo(result.CommitId));
        Assert.That(Resolve(wt, "HEAD"), Is.EqualTo(result.CommitId));
        Assert.That(Resolve(main, "refs/heads/master"), Is.EqualTo(masterBefore), "the main working tree's branch must not move.");
        Assert.That(Reflog(wt, "HEAD")[0], Is.EqualTo(Reflog(twinWt, "HEAD")[0]), "the worktree's HEAD reflog entry (identity from the main repository's config).");
        Assert.That(Reflog(main, "refs/heads/wtbranch")[0], Is.EqualTo(Reflog(twinMain, "refs/heads/wtbranch")[0]));
        AssertSameHeadTree(wt, twinWt);
        AssertFsckClean(main);
    }
}
