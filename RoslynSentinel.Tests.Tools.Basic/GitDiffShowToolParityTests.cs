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
/// Runs the Git tool's diff and show (<see cref="GitImpl.DiffAsync"/>, <see cref="GitImpl.ShowAsync"/>) with a libgit2-only reader and a CLI-only reader on
/// throwaway repos and asserts the response DTOs serialize identically. Covered: the diff name-status list (nameOnly) for working, staged, a ref and a range, the
/// name-status list of show, and the show header (hash, author, date, message). A case libgit2 declines (renames, submodules, conflicts, an annotated tag, a
/// configured diff.renames and so on) goes through the production composite: the test asserts the CLI answered and that its DTO equals the CLI-only one. The
/// stat text and the patch are not covered by a parity claim because they stay on the CLI (see the comment at the call site in GitImpl.DiffAsync); one test pins
/// that they never reach the libgit2 reader.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitDiffShowToolParityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly string[] Identity = ["-c", "user.name=Test", "-c", "user.email=test@example.com"];

    private string _root = null!;
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-gitdiff-" + Guid.NewGuid().ToString("N"));
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

    private static void WriteBytes(string directory, string relativePath, byte[] content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    /// <summary>git with extra environment variables on this child only (author and committer dates), failing on a non-zero exit.</summary>
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

    private static string Commit(string directory, string message, string? authorDate = null)
    {
        var environment = authorDate is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = authorDate, ["GIT_COMMITTER_DATE"] = authorDate };
        RunGitEnv(directory, environment, [.. Identity, "commit", "--quiet", "--allow-empty", "--allow-empty-message", "-m", message]);
        return RunGit(directory, "rev-parse", "HEAD");
    }

    /// <summary>Stages everything and commits.</summary>
    private static string CommitAll(string directory, string message, string? authorDate = null)
    {
        RunGit(directory, "add", "-A");
        return Commit(directory, message, authorDate);
    }

    private void CommitBaseline()
    {
        Write(_repo, "a.txt", "a\n");
        Write(_repo, "b.txt", "b\n");
        Write(_repo, "dir/c.txt", "c\n");
        CommitAll(_repo, "baseline");
    }

    private static Task<GitDiffResult> Diff(IGitReader reader, string root, string target, string? paths, bool nameOnly = true, bool stat = false) =>
        Impl(reader).DiffAsync(root, target, paths, 65536, nameOnly, stat, CancellationToken.None);

    private static Task<GitShowResult> Show(IGitReader reader, string root, string target, string? paths = null, bool nameOnly = true, bool stat = false) =>
        Impl(reader).ShowAsync(root, target, paths, 65536, nameOnly, stat, CancellationToken.None);

    /// <summary>Diff through the libgit2-only and the CLI-only reader; asserts identical DTOs and returns the CLI-backed result.</summary>
    private static async Task<GitDiffResult> DiffBothAsync(string root, string target, string? paths = null, bool nameOnly = true)
    {
        var lib = await Diff(new Libgit2GitReader(), root, target, paths, nameOnly);
        var cli = await Diff(new CliGitReader(), root, target, paths, nameOnly);
        Assert.That(Serialize(lib), Is.EqualTo(Serialize(cli)), $"libgit2-backed diff differs from the CLI-backed one.\nlibgit2: {Serialize(lib)}\ncli:     {Serialize(cli)}");
        return cli;
    }

    private static async Task<GitShowResult> ShowBothAsync(string root, string target, string? paths = null, bool nameOnly = true, bool stat = false)
    {
        var lib = await Show(new Libgit2GitReader(), root, target, paths, nameOnly, stat);
        var cli = await Show(new CliGitReader(), root, target, paths, nameOnly, stat);
        Assert.That(Serialize(lib), Is.EqualTo(Serialize(cli)), $"libgit2-backed show differs from the CLI-backed one.\nlibgit2: {Serialize(lib)}\ncli:     {Serialize(cli)}");
        return cli;
    }

    /// <summary>A case libgit2 declines: the composite answers from the CLI (asserted) and its DTO equals the CLI-only reader's.</summary>
    private static async Task<GitDiffResult> DiffCliRoutedAsync(string root, string target, string? paths = null)
    {
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var viaComposite = await Diff(composite, root, target, paths);
        var viaCli = await Diff(new CliGitReader(), root, target, paths);
        Assert.Multiple(() =>
        {
            Assert.That(composite.DeclineCount, Is.EqualTo(1), "libgit2 should have declined this case (failure reason: " + composite.FirstFallbackReason + ")");
            Assert.That(composite.LastDeclineReason, Is.Not.Null.And.Not.Empty);
            Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(Serialize(viaComposite), Is.EqualTo(Serialize(viaCli)));
        });
        return viaCli;
    }

    private static async Task<GitShowResult> ShowCliRoutedAsync(string root, string target, string? paths = null)
    {
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var viaComposite = await Show(composite, root, target, paths);
        var viaCli = await Show(new CliGitReader(), root, target, paths);
        Assert.Multiple(() =>
        {
            Assert.That(composite.DeclineCount, Is.GreaterThanOrEqualTo(1), "libgit2 should have declined part of this case (failure reason: " + composite.FirstFallbackReason + ")");
            Assert.That(composite.LastDeclineReason, Is.Not.Null.And.Not.Empty);
            Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(Serialize(viaComposite), Is.EqualTo(Serialize(viaCli)));
        });
        return viaCli;
    }

    // The tool terminates lines with Environment.NewLine (as its git runner always has), so split on either.
    private static string[] Lines(GitDiffResult result) => result.Diff.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static string[] Lines(GitShowResult result) => result.Diff.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    // ---- diff nameOnly: working ----

    [Test]
    public async Task DiffWorking_ModifiedDeletedAndStagedFiles_UntrackedExcluded_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "changed\n");
        File.Delete(Path.Combine(_repo, "b.txt"));
        Write(_repo, "dir/c.txt", "staged\n");
        RunGit(_repo, "add", "dir/c.txt");
        Write(_repo, "dir/c.txt", "staged then changed again\n");
        Write(_repo, "untracked.txt", "u\n");

        var result = await DiffBothAsync(_repo, "working");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(Lines(result), Is.EqualTo(new[] { "M\ta.txt", "D\tb.txt", "M\tdir/c.txt" }));
            Assert.That(result.FilesChanged, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task DiffWorking_UnstagedAliasAndNothingChanged_BothBackendsAgreeAsync()
    {
        CommitBaseline();

        var none = await DiffBothAsync(_repo, "working");
        Write(_repo, "a.txt", "changed\n");
        var alias = await DiffBothAsync(_repo, "unstaged");

        Assert.Multiple(() =>
        {
            Assert.That(none.Diff, Is.Empty);
            Assert.That(none.FilesChanged, Is.Zero);
            Assert.That(Lines(alias), Is.EqualTo(new[] { "M\ta.txt" }));
        });
    }

    [Test]
    public async Task DiffWorking_OrderingSpacesAndNonAsciiPaths_BothBackendsAgreeAsync()
    {
        foreach (var path in new[] { "a.txt", "a/x.txt", "a-b.txt", "a0.txt", "B.txt", "Zeta.txt", "été.txt", "dir with space/file name.txt", "日本/語.txt", "z/\U0001F600.txt" })
        {
            Write(_repo, path, "one\n");
        }

        CommitAll(_repo, "many");
        foreach (var path in new[] { "a.txt", "a/x.txt", "a-b.txt", "a0.txt", "B.txt", "Zeta.txt", "été.txt", "dir with space/file name.txt", "日本/語.txt", "z/\U0001F600.txt" })
        {
            Write(_repo, path, "two\n");
        }

        var result = await DiffBothAsync(_repo, "working");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(result), Has.Length.EqualTo(10));
            Assert.That(Lines(result)[0], Is.EqualTo("M\tB.txt"), "byte order: uppercase before lowercase");
            Assert.That(result.Diff, Does.Contain("été.txt").And.Contain("日本/語.txt").And.Contain("dir with space/file name.txt"));
        });
    }

    [Test]
    public async Task DiffWorking_WithPaths_FileDirectoryTrailingSlashAndNoMatch_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "changed\n");
        Write(_repo, "dir/c.txt", "changed\n");
        Write(_repo, "dir/sub/d.txt", "new\n");
        RunGit(_repo, "add", "dir/sub/d.txt");
        Write(_repo, "dir/sub/d.txt", "changed after add\n");

        var file = await DiffBothAsync(_repo, "working", "a.txt");
        var directory = await DiffBothAsync(_repo, "working", "dir");
        var slash = await DiffBothAsync(_repo, "working", "dir/");
        var several = await DiffBothAsync(_repo, "working", "[\"a.txt\",\"dir/sub\"]");
        var none = await DiffBothAsync(_repo, "working", "nothing/here.txt");
        var csv = await DiffBothAsync(_repo, "working", "a.txt,dir/c.txt");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(file), Is.EqualTo(new[] { "M\ta.txt" }));
            Assert.That(Lines(directory), Is.EqualTo(new[] { "M\tdir/c.txt", "M\tdir/sub/d.txt" }), "unstaged changes: d.txt was staged new, then changed again");
            Assert.That(Lines(slash), Is.EqualTo(Lines(directory)));
            Assert.That(Lines(several), Is.EqualTo(new[] { "M\ta.txt", "M\tdir/sub/d.txt" }));
            Assert.That(none.Diff, Is.Empty);
            Assert.That(Lines(csv), Is.EqualTo(new[] { "M\ta.txt", "M\tdir/c.txt" }));
        });
    }

    [Test]
    public async Task DiffWorking_GlobPathspec_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "changed\n");

        var result = await DiffBothAsync(_repo, "working", "*.txt");

        Assert.That(Lines(result), Is.EqualTo(new[] { "M\ta.txt" }));
    }

    [Test]
    public async Task DiffStaged_GlobPathspec_IsAnsweredByTheCliAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "changed\n");
        RunGit(_repo, "add", "a.txt");

        var result = await DiffCliRoutedAsync(_repo, "staged", "*.txt");

        Assert.That(Lines(result), Is.EqualTo(new[] { "M\ta.txt" }));
    }

    // The working-tree comparisons (working, unstaged, a single ref) stay with git. libgit2's index-to-workdir diff, like its status, lists a stat-dirty file whose
    // only difference is EOL conversion as modified; git diff lists nothing for it (git status does, which is why the status read still agrees with git).
    [Test]
    public async Task DiffWorkingTreeTargets_StayWithGit_NeverReachTheReaderAsync()
    {
        RunGit(_repo, "config", "core.autocrlf", "true");
        Write(_repo, "a.txt", "line1\nline2\n");
        Write(_repo, "b.txt", "b\n");
        CommitAll(_repo, "lf in the repository");
        Write(_repo, "a.txt", "line1\r\nline2\r\n");
        Write(_repo, "b.txt", "changed\r\n");
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });

        var working = await Diff(composite, _repo, "working", null);
        var unstaged = await Diff(composite, _repo, "unstaged", null);
        var reference = await Diff(composite, _repo, "HEAD", null);

        Assert.Multiple(() =>
        {
            Assert.That(Lines(working), Is.EqualTo(new[] { "M\tb.txt" }));
            Assert.That(unstaged.Diff, Is.EqualTo(working.Diff));
            Assert.That(reference.Diff, Is.EqualTo(working.Diff));
            Assert.That(composite.Libgit2Count, Is.Zero);
            Assert.That(composite.CliFallbackCount, Is.Zero, "the tool does not even ask the reader");
            Assert.That(composite.DeclineCount, Is.Zero, "the tool does not even ask the reader");
        });
    }

    [Test]
    public void Libgit2Reader_DeclinesComparisonsWithTheWorkingTree()
    {
        CommitBaseline();

        Assert.Multiple(() =>
        {
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().GetNameStatus(_repo, "working", null, Timeout));
            Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().GetNameStatus(_repo, "HEAD", null, Timeout));
            Assert.That(new CliGitReader().GetNameStatus(_repo, "working", null, Timeout), Is.Empty);
        });
    }

    [Test]
    public async Task DiffWorking_BinaryFiles_BothBackendsAgreeAsync()
    {
        WriteBytes(_repo, "bin.dat", [0, 1, 2, 3, 0, 255]);
        Write(_repo, "a.txt", "a\n");
        CommitAll(_repo, "binary");
        WriteBytes(_repo, "bin.dat", [0, 1, 2, 3, 0, 254, 7]);
        Write(_repo, "a.txt", "changed\n");

        var result = await DiffBothAsync(_repo, "working");

        Assert.That(Lines(result), Is.EqualTo(new[] { "M\ta.txt", "M\tbin.dat" }));
    }

    [Test]
    public async Task DiffWorking_AutocrlfStatDirtyFileWithOnlyEolConversion_IsNotListedByEitherBackendAsync()
    {
        RunGit(_repo, "config", "core.autocrlf", "true");
        Write(_repo, "a.txt", "line1\nline2\n");
        Write(_repo, "b.txt", "b\n");
        CommitAll(_repo, "lf in the repository");
        // The working copy now has CRLF line endings and a new modification time: stat-dirty, but the same content once git converts it back.
        Write(_repo, "a.txt", "line1\r\nline2\r\n");
        Write(_repo, "b.txt", "changed\r\n");

        var result = await DiffBothAsync(_repo, "working");

        Assert.That(Lines(result), Is.EqualTo(new[] { "M\tb.txt" }), "a.txt differs only by its line endings, which git does not count");
    }

    [Test]
    public async Task DiffWorking_GitattributesTextEolCrlf_StatDirtyFileIsNotListedByEitherBackendAsync()
    {
        Write(_repo, ".gitattributes", "*.txt text eol=crlf\n*.bin -text\n");
        Write(_repo, "a.txt", "line1\nline2\n");
        Write(_repo, "b.txt", "b\n");
        Write(_repo, "raw.bin", "x\r\ny\n");
        CommitAll(_repo, "attributes");
        Write(_repo, "a.txt", "line1\r\nline2\r\n");
        Write(_repo, "raw.bin", "x\ny\n"); // -text: the line-ending change is a real change here
        Write(_repo, "b.txt", "changed\r\n");

        var result = await DiffBothAsync(_repo, "working");

        Assert.That(Lines(result), Is.EqualTo(new[] { "M\tb.txt", "M\traw.bin" }));
    }

    [Test]
    public async Task DiffWorking_TouchedFileWithIdenticalContent_IsNotListedAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "a\n"); // same bytes, new modification time

        var result = await DiffBothAsync(_repo, "working");

        Assert.That(result.Diff, Is.Empty);
    }

    [Test]
    public async Task DiffWorking_UnbornBranch_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "a\n");
        RunGit(_repo, "add", "a.txt");

        var result = await DiffBothAsync(_repo, "working");

        Assert.That(result.IsError, Is.False);
    }

    [Test]
    public async Task DiffWorking_IntentToAddFile_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "later.txt", "later\n");
        RunGit(_repo, "add", "-N", "later.txt");

        var result = await DiffBothAsync(_repo, "working");

        Assert.That(result.IsError, Is.False);
    }

    // ---- diff nameOnly: staged ----

    [Test]
    public async Task DiffStaged_AddedModifiedAndSeparateDeletes_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "new.txt", "n\n");
        Write(_repo, "a.txt", "changed\n");
        RunGit(_repo, "add", "-A");
        Write(_repo, "b.txt", "unstaged only\n");

        var addedAndModified = await DiffBothAsync(_repo, "staged");
        Commit(_repo, "second");
        RunGit(_repo, "rm", "--quiet", "dir/c.txt");
        var deleted = await DiffBothAsync(_repo, "staged");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(addedAndModified), Is.EqualTo(new[] { "M\ta.txt", "A\tnew.txt" }));
            Assert.That(Lines(deleted), Is.EqualTo(new[] { "D\tdir/c.txt" }));
        });
    }

    [Test]
    public async Task DiffStaged_IntentToAddFile_IsAnsweredByTheCliAsync()
    {
        CommitBaseline();
        Write(_repo, "later.txt", "later\n");
        RunGit(_repo, "add", "-N", "later.txt");
        Write(_repo, "a.txt", "changed\n");
        RunGit(_repo, "add", "a.txt");

        // git diff --cached leaves an intent-to-add path out; libgit2 would list it as added.
        var result = await DiffCliRoutedAsync(_repo, "staged");

        Assert.That(Lines(result), Is.EqualTo(new[] { "M\ta.txt" }));
    }

    [Test]
    public async Task DiffStaged_WithPaths_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "changed\n");
        Write(_repo, "dir/c.txt", "changed\n");
        Write(_repo, "dir/new file.txt", "n\n");
        RunGit(_repo, "add", "-A");

        var file = await DiffBothAsync(_repo, "staged", "a.txt");
        var directory = await DiffBothAsync(_repo, "staged", "dir");
        var slash = await DiffBothAsync(_repo, "staged", "dir/");
        var several = await DiffBothAsync(_repo, "staged", "[\"a.txt\",\"dir/new file.txt\"]");
        var none = await DiffBothAsync(_repo, "staged", "b.txt");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(file), Is.EqualTo(new[] { "M\ta.txt" }));
            Assert.That(Lines(directory), Is.EqualTo(new[] { "M\tdir/c.txt", "A\tdir/new file.txt" }));
            Assert.That(Lines(slash), Is.EqualTo(Lines(directory)));
            Assert.That(Lines(several), Is.EqualTo(new[] { "M\ta.txt", "A\tdir/new file.txt" }));
            Assert.That(none.Diff, Is.Empty);
        });
    }

    [Test]
    public async Task DiffStaged_TypeChange_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_root, "link-target.txt", "a.txt");
        var blob = RunGit(_repo, "hash-object", "-w", Path.Combine(_root, "link-target.txt"));
        RunGit(_repo, "update-index", "--cacheinfo", $"120000,{blob},b.txt");

        var result = await DiffBothAsync(_repo, "staged");

        Assert.That(Lines(result), Is.EqualTo(new[] { "T\tb.txt" }));
    }

    [Test]
    public async Task DiffStaged_ExecutableBitChange_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        RunGit(_repo, "update-index", "--chmod=+x", "a.txt");

        var result = await DiffBothAsync(_repo, "staged");

        Assert.That(Lines(result), Is.EqualTo(new[] { "M\ta.txt" }));
    }

    [Test]
    public async Task DiffStaged_RenameAndAddedPlusDeletedPaths_AreAnsweredByTheCliAsync()
    {
        CommitBaseline();
        RunGit(_repo, "mv", "a.txt", "renamed.txt");

        var result = await DiffCliRoutedAsync(_repo, "staged");

        Assert.That(result.Diff, Does.StartWith("R100\ta.txt\trenamed.txt"));
    }

    [Test]
    public async Task DiffStaged_AddedAndDeletedUnrelatedFiles_AreAnsweredByTheCliAsync()
    {
        CommitBaseline();
        RunGit(_repo, "rm", "--quiet", "b.txt");
        Write(_repo, "unrelated.txt", "something entirely different, long enough to not match\nmore\nmore\n");
        RunGit(_repo, "add", "unrelated.txt");

        var result = await DiffCliRoutedAsync(_repo, "staged");

        Assert.That(Lines(result), Is.EqualTo(new[] { "D\tb.txt", "A\tunrelated.txt" }));
    }

    [Test]
    public async Task DiffStaged_UnbornBranch_IsAnsweredByTheCliAsync()
    {
        Write(_repo, "a.txt", "a\n");
        RunGit(_repo, "add", "a.txt");

        var result = await DiffCliRoutedAsync(_repo, "staged");

        Assert.That(Lines(result), Is.EqualTo(new[] { "A\ta.txt" }));
    }

    [Test]
    public async Task DiffStaged_NameGitQuotes_IsAnsweredByTheCliOrServedIdenticallyAsync()
    {
        CommitBaseline();
        Write(_root, "blob.txt", "quote\n");
        var blob = RunGit(_repo, "hash-object", "-w", Path.Combine(_root, "blob.txt"));
        try
        {
            RunGit(_repo, "update-index", "--add", "--cacheinfo", $"100644,{blob},we\"ird.txt");
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore("this platform's git refuses a quote in a path");
        }

        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });
        var viaComposite = await Diff(composite, _repo, "staged", null);
        var viaCli = await Diff(new CliGitReader(), _repo, "staged", null);

        Assert.Multiple(() =>
        {
            Assert.That(Serialize(viaComposite), Is.EqualTo(Serialize(viaCli)));
            Assert.That(composite.DeclineCount, Is.EqualTo(1), "a name git prints quoted is left to git");
            Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
        });
    }

    // ---- diff nameOnly: ref and range ----

    [Test]
    public async Task DiffRef_HeadBranchTagAndHash_AgainstTheWorkingTree_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        RunGit(_repo, "tag", "v1");
        Write(_repo, "a.txt", "second\n");
        Write(_repo, "extra.txt", "extra\n");
        var second = CommitAll(_repo, "second");
        RunGit(_repo, "branch", "feature/x", "HEAD~1");
        Write(_repo, "b.txt", "working change\n");
        Write(_repo, "staged-new.txt", "s\n");
        RunGit(_repo, "add", "staged-new.txt");
        Write(_repo, "untracked.txt", "u\n");

        var head = await DiffBothAsync(_repo, "HEAD");
        var parent = await DiffBothAsync(_repo, "HEAD~1");
        var tag = await DiffBothAsync(_repo, "v1");
        var branch = await DiffBothAsync(_repo, "feature/x");
        var hash = await DiffBothAsync(_repo, second[..10]);

        Assert.Multiple(() =>
        {
            Assert.That(Lines(head), Is.EqualTo(new[] { "M\tb.txt", "A\tstaged-new.txt" }), "staged-new counts, untracked does not");
            Assert.That(Lines(parent), Is.EqualTo(new[] { "M\ta.txt", "M\tb.txt", "A\textra.txt", "A\tstaged-new.txt" }));
            Assert.That(tag.Diff, Is.EqualTo(parent.Diff));
            Assert.That(branch.Diff, Is.EqualTo(parent.Diff));
            Assert.That(hash.Diff, Is.EqualTo(head.Diff));
        });
    }

    [Test]
    public async Task DiffRef_DeletedFileInTheWorkingTree_AndWithPaths_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        File.Delete(Path.Combine(_repo, "b.txt"));
        Write(_repo, "a.txt", "changed\n");

        var all = await DiffBothAsync(_repo, "HEAD");
        var limited = await DiffBothAsync(_repo, "HEAD", "b.txt");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(all), Is.EqualTo(new[] { "M\ta.txt", "D\tb.txt" }));
            Assert.That(Lines(limited), Is.EqualTo(new[] { "D\tb.txt" }));
        });
    }

    [Test]
    public async Task DiffRange_TwoRefs_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        RunGit(_repo, "tag", "base");
        Write(_repo, "a.txt", "second\n");
        Write(_repo, "added.txt", "x\n");
        CommitAll(_repo, "second");
        RunGit(_repo, "rm", "--quiet", "b.txt");
        Commit(_repo, "third");
        Write(_repo, "working-only.txt", "ignored by a range\n");

        var range = await DiffBothAsync(_repo, "HEAD~2..HEAD~1");
        var rangeTag = await DiffBothAsync(_repo, "base..HEAD~1");
        var reversed = await DiffBothAsync(_repo, "HEAD~1..HEAD~2");
        var limited = await DiffBothAsync(_repo, "HEAD~2..HEAD~1", "a.txt");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(range), Is.EqualTo(new[] { "A\tadded.txt", "M\ta.txt" }.OrderBy(l => l[2..], StringComparer.Ordinal).ToArray()));
            Assert.That(rangeTag.Diff, Is.EqualTo(range.Diff));
            Assert.That(Lines(reversed), Has.Length.EqualTo(2));
            Assert.That(Lines(limited), Is.EqualTo(new[] { "M\ta.txt" }));
        });
    }

    [Test]
    public async Task DiffRange_RenameBetweenRefs_IsAnsweredByTheCliAsync()
    {
        CommitBaseline();
        RunGit(_repo, "tag", "base");
        RunGit(_repo, "mv", "a.txt", "moved.txt");
        Commit(_repo, "move");

        var result = await DiffCliRoutedAsync(_repo, "base..HEAD");

        Assert.That(result.Diff, Does.StartWith("R100\ta.txt\tmoved.txt"));
    }

    [Test]
    public async Task DiffRange_ThreeDotsEmptySideOrUnresolvableRevisionForm_IsAnsweredByTheCliAsync()
    {
        CommitBaseline();
        RunGit(_repo, "branch", "side");
        Write(_repo, "a.txt", "second\n");
        CommitAll(_repo, "second");

        // Never sent to the reader by the tool (merge-base form, tree-ish): git answers directly.
        var threeDots = await DiffBothAsync(_repo, "side...HEAD");
        var caret = await DiffBothAsync(_repo, "HEAD^{tree}");
        // Sent to the reader, which declines an empty side (git reads it as HEAD).
        var emptySide = await DiffCliRoutedAsync(_repo, "side..");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(threeDots), Is.EqualTo(new[] { "M\ta.txt" }));
            Assert.That(emptySide.IsError, Is.False);
            Assert.That(caret.IsError, Is.False);
        });
    }

    [Test]
    public async Task DiffRef_UnknownRevision_ReportsGitsOwnErrorFromBothBackendsAsync()
    {
        CommitBaseline();

        var lib = await Diff(new Libgit2GitReader(), _repo, "no-such-ref", null);
        var cli = await Diff(new CliGitReader(), _repo, "no-such-ref", null);

        Assert.Multiple(() =>
        {
            Assert.That(cli.IsError, Is.True);
            Assert.That(cli.Error, Does.Contain("Valid diff targets"));
            Assert.That(Serialize(lib), Is.EqualTo(Serialize(cli)));
        });
    }

    [Test]
    public async Task DiffRange_NameThatIsAlsoAFile_IsAnsweredByTheCliAndWithPathsByBothAsync()
    {
        CommitBaseline();
        RunGit(_repo, "branch", "feature");
        Write(_repo, "a.txt", "changed\n");
        Write(_repo, "feature..HEAD", "a file named like the range\n");
        CommitAll(_repo, "second");

        var ambiguous = await DiffCliRoutedAsync(_repo, "feature..HEAD");
        var unambiguous = await DiffBothAsync(_repo, "feature..HEAD", "a.txt");

        Assert.Multiple(() =>
        {
            Assert.That(ambiguous.IsError, Is.True, "git refuses an argument that is both a revision range and a path");
            Assert.That(Lines(unambiguous), Is.EqualTo(new[] { "M\ta.txt" }));
        });
    }

    [Test]
    public async Task Diff_ConfiguredRenamesOrOrderFile_IsAnsweredByTheCliAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "changed\n");
        RunGit(_repo, "add", "a.txt");
        RunGit(_repo, "config", "diff.renames", "false");

        var renames = await DiffCliRoutedAsync(_repo, "staged");
        RunGit(_repo, "config", "--unset", "diff.renames");
        Write(_root, "order.txt", "b.txt\na.txt\n");
        RunGit(_repo, "config", "diff.orderFile", Path.Combine(_root, "order.txt").Replace('\\', '/'));
        Write(_repo, "b.txt", "changed\n");
        RunGit(_repo, "add", "b.txt");
        var order = await DiffCliRoutedAsync(_repo, "staged");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(renames), Is.EqualTo(new[] { "M\ta.txt" }));
            Assert.That(Lines(order), Is.EqualTo(new[] { "M\tb.txt", "M\ta.txt" }), "git sorts by the order file, which the libgit2 reader does not");
        });
    }

    [Test]
    public async Task Diff_UnmergedPaths_AreAnsweredByTheCliAsync()
    {
        CommitBaseline();
        RunGit(_repo, "checkout", "--quiet", "-b", "other");
        Write(_repo, "a.txt", "other side\n");
        CommitAll(_repo, "other");
        RunGit(_repo, "checkout", "--quiet", "-");
        Write(_repo, "a.txt", "this side\n");
        CommitAll(_repo, "this");
        try
        {
            RunGit(_repo, [.. Identity, "merge", "other"]);
        }
        catch (InvalidOperationException)
        {
            // the conflict makes git exit non-zero, which is the state under test
        }

        var working = await DiffBothAsync(_repo, "working");
        var staged = await DiffCliRoutedAsync(_repo, "staged");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(working).Any(line => line.StartsWith("U\ta.txt", StringComparison.Ordinal)), Is.True, working.Diff);
            Assert.That(Lines(staged).Any(line => line.StartsWith("U\ta.txt", StringComparison.Ordinal)), Is.True, staged.Diff);
        });
    }

    [Test]
    public async Task Diff_RepositoryWithASubmodule_IsAnsweredByTheCliAsync()
    {
        var upstream = Path.Combine(_root, "upstream");
        InitRepo(upstream);
        Write(upstream, "u.txt", "u\n");
        CommitAll(upstream, "upstream");
        CommitBaseline();
        try
        {
            RunGit(_repo, "-c", "protocol.file.allow=always", "submodule", "add", "--quiet", upstream, "sub");
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore("git could not add a local submodule here");
        }

        // The submodule and .gitmodules are staged: the gitlink is the 'A' libgit2 leaves to git.
        var staged = await DiffCliRoutedAsync(_repo, "staged");
        Commit(_repo, "with submodule");
        var range = await DiffCliRoutedAsync(_repo, "HEAD~1..HEAD");
        var show = await ShowCliRoutedAsync(_repo, "HEAD");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(staged), Is.EqualTo(new[] { "A\t.gitmodules", "A\tsub" }));
            Assert.That(Lines(range), Is.EqualTo(Lines(staged)));
            Assert.That(Lines(show), Is.EqualTo(Lines(staged)));
        });
    }

    [Test]
    public async Task Diff_LinkedWorktreeViaItsOwnPath_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        var worktree = Path.Combine(_root, "wt");
        RunGit(_repo, "worktree", "add", "--quiet", worktree, "-b", "wt-branch");
        Write(worktree, "a.txt", "changed in the worktree\n");
        Write(worktree, "wt-only.txt", "w\n");
        RunGit(worktree, "add", "wt-only.txt");
        Write(_repo, "b.txt", "changed in the primary\n");

        var inWorktree = await DiffBothAsync(worktree, "working");
        var stagedInWorktree = await DiffBothAsync(worktree, "staged");
        var inPrimary = await DiffBothAsync(_repo, "working");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(inWorktree), Is.EqualTo(new[] { "M\ta.txt" }));
            Assert.That(Lines(stagedInWorktree), Is.EqualTo(new[] { "A\twt-only.txt" }));
            Assert.That(Lines(inPrimary), Is.EqualTo(new[] { "M\tb.txt" }));
        });
    }

    // ---- show: header and nameOnly ----

    [Test]
    public async Task Show_RootCommitAndOrdinaryCommit_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        var root = RunGit(_repo, "rev-parse", "HEAD");
        Write(_repo, "a.txt", "second\n");
        Write(_repo, "é new.txt", "n\n");
        CommitAll(_repo, "second commit\n\nwith a body\nand more lines\n");

        var rootShow = await ShowBothAsync(_repo, root);
        var head = await ShowBothAsync(_repo, "HEAD");
        var byBranch = await ShowBothAsync(_repo, RunGit(_repo, "rev-parse", "--abbrev-ref", "HEAD"));

        Assert.Multiple(() =>
        {
            Assert.That(Lines(rootShow), Is.EqualTo(new[] { "A\ta.txt", "A\tb.txt", "A\tdir/c.txt" }));
            Assert.That(rootShow.Message, Is.EqualTo("baseline"));
            Assert.That(Lines(head), Is.EqualTo(new[] { "M\ta.txt", "A\té new.txt" }.OrderBy(l => l[2..], StringComparer.Ordinal).ToArray()));
            Assert.That(head.Message, Is.EqualTo("second commit\n\nwith a body\nand more lines"));
            Assert.That(byBranch.Hash, Is.EqualTo(head.Hash));
        });
    }

    [Test]
    public async Task Show_HeaderFieldsAuthorDateOffsetsAndMessages_BothBackendsAgreeAsync()
    {
        Write(_repo, "a.txt", "1\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "utc commit", "2020-12-31T00:00:00+0000");
        Write(_repo, "a.txt", "2\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "plus offset\r\n\r\nCRLF body\r\n", "2021-03-04T05:06:07+0530");
        Write(_repo, "a.txt", "3\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "minus offset 日本語 \U0001F600", "2021-06-07T23:59:59-0800");
        Write(_repo, "a.txt", "4\n");
        RunGit(_repo, "add", "-A");
        Commit(_repo, "", "2022-01-01T12:00:00+0100"); // an empty message
        Write(_repo, "a.txt", "5\n");
        RunGit(_repo, "add", "-A");
        RunGitEnv(_repo, new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = "Émile Zürich", ["GIT_AUTHOR_EMAIL"] = "e@example.com", ["GIT_COMMITTER_NAME"] = "C", ["GIT_COMMITTER_EMAIL"] = "c@example.com" },
            [.. Identity, "commit", "--quiet", "-m", "non-ASCII author"]);

        var utc = await ShowBothAsync(_repo, "HEAD~4");
        var plus = await ShowBothAsync(_repo, "HEAD~3");
        var minus = await ShowBothAsync(_repo, "HEAD~2");
        var empty = await ShowBothAsync(_repo, "HEAD~1");
        var author = await ShowBothAsync(_repo, "HEAD");

        Assert.Multiple(() =>
        {
            Assert.That(utc.Date, Is.AnyOf("2020-12-31T00:00:00Z", "2020-12-31T00:00:00+00:00"));
            Assert.That(plus.Date, Is.EqualTo("2021-03-04T05:06:07+05:30"));
            Assert.That(minus.Date, Is.EqualTo("2021-06-07T23:59:59-08:00"));
            Assert.That(minus.Message, Does.Contain("日本語"));
            Assert.That(empty.Message, Is.Empty);
            Assert.That(author.Author, Is.EqualTo("Émile Zürich"));
        });
    }

    [Test]
    public async Task Show_MergeCommit_ListsTheDiffAgainstTheFirstParentAsync()
    {
        CommitBaseline();
        RunGit(_repo, "checkout", "--quiet", "-b", "side");
        Write(_repo, "side.txt", "s\n");
        CommitAll(_repo, "side work");
        RunGit(_repo, "checkout", "--quiet", "-");
        Write(_repo, "main.txt", "m\n");
        CommitAll(_repo, "main work");
        RunGit(_repo, [.. Identity, "merge", "--no-ff", "--quiet", "-m", "merge side", "side"]);

        var merge = await ShowBothAsync(_repo, "HEAD");
        var limited = await ShowBothAsync(_repo, "HEAD", "side.txt");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(merge), Is.EqualTo(new[] { "A\tside.txt" }), "diff against the first parent: what the merge brought in");
            Assert.That(merge.Message, Is.EqualTo("merge side"));
            Assert.That(Lines(limited), Is.EqualTo(new[] { "A\tside.txt" }));
        });
    }

    [Test]
    public async Task Show_WithPathsAndNonAsciiAndSpacePaths_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        Write(_repo, "dir with space/ü.txt", "u\n");
        Write(_repo, "a.txt", "changed\n");
        Write(_repo, "dir/c.txt", "changed\n");
        CommitAll(_repo, "mixed");

        var space = await ShowBothAsync(_repo, "HEAD", "dir with space");
        var several = await ShowBothAsync(_repo, "HEAD", "[\"a.txt\",\"dir/c.txt\"]");
        var none = await ShowBothAsync(_repo, "HEAD", "b.txt");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(space), Is.EqualTo(new[] { "A\tdir with space/ü.txt" }));
            Assert.That(Lines(several), Is.EqualTo(new[] { "M\ta.txt", "M\tdir/c.txt" }));
            Assert.That(none.Diff, Is.Empty);
        });
    }

    [Test]
    public async Task Show_RenameInTheCommit_IsAnsweredByTheCliAsync()
    {
        CommitBaseline();
        RunGit(_repo, "mv", "a.txt", "renamed.txt");
        Commit(_repo, "rename");

        var result = await ShowCliRoutedAsync(_repo, "HEAD");

        Assert.That(result.Diff, Does.StartWith("R100\ta.txt\trenamed.txt"));
    }

    [Test]
    public async Task Show_AnnotatedTagTarget_HeaderIsAnsweredByTheCliAsync()
    {
        CommitBaseline();
        RunGit(_repo, [.. Identity, "tag", "-a", "v1", "-m", "release one"]);
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });

        var viaComposite = await Show(composite, _repo, "v1");
        var viaCli = await Show(new CliGitReader(), _repo, "v1");

        Assert.Multiple(() =>
        {
            Assert.That(Serialize(viaComposite), Is.EqualTo(Serialize(viaCli)));
            Assert.That(composite.DeclineCount, Is.GreaterThanOrEqualTo(1), "git prints the tag's own header for an annotated tag");
            Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
        });
    }

    [Test]
    public async Task Show_LightweightTagAndUnknownRevision_BothBackendsAgreeAsync()
    {
        CommitBaseline();
        RunGit(_repo, "tag", "light");

        var tag = await ShowBothAsync(_repo, "light");
        var lib = await Show(new Libgit2GitReader(), _repo, "no-such-rev");
        var cli = await Show(new CliGitReader(), _repo, "no-such-rev");

        Assert.Multiple(() =>
        {
            Assert.That(tag.IsError, Is.False);
            Assert.That(cli.IsError, Is.True);
            Assert.That(Serialize(lib), Is.EqualTo(Serialize(cli)));
        });
    }

    [Test]
    public async Task Show_StatAndPatch_ReachTheCliAndNeverTheNameStatusReadAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "changed\n");
        CommitAll(_repo, "change a");
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });

        var stat = await Show(composite, _repo, "HEAD", null, nameOnly: false, stat: true);
        var patch = await Show(composite, _repo, "HEAD", null, nameOnly: false, stat: false);
        var viaCliStat = await Show(new CliGitReader(), _repo, "HEAD", null, nameOnly: false, stat: true);
        var viaCliPatch = await Show(new CliGitReader(), _repo, "HEAD", null, nameOnly: false, stat: false);

        Assert.Multiple(() =>
        {
            Assert.That(stat.Diff, Does.Contain("a.txt").And.Contain("1 file changed"));
            Assert.That(patch.Diff, Does.StartWith("diff --git a/a.txt b/a.txt"));
            Assert.That(Serialize(stat), Is.EqualTo(Serialize(viaCliStat)));
            Assert.That(Serialize(patch), Is.EqualTo(Serialize(viaCliPatch)));
            Assert.That(composite.Libgit2Count, Is.EqualTo(2), "only the header was read through libgit2, once per call");
            Assert.That(composite.CliFallbackCount, Is.Zero);
            Assert.That(composite.DeclineCount, Is.Zero, "stat and patch are never asked of the reader, so nothing was declined");
        });
    }

    [Test]
    public async Task Diff_StatAndPatch_NeverReachTheReaderAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "changed\n");
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });

        var stat = await Diff(composite, _repo, "working", null, nameOnly: false, stat: true);
        var patch = await Diff(composite, _repo, "working", null, nameOnly: false, stat: false);

        Assert.Multiple(() =>
        {
            Assert.That(stat.Diff, Does.Contain("1 file changed"));
            Assert.That(patch.Diff, Does.Contain("+changed"));
            Assert.That(composite.Libgit2Count, Is.Zero);
            Assert.That(composite.CliFallbackCount, Is.Zero);
            Assert.That(composite.DeclineCount, Is.Zero);
        });
    }

    // ---- which backend serves ----

    [Test]
    public async Task DiffAndShowNameOnly_InTheNormalEnvironment_AreServedByLibgit2NotTheFallbackAsync()
    {
        CommitBaseline();
        RunGit(_repo, "branch", "side");
        Write(_repo, "a.txt", "second\n");
        CommitAll(_repo, "second");
        Write(_repo, "b.txt", "working\n");
        Write(_repo, "staged.txt", "s\n");
        RunGit(_repo, "add", "staged.txt");
        var composite = new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { });

        var staged = await Diff(composite, _repo, "staged", null);
        var range = await Diff(composite, _repo, "side..HEAD", null);
        var show = await Show(composite, _repo, "HEAD");

        Assert.Multiple(() =>
        {
            Assert.That(Lines(staged), Is.EqualTo(new[] { "A\tstaged.txt" }));
            Assert.That(Lines(range), Is.EqualTo(new[] { "M\ta.txt" }));
            Assert.That(Lines(show), Is.EqualTo(new[] { "M\ta.txt" }));
            Assert.That(composite.FirstFallbackReason, Is.Null, "libgit2 must serve these reads; a CLI answer means a silent fallback");
            Assert.That(composite.CliFallbackCount, Is.Zero);
            Assert.That(composite.DeclineCount, Is.Zero, "libgit2 must serve these reads; a decline would hand them to the CLI");
            Assert.That(composite.Libgit2Count, Is.EqualTo(4), "the staged and range file lists plus the show header and the show file list");
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
        });
    }

    [Test]
    public async Task DiffNameOnly_ThroughTheProductionDefaultReader_CountsALibgit2CallAsync()
    {
        CommitBaseline();
        Write(_repo, "a.txt", "changed\n");
        RunGit(_repo, "add", "a.txt");

        var before = GitReaders.Default.Libgit2Count;
        var result = await new GitImpl(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly)).DiffAsync(_repo, "staged", null, 65536, true, false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(GitReaders.Default.Libgit2Count, Is.GreaterThan(before), "the public GitImpl constructor must route the file list through GitReaders.Default");
        });
    }
}
