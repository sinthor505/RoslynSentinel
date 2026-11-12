using System.Text;
using System.Text.Json;
using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// libgit2 1.8.6 reads a linked worktree's "local" config from the per-worktree git directory only, so settings that live in the MAIN repository's
/// <c>.git/config</c> (the usual place) are invisible to it there while git itself honors them. For every config key that can change what a reader
/// returns, this sets the key repo-local (main repository's config) and runs every <see cref="IGitReader"/> method through <see cref="Libgit2GitReader"/> and
/// <see cref="CliGitReader"/> alone, in the main working tree and in a linked worktree, against a working state built to expose the setting. The libgit2
/// answer must equal the CLI answer or be a clean decline (<see cref="NotSupportedException"/>, which the composite turns into a CLI answer).
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// The machine's own global/system git config (for example a global core.autocrlf=true) is visible to both backends; each case sets its key explicitly repo-local.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitWorktreeRepoLocalConfigTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private string _root = null!;
    private string _repo = null!;
    private string _wt = null!;
    private string _firstSha = null!;
    private string _excludesFile = null!;
    private string _orderFile = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-wtcfg-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        _wt = Path.Combine(_root, "wt");
        _excludesFile = Path.Combine(_root, "excludes.cfg");
        _orderFile = Path.Combine(_root, "order.cfg");
        InitRepo(_repo);
        // The baseline every case restores: autocrlf pinned local so the committed blobs are stored byte for byte.
        RunGit(_repo, "config", "core.autocrlf", "false");
        File.WriteAllText(_excludesFile, "*.excluded\n");
        File.WriteAllText(_orderFile, "b.txt\na.txt\n");

        Write(_repo, "a.txt", "one\n");
        Write(_repo, "sub/b.txt", "b\n");
        Write(_repo, ".gitignore", "*.log\nBuild/\n");
        Write(_repo, "Case.txt", "case\n");
        Write(_repo, "old.txt", "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\n");
        Write(_repo, "old2.txt", "alpha\nbeta\ngamma\ndelta\nepsilon\nzeta\neta\ntheta\n");
        WriteBytes(_repo, "crlf-both.txt", "x\r\ny\r\n");
        WriteBytes(_repo, "lf-head.txt", "a\nb\n");
        Write(_repo, "exec.sh", "#!/bin/sh\n");
        _firstSha = CommitAll(_repo, "first");

        // An executable-bit entry and a symlink entry, committed without "add -A" (which would re-stage them under this platform's mode rules).
        RunGit(_repo, "update-index", "--chmod=+x", "exec.sh");
        var targetBlob = Path.Combine(_root, "link-target.tmp");
        File.WriteAllBytes(targetBlob, Encoding.ASCII.GetBytes("a.txt"));
        var blobId = RunGit(_repo, "hash-object", "-w", targetBlob);
        RunGit(_repo, "update-index", "--add", "--cacheinfo", $"120000,{blobId},link");
        Commit(_repo, "modes");

        // A commit with a rename, so commit diffs can show diff.renames at work.
        RunGit(_repo, "mv", "old2.txt", "new2.txt");
        Commit(_repo, "rename commit");
        RunGit(_repo, "branch", "side");

        // A stash entry (shared by every worktree), then the linked worktree.
        Write(_repo, "a.txt", "stashed\n");
        RunGit(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "stash", "push", "--quiet");
        RunGit(_repo, "worktree", "add", "--quiet", _wt, "-b", "linked");

        foreach (var directory in new[] { _repo, _wt })
        {
            ApplyWorkingState(directory);
        }
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

    // ---- fixture helpers ----

    private static void Write(string directory, string relativePath, string content) => WriteBytes(directory, relativePath, content);

    private static void WriteBytes(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
    }

    private static void Commit(string directory, string subject) =>
        RunGit(directory, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", subject);

    /// <summary>The working-tree and index state every setting is judged against: it exposes EOL, mode, symlink, case, ignore, rename and quoting differences at once.</summary>
    private static void ApplyWorkingState(string directory)
    {
        // LF in HEAD, CRLF on disk. Git only compares content (and so only applies core.autocrlf) when the size recorded in the index equals the file's size, so the
        // entry is re-recorded from the CRLF file under autocrlf=true (same blob as HEAD, nothing staged) and the file touched: clean under true/input, modified under false.
        WriteBytes(directory, "lf-head.txt", "a\r\nb\r\n");
        RunGit(directory, "-c", "core.autocrlf=true", "add", "lf-head.txt");
        File.SetLastWriteTimeUtc(Path.Combine(directory, "lf-head.txt"), DateTime.UtcNow.AddSeconds(30));
        WriteBytes(directory, "link", "a.txt");                     // a plain file where HEAD has a symlink
        var upper = Path.Combine(directory, "Case.txt");            // renamed by case only on disk
        var holding = Path.Combine(directory, "case.tmp");
        File.Move(upper, holding);
        File.Move(holding, Path.Combine(directory, "case.txt"));
        RunGit(directory, "mv", "old.txt", "new.txt");              // a staged rename
        Write(directory, "x.excluded", "excluded\n");               // ignored only by core.excludesFile
        Write(directory, "é-quote.txt", "non-ascii name\n");   // untracked, name needs core.quotePath
        Write(directory, "Build/x.txt", "ignored dir\n");           // ignored by "Build/", and by case when ignorecase is true
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private sealed record Probe(string Label, string Method, Func<IGitReader, string, object?> Call);

    private Probe[] Probes() =>
    [
        new("GetHeadSha(full)", nameof(IGitReader.GetHeadSha), (r, d) => r.GetHeadSha(d, shortForm: false, Timeout)),
        new("GetHeadSha(short)", nameof(IGitReader.GetHeadSha), (r, d) => r.GetHeadSha(d, shortForm: true, Timeout)),
        new("GetCommitsSince", nameof(IGitReader.GetCommitsSince), (r, d) => r.GetCommitsSince(d, _firstSha, Timeout)),
        new("GetStatus", nameof(IGitReader.GetStatus), (r, d) => r.GetStatus(d, pathspecs: null, Timeout)),
        new("GetStatus(pathspec)", nameof(IGitReader.GetStatus), (r, d) => r.GetStatus(d, ["*.txt"], Timeout)),
        new("GetRepositoryStatus", nameof(IGitReader.GetRepositoryStatus), (r, d) => r.GetRepositoryStatus(d, Timeout)),
        new("GetLog", nameof(IGitReader.GetLog), (r, d) => r.GetLog(d, 10, revision: null, paths: null, pickaxe: null, pickaxeRegex: false, follow: false, Timeout)),
        new("GetLog(paths)", nameof(IGitReader.GetLog), (r, d) => r.GetLog(d, 10, revision: null, paths: ["a.txt"], pickaxe: null, pickaxeRegex: false, follow: false, Timeout)),
        new("ReadBlobAtHead", nameof(IGitReader.ReadBlobAtHead), (r, d) => r.ReadBlobAtHead(d, "crlf-both.txt", Timeout)),
        // Compared in the order returned: both readers list git's byte order (GitPathOrderParityTests pins that for mixed-case and non-ASCII names).
        new("ListTrackedPaths", nameof(IGitReader.ListTrackedPaths), (r, d) => r.ListTrackedPaths(d, Timeout)),
        new("ListIndexPathsMatching", nameof(IGitReader.ListIndexPathsMatching), (r, d) => r.ListIndexPathsMatching(d, ["Case.txt", "case.txt", "sub"], Timeout)),
        new("ListHeadPathsMatching", nameof(IGitReader.ListHeadPathsMatching), (r, d) => r.ListHeadPathsMatching(d, ["Case.txt", "case.txt", "sub"], Timeout)),
        new("FindIgnoredPaths", nameof(IGitReader.FindIgnoredPaths), (r, d) => r.FindIgnoredPaths(d, ["x.excluded", "build/x.txt", "Build/x.txt", "x.log", "a.txt", "case.txt"], Timeout)),
        new("ListBranches", nameof(IGitReader.ListBranches), (r, d) => r.ListBranches(d, Timeout)),
        new("ListStashes", nameof(IGitReader.ListStashes), (r, d) => r.ListStashes(d, Timeout)),
        new("CountStashes", nameof(IGitReader.CountStashes), (r, d) => r.CountStashes(d, Timeout)),
        new("GetNameStatus(staged)", nameof(IGitReader.GetNameStatus), (r, d) => r.GetNameStatus(d, "staged", paths: null, Timeout)),
        new("GetNameStatus(range)", nameof(IGitReader.GetNameStatus), (r, d) => r.GetNameStatus(d, _firstSha + "..HEAD", paths: null, Timeout)),
        new("GetCommitNameStatus", nameof(IGitReader.GetCommitNameStatus), (r, d) => r.GetCommitNameStatus(d, "HEAD", paths: null, Timeout)),
        // The raw message differs only in its final newline between the readers (libgit2 keeps it, the CLI output is trimmed); the Git tool trims it, so compare it trimmed.
        new("GetCommitHeader", nameof(IGitReader.GetCommitHeader), (r, d) => r.GetCommitHeader(d, "HEAD", Timeout) is { } header ? header with { Message = header.Message.TrimEnd('\r', '\n') } : null),
        new("GetCommitMessages", nameof(IGitReader.GetCommitMessages), (r, d) => r.GetCommitMessages(d, Timeout)),
        new("FindCommitByTrailer", nameof(IGitReader.FindCommitByTrailer), (r, d) => r.FindCommitByTrailer(d, "Tool-Call-Id", "none", 50, Timeout)),
        new("GetBoolConfig", nameof(IGitReader.GetBoolConfig), (r, d) => r.GetBoolConfig(d, "core.ignorecase", Timeout)),
        new("GetHeadWithParents", nameof(IGitReader.GetHeadWithParents), (r, d) => r.GetHeadWithParents(d, Timeout)),
        new("GetMergeHead", nameof(IGitReader.GetMergeHead), (r, d) => r.GetMergeHead(d, Timeout)),
        new("ResolveCommitAbbreviation", nameof(IGitReader.ResolveCommitAbbreviation), (r, d) => r.ResolveCommitAbbreviation(d, _firstSha[..12], Timeout)),
        new("GetCommitMessage", nameof(IGitReader.GetCommitMessage), (r, d) => r.GetCommitMessage(d, "HEAD", Timeout)),
        new("GetUpstreamContainingHead", nameof(IGitReader.GetUpstreamContainingHead), (r, d) => r.GetUpstreamContainingHead(d, Timeout)),
        new("GetInProgressOperation", nameof(IGitReader.GetInProgressOperation), (r, d) => r.GetInProgressOperation(d, Timeout)),
        new("HasStagedChanges", nameof(IGitReader.HasStagedChanges), (r, d) => r.HasStagedChanges(d, paths: null, Timeout)),
    ];

    private static void SetLocal(string directory, string key, string value) => RunGit(directory, "config", "--local", key, value);

    private static IEnumerable<TestCaseData> SettingCases()
    {
        (string Key, string[] Values)[] settings =
        [
            ("core.autocrlf", ["true", "input", "false"]),
            ("core.safecrlf", ["true", "warn", "false"]),
            ("core.filemode", ["true", "false"]),
            ("core.ignorecase", ["true", "false"]),
            ("core.symlinks", ["true", "false"]),
            ("core.excludesFile", ["<excludesFile>"]),
            ("core.abbrev", ["12", "4", "auto", "no"]),
            ("core.quotepath", ["true", "false"]),
            ("diff.renames", ["false", "true", "copies"]),
            ("status.renames", ["false", "copies"]),
            ("diff.orderFile", ["<orderFile>"]),
            ("branch.sort", ["-refname"]),
            ("core.eol", ["crlf", "lf"]),
        ];
        foreach (var (key, values) in settings)
        {
            foreach (var value in values)
            {
                yield return new TestCaseData(key, value).SetName($"RepoLocalSetting_{key}={value}");
            }
        }
    }

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "...";

    private Libgit2GitReader NewLibgit2Reader() =>
        // A fresh length cache per reader, probing the CLI like the production one, so a core.abbrev case is not served a stale cached length.
        new(new ShortHashLengths((workingDirectory, _) =>
        {
            var text = RunGit(workingDirectory, "rev-parse", "--short", "HEAD");
            return text.Length is >= 4 and <= 40 ? text.Length : null;
        }));

    [TestCaseSource(nameof(SettingCases))]
    public void RepoLocalSetting_LibgitVersusCli_InTheMainTreeAndALinkedWorktree(string key, string value)
    {
        var cli = new CliGitReader();
        var differences = new List<string>();
        var actualValue = value.Replace("<excludesFile>", _excludesFile, StringComparison.Ordinal).Replace("<orderFile>", _orderFile, StringComparison.Ordinal);
        SetLocal(_repo, key, actualValue);

        foreach (var (place, directory) in new[] { ("main", _repo), ("worktree", _wt) })
        {
            // Every libgit2 answer first, then every CLI answer, so the CLI's index refreshes cannot influence a libgit2 result of the same place.
            var lib = NewLibgit2Reader();
            var probes = Probes();
            var libResults = new Dictionary<string, (string? Json, string? Failure)>();
            foreach (var probe in probes)
            {
                try
                {
                    libResults[probe.Label] = (Json(probe.Call(lib, directory)), null);
                }
                catch (NotSupportedException)
                {
                    libResults[probe.Label] = (null, null);
                }
                catch (Exception ex)
                {
                    libResults[probe.Label] = (null, $"libgit2 threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (var probe in probes)
            {
                var cliJson = Json(probe.Call(cli, directory));
                var (libJson, failure) = libResults[probe.Label];
                if (failure is not null)
                {
                    differences.Add($"[{key}={value}] {place} {probe.Label}: {failure}");
                }
                else if (libJson is not null && libJson != cliJson)
                {
                    differences.Add($"[{key}={value}] {place} {probe.Label}: libgit2={Truncate(libJson)} cli={Truncate(cliJson)}");
                }
            }
        }

        Assert.That(differences, Is.Empty, string.Join(Environment.NewLine, differences));
    }

    // Pins the third-party behavior Libgit2GitReader.ConfigValue works around. If this fails after a LibGit2Sharp/libgit2 upgrade, the managed Repository.Config now
    // reads the main repository's config in a linked worktree: the worktree branch of ConfigValue (and this test) can go.
    [Test]
    public void LibGit2Sharp_ManagedRepoConfig_InALinkedWorktree_MissesTheMainConfig_AndSpoilsLaterNativeLookups()
    {
        SetLocal(_repo, "core.abbrev", "4");

        using var untouched = new LibGit2Sharp.Repository(LibGit2Sharp.Repository.Discover(_wt));
        var shortWhenConfigUntouched = untouched.ObjectDatabase.ShortenObjectId(untouched.Head.Tip, 4);

        using var touched = new LibGit2Sharp.Repository(LibGit2Sharp.Repository.Discover(_wt));
        var managedValue = touched.Config.Get<string>("core.abbrev")?.Value;
        var shortAfterConfigTouched = touched.ObjectDatabase.ShortenObjectId(touched.Head.Tip, 4);

        using var main = new LibGit2Sharp.Repository(_repo);
        var mainManagedValue = main.Config.Get<string>("core.abbrev")?.Value;

        Assert.Multiple(() =>
        {
            Assert.That(mainManagedValue, Is.EqualTo("4"), "the main repository's managed config sees its own local config");
            Assert.That(shortWhenConfigUntouched, Has.Length.EqualTo(4), "native lookups in the worktree see the main repository's core.abbrev while the managed config is untouched");
            Assert.That(managedValue, Is.Null, "the managed config of a linked worktree misses the main repository's config");
            Assert.That(shortAfterConfigTouched, Has.Length.EqualTo(7), "and once touched it makes the native lookup miss it too (libgit2's default of 7)");
        });
    }

    [Test]
    public void GetBoolConfig_InALinkedWorktree_ReadsTheMainRepositorysConfig_ServedByLibgit2()
    {
        SetLocal(_repo, "custom.flag", "yes");
        SetLocal(_repo, "core.autocrlf", "false"); // the machine's global/system value may be true: the repo-local one must win in the worktree too
        var cli = new CliGitReader();
        var composite = new FallbackGitReader(new Libgit2GitReader(), cli, _ => { });

        var results = new[] { _repo, _wt }.Select(directory => (
            Flag: composite.GetBoolConfig(directory, "custom.flag", Timeout),
            Autocrlf: composite.GetBoolConfig(directory, "core.autocrlf", Timeout),
            Unset: composite.GetBoolConfig(directory, "custom.unset", Timeout))).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(results[0], Is.EqualTo((Flag: (bool?)true, Autocrlf: (bool?)false, Unset: (bool?)null)), "main");
            Assert.That(results[1], Is.EqualTo(results[0]), "linked worktree");
            Assert.That(cli.GetBoolConfig(_wt, "custom.flag", Timeout), Is.True);
            Assert.That(composite.CliFallbackCount, Is.Zero, "libgit2 answers in a linked worktree: " + composite.FirstFallbackReason);
            Assert.That(composite.Libgit2Count, Is.EqualTo(6));
        });
    }

    // Config the libgit2 reader reads itself to decide whether it can answer: it must see the main repository's config from a linked worktree, so it declines there exactly as
    // in the main tree (a blind read would answer from libgit2 with the wrong order or the wrong rename rows). The staged state has no renames, so GetNameStatus is served otherwise.
    [TestCase("branch.sort", "-refname", "ListBranches")]
    [TestCase("diff.renames", "false", "GetRepositoryStatus")]
    [TestCase("status.renames", "copies", "GetRepositoryStatus")]
    [TestCase("diff.orderFile", "<orderFile>", "GetNameStatus(staged)")]
    public void ConfigThatMakesLibgit2Decline_IsSeenFromALinkedWorktreeToo(string key, string value, string probeLabel)
    {
        File.WriteAllText(_orderFile, "sub/b.txt\na.txt\n");
        foreach (var directory in new[] { _repo, _wt })
        {
            RunGit(directory, "reset", "--quiet");
            Write(directory, "a.txt", "changed a\n");
            Write(directory, "sub/b.txt", "changed b\n");
            RunGit(directory, "add", "a.txt", "sub/b.txt");
        }

        var probe = Probes().Single(p => p.Label == probeLabel);
        var lib = NewLibgit2Reader();
        var cli = new CliGitReader();
        var before = new[] { _repo, _wt }.Select(directory => Json(probe.Call(lib, directory))).ToArray(); // served by libgit2 before the setting (a throw fails the test)

        SetLocal(_repo, key, value.Replace("<orderFile>", _orderFile, StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(before, Has.None.EqualTo("null"), "libgit2 serves the probe before the setting is configured");
            foreach (var (place, directory) in new[] { ("main", _repo), ("worktree", _wt) })
            {
                Assert.Throws<NotSupportedException>(() => probe.Call(lib, directory), $"libgit2 must decline {probeLabel} once {key} is configured ({place})");
                Assert.That(Json(probe.Call(cli, directory)), Is.Not.EqualTo("null"), $"the CLI answers ({place})");
            }

            if (key == "diff.orderFile")
            {
                // The setting changes git's answer, which is why declining matters: the staged files come out in the order file's order.
                var json = Json(probe.Call(cli, _wt));
                Assert.That(json.IndexOf("sub/b.txt", StringComparison.Ordinal), Is.LessThan(json.IndexOf("a.txt", StringComparison.Ordinal)).And.GreaterThanOrEqualTo(0));
            }
        });
    }
}
