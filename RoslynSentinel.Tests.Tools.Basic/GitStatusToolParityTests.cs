using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Runs the Git tool's status path (<see cref="GitImpl.StatusAsync"/>, the code behind <c>Git(operation: status)</c>) once with a
/// libgit2-only reader and once with a CLI-only reader on throwaway repos built with
/// <see cref="RoslynSentinel.Tests.Battery.Basic.GitTestRepo"/>, and asserts the response DTOs serialize identically (every field is
/// compared; nothing is ignored, the DTO carries no timings). Neither reader can fall back, so a libgit2 failure fails the test
/// instead of silently passing through the CLI. One test proves the production composite really is served by libgit2.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitStatusToolParityTests
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

        _root = Path.Combine(Path.GetTempPath(), "rs-gitstatus-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        InitRepo(_repo);
        // Pin EOL handling per test: the machine's global autocrlf must not decide the outcome.
        RunGit(_repo, "config", "core.autocrlf", "false");
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

    private static GitImpl Impl(IGitReader reader) =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, reader);

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteBytes(string directory, string relativePath, byte[] content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static (int ExitCode, string StdOut, string StdErr) GitWithIdentity(string directory, params string[] args) =>
        RunGitRaw(directory, ["-c", "user.name=Test", "-c", "user.email=test@example.com", .. args]);

    /// <summary>
    /// Runs status through both backends, asserts the DTOs are deeply equal (compared as JSON, so every serialized field counts)
    /// and returns the CLI-backed DTO for content assertions.
    /// </summary>
    private static async Task<GitStatusResult> BothBackendsAsync(string gitRoot, int maxEntries = GitImpl.DefaultStatusMaxEntries)
    {
        var lib = await Impl(new Libgit2GitReader()).StatusAsync(gitRoot, maxEntries, CancellationToken.None);
        var cli = await Impl(new CliGitReader()).StatusAsync(gitRoot, maxEntries, CancellationToken.None);
        var libJson = JsonSerializer.Serialize(lib);
        var cliJson = JsonSerializer.Serialize(cli);
        Assert.That(libJson, Is.EqualTo(cliJson), $"libgit2-backed status differs from the CLI-backed status.\nlibgit2: {libJson}\ncli:     {cliJson}");
        return cli;
    }

    private static string[] Paths(IEnumerable<GitStatusEntry> entries) => [.. entries.Select(e => e.Path)];

    private static string[] Rows(IEnumerable<GitStatusEntry> entries) =>
        [.. entries.Select(e => $"{e.Status}:{(e.OriginalPath is null ? string.Empty : e.OriginalPath + "->")}{e.Path}")];

    private static void AssertByteOrdered(IReadOnlyList<string> paths)
    {
        var expected = paths.OrderBy(p => p, Comparer<string>.Create((x, y) => Encoding.UTF8.GetBytes(x).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y)))).ToArray();
        Assert.That(paths, Is.EqualTo(expected), "git lists paths in byte (UTF-8) order");
    }

    // ---- cases ----

    [Test]
    public async Task Status_Clean_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");

        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(result.IsClean, Is.True);
            Assert.That(result.InProgress, Is.Null);
            Assert.That(result.Branch, Is.Not.Empty.And.Not.EqualTo("unknown"));
        });
    }

    [Test]
    public async Task Status_StagedUnstagedAndUntrackedMix_BothBackendsAgreeAsync()
    {
        foreach (var name in new[] { "a", "b", "c", "d", "m" })
        {
            Write(_repo, $"{name}.txt", $"{name}\n");
        }

        CommitAll(_repo, "base");
        Write(_repo, "a.txt", "a changed\n");               // " M"
        Write(_repo, "b.txt", "b staged\n");
        RunGit(_repo, "add", "b.txt");
        Write(_repo, "b.txt", "b staged then changed\n");   // "MM"
        File.Delete(Path.Combine(_repo, "c.txt"));          // " D"
        RunGit(_repo, "rm", "--quiet", "d.txt");            // "D "
        Write(_repo, "e.txt", "new staged\n");
        RunGit(_repo, "add", "e.txt");                      // "A "
        Write(_repo, "f.txt", "untracked\n");               // "??"

        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(Rows(result.Staged), Is.EqualTo(new[] { "modified:b.txt", "deleted:d.txt", "added:e.txt" }));
            Assert.That(Rows(result.Unstaged), Is.EqualTo(new[] { "modified:a.txt", "modified:b.txt", "deleted:c.txt" }));
            Assert.That(result.Untracked, Is.EqualTo(new[] { "f.txt" }));
            Assert.That(result.IsClean, Is.False);
        });
    }

    [Test]
    public async Task Status_StagedRename_BothBackendsAgreeAsync()
    {
        Write(_repo, "old name.txt", string.Join('\n', Enumerable.Range(0, 30).Select(i => $"line {i}")) + "\n");
        CommitAll(_repo, "base");
        RunGit(_repo, "mv", "old name.txt", "new name.txt");
        Write(_repo, "new name.txt", string.Join('\n', Enumerable.Range(0, 30).Select(i => $"line {i}")) + "\nextra\n");   // "RM"

        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(Rows(result.Staged), Is.EqualTo(new[] { "renamed:old name.txt->new name.txt" }));
            Assert.That(Rows(result.Unstaged), Is.EqualTo(new[] { "modified:new name.txt" }));
        });
    }

    [Test]
    public async Task Status_UntrackedFileAndDirectory_AreListedIndividuallyAsync()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "base");
        Write(_repo, "loose.txt", "x\n");
        Write(_repo, "dir/one.txt", "1\n");
        Write(_repo, "dir/sub/two.txt", "2\n");
        Directory.CreateDirectory(Path.Combine(_repo, "empty-dir")); // git lists no empty directory

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.Untracked, Is.EqualTo(new[] { "dir/one.txt", "dir/sub/two.txt", "loose.txt" }));
    }

    [Test]
    public async Task Status_IgnoredFiles_AreNotListedAsync()
    {
        Write(_repo, ".gitignore", "*.log\nbuild/\n");
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "base");
        Write(_repo, "debug.log", "noise\n");
        Write(_repo, "build/out/x.txt", "noise\n");
        Write(_repo, "sub/trace.log", "noise\n");
        Write(_repo, "kept.txt", "visible\n");

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.Untracked, Is.EqualTo(new[] { "kept.txt" }));
    }

    [Test]
    public async Task Status_SpacesAndNonAsciiPaths_RoundTripAsync()
    {
        Write(_repo, "base.txt", "b\n");
        CommitAll(_repo, "base");
        Write(_repo, "dir with space/é ü file.txt", "x\n");
        Write(_repo, "日本語.txt", "x\n");
        Write(_repo, "tab\tless.txt".Replace('\t', '_'), "x\n");
        Write(_repo, "quote'single.txt", "x\n");

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.Untracked, Is.EquivalentTo(new[] { "dir with space/é ü file.txt", "日本語.txt", "tab_less.txt", "quote'single.txt" }));
    }

    [Test]
    public async Task Status_ListsFollowGitsByteOrder_NotTheFileSystemsCaseRulesAsync()
    {
        // 'B' < 'Z' < 'a' < 'm' in bytes (a case-insensitive order gives a, B, m, Z); "a.txt" < "a/b.txt" because '.' < '/'; U+FF21 (EF BC A1)
        // sorts before U+1F600 (F0 9F 98 80) in UTF-8 although a UTF-16 ordinal compare puts the surrogate pair first; "é" (C3 A9) sorts
        // after plain ASCII. No two names differ only by case: this runs on a case-insensitive file system.
        string[] names = ["a.txt", "B.txt", "Z.txt", "m.txt", "a/b.txt", "é.txt", "Ａ.txt", "\U0001F600.txt"];
        foreach (var name in names)
        {
            Write(_repo, name, "x\n");
        }

        CommitAll(_repo, "base");
        foreach (var name in names)
        {
            Write(_repo, name, "changed\n");
        }

        foreach (var name in names.Select(n => "new-" + n))
        {
            Write(_repo, name, "x\n");
        }

        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.Unstaged, Has.Count.EqualTo(names.Length));
            AssertByteOrdered(Paths(result.Unstaged));
            Assert.That(result.Untracked, Has.Count.EqualTo(names.Length));
            AssertByteOrdered(result.Untracked);
        });
    }

    [Test]
    public async Task Status_CrlfWithGitAttributes_TextEolAndBinaryAsync()
    {
        Write(_repo, ".gitattributes", "*.txt text eol=crlf\n*.sh text eol=lf\n*.bin binary\n");
        Write(_repo, "doc.txt", "one\ntwo\n");
        Write(_repo, "run.sh", "echo hi\n");
        WriteBytes(_repo, "data.bin", "a\r\nb\r\n"u8.ToArray());
        CommitAll(_repo, "base");

        // eol=crlf: the working copy is CRLF, the index LF -> clean. eol=lf script written with CRLF -> modified.
        // Binary written with different line endings -> modified (no conversion).
        WriteBytes(_repo, "doc.txt", "one\r\ntwo\r\n"u8.ToArray());
        WriteBytes(_repo, "run.sh", "echo hi\r\n"u8.ToArray());
        WriteBytes(_repo, "data.bin", "a\nb\n"u8.ToArray());

        var result = await BothBackendsAsync(_repo);

        Assert.That(Rows(result.Unstaged), Does.Contain("modified:data.bin"));
    }

    [Test]
    public async Task Status_AutocrlfTrue_CrlfWorkingCopyOfLfBlob_BothBackendsAgreeAsync()
    {
        Write(_repo, "doc.txt", "one\ntwo\n");
        CommitAll(_repo, "base");
        RunGit(_repo, "config", "core.autocrlf", "true");
        WriteBytes(_repo, "doc.txt", "one\r\ntwo\r\n"u8.ToArray());
        WriteBytes(_repo, "other.txt", "x\r\n"u8.ToArray());

        // No expectation on the content: git itself calls the CRLF copy of an LF blob modified here; only the agreement matters.
        await BothBackendsAsync(_repo);
    }

    [Test]
    public async Task Status_DetachedHead_BranchIsHeadAsync()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");
        RunGit(_repo, "checkout", "--quiet", "--detach");
        Write(_repo, "a.txt", "changed\n");

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.Branch, Is.EqualTo("HEAD"));
    }

    [Test]
    public async Task Status_UnbornBranch_BranchIsUnknownAndStagedFilesAreAddedAsync()
    {
        Write(_repo, "staged.txt", "s\n");
        RunGit(_repo, "add", "staged.txt");
        Write(_repo, "loose.txt", "l\n");

        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(result.Branch, Is.EqualTo("unknown"));
            Assert.That(Rows(result.Staged), Is.EqualTo(new[] { "added:staged.txt" }));
            Assert.That(result.Untracked, Is.EqualTo(new[] { "loose.txt" }));
        });
    }

    [Test]
    public async Task Status_FreshEmptyRepository_IsCleanWithUnknownBranchAsync()
    {
        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsClean, Is.True);
            Assert.That(result.Branch, Is.EqualTo("unknown"));
        });
    }

    [Test]
    public async Task Status_BranchNamesWithSlashAndAmbiguousTag_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "first");
        RunGit(_repo, "checkout", "--quiet", "-b", "feature/x");

        var slash = await BothBackendsAsync(_repo);

        // A tag with the branch's short name makes git print "heads/feature/x" (and warn on stderr).
        RunGit(_repo, "tag", "feature/x");
        var ambiguous = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(slash.Branch, Is.EqualTo("feature/x"));
            Assert.That(ambiguous.Branch, Is.EqualTo("heads/feature/x"));
        });
    }

    [Test]
    public async Task Status_MergeConflict_InProgressMergeWithConflictsOnBothSidesAsync()
    {
        MakeMergeConflict();

        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.InProgress, Is.EqualTo("merge"));
            Assert.That(result.Staged.Where(e => e.Status == "conflict").Select(e => e.Path), Is.EquivalentTo(new[] { "a.txt", "d.txt", "x.txt" }));
            Assert.That(result.Unstaged.Where(e => e.Status == "conflict").Select(e => e.Path), Is.EquivalentTo(new[] { "a.txt", "d.txt", "x.txt" }));
        });
    }

    [Test]
    public async Task Status_CherryPickConflict_InProgressCherryPickAsync()
    {
        MakeMergeConflict(startMerge: false);
        Assert.That(GitWithIdentity(_repo, "cherry-pick", "side").ExitCode, Is.Not.Zero);

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.InProgress, Is.EqualTo("cherry-pick"));
    }

    [Test]
    public async Task Status_RevertConflict_InProgressRevertAsync()
    {
        Write(_repo, "a.txt", "1\n");
        CommitAll(_repo, "c1");
        Write(_repo, "a.txt", "2\n");
        var middle = CommitAll(_repo, "c2");
        Write(_repo, "a.txt", "3\n");
        CommitAll(_repo, "c3");
        Assert.That(GitWithIdentity(_repo, "revert", middle).ExitCode, Is.Not.Zero);

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.InProgress, Is.EqualTo("revert"));
    }

    [Test]
    public async Task Status_RebaseConflict_InProgressRebaseAsync()
    {
        MakeMergeConflict(startMerge: false);
        var mainBranch = RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD");
        RunGit(_repo, "checkout", "--quiet", "side");
        Assert.That(GitWithIdentity(_repo, "rebase", mainBranch).ExitCode, Is.Not.Zero);

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.InProgress, Is.EqualTo("rebase"));
    }

    [Test]
    public async Task Status_LinkedWorktreeViaRepoPath_ReportsTheWorktreesOwnStateAsync()
    {
        Write(_repo, "a.txt", "base\n");
        CommitAll(_repo, "base");
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        Write(worktree, "a.txt", "worktree change\n");
        Write(worktree, "only-in-worktree.txt", "w\n");

        // The primary checkout stays clean; the worktree is the one with changes, found the way repoPath is: through TryGetGitRoot.
        var worktreeRoot = Impl(new CliGitReader()).TryGetGitRoot(out var error, worktree);
        Assert.That(worktreeRoot, Is.Not.Null, error);
        var inWorktree = await BothBackendsAsync(worktreeRoot!);
        var inPrimary = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(inWorktree.Branch, Is.EqualTo("wt-branch"));
            Assert.That(Rows(inWorktree.Unstaged), Is.EqualTo(new[] { "modified:a.txt" }));
            Assert.That(inWorktree.Untracked, Is.EqualTo(new[] { "only-in-worktree.txt" }));
            Assert.That(inPrimary.IsClean, Is.True);
        });
    }

    [Test]
    public async Task Status_MergeConflictInsideLinkedWorktree_InProgressComesFromThePerWorktreeGitDirAsync()
    {
        Write(_repo, "a.txt", "base\n");
        CommitAll(_repo, "base");
        RunGit(_repo, "branch", "other");
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        Write(worktree, "a.txt", "from worktree\n");
        CommitAll(worktree, "worktree change");
        RunGit(_repo, "checkout", "--quiet", "other");
        Write(_repo, "a.txt", "from other\n");
        CommitAll(_repo, "other change");
        Assert.That(GitWithIdentity(worktree, "merge", "other").ExitCode, Is.Not.Zero);

        var inWorktree = await BothBackendsAsync(worktree);
        var inPrimary = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(inWorktree.InProgress, Is.EqualTo("merge"));
            Assert.That(inPrimary.InProgress, Is.Null);
        });
    }

    [Test]
    public async Task Status_MaxEntries_TruncatesToASampleWithFullCountsAsync()
    {
        for (var i = 0; i < 12; i++)
        {
            Write(_repo, $"tracked-{i:00}.txt", "x\n");
        }

        CommitAll(_repo, "base");
        for (var i = 0; i < 12; i++)
        {
            Write(_repo, $"tracked-{i:00}.txt", "changed\n");
            Write(_repo, $"untracked-{i:00}.txt", "x\n");
        }

        var total = 24;
        var truncated = await BothBackendsAsync(_repo, maxEntries: 15);
        var exact = await BothBackendsAsync(_repo, maxEntries: total);
        var tiny = await BothBackendsAsync(_repo, maxEntries: 3);

        Assert.Multiple(() =>
        {
            Assert.That(truncated.IsTruncated, Is.True);
            Assert.That(truncated.TotalUnstagedCount, Is.EqualTo(12));
            Assert.That(truncated.TotalUntrackedCount, Is.EqualTo(12));
            Assert.That(truncated.Unstaged, Has.Count.EqualTo(10));
            Assert.That(truncated.UnstagedByStatus, Is.EqualTo(new Dictionary<string, int> { ["modified"] = 12 }));
            Assert.That(exact.IsTruncated, Is.False);
            Assert.That(exact.Unstaged, Has.Count.EqualTo(12));
            Assert.That(tiny.Unstaged, Has.Count.EqualTo(3), "the sample is capped by maxEntries when that is below 10");
        });
    }

    [Test]
    public async Task Status_NestedRepository_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "base");
        var nested = Path.Combine(_repo, "vendor", "lib");
        InitRepo(nested);
        Write(nested, "inner.txt", "i\n");

        await BothBackendsAsync(_repo);
    }

    [Test]
    public async Task Status_IntentToAdd_IsAnsweredByTheCliAndMatchesItAsync()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "base");
        Write(_repo, "planned.txt", "p\n");
        RunGit(_repo, "add", "--intent-to-add", "planned.txt");

        // libgit2 reports git add -N as added-and-modified where git says " A" and cannot tell them apart, so it declines.
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var viaComposite = await Impl(composite).StatusAsync(_repo, GitImpl.DefaultStatusMaxEntries, CancellationToken.None);
        var viaCli = await Impl(new CliGitReader()).StatusAsync(_repo, GitImpl.DefaultStatusMaxEntries, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
            Assert.That(JsonSerializer.Serialize(viaComposite), Is.EqualTo(JsonSerializer.Serialize(viaCli)));
            Assert.That(Rows(viaCli.Staged), Is.Empty);
            Assert.That(Rows(viaCli.Unstaged), Is.EqualTo(new[] { "added:planned.txt" }));
        });
    }

    [Test]
    public async Task Status_TypeChange_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "a\n");
        Write(_repo, "b.txt", "b\n");
        CommitAll(_repo, "base");
        // A tracked file replaced by a directory of the same name: " D" for the file plus untracked children.
        File.Delete(Path.Combine(_repo, "a.txt"));
        Write(_repo, "a.txt/inside.txt", "i\n");
        // A staged delete whose path comes back as an untracked file: "D " and "??" for one path.
        RunGit(_repo, "rm", "--quiet", "b.txt");
        Write(_repo, "b.txt", "again\n");

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.Untracked, Is.EquivalentTo(new[] { "a.txt/inside.txt", "b.txt" }));
    }

    [Test]
    public async Task Status_NotARepository_BothBackendsReportTheSameErrorAsync()
    {
        var empty = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(empty);

        var result = await BothBackendsAsync(empty);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Error, Is.Not.Empty);
        });
    }

    [Test]
    public async Task Status_RepositoryWithSubmodule_IsAnsweredByTheCliAndMatchesItAsync()
    {
        var upstream = Path.Combine(_root, "upstream");
        InitRepo(upstream);
        Write(upstream, "lib.txt", "lib\n");
        CommitAll(upstream, "lib");
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "base");
        RunGit(_repo, "-c", "protocol.file.allow=always", "submodule", "add", "--quiet", upstream, "lib");
        CommitAll(_repo, "add submodule");
        Write(_repo, "lib/dirty.txt", "untracked inside the submodule\n");

        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var viaComposite = await Impl(composite).StatusAsync(_repo, GitImpl.DefaultStatusMaxEntries, CancellationToken.None);
        var viaCli = await Impl(new CliGitReader()).StatusAsync(_repo, GitImpl.DefaultStatusMaxEntries, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli), "a repository with a submodule is declined by libgit2 and answered by the CLI");
            Assert.That(composite.DeclineCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(composite.LastDeclineReason, Is.Not.Null.And.Not.Empty);
            Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(JsonSerializer.Serialize(viaComposite), Is.EqualTo(JsonSerializer.Serialize(viaCli)));
        });
    }

    [Test]
    public async Task Status_InTheNormalEnvironment_IsServedByLibgit2NotTheFallbackAsync()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "base");
        Write(_repo, "a.txt", "changed\n");
        Write(_repo, "new.txt", "n\n");

        // The same composite shape production uses (GitReaders.Default), on a private instance so parallel fixtures cannot move its counters.
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var result = await Impl(composite).StatusAsync(_repo, GitImpl.DefaultStatusMaxEntries, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2), "libgit2 must serve status; a CLI answer means a silent fallback (reason: " + composite.FirstFallbackReason + ")");
            Assert.That(composite.Libgit2Count, Is.EqualTo(1));
            Assert.That(composite.CliFallbackCount, Is.EqualTo(0));
            Assert.That(composite.DeclineCount, Is.Zero, "libgit2 must serve status; a decline would hand it to the CLI");
            Assert.That(composite.FirstFallbackReason, Is.Null);
        });
    }

    [Test]
    public async Task Status_ThroughTheProductionDefaultReader_CountsALibgit2CallAsync()
    {
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "base");

        var before = GitReaders.Default.Libgit2Count;
        var result = await new GitImpl(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly)).StatusAsync(_repo, GitImpl.DefaultStatusMaxEntries, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            // At least one: other fixtures share the process-wide default and may add calls of their own.
            Assert.That(GitReaders.Default.Libgit2Count, Is.GreaterThan(before), "the public GitImpl constructor must route status through GitReaders.Default");
        });
    }

    // ---- scenario builders ----

    /// <summary>
    /// base commit with a.txt, d.txt; branch "side" changes a.txt, d.txt and adds x.txt; the default branch changes a.txt, deletes d.txt
    /// and adds a different x.txt (content, add/add and modify/delete conflicts). Leaves the default branch checked out; merges "side"
    /// into it unless <paramref name="startMerge"/> is false.
    /// </summary>
    private void MakeMergeConflict(bool startMerge = true)
    {
        Write(_repo, "a.txt", "base\n");
        Write(_repo, "d.txt", "base d\n");
        CommitAll(_repo, "base");
        RunGit(_repo, "checkout", "--quiet", "-b", "side");
        Write(_repo, "a.txt", "side\n");
        Write(_repo, "d.txt", "side d\n");
        Write(_repo, "x.txt", "side x\n");
        CommitAll(_repo, "side");
        RunGit(_repo, "checkout", "--quiet", "-");
        Write(_repo, "a.txt", "main\n");
        File.Delete(Path.Combine(_repo, "d.txt"));
        Write(_repo, "x.txt", "main x\n");
        CommitAll(_repo, "main");

        if (startMerge)
        {
            Assert.That(GitWithIdentity(_repo, "merge", "side").ExitCode, Is.Not.Zero, "the merge must stop on conflicts");
        }
    }
}
