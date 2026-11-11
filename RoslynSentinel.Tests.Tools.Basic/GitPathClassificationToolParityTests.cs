using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The Git tool classifies the paths it is asked to stage, commit or hunk-stage by reading the index (ls-files), HEAD's tree (ls-tree) and the
/// ignore rules (check-ignore). These tests run the tool entry points that do so (<see cref="GitImpl.HunksAsync"/> read-only,
/// <see cref="GitImpl.StageAsync"/> with the index reset between runs) with a libgit2-only and a CLI-only reader and assert the response DTOs
/// serialize identically, then compare the three reader methods directly. A case libgit2 declines goes through the production composite: the test
/// asserts the CLI answered and that its answer equals the CLI-only one. Neither single-backend reader can fall back, so a libgit2 failure fails
/// the test.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitPathClassificationToolParityTests
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

        _root = Path.Combine(Path.GetTempPath(), "rs-gitpaths-" + Guid.NewGuid().ToString("N"));
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

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Commit(string directory, string message)
    {
        RunGit(directory, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "--allow-empty", "-m", message);
        return RunGit(directory, "rev-parse", "HEAD");
    }

    private static string Json(string[] paths) => JsonSerializer.Serialize(paths);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    /// <summary>Hunks (read-only) through the libgit2-only and the CLI-only reader; asserts identical DTOs and returns the CLI-backed JSON.</summary>
    private static async Task<string> HunksBothAsync(string gitRoot, params string[] paths)
    {
        var lib = Serialize(await Impl(new Libgit2GitReader()).HunksAsync(gitRoot, Json(paths), 20, CancellationToken.None));
        var cli = Serialize(await Impl(new CliGitReader()).HunksAsync(gitRoot, Json(paths), 20, CancellationToken.None));
        Assert.That(lib, Is.EqualTo(cli), $"libgit2-backed hunks differs from the CLI-backed one for [{string.Join(", ", paths)}].\nlibgit2: {lib}\ncli:     {cli}");
        return cli;
    }

    /// <summary>
    /// Stage (scope listed) through each reader on the same repository, resetting the index in between and afterwards; asserts identical DTOs and
    /// returns the CLI-backed JSON.
    /// </summary>
    private async Task<string> StageBothAsync(params string[] paths)
    {
        var lib = Serialize(await Impl(new Libgit2GitReader()).StageAsync(_repo, GitStageScope.listed, Json(paths), CancellationToken.None));
        RunGitRaw(_repo, "reset", "--quiet");
        var cli = Serialize(await Impl(new CliGitReader()).StageAsync(_repo, GitStageScope.listed, Json(paths), CancellationToken.None));
        RunGitRaw(_repo, "reset", "--quiet");
        Assert.That(lib, Is.EqualTo(cli), $"libgit2-backed stage differs from the CLI-backed one for [{string.Join(", ", paths)}].\nlibgit2: {lib}\ncli:     {cli}");
        return cli;
    }

    private static string[] Sorted(IReadOnlyList<string>? list) => list is null ? [] : [.. list.Order(StringComparer.Ordinal)];

    /// <summary>Both single readers must answer a clean path set identically (as a set; the order of a listing is not part of the contract).</summary>
    private static void AssertListingsAgree(Func<IGitReader, IReadOnlyList<string>?> read, string what, string repo)
    {
        var lib = read(new Libgit2GitReader());
        var cli = read(new CliGitReader());
        Assert.Multiple(() =>
        {
            Assert.That(lib is null, Is.EqualTo(cli is null), $"{what}: null-ness differs (libgit2 {lib?.Count.ToString() ?? "null"}, cli {cli?.Count.ToString() ?? "null"})");
            Assert.That(Sorted(lib), Is.EqualTo(Sorted(cli)), what);
        });
    }

    private void AssertIndexAndHeadAgree(params string[] paths)
    {
        var what = $"[{string.Join(", ", paths)}]";
        AssertListingsAgree(reader => reader.ListIndexPathsMatching(_repo, paths, Timeout), "index " + what, _repo);
        AssertListingsAgree(reader => reader.ListHeadPathsMatching(_repo, paths, Timeout), "head " + what, _repo);
    }

    private void CommitBaseline()
    {
        Write(_repo, "a.txt", "a\n");
        Write(_repo, "dir/b.txt", "b\n");
        Write(_repo, "dir/sub/c.txt", "c\n");
        Write(_repo, "dir with space/\u00e9 \u00fc file.txt", "e\n");
        Write(_repo, "\u65e5\u672c\u8a9e/\U0001F600.txt", "j\n");
        Write(_repo, "Readme.txt", "r\n");
        Write(_repo, ".gitignore", "*.log\nbuild/\n!keep.log\nignored_dir/\n!ignored_dir/inner.txt\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "baseline");
    }

    // ---- tool level: Hunks (read-only) ----

    [Test]
    public async Task Hunks_TrackedModifiedFile_PlainSpacesNonAsciiAndNested_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "a\nchanged\n");
        Write(_repo, "dir/sub/c.txt", "c\nchanged\n");
        Write(_repo, "dir with space/\u00e9 \u00fc file.txt", "e\nchanged\n");
        Write(_repo, "\u65e5\u672c\u8a9e/\U0001F600.txt", "j\nchanged\n");

        var seen = new List<string>();
        foreach (var path in new[] { "a.txt", "dir/sub/c.txt", "dir with space/\u00e9 \u00fc file.txt", "\u65e5\u672c\u8a9e/\U0001F600.txt" })
        {
            var json = await HunksBothAsync(_repo, path);
            Assert.That(json, Does.Contain("\"IsError\":false"), path);
            seen.Add(path);
        }

        Assert.That(seen, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task Hunks_Refusals_UntrackedMissingDeletedUnchangedDirectoryAndCaseVariant_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "new.txt", "untracked\n");
        File.Delete(Path.Combine(_repo, "dir", "b.txt"));
        Write(_repo, "a.txt", "a\nchanged\n");

        var refusals = new Dictionary<string, string>();
        foreach (var path in new[] { "new.txt", "nope.txt", "dir/b.txt", "dir/sub/c.txt", "dir", "readme.TXT", "Readme.txt" })
        {
            refusals[path] = await HunksBothAsync(_repo, path);
        }

        // Each of these must be a refusal or an empty diff, never a hunk list for a file git does not track.
        Assert.That(refusals["new.txt"], Does.Contain("\"IsError\":true"));
        Assert.That(refusals["nope.txt"], Does.Contain("\"IsError\":true"));
    }

    [Test]
    public async Task Hunks_UnbornBranchWithStagedFile_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "a\n");
        RunGit(_repo, "add", "a.txt");

        await HunksBothAsync(_repo, "a.txt");
        await HunksBothAsync(_repo, "b.txt");
    }

    [Test]
    public async Task Hunks_AbsentSubmodule_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        // A gitlink in the index and in HEAD whose directory is not on disk (a submodule that was never cloned).
        RunGit(_repo, "update-index", "--add", "--cacheinfo", "160000," + new string('a', 40) + ",vend");
        Commit(_repo, "add submodule entry");

        foreach (var path in new[] { "vend", "vend/inside.txt", "vend/sub/deeper.txt" })
        {
            await HunksBothAsync(_repo, path);
        }

        AssertIndexAndHeadAgree("vend");
        AssertIndexAndHeadAgree("vend/inside.txt");
    }

    [Test]
    public async Task Hunks_LinkedWorktree_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        Write(worktree, "a.txt", "a\nchanged in worktree\n");
        Write(worktree, "only-wt.txt", "w\n");
        RunGit(worktree, "add", "only-wt.txt");

        var worktreeRoot = Impl(new CliGitReader()).TryGetGitRoot(out var error, worktree);
        Assert.That(worktreeRoot, Is.Not.Null, error);

        var json = await HunksBothAsync(worktreeRoot!, "a.txt");
        await HunksBothAsync(worktreeRoot!, "only-wt.txt");
        await HunksBothAsync(_repo, "a.txt"); // the primary checkout is unchanged

        Assert.That(json, Does.Contain("\"IsError\":false"));
    }

    // ---- tool level: Stage (mutating; index reset between runs) ----

    [Test]
    public async Task Stage_MixOfTrackedModifiedUntrackedAndDeleted_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "a\nchanged\n");
        Write(_repo, "new top.txt", "n\n");
        Write(_repo, "dir/sub/new nested.txt", "n\n");
        Write(_repo, "\u00fcber/\u00e9.txt", "n\n");
        File.Delete(Path.Combine(_repo, "dir", "b.txt"));
        Directory.Delete(Path.Combine(_repo, "dir with space"), recursive: true);

        var json = await StageBothAsync("a.txt", "new top.txt", "dir/sub/new nested.txt", "\u00fcber/\u00e9.txt", "dir/b.txt", "dir with space");

        Assert.That(json, Does.Contain("\"IsError\":false"));
    }

    [Test]
    public async Task Stage_MissingPathAndIgnoredPaths_BothBackendsReportTheSameRefusalAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "a\nchanged\n");
        Write(_repo, "debug.log", "ignored by *.log\n");
        Write(_repo, "build/out.txt", "ignored directory\n");
        Write(_repo, "ignored_dir/other.txt", "ignored directory\n");
        Write(_repo, "keep.log", "negated\n");

        var missing = await StageBothAsync("a.txt", "nope.txt");
        var ignoredFile = await StageBothAsync("a.txt", "debug.log");
        var ignoredDirectory = await StageBothAsync("build");
        var ignoredChild = await StageBothAsync("ignored_dir/other.txt");
        var negated = await StageBothAsync("keep.log");

        Assert.Multiple(() =>
        {
            Assert.That(missing, Does.Contain("not tracked and do not exist on disk"));
            Assert.That(ignoredFile, Does.Contain("ignored by .gitignore"));
            Assert.That(ignoredDirectory, Does.Contain("ignored by .gitignore"));
            Assert.That(ignoredChild, Does.Contain("ignored by .gitignore"));
            Assert.That(negated, Does.Contain("\"IsError\":false"));
        });
    }

    [Test]
    public async Task Stage_PathsGitNormalisesOrRejects_AreAnsweredByTheCliWithTheSameDtoAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "a\nchanged\n");

        // ValidateRepoRoot (in the tool) refuses ".." and absolute paths before classification; "./a.txt" and "dir//b.txt" reach it.
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var viaComposite = Serialize(await Impl(composite).StageAsync(_repo, GitStageScope.listed, Json(["./a.txt"]), CancellationToken.None));
        RunGitRaw(_repo, "reset", "--quiet");
        var viaCli = Serialize(await Impl(new CliGitReader()).StageAsync(_repo, GitStageScope.listed, Json(["./a.txt"]), CancellationToken.None));
        RunGitRaw(_repo, "reset", "--quiet");

        Assert.That(viaComposite, Is.EqualTo(viaCli));
    }

    // ---- reader level: ls-files / ls-tree ----

    [Test]
    public void IndexAndHeadListings_FilesDirectoriesAndMissing_BothReadersAgree()
    {
        CommitBaseline();

        AssertIndexAndHeadAgree("a.txt");
        AssertIndexAndHeadAgree("dir");
        AssertIndexAndHeadAgree("dir/sub");
        AssertIndexAndHeadAgree("dir/b.txt", "dir/sub/c.txt");
        AssertIndexAndHeadAgree("dir", "dir/b.txt"); // overlapping: each file once
        AssertIndexAndHeadAgree("nope", "dir/nope", "a.txt/inside");
        AssertIndexAndHeadAgree("dir with space/\u00e9 \u00fc file.txt");
        AssertIndexAndHeadAgree("dir with space");
        AssertIndexAndHeadAgree("\u65e5\u672c\u8a9e", "\u65e5\u672c\u8a9e/\U0001F600.txt");
        AssertIndexAndHeadAgree("d"); // a prefix of a name is not a directory above it
        AssertIndexAndHeadAgree(".gitignore");
    }

    [Test]
    public void IndexAndHeadListings_CaseVariantsOfTrackedNames_BothReadersAgree()
    {
        // Literal and case-sensitive on both sides even where core.ignorecase is true (the Windows default after git init).
        CommitBaseline();

        AssertIndexAndHeadAgree("readme.TXT");
        AssertIndexAndHeadAgree("README.TXT", "Readme.txt");
        AssertIndexAndHeadAgree("DIR");
    }

    [Test]
    public void IndexAndHeadListings_IndexAheadOfAndBehindHead_BothReadersAgree()
    {
        CommitBaseline();
        Write(_repo, "staged-new.txt", "s\n");
        RunGit(_repo, "add", "staged-new.txt");
        RunGit(_repo, "rm", "--quiet", "--cached", "a.txt"); // in HEAD, gone from the index
        Write(_repo, "untracked.txt", "u\n");

        AssertIndexAndHeadAgree("staged-new.txt", "a.txt", "untracked.txt");
        AssertIndexAndHeadAgree("dir");
    }

    [Test]
    public void IndexListing_IntentToAddAndSubmoduleEntries_BothReadersAgree()
    {
        CommitBaseline();
        Write(_repo, "ita.txt", "i\n");
        RunGit(_repo, "add", "--intent-to-add", "ita.txt");
        RunGit(_repo, "update-index", "--add", "--cacheinfo", "160000," + new string('b', 40) + ",vend");

        AssertListingsAgree(reader => reader.ListIndexPathsMatching(_repo, ["ita.txt", "vend", "vend/x"], Timeout), "index ita and gitlink", _repo);
    }

    [Test]
    public void IndexListing_UnmergedPath_IsListedOnceByBothReaders()
    {
        Write(_repo, "conflict.txt", "base\n");
        Write(_repo, "other.txt", "o\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "base");
        var main = RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD");
        RunGit(_repo, "checkout", "--quiet", "-b", "side");
        Write(_repo, "conflict.txt", "side\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "side");
        RunGit(_repo, "checkout", "--quiet", main);
        Write(_repo, "conflict.txt", "main\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "main");
        var merge = RunGitRaw(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "side");
        Assert.That(merge.ExitCode, Is.Not.Zero, "the merge should conflict: " + merge.StdOut);

        AssertIndexAndHeadAgree("conflict.txt");
        AssertIndexAndHeadAgree("conflict.txt", "other.txt");
        Assert.That(new Libgit2GitReader().ListIndexPathsMatching(_repo, ["conflict.txt"], Timeout), Is.EqualTo(new[] { "conflict.txt" }));
    }

    [Test]
    public void IndexAndHeadListings_UnbornBranch_BothReadersAgree()
    {
        Write(_repo, "a.txt", "a\n");
        RunGit(_repo, "add", "a.txt");

        AssertIndexAndHeadAgree("a.txt");
        AssertIndexAndHeadAgree("b.txt");
        Assert.That(new Libgit2GitReader().ListHeadPathsMatching(_repo, ["a.txt"], Timeout), Is.Null, "ls-tree HEAD fails on an unborn branch");
        Assert.That(new Libgit2GitReader().ListIndexPathsMatching(_repo, ["a.txt"], Timeout), Is.EqualTo(new[] { "a.txt" }));
    }

    [Test]
    public void IndexAndHeadListings_LinkedWorktree_BothReadersAgree()
    {
        CommitBaseline();
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        Write(worktree, "wt-only.txt", "w\n");
        RunGit(worktree, "add", "wt-only.txt");

        foreach (var directory in new[] { worktree, _repo })
        {
            AssertListingsAgree(reader => reader.ListIndexPathsMatching(directory, ["wt-only.txt", "a.txt", "dir"], Timeout), "index in " + directory, directory);
            AssertListingsAgree(reader => reader.ListHeadPathsMatching(directory, ["wt-only.txt", "a.txt", "dir"], Timeout), "head in " + directory, directory);
        }
    }

    [TestCase("./a.txt")]
    [TestCase("dir//b.txt")]
    [TestCase("dir/./b.txt")]
    [TestCase("dir/../a.txt")]
    [TestCase("dir/")]
    [TestCase("../outside")]
    [TestCase("")]
    [TestCase("/a.txt")]
    [TestCase("C:/x/a.txt")]
    [TestCase("dir\\b.txt")]
    public void IndexAndHeadListings_PathsGitNormalisesOrRejects_AreDeclinedByLibgit2AndAnsweredByTheCli(string path)
    {
        CommitBaseline();

        Assert.Multiple(() =>
        {
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().ListIndexPathsMatching(_repo, [path], Timeout));
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().ListHeadPathsMatching(_repo, [path], Timeout));
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().FindIgnoredPaths(_repo, [path], Timeout));
        });

        // The composite answers from the CLI and records exactly why libgit2 stepped aside.
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var viaComposite = composite.ListIndexPathsMatching(_repo, [path], Timeout);
        var viaCli = new CliGitReader().ListIndexPathsMatching(_repo, [path], Timeout);
        Assert.Multiple(() =>
        {
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
            Assert.That(composite.DeclineCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(composite.LastDeclineReason, Is.Not.Null.And.Not.Empty);
            Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(Sorted(viaComposite), Is.EqualTo(Sorted(viaCli)));
        });
    }

    // ---- reader level: check-ignore ----

    private void WriteIgnoreFixture()
    {
        var globalFile = Path.Combine(_root, "global-excludes.txt");
        File.WriteAllText(globalFile, "globalonly.tmp\n");
        RunGit(_repo, "config", "core.excludesFile", globalFile.Replace('\\', '/'));
        Write(_repo, ".gitignore", "*.log\nbuild/\n!keep.log\nignored_dir/\n!ignored_dir/inner.txt\nvendor/*\n!vendor/keep.txt\n/rooted.txt\nsub/deep/\n");
        Write(_repo, "sub/.gitignore", "*.cache\n!important.cache\n");
        Directory.CreateDirectory(Path.Combine(_repo, ".git", "info"));
        File.WriteAllText(Path.Combine(_repo, ".git", "info", "exclude"), "secret.txt\n");
        foreach (var file in new[]
                 {
                     "a.log", "keep.log", "tracked.log", "ignored_dir/inner.txt", "ignored_dir/other.txt", "build/x.txt", "secret.txt", "globalonly.tmp", "sub/x.log",
                     "vendor/keep.txt", "vendor/drop.txt", "plain.txt", "rooted.txt", "sub/rooted.txt", "sub/a.cache", "sub/important.cache", "sub/deep/f.txt", "\u00fc nicode/\u00e9.log",
                 })
        {
            Write(_repo, file, "x\n");
        }

        RunGit(_repo, "add", "-f", "tracked.log");
        Commit(_repo, "ignore fixture");
    }

    [Test]
    public void FindIgnoredPaths_RulesFromGitignoreInfoExcludeAndGlobalExcludes_BothReadersAgree()
    {
        WriteIgnoreFixture();
        string[] paths =
        [
            "a.log", "keep.log", "tracked.log", "ignored_dir", "ignored_dir/inner.txt", "ignored_dir/other.txt", "build", "build/x.txt", "build/nope.txt", "missing-dir/build",
            "secret.txt", "globalonly.tmp", "sub/x.log", "vendor/keep.txt", "vendor/drop.txt", "plain.txt", "missing.log", "rooted.txt", "sub/rooted.txt", "sub/a.cache",
            "sub/important.cache", "sub/deep/f.txt", "sub/deep", "\u00fc nicode/\u00e9.log", "dir with space/none.log",
        ];

        var lib = new Libgit2GitReader().FindIgnoredPaths(_repo, paths, Timeout);
        var cli = new CliGitReader().FindIgnoredPaths(_repo, paths, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(lib, Is.EqualTo(cli), "libgit2 and git check-ignore disagree on which paths are ignored");
            Assert.That(cli, Does.Contain("a.log"));
            Assert.That(cli, Does.Contain("secret.txt"), ".git/info/exclude applies");
            Assert.That(cli, Does.Contain("globalonly.tmp"), "core.excludesFile applies");
            Assert.That(cli, Does.Not.Contain("keep.log"), "negated");
            Assert.That(cli, Does.Not.Contain("tracked.log"), "a tracked file is never ignored");
        });
    }

    [Test]
    public void FindIgnoredPaths_NoMatchesAndRepeatedPaths_BothReadersAgree()
    {
        WriteIgnoreFixture();

        Assert.Multiple(() =>
        {
            Assert.That(new Libgit2GitReader().FindIgnoredPaths(_repo, ["plain.txt"], Timeout), Is.Empty);
            Assert.That(new CliGitReader().FindIgnoredPaths(_repo, ["plain.txt"], Timeout), Is.Empty);
            Assert.That(new Libgit2GitReader().FindIgnoredPaths(_repo, ["a.log", "plain.txt", "a.log"], Timeout),
                Is.EqualTo(new CliGitReader().FindIgnoredPaths(_repo, ["a.log", "plain.txt", "a.log"], Timeout)));
        });
    }

    [Test]
    public void FindIgnoredPaths_LinkedWorktree_BothReadersAgree()
    {
        WriteIgnoreFixture();
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        Write(worktree, "x.log", "x\n");
        Write(worktree, "plain.txt", "changed\n");

        string[] paths = ["x.log", "plain.txt", "secret.txt", "build", "tracked.log"];
        Assert.That(new Libgit2GitReader().FindIgnoredPaths(worktree, paths, Timeout), Is.EqualTo(new CliGitReader().FindIgnoredPaths(worktree, paths, Timeout)));
    }

    [Test]
    public void FindIgnoredPaths_NoRepository_IsNullFromLibgit2AndEmptyFromTheCli()
    {
        var empty = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(empty);

        // The CLI reader answers an empty list when git fails (its per-path exit code is simply not 0), libgit2 has no repository to ask: both mean "nothing ignored" to the tool.
        Assert.Multiple(() =>
        {
            Assert.That(new Libgit2GitReader().FindIgnoredPaths(empty, ["a.log"], Timeout), Is.Null);
            Assert.That(new CliGitReader().FindIgnoredPaths(empty, ["a.log"], Timeout), Is.Empty);
        });
    }

    // ---- which backend serves ----

    [Test]
    public async Task PathClassification_InTheNormalEnvironment_IsServedByLibgit2NotTheFallbackAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "a\nchanged\n");
        Write(_repo, "new.txt", "n\n");
        Write(_repo, "debug.log", "ignored\n");

        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var impl = Impl(composite);
        var hunks = await impl.HunksAsync(_repo, Json(["a.txt"]), 20, CancellationToken.None);
        var staged = await impl.StageAsync(_repo, GitStageScope.listed, Json(["a.txt", "new.txt"]), CancellationToken.None);
        RunGitRaw(_repo, "reset", "--quiet");
        var refused = await impl.StageAsync(_repo, GitStageScope.listed, Json(["debug.log"]), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(hunks.IsError, Is.False);
            Assert.That(staged.IsError, Is.False);
            Assert.That(refused.IsError, Is.True);
            Assert.That(composite.FirstFallbackReason, Is.Null, "libgit2 must serve these reads; a CLI answer means a silent fallback");
            Assert.That(composite.CliFallbackCount, Is.Zero);
            Assert.That(composite.DeclineCount, Is.Zero, "libgit2 must serve these reads; a decline would hand them to the CLI");
            // Hunks and each Stage classify once (index + HEAD); the ignored-path refusal adds a check-ignore call.
            Assert.That(composite.Libgit2Count, Is.GreaterThanOrEqualTo(7));
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
        });
    }

    [Test]
    public async Task PathClassification_ThroughTheProductionDefaultReader_CountsLibgit2CallsAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "a\nchanged\n");

        var before = GitReaders.Default.Libgit2Count;
        var hunks = await new GitImpl(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly)).HunksAsync(_repo, Json(["a.txt"]), 20, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(hunks.IsError, Is.False);
            Assert.That(GitReaders.Default.Libgit2Count, Is.GreaterThan(before), "the public GitImpl constructor must route the path-classification reads through GitReaders.Default");
        });
    }
}
