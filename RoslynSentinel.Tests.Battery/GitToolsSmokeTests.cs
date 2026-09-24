// Smoke coverage for the Git tool's read-only operations (status/log/diff). Not a repro attempt for
// the unreproduced one-off "Git(operation: status) hung indefinitely" TODO entry -> the 30s
// GitProcessTimeout added for that entry already gives it a bounded failure mode. The goal here is
// just confirming each operation responds well within that bound against a real git repo, so a
// regression that made every Git call slow/hang would fail fast in CI instead of only surfacing
// live via a 30s timeout during actual use.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class GitToolsSmokeTests
{
    // Generous relative to GitProcessTimeout's 30s -> this isn't testing the timeout boundary
    // itself, just that a normal call on a tiny repo comes back promptly, not near the ceiling.
    private static readonly TimeSpan ResponseBound = TimeSpan.FromSeconds(10);

    private string _repoDir = null!;
    private GitTools _gitTools = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitSmoke_" + Guid.NewGuid());
        Directory.CreateDirectory(_repoDir);
        RunGit(_repoDir, "init");
        RunGit(_repoDir, "config", "user.email", "test@example.com");
        RunGit(_repoDir, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "hello");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "initial commit");

        // GitTools only ever calls GetSolutionRoot() to find the git root - it doesn't need a real
        // Roslyn solution loaded, so FakeWorkspaceManager.SolutionPath alone is enough (see its own
        // "Mirrors PersistentWorkspaceManager.GetSolutionRoot()" comment).
        var workspaceManager = new FakeWorkspaceManager { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitTools = new GitTools(workspaceManager, NullLogger<GitTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_repoDir))
        {
            // Windows can leave .git's object files read-only; clear that before recursive delete.
            foreach (var file in Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repoDir, recursive: true);
        }
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed with exit code {process.ExitCode} in '{workingDirectory}'.");
        }
    }

    [Test]
    public async Task Git_Status_RespondsWithinBoundAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _gitTools.Git(reason: "test message", GitOperation.status);
        sw.Stop();

        Assert.That(result, Is.Not.Null);
        Assert.That(sw.Elapsed, Is.LessThan(ResponseBound), $"Git(status) took {sw.Elapsed}, expected under {ResponseBound}.");
    }

    [Test]
    public async Task Git_Log_RespondsWithinBoundAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _gitTools.Git(reason: "test message", GitOperation.log, count: 5);
        sw.Stop();

        Assert.That(result, Is.Not.Null);
        Assert.That(sw.Elapsed, Is.LessThan(ResponseBound), $"Git(log) took {sw.Elapsed}, expected under {ResponseBound}.");
    }

    [Test]
    public async Task Git_Diff_RespondsWithinBoundAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "hello again");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _gitTools.Git(reason: "test message", GitOperation.diff);
        sw.Stop();

        Assert.That(result, Is.Not.Null);
        Assert.That(sw.Elapsed, Is.LessThan(ResponseBound), $"Git(diff) took {sw.Elapsed}, expected under {ResponseBound}.");
    }

    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task Git_Reset_Soft_MovesHeadAndRestagesChangesAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "second commit content");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "second commit");

        var result = await _gitTools.Git(reason: "test message", GitOperation.reset, mode: GitResetMode.soft);

        Assert.That(result, Is.Not.Null);
        var status = result.SuccessData as GitStatusResult;
        Assert.That(status?.Success, Is.True);
        Assert.That(status?.Staged.Select(s => s.Path), Does.Contain("README.md"),
            "git reset --soft should leave the second commit's change staged, not discarded.");

        var log = await _gitTools.Git(reason: "test message", GitOperation.log, count: 5);
        var logResult = log.SuccessData as GitLogResult;
        Assert.That(logResult?.Commits.Select(c => c.Message), Does.Not.Contain("second commit"),
            "git reset --soft should move HEAD past the second commit.");
    }

    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task Git_Reset_Mixed_UnstagesButKeepsWorkingTreeChangesAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "second commit content");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "second commit");

        var result = await _gitTools.Git(reason: "test message", GitOperation.reset, mode: GitResetMode.mixed);

        Assert.That(result, Is.Not.Null);
        var status = result.SuccessData as GitStatusResult;
        Assert.That(status?.Success, Is.True, status?.Error);
        Assert.That(status?.Staged, Is.Empty, "git reset --mixed should leave nothing staged.");
        Assert.That(status?.Unstaged.Select(s => s.Path), Does.Contain("README.md"),
            "git reset --mixed should leave the second commit's change unstaged, not discarded.");
        Assert.That(File.ReadAllText(Path.Combine(_repoDir, "README.md")), Is.EqualTo("second commit content"),
            "git reset --mixed must never touch the working tree.");
    }

    // Added by InsertMemberAfter (expected - used for diagnostics)
    [Test]
    public async Task Git_Status_RepoPath_TargetsADifferentRepoThanTheLoadedSolutionAsync()
    {
        var otherRepoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitSmoke_Other_" + Guid.NewGuid());
        Directory.CreateDirectory(otherRepoDir);
        try
        {
            RunGit(otherRepoDir, "init");
            RunGit(otherRepoDir, "config", "user.email", "test@example.com");
            RunGit(otherRepoDir, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(otherRepoDir, "OTHER.md"), "other repo");
            RunGit(otherRepoDir, "add", "-A");
            RunGit(otherRepoDir, "commit", "-m", "other repo initial commit");
            File.WriteAllText(Path.Combine(otherRepoDir, "OTHER.md"), "other repo, modified");

            // _gitTools' own loaded-solution repo (_repoDir) is clean at this point - if repoPath were
            // ignored and the tool fell back to _repoDir, this would incorrectly come back clean too.
            var result = await _gitTools.Git(reason: "test message", GitOperation.status, repoPath: otherRepoDir);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.SuccessData, Is.Not.Null);
            var status = (GitStatusResult)result.SuccessData;
            Assert.That(status.Success, Is.True, status.Error);
            Assert.That(status.IsClean, Is.False, "the other repo has an uncommitted change and should not report clean.");
            Assert.That(status.Unstaged.Select(s => s.Path), Does.Contain("OTHER.md"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(otherRepoDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(otherRepoDir, recursive: true);
        }
    }

    [Test]
    public async Task Git_Commit_RepoPath_IsRejectedAsync()
    {
        var result = await _gitTools.Git(reason: "test message", GitOperation.commit, message: "should be rejected", repoPath: _repoDir);

        Assert.That(result, Is.Not.Null);
        // The repoPath/mutating-op guard returns an anonymous { IsSuccess, ErrorDetails } object, not a
        // GitStatusResult - read it via reflection rather than assuming a concrete type.
        var successProperty = result.GetType().GetProperty("IsSuccess");
        Assert.That(successProperty, Is.Not.Null);
        Assert.That(successProperty!.GetValue(result), Is.EqualTo(false),
            "repoPath must only be accepted for status/log/diff/show - mutating operations should stay scoped to the loaded solution.");
    }


    // Added by AddMember (expected - used for diagnostics)
    // Regression coverage for docs/current/blockers/resolved/blocking_error_git_diff_mojibake_display.md:
    // RunGitAsync previously decoded git's redirected stdout/stderr using Console.OutputEncoding
    // (the legacy OS codepage) instead of UTF-8, so any non-ASCII multi-byte character in a diffed
    // file's content came back as mojibake. This asserts the actual UTF-8 character round-trips.
    [Test]
    public async Task Git_Diff_RoundTripsNonAsciiCharacterCorrectlyAsync()
    {
        // U+2014 EM DASH is UTF-8 encoded as the 3-byte sequence E2 80 94 - exactly the shape that
        // decoded one byte at a time through a legacy codepage, per the linked blocker doc.
        const string emDash = "—";
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), $"hello {emDash} again", System.Text.Encoding.UTF8);

        var result = await _gitTools.Git(reason: "test message", GitOperation.diff);

        Assert.That(result.IsSuccess, Is.True);
        var diff = (GitDiffResult)result.SuccessData!;
        Assert.That(diff.Success, Is.True, diff.Error);
        Assert.That(diff.Diff, Does.Contain(emDash),
            "the diff should contain the real UTF-8 em dash, not a mis-decoded mojibake substitute.");
        Assert.That(diff.Warning, Is.Null, "a correctly-decoded diff should not raise a corruption warning.");
    }


    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public async Task Git_Show_RoundTripsNonAsciiCharacterInFileContentCorrectlyAsync()
    {
        const string emDash = "—";
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), $"hello {emDash} again", System.Text.Encoding.UTF8);
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add non-ascii content");

        var result = await _gitTools.Git(reason: "test message", GitOperation.show, target: "HEAD");

        Assert.That(result.IsSuccess, Is.True);
        var show = (GitShowResult)result.SuccessData!;
        Assert.That(show.Success, Is.True, show.Error);
        Assert.That(show.Diff, Does.Contain(emDash),
            "git show's diff should contain the real UTF-8 em dash, not a mis-decoded mojibake substitute.");
        Assert.That(show.Warning, Is.Null, "a correctly-decoded show result should not raise a corruption warning.");
    }


    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public async Task Git_Diff_WarnsWhenOutputContainsReplacementCharacterAsync()
    {
        // Simulates a genuinely undecodable byte sequence reaching the diff text (rather than
        // re-triggering the original bug, which is now fixed) - if any future regression or a
        // different-cause decode failure inserts U+FFFD, the Warning field must surface it instead
        // of silently returning corrupted text.
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "hello � again");

        var result = await _gitTools.Git(reason: "test message", GitOperation.diff);

        Assert.That(result.IsSuccess, Is.True);
        var diff = (GitDiffResult)result.SuccessData!;
        Assert.That(diff.Success, Is.True, diff.Error);
        Assert.That(diff.Warning, Is.Not.Null.And.Contains("U+FFFD"),
            "a diff containing the Unicode replacement character must raise a decode-corruption warning.");
    }
}
