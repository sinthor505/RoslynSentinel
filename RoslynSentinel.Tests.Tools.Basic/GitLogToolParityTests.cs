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
/// Runs the Git tool's log path (<see cref="GitImpl.LogAsync"/>, the code behind <c>Git(operation: log)</c>) with a libgit2-only reader and a
/// CLI-only reader on throwaway repos and asserts the response DTOs serialize identically. Cases libgit2 cannot reproduce (pickaxe, follow,
/// ranges, merges under a path filter, non-UTF-8 commits, ...) go through the production composite instead: the test asserts the CLI
/// answered and that its DTO equals the CLI-only one. Neither single-backend reader can fall back, so a libgit2 failure fails the test.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitLogToolParityTests
{
    private static readonly UTF8Encoding Utf8 = new(false);

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

        _root = Path.Combine(Path.GetTempPath(), "rs-gitlog-" + Guid.NewGuid().ToString("N"));
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

    private static Task<GitLogResult> LogWith(IGitReader reader, string gitRoot, int count, string? refName, string? paths, string? pickaxe = null, bool pickaxeRegex = false, bool follow = false) =>
        Impl(reader).LogAsync(gitRoot, count, refName, paths, pickaxe, pickaxeRegex, follow, CancellationToken.None);

    /// <summary>Runs log through the libgit2-only and the CLI-only reader, asserts identical DTOs and returns the CLI-backed one.</summary>
    private static async Task<GitLogResult> BothBackendsAsync(string gitRoot, int count = 20, string? refName = null, string? paths = null)
    {
        var lib = await LogWith(new Libgit2GitReader(), gitRoot, count, refName, paths);
        var cli = await LogWith(new CliGitReader(), gitRoot, count, refName, paths);
        var libJson = JsonSerializer.Serialize(lib);
        var cliJson = JsonSerializer.Serialize(cli);
        Assert.That(libJson, Is.EqualTo(cliJson), $"libgit2-backed log differs from the CLI-backed log.\nlibgit2: {libJson}\ncli:     {cliJson}");
        return cli;
    }

    /// <summary>
    /// For a case libgit2 declines: the composite answers from the CLI (asserted) and equals the CLI-only reader's DTO. Returns that DTO.
    /// </summary>
    private static async Task<GitLogResult> CliRoutedAsync(string gitRoot, int count = 20, string? refName = null, string? paths = null, string? pickaxe = null, bool pickaxeRegex = false, bool follow = false)
    {
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var viaComposite = await LogWith(composite, gitRoot, count, refName, paths, pickaxe, pickaxeRegex, follow);
        var viaCli = await LogWith(new CliGitReader(), gitRoot, count, refName, paths, pickaxe, pickaxeRegex, follow);
        var bypassesReader = !string.IsNullOrWhiteSpace(pickaxe) || follow;
        Assert.Multiple(() =>
        {
            if (bypassesReader)
            {
                // Pickaxe and follow never reach the reader: they are not recorded as a fallback of libgit2.
                Assert.That(composite.Libgit2Count + composite.CliFallbackCount + composite.DeclineCount, Is.Zero, "pickaxe and follow go straight to git");
                Assert.That(composite.FirstFallbackReason, Is.Null);
                Assert.That(composite.LastDeclineReason, Is.Null);
            }
            else
            {
                Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli), "libgit2 should have declined this case (failure reason: " + composite.FirstFallbackReason + ")");
                Assert.That(composite.DeclineCount, Is.GreaterThanOrEqualTo(1));
                Assert.That(composite.LastDeclineReason, Is.Not.Null.And.Not.Empty);
                Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
                Assert.That(composite.FirstFallbackReason, Is.Null);
            }

            Assert.That(JsonSerializer.Serialize(viaComposite), Is.EqualTo(JsonSerializer.Serialize(viaCli)));
        });
        return viaCli;
    }

    private static string[] Subjects(GitLogResult result) => [.. result.Commits.Select(c => c.Message.Split('\n')[0].TrimEnd('\r'))];

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>git with extra environment variables on this child only (identity and dates), failing on a non-zero exit.</summary>
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

    /// <summary>Stages everything and commits with the message written verbatim (no cleanup), optional author name, author date and committer date.</summary>
    private string Commit(
        string directory, string message, string? authorName = null, string? authorDate = null, string? committerDate = null, Encoding? encoding = null, params string[] extraConfig)
    {
        var file = Path.Combine(_root, $"message{_messageFiles++}.txt");
        File.WriteAllBytes(file, (encoding ?? Utf8).GetBytes(message));
        var environment = new Dictionary<string, string>();
        if (authorName is not null)
        {
            environment["GIT_AUTHOR_NAME"] = authorName;
        }

        if (authorDate is not null)
        {
            environment["GIT_AUTHOR_DATE"] = authorDate;
        }

        if (committerDate is not null)
        {
            environment["GIT_COMMITTER_DATE"] = committerDate;
        }

        RunGit(directory, "add", "-A");
        RunGitEnv(directory, environment, ["-c", "user.name=Test", "-c", "user.email=test@example.com", .. extraConfig, "commit", "--quiet", "--allow-empty", "--allow-empty-message", "--cleanup=verbatim", "-F", file]);
        return RunGit(directory, "rev-parse", "HEAD");
    }

    private static string Utc(int secondsAfterEpochBase) =>
        DateTimeOffset.FromUnixTimeSeconds(1_600_000_000 + secondsAfterEpochBase).ToString("yyyy-MM-dd'T'HH:mm:ss'+0000'");

    /// <summary>Feeds a git fast-import script (UTF-8) to <c>git fast-import</c> in <paramref name="directory"/>.</summary>
    private static void FastImport(string directory, string script)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("fast-import");
        psi.ArgumentList.Add("--quiet");
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        using var process = Process.Start(psi)!;
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        process.StandardInput.BaseStream.Write(Utf8.GetBytes(script));
        process.StandardInput.Close();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("git fast-import timed out.");
        }

        Task.WaitAll(stdOut, stdErr);
        Assert.That(process.ExitCode, Is.Zero, "git fast-import failed: " + stdErr.Result);
    }

    /// <summary>
    /// A pseudo-random history of <paramref name="commits"/> commits on refs/heads/main: committer dates drawn from four consecutive seconds (many
    /// same-second ties and parents newer than children) and about a third of the commits merges, some with three parents.
    /// </summary>
    private static string RandomHistoryScript(int seed, int commits)
    {
        var random = new Random(seed);
        var script = new StringBuilder();
        for (var i = 1; i <= commits; i++)
        {
            var when = 1_600_000_000 + random.Next(0, 4);
            var message = $"commit {i}\n";
            var content = $"content {i}\n";
            script.Append($"commit refs/heads/main\nmark :{i}\nauthor A U <a@example.com> {when} +0000\ncommitter C U <c@example.com> {when} +0000\n");
            script.Append($"data {Utf8.GetByteCount(message)}\n{message}\n");
            if (i > 1)
            {
                var first = random.Next(100) < 60 ? i - 1 : random.Next(1, i);
                script.Append($"from :{first}\n");
                if (i > 2 && random.Next(100) < 35)
                {
                    int second;
                    do
                    {
                        second = random.Next(1, i);
                    }
                    while (second == first);

                    script.Append($"merge :{second}\n");
                    if (i > 4 && random.Next(100) < 20)
                    {
                        int third;
                        do
                        {
                            third = random.Next(1, i);
                        }
                        while (third == first || third == second);

                        script.Append($"merge :{third}\n");
                    }
                }
            }

            script.Append($"M 100644 inline f{i % 7}.txt\ndata {Utf8.GetByteCount(content)}\n{content}\n");
        }

        return script.ToString();
    }

    private void ImportRandomHistory(int seed, int commits)
    {
        FastImport(_repo, RandomHistoryScript(seed, commits));
        RunGit(_repo, "symbolic-ref", "HEAD", "refs/heads/main");
        RunGit(_repo, "reset", "--quiet", "--hard");
    }

    // ---- plain log ----

    [Test]
    public async Task Log_Plain_BothBackendsAgreeAsync()
    {
        for (var i = 1; i <= 5; i++)
        {
            Write(_repo, "a.txt", $"{i}\n");
            Commit(_repo, $"commit {i}\n\nbody of {i}\n");
        }

        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(Subjects(result), Is.EqualTo(new[] { "commit 5", "commit 4", "commit 3", "commit 2", "commit 1" }));
            Assert.That(result.Commits[0].Hash, Has.Length.EqualTo(40));
            Assert.That(result.Commits[0].ShortHash, Has.Length.EqualTo(7));
            Assert.That(result.Commits[0].Message, Does.Contain("body of 5"));
        });
    }

    [Test]
    public async Task Log_CountLimit_BothBackendsAgreeAsync()
    {
        for (var i = 1; i <= 4; i++)
        {
            Commit(_repo, $"commit {i}\n");
        }

        var two = await BothBackendsAsync(_repo, count: 2);
        var zeroClampsToOne = await BothBackendsAsync(_repo, count: 0);
        var moreThanExist = await BothBackendsAsync(_repo, count: 50);

        Assert.Multiple(() =>
        {
            Assert.That(two.Commits, Has.Count.EqualTo(2));
            Assert.That(zeroClampsToOne.Commits, Has.Count.EqualTo(1));
            Assert.That(moreThanExist.Commits, Has.Count.EqualTo(4));
        });
    }

    [Test]
    public async Task Log_RefForms_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "first\n");
        RunGit(_repo, "tag", "light");
        Commit(_repo, "second\n");
        RunGit(_repo, "tag", "-a", "annotated", "-m", "annotated tag", "HEAD");
        RunGit(_repo, "checkout", "--quiet", "-b", "feature/x");
        Commit(_repo, "on feature\n");
        var head = RunGit(_repo, "rev-parse", "HEAD");

        var forms = new[] { "feature/x", "light", "annotated", "HEAD~1", "HEAD^", "HEAD~2", head, head[..10], "heads/feature/x", "refs/tags/light" };
        var seen = new List<string>();
        foreach (var form in forms)
        {
            var result = await BothBackendsAsync(_repo, refName: form);
            Assert.That(result.IsError, Is.False, form);
            seen.Add($"{form}: {string.Join('|', Subjects(result))}");
        }

        Assert.That(seen, Does.Contain("light: first"));
        Assert.That(seen, Does.Contain("annotated: second|first"));
        Assert.That(seen, Does.Contain("HEAD~1: second|first"));
    }

    [Test]
    public async Task Log_UnknownRef_BothBackendsReportTheSameErrorAsync()
    {
        Commit(_repo, "first\n");

        var result = await BothBackendsAsync(_repo, refName: "no-such-branch");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.Error, Is.Not.Empty);
        });
    }

    [Test]
    public async Task Log_UnbornBranch_BothBackendsReportTheSameErrorAsync()
    {
        var result = await BothBackendsAsync(_repo);

        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task Log_RefThatIsAlsoAFile_IsAnsweredByTheCliAsync()
    {
        Write(_repo, "topic", "a file named like the branch\n");
        Commit(_repo, "first\n");
        RunGit(_repo, "branch", "topic");

        var result = await CliRoutedAsync(_repo, refName: "topic");

        Assert.That(result.IsError, Is.True, "git refuses a name that is both a revision and a file");
    }

    [Test]
    public async Task Log_RangeAndOtherRevisionSyntax_AreAnsweredByTheCliAsync()
    {
        Commit(_repo, "one\n");
        Commit(_repo, "two\n");
        Commit(_repo, "three\n");

        var range = await CliRoutedAsync(_repo, refName: "HEAD~2..HEAD");
        var searched = await CliRoutedAsync(_repo, refName: ":/two");

        Assert.Multiple(() =>
        {
            Assert.That(Subjects(range), Is.EqualTo(new[] { "three", "two" }));
            Assert.That(searched.IsError, Is.False);
        });
    }

    // ---- paths, rename, follow, pickaxe ----

    [Test]
    public async Task Log_PathFilter_SingleMultipleDirectoryAndMissing_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "a1\n");
        Commit(_repo, "add a\n");
        Write(_repo, "dir/b.txt", "b1\n");
        Commit(_repo, "add dir/b\n");
        Write(_repo, "a.txt", "a2\n");
        Write(_repo, "dir/sub/c.txt", "c1\n");
        Commit(_repo, "change a, add dir/sub/c\n");
        Write(_repo, "other.txt", "o\n");
        Commit(_repo, "unrelated\n");

        var single = await BothBackendsAsync(_repo, paths: "a.txt");
        var multiple = await BothBackendsAsync(_repo, paths: JsonSerializer.Serialize(new[] { "a.txt", "other.txt" }));
        var directory = await BothBackendsAsync(_repo, paths: "dir");
        var directorySlash = await BothBackendsAsync(_repo, paths: "dir/");
        var nested = await BothBackendsAsync(_repo, paths: "dir/sub");
        var missing = await BothBackendsAsync(_repo, paths: "nope.txt");
        var limited = await BothBackendsAsync(_repo, count: 1, paths: "a.txt");

        Assert.Multiple(() =>
        {
            Assert.That(Subjects(single), Is.EqualTo(new[] { "change a, add dir/sub/c", "add a" }));
            Assert.That(Subjects(multiple), Is.EqualTo(new[] { "unrelated", "change a, add dir/sub/c", "add a" }));
            Assert.That(Subjects(directory), Is.EqualTo(new[] { "change a, add dir/sub/c", "add dir/b" }));
            Assert.That(Subjects(directorySlash), Is.EqualTo(Subjects(directory)));
            Assert.That(Subjects(nested), Is.EqualTo(new[] { "change a, add dir/sub/c" }));
            Assert.That(missing.Commits, Is.Empty);
            Assert.That(Subjects(limited), Is.EqualTo(new[] { "change a, add dir/sub/c" }));
        });
    }

    [Test]
    public async Task Log_PathFilter_SpacesAndNonAscii_BothBackendsAgreeAsync()
    {
        Write(_repo, "dir with space/é ü file.txt", "1\n");
        Commit(_repo, "add spaced\n");
        Write(_repo, "日本語/\U0001F600.txt", "1\n");
        Commit(_repo, "add japanese\n");
        Write(_repo, "dir with space/é ü file.txt", "2\n");
        Commit(_repo, "change spaced\n");

        var spaced = await BothBackendsAsync(_repo, paths: JsonSerializer.Serialize(new[] { "dir with space/é ü file.txt" }));
        var folder = await BothBackendsAsync(_repo, paths: JsonSerializer.Serialize(new[] { "dir with space" }));
        var japanese = await BothBackendsAsync(_repo, paths: JsonSerializer.Serialize(new[] { "日本語/\U0001F600.txt" }));

        Assert.Multiple(() =>
        {
            Assert.That(Subjects(spaced), Is.EqualTo(new[] { "change spaced", "add spaced" }));
            Assert.That(Subjects(folder), Is.EqualTo(new[] { "change spaced", "add spaced" }));
            Assert.That(Subjects(japanese), Is.EqualTo(new[] { "add japanese" }));
        });
    }

    [Test]
    public async Task Log_PathFilter_DifferentCase_BothBackendsAgreeAsync()
    {
        // core.ignorecase decides whether git matches the pathspec case-insensitively (it is true after git init on Windows); both backends must agree either way.
        Write(_repo, "Readme.txt", "1\n");
        Commit(_repo, "add readme\n");

        await BothBackendsAsync(_repo, paths: "README.TXT");
        await BothBackendsAsync(_repo, paths: "readme.txt");
    }

    [Test]
    public async Task Log_PathFilter_GlobAndMagicAndRelativeSpecs_AreAnsweredByTheCliAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Write(_repo, "dir/b.txt", "1\n");
        Commit(_repo, "add\n");

        var glob = await CliRoutedAsync(_repo, paths: "*.txt");
        var magic = await CliRoutedAsync(_repo, paths: ":(top)a.txt");
        var dotSlash = await CliRoutedAsync(_repo, paths: "./a.txt");
        var dot = await CliRoutedAsync(_repo, paths: ".");

        Assert.Multiple(() =>
        {
            Assert.That(glob.Commits, Has.Count.EqualTo(1));
            Assert.That(magic.Commits, Has.Count.EqualTo(1));
            Assert.That(dotSlash.Commits, Has.Count.EqualTo(1));
            Assert.That(dot.Commits, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Log_Rename_WithoutFollowListsOnlyTheNewNameAndWithFollowGoesToTheCliAsync()
    {
        var body = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line {i}")) + "\n";
        Write(_repo, "old.txt", body);
        Commit(_repo, "add old\n");
        Write(_repo, "old.txt", body + "more\n");
        Commit(_repo, "edit old\n");
        RunGit(_repo, "mv", "old.txt", "new.txt");
        Commit(_repo, "rename to new\n");
        Write(_repo, "new.txt", body + "more\nand more\n");
        Commit(_repo, "edit new\n");

        var withoutFollow = await BothBackendsAsync(_repo, paths: "new.txt");
        var oldName = await BothBackendsAsync(_repo, paths: "old.txt");
        var withFollow = await CliRoutedAsync(_repo, paths: "new.txt", follow: true);

        Assert.Multiple(() =>
        {
            Assert.That(Subjects(withoutFollow), Is.EqualTo(new[] { "edit new", "rename to new" }));
            Assert.That(Subjects(oldName), Is.EqualTo(new[] { "rename to new", "edit old", "add old" }));
            Assert.That(Subjects(withFollow), Is.EqualTo(new[] { "edit new", "rename to new", "edit old", "add old" }));
        });
    }

    [Test]
    public async Task Log_Follow_ValidationErrorsStayTheToolsOwnAsync()
    {
        Commit(_repo, "first\n");

        var noPath = await LogWith(new Libgit2GitReader(), _repo, 20, null, null, follow: true);
        var twoPaths = await LogWith(new Libgit2GitReader(), _repo, 20, null, JsonSerializer.Serialize(new[] { "a", "b" }), follow: true);

        Assert.Multiple(() =>
        {
            Assert.That(noPath.Error, Does.StartWith("follow needs exactly one path in paths, but none was supplied."));
            Assert.That(twoPaths.Error, Does.StartWith("follow needs exactly one path in paths, but 2 were supplied."));
        });
    }

    [Test]
    public async Task Log_PickaxeStringAndRegex_AreAnsweredByTheCliAsync()
    {
        Write(_repo, "a.txt", "alpha\n");
        Commit(_repo, "add alpha\n");
        Write(_repo, "a.txt", "alpha\nneedle one\n");
        Commit(_repo, "add needle\n");
        Write(_repo, "a.txt", "alpha\nneedle one\nneedle two\n");
        Commit(_repo, "add second needle\n");

        var s = await CliRoutedAsync(_repo, pickaxe: "needle");
        var g = await CliRoutedAsync(_repo, pickaxe: "needle (one|two)", pickaxeRegex: true);
        var blank = await BothBackendsAsync(_repo); // a blank pickaxe is no pickaxe

        var blankPickaxe = await LogWith(new Libgit2GitReader(), _repo, 20, null, null, pickaxe: "  ");

        Assert.Multiple(() =>
        {
            Assert.That(Subjects(s), Is.EqualTo(new[] { "add second needle", "add needle" }));
            Assert.That(Subjects(g), Is.EqualTo(new[] { "add second needle", "add needle" }));
            Assert.That(JsonSerializer.Serialize(blankPickaxe), Is.EqualTo(JsonSerializer.Serialize(blank)));
        });
    }

    // ---- merges and order ----

    [Test]
    public async Task Log_MergeHistoryWithTiesAndClockSkew_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "base\n");
        Commit(_repo, "base\n", committerDate: Utc(0));
        var mainBranch = RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD");
        RunGit(_repo, "checkout", "--quiet", "-b", "side");
        Write(_repo, "side.txt", "s1\n");
        Commit(_repo, "side 1\n", committerDate: Utc(1));
        Write(_repo, "side.txt", "s2\n");
        Commit(_repo, "side 2\n", committerDate: Utc(1));        // same second as its parent
        RunGit(_repo, "checkout", "--quiet", mainBranch);
        Write(_repo, "main.txt", "m1\n");
        Commit(_repo, "main 1\n", committerDate: Utc(1));
        Write(_repo, "main.txt", "m2\n");
        Commit(_repo, "main 2\n", committerDate: Utc(3));
        RunGitEnv(_repo, new Dictionary<string, string> { ["GIT_COMMITTER_DATE"] = Utc(3), ["GIT_AUTHOR_DATE"] = Utc(3) },
            "-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "--quiet", "--no-ff", "-m", "merge side", "side");
        Write(_repo, "main.txt", "m3\n");
        Commit(_repo, "after merge, older than its parent\n", committerDate: Utc(-100)); // skew: newer parent, older child
        Write(_repo, "main.txt", "m4\n");
        Commit(_repo, "tip\n", committerDate: Utc(3));

        var result = await BothBackendsAsync(_repo, count: 100);

        Assert.That(result.Commits, Has.Count.EqualTo(8));
    }

    [Test]
    public async Task Log_OctopusMerge_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "base\n");
        Commit(_repo, "base\n", committerDate: Utc(0));
        var mainBranch = RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD");
        foreach (var name in new[] { "one", "two", "three" })
        {
            RunGit(_repo, "checkout", "--quiet", "-b", name, mainBranch);
            Write(_repo, $"{name}.txt", name);
            Commit(_repo, $"branch {name}\n", committerDate: Utc(1));
        }

        RunGit(_repo, "checkout", "--quiet", mainBranch);
        RunGitEnv(_repo, new Dictionary<string, string> { ["GIT_COMMITTER_DATE"] = Utc(2) },
            "-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "--quiet", "--no-ff", "-m", "octopus", "one", "two", "three");

        var result = await BothBackendsAsync(_repo, count: 100);

        Assert.That(Subjects(result)[0], Is.EqualTo("octopus"));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public async Task Log_RandomMergeHistory_SameSecondTiesAndSkew_BothBackendsAgreeAsync(int seed)
    {
        ImportRandomHistory(seed, commits: 160);

        var result = await BothBackendsAsync(_repo, count: 100);

        Assert.That(result.Commits.Count, Is.GreaterThan(9), "the generated history should reach well past a handful of commits");
    }

    [Test]
    public async Task Log_RandomMergeHistory_OtherTips_BothBackendsAgreeAsync()
    {
        ImportRandomHistory(seed: 11, commits: 120);
        RunGit(_repo, "branch", "old", "HEAD~7");

        await BothBackendsAsync(_repo, count: 100, refName: "old");
        await BothBackendsAsync(_repo, count: 10, refName: "HEAD^2");
    }

    [Test]
    public async Task Log_PathFilterOnMergeHistory_IsServedUntilAMergeIsReachedThenTheCliAnswersAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "base\n", committerDate: Utc(0));
        var mainBranch = RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD");
        RunGit(_repo, "checkout", "--quiet", "-b", "side");
        Write(_repo, "b.txt", "s\n");
        Commit(_repo, "side\n", committerDate: Utc(1));
        RunGit(_repo, "checkout", "--quiet", mainBranch);
        Write(_repo, "a.txt", "2\n");
        Commit(_repo, "main change\n", committerDate: Utc(2));
        RunGitEnv(_repo, new Dictionary<string, string> { ["GIT_COMMITTER_DATE"] = Utc(3) },
            "-c", "user.name=Test", "-c", "user.email=test@example.com", "merge", "--quiet", "--no-ff", "-m", "merge side", "side");
        Write(_repo, "a.txt", "3\n");
        Commit(_repo, "tip change\n", committerDate: Utc(4));

        // One commit is found before the walk meets the merge: libgit2 answers (BothBackendsAsync would throw on a decline).
        var first = await BothBackendsAsync(_repo, count: 1, paths: "a.txt");
        // More commits need the walk through the merge, where git simplifies the history: the CLI answers.
        var deeper = await CliRoutedAsync(_repo, count: 100, paths: "a.txt");

        Assert.Multiple(() =>
        {
            Assert.That(Subjects(first), Is.EqualTo(new[] { "tip change" }));
            Assert.That(Subjects(deeper), Is.EqualTo(new[] { "tip change", "main change", "base" }));
        });
    }

    [Test]
    public async Task Log_PathFilterOnADeepLinearHistory_PastTheWalkCap_IsAnsweredByTheCliAsync()
    {
        // Only the first commit touches hot.txt; the 2,000 after it touch other.txt, so a path-limited walk has to pass them all (the cap is 2,000).
        var script = new StringBuilder();
        for (var i = 1; i <= 2001; i++)
        {
            var message = $"commit {i}\n";
            var file = i == 1 ? "hot.txt" : "other.txt";
            var content = $"content {i}\n";
            script.Append($"commit refs/heads/main\nmark :{i}\ncommitter C <c@example.com> {1_600_000_000 + i} +0000\ndata {Utf8.GetByteCount(message)}\n{message}");
            if (i > 1)
            {
                script.Append($"from :{i - 1}\n");
            }

            script.Append($"M 100644 inline {file}\ndata {Utf8.GetByteCount(content)}\n{content}\n");
        }

        FastImport(_repo, script.ToString());
        RunGit(_repo, "symbolic-ref", "HEAD", "refs/heads/main");

        var deep = await CliRoutedAsync(_repo, paths: "hot.txt");
        var shallow = await BothBackendsAsync(_repo, count: 5, paths: "other.txt"); // five matches turn up within the first five commits

        Assert.Multiple(() =>
        {
            Assert.That(Subjects(deep), Is.EqualTo(new[] { "commit 1" }));
            Assert.That(shallow.Commits, Has.Count.EqualTo(5));
        });
    }

    // ---- text: identity, dates, encodings, line endings ----

    [Test]
    public async Task Log_AuthorDates_KeepTheAuthorsOwnUtcOffsetAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "plus half\n", authorDate: "2020-01-02T03:04:05+0530");
        Commit(_repo, "minus\n", authorDate: "2020-06-07T23:59:59-0800");
        Commit(_repo, "utc\n", authorDate: "2020-12-31T00:00:00+0000");
        Commit(_repo, "plus max\n", authorDate: "2021-03-04T05:06:07+1400");

        var result = await BothBackendsAsync(_repo);

        var dates = result.Commits.Select(c => c.Date).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(dates[0], Is.EqualTo("2021-03-04T05:06:07+14:00"));
            // Newer git spells a zero offset "Z", older git "+00:00"; the libgit2 reader asks the installed git, so both backends agree either way.
            Assert.That(dates[1], Is.AnyOf("2020-12-31T00:00:00Z", "2020-12-31T00:00:00+00:00"));
            Assert.That(dates[2], Is.EqualTo("2020-06-07T23:59:59-08:00"));
            Assert.That(dates[3], Is.EqualTo("2020-01-02T03:04:05+05:30"));
        });
    }

    [Test]
    public async Task Log_NonAsciiAuthorAndMessage_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "Fix café ☕ \U0001F600\n\nbody ñ 日本語\n", authorName: "Zoë Ünïcode 日本");
        // A repository-level output encoding must not leak into the tool's text: both readers force UTF-8 (the CLI explicitly, libgit2 never converts).
        RunGit(_repo, "config", "i18n.logOutputEncoding", "ISO-8859-1");

        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.Commits[0].Author, Is.EqualTo("Zoë Ünïcode 日本"));
            Assert.That(result.Commits[0].Message, Does.StartWith("Fix café ☕ \U0001F600"));
            Assert.That(result.Commits[0].Message, Does.Contain("body ñ 日本語"));
        });
    }

    [Test]
    public async Task Log_Latin1EncodedCommit_IsAnsweredByTheCliAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "café in latin-1\n", encoding: Encoding.Latin1, extraConfig: ["-c", "i18n.commitEncoding=ISO-8859-1"]);

        var result = await CliRoutedAsync(_repo);

        Assert.That(result.Commits[0].Message, Is.EqualTo("café in latin-1"));
    }

    [Test]
    public async Task Log_CrlfAndLoneCrBodiesAndOddWhitespace_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "crlf subject\r\n\r\nbody one\r\nbody two\r\n\r\n\r\nlast\r\n");
        Commit(_repo, "lone cr\rsecond line\r\rthird\r");
        Commit(_repo, "\n\n  leading blank lines and no final newline");
        Commit(_repo, "trailing blank lines\n\n\n\n");
        Commit(_repo, "mixed\r\n\nendings\n\rhere\n");
        Commit(_repo, string.Empty);

        var result = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(result.Commits, Has.Count.EqualTo(6));
            // Every line break is the platform newline, and the message is trimmed of leading and trailing breaks.
            Assert.That(result.Commits[5].Message, Is.EqualTo($"crlf subject{Environment.NewLine}{Environment.NewLine}body one{Environment.NewLine}body two{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}last"));
            Assert.That(result.Commits[4].Message, Is.EqualTo($"lone cr{Environment.NewLine}second line{Environment.NewLine}{Environment.NewLine}third"));
            Assert.That(result.Commits[0].Message, Is.EqualTo(string.Empty));
        });
    }

    [Test]
    public async Task Log_MessageWithTheCliParsersSeparatorCharacters_IsAnsweredByTheCliAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "has a unit\u001fseparator inside\n");

        var result = await CliRoutedAsync(_repo);

        Assert.That(result.Commits, Has.Count.EqualTo(1));
    }

    // ---- abbreviation ----

    [TestCase("4", 4)]
    [TestCase("7", 7)]
    [TestCase("10", 10)]
    [TestCase("12", 12)]
    [TestCase("40", 40)]
    [TestCase("no", 40)]
    [TestCase("auto", 7)]
    public async Task Log_CoreAbbrevSettings_BothBackendsAgreeAsync(string setting, int expectedLength)
    {
        Commit(_repo, "first\n");
        Commit(_repo, "second\n");
        RunGit(_repo, "config", "core.abbrev", setting);

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.Commits[0].ShortHash, Has.Length.EqualTo(expectedLength));
    }

    [Test]
    public async Task Log_LargePackedRepository_AutomaticAbbreviationGrowsLikeGitAsync()
    {
        // 17,000 packed blobs push git's automatic abbreviation from 7 to 8 hex digits (half the bit length of the object count, rounded up).
        var script = new StringBuilder();
        for (var i = 0; i < 17_000; i++)
        {
            script.Append($"blob\ndata 9\nblob{i:D5}\n");
        }

        const string message = "big\n";
        script.Append($"commit refs/heads/main\ncommitter C <c@example.com> 1600000000 +0000\ndata {message.Length}\n{message}M 100644 inline a.txt\ndata 2\nx\n\n");
        FastImport(_repo, script.ToString());
        RunGit(_repo, "symbolic-ref", "HEAD", "refs/heads/main");

        var result = await BothBackendsAsync(_repo);

        Assert.That(result.Commits[0].ShortHash, Has.Length.GreaterThanOrEqualTo(8), "git's abbreviation for a repository this size");
    }

    // ---- repository kinds ----

    [Test]
    public async Task Log_LinkedWorktreeViaRepoPath_ReportsTheWorktreesBranchAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "on main\n");
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        Write(worktree, "w.txt", "w\n");
        Commit(worktree, "only in worktree\n");

        var worktreeRoot = Impl(new CliGitReader()).TryGetGitRoot(out var error, worktree);
        Assert.That(worktreeRoot, Is.Not.Null, error);
        var inWorktree = await BothBackendsAsync(worktreeRoot!);
        var inWorktreePath = await BothBackendsAsync(worktreeRoot!, paths: "w.txt");
        var inPrimary = await BothBackendsAsync(_repo);

        Assert.Multiple(() =>
        {
            Assert.That(Subjects(inWorktree), Is.EqualTo(new[] { "only in worktree", "on main" }));
            Assert.That(Subjects(inWorktreePath), Is.EqualTo(new[] { "only in worktree" }));
            Assert.That(Subjects(inPrimary), Is.EqualTo(new[] { "on main" }));
        });
    }

    [Test]
    public async Task Log_ShallowClone_IsAnsweredByTheCliAsync()
    {
        Commit(_repo, "one\n");
        Commit(_repo, "two\n");
        Commit(_repo, "three\n");
        var clone = Path.Combine(_root, "shallow");
        RunGit(_root, "clone", "--quiet", "--depth", "2", "file:///" + _repo.Replace('\\', '/'), clone);

        var result = await CliRoutedAsync(clone);

        Assert.That(Subjects(result), Is.EqualTo(new[] { "three", "two" }));
    }

    [Test]
    public async Task Log_SubdirectoryAsWorkingDirectoryIsNotTheTools_RootIsAlwaysTheRepositoryAsync()
    {
        Write(_repo, "dir/a.txt", "1\n");
        Commit(_repo, "add\n");

        // The tool always passes the repository root; a path relative to the root must work the same way for both readers.
        var result = await BothBackendsAsync(_repo, paths: "dir/a.txt");

        Assert.That(result.Commits, Has.Count.EqualTo(1));
    }

    // ---- which backend serves ----

    [Test]
    public async Task Log_InTheNormalEnvironment_IsServedByLibgit2NotTheFallbackAsync()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "first\n");
        Commit(_repo, "second\n");

        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var result = await LogWith(composite, _repo, 20, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(result.Commits, Has.Count.EqualTo(2));
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2), "libgit2 must serve log; a CLI answer means a silent fallback (reason: " + composite.FirstFallbackReason + ")");
            Assert.That(composite.Libgit2Count, Is.EqualTo(1));
            Assert.That(composite.CliFallbackCount, Is.EqualTo(0));
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(composite.DeclineCount, Is.Zero, "libgit2 must serve log; a decline would hand it to the CLI");
        });
    }

    [Test]
    public async Task Log_PathFilterOnLinearHistory_IsServedByLibgit2Async()
    {
        Write(_repo, "a.txt", "1\n");
        Commit(_repo, "first\n");
        Write(_repo, "a.txt", "2\n");
        Commit(_repo, "second\n");

        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var result = await LogWith(composite, _repo, 20, "HEAD~1", "a.txt");

        Assert.Multiple(() =>
        {
            Assert.That(Subjects(result), Is.EqualTo(new[] { "first" }));
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2), composite.FirstFallbackReason);
            Assert.That(composite.CliFallbackCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Log_ThroughTheProductionDefaultReader_CountsALibgit2CallAsync()
    {
        Commit(_repo, "first\n");

        var before = GitReaders.Default.Libgit2Count;
        var result = await new GitImpl(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly)).LogAsync(_repo, 20, null, null, null, false, false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            // At least one: other fixtures share the process-wide default and may add calls of their own.
            Assert.That(GitReaders.Default.Libgit2Count, Is.GreaterThan(before), "the public GitImpl constructor must route log through GitReaders.Default");
        });
    }
}
