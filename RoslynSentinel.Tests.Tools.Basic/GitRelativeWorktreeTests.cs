using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Repositories whose linked worktrees use relative paths (git 2.48+: <c>worktree.useRelativePaths=true</c>, which makes <c>git worktree add</c> write
/// <c>extensions.relativeWorktrees = true</c> and relative <c>gitdir</c> files). Pins what libgit2 1.8.6 (LibGit2Sharp 0.32.0) does with them: it REJECTS
/// both the main repository and the linked worktree with <c>LibGit2SharpException: unsupported extension name extensions.relativeworktrees</c>, so every
/// <see cref="IGitReader"/> call must reach the CLI through <see cref="FallbackGitReader"/> with the CLI-only answer, visibly (first fallback reason,
/// counters, <see cref="GitBackendWarmup"/> repo probe), and the Git tool's status and log must equal the CLI-only DTOs. A second group removes the extension
/// flag but keeps the relative gitdir files, which libgit2 opens: the relative paths must resolve (common directory, short-hash cache key).
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// When LibGit2Sharp moves to a libgit2 that understands extensions.relativeWorktrees, the "rejects" tests here fail on purpose: re-pin them to parity.
// Every Libgit2GitReader call disposes its Repository, and this fixture opens none directly that outlives a statement, so the delete in TearDown is safe.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitRelativeWorktreeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private string _root = null!;
    private string _repo = null!;
    private string _wt = null!;
    private string _firstSha = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-relwt-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        _wt = Path.Combine(_root, "wt");
        InitRepo(_repo);
        RunGit(_repo, "config", "core.autocrlf", "false");
        RunGit(_repo, "config", "worktree.useRelativePaths", "true");

        Write(_repo, "a.txt", "one\n");
        Write(_repo, "sub/b.txt", "b\n");
        Write(_repo, ".gitignore", "*.log\n");
        _firstSha = CommitAll(_repo, "first");
        Write(_repo, "a.txt", "two\n");
        CommitAll(_repo, "second", "Tool-Call-Id: relwt-1");
        RunGit(_repo, "branch", "side");

        // A stash entry, then dirty state, so ListStashes/CountStashes/status have something to report (stashes are shared by all worktrees).
        Write(_repo, "a.txt", "stashed\n");
        RunGit(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "stash", "push", "--quiet");

        var add = RunGitRaw(_repo, "worktree", "add", "--quiet", _wt, "-b", "linked");
        if (add.ExitCode != 0)
        {
            Assert.Ignore("git worktree add failed (" + add.StdErr.Trim() + ").");
        }

        // Robust to a git without relative worktrees: the extension and a relative gitdir file are the premise of every test here.
        var extension = RunGitRaw(_repo, "config", "--get", "extensions.relativeWorktrees");
        if (extension.ExitCode != 0 || !string.Equals(extension.StdOut.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore("this git does not write extensions.relativeWorktrees for worktree.useRelativePaths (git version: " + RunGit(_repo, "--version") + ").");
        }

        var gitFile = File.ReadAllText(Path.Combine(_wt, ".git")).Trim();
        if (!gitFile.StartsWith("gitdir:", StringComparison.Ordinal) || Path.IsPathRooted(gitFile["gitdir:".Length..].Trim()))
        {
            Assert.Ignore("this git wrote an absolute gitdir path into the linked worktree: " + gitFile);
        }

        Write(_wt, "wt-file.txt", "linked\n");
        CommitAll(_wt, "linked commit");

        // Dirty the main working tree and the linked one: modified, staged, untracked and ignored files.
        Write(_repo, "a.txt", "dirty\n");
        Write(_repo, "staged.txt", "staged\n");
        RunGit(_repo, "add", "staged.txt");
        Write(_repo, "new.txt", "new\n");
        Write(_repo, "x.log", "ignored\n");
        Write(_wt, "wt-untracked.txt", "u\n");
        Write(_wt, "x.log", "ignored\n");
    }

    [TearDown]
    public void TearDown()
    {
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        // git marks pack/object files read-only; clear it so the delete succeeds on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // ---- helpers ----

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Dir(bool linkedWorktree) => linkedWorktree ? _wt : _repo;

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static GitImpl Impl(IGitReader reader) =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, reader);

    /// <summary>Keeps git's relative gitdir/commondir files but drops the extension flag, which makes libgit2 open both repositories.</summary>
    private void DropExtensionFlagKeepRelativePaths()
    {
        RunGit(_repo, "config", "--unset", "extensions.relativeWorktrees");
        RunGit(_repo, "config", "core.repositoryformatversion", "0");
        var status = RunGitRaw(_wt, "status", "--short");
        if (status.ExitCode != 0)
        {
            Assert.Ignore("this git refuses relative worktree paths without extensions.relativeWorktrees: " + status.StdErr.Trim());
        }
    }

    /// <summary>One call per <see cref="IGitReader"/> method (GetHeadSha twice), each chosen so the CLI answer is a real, non-null one.</summary>
    private sealed record Probe(string Label, string Method, Func<IGitReader, string, object?> Call, bool MayBeNull = false);

    private static (int ExitCode, string StdErr) ExitAndStdErr(GitRunResult run) => (run.ExitCode, run.StdErr);

    private Probe[] Probes() =>
    [
        new("GetHeadSha(full)", nameof(IGitReader.GetHeadSha), (r, d) => r.GetHeadSha(d, shortForm: false, Timeout)),
        new("GetHeadSha(short)", nameof(IGitReader.GetHeadSha), (r, d) => r.GetHeadSha(d, shortForm: true, Timeout)),
        new("GetCommitsSince", nameof(IGitReader.GetCommitsSince), (r, d) => r.GetCommitsSince(d, _firstSha, Timeout)),
        new("GetStatus", nameof(IGitReader.GetStatus), (r, d) => r.GetStatus(d, pathspecs: null, Timeout)),
        new("GetRepositoryStatus", nameof(IGitReader.GetRepositoryStatus), (r, d) => r.GetRepositoryStatus(d, Timeout)),
        new("GetLog", nameof(IGitReader.GetLog), (r, d) => r.GetLog(d, 10, revision: null, paths: null, pickaxe: null, pickaxeRegex: false, follow: false, Timeout)),
        new("ReadBlobAtHead", nameof(IGitReader.ReadBlobAtHead), (r, d) => r.ReadBlobAtHead(d, "a.txt", Timeout)),
        new("ListTrackedPaths", nameof(IGitReader.ListTrackedPaths), (r, d) => r.ListTrackedPaths(d, Timeout)),
        new("ListIndexPathsMatching", nameof(IGitReader.ListIndexPathsMatching), (r, d) => r.ListIndexPathsMatching(d, ["a.txt", "sub"], Timeout)),
        new("ListHeadPathsMatching", nameof(IGitReader.ListHeadPathsMatching), (r, d) => r.ListHeadPathsMatching(d, ["a.txt", "sub"], Timeout)),
        new("FindIgnoredPaths", nameof(IGitReader.FindIgnoredPaths), (r, d) => r.FindIgnoredPaths(d, ["x.log", "a.txt"], Timeout)),
        new("ListBranches", nameof(IGitReader.ListBranches), (r, d) => r.ListBranches(d, Timeout)),
        new("ListStashes", nameof(IGitReader.ListStashes), (r, d) => r.ListStashes(d, Timeout)),
        new("CountStashes", nameof(IGitReader.CountStashes), (r, d) => r.CountStashes(d, Timeout)),
        new("GetNameStatus", nameof(IGitReader.GetNameStatus), (r, d) => r.GetNameStatus(d, _firstSha + "..HEAD", paths: null, Timeout)),
        new("GetCommitNameStatus", nameof(IGitReader.GetCommitNameStatus), (r, d) => r.GetCommitNameStatus(d, "HEAD", paths: null, Timeout)),
        // The raw message differs only in its final newline between the readers (libgit2 keeps it, the CLI output is trimmed); the Git tool trims it when it builds its DTO, so compare it trimmed.
        new("GetCommitHeader", nameof(IGitReader.GetCommitHeader), (r, d) => r.GetCommitHeader(d, "HEAD", Timeout) is { } header ? header with { Message = header.Message.TrimEnd('\r', '\n') } : null),
        new("GetCommitMessages", nameof(IGitReader.GetCommitMessages), (r, d) => r.GetCommitMessages(d, Timeout)),
        new("FindCommitByTrailer", nameof(IGitReader.FindCommitByTrailer), (r, d) => r.FindCommitByTrailer(d, "Tool-Call-Id", "relwt-1", 50, Timeout)),
        new("GetBoolConfig", nameof(IGitReader.GetBoolConfig), (r, d) => r.GetBoolConfig(d, "worktree.useRelativePaths", Timeout)),
        new("GetHeadWithParents", nameof(IGitReader.GetHeadWithParents), (r, d) => r.GetHeadWithParents(d, Timeout)),
        // Null: the fixtures are not mid-merge. The fallback and opens-fine walks only need both readers to agree.
        new("GetMergeHead", nameof(IGitReader.GetMergeHead), (r, d) => r.GetMergeHead(d, Timeout), MayBeNull: true),
        // The two index writes: projected to exit code and stderr (a GitRunResult carries a byte array, which does not compare or serialize usefully). a.txt is tracked, so staging it is
        // an idempotent no-op or one more staged file whichever reader runs first; the unstaged path is in neither the index nor HEAD, so that reset changes nothing.
        new("StageWholeFiles", nameof(IGitReader.StageWholeFiles), (r, d) => ExitAndStdErr(r.StageWholeFiles(d, IndexStageMode.AddListed, ["a.txt"], Timeout, CancellationToken.None))),
        new("UnstagePaths", nameof(IGitReader.UnstagePaths), (r, d) => ExitAndStdErr(r.UnstagePaths(d, ["never-existed.txt"], Timeout, CancellationToken.None))),
    ];

    // ---- libgit2 rejects extensions.relativeWorktrees ----

    [Test]
    public void ProbeTable_CoversEveryIGitReaderMethod()
    {
        // A method added to IGitReader without a row here would silently skip the fallback proof below.
        var interfaceMethods = typeof(IGitReader).GetMethods().Select(m => m.Name).Distinct().Order().ToArray();
        var covered = Probes().Select(p => p.Method).Distinct().Order().ToArray();

        Assert.That(covered, Is.EqualTo(interfaceMethods), "add a Probe row for every IGitReader method");
    }

    [Test]
    public void SetUp_ProducesTheRepositoryShapeUnderTest()
    {
        var cli = new CliGitReader();

        Assert.Multiple(() =>
        {
            Assert.That(RunGit(_repo, "config", "--get", "extensions.relativeWorktrees"), Is.EqualTo("true"));
            Assert.That(RunGit(_repo, "config", "--get", "core.repositoryformatversion"), Is.EqualTo("1"));
            Assert.That(File.ReadAllText(Path.Combine(_wt, ".git")), Does.StartWith("gitdir: ").And.Not.Contain(_root), "the gitdir file is relative");
            Assert.That(cli.GetHeadSha(_repo, shortForm: false, Timeout), Is.Not.Null.And.Not.Empty);
            Assert.That(cli.GetHeadSha(_wt, shortForm: false, Timeout), Is.Not.Null.And.Not.EqualTo(cli.GetHeadSha(_repo, shortForm: false, Timeout)), "the worktree has its own HEAD");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Libgit2_RejectsBothTheMainRepositoryAndTheLinkedWorktree_WithUnsupportedExtension(bool linkedWorktree)
    {
        var dir = Dir(linkedWorktree);

        // Repository.Discover only walks to the git directory; it is the open that checks extensions.
        var gitDirectory = LibGit2Sharp.Repository.Discover(dir);
        Assert.That(gitDirectory, Is.Not.Null, "libgit2 finds the git directory");
        var direct = Assert.Throws<LibGit2Sharp.LibGit2SharpException>(() =>
        {
            using var repository = new LibGit2Sharp.Repository(gitDirectory);
        });
        var viaReader = Assert.Throws<LibGit2Sharp.LibGit2SharpException>(() => new Libgit2GitReader().GetHeadSha(dir, shortForm: false, Timeout));

        Assert.Multiple(() =>
        {
            Assert.That(direct!.Message, Does.Contain("unsupported extension name").IgnoreCase.And.Contain("relativeworktrees").IgnoreCase);
            Assert.That(viaReader!.Message, Is.EqualTo(direct.Message));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EveryReaderMethod_FallsBackToTheCli_WithTheCliOnlyAnswer_AndTheFallbackIsVisible(bool linkedWorktree)
    {
        var dir = Dir(linkedWorktree);
        var cli = new CliGitReader();

        foreach (var probe in Probes())
        {
            var logged = new ConcurrentQueue<string>();
            var composite = new FallbackGitReader(new Libgit2GitReader(), cli, logged.Enqueue);

            var viaComposite = Json(probe.Call(composite, dir));
            var viaCli = Json(probe.Call(cli, dir));

            Assert.Multiple(() =>
            {
                if (!probe.MayBeNull)
                {
                    Assert.That(viaCli, Is.Not.EqualTo("null"), probe.Label + ": the CLI must have a real answer for the comparison to mean anything");
                }

                Assert.That(viaComposite, Is.EqualTo(viaCli), probe.Label + ": composite differs from the CLI-only reader");
                Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli), probe.Label);
                Assert.That(composite.CliFallbackCount, Is.EqualTo(1), probe.Label);
            Assert.That(composite.DeclineCount, Is.Zero, probe.Label + ": a repository libgit2 cannot open is a real failure, not a planned decline");
                Assert.That(composite.Libgit2Count, Is.Zero, probe.Label + ": libgit2 must not have answered");
                Assert.That(composite.FirstFallbackReason, Does.StartWith("LibGit2SharpException").And.Contain("relativeworktrees").IgnoreCase, probe.Label);
                Assert.That(logged, Has.Count.EqualTo(1), probe.Label + ": the fallback is logged once, to the log sink");
            });
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GitToolStatusAndLog_OnTheCompositeReader_EqualTheCliOnlyDtos(bool linkedWorktree)
    {
        var dir = Dir(linkedWorktree);
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });

        var statusViaComposite = await Impl(composite).StatusAsync(dir, GitImpl.DefaultStatusMaxEntries, CancellationToken.None);
        var statusViaCli = await Impl(new CliGitReader()).StatusAsync(dir, GitImpl.DefaultStatusMaxEntries, CancellationToken.None);
        var logViaComposite = await Impl(composite).LogAsync(dir, 20, null, null, null, false, false, CancellationToken.None);
        var logViaCli = await Impl(new CliGitReader()).LogAsync(dir, 20, null, null, null, false, false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(statusViaCli.IsError, Is.False);
            Assert.That(statusViaCli.IsClean, Is.False, "the fixture left changed and untracked files");
            Assert.That(statusViaCli.Branch, Is.EqualTo(linkedWorktree ? "linked" : RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD")));
            Assert.That(Json(statusViaComposite), Is.EqualTo(Json(statusViaCli)), "status DTO differs from the CLI-only one");
            Assert.That(logViaCli.IsError, Is.False);
            Assert.That(logViaCli.Commits, Is.Not.Empty);
            Assert.That(Json(logViaComposite), Is.EqualTo(Json(logViaCli)), "log DTO differs from the CLI-only one");
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
            Assert.That(composite.Libgit2Count, Is.Zero, "libgit2 must not have answered any of it");
            Assert.That(composite.CliFallbackCount, Is.GreaterThan(0));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void WarmupRepoProbe_ReportsAFallbackNotOk_AndNeverThrows(bool linkedWorktree)
    {
        var dir = Dir(linkedWorktree);
        var logged = new ConcurrentQueue<string>();
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), logged.Enqueue);
        var warmup = new GitBackendWarmup(
            () => LibGit2Sharp.GlobalSettings.Version.ToString(),
            repoProbe: null,
            composite,
            logged.Enqueue,
            new ShortHashLengths((_, _) => 7));

        warmup.StartRepoProbe(dir);
        _ = composite.GetHeadSha(dir, shortForm: false, Timeout); // a real read, so the snapshot's fallback counters and reason are populated
        var block = warmup.Report(TimeSpan.FromSeconds(30));

        Assert.Multiple(() =>
        {
            Assert.That(warmup.RepoStatus!.IsFaulted, Is.False, "the repo probe task must never fault");
            Assert.That(block.NativeLoad, Is.EqualTo("ok"), "the native library itself loads fine");
            Assert.That(block.RepoProbe, Does.StartWith("fallback: LibGit2SharpException").And.Contain("relativeworktrees").IgnoreCase);
            Assert.That(block.RepoProbe, Is.Not.EqualTo(GitBackendWarmup.RepoOk));
            Assert.That(block.FirstFallbackReason, Does.StartWith("LibGit2SharpException").And.Contain("relativeworktrees").IgnoreCase);
            Assert.That(block.CliFallbackCount, Is.GreaterThan(0));
            Assert.That(block.Libgit2Count, Is.Zero);
            Assert.That(block.ShortHashLength, Is.Null, "the probe never reached the short-hash refresh");
            Assert.That(logged, Is.Not.Empty.And.All.Contain("relativeworktrees").IgnoreCase, "every diagnostic names the extension: " + string.Join(" | ", logged));
        });
    }

    // ---- relative gitdir paths that libgit2 does open (extension flag removed) ----

    [TestCase(false)]
    [TestCase(true)]
    public void RelativeGitdirWithoutTheExtension_Libgit2Opens_EveryMethodAgreesWithTheCli(bool linkedWorktree)
    {
        DropExtensionFlagKeepRelativePaths();
        var dir = Dir(linkedWorktree);
        var cli = new CliGitReader();
        string[] servedByLibgit2 = ["GetHeadSha(full)", "GetStatus", "GetRepositoryStatus", "ListTrackedPaths", "GetCommitMessages", "GetCommitsSince"];

        foreach (var probe in Probes())
        {
            var composite = new FallbackGitReader(new Libgit2GitReader(), cli, _ => { });

            var viaComposite = Json(probe.Call(composite, dir));
            var viaCli = Json(probe.Call(cli, dir));

            Assert.Multiple(() =>
            {
                Assert.That(viaComposite, Is.EqualTo(viaCli), probe.Label + ": composite differs from the CLI-only reader");
                // libgit2 may still decline a request it cannot reproduce (NotSupportedException); it must never fail to open the repository.
                Assert.That(composite.FirstFallbackReason, Is.Null, probe.Label + ": libgit2 failed to open the repository");
                if (servedByLibgit2.Contains(probe.Label))
                {
                    Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2), probe.Label + ": libgit2 should answer this (reason: " + composite.FirstFallbackReason + ")");
                }
            });
        }
    }

    [Test]
    public void RelativeGitdirWithoutTheExtension_GetBoolConfig_InTheLinkedWorktreeIsAnsweredByLibgit2_FromTheMainRepositorysConfig()
    {
        // libgit2 1.8.6 does not read the main repository's .git/config through a linked worktree's Repository.Config (it would answer null for a key that is set there);
        // Libgit2GitReader.ConfigValue reads <commondir>/config explicitly, so libgit2 serves the linked worktree and gives the CLI's answer
        // (see GitWorktreeRepoLocalConfigTests for the per-setting matrix).
        DropExtensionFlagKeepRelativePaths();
        var cli = new CliGitReader();
        var inMain = new FallbackGitReader(new Libgit2GitReader(), cli, _ => { });
        var inLinked = new FallbackGitReader(new Libgit2GitReader(), cli, _ => { });

        var main = inMain.GetBoolConfig(_repo, "worktree.useRelativePaths", Timeout);
        var linked = inLinked.GetBoolConfig(_wt, "worktree.useRelativePaths", Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(main, Is.True);
            Assert.That(inMain.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
            Assert.That(linked, Is.True, "the key is set in the main repository's config, which a linked worktree shares");
            Assert.That(inLinked.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2), inLinked.FirstFallbackReason);
            Assert.That(inLinked.CliFallbackCount, Is.Zero);
        });
    }

    [Test]
    public void RelativeGitdirWithoutTheExtension_MainRepoAndWorktreeShareOneShortHashCacheEntry()
    {
        DropExtensionFlagKeepRelativePaths();
        var probes = new ConcurrentQueue<string>();
        var lengths = new ShortHashLengths((workingDirectory, _) =>
        {
            probes.Enqueue(workingDirectory);
            return 7;
        });
        var lib = new Libgit2GitReader(lengths);
        var cli = new CliGitReader();

        // The worktree's common directory comes from its commondir file ("../.."), relative to its per-worktree git directory: it must normalise to the
        // main repository's .git, or the second call would probe again under a different key.
        var shortMain = lib.GetHeadSha(_repo, shortForm: true, Timeout);
        var shortLinked = lib.GetHeadSha(_wt, shortForm: true, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(probes, Has.Count.EqualTo(1), "one cache entry (one probe) for the repository and its worktree: " + string.Join(" | ", probes));
            Assert.That(shortMain, Is.EqualTo(cli.GetHeadSha(_repo, shortForm: true, Timeout)));
            Assert.That(shortLinked, Is.EqualTo(cli.GetHeadSha(_wt, shortForm: true, Timeout)));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RelativeGitdirWithoutTheExtension_WarmupRepoProbeIsOk_AndLearnsTheShortHashLength(bool linkedWorktree)
    {
        DropExtensionFlagKeepRelativePaths();
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var lengths = new ShortHashLengths((_, _) => 7);
        var warmup = new GitBackendWarmup(() => LibGit2Sharp.GlobalSettings.Version.ToString(), repoProbe: null, composite, log: null, lengths);

        warmup.StartRepoProbe(Dir(linkedWorktree));
        var block = warmup.Report(TimeSpan.FromSeconds(30));

        Assert.Multiple(() =>
        {
            Assert.That(block.RepoProbe, Is.EqualTo(GitBackendWarmup.RepoOk));
            Assert.That(block.ShortHashLength, Is.EqualTo(7));
        });
    }
}
