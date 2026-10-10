// Smoke coverage for the Git tool's read-only operations (status/log/diff). Not a repro attempt for
// the unreproduced one-off "Git(operation: status) hung indefinitely" TODO entry -> the 30s
// GitProcessTimeout added for that entry already gives it a bounded failure mode. The goal here is
// just confirming each operation responds well within that bound against a real git repo, so a
// regression that made every Git call slow/hang would fail fast in CI instead of only surfacing
// live via a 30s timeout during actual use.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")] // sentinel:auto-category
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
        DeleteDirectoryTree(_repoDir);
        DeleteDirectoryTree(RemoteDir);
        DeleteDirectoryTree(CloneDir);
        DeleteDirectoryTree(WorktreeDir);
    }

    // Sibling directories used by the remote-fixture tests (bare remote, second clone).
    private string RemoteDir => _repoDir + "_remote";

    private string CloneDir => _repoDir + "_clone";

    // Sibling directory used by the worktree tests (outside the repo root, as worktree add requires).
    private string WorktreeDir => _repoDir + "_wt";

    private static void DeleteDirectoryTree(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        // Windows can leave .git's object files read-only; clear that before recursive delete.
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
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

    // Captures git command output (stdout only), returns empty string on non-zero exit.
    private string RunGitCapture(params string[] args)
    {
        var (exitCode, output, _) = RunGitRaw(args);
        return exitCode == 0 ? output : string.Empty;
    }

    // Runs git in the test repo and returns exit code, stdout and stderr regardless of exit code.
    // stdin is redirected and closed immediately (and stderr drained): with stdin inherited from the
    // testhost, a git child spawned with only stdout redirected stalled forever. The bounded wait
    // turns any future stall into a fast, named failure instead of a hung test run.
    private (int ExitCode, string Stdout, string Stderr) RunGitRaw(params string[] args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', args)} did not exit within 20s in '{_repoDir}'.");
        }

        process.WaitForExit();
        return (process.ExitCode, outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult());
    }

    // Returns the list of file paths currently staged in the index.
    private List<string> StagedPaths()
    {
        // core.quotePath=false so non-ASCII names come back verbatim rather than octal-escaped.
        var output = RunGitCapture("-c", "core.quotePath=false", "diff", "--cached", "--name-only");
        return output.Split(new[] { '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
    }

    // Returns the list of file paths in HEAD (the current commit).
    private List<string> HeadPaths()
    {
        // --no-renames: without it a rename collapses to just the new path, hiding the deleted old path.
        var output = RunGitCapture("show", "--no-renames", "--name-only", "--format=", "HEAD");
        return output.Split(new[] { '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
    }

    // Writes a file at the relative path within the test repo.
    private void WriteFile(string relPath, string content)
    {
        var fullPath = Path.Combine(_repoDir, relPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(fullPath, content);
    }

    // Creates a .gitignore file in the repo root with the given patterns.
    private void CreateGitignore(string patterns)
    {
        WriteFile(".gitignore", patterns);
        RunGit(_repoDir, "add", ".gitignore");
        RunGit(_repoDir, "commit", "-m", "add .gitignore");
    }

    // Force-tracks a file under a gitignored path so it is included despite the ignore pattern.
    private void ForceTrackUnderGitignored(string relPath, string content)
    {
        WriteFile(relPath, content);
        RunGit(_repoDir, "add", "-f", relPath);
        RunGit(_repoDir, "commit", "-m", $"force-track {relPath}");
    }

    // Configures git to use CRLF mode and warn on conversion, for testing CRLF-related corner cases.
    private void EnableCrlfWarnings()
    {
        RunGit(_repoDir, "config", "core.autocrlf", "true");
        RunGit(_repoDir, "config", "core.safecrlf", "warn");
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

    [Test]
    public async Task Git_Diff_UnstagedAlias_MatchesWorkingAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "hello again");

        var unstagedResult = await _gitTools.Git(reason: "test message", GitOperation.diff, target: "unstaged");
        var workingResult = await _gitTools.Git(reason: "test message", GitOperation.diff, target: "working");

        Assert.That(!unstagedResult.IsError, Is.True);
        var unstagedDiff = (GitDiffResult)unstagedResult.SuccessData!;
        Assert.That(!unstagedDiff.IsError, Is.True, unstagedDiff.Error);

        Assert.That(!workingResult.IsError, Is.True);
        var workingDiff = (GitDiffResult)workingResult.SuccessData!;
        Assert.That(!workingDiff.IsError, Is.True, workingDiff.Error);

        Assert.That(unstagedDiff.Diff, Is.EqualTo(workingDiff.Diff), "unstaged and working should produce identical diffs");
        Assert.That(unstagedDiff.Diff, Does.Contain("README.md"), "diff should contain the modified file name");
    }

    [Test]
    public async Task Git_Diff_UnknownRef_ListsValidTargetsAsync()
    {
        var result = await _gitTools.Git(reason: "test message", GitOperation.diff, target: "nosuchref");

        Assert.That(result.IsError, Is.True, "diff with unknown ref should be an error");
        Assert.That(result.ErrorData?.Message, Does.Contain("Valid diff targets"), "error message should list valid diff targets");
    }

    [Test]
    [Category("GitCommitEntry")] // sentinel:auto-category
    [Category("GitLogResult")] // sentinel:auto-category
    [Category("GitResult")] // sentinel:auto-category
    [Category("GitStatusEntry")] // sentinel:auto-category
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Reset_Soft_MovesHeadAndRestagesChangesAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "second commit content");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "second commit");

        var result = await _gitTools.Git(reason: "test message", GitOperation.reset, mode: GitResetMode.soft, @ref: "HEAD~1");

        Assert.That(result, Is.Not.Null);
        var status = result.SuccessData as GitStatusResult;
        Assert.That(status, Is.Not.Null);
        Assert.That(!status!.IsError, Is.True);
        Assert.That(status.Staged.Select(s => s.Path), Does.Contain("README.md"),
            "git reset --soft should leave the second commit's change staged, not discarded.");

        var log = await _gitTools.Git(reason: "test message", GitOperation.log, count: 5);
        var logResult = log.SuccessData as GitLogResult;
        Assert.That(logResult?.Commits.Select(c => c.Message), Does.Not.Contain("second commit"),
            "git reset --soft should move HEAD past the second commit.");
    }

    [Test]
    [Category("GitResult")] // sentinel:auto-category
    [Category("GitStatusEntry")] // sentinel:auto-category
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Reset_Mixed_UnstagesButKeepsWorkingTreeChangesAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "second commit content");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "second commit");

        var result = await _gitTools.Git(reason: "test message", GitOperation.reset, mode: GitResetMode.mixed, @ref: "HEAD~1");

        Assert.That(result, Is.Not.Null);
        var status = result.SuccessData as GitStatusResult;
        Assert.That(status, Is.Not.Null);
        Assert.That(!status!.IsError, Is.True, status.Error);
        Assert.That(status.Staged, Is.Empty, "git reset --mixed should leave nothing staged.");
        Assert.That(status?.Unstaged.Select(s => s.Path), Does.Contain("README.md"),
            "git reset --mixed should leave the second commit's change unstaged, not discarded.");
        Assert.That(File.ReadAllText(Path.Combine(_repoDir, "README.md")), Is.EqualTo("second commit content"),
            "git reset --mixed must never touch the working tree.");
    }

    [Test]
    [Category("GitResult")] // sentinel:auto-category
    [Category("GitStatusEntry")] // sentinel:auto-category
    [Category("GitStatusResult")] // sentinel:auto-category
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
            Assert.That(!result.IsError, Is.True);
            Assert.That(result.SuccessData, Is.Not.Null);
            var status = (GitStatusResult)result.SuccessData;
            Assert.That(!status.IsError, Is.True, status.Error);
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
    [Category("GitCommitResult")] // sentinel:auto-category
    public async Task Git_Commit_ReturnsCommitHashLengthMatchingActualHashAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "commit hash length test");
        var result = await _gitTools.Git(reason: "test message", GitOperation.commit, message: "test commit", scope: GitStageScope.tracked);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var commit = result.SuccessData as GitCommitResult;
        Assert.That(commit, Is.Not.Null);
        Assert.That(commit!.CommitHashLength, Is.EqualTo(commit.CommitHash.Length),
            "CommitHashLength must always match the literal length of CommitHash.");
        Assert.That(commit.CommitHashLength, Is.EqualTo(40),
            "A real commit's hash is a 40-character SHA-1.");
    }

    [Test]
    [Category("GitCommitEntry")] // sentinel:auto-category
    [Category("GitLogResult")] // sentinel:auto-category
    [Category("GitRevertResult")] // sentinel:auto-category
    public async Task Git_Revert_ReturnsCommitHashLengthMatchingActualHashAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "revert hash length test");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "commit to revert");
        var log = await _gitTools.Git(reason: "test message", GitOperation.log, count: 1);
        var hashToRevert = ((GitLogResult)log.SuccessData!).Commits[0].Hash;

        var result = await _gitTools.Git(reason: "test message", GitOperation.revert, commitHash: hashToRevert);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var revert = result.SuccessData as GitRevertResult;
        Assert.That(revert, Is.Not.Null);
        Assert.That(revert!.CommitHashLength, Is.EqualTo(revert.CommitHash.Length),
            "CommitHashLength must always match the literal length of CommitHash.");
        Assert.That(revert.CommitHashLength, Is.EqualTo(40),
            "A real revert commit's hash is a 40-character SHA-1.");
    }

    [Test]
    public async Task Git_Commit_RepoPath_IsRejectedAsync()
    {
        var result = await _gitTools.Git(reason: "test message", GitOperation.commit, message: "should be rejected", repoPath: _repoDir);

        Assert.That(result, Is.Not.Null);
        // The repoPath/mutating-op guard returns an error envelope (IsError = true, InvalidArguments), not a
        // GitStatusResult.
        Assert.That(result.IsError, Is.True,
            "repoPath must only be accepted for status/log/diff/show - mutating operations should stay scoped to the loaded solution.");
    }


    // Regression coverage for docs/current/blockers/resolved/blocking_error_git_diff_mojibake_display.md:
    // RunGitAsync previously decoded git's redirected stdout/stderr using Console.OutputEncoding
    // (the legacy OS codepage) instead of UTF-8, so any non-ASCII multi-byte character in a diffed
    // file's content came back as mojibake. This asserts the actual UTF-8 character round-trips.
    [Test]
    [Category("GitDiffResult")] // sentinel:auto-category
    [Category("GitResult")] // sentinel:auto-category
    public async Task Git_Diff_RoundTripsNonAsciiCharacterCorrectlyAsync()
    {
        // U+2014 EM DASH is UTF-8 encoded as the 3-byte sequence E2 80 94 - exactly the shape that
        // decoded one byte at a time through a legacy codepage, per the linked blocker doc.
        const string emDash = "—";
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), $"hello {emDash} again", System.Text.Encoding.UTF8);

        var result = await _gitTools.Git(reason: "test message", GitOperation.diff);

        Assert.That(!result.IsError, Is.True);
        var diff = (GitDiffResult)result.SuccessData!;
        Assert.That(!diff.IsError, Is.True, diff.Error);
        Assert.That(diff.Diff, Does.Contain(emDash),
            "the diff should contain the real UTF-8 em dash, not a mis-decoded mojibake substitute.");
        Assert.That(diff.Warning, Is.Null, "a correctly-decoded diff should not raise a corruption warning.");
    }


    [Test]
    [Category("GitResult")] // sentinel:auto-category
    [Category("GitShowResult")] // sentinel:auto-category
    public async Task Git_Show_RoundTripsNonAsciiCharacterInFileContentCorrectlyAsync()
    {
        const string emDash = "—";
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), $"hello {emDash} again", System.Text.Encoding.UTF8);
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add non-ascii content");

        var result = await _gitTools.Git(reason: "test message", GitOperation.show, target: "HEAD");

        Assert.That(!result.IsError, Is.True);
        var show = (GitShowResult)result.SuccessData!;
        Assert.That(!show.IsError, Is.True, show.Error);
        Assert.That(show.Diff, Does.Contain(emDash),
            "git show's diff should contain the real UTF-8 em dash, not a mis-decoded mojibake substitute.");
        Assert.That(show.Warning, Is.Null, "a correctly-decoded show result should not raise a corruption warning.");
    }


    [Test]
    [Category("GitDiffResult")] // sentinel:auto-category
    [Category("GitResult")] // sentinel:auto-category
    public async Task Git_Diff_WarnsWhenOutputContainsReplacementCharacterAsync()
    {
        // Simulates a genuinely undecodable byte sequence reaching the diff text (rather than
        // re-triggering the original bug, which is now fixed) - if any future regression or a
        // different-cause decode failure inserts U+FFFD, the Warning field must surface it instead
        // of silently returning corrupted text.
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "hello � again");

        var result = await _gitTools.Git(reason: "test message", GitOperation.diff);

        Assert.That(!result.IsError, Is.True);
        var diff = (GitDiffResult)result.SuccessData!;
        Assert.That(!diff.IsError, Is.True, diff.Error);
        Assert.That(diff.Warning, Is.Not.Null.And.Contains("U+FFFD"),
            "a diff containing the Unicode replacement character must raise a decode-corruption warning.");
    }

    // Phase 1 tests: listed-scope commit fixes (blocker case 1 and 2, gaps A-B, and CRLF hygiene)

    [Test]
    public async Task Git_Commit_ListedScope_RenameStagedViaAllThenCommittedWithBothSidesAsync()
    {
        // Blocker case 1: a rename is staged via scope: all, then commit scope: listed
        // naming both the old and new path. Must succeed (not reject with "did not match").
        File.WriteAllText(Path.Combine(_repoDir, "original.txt"), "content");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add original.txt");

        File.Move(Path.Combine(_repoDir, "original.txt"), Path.Combine(_repoDir, "renamed.txt"));
        var stageResult = await _gitTools.Git(reason: "stage rename", GitOperation.stage, scope: GitStageScope.all);
        Assert.That(!stageResult.IsError, Is.True, stageResult.ErrorData?.Message);

        var commitResult = await _gitTools.Git(
            reason: "commit both sides of rename",
            GitOperation.commit,
            message: "rename file",
            scope: GitStageScope.listed,
            files: "original.txt,renamed.txt");

        Assert.That(!commitResult.IsError, Is.True, commitResult.ErrorData?.Message);
        var commit = commitResult.SuccessData as GitCommitResult;
        var headPaths = HeadPaths();
        Assert.That(headPaths, Does.Contain("renamed.txt"),
            "the renamed file should be in HEAD after successful commit");
        Assert.That(headPaths, Does.Contain("original.txt"),
            "the commit should record the deletion of the original path (both rename sides committed)");
        Assert.That(StagedPaths(), Is.Empty, "nothing should remain staged after committing both sides");
    }

    [Test]
    [Category("GitCommitResult")] // sentinel:auto-category
    public async Task Git_Commit_ListedScope_ForceTrackedFileUnderGitignoredDirAsync()
    {
        // Blocker case 2: a force-tracked file under a gitignored directory is deleted
        // on disk and committed. Must succeed with no advisory error.
        CreateGitignore("TestResults/\n");
        ForceTrackUnderGitignored("TestResults/test.coverage", "coverage data");

        // Delete the file on disk to simulate case where it's tracked but gone.
        File.Delete(Path.Combine(_repoDir, "TestResults/test.coverage"));

        var commitResult = await _gitTools.Git(
            reason: "commit deletion under ignored dir",
            GitOperation.commit,
            message: "remove coverage file",
            scope: GitStageScope.listed,
            files: "TestResults/test.coverage");

        Assert.That(!commitResult.IsError, Is.True, commitResult.ErrorData?.Message);
        var commit = commitResult.SuccessData as GitCommitResult;
        Assert.That(commit!.CommitHash.Length, Is.GreaterThan(0),
            "commit should have a hash");
        // HeadPaths lists paths touched by HEAD (deletions included), so check the tree itself.
        var treePaths = RunGitCapture("ls-tree", "-r", "--name-only", "HEAD");
        Assert.That(treePaths, Does.Not.Contain("TestResults/test.coverage"),
            "the deleted file should not be in the HEAD tree");
        Assert.That(HeadPaths(), Does.Contain("TestResults/test.coverage"),
            "HEAD should record the deletion of the force-tracked file");
    }

    [Test]
    [Category("GitCommitResult")] // sentinel:auto-category
    public async Task Git_Commit_FilesWithoutScope_CommitsExactPathsAndLeavesOtherStaged_GapAAsync()
    {
        // Gap A: commit with files and no scope should commit exactly those paths
        // and leave other staged paths staged.
        File.WriteAllText(Path.Combine(_repoDir, "file1.txt"), "content1");
        File.WriteAllText(Path.Combine(_repoDir, "file2.txt"), "content2");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "initial two files");

        // Modify both files and stage both.
        File.WriteAllText(Path.Combine(_repoDir, "file1.txt"), "modified1");
        File.WriteAllText(Path.Combine(_repoDir, "file2.txt"), "modified2");
        RunGit(_repoDir, "add", "-A");

        // Commit only file1 via the tool (no scope parameter).
        var commitResult = await _gitTools.Git(
            reason: "commit with files, no scope",
            GitOperation.commit,
            message: "modify file1",
            files: "file1.txt");

        Assert.That(!commitResult.IsError, Is.True, commitResult.ErrorData?.Message);
        var commit = commitResult.SuccessData as GitCommitResult;

        // Verify file1 was committed.
        var headPaths = HeadPaths();
        Assert.That(headPaths, Does.Contain("file1.txt"),
            "file1 should be in the committed HEAD");

        // Verify file2 is still staged.
        var remainingStaged = StagedPaths();
        Assert.That(remainingStaged, Does.Contain("file2.txt"),
            "file2 should still be staged after the partial commit");
        Assert.That(commit!.RemainingStaged, Does.Contain("file2.txt"),
            "RemainingStaged should report file2");
    }

    [Test]
    public async Task Git_Stage_FilesWithoutScope_StagesOnlyThosePaths_GapBAsync()
    {
        // Gap B: stage with files and no scope should stage only those paths.
        File.WriteAllText(Path.Combine(_repoDir, "new1.txt"), "new content1");
        File.WriteAllText(Path.Combine(_repoDir, "new2.txt"), "new content2");

        // Stage only new1.txt.
        var stageResult = await _gitTools.Git(
            reason: "stage with files, no scope",
            GitOperation.stage,
            files: "new1.txt");

        Assert.That(!stageResult.IsError, Is.True, stageResult.ErrorData?.Message);

        // Verify only new1.txt is staged.
        var stagedPaths = StagedPaths();
        Assert.That(stagedPaths, Does.Contain("new1.txt"),
            "new1 should be staged");
        Assert.That(stagedPaths, Does.Not.Contain("new2.txt"),
            "new2 should not be staged");
    }

    [Test]
    public async Task Git_Stage_MissingUntrackedPath_ErrorNamesPathAndLeavesIndexUnchangedAsync()
    {
        // Atomic failure: listing a missing (untracked, not on disk) path should error,
        // name the path, and leave StagedPaths unchanged.
        File.WriteAllText(Path.Combine(_repoDir, "existing.txt"), "exists");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add existing");

        var stageResult = await _gitTools.Git(
            reason: "stage missing path",
            GitOperation.stage,
            scope: GitStageScope.listed,
            files: "missing.txt");

        Assert.That(!stageResult.IsError, Is.False,
            "staging a missing path should fail");
        var error = stageResult.ErrorData;
        Assert.That(error?.Message, Does.Contain("missing.txt"),
            "error should name the missing path");
        var stagedPaths = StagedPaths();
        Assert.That(stagedPaths, Is.Empty,
            "index should be unchanged after failed stage");
    }

    [Test]
    public async Task Git_Stage_UntrackedPathUnderGitignoredDir_ErrorNamesMissingFlagAsync()
    {
        // Listed untracked path under a gitignored directory should error and mention
        // that -f is not available (tool has no force flag).
        CreateGitignore("ignored/\n");
        // Must exist on disk: a path that is neither tracked nor on disk is (correctly) reported Missing.
        WriteFile("ignored/file.txt", "ignored content");

        var stageResult = await _gitTools.Git(
            reason: "stage ignored path",
            GitOperation.stage,
            scope: GitStageScope.listed,
            files: "ignored/file.txt");

        Assert.That(!stageResult.IsError, Is.False,
            "staging an ignored path should fail");
        var error = stageResult.ErrorData;
        Assert.That(error?.Message, Does.Contain("ignored/file.txt"),
            "error should name the ignored path");
        Assert.That(error?.Message, Does.Contain("-f"),
            "error should mention the -f flag is not available");
    }

    [Test]
    public async Task Git_Stage_MixedOkAndIgnoredPaths_StagesNothingAsync()
    {
        // git add stages the non-ignored paths of a mixed list and only then exits 1, so the tool
        // must refuse up front to honour "Nothing was staged."
        CreateGitignore("ignored/\n");
        WriteFile("ignored/file.txt", "ignored content");
        WriteFile("ok.txt", "fine");

        var stageResult = await _gitTools.Git(
            reason: "stage mixed list",
            GitOperation.stage,
            scope: GitStageScope.listed,
            files: "ok.txt,ignored/file.txt");

        Assert.That(!stageResult.IsError, Is.False);
        Assert.That(stageResult.ErrorData?.Message, Does.Contain("ignored/file.txt"));
        Assert.That(stageResult.ErrorData?.Message, Does.Contain("Nothing was staged"));
        Assert.That(StagedPaths(), Is.Empty, "ok.txt must not have been staged by the failed call");
    }

    [Test]
    [Category("GitCommitResult")] // sentinel:auto-category
    public async Task Git_Commit_RenameWithOnlyOneListedSide_LeavesOtherSideStaged_Async()
    {
        // Rename listed by only one side: commit succeeds, RemainingStaged contains the other side.
        File.WriteAllText(Path.Combine(_repoDir, "original.txt"), "content");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add original");

        File.Move(Path.Combine(_repoDir, "original.txt"), Path.Combine(_repoDir, "renamed.txt"));
        RunGit(_repoDir, "add", "-A");

        // Commit only the new side of the rename.
        var commitResult = await _gitTools.Git(
            reason: "commit only new side",
            GitOperation.commit,
            message: "rename",
            scope: GitStageScope.listed,
            files: "renamed.txt");

        Assert.That(!commitResult.IsError, Is.True, commitResult.ErrorData?.Message);
        var commit = commitResult.SuccessData as GitCommitResult;
        Assert.That(commit!.RemainingStaged, Does.Contain("original.txt"),
            "the old side of the rename should remain staged");
    }

    [Test]
    [Category("GitCommitResult")] // sentinel:auto-category
    public async Task Git_Commit_ListedScope_AlreadyStagedDeletion_CommitsOnlyItAndReportsCleanRemainingStagedAsync()
    {
        // Answers the plan's open question: git commit --only -- <path> resolves a path whose
        // deletion is already staged (in HEAD, absent from index and disk). Also pins that
        // RemainingStaged entries carry no trailing CR when there is more than one.
        WriteFile("a.txt", "a");
        WriteFile("b.txt", "b");
        WriteFile("c.txt", "c");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add a b c");

        WriteFile("a.txt", "a2");
        File.Delete(Path.Combine(_repoDir, "b.txt"));
        WriteFile("c.txt", "c2");
        RunGit(_repoDir, "add", "-A");
        Assert.That(StagedPaths(), Is.EquivalentTo(new[] { "a.txt", "b.txt", "c.txt" }));

        var commitResult = await _gitTools.Git(
            reason: "commit staged deletion only",
            GitOperation.commit,
            message: "delete b",
            scope: GitStageScope.listed,
            files: "b.txt");

        Assert.That(!commitResult.IsError, Is.True, commitResult.ErrorData?.Message);
        var commit = (GitCommitResult)commitResult.SuccessData!;
        Assert.That(HeadPaths(), Is.EqualTo(new[] { "b.txt" }), "HEAD should contain only the deletion of b.txt");
        Assert.That(commit.RemainingStaged, Is.EquivalentTo(new[] { "a.txt", "c.txt" }),
            "the other staged paths stay staged, reported without stray line-ending characters");
    }

    [Test]
    public async Task Git_Stage_ListedScope_TrackedDirectoryDeletedFromDisk_StagesTheDeletionsAsync()
    {
        // A directory path that is tracked (files under it are in the index) but gone from disk must
        // classify as InIndex, not Missing, so `git add -- <dir>` stages the removals.
        WriteFile("subdir/one.txt", "one");
        WriteFile("subdir/nested/two.txt", "two");
        WriteFile("keep.txt", "keep");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add subdir");

        Directory.Delete(Path.Combine(_repoDir, "subdir"), recursive: true);
        Assert.That(StagedPaths(), Is.Empty, "precondition: nothing staged before the tool call");

        var stageResult = await _gitTools.Git(
            reason: "stage deleted tracked directory",
            GitOperation.stage,
            scope: GitStageScope.listed,
            files: "subdir");

        Assert.That(!stageResult.IsError, Is.True, stageResult.ErrorData?.Message);
        Assert.That(StagedPaths(), Is.EquivalentTo(new[] { "subdir/one.txt", "subdir/nested/two.txt" }),
            "both deletions under the removed directory should be staged");
    }

    [Test]
    public async Task Git_Stage_FilenameWithSpecialChars_StagedLiterally_NotGlobbedAsync()
    {
        // Filename containing '[' and '*' is staged literally (not globbed).
        // Create two files: one matching the glob, one literal.
        WriteFile("test[a].txt", "literal special chars");
        WriteFile("testa.txt", "would match glob");

        var stageResult = await _gitTools.Git(
            reason: "stage literal with special chars",
            GitOperation.stage,
            scope: GitStageScope.listed,
            files: "test[a].txt");

        Assert.That(!stageResult.IsError, Is.True, stageResult.ErrorData?.Message);

        var stagedPaths = StagedPaths();
        Assert.That(stagedPaths, Does.Contain("test[a].txt"),
            "the literal filename with brackets should be staged");
        Assert.That(stagedPaths, Does.Not.Contain("testa.txt"),
            "the glob-matching file should not be staged (literal, not glob)");
    }

    [Test]
    public async Task Git_Commit_WithCrlfWarnings_ErrorDoesNotContainLfWarningTextAsync()
    {
        // A tracked LF file modified under core.autocrlf=true + core.safecrlf=warn makes git emit
        // "LF will be replaced by CRLF" on stderr. A failing pre-commit hook then makes the commit
        // fail with git stderr in the error path, so CleanGitStderr is actually exercised.
        WriteFile("lf.txt", "line one\nline two\n");
        RunGit(_repoDir, "add", "lf.txt");
        RunGit(_repoDir, "commit", "-m", "add lf file");

        EnableCrlfWarnings();
        WriteFile("lf.txt", "line one\nline two\nline three\n");

        var hookPath = Path.Combine(_repoDir, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        File.WriteAllText(hookPath, "#!/bin/sh\necho hook-rejected >&2\nexit 1\n");

        // Sanity: raw git stderr for the same flow really contains the warning, so the negative
        // assertion below cannot pass vacuously.
        var raw = RunGitRaw("commit", "-m", "raw probe", "--only", "--", "lf.txt");
        Assert.That(raw.ExitCode, Is.Not.EqualTo(0), "the failing hook should make the raw commit fail");
        Assert.That(raw.Stderr, Does.Contain("LF will be replaced"),
            "precondition: git must emit the LF/CRLF warning in this flow, otherwise this test proves nothing");
        Assert.That(raw.Stderr, Does.Contain("hook-rejected"));

        var commitResult = await _gitTools.Git(
            reason: "commit with failing hook under crlf warnings",
            GitOperation.commit,
            message: "should fail",
            scope: GitStageScope.listed,
            files: "lf.txt");

        Assert.That(!commitResult.IsError, Is.False, "the failing pre-commit hook should fail the commit");
        var message = commitResult.ErrorData?.Message;
        Assert.That(message, Does.Contain("hook-rejected"),
            "the hook's stderr should still reach the caller");
        Assert.That(message, Does.Not.Contain("LF will be replaced"),
            "CleanGitStderr should filter the LF/CRLF warning out of the error message");
    }

    [Test]
    public async Task Git_Commit_FilesPlusAllScope_ReturnsRefusalAsync()
    {
        // files plus scope: all should return a refusal (existing behavior).
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "modified");
        RunGit(_repoDir, "add", "-A");

        var result = await _gitTools.Git(
            reason: "attempt files plus all scope",
            GitOperation.commit,
            message: "test",
            scope: GitStageScope.all,
            files: "README.md");

        Assert.That(!result.IsError, Is.False,
            "files plus scope: all should be rejected");
        var error = result.ErrorData;
        Assert.That(error?.Message, Does.Contain("files").Or.Contain("scope"),
            "error should name the conflicting parameters");
    }

    [Test]
    public async Task Git_Stage_FilesPlusAllScope_ReturnsRefusalAsync()
    {
        // files plus scope: all should return a refusal for stage too.
        File.WriteAllText(Path.Combine(_repoDir, "new.txt"), "new");

        var result = await _gitTools.Git(
            reason: "attempt files plus all scope on stage",
            GitOperation.stage,
            scope: GitStageScope.all,
            files: "new.txt");

        Assert.That(!result.IsError, Is.False,
            "files plus scope: all should be rejected for stage");
        var error = result.ErrorData;
        Assert.That(error?.Message, Does.Contain("files").Or.Contain("scope"),
            "error should name the conflicting parameters");
    }

    [Test]
    public async Task Git_Stage_DirectoryPath_WorksRegression_Async()
    {
        // Directory path listed (git add of a directory) still works (regression guard).
        WriteFile("subdir/file1.txt", "content1");
        WriteFile("subdir/file2.txt", "content2");

        var result = await _gitTools.Git(
            reason: "stage a directory",
            GitOperation.stage,
            scope: GitStageScope.listed,
            files: "subdir");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var stagedPaths = StagedPaths();
        Assert.That(stagedPaths, Does.Contain("subdir/file1.txt"),
            "files under the staged directory should be staged");
        Assert.That(stagedPaths, Does.Contain("subdir/file2.txt"),
            "files under the staged directory should be staged");
    }

    // Phase 3 tests: read-side parity (status -z parsing, maxEntries, nameOnly/stat, byte cap, conflicts)

    [Test]
    [Category("GitCommitResult")] // sentinel:auto-category
    [Category("GitStatusEntry")] // sentinel:auto-category
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Status_PathsWithSpacesAndNonAscii_RoundTripIntoFilesAsync()
    {
        // Non-ASCII built from char codes (e-acute, u-umlaut) so this source file stays ASCII-only.
        const string spaced = "my notes.txt";
        var accented = "caf" + (char)0xE9 + " men" + (char)0xFC + ".txt";
        WriteFile(spaced, "a");
        WriteFile(accented, "b");

        var statusResult = await _gitTools.Git(reason: "list untracked paths", GitOperation.status);
        Assert.That(!statusResult.IsError, Is.True, statusResult.ErrorData?.Message);
        var status = (GitStatusResult)statusResult.SuccessData!;
        Assert.That(status.Untracked, Is.EquivalentTo(new[] { spaced, accented }),
            "status must return the real names (no quotes, no octal escapes) so they are usable as-is");

        // Stage using exactly the strings status returned.
        var files = System.Text.Json.JsonSerializer.Serialize(status.Untracked.ToArray());
        var stageResult = await _gitTools.Git(reason: "stage status-returned paths", GitOperation.stage, files: files);
        Assert.That(!stageResult.IsError, Is.True, stageResult.ErrorData?.Message);
        Assert.That(StagedPaths(), Is.EquivalentTo(new[] { spaced, accented }));

        var stagedStatus = (GitStatusResult)stageResult.SuccessData!;
        Assert.That(stagedStatus.Staged.Select(e => e.Path), Is.EquivalentTo(new[] { spaced, accented }));

        // Commit one of them, again using the returned string; RemainingStaged must report the
        // other one unquoted so it too can be fed back into files.
        var commitResult = await _gitTools.Git(
            reason: "commit the spaced path", GitOperation.commit, message: "add spaced", files: spaced);
        Assert.That(!commitResult.IsError, Is.True, commitResult.ErrorData?.Message);
        var commit = (GitCommitResult)commitResult.SuccessData!;
        Assert.That(commit.RemainingStaged, Is.EqualTo(new[] { accented }),
            "RemainingStaged must list the non-ASCII path verbatim, not octal-escaped");
    }

    [Test]
    [Category("GitStatusEntry")] // sentinel:auto-category
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Status_StagedRename_ReturnsPathAndOriginalPathAsync()
    {
        WriteFile("old name.txt", "line one\nline two\nline three\nline four\nline five\n");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add old name");

        // git mv stages the rename; the destination has a space so -z record ordering and
        // whitespace handling are both exercised.
        RunGit(_repoDir, "mv", "old name.txt", "new name.txt");

        var result = await _gitTools.Git(reason: "status after rename", GitOperation.status);
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var status = (GitStatusResult)result.SuccessData!;

        var renamed = status.Staged.Single(e => e.Status == "renamed");
        Assert.That(renamed.Path, Is.EqualTo("new name.txt"), "Path is the rename destination");
        Assert.That(renamed.OriginalPath, Is.EqualTo("old name.txt"), "OriginalPath is the rename source");
        Assert.That(status.Staged, Has.Count.EqualTo(1), "the source path must not also appear as its own entry");
        Assert.That(status.Untracked, Is.Empty);

        // A non-rename entry has no OriginalPath.
        WriteFile("plain.txt", "x");
        RunGit(_repoDir, "add", "plain.txt");
        var again = (GitStatusResult)(await _gitTools.Git(reason: "status with plain add", GitOperation.status)).SuccessData!;
        Assert.That(again.Staged.Single(e => e.Path == "plain.txt").OriginalPath, Is.Null);
    }

    [Test]
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Status_MaxEntries_ControlsTruncationThresholdAsync()
    {
        for (var i = 0; i < 60; i++)
        {
            WriteFile($"bulk{i:D2}.txt", "x");
        }

        // Default (50): 60 entries is over the threshold, so the result is truncated to a sample.
        var defaultResult = await _gitTools.Git(reason: "default status", GitOperation.status);
        var truncated = (GitStatusResult)defaultResult.SuccessData!;
        Assert.That(truncated.IsTruncated, Is.True);
        Assert.That(truncated.TotalUntrackedCount, Is.EqualTo(60), "counts stay complete when truncated");
        Assert.That(truncated.Untracked, Has.Count.EqualTo(10));

        // maxEntries above 50 lists every entry, no truncation.
        var bigResult = await _gitTools.Git(reason: "status with larger cap", GitOperation.status, maxEntries: 100);
        Assert.That(!bigResult.IsError, Is.True, bigResult.ErrorData?.Message);
        var full = (GitStatusResult)bigResult.SuccessData!;
        Assert.That(full.IsTruncated, Is.False);
        Assert.That(full.Untracked, Has.Count.EqualTo(60), "maxEntries: 100 must return every one of the 60 entries");
        Assert.That(full.Untracked, Does.Contain("bulk00.txt").And.Contain("bulk59.txt"));

        // Boundary: total == maxEntries is not truncated; one fewer is.
        var exact = (GitStatusResult)(await _gitTools.Git(reason: "exact cap", GitOperation.status, maxEntries: 60)).SuccessData!;
        Assert.That(exact.IsTruncated, Is.False);
        var under = (GitStatusResult)(await _gitTools.Git(reason: "cap below total", GitOperation.status, maxEntries: 59)).SuccessData!;
        Assert.That(under.IsTruncated, Is.True);
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(5001)]
    public async Task Git_Status_MaxEntriesOutOfRange_IsRefusedWithNamedParameterAsync(int maxEntries)
    {
        var result = await _gitTools.Git(reason: "out of range cap", GitOperation.status, maxEntries: maxEntries);

        Assert.That(!result.IsError, Is.False);
        var message = result.ErrorData?.Message;
        Assert.That(message, Does.Contain("maxEntries"), "error must name the offending parameter");
        Assert.That(message, Does.Contain("1").And.Contain("5000"), "error must state the valid range");
        Assert.That(message, Does.Contain(maxEntries.ToString()), "error must echo the rejected value");
    }

    [Test]
    [Category("GitDiffResult")] // sentinel:auto-category
    [Category("GitShowResult")] // sentinel:auto-category
    public async Task Git_ShowAndDiff_NameOnlyAndStat_ReturnFileListAndStatInsteadOfPatchAsync()
    {
        var accented = "caf" + (char)0xE9 + ".txt";
        WriteFile("a.txt", "alpha\nalpha2\n");
        WriteFile("sub dir/b.txt", "beta\n");
        WriteFile(accented, "gamma\n");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add three files");

        // show + nameOnly: --name-status lines, no patch.
        var nameOnly = await _gitTools.Git(reason: "show name list", GitOperation.show, target: "HEAD", nameOnly: true);
        Assert.That(!nameOnly.IsError, Is.True, nameOnly.ErrorData?.Message);
        var nameShow = (GitShowResult)nameOnly.SuccessData!;
        Assert.That(nameShow.FilesChanged, Is.EqualTo(3));
        Assert.That(nameShow.Diff, Does.Contain("A\ta.txt").And.Contain("A\tsub dir/b.txt").And.Contain("A\t" + accented),
            "name-status lines, with non-ASCII and spaced paths verbatim");
        Assert.That(nameShow.Diff, Does.Not.Contain("diff --git").And.Not.Contain("+alpha"), "no patch text");

        // show + stat: --stat text, no patch.
        var statOnly = await _gitTools.Git(reason: "show stat", GitOperation.show, target: "HEAD", stat: true);
        Assert.That(!statOnly.IsError, Is.True, statOnly.ErrorData?.Message);
        var statShow = (GitShowResult)statOnly.SuccessData!;
        Assert.That(statShow.FilesChanged, Is.EqualTo(3));
        Assert.That(statShow.Diff, Does.Contain("a.txt").And.Contain("3 files changed"));
        Assert.That(statShow.Diff, Does.Not.Contain("diff --git").And.Not.Contain("+alpha"), "no patch text");

        // diff honours the same flags (working-tree change to one file).
        WriteFile("a.txt", "alpha\nalpha2\nalpha3\n");
        var diffNames = (GitDiffResult)(await _gitTools.Git(reason: "diff name list", GitOperation.diff, nameOnly: true)).SuccessData!;
        Assert.That(diffNames.Diff.Trim(), Is.EqualTo("M\ta.txt"));
        Assert.That(diffNames.FilesChanged, Is.EqualTo(1));
        var diffStat = (GitDiffResult)(await _gitTools.Git(reason: "diff stat", GitOperation.diff, stat: true)).SuccessData!;
        Assert.That(diffStat.Diff, Does.Contain("a.txt").And.Contain("1 file changed"));
        Assert.That(diffStat.FilesChanged, Is.EqualTo(1));

        // Default is still the full patch.
        var patch = (GitDiffResult)(await _gitTools.Git(reason: "diff patch", GitOperation.diff)).SuccessData!;
        Assert.That(patch.Diff, Does.Contain("diff --git").And.Contain("+alpha3"));
    }

    [TestCase(GitOperation.diff)]
    [TestCase(GitOperation.show)]
    public async Task Git_DiffAndShow_NameOnlyPlusStat_IsRefusedNamingBothParametersAsync(GitOperation operation)
    {
        var result = await _gitTools.Git(reason: "both format flags", operation, target: operation == GitOperation.show ? "HEAD" : "working", nameOnly: true, stat: true);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData?.Message, Does.Contain("nameOnly").And.Contain("stat"),
            "refusal must name both conflicting parameters");
    }

    [Test]
    [Category("GitDiffResult")] // sentinel:auto-category
    public async Task Git_Diff_MaxBytes_CountsUtf8BytesAndNeverSplitsSurrogatePairAsync()
    {
        // U+1F600 is 4 UTF-8 bytes and a 2-char surrogate pair in .NET: 2000 of them are 8000
        // bytes / 4000 chars, so a char-based cap of 1024 would keep ~4 KB of bytes, and any cut
        // that is not pair-aware can leave a lone high surrogate at the end.
        var emoji = char.ConvertFromUtf32(0x1F600);
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), string.Concat(Enumerable.Repeat(emoji, 2000)), System.Text.Encoding.UTF8);

        var result = await _gitTools.Git(reason: "diff with tiny cap", GitOperation.diff, maxBytes: 1024);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var diff = (GitDiffResult)result.SuccessData!;
        const string marker = "\n... (truncated at 1024 bytes)";
        Assert.That(diff.Diff, Does.EndWith(marker), "output past the cap must be marked truncated");

        var body = diff.Diff[..^marker.Length];
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(body), Is.LessThanOrEqualTo(1024),
            "the cap is measured in UTF-8 bytes, not chars");
        Assert.That(body.EnumerateRunes().Any(r => r == System.Text.Rune.ReplacementChar), Is.False,
            "no lone surrogate (which would become U+FFFD) may result from cutting mid-pair");
        Assert.That(body, Does.EndWith(emoji), "the cut lands on a whole emoji, not mid-character");
        Assert.That(diff.Warning, Is.Null, "a clean cut must not raise the decode-corruption warning");
    }

    // Leaves the repo mid-merge with a real conflict on relPath: two branches change (or, when
    // existsInBase is false, both independently add) the same file with different content.
    private void CreateMergeConflict(string relPath, bool existsInBase)
    {
        if (existsInBase)
        {
            WriteFile(relPath, "base\n");
            RunGit(_repoDir, "add", "-A");
            RunGit(_repoDir, "commit", "-m", "add base file");
        }

        var baseBranch = RunGitCapture("rev-parse", "--abbrev-ref", "HEAD").Trim();
        RunGit(_repoDir, "checkout", "-b", "feature");
        WriteFile(relPath, "feature side\n");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "feature change");

        RunGit(_repoDir, "checkout", baseBranch);
        WriteFile(relPath, "main side\n");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "main change");

        // A conflicting merge exits non-zero by design, so RunGit (which throws) is not usable here.
        var (exitCode, _, stderr) = RunGitRaw("merge", "feature");
        Assert.That(exitCode, Is.Not.EqualTo(0), "fixture must produce a conflict: " + stderr);
    }

    [TestCase("shared.txt", true, "UU")]
    [TestCase("bothadded.txt", false, "AA")]
    [Category("GitStatusEntry")] // sentinel:auto-category
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Status_MergeConflict_IsLabelledConflictOnBothSidesAsync(string relPath, bool existsInBase, string expectedPair)
    {
        CreateMergeConflict(relPath, existsInBase);
        Assert.That(RunGitCapture("status", "--porcelain=v1"), Does.Contain(expectedPair + " " + relPath),
            "fixture sanity: git itself must report the " + expectedPair + " pair");

        var result = await _gitTools.Git(reason: "status during merge conflict", GitOperation.status);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var status = (GitStatusResult)result.SuccessData!;
        var staged = status.Staged.Single(e => e.Path == relPath);
        var unstaged = status.Unstaged.Single(e => e.Path == relPath);
        Assert.That(staged.Status, Is.EqualTo("conflict"), "AA must not read as a staged 'added'");
        Assert.That(unstaged.Status, Is.EqualTo("conflict"));
        Assert.That(status.IsClean, Is.False);
    }

    // Phase 4 tests: ref parameter, reset safety, checkout, pull, amend, non-interactive env

    [TestCase(GitResetMode.soft)]
    [TestCase(GitResetMode.mixed)]
    public async Task Git_Reset_WithoutRef_IsRefusedNamingRefParameterAndMovesNothingAsync(GitResetMode mode)
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "second commit content");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "second commit");
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();

        var result = await _gitTools.Git(reason: "bare reset", GitOperation.reset, mode: mode);

        Assert.That(!result.IsError, Is.False, "reset must no longer default to HEAD~1");
        var message = result.ErrorData?.Message;
        Assert.That(message, Does.Contain("ref"), "error must name the ref parameter");
        Assert.That(message, Does.Contain("HEAD~1"), "error must show a concrete example value");
        Assert.That(message, Does.Contain("unstage"), "error must point at the no-move alternative");
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(headBefore), "HEAD must not move");
        Assert.That(File.ReadAllText(Path.Combine(_repoDir, "README.md")), Is.EqualTo("second commit content"));
    }

    [Test]
    [Category("GitCommitEntry")] // sentinel:auto-category
    [Category("GitDiffResult")] // sentinel:auto-category
    [Category("GitLogResult")] // sentinel:auto-category
    [Category("GitShowResult")] // sentinel:auto-category
    public async Task Git_Ref_WorksOnLogShowDiffAndReset_AndAliasesStillAcceptedAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "second commit content");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "second commit");
        var firstHash = RunGitCapture("rev-parse", "HEAD~1").Trim();

        // log: ref is the start ref, so history begins at the first commit.
        var log = await _gitTools.Git(reason: "log from ref", GitOperation.log, @ref: "HEAD~1");
        Assert.That(!log.IsError, Is.True, log.ErrorData?.Message);
        Assert.That(((GitLogResult)log.SuccessData!).Commits.Select(c => c.Message), Is.EqualTo(new[] { "initial commit" }));

        // log: the branchName alias still works and agrees with ref when both carry the same value.
        var aliasLog = await _gitTools.Git(reason: "log from alias", GitOperation.log, branchName: "HEAD~1");
        Assert.That(((GitLogResult)aliasLog.SuccessData!).Commits, Has.Count.EqualTo(1));
        var sameLog = await _gitTools.Git(reason: "log with equal ref and alias", GitOperation.log, @ref: "HEAD~1", branchName: "HEAD~1");
        Assert.That(!sameLog.IsError, Is.True, "identical values are not a conflict");

        // show: ref names the commit; commitHash and target aliases resolve to the same commit.
        foreach (var show in new[]
        {
            await _gitTools.Git(reason: "show by ref", GitOperation.show, @ref: firstHash),
            await _gitTools.Git(reason: "show by commitHash", GitOperation.show, commitHash: firstHash),
            await _gitTools.Git(reason: "show by target", GitOperation.show, target: firstHash),
        })
        {
            Assert.That(!show.IsError, Is.True, show.ErrorData?.Message);
            Assert.That(((GitShowResult)show.SuccessData!).Hash, Is.EqualTo(firstHash));
        }

        // diff: ref diffs the working tree against that ref.
        var diff = await _gitTools.Git(reason: "diff against ref", GitOperation.diff, @ref: "HEAD~1", nameOnly: true);
        Assert.That(!diff.IsError, Is.True, diff.ErrorData?.Message);
        Assert.That(((GitDiffResult)diff.SuccessData!).Diff.Trim(), Is.EqualTo("M\tREADME.md"));

        // reset: the branchName alias is still accepted as the ref.
        var reset = await _gitTools.Git(reason: "reset via alias", GitOperation.reset, branchName: "HEAD~1", mode: GitResetMode.soft);
        Assert.That(!reset.IsError, Is.True, reset.ErrorData?.Message);
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(firstHash));
    }

    [Test]
    public async Task Git_Ref_ConflictingWithAliases_IsRefusedNamingBothParametersAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "second commit content");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "second commit");
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();
        var firstHash = RunGitCapture("rev-parse", "HEAD~1").Trim();

        var cases = new (string Label, string AliasName, Func<Task<SentinelCallToolResult<object>>> Call)[]
        {
            ("log/branchName", "branchName", () => _gitTools.Git(reason: "conflict", GitOperation.log, @ref: "HEAD", branchName: "HEAD~1")),
            ("show/commitHash", "commitHash", () => _gitTools.Git(reason: "conflict", GitOperation.show, @ref: "HEAD", commitHash: firstHash)),
            ("show/target", "target", () => _gitTools.Git(reason: "conflict", GitOperation.show, @ref: "HEAD", target: firstHash)),
            ("show/commitHash+target", "commitHash", () => _gitTools.Git(reason: "conflict", GitOperation.show, commitHash: "HEAD", target: firstHash)),
            ("diff/target", "target", () => _gitTools.Git(reason: "conflict", GitOperation.diff, @ref: "HEAD", target: "staged")),
            ("reset/branchName", "branchName", () => _gitTools.Git(reason: "conflict", GitOperation.reset, @ref: "HEAD~1", branchName: "HEAD")),
        };

        foreach (var (label, aliasName, call) in cases)
        {
            var result = await call();
            Assert.That(!result.IsError, Is.False, label);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"), label);
            Assert.That(result.ErrorData?.Message, Does.Contain(aliasName), $"{label}: message must name the alias parameter");
            if (!label.Contains('+'))
            {
                Assert.That(result.ErrorData?.Message, Does.Contain("'ref'"), $"{label}: message must name ref");
            }
        }

        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(headBefore), "a refused reset must not move HEAD");
    }

    [Test]
    public async Task Git_Ref_OnUnsupportedOperation_IsRefusedAsync()
    {
        var result = await _gitTools.Git(reason: "ref on branch", GitOperation.branch, @ref: "HEAD");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
        Assert.That(result.ErrorData?.Message, Does.Contain("ref").And.Contain("log/show/diff/reset"));
    }

    [Test]
    [Category("GitCheckoutResult")] // sentinel:auto-category
    public async Task Git_Checkout_CreateBranch_ExistingIsPlainCheckoutAndNewIsCreated_ReportedTruthfullyAsync()
    {
        var baseBranch = RunGitCapture("rev-parse", "--abbrev-ref", "HEAD").Trim();

        // Missing branch: created, and reported as created.
        var created = await _gitTools.Git(reason: "create and switch", GitOperation.checkout, branchName: "topic", createBranch: true);
        Assert.That(!created.IsError, Is.True, created.ErrorData?.Message);
        var createdResult = (GitCheckoutResult)created.SuccessData!;
        Assert.That(createdResult.CreatedNewBranch, Is.True);
        Assert.That(createdResult.Note, Is.Null);
        Assert.That(RunGitCapture("rev-parse", "--abbrev-ref", "HEAD").Trim(), Is.EqualTo("topic"));

        // Move the existing branch ahead so a wrongly re-created/reset branch would be visible.
        WriteFile("topic.txt", "topic work");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "topic work");
        var topicTip = RunGitCapture("rev-parse", "HEAD").Trim();
        RunGit(_repoDir, "checkout", baseBranch);

        // Existing branch: plain checkout, not an error, and CreatedNewBranch is false.
        var existing = await _gitTools.Git(reason: "create-or-switch existing", GitOperation.checkout, branchName: "topic", createBranch: true);
        Assert.That(!existing.IsError, Is.True, existing.ErrorData?.Message);
        var existingResult = (GitCheckoutResult)existing.SuccessData!;
        Assert.That(existingResult.CreatedNewBranch, Is.False, "the branch already existed, so nothing was created");
        Assert.That(existingResult.Branch, Is.EqualTo("topic"));
        Assert.That(RunGitCapture("rev-parse", "--abbrev-ref", "HEAD").Trim(), Is.EqualTo("topic"));
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(topicTip), "the existing branch must be untouched");

        // A startPoint cannot apply to an existing branch: still a plain checkout, but say so.
        RunGit(_repoDir, "checkout", baseBranch);
        var withStart = await _gitTools.Git(reason: "existing with startPoint", GitOperation.checkout, branchName: "topic", createBranch: true, startPoint: "HEAD");
        Assert.That(!withStart.IsError, Is.True, withStart.ErrorData?.Message);
        var withStartResult = (GitCheckoutResult)withStart.SuccessData!;
        Assert.That(withStartResult.CreatedNewBranch, Is.False);
        Assert.That(withStartResult.Note, Does.Contain("startPoint").And.Contain("ignored"));
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(topicTip));
    }

    [Test]
    public async Task Git_Checkout_StartPointWithoutCreateBranch_IsRefusedNamingMissingFlagAsync()
    {
        var baseBranch = RunGitCapture("rev-parse", "--abbrev-ref", "HEAD").Trim();
        RunGit(_repoDir, "branch", "other");

        var result = await _gitTools.Git(reason: "startPoint alone", GitOperation.checkout, branchName: "other", startPoint: "HEAD");

        Assert.That(!result.IsError, Is.False, "a startPoint that would be silently ignored must be refused");
        Assert.That(result.ErrorData?.Message, Does.Contain("startPoint").And.Contain("createBranch"));
        Assert.That(RunGitCapture("rev-parse", "--abbrev-ref", "HEAD").Trim(), Is.EqualTo(baseBranch), "nothing may be checked out");
    }

    // Creates a bare repo next to the test repo, registers it as 'origin', and pushes the current
    // branch with upstream tracking. Returns the current branch name. Cleaned up by TearDown.
    private string AddBareRemoteAndPush()
    {
        Directory.CreateDirectory(RemoteDir);
        RunGit(RemoteDir, "init", "--bare");
        RunGit(_repoDir, "remote", "add", "origin", RemoteDir);
        var branch = RunGitCapture("rev-parse", "--abbrev-ref", "HEAD").Trim();
        RunGit(_repoDir, "push", "-u", "origin", branch);
        return branch;
    }

    [Test]
    public async Task Git_Checkout_BranchNameSharedWithTrackedFile_SwitchesToTheBranchAsync()
    {
        // 'feat' exists only as origin/feat AND as a tracked file. Without a trailing "--", git
        // refuses ("could be both a local file and a tracking branch"); with it, the argument is
        // unambiguously a branch and git creates the tracking branch.
        var baseBranch = AddBareRemoteAndPush();
        RunGit(_repoDir, "branch", "feat");
        RunGit(_repoDir, "push", "origin", "feat");
        RunGit(_repoDir, "branch", "-D", "feat");
        WriteFile("feat", "a file that shares the branch name");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add file named feat");
        Assert.That(RunGitRaw("checkout", "feat").ExitCode, Is.Not.EqualTo(0), "fixture sanity: bare checkout of the name is ambiguous");

        var result = await _gitTools.Git(reason: "checkout ambiguous name", GitOperation.checkout, branchName: "feat");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(RunGitCapture("rev-parse", "--abbrev-ref", "HEAD").Trim(), Is.EqualTo("feat"));
        Assert.That(baseBranch, Is.Not.EqualTo("feat"));
    }

    // A second clone pushes remote.txt to the remote, then the test repo commits local.txt, so the
    // local branch and its upstream have each moved on independently (divergent).
    private void DivergeFromRemote(string branch)
    {
        RunGit(Path.GetTempPath(), "clone", RemoteDir, CloneDir);
        RunGit(CloneDir, "config", "user.email", "other@example.com");
        RunGit(CloneDir, "config", "user.name", "Other");
        File.WriteAllText(Path.Combine(CloneDir, "remote.txt"), "from the remote");
        RunGit(CloneDir, "add", "-A");
        RunGit(CloneDir, "commit", "-m", "remote commit");
        RunGit(CloneDir, "push", "origin", branch);

        WriteFile("local.txt", "from local");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "local commit");
    }

    // Sets the process-wide GIT_CONFIG_NOSYSTEM env var for its duration, so it must not overlap
    // the other (parallel) tests in this fixture, whose git children would inherit it.
    [Test]
    [NonParallelizable]
    public async Task Git_Pull_DivergentBranchesWithNoPullConfig_MergesInsteadOfFailingAsync()
    {
        var previousNoSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        try
        {
            var branch = AddBareRemoteAndPush();
            DivergeFromRemote(branch);

            // Fixture sanity: with no pull.rebase/pull.ff configured, git refuses a bare pull of
            // divergent branches, which is exactly what rebase=false used to fall into.
            Assert.That(RunGitCapture("config", "--get", "pull.rebase"), Is.Empty, "fixture assumes no pull.rebase config");
            Assert.That(RunGitCapture("config", "--get", "pull.ff"), Is.Empty, "fixture assumes no pull.ff config");
            var bare = RunGitRaw("pull", "origin");
            Assert.That(bare.ExitCode, Is.Not.EqualTo(0), "fixture sanity: a bare pull must fail here: " + bare.Stderr);

            var result = await _gitTools.Git(reason: "pull divergent", GitOperation.pull);

            Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
            Assert.That(File.Exists(Path.Combine(_repoDir, "remote.txt")), Is.True, "the remote commit must be merged in");
            Assert.That(File.Exists(Path.Combine(_repoDir, "local.txt")), Is.True, "the local commit must be kept");
            Assert.That(RunGitCapture("rev-list", "--merges", "--count", "HEAD").Trim(), Is.EqualTo("1"), "rebase=false must produce a merge commit");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", previousNoSystem);
        }
    }

    [Test]
    public async Task Git_Pull_RebaseTrue_RebasesInsteadOfMergingAsync()
    {
        var branch = AddBareRemoteAndPush();
        DivergeFromRemote(branch);

        var result = await _gitTools.Git(reason: "pull with rebase", GitOperation.pull, rebase: true);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(RunGitCapture("rev-list", "--merges", "--count", "HEAD").Trim(), Is.EqualTo("0"), "rebase=true must not create a merge commit");
        Assert.That(File.Exists(Path.Combine(_repoDir, "remote.txt")), Is.True);
        Assert.That(File.Exists(Path.Combine(_repoDir, "local.txt")), Is.True);
    }

    [Test]
    public async Task Git_Commit_Amend_OfHeadAlreadyPushedToUpstream_IsRefusedAndHeadUnchangedAsync()
    {
        AddBareRemoteAndPush();
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();

        var result = await _gitTools.Git(reason: "amend pushed head", GitOperation.commit, amend: true, message: "reworded");

        Assert.That(!result.IsError, Is.False, "amending a commit already contained in its upstream must be refused");
        Assert.That(result.ErrorData?.Message, Does.Contain("upstream"), "error must explain the upstream reason");
        Assert.That(result.ErrorData?.Message, Does.Contain("Nothing was amended"));
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(headBefore), "HEAD must not be rewritten");
    }

    [Test]
    public async Task Git_Commit_Amend_WithNoUpstreamConfigured_IsAllowedAsync()
    {
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();

        var result = await _gitTools.Git(reason: "amend local only", GitOperation.commit, amend: true, message: "reworded locally");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.Not.EqualTo(headBefore), "amend must rewrite HEAD");
        Assert.That(RunGitCapture("log", "-1", "--format=%s").Trim(), Is.EqualTo("reworded locally"));
    }

    [Test]
    public async Task Git_Commit_Amend_OfUnpushedCommitOnTopOfPushedHistory_IsAllowedAsync()
    {
        AddBareRemoteAndPush();
        File.WriteAllText(Path.Combine(_repoDir, "unpushed.txt"), "not pushed yet");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "unpushed commit");
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();

        var result = await _gitTools.Git(reason: "amend unpushed head", GitOperation.commit, amend: true, message: "reworded unpushed");

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.Not.EqualTo(headBefore), "amend must rewrite the unpushed HEAD");
        Assert.That(RunGitCapture("log", "-1", "--format=%s").Trim(), Is.EqualTo("reworded unpushed"));
    }

    // Phase 5 tests: abort, InProgress, conflict advice, revert of a merge commit

    [Test]
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Abort_ConflictedMerge_RestoresCleanTreeAsync()
    {
        CreateMergeConflict("shared.txt", existsInBase: true);
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();

        var result = await _gitTools.Git(reason: "abort conflicted merge", GitOperation.abort);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var status = (GitStatusResult)result.SuccessData!;
        Assert.That(status.AbortedOperation, Is.EqualTo("merge"));
        Assert.That(status.InProgress, Is.Null, "nothing may remain in progress after the abort");
        Assert.That(status.IsClean, Is.True);
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(headBefore), "abort must not move HEAD");
        Assert.That(File.ReadAllText(Path.Combine(_repoDir, "shared.txt")), Does.Contain("main side").And.Not.Contain("<<<<<<<"));
    }

    [Test]
    public async Task Git_Abort_NothingInProgress_IsRefusedAndChangesNothingAsync()
    {
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();

        var result = await _gitTools.Git(reason: "abort with nothing to abort", GitOperation.abort);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData?.Message, Does.Contain("Nothing to abort"));
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(headBefore));
    }

    [Test]
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Status_InProgress_IsNullWhenIdleAndMergeDuringConflictedMergeAsync()
    {
        var idle = await _gitTools.Git(reason: "status when idle", GitOperation.status);
        Assert.That(((GitStatusResult)idle.SuccessData!).InProgress, Is.Null);

        CreateMergeConflict("shared.txt", existsInBase: true);
        var merging = await _gitTools.Git(reason: "status during merge", GitOperation.status);

        Assert.That(!merging.IsError, Is.True, merging.ErrorData?.Message);
        Assert.That(((GitStatusResult)merging.SuccessData!).InProgress, Is.EqualTo("merge"));
    }

    // A second clone pushes a conflicting edit of README.md to the remote, then the test repo commits
    // its own different edit of README.md, so a pull must conflict. Returns the local HEAD hash.
    private string DivergeConflictingFromRemote(string branch)
    {
        RunGit(Path.GetTempPath(), "clone", RemoteDir, CloneDir);
        RunGit(CloneDir, "config", "user.email", "other@example.com");
        RunGit(CloneDir, "config", "user.name", "Other");
        File.WriteAllText(Path.Combine(CloneDir, "README.md"), "remote readme\n");
        RunGit(CloneDir, "commit", "-am", "remote readme edit");
        RunGit(CloneDir, "push", "origin", branch);

        WriteFile("README.md", "local readme\n");
        RunGit(_repoDir, "commit", "-am", "local readme edit");
        return RunGitCapture("rev-parse", "HEAD").Trim();
    }

    [TestCase(false, "merge")]
    [TestCase(true, "rebase")]
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Pull_Conflict_MentionsAbortAndStateThenAbortRestoresLocalHeadAsync(bool rebase, string expectedState)
    {
        var branch = AddBareRemoteAndPush();
        var localHead = DivergeConflictingFromRemote(branch);

        var pull = await _gitTools.Git(reason: "conflicting pull", GitOperation.pull, rebase: rebase);

        Assert.That(!pull.IsError, Is.False, "fixture must produce a conflict");
        Assert.That(pull.ErrorData?.Message, Does.Contain("mid-" + expectedState).And.Contain("operation: abort"));
        var during = (GitStatusResult)(await _gitTools.Git(reason: "status mid-pull", GitOperation.status)).SuccessData!;
        Assert.That(during.InProgress, Is.EqualTo(expectedState));

        var abort = await _gitTools.Git(reason: "abort conflicting pull", GitOperation.abort);

        Assert.That(!abort.IsError, Is.True, abort.ErrorData?.Message);
        Assert.That(((GitStatusResult)abort.SuccessData!).IsClean, Is.True);
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(localHead), "abort must restore the pre-pull HEAD");
    }

    [Test]
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Revert_Conflict_MentionsAbortThenAbortRestoresCleanTreeAsync()
    {
        foreach (var version in new[] { "v1", "v2", "v3" })
        {
            WriteFile("README.md", version + "\n");
            RunGit(_repoDir, "commit", "-am", "readme " + version);
        }

        var v2 = RunGitCapture("rev-parse", "HEAD~1").Trim();
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();

        var revert = await _gitTools.Git(reason: "conflicting revert", GitOperation.revert, commitHash: v2);

        Assert.That(!revert.IsError, Is.False, "reverting v2 under v3 must conflict");
        Assert.That(revert.ErrorData?.Message, Does.Contain("mid-revert").And.Contain("operation: abort"));
        var abort = await _gitTools.Git(reason: "abort conflicting revert", GitOperation.abort);

        Assert.That(!abort.IsError, Is.True, abort.ErrorData?.Message);
        var status = (GitStatusResult)abort.SuccessData!;
        Assert.That(status.AbortedOperation, Is.EqualTo("revert"));
        Assert.That(status.IsClean, Is.True);
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(headBefore));
        Assert.That(File.ReadAllText(Path.Combine(_repoDir, "README.md")), Does.Contain("v3"));
    }

    // Creates feature (adds feature.txt) and main (adds main.txt) branches and merges feature into main
    // with --no-ff. Returns the merge commit hash.
    private string CreateMergeCommit()
    {
        var baseBranch = RunGitCapture("rev-parse", "--abbrev-ref", "HEAD").Trim();
        RunGit(_repoDir, "checkout", "-b", "feature");
        WriteFile("feature.txt", "feature\n");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "feature work");
        RunGit(_repoDir, "checkout", baseBranch);
        WriteFile("main.txt", "main\n");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "main work");
        RunGit(_repoDir, "merge", "--no-ff", "-m", "merge feature", "feature");
        return RunGitCapture("rev-parse", "HEAD").Trim();
    }

    [Test]
    public async Task Git_Revert_MergeCommit_WithoutMainline_IsRefusedNamingMainlineAndChangesNothingAsync()
    {
        var mergeHash = CreateMergeCommit();

        var result = await _gitTools.Git(reason: "revert merge without mainline", GitOperation.revert, commitHash: mergeHash);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData?.Message, Does.Contain("mainline").And.Contain("merge commit"));
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(mergeHash), "nothing may be reverted");
        Assert.That(RunGitCapture("status", "--porcelain"), Is.Empty);
    }

    [Test]
    public async Task Git_Revert_MergeCommit_WithMainline1_RevertsTheMergedInBranchAsync()
    {
        var mergeHash = CreateMergeCommit();

        var result = await _gitTools.Git(reason: "revert merge with mainline", GitOperation.revert, commitHash: mergeHash, mainline: 1);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.Not.EqualTo(mergeHash), "a revert commit must be created");
        Assert.That(File.Exists(Path.Combine(_repoDir, "feature.txt")), Is.False, "the merged-in branch's change must be undone");
        Assert.That(File.Exists(Path.Combine(_repoDir, "main.txt")), Is.True, "the mainline's own change must stay");
    }

    [Test]
    public async Task Git_Revert_MainlineMisuse_IsRefusedWithNamedReasonAsync()
    {
        var mergeHash = CreateMergeCommit();
        var plain = RunGitCapture("rev-parse", "HEAD^2").Trim();

        var onPlainCommit = await _gitTools.Git(reason: "mainline on plain commit", GitOperation.revert, commitHash: plain, mainline: 1);
        var outOfRange = await _gitTools.Git(reason: "mainline out of range", GitOperation.revert, commitHash: mergeHash, mainline: 3);
        var onOtherOperation = await _gitTools.Git(reason: "mainline on status", GitOperation.status, mainline: 1);

        Assert.That(onPlainCommit.ErrorData?.Message, Does.Contain("not a merge commit"));
        Assert.That(outOfRange.ErrorData?.Message, Does.Contain("out of range"));
        Assert.That(onOtherOperation.ErrorData?.Message, Does.Contain("only supported for revert"));
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(mergeHash));
    }

    // A local HTTP endpoint that answers every request with 401 + Basic challenge forces git to ask for
    // credentials. With GIT_TERMINAL_PROMPT=0 set by RunGitAsync, git must fail fast with
    // "terminal prompts disabled" instead of blocking on a prompt nobody can answer.
    [Test]
    public async Task Git_Fetch_AuthChallenge_FailsFastWithoutPromptingAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var read = 0;
                    var seen = new System.Text.StringBuilder();
                    while (!seen.ToString().Contains("\r\n\r\n") && (read = await stream.ReadAsync(buffer)) > 0)
                    {
                        seen.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                    }
                    var response = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    var bytes = System.Text.Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
            }
            catch (Exception)
            {
                // Listener stopped by the test; nothing to report.
            }
        });

        try
        {
            RunGit(_repoDir, "config", "credential.helper", "");
            RunGit(_repoDir, "remote", "add", "origin", $"http://127.0.0.1:{port}/repo.git");

            var task = _gitTools.Git(reason: "fetch with auth challenge", GitOperation.fetch);
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60)));

            Assert.That(finished, Is.SameAs(task), "git fetch blocked instead of failing fast - credential prompting is not disabled");
            var result = await task;
            Assert.That(!result.IsError, Is.False);
            Assert.That(result.ErrorData?.Message, Does.Contain("terminal prompts disabled"));
        }
        finally
        {
            listener.Stop();
            await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    // Phase 6 tests: specific machine-readable ErrorCode + Detail on known failure causes, GitError as fallback

    private static void AssertCoded(SentinelCallToolResult<object> result, string expectedCode)
    {
        Assert.That(!result.IsError, Is.False, "the call must fail");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(expectedCode), result.ErrorData?.Message);
        Assert.That(result.ErrorData?.Message, Is.Not.Empty);
        Assert.That(result.ErrorData?.Detail, Is.Not.Null.And.Not.Empty, "a classified failure must carry an actionable Detail");
    }

    [Test]
    public async Task Git_Stage_MissingPath_ReportsGitPathNotFoundWithDetailAsync()
    {
        var result = await _gitTools.Git(reason: "stage missing path", GitOperation.stage, scope: GitStageScope.listed, files: "missing.txt");

        AssertCoded(result, "GitPathNotFound");
        Assert.That(result.ErrorData?.Message, Does.Contain("missing.txt"));
        Assert.That(result.ErrorData?.Detail, Does.Contain("Git(operation: status)"));
    }

    [Test]
    public async Task Git_Stage_PathOutsideRepo_ReportsGitPathNotFoundAsync()
    {
        var result = await _gitTools.Git(reason: "stage outside path", GitOperation.stage, scope: GitStageScope.listed, files: "../outside.txt");

        AssertCoded(result, "GitPathNotFound");
        Assert.That(result.ErrorData?.Message, Does.Contain("outside the repository"));
    }

    [Test]
    public async Task Git_Commit_MissingPath_ReportsGitPathNotFoundAsync()
    {
        var result = await _gitTools.Git(reason: "commit missing path", GitOperation.commit, message: "x", files: "missing.txt");

        AssertCoded(result, "GitPathNotFound");
    }

    [Test]
    public async Task Git_StageAndCommit_IgnoredPath_ReportGitIgnoredPathAsync()
    {
        CreateGitignore("ignored/\n");
        WriteFile("ignored/file.txt", "ignored content");

        var stage = await _gitTools.Git(reason: "stage ignored", GitOperation.stage, scope: GitStageScope.listed, files: "ignored/file.txt");
        var commit = await _gitTools.Git(reason: "commit ignored", GitOperation.commit, message: "x", files: "ignored/file.txt");

        AssertCoded(stage, "GitIgnoredPath");
        AssertCoded(commit, "GitIgnoredPath");
        Assert.That(stage.ErrorData?.Detail, Does.Contain(".gitignore"));
        Assert.That(StagedPaths(), Is.Empty);
    }

    [Test]
    public async Task Git_Commit_NothingStaged_ReportsGitNothingToCommitAsync()
    {
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();

        var result = await _gitTools.Git(reason: "commit with empty index", GitOperation.commit, message: "empty");

        AssertCoded(result, "GitNothingToCommit");
        Assert.That(result.ErrorData?.Detail, Does.Contain("Git(operation: stage"));
        Assert.That(RunGitCapture("rev-parse", "HEAD").Trim(), Is.EqualTo(headBefore));
    }

    [Test]
    public async Task Git_Commit_ListedUnchangedTrackedFile_ReportsGitNothingToCommitAsync()
    {
        var result = await _gitTools.Git(reason: "commit unchanged listed file", GitOperation.commit, message: "no-op", files: "README.md");

        AssertCoded(result, "GitNothingToCommit");
    }

    [Test]
    public async Task Git_Revert_Conflict_ReportsGitOperationInProgressAsync()
    {
        foreach (var version in new[] { "v1", "v2", "v3" })
        {
            WriteFile("README.md", version + "\n");
            RunGit(_repoDir, "commit", "-am", "readme " + version);
        }

        var v2 = RunGitCapture("rev-parse", "HEAD~1").Trim();

        var result = await _gitTools.Git(reason: "conflicting revert", GitOperation.revert, commitHash: v2);

        AssertCoded(result, "GitOperationInProgress");
        Assert.That(result.ErrorData?.Message, Does.Contain("mid-revert"));
        Assert.That(result.ErrorData?.Detail, Does.Contain("operation: abort"));
    }

    [Test]
    public async Task Git_Commit_DuringConflictedMerge_ReportsGitOperationInProgressAsync()
    {
        CreateMergeConflict("shared.txt", existsInBase: true);

        var result = await _gitTools.Git(reason: "commit while conflicted", GitOperation.commit, message: "premature");

        AssertCoded(result, "GitOperationInProgress");
        Assert.That(result.ErrorData?.Message, Does.Contain("mid-merge"));
    }

    [Test]
    public async Task Git_Pull_Conflict_ReportsGitOperationInProgressAsync()
    {
        var branch = AddBareRemoteAndPush();
        DivergeConflictingFromRemote(branch);

        var result = await _gitTools.Git(reason: "conflicting pull", GitOperation.pull);

        AssertCoded(result, "GitOperationInProgress");
    }

    [Test]
    public async Task Git_RefRequiredRefusals_ReportGitRefRequiredAsync()
    {
        var reset = await _gitTools.Git(reason: "reset without ref", GitOperation.reset);
        var revert = await _gitTools.Git(reason: "revert without commitHash", GitOperation.revert);
        var checkout = await _gitTools.Git(reason: "checkout without branchName", GitOperation.checkout);

        AssertCoded(reset, "GitRefRequired");
        AssertCoded(revert, "GitRefRequired");
        AssertCoded(checkout, "GitRefRequired");
        Assert.That(reset.ErrorData?.Detail, Does.Contain("HEAD~1"));
    }

    [Test]
    public async Task Git_UnclassifiedFailure_StillReportsFallbackGitErrorWithoutDetailAsync()
    {
        var result = await _gitTools.Git(reason: "reset to unknown ref", GitOperation.reset, @ref: "no-such-ref-anywhere");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("GitError"));
        Assert.That(result.ErrorData?.Detail, Is.Null, "unclassified failures carry no invented next step");
        Assert.That(result.ErrorData?.Message, Does.Contain("git reset failed"));
    }

    [Test]
    public async Task Git_AbortWithNothingInProgress_StillReportsFallbackGitErrorAsync()
    {
        var result = await _gitTools.Git(reason: "abort nothing", GitOperation.abort);

        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("GitError"));
    }

    [Test]
    [Category("GitStatusResult")] // sentinel:auto-category
    public async Task Git_Status_UntrackedFiles_ExpandsDirectoriesToIndividualFilesAsync()
    {
        // Create untracked nested directory structure.
        WriteFile("NewDir/a.txt", "content a");
        WriteFile("NewDir/sub/b.txt", "content b");
        WriteFile("NewDir/sub/c.txt", "content c");

        var result = await _gitTools.Git(reason: "status with nested untracked files", GitOperation.status, maxEntries: 50);
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var status = (GitStatusResult)result.SuccessData!;

        // Assert all individual file paths are present.
        Assert.That(status.Untracked, Does.Contain("NewDir/a.txt"), "individual files must be listed");
        Assert.That(status.Untracked, Does.Contain("NewDir/sub/b.txt"), "nested files must be listed");
        Assert.That(status.Untracked, Does.Contain("NewDir/sub/c.txt"), "nested files must be listed");

        // Assert directory entries are NOT in the list (directories must be expanded, not collapsed).
        Assert.That(status.Untracked, Does.Not.Contain("NewDir/"), "collapsed directory entry must not appear");
        Assert.That(status.Untracked, Does.Not.Contain("NewDir/sub/"), "collapsed subdirectory entry must not appear");

        // Verify exact count: 3 files, no directory entries.
        Assert.That(status.Untracked, Has.Count.EqualTo(3), "exactly 3 individual files, no collapsed directories");
    }

    // Step 3 of plan_git_tool_tag_stash_worktree_hunks.md: Git(operation: tag, action: list|create|delete).

    private string ShortHead(string rev = "HEAD") => RunGitCapture("rev-parse", "--short", rev).Trim();

    private string TagNames() => RunGitCapture("tag", "-l").Trim();

    // Step 5 helpers (stash). GitOutStrict fails the test on a non-zero git exit, so an "is empty"
    // assertion cannot pass vacuously the way RunGitCapture's empty-on-failure result could.
    private string GitOutStrict(params string[] args)
    {
        var (exitCode, output, error) = RunGitRaw(args);
        Assert.That(exitCode, Is.EqualTo(0), $"git {string.Join(' ', args)} failed: {error}");
        return output.Trim();
    }

    private int StashEntryCount() => GitOutStrict("stash", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    private string ReadRepoFile(string relPath) => File.ReadAllText(Path.Combine(_repoDir, relPath));

    private bool RepoFileExists(string relPath) => File.Exists(Path.Combine(_repoDir, relPath));

    // Leaves one stash entry (README.md "changed" stashed away) and a clean tree.
    private void MakeOneStashEntry()
    {
        WriteFile("README.md", "changed");
        RunGit(_repoDir, "stash", "push");
    }

    private void CommitSecondChange()
    {
        WriteFile("second.txt", "second");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "second commit");
    }

    [Test]
    public async Task Git_Tag_List_OnRepoWithoutTags_ReturnsEmptyListAsync()
    {
        var result = await _gitTools.Git(reason: "list tags in empty repo", GitOperation.tag, action: GitAction.list);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var tags = (GitTagResult)result.SuccessData!;
        Assert.That(tags.Tags, Is.Empty);
        Assert.That(tags.TotalCount, Is.EqualTo(0));
        Assert.That(tags.IsTruncated, Is.False);
    }

    [Test]
    public async Task Git_Tag_Create_Lightweight_ThenListShowsItAsync()
    {
        var create = await _gitTools.Git(reason: "create lightweight tag", GitOperation.tag, action: GitAction.create, tagName: "v1.0.0");

        Assert.That(create.IsError, Is.False, create.ErrorData?.Message);
        var created = (GitTagResult)create.SuccessData!;
        Assert.That(created.Created, Is.EqualTo("v1.0.0"));
        Assert.That(created.Annotated, Is.False);
        Assert.That(created.TargetHash, Is.EqualTo(ShortHead()));
        Assert.That(TagNames(), Is.EqualTo("v1.0.0"));

        var list = await _gitTools.Git(reason: "list tags", GitOperation.tag, action: GitAction.list);

        Assert.That(list.IsError, Is.False, list.ErrorData?.Message);
        var tags = (GitTagResult)list.SuccessData!;
        Assert.That(tags.Tags, Has.Count.EqualTo(1));
        Assert.That(tags.Tags[0].Name, Is.EqualTo("v1.0.0"));
        Assert.That(tags.Tags[0].Kind, Is.EqualTo("lightweight"));
        Assert.That(tags.Tags[0].TargetHash, Is.EqualTo(ShortHead()));
        Assert.That(tags.TotalCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Git_Tag_Create_WithMessage_IsAnnotatedAsync()
    {
        var create = await _gitTools.Git(reason: "create annotated tag", GitOperation.tag, action: GitAction.create, tagName: "v1.0.0", message: "release one");

        Assert.That(create.IsError, Is.False, create.ErrorData?.Message);
        Assert.That(((GitTagResult)create.SuccessData!).Annotated, Is.True);
        Assert.That(RunGitCapture("cat-file", "-t", "refs/tags/v1.0.0").Trim(), Is.EqualTo("tag"));

        var list = await _gitTools.Git(reason: "list tags", GitOperation.tag, action: GitAction.list);

        var entry = ((GitTagResult)list.SuccessData!).Tags.Single();
        Assert.That(entry.Kind, Is.EqualTo("annotated"));
        Assert.That(entry.Subject, Is.EqualTo("release one"));
        Assert.That(entry.TargetHash, Is.EqualTo(ShortHead()), "an annotated tag must report the peeled commit, not the tag object.");
    }

    [Test]
    public async Task Git_Tag_Create_AtRef_TagsThatCommitAsync()
    {
        var firstHash = ShortHead();
        CommitSecondChange();

        var create = await _gitTools.Git(reason: "tag the previous commit", GitOperation.tag, action: GitAction.create, tagName: "old", @ref: "HEAD~1");

        Assert.That(create.IsError, Is.False, create.ErrorData?.Message);
        var created = (GitTagResult)create.SuccessData!;
        Assert.That(created.TargetHash, Is.EqualTo(firstHash));
        Assert.That(created.TargetHash, Is.Not.EqualTo(ShortHead()));
        Assert.That(ShortHead("old^{commit}"), Is.EqualTo(firstHash));
    }

    [Test]
    public async Task Git_Tag_Create_ExistingName_ReportsGitAlreadyExistsAndDoesNotMoveTagAsync()
    {
        var firstHash = ShortHead();
        var first = await _gitTools.Git(reason: "create tag", GitOperation.tag, action: GitAction.create, tagName: "v1.0.0");
        Assert.That(first.IsError, Is.False, first.ErrorData?.Message);
        CommitSecondChange();

        var again = await _gitTools.Git(reason: "create same tag again", GitOperation.tag, action: GitAction.create, tagName: "v1.0.0");

        AssertCoded(again, "GitAlreadyExists");
        Assert.That(again.ErrorData?.Message, Does.Contain("never overwrites"));
        Assert.That(ShortHead("v1.0.0^{commit}"), Is.EqualTo(firstHash), "the existing tag must not move.");
    }

    [Test]
    public async Task Git_Tag_Create_InvalidName_ReportsGitInvalidNameAsync()
    {
        var result = await _gitTools.Git(reason: "create tag with invalid name", GitOperation.tag, action: GitAction.create, tagName: "bad name");

        AssertCoded(result, "GitInvalidName");
        Assert.That(result.ErrorData?.Message, Does.Contain("bad name"));
        Assert.That(TagNames(), Is.Empty);
    }

    [Test]
    public async Task Git_Tag_Create_WithoutTagName_ReportsGitRefRequiredAsync()
    {
        var result = await _gitTools.Git(reason: "create tag without a name", GitOperation.tag, action: GitAction.create);

        AssertCoded(result, "GitRefRequired");
        Assert.That(result.ErrorData?.Message, Does.Contain("tagName"));
        Assert.That(TagNames(), Is.Empty);
    }

    [Test]
    public async Task Git_Tag_Create_UnknownRef_ReportsGitTargetNotFoundAsync()
    {
        var result = await _gitTools.Git(reason: "create tag at unknown ref", GitOperation.tag, action: GitAction.create, tagName: "v1.0.0", @ref: "no-such-ref-anywhere");

        AssertCoded(result, "GitTargetNotFound");
        Assert.That(result.ErrorData?.Message, Does.Contain("no-such-ref-anywhere"));
        Assert.That(TagNames(), Is.Empty);
    }

    [Test]
    public async Task Git_Tag_Delete_RemovesTagAndReportsPreviousTargetAsync()
    {
        var headHash = ShortHead();
        RunGit(_repoDir, "tag", "v1.0.0");

        var result = await _gitTools.Git(reason: "delete tag", GitOperation.tag, action: GitAction.delete, tagName: "v1.0.0");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var deleted = (GitTagResult)result.SuccessData!;
        Assert.That(deleted.Deleted, Is.EqualTo("v1.0.0"));
        Assert.That(deleted.PreviousTargetHash, Is.EqualTo(headHash));
        Assert.That(deleted.Note, Does.Contain("Deleted locally only"));
        Assert.That(TagNames(), Is.Empty);
    }

    [Test]
    public async Task Git_Tag_Delete_Missing_ReportsGitTargetNotFoundAsync()
    {
        RunGit(_repoDir, "tag", "keep-me");

        var result = await _gitTools.Git(reason: "delete missing tag", GitOperation.tag, action: GitAction.delete, tagName: "nope");

        AssertCoded(result, "GitTargetNotFound");
        Assert.That(result.ErrorData?.Message, Does.Contain("keep-me"));
        Assert.That(TagNames(), Is.EqualTo("keep-me"));
    }

    [Test]
    public async Task Git_Tag_WithoutAction_ReportsGitActionRequiredNamingValidValuesAsync()
    {
        var result = await _gitTools.Git(reason: "tag without action", GitOperation.tag);

        AssertCoded(result, "GitActionRequired");
        Assert.That(result.ErrorData?.Message, Does.Contain("list, create, delete"));
    }

    [Test]
    public async Task Git_Tag_List_WithRepoPath_TargetsThatRepoAsync()
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
            RunGit(otherRepoDir, "tag", "other-tag");

            // The loaded solution's repo (_repoDir) has no tags; if repoPath were ignored this would be empty.
            var result = await _gitTools.Git(reason: "list tags in other repo", GitOperation.tag, action: GitAction.list, repoPath: otherRepoDir);

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            var tags = (GitTagResult)result.SuccessData!;
            Assert.That(tags.Tags.Select(t => t.Name), Is.EqualTo(new[] { "other-tag" }));
        }
        finally
        {
            DeleteDirectoryTree(otherRepoDir);
        }
    }

    [Test]
    public async Task Git_Tag_Create_WithRepoPath_IsRefusedAsync()
    {
        var result = await _gitTools.Git(reason: "create tag with repoPath", GitOperation.tag, action: GitAction.create, tagName: "v1.0.0", repoPath: _repoDir);

        Assert.That(result.IsError, Is.True, "repoPath is only accepted for read-only operations and list actions.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
        Assert.That(result.ErrorData?.Message, Does.Contain("repoPath"));
        Assert.That(TagNames(), Is.Empty);
    }

    [Test]
    public async Task Git_Stash_List_Empty_ReturnsEmptyListAsync()
    {
        var result = await _gitTools.Git(reason: "list stash without entries", GitOperation.stash, action: GitAction.list);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var stash = (GitStashResult)result.SuccessData!;
        Assert.That(stash.Entries, Is.Empty);
        Assert.That(stash.TotalCount, Is.EqualTo(0));
        Assert.That(stash.IsTruncated, Is.False);
    }

    [Test]
    public async Task Git_Stash_Push_NoChanges_ReportsGitNoChangesAndCreatesNoEntryAsync()
    {
        var result = await _gitTools.Git(reason: "stash a clean tree", GitOperation.stash, action: GitAction.push);

        AssertCoded(result, "GitNoChanges");
        Assert.That(result.ErrorData?.Message, Does.Contain("Nothing to stash"));
        Assert.That(StashEntryCount(), Is.EqualTo(0));
    }

    [Test]
    public async Task Git_Stash_Push_StashesTrackedChangeAndLeavesUntrackedFileAsync()
    {
        WriteFile("README.md", "changed");
        WriteFile("untracked.txt", "keep me");

        var result = await _gitTools.Git(reason: "stash tracked change", GitOperation.stash, action: GitAction.push);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var stash = (GitStashResult)result.SuccessData!;
        Assert.That(stash.Created, Is.True);
        Assert.That(StashEntryCount(), Is.EqualTo(1));
        Assert.That(ReadRepoFile("README.md"), Is.EqualTo("hello"), "the tracked change must be stashed away.");
        Assert.That(RepoFileExists("untracked.txt"), Is.True, "untracked files stay put unless includeUntracked is set.");
    }

    [Test]
    public async Task Git_Stash_Push_IncludeUntracked_StashesUntrackedFileAsync()
    {
        WriteFile("untracked.txt", "stash me");

        var withoutFlag = await _gitTools.Git(reason: "stash only an untracked file without the flag", GitOperation.stash, action: GitAction.push);
        AssertCoded(withoutFlag, "GitNoChanges");
        Assert.That(RepoFileExists("untracked.txt"), Is.True);

        var result = await _gitTools.Git(reason: "stash untracked file", GitOperation.stash, action: GitAction.push, includeUntracked: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((GitStashResult)result.SuccessData!).Created, Is.True);
        Assert.That(RepoFileExists("untracked.txt"), Is.False);
        Assert.That(GitOutStrict("show", "--name-only", "--format=", "stash@{0}^3"), Does.Contain("untracked.txt"));
    }

    [Test]
    public async Task Git_Stash_Push_WithMessageAndPaths_StashesOnlyListedPathAsync()
    {
        WriteFile("other.txt", "orig");
        RunGit(_repoDir, "add", "-A");
        RunGit(_repoDir, "commit", "-m", "add other.txt");
        WriteFile("README.md", "readme changed");
        WriteFile("other.txt", "other changed");

        var result = await _gitTools.Git(reason: "stash one path with a label", GitOperation.stash, action: GitAction.push, message: "only readme", paths: "README.md");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var stash = (GitStashResult)result.SuccessData!;
        Assert.That(stash.Created, Is.True);
        Assert.That(stash.Message, Is.EqualTo("only readme"));
        Assert.That(ReadRepoFile("README.md"), Is.EqualTo("hello"), "the listed path must be stashed away.");
        Assert.That(ReadRepoFile("other.txt"), Is.EqualTo("other changed"), "an unlisted path must stay modified.");
        Assert.That(StashEntryCount(), Is.EqualTo(1));
    }

    [Test]
    public async Task Git_Stash_List_ReportsIndexMessageAndHashAsync()
    {
        WriteFile("README.md", "first change");
        var first = await _gitTools.Git(reason: "stash first", GitOperation.stash, action: GitAction.push, message: "label one");
        Assert.That(first.IsError, Is.False, first.ErrorData?.Message);
        WriteFile("README.md", "second change");
        var second = await _gitTools.Git(reason: "stash second", GitOperation.stash, action: GitAction.push, message: "label two");
        Assert.That(second.IsError, Is.False, second.ErrorData?.Message);

        var list = await _gitTools.Git(reason: "list stash", GitOperation.stash, action: GitAction.list);

        Assert.That(list.IsError, Is.False, list.ErrorData?.Message);
        var stash = (GitStashResult)list.SuccessData!;
        Assert.That(stash.TotalCount, Is.EqualTo(2));
        Assert.That(stash.Entries.Select(e => e.Index), Is.EqualTo(new[] { 0, 1 }));
        Assert.That(stash.Entries[0].Message, Is.EqualTo("label two"), "index 0 is the newest entry.");
        Assert.That(stash.Entries[1].Message, Is.EqualTo("label one"));
        Assert.That(stash.Entries[0].Hash, Is.Not.Empty);
        Assert.That(stash.Entries[0].Branch, Is.Not.Null.And.Not.Empty);
        Assert.That(stash.Entries[0].Date, Is.Not.Empty);
    }

    [Test]
    public async Task Git_Stash_Pop_WithoutStashIndex_ReportsGitRefRequiredAndKeepsEntryAsync()
    {
        MakeOneStashEntry();

        var result = await _gitTools.Git(reason: "pop without an index", GitOperation.stash, action: GitAction.pop);

        AssertCoded(result, "GitRefRequired");
        Assert.That(result.ErrorData?.Message, Does.Contain("stashIndex"));
        Assert.That(StashEntryCount(), Is.EqualTo(1));
        Assert.That(ReadRepoFile("README.md"), Is.EqualTo("hello"));
    }

    [Test]
    public async Task Git_Stash_Pop_UnknownIndex_ReportsGitTargetNotFoundAsync()
    {
        MakeOneStashEntry();

        var result = await _gitTools.Git(reason: "pop an index that does not exist", GitOperation.stash, action: GitAction.pop, stashIndex: 5);

        AssertCoded(result, "GitTargetNotFound");
        Assert.That(result.ErrorData?.Message, Does.Contain("stash@{5}"));
        Assert.That(StashEntryCount(), Is.EqualTo(1));
    }

    [Test]
    public async Task Git_Stash_Pop_DirtyTree_ReportsGitWorkingTreeDirtyAndKeepsEntryAsync()
    {
        MakeOneStashEntry();
        WriteFile("README.md", "dirty again");

        var result = await _gitTools.Git(reason: "pop onto a dirty tree", GitOperation.stash, action: GitAction.pop, stashIndex: 0);

        AssertCoded(result, "GitWorkingTreeDirty");
        Assert.That(result.ErrorData?.Message, Does.Contain("README.md"));
        Assert.That(StashEntryCount(), Is.EqualTo(1));
        Assert.That(ReadRepoFile("README.md"), Is.EqualTo("dirty again"), "the refused pop must not touch the tree.");
    }

    [Test]
    public async Task Git_Stash_Pop_RestoresChangeAndDropsEntryAsync()
    {
        MakeOneStashEntry();

        var result = await _gitTools.Git(reason: "pop the newest entry", GitOperation.stash, action: GitAction.pop, stashIndex: 0);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((GitStashResult)result.SuccessData!).AppliedIndex, Is.EqualTo(0));
        Assert.That(ReadRepoFile("README.md"), Is.EqualTo("changed"));
        Assert.That(StashEntryCount(), Is.EqualTo(0), "pop must drop the entry once it applied cleanly.");
    }

    [Test]
    public async Task Git_Stash_Apply_RestoresChangeAndKeepsEntryAsync()
    {
        MakeOneStashEntry();

        var result = await _gitTools.Git(reason: "apply the newest entry", GitOperation.stash, action: GitAction.apply, stashIndex: 0);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((GitStashResult)result.SuccessData!).AppliedIndex, Is.EqualTo(0));
        Assert.That(ReadRepoFile("README.md"), Is.EqualTo("changed"));
        Assert.That(StashEntryCount(), Is.EqualTo(1), "apply must keep the entry.");
    }

    [Test]
    public async Task Git_Stash_Push_Result_NoteMentionsLoadSolutionAsync()
    {
        WriteFile("README.md", "changed");

        var result = await _gitTools.Git(reason: "stash and read the note", GitOperation.stash, action: GitAction.push);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((GitStashResult)result.SuccessData!).Note, Does.Contain("LoadSolution(forceReload: true)"));
    }

    [Test]
    public async Task Git_Stash_Push_WithRepoPath_IsRefusedAsync()
    {
        WriteFile("README.md", "changed");

        var result = await _gitTools.Git(reason: "stash with repoPath", GitOperation.stash, action: GitAction.push, repoPath: _repoDir);

        Assert.That(result.IsError, Is.True, "repoPath is only accepted for read-only operations and list actions.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
        Assert.That(result.ErrorData?.Message, Does.Contain("repoPath"));
        Assert.That(StashEntryCount(), Is.EqualTo(0));
        Assert.That(ReadRepoFile("README.md"), Is.EqualTo("changed"), "the refused push must not touch the tree.");
    }

    [Test]
    public async Task Git_StashIndexAndIncludeUntracked_OnOtherOperation_AreRefusedAsync()
    {
        var withIndex = await _gitTools.Git(reason: "stashIndex on status", GitOperation.status, stashIndex: 0);
        var withUntracked = await _gitTools.Git(reason: "includeUntracked on status", GitOperation.status, includeUntracked: true);

        Assert.That(withIndex.IsError, Is.True);
        Assert.That(withIndex.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
        Assert.That(withIndex.ErrorData?.Message, Does.Contain("stashIndex"));
        Assert.That(withUntracked.IsError, Is.True);
        Assert.That(withUntracked.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
        Assert.That(withUntracked.ErrorData?.Message, Does.Contain("includeUntracked"));
    }

    [Test]
    public async Task Git_Stash_Pop_UntrackedFileInTheWay_FailsAndKeepsEntryAsync()
    {
        WriteFile("untracked.txt", "stashed copy");
        RunGit(_repoDir, "stash", "push", "--include-untracked");
        WriteFile("untracked.txt", "new file in the way");

        var result = await _gitTools.Git(reason: "pop over a colliding untracked file", GitOperation.stash, action: GitAction.pop, stashIndex: 0);

        Assert.That(result.IsError, Is.True);
        Assert.That(StashEntryCount(), Is.EqualTo(1), "a failed pop must keep the entry.");
        Assert.That(ReadRepoFile("untracked.txt"), Is.EqualTo("new file in the way"), "the colliding file must not be overwritten.");
        Assert.That(GitOutStrict("status", "--porcelain", "--untracked-files=no"), Is.Empty);
    }

    [Test]
    public async Task Git_Stash_Pop_Conflict_RollsBackToCleanTreeKeepsEntryAndReportsGitStashConflictAsync()
    {
        await MakeStashConflictAsync();

        var result = await _gitTools.Git(reason: "pop a stash that conflicts", GitOperation.stash, action: GitAction.pop, stashIndex: 0);

        AssertStashConflictRolledBack(result);
    }

    [Test]
    public async Task Git_Stash_Apply_Conflict_RollsBackToCleanTreeKeepsEntryAsync()
    {
        await MakeStashConflictAsync();

        var result = await _gitTools.Git(reason: "apply a stash that conflicts", GitOperation.stash, action: GitAction.apply, stashIndex: 0);

        AssertStashConflictRolledBack(result);
    }

    // D-18 setup: README.md is edited and stashed through the tool, then a different edit to the same
    // line is committed, so applying stash@{0} must conflict.
    private async Task MakeStashConflictAsync()
    {
        WriteFile("README.md", "from stash");
        var push = await _gitTools.Git(reason: "stash the first edit", GitOperation.stash, action: GitAction.push);
        Assert.That(push.IsError, Is.False, push.ErrorData?.Message);
        WriteFile("README.md", "from commit");
        RunGit(_repoDir, "commit", "-am", "conflicting change to the same line");
    }

    // The four repo assertions of D-18: clean tracked tree, no unmerged paths, entry kept, committed content intact.
    private void AssertStashConflictRolledBack(SentinelCallToolResult<object> result)
    {
        AssertCoded(result, "GitStashConflict");
        Assert.That(result.ErrorData?.Message, Does.Contain("rolled back"));
        Assert.That(result.ErrorData?.Message, Does.Contain("stash@{0} was kept"));
        Assert.That(GitOutStrict("status", "--porcelain", "--untracked-files=no"), Is.Empty, "the tracked tree must be clean after the rollback.");
        Assert.That(GitOutStrict("diff", "--name-only", "--diff-filter=U"), Is.Empty, "no path may stay unmerged.");
        Assert.That(StashEntryCount(), Is.EqualTo(1), "the stash entry must be kept.");
        var content = ReadRepoFile("README.md");
        Assert.That(content, Does.Not.Contain("<<<<<<<"), "no conflict markers may remain.");
        Assert.That(content, Is.EqualTo("from commit"), "README.md must equal the committed content.");
    }

    [Test]
    public async Task Git_Worktree_List_ReportsMainAndCurrentAsync()
    {
        var result = await _gitTools.Git(reason: "list worktrees", GitOperation.worktree, action: GitAction.list);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var worktrees = (GitWorktreeResult)result.SuccessData!;
        Assert.That(worktrees.Worktrees, Has.Count.EqualTo(1));
        Assert.That(worktrees.Worktrees[0].IsMain, Is.True);
        Assert.That(worktrees.Worktrees[0].IsCurrent, Is.True);
        Assert.That(worktrees.Worktrees[0].IsLocked, Is.False);
        Assert.That(worktrees.Worktrees[0].Branch, Is.Not.Null.And.Not.Empty);
    }

    // Step 7 helpers (worktree).
    private string CurrentBranch() => GitOutStrict("branch", "--show-current");

    private Task<SentinelCallToolResult<object>> AddWorktreeAsync(string branch, bool createBranch = true, string? path = null)
        => _gitTools.Git(reason: "add a worktree", GitOperation.worktree, action: GitAction.add, worktreePath: path ?? WorktreeDir, branchName: branch, createBranch: createBranch);

    private Task<SentinelCallToolResult<object>> RemoveWorktreeAsync(bool discard = false, string? path = null)
        => _gitTools.Git(reason: "remove a worktree", GitOperation.worktree, action: GitAction.remove, worktreePath: path ?? WorktreeDir, discardUncommittedChanges: discard);

    // Adds WorktreeDir on a new branch, then leaves one modified tracked file and one untracked file inside it.
    private async Task MakeDirtyWorktreeAsync()
    {
        var add = await AddWorktreeAsync("wt-dirty");
        Assert.That(add.IsError, Is.False, add.ErrorData?.Message);
        File.WriteAllText(Path.Combine(WorktreeDir, "README.md"), "edited in the worktree");
        File.WriteAllText(Path.Combine(WorktreeDir, "scratch.txt"), "untracked");
    }

    [Test]
    public async Task Git_Worktree_Add_NewBranchOutsideRepo_CreatesWorktreeAsync()
    {
        var result = await AddWorktreeAsync("wt-feature");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var added = (GitWorktreeResult)result.SuccessData!;
        Assert.That(added.Added, Is.Not.Null.And.Not.Empty);
        Assert.That(added.Branch, Is.EqualTo("wt-feature"));
        Assert.That(added.Note, Does.Contain("LoadSolution"));
        Assert.That(File.Exists(Path.Combine(WorktreeDir, "README.md")), Is.True, "the worktree must be checked out on disk.");
        Assert.That(GitOutStrict("branch", "--list", "wt-feature"), Does.Contain("wt-feature"));

        var list = await _gitTools.Git(reason: "list worktrees", GitOperation.worktree, action: GitAction.list);
        var worktrees = ((GitWorktreeResult)list.SuccessData!).Worktrees;
        Assert.That(worktrees, Has.Count.EqualTo(2));
        Assert.That(worktrees.Count(w => w.IsMain), Is.EqualTo(1));
        Assert.That(worktrees.Single(w => !w.IsMain).Branch, Is.EqualTo("wt-feature"));
        Assert.That(worktrees.Single(w => !w.IsMain).IsCurrent, Is.False);
    }

    [Test]
    public async Task Git_Worktree_Add_PathInsideRepo_ReportsGitRefusedAsync()
    {
        var inside = Path.Combine(_repoDir, "inner_wt");

        var result = await AddWorktreeAsync("wt-inside", path: inside);

        AssertCoded(result, "GitRefused");
        Assert.That(Directory.Exists(inside), Is.False, "nothing may be created inside the repo.");
        Assert.That(GitOutStrict("branch", "--list", "wt-inside"), Is.Empty, "no branch may be created by a refused add.");
    }

    [Test]
    public async Task Git_Worktree_Add_RelativePath_ReportsGitRefusedAsync()
    {
        var result = await AddWorktreeAsync("wt-relative", path: Path.Combine("sub", "wt"));

        AssertCoded(result, "GitRefused");
        Assert.That(result.ErrorData?.Message, Does.Contain("absolute"));
    }

    [Test]
    public async Task Git_Worktree_Add_NonEmptyExistingPath_ReportsGitAlreadyExistsAsync()
    {
        Directory.CreateDirectory(WorktreeDir);
        File.WriteAllText(Path.Combine(WorktreeDir, "occupant.txt"), "already here");

        var result = await AddWorktreeAsync("wt-occupied");

        AssertCoded(result, "GitAlreadyExists");
        Assert.That(File.Exists(Path.Combine(WorktreeDir, "occupant.txt")), Is.True);
        Assert.That(GitOutStrict("branch", "--list", "wt-occupied"), Is.Empty);
    }

    [Test]
    public async Task Git_Worktree_Add_BranchAlreadyCheckedOutElsewhere_ReportsGitAlreadyExistsNamingThatPathAsync()
    {
        // The main worktree has the current branch checked out; a second checkout of it is refused.
        var result = await AddWorktreeAsync(CurrentBranch(), createBranch: false);

        AssertCoded(result, "GitAlreadyExists");
        Assert.That(result.ErrorData?.Message, Does.Contain(Path.GetFileName(_repoDir)), "the message must name the worktree holding the branch.");
        Assert.That(Directory.Exists(WorktreeDir), Is.False);
    }

    [Test]
    public async Task Git_Worktree_Add_MissingBranchWithoutCreateBranch_ReportsGitTargetNotFoundAsync()
    {
        var result = await AddWorktreeAsync("no-such-branch", createBranch: false);

        AssertCoded(result, "GitTargetNotFound");
        Assert.That(result.ErrorData?.Detail, Does.Contain("createBranch"));
        Assert.That(Directory.Exists(WorktreeDir), Is.False);
    }

    [Test]
    public async Task Git_Worktree_Add_CreateBranchOnExistingBranch_ReportsGitAlreadyExistsAsync()
    {
        RunGit(_repoDir, "branch", "already-there");

        var result = await AddWorktreeAsync("already-there", createBranch: true);

        AssertCoded(result, "GitAlreadyExists");
        Assert.That(Directory.Exists(WorktreeDir), Is.False);
    }

    [Test]
    public async Task Git_Worktree_Remove_CleanWorktree_RemovesDirectoryAndKeepsBranchAsync()
    {
        var add = await AddWorktreeAsync("wt-clean");
        Assert.That(add.IsError, Is.False, add.ErrorData?.Message);

        var result = await RemoveWorktreeAsync();

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var removed = (GitWorktreeResult)result.SuccessData!;
        Assert.That(removed.Removed, Is.Not.Null.And.Not.Empty);
        Assert.That(removed.DiscardedPaths, Is.Null.Or.Empty);
        Assert.That(removed.Note, Does.Contain("wt-clean"));
        Assert.That(Directory.Exists(WorktreeDir), Is.False);
        Assert.That(GitOutStrict("branch", "--list", "wt-clean"), Does.Contain("wt-clean"), "the branch must be kept.");
    }

    [Test]
    public async Task Git_Worktree_Remove_MainWorktree_ReportsGitRefusedAsync()
    {
        var result = await RemoveWorktreeAsync(path: _repoDir);

        AssertCoded(result, "GitRefused");
        Assert.That(Directory.Exists(Path.Combine(_repoDir, ".git")), Is.True);
        Assert.That(File.Exists(Path.Combine(_repoDir, "README.md")), Is.True);
    }

    [Test]
    public async Task Git_Worktree_Remove_Unknown_ReportsGitTargetNotFoundAsync()
    {
        var result = await RemoveWorktreeAsync(path: WorktreeDir);

        AssertCoded(result, "GitTargetNotFound");
    }

    [Test]
    public async Task Git_Worktree_WithoutAction_ReportsGitActionRequiredAsync()
    {
        var result = await _gitTools.Git(reason: "worktree without action", GitOperation.worktree);

        AssertCoded(result, "GitActionRequired");
        Assert.That(result.ErrorData?.Message, Does.Contain("list, add, remove"));
    }

    [Test]
    public async Task Git_Worktree_Remove_DirtyWorktree_WithoutFlag_ReportsGitWorkingTreeDirtyNamingFlagAndLeavesDirectoryAsync()
    {
        await MakeDirtyWorktreeAsync();

        var result = await RemoveWorktreeAsync();

        AssertCoded(result, "GitWorkingTreeDirty");
        Assert.That(result.ErrorData?.Message, Does.Contain("uncommitted changes"));
        Assert.That(result.ErrorData?.Message, Does.Contain("Nothing was removed"));
        Assert.That(result.ErrorData?.Message, Does.Contain("discardUncommittedChanges: true"));
        Assert.That(Directory.Exists(WorktreeDir), Is.True, "the directory must be left in place.");
        Assert.That(File.ReadAllText(Path.Combine(WorktreeDir, "README.md")), Is.EqualTo("edited in the worktree"));
        Assert.That(File.ReadAllText(Path.Combine(WorktreeDir, "scratch.txt")), Is.EqualTo("untracked"));
    }

    [Test]
    public async Task Git_Worktree_Remove_DirtyWorktree_WithFlag_RemovesAndReportsDiscardedPathsAsync()
    {
        await MakeDirtyWorktreeAsync();

        var result = await RemoveWorktreeAsync(discard: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var removed = (GitWorktreeResult)result.SuccessData!;
        Assert.That(removed.Removed, Is.Not.Null.And.Not.Empty);
        Assert.That(removed.DiscardedPaths, Is.Not.Null);
        Assert.That(removed.DiscardedPaths!.Any(p => p.Contains("README.md")), Is.True, "the modified tracked file must be listed.");
        Assert.That(removed.DiscardedPaths!.Any(p => p.Contains("scratch.txt")), Is.True, "the untracked file must be listed.");
        Assert.That(removed.Note, Does.Contain("not recoverable"));
        Assert.That(Directory.Exists(WorktreeDir), Is.False);
        Assert.That(GitOutStrict("branch", "--list", "wt-dirty"), Does.Contain("wt-dirty"), "the branch must be kept.");
    }

    [Test]
    public async Task Git_Worktree_Remove_CleanWorktree_WithFlag_RemovesWithoutDiscardedPathsAsync()
    {
        var add = await AddWorktreeAsync("wt-clean-flag");
        Assert.That(add.IsError, Is.False, add.ErrorData?.Message);

        var result = await RemoveWorktreeAsync(discard: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((GitWorktreeResult)result.SuccessData!).DiscardedPaths, Is.Null.Or.Empty);
        Assert.That(Directory.Exists(WorktreeDir), Is.False);
    }

    [Test]
    public async Task Git_Worktree_Remove_LockedWorktree_WithFlag_StillReportsGitRefusedAsync()
    {
        var add = await AddWorktreeAsync("wt-locked");
        Assert.That(add.IsError, Is.False, add.ErrorData?.Message);
        RunGit(_repoDir, "worktree", "lock", WorktreeDir);

        var result = await RemoveWorktreeAsync(discard: true);

        AssertCoded(result, "GitRefused");
        Assert.That(Directory.Exists(WorktreeDir), Is.True, "a locked worktree must be left in place.");
    }

    [Test]
    public async Task Git_DiscardUncommittedChanges_OnOtherOperationOrAction_IsRefusedAsync()
    {
        var onStatus = await _gitTools.Git(reason: "discard flag on status", GitOperation.status, discardUncommittedChanges: true);
        var onWorktreeList = await _gitTools.Git(reason: "discard flag on worktree list", GitOperation.worktree, action: GitAction.list, discardUncommittedChanges: true);

        foreach (var result in new[] { onStatus, onWorktreeList })
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
            Assert.That(result.ErrorData?.Message, Does.Contain("discardUncommittedChanges"));
        }
    }

    [Test]
    public async Task Git_WorktreePath_OnOtherOperation_IsRefusedAsync()
    {
        var result = await _gitTools.Git(reason: "worktreePath on status", GitOperation.status, worktreePath: WorktreeDir);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
        Assert.That(result.ErrorData?.Message, Does.Contain("worktreePath"));
    }

    [Test]
    public async Task Git_ActionOnOtherOperation_IsRefusedAsync()
    {
        var result = await _gitTools.Git(reason: "action on status", GitOperation.status, action: GitAction.list);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
        Assert.That(result.ErrorData?.Message, Does.Contain("action"));
    }

    // ---- operation=hunks ----

    // Commits a file of "line 1".."line N" (LF endings, written as bytes so the platform and any
    // global autocrlf setting cannot change them) and returns its lines for the caller to edit.
    private List<string> CommitNumberedFile(string relPath, int lineCount)
    {
        RunGit(_repoDir, "config", "core.autocrlf", "false");
        var lines = Enumerable.Range(1, lineCount).Select(n => "line " + n).ToList();
        WriteLines(relPath, lines);
        RunGit(_repoDir, "add", "--", relPath);
        RunGit(_repoDir, "commit", "-m", "add " + relPath);
        return lines;
    }

    private void WriteLines(string relPath, IEnumerable<string> lines, string eol = "\n")
    {
        File.WriteAllBytes(Path.Combine(_repoDir, relPath), System.Text.Encoding.UTF8.GetBytes(string.Join(eol, lines) + eol));
    }

    // Three distant, differently shaped edits: line 3 replaced, one line inserted after line 15,
    // original line 27 deleted. Zero-context hunks: (-3,1 +3,1) (-15,0 +16,1) (-27,1 +27,0); the deletion's new-file
    // position is git's "line before the deletion", shifted by the earlier insertion.
    private static List<string> ApplyThreeDistantEdits(List<string> lines)
    {
        var edited = new List<string>(lines);
        edited[2] = "line 3 changed";
        edited.Insert(15, "inserted after 15");
        edited.RemoveAt(27);
        return edited;
    }

    [Test]
    public async Task Git_Hunks_ListsSeparateHunksWithLineNumbersAsync()
    {
        var lines = CommitNumberedFile("a.txt", 30);
        WriteLines("a.txt", ApplyThreeDistantEdits(lines));

        var result = await _gitTools.Git(reason: "list hunks", GitOperation.hunks, files: "a.txt");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var hunks = (GitHunksResult)result.SuccessData!;
        Assert.That(hunks.Path, Is.EqualTo("a.txt"));
        Assert.That(hunks.TotalHunks, Is.EqualTo(3));
        Assert.That(hunks.IsTruncated, Is.False);
        Assert.That(hunks.Fingerprint, Is.Not.Null.And.Not.Empty);
        Assert.That(hunks.Hunks.Select(h => h.Index), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(hunks.Hunks.Select(h => h.NewStart), Is.EqualTo(new[] { 3, 16, 27 }));
        Assert.That(hunks.Hunks.Select(h => h.Added), Is.EqualTo(new[] { 1, 1, 0 }));
        Assert.That(hunks.Hunks.Select(h => h.Removed), Is.EqualTo(new[] { 1, 0, 1 }));
        Assert.That(hunks.Hunks[0].Preview, Does.Contain("line 3 changed"));
        Assert.That(hunks.Note, Does.Contain("hunkFingerprint"));
    }

    [Test]
    public async Task Git_Hunks_FingerprintChangesWhenFileChangesAsync()
    {
        var lines = CommitNumberedFile("a.txt", 30);
        var edited = ApplyThreeDistantEdits(lines);
        WriteLines("a.txt", edited);

        var first = (GitHunksResult)(await _gitTools.Git(reason: "list hunks", GitOperation.hunks, files: "a.txt")).SuccessData!;
        var again = (GitHunksResult)(await _gitTools.Git(reason: "list hunks again", GitOperation.hunks, files: "a.txt")).SuccessData!;
        edited[29] = "line 30 also changed";
        WriteLines("a.txt", edited);
        var changed = (GitHunksResult)(await _gitTools.Git(reason: "list hunks after another edit", GitOperation.hunks, files: "a.txt")).SuccessData!;

        Assert.That(again.Fingerprint, Is.EqualTo(first.Fingerprint), "an unchanged file must keep its fingerprint.");
        Assert.That(changed.Fingerprint, Is.Not.EqualTo(first.Fingerprint));
        Assert.That(changed.TotalHunks, Is.EqualTo(4));
    }

    [Test]
    public async Task Git_Hunks_CountCapsTheListedHunksButNotTheFingerprintAsync()
    {
        var lines = CommitNumberedFile("a.txt", 30);
        WriteLines("a.txt", ApplyThreeDistantEdits(lines));

        var all = (GitHunksResult)(await _gitTools.Git(reason: "list all hunks", GitOperation.hunks, files: "a.txt")).SuccessData!;
        var capped = await _gitTools.Git(reason: "list one hunk", GitOperation.hunks, files: "a.txt", count: 1);

        Assert.That(capped.IsError, Is.False, capped.ErrorData?.Message);
        var cappedHunks = (GitHunksResult)capped.SuccessData!;
        Assert.That(cappedHunks.Hunks, Has.Count.EqualTo(1));
        Assert.That(cappedHunks.TotalHunks, Is.EqualTo(3));
        Assert.That(cappedHunks.IsTruncated, Is.True);
        Assert.That(cappedHunks.Fingerprint, Is.EqualTo(all.Fingerprint), "the fingerprint covers the whole file diff, not the listed subset.");
    }

    [Test]
    public async Task Git_Hunks_UntrackedFile_ReportsGitHunkUnsupportedAsync()
    {
        WriteFile("brand-new.txt", "one\ntwo\n");

        var result = await _gitTools.Git(reason: "hunks of untracked file", GitOperation.hunks, files: "brand-new.txt");

        AssertCoded(result, "GitHunkUnsupported");
        Assert.That(result.ErrorData?.Message, Does.Contain("untracked"));
    }

    [Test]
    public async Task Git_Hunks_UnchangedFile_ReportsGitNoChangesAsync()
    {
        var result = await _gitTools.Git(reason: "hunks of unchanged file", GitOperation.hunks, files: "README.md");

        AssertCoded(result, "GitNoChanges");
        Assert.That(result.ErrorData?.Message, Does.Contain("README.md"));
    }

    [Test]
    public async Task Git_Hunks_TwoPaths_ReportsGitHunkSelectionAsync()
    {
        CommitNumberedFile("a.txt", 5);

        var result = await _gitTools.Git(reason: "hunks of two files", GitOperation.hunks, files: "README.md,a.txt");

        AssertCoded(result, "GitHunkSelection");
        Assert.That(result.ErrorData?.Message, Does.Contain("one file at a time"));
    }

    [Test]
    public async Task Git_Hunks_WithoutFiles_ReportsGitRefRequiredAsync()
    {
        var result = await _gitTools.Git(reason: "hunks without files", GitOperation.hunks);

        AssertCoded(result, "GitRefRequired");
        Assert.That(result.ErrorData?.Detail, Does.Contain("files"));
    }

    [Test]
    public async Task Git_Hunks_BinaryFile_ReportsGitHunkUnsupportedAsync()
    {
        RunGit(_repoDir, "config", "core.autocrlf", "false");
        File.WriteAllBytes(Path.Combine(_repoDir, "blob.bin"), [0x00, 0x01, 0x02, 0x03]);
        RunGit(_repoDir, "add", "--", "blob.bin");
        RunGit(_repoDir, "commit", "-m", "add binary");
        File.WriteAllBytes(Path.Combine(_repoDir, "blob.bin"), [0x00, 0x01, 0x09, 0x03, 0x00]);

        var result = await _gitTools.Git(reason: "hunks of binary file", GitOperation.hunks, files: "blob.bin");

        AssertCoded(result, "GitHunkUnsupported");
        Assert.That(result.ErrorData?.Message, Does.Contain("binary").IgnoreCase);
    }

    [Test]
    public async Task Git_Hunks_CrlfFile_ParsesAsync()
    {
        // Committed with CRLF and autocrlf off, so the diff body lines carry a literal CR.
        RunGit(_repoDir, "config", "core.autocrlf", "false");
        var lines = Enumerable.Range(1, 30).Select(n => "crlf line " + n).ToList();
        WriteLines("crlf.txt", lines, "\r\n");
        RunGit(_repoDir, "add", "--", "crlf.txt");
        RunGit(_repoDir, "commit", "-m", "add crlf file");
        lines[4] = "crlf line 5 changed";
        lines[24] = "crlf line 25 changed";
        WriteLines("crlf.txt", lines, "\r\n");

        var result = await _gitTools.Git(reason: "hunks of CRLF file", GitOperation.hunks, files: "crlf.txt");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var hunks = (GitHunksResult)result.SuccessData!;
        Assert.That(hunks.TotalHunks, Is.EqualTo(2));
        Assert.That(hunks.Hunks.Select(h => h.NewStart), Is.EqualTo(new[] { 5, 25 }));
        Assert.That(hunks.Hunks.Select(h => h.Added), Is.EqualTo(new[] { 1, 1 }));
        Assert.That(hunks.Hunks.Select(h => h.Removed), Is.EqualTo(new[] { 1, 1 }));
        Assert.That(hunks.Hunks[0].Preview, Does.Contain("crlf line 5 changed"));
    }

    [Test]
    public async Task Git_Hunks_RepoPath_TargetsThatRepoAsync()
    {
        var otherRepoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitSmoke_Other_" + Guid.NewGuid());
        Directory.CreateDirectory(otherRepoDir);
        try
        {
            RunGit(otherRepoDir, "init");
            RunGit(otherRepoDir, "config", "user.email", "test@example.com");
            RunGit(otherRepoDir, "config", "user.name", "Test");
            RunGit(otherRepoDir, "config", "core.autocrlf", "false");
            File.WriteAllBytes(Path.Combine(otherRepoDir, "OTHER.md"), System.Text.Encoding.UTF8.GetBytes("other repo\n"));
            RunGit(otherRepoDir, "add", "-A");
            RunGit(otherRepoDir, "commit", "-m", "other repo initial commit");
            File.WriteAllBytes(Path.Combine(otherRepoDir, "OTHER.md"), System.Text.Encoding.UTF8.GetBytes("other repo changed\n"));

            // OTHER.md does not exist in the loaded solution's repo (_repoDir); if repoPath were ignored this would fail.
            var result = await _gitTools.Git(reason: "hunks in other repo", GitOperation.hunks, files: "OTHER.md", repoPath: otherRepoDir);

            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            var hunks = (GitHunksResult)result.SuccessData!;
            Assert.That(hunks.Path, Is.EqualTo("OTHER.md"));
            Assert.That(hunks.TotalHunks, Is.EqualTo(1));
        }
        finally
        {
            DeleteDirectoryTree(otherRepoDir);
        }
    }
}
