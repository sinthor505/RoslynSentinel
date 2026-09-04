// Smoke coverage for the Git tool's read-only operations (status/log/diff). Not a repro attempt for
// the unreproduced one-off "Git(operation: status) hung indefinitely" TODO entry — the 30s
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
    // Generous relative to GitProcessTimeout's 30s — this isn't testing the timeout boundary
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
        var result = await _gitTools.Git(reason: "test", GitOperation.status);
        sw.Stop();

        Assert.That(result, Is.Not.Null);
        Assert.That(sw.Elapsed, Is.LessThan(ResponseBound), $"Git(status) took {sw.Elapsed}, expected under {ResponseBound}.");
    }

    [Test]
    public async Task Git_Log_RespondsWithinBoundAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _gitTools.Git(reason: "test", GitOperation.log, count: 5);
        sw.Stop();

        Assert.That(result, Is.Not.Null);
        Assert.That(sw.Elapsed, Is.LessThan(ResponseBound), $"Git(log) took {sw.Elapsed}, expected under {ResponseBound}.");
    }

    [Test]
    public async Task Git_Diff_RespondsWithinBoundAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "hello again");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _gitTools.Git(reason: "test", GitOperation.diff);
        sw.Stop();

        Assert.That(result, Is.Not.Null);
        Assert.That(sw.Elapsed, Is.LessThan(ResponseBound), $"Git(diff) took {sw.Elapsed}, expected under {ResponseBound}.");
    }
}
