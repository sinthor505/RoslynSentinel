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
public class SentinelGitToolsSmokeTests
{
    // Generous relative to GitProcessTimeout's 30s -> this isn't testing the timeout boundary
    // itself, just that a normal call on a tiny repo comes back promptly, not near the ceiling.
    private static readonly TimeSpan ResponseBound = TimeSpan.FromSeconds(10);

    private string _repoDir = null!;
    private SentinelGitTools _gitTools = null!;

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

        // SentinelGitTools only ever calls GetSolutionRoot() to find the git root - it doesn't need a real
        // Roslyn solution loaded, so FakeWorkspaceManager.SolutionPath alone is enough (see its own
        // "Mirrors PersistentWorkspaceManager.GetSolutionRoot()" comment).
        var workspaceManager = new FakeWorkspaceManager { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitTools = new SentinelGitTools(workspaceManager, NullLogger<SentinelGitTools>.Instance);
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
        var status = (GitStatusResult)result;
        Assert.That(status.Success, Is.True, status.Error);
        Assert.That(status.Staged.Select(s => s.Path), Does.Contain("README.md"),
            "git reset --soft should leave the second commit's change staged, not discarded.");

        var log = await _gitTools.Git(reason: "test message", GitOperation.log, count: 5);
        var logResult = (GitLogResult)log;
        Assert.That(logResult.Commits.Select(c => c.Message), Does.Not.Contain("second commit"),
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
        var status = (GitStatusResult)result;
        Assert.That(status.Success, Is.True, status.Error);
        Assert.That(status.Staged, Is.Empty, "git reset --mixed should leave nothing staged.");
        Assert.That(status.Unstaged.Select(s => s.Path), Does.Contain("README.md"),
            "git reset --mixed should leave the second commit's change unstaged, not discarded.");
        Assert.That(File.ReadAllText(Path.Combine(_repoDir, "README.md")), Is.EqualTo("second commit content"),
            "git reset --mixed must never touch the working tree.");
    }
}
