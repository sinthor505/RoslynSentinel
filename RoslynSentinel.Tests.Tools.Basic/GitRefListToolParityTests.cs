using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Runs the Git tool's branch list (<see cref="GitImpl.BranchAsync"/> without a branch name), stash list and stash apply
/// (<see cref="GitImpl.StashAsync"/>, which counts entries around the apply) with a libgit2-only reader and a CLI-only reader on throwaway repos and
/// asserts the response DTOs serialize identically. A case libgit2 declines (a detached HEAD, branch.sort) goes through the production composite: the test asserts the CLI answered and that its DTO equals the CLI-only one. Neither
/// single-backend reader can fall back, so a libgit2 failure fails the test. Tag list and worktree list are not covered because they stay on the CLI.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitRefListToolParityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private string _root = null!;
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-gitrefs-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        InitRepo(_repo);
        RunGit(_repo, "config", "core.autocrlf", "false");
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

    // ---- helpers ----

    private static GitImpl Impl(IGitReader reader) =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, reader);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static readonly string[] Identity = ["-c", "user.name=Test", "-c", "user.email=test@example.com"];

    /// <summary>git with extra environment variables on this child only (committer date), failing on a non-zero exit.</summary>
    private static string RunGitEnv(string directory, IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (name, value) in environment)
        {
            psi.Environment[name] = value;
        }

        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', args)} timed out in '{directory}'.");
        }

        Task.WaitAll(stdOut, stdErr);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}) in '{directory}': {stdErr.Result.Trim()}");
        }

        return stdOut.Result.Trim();
    }

    private static string Commit(string directory, string message)
    {
        RunGit(directory, [.. Identity, "commit", "--quiet", "--allow-empty", "-m", message]);
        return RunGit(directory, "rev-parse", "HEAD");
    }

    private void CommitBaseline()
    {
        Write(_repo, "a.txt", "a\n");
        Write(_repo, "b.txt", "b\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "baseline");
    }

    /// <summary>Stashes a fresh change to a.txt (and optionally an untracked file) under <paramref name="message"/>, with an optional committer date.</summary>
    private void PushStash(string? message, bool includeUntracked = false, string? committerDate = null, string? directory = null)
    {
        var where = directory ?? _repo;
        Write(where, "a.txt", $"change {Guid.NewGuid():N}\n");
        string[] args = [.. Identity, "stash", "push", "--quiet"];
        if (includeUntracked)
        {
            Write(where, $"untracked-{Guid.NewGuid():N}.txt", "u\n");
            args = [.. args, "--include-untracked"];
        }

        if (message is not null)
        {
            args = [.. args, "-m", message];
        }

        RunGitEnv(where, committerDate is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["GIT_COMMITTER_DATE"] = committerDate }, args);
    }

    /// <summary>Branch list through the libgit2-only and the CLI-only reader; asserts identical DTOs and returns the CLI-backed result.</summary>
    private static async Task<GitBranchResult> BranchesBothAsync(string gitRoot)
    {
        var lib = await Impl(new Libgit2GitReader()).BranchAsync(gitRoot, null, null, false, CancellationToken.None);
        var cli = await Impl(new CliGitReader()).BranchAsync(gitRoot, null, null, false, CancellationToken.None);
        Assert.That(Serialize(lib), Is.EqualTo(Serialize(cli)), $"libgit2-backed branch list differs from the CLI-backed one.\nlibgit2: {Serialize(lib)}\ncli:     {Serialize(cli)}");
        return cli;
    }

    /// <summary>A case libgit2 declines: the composite answers from the CLI (asserted) with the CLI-only reader's DTO.</summary>
    private static async Task<GitBranchResult> BranchesCliRoutedAsync(string gitRoot)
    {
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var viaComposite = await Impl(composite).BranchAsync(gitRoot, null, null, false, CancellationToken.None);
        var viaCli = await Impl(new CliGitReader()).BranchAsync(gitRoot, null, null, false, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli), "libgit2 should have declined this case (reason: " + composite.FirstFallbackReason + ")");
            Assert.That(composite.FirstFallbackReason, Does.StartWith("NotSupportedException"));
            Assert.That(Serialize(viaComposite), Is.EqualTo(Serialize(viaCli)));
        });
        return viaCli;
    }

    private static Task<GitStashResult> StashList(IGitReader reader, string gitRoot, int count = 50) =>
        Impl(reader).StashAsync(gitRoot, GitAction.list, null, false, null, null, count, CancellationToken.None);

    private static async Task<GitStashResult> StashesBothAsync(string gitRoot, int count = 50)
    {
        var lib = await StashList(new Libgit2GitReader(), gitRoot, count);
        var cli = await StashList(new CliGitReader(), gitRoot, count);
        Assert.That(Serialize(lib), Is.EqualTo(Serialize(cli)), $"libgit2-backed stash list differs from the CLI-backed one.\nlibgit2: {Serialize(lib)}\ncli:     {Serialize(cli)}");
        return cli;
    }

    private static string[] Names(GitBranchResult result) => [.. result.Branches.Select(b => (b.IsCurrent ? "*" : "") + (b.IsRemote ? "remote:" : "") + b.Name)];

    // ---- branch list ----

    [Test]
    public async Task Branches_SlashNonAsciiAndMixedCaseNames_LooseAndPacked_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        foreach (var name in new[] { "feature/x", "feature/ü-日本", "Zeta", "alpha", "a-b", "a.b", "a/b", "topic/\U0001F600", "topic/Ａ" })
        {
            RunGit(_repo, "branch", name);
        }

        RunGit(_repo, "pack-refs", "--all"); // half of the refs end up packed, the rest loose
        foreach (var name in new[] { "loose/one", "loose/Two", "Ärger" })
        {
            RunGit(_repo, "branch", name);
        }

        var result = await BranchesBothAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(result.Branches, Has.Count.EqualTo(13), "twelve created plus the current branch");
            Assert.That(Names(result), Does.Contain("topic/\U0001F600"));
            Assert.That(result.Branches.Count(b => b.IsCurrent), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Branches_OnlyThePackedCurrentBranch_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        RunGit(_repo, "pack-refs", "--all");

        var result = await BranchesBothAsync(_repo);

        Assert.That(result.Branches, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Branches_RemoteTrackingAndSymbolicHead_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        var head = RunGit(_repo, "rev-parse", "HEAD");
        foreach (var reference in new[] { "refs/remotes/origin/main", "refs/remotes/origin/feature/x", "refs/remotes/origin/ü", "refs/remotes/upstream/main" })
        {
            RunGit(_repo, "update-ref", reference, head);
        }

        RunGit(_repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        RunGit(_repo, "branch", "local-only");
        RunGit(_repo, "pack-refs", "--all");
        RunGit(_repo, "update-ref", "refs/remotes/origin/after-pack", head);

        var result = await BranchesBothAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(Names(result), Does.Contain("remote:origin/feature/x"));
            Assert.That(Names(result), Has.None.Contains("HEAD"), "the symbolic origin/HEAD row is skipped");
            Assert.That(result.Branches.Count(b => b.IsRemote), Is.EqualTo(5));
        });
    }

    [Test]
    public async Task Branches_CheckedOutInAnotherWorktree_IsNotMarkedCurrentAsync()
    {
        CommitBaseline();
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");

        var primary = await BranchesBothAsync(_repo);
        var inWorktree = await BranchesBothAsync(worktree);

        Assert.Multiple(() =>
        {
            Assert.That(primary.Branches.Single(b => b.IsCurrent).Name, Is.Not.EqualTo("wt-branch"));
            Assert.That(inWorktree.Branches.Single(b => b.IsCurrent).Name, Is.EqualTo("wt-branch"));
            Assert.That(primary.Branches.Single(b => b.Name == "wt-branch").IsCurrent, Is.False, "git marks it with '+', which the tool does not report as current");
        });
    }

    [Test]
    public async Task Branches_UnbornBranch_BothBackendsAgreeAsync()
    {
        var result = await BranchesBothAsync(_repo);

        Assert.That(result.Branches, Is.Empty);
    }

    [Test]
    public async Task Branches_DetachedHead_IsAnsweredByTheCliAsync()
    {
        CommitBaseline();
        Commit(_repo, "second");
        RunGit(_repo, "checkout", "--quiet", "--detach", "HEAD~1");

        var result = await BranchesCliRoutedAsync(_repo);

        Assert.That(Names(result).Any(n => n.StartsWith("*(HEAD detached at ", StringComparison.Ordinal)), Is.True, string.Join(" | ", Names(result)));
    }

    [Test]
    public async Task Branches_ConfiguredBranchSort_IsAnsweredByTheCliAsync()
    {
        CommitBaseline();
        RunGit(_repo, "branch", "zzz");
        RunGit(_repo, "branch", "aaa");
        RunGit(_repo, "config", "branch.sort", "-refname");

        var result = await BranchesCliRoutedAsync(_repo);

        Assert.That(result.Branches.First().Name, Is.EqualTo("zzz"), "git honours branch.sort, which the libgit2 reader leaves to it");
    }

    // ---- stash list ----

    [Test]
    public async Task Stashes_NoStash_BothBackendsAgreeAsync()
    {
        CommitBaseline();

        var result = await StashesBothAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(result.Entries, Is.Empty);
        });
    }

    [Test]
    public async Task Stashes_WipLabelledAndUntrackedEntries_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        PushStash(null);                                                                     // "WIP on <branch>: <hash> <subject>"
        PushStash("labelled stash");                                                          // "On <branch>: <label>"
        PushStash("with untracked é 日本語 \U0001F600", includeUntracked: true); // non-ASCII label, untracked file in the stash
        RunGit(_repo, "checkout", "--quiet", "-b", "feature/other");
        PushStash("on another branch");

        var result = await StashesBothAsync(_repo);
        var truncated = await StashesBothAsync(_repo, count: 2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Entries, Has.Count.EqualTo(4));
            Assert.That(result.Entries.Select(e => e.Index), Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(result.Entries[0].Branch, Is.EqualTo("feature/other"));
            Assert.That(result.Entries[0].Message, Is.EqualTo("on another branch"));
            Assert.That(result.Entries[1].Message, Does.Contain("日本語"));
            Assert.That(result.Entries[3].Message, Does.Contain("baseline"), "the WIP subject carries the commit subject");
            Assert.That(truncated.Entries, Has.Count.EqualTo(2));
            Assert.That(truncated.IsTruncated, Is.True);
        });
    }

    [Test]
    public async Task Stashes_CommitterOffsetsZeroPositiveAndNegative_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        PushStash("utc", committerDate: "2020-12-31T00:00:00+0000");
        PushStash("plus", committerDate: "2021-03-04T05:06:07+0530");
        PushStash("minus", committerDate: "2021-06-07T23:59:59-0800");

        var result = await StashesBothAsync(_repo);

        Assert.That(result.Entries.Select(e => e.Date), Is.EqualTo(new[]
        {
            "2021-06-07T23:59:59-08:00",
            "2021-03-04T05:06:07+05:30",
            result.Entries[2].Date, // a zero offset is "Z" on current git and "+00:00" on older git; both backends already agreed above
        }));
        Assert.That(result.Entries[2].Date, Is.AnyOf("2020-12-31T00:00:00Z", "2020-12-31T00:00:00+00:00"));
    }

    [Test]
    public async Task Stashes_LinkedWorktreeSharesTheRepositorysStashesAsync()
    {
        CommitBaseline();
        PushStash("from primary");
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        PushStash("from worktree", directory: worktree);

        var inWorktree = await StashesBothAsync(worktree);
        var inPrimary = await StashesBothAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(inWorktree.Entries, Has.Count.EqualTo(2));
            Assert.That(Serialize(inPrimary), Is.EqualTo(Serialize(inWorktree)));
        });
    }

    [Test]
    public async Task LogAndStashList_RepositoryWithAMultiPackIndex_AreServedByLibgit2AndEqualTheCliAsync()
    {
        // git's abbreviation length used to be estimated from the packed object count, which a multi-pack-index makes unreproducible, so this repository
        // (like the real one) went to the CLI. The length is now probed from git itself, so libgit2 serves both and the rows equal the CLI's.
        CommitBaseline();
        RunGit(_repo, "repack", "-d", "-q");
        Commit(_repo, "second");
        Write(_repo, "c.txt", "c\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "third");
        RunGit(_repo, "repack", "-d", "-q");
        RunGit(_repo, "multi-pack-index", "write");
        Assert.That(File.Exists(Path.Combine(_repo, ".git", "objects", "pack", "multi-pack-index")), Is.True, "the fixture needs a multi-pack-index");
        PushStash("after midx");
        PushStash("second after midx");

        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var libgit2Before = composite.Libgit2Count;
        var impl = Impl(composite);
        var stashViaComposite = await StashList(composite, _repo);
        var logViaComposite = await impl.LogAsync(_repo, 20, null, null, null, false, false, CancellationToken.None);
        var stashViaCli = await StashList(new CliGitReader(), _repo);
        var logViaCli = await Impl(new CliGitReader()).LogAsync(_repo, 20, null, null, null, false, false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(composite.FirstFallbackReason, Is.Null, "libgit2 must serve a multi-pack-index repository now; a CLI answer means a decline");
            Assert.That(composite.CliFallbackCount, Is.Zero);
            Assert.That(composite.Libgit2Count - libgit2Before, Is.GreaterThanOrEqualTo(2), "one libgit2 call each for the stash list and the log");
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
            Assert.That(Serialize(stashViaComposite), Is.EqualTo(Serialize(stashViaCli)));
            Assert.That(Serialize(logViaComposite), Is.EqualTo(Serialize(logViaCli)));
            Assert.That(stashViaCli.Entries, Has.Count.EqualTo(2));
            Assert.That(logViaCli.Commits, Has.Count.GreaterThanOrEqualTo(3));
        });
    }

    // One setting per test case (a fresh repository each): the probed abbreviation length is cached per repository, so editing core.abbrev mid-test would be
    // the accepted staleness. "auto" is the same as unset to git.
    [TestCase("7", 7)]
    [TestCase("10", 10)]
    [TestCase("12", 12)]
    [TestCase("40", 40)]
    [TestCase("auto", 7)]
    public async Task Stashes_AcrossCoreAbbrevSettings_BothBackendsAgreeAsync(string setting, int expectedLength)
    {
        CommitBaseline();
        RunGit(_repo, "config", "core.abbrev", setting);
        PushStash("one");
        PushStash("two");

        var result = await StashesBothAsync(_repo);

        Assert.That(result.Entries, Has.Count.EqualTo(2));
        Assert.That(result.Entries.Select(e => e.Hash.Length), Is.All.EqualTo(expectedLength));
    }

    // ---- stash count and apply ----

    [Test]
    public void CountStashes_ZeroOneAndMany_BothReadersAgree()
    {
        CommitBaseline();
        var counts = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var lib = new Libgit2GitReader().CountStashes(_repo, Timeout);
            var cli = new CliGitReader().CountStashes(_repo, Timeout);
            counts.Add($"{lib}/{cli}");
            Assert.That(lib, Is.EqualTo(cli));
            PushStash($"stash {i}", includeUntracked: i % 2 == 1);
        }

        Assert.That(counts, Is.EqualTo(new[] { "0/0", "1/1", "2/2", "3/3" }));
    }

    [Test]
    public void CountStashes_NotARepository_IsNullFromBothReaders()
    {
        var empty = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(empty);

        Assert.Multiple(() =>
        {
            Assert.That(new Libgit2GitReader().CountStashes(empty, Timeout), Is.Null);
            Assert.That(new CliGitReader().CountStashes(empty, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().ListStashes(empty, Timeout), Is.Null);
            Assert.That(new CliGitReader().ListStashes(empty, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().ListBranches(empty, Timeout), Is.Null);
            Assert.That(new CliGitReader().ListBranches(empty, Timeout), Is.Null);
        });
    }

    [Test]
    public async Task Stashes_ApplyThenCount_BothBackendsReportTheSameResultAsync()
    {
        CommitBaseline();
        PushStash("to apply", includeUntracked: true);

        var lib = Serialize(await Impl(new Libgit2GitReader()).StashAsync(_repo, GitAction.apply, null, false, null, 0, 20, CancellationToken.None));
        RunGit(_repo, "checkout", "--quiet", "--", ".");
        RunGit(_repo, "clean", "--quiet", "-fd");
        var cli = Serialize(await Impl(new CliGitReader()).StashAsync(_repo, GitAction.apply, null, false, null, 0, 20, CancellationToken.None));

        Assert.That(lib, Is.EqualTo(cli), $"libgit2-backed apply differs from the CLI-backed one.\nlibgit2: {lib}\ncli:     {cli}");
        Assert.That(cli, Does.Contain("\"IsError\":false"));
    }

    // ---- which backend serves ----

    [Test]
    public async Task BranchAndStashLists_InTheNormalEnvironment_AreServedByLibgit2NotTheFallbackAsync()
    {
        CommitBaseline();
        RunGit(_repo, "branch", "feature/x");
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var impl = Impl(composite);

        var branches = await impl.BranchAsync(_repo, null, null, false, CancellationToken.None);
        var emptyStashes = await impl.StashAsync(_repo, GitAction.list, null, false, null, null, 20, CancellationToken.None);
        PushStash("one");
        var count = composite.CountStashes(_repo, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(branches.Branches, Has.Count.EqualTo(2));
            Assert.That(emptyStashes.Entries, Is.Empty);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(composite.FirstFallbackReason, Is.Null, "libgit2 must serve these reads; a CLI answer means a silent fallback");
            Assert.That(composite.CliFallbackCount, Is.Zero);
            Assert.That(composite.Libgit2Count, Is.EqualTo(3));
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
        });
    }

    [Test]
    public async Task StashList_WithEntriesInANormalRepository_IsServedByLibgit2Async()
    {
        CommitBaseline();
        PushStash("one");
        PushStash("two");
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });

        var result = await StashList(composite, _repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.Entries, Has.Count.EqualTo(2));
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2), composite.FirstFallbackReason);
            Assert.That(composite.CliFallbackCount, Is.Zero);
        });
    }

    [Test]
    public async Task BranchList_ThroughTheProductionDefaultReader_CountsALibgit2CallAsync()
    {
        CommitBaseline();

        var before = GitReaders.Default.Libgit2Count;
        var result = await new GitImpl(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly)).BranchAsync(_repo, null, null, false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(GitReaders.Default.Libgit2Count, Is.GreaterThan(before), "the public GitImpl constructor must route the branch list through GitReaders.Default");
        });
    }
}
