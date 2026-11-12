using System.Diagnostics;
using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;
using static RoslynSentinel.Tests.Tools.Basic.CasCommitTestSupport;
using static RoslynSentinel.Tests.Tools.Basic.GitHookTestSupport;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="GitHooks.LaunchPostHook"/> with marker hooks (a <c>#!/bin/sh</c> script that writes a marker file) in real temporary repositories and the real <c>sh.exe</c>: the launch returns
/// before a slow hook finishes and the hook completes later; the hook runs in the work tree root (also from a subdirectory and in a linked worktree); GIT_* variables are scrubbed from the child
/// (the ambient environment is INJECTED through <see cref="GitHookOptions.AmbientEnvironment"/>, so no process-wide variable is set) while the rest passes through and git's tool directories are on PATH;
/// a hook printing 1 MB to stdout and stderr neither blocks nor hangs the caller; stdin is closed (a hook running <c>cat</c> sees EOF); arguments are passed; and every failure is a typed
/// result, never an exception. Leftover hook processes are killed by pid in TearDown, and the temp directory is deleted with retries because a just-exited child may still hold it as its cwd.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git and sh.exe): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// The timing assertions are deliberately generous (launch under 1500 ms against a 3 s hook; markers awaited up to 15 s) so a loaded machine does not fail them.
[TestFixture]
[Category("OutOfProcess")] // starts git and sh.exe processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHooksLaunchTests
{
    private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(15);

    private string _root = null!;
    private readonly List<int> _launched = [];

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        if (!GitShellLocator.Locate().Found)
        {
            Assert.Ignore("Git for Windows' sh.exe is not available.");
        }

        _root = NewRoot("rs-hooklaunch-");
        _launched.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var processId in _launched)
        {
            KillIfRunning(processId);
        }

        DeleteTreeWithRetries(_root);
    }

    private HookLaunchResult Launch(string workingDirectory, string hookName = "post-commit", IReadOnlyList<string>? arguments = null, GitHookOptions? options = null)
    {
        var result = GitHooks.LaunchPostHook(workingDirectory, hookName, arguments, options);
        if (result.ProcessId is { } processId)
        {
            _launched.Add(processId);
        }

        return result;
    }

    private string RepoWithMarkerHook(string name, string prelude, out string marker, string hookName = "post-commit")
    {
        var repo = NewRepo(_root, name);
        marker = Path.Combine(_root, name + ".marker");
        WriteHook(Path.Combine(repo, ".git", "hooks"), hookName, MarkerScript(marker, prelude));
        return repo;
    }

    [Test]
    public void SlowHook_LaunchReturnsImmediately_AndTheHookFinishesLater_InTheWorkTreeRoot()
    {
        var repo = RepoWithMarkerHook("slow", "sleep 3", out var marker);
        Launch(repo); // warm-up of libgit2 and the shell memo: the assertion below measures the launch, not the first-use cost
        WaitForMarker(marker, MarkerTimeout);
        File.Delete(marker);

        var clock = Stopwatch.StartNew();
        var result = Launch(repo);
        clock.Stop();

        Assert.That(result.Outcome, Is.EqualTo(HookLaunchOutcome.Launched), result.Detail);
        Assert.That(result.ProcessId, Is.Not.Null);
        Assert.That(clock.ElapsedMilliseconds, Is.LessThan(1500), "the launch must not wait for the 3 s hook");
        Assert.That(File.Exists(marker), Is.False, "the hook is still sleeping when the launch returns");

        var lines = WaitForMarker(marker, MarkerTimeout);

        Assert.That(Canon(lines["cwd"]), Is.EqualTo(Canon(repo)).IgnoreCase);
        Assert.That(lines["tool"], Is.EqualTo("ok"), "git's tool directories (cat, env, grep) must be on the child's PATH");
    }

    [Test]
    public void SubdirectoryAndLinkedWorktree_RunTheHookInTheirOwnWorkTreeRoot()
    {
        var main = RepoWithMarkerHook("cwd-main", "", out var marker);
        var sub = Path.Combine(main, "some", "dir");
        Directory.CreateDirectory(sub);

        Assert.That(Launch(sub).Outcome, Is.EqualTo(HookLaunchOutcome.Launched));
        Assert.That(Canon(WaitForMarker(marker, MarkerTimeout)["cwd"]), Is.EqualTo(Canon(main)).IgnoreCase, "from a subdirectory");
        File.Delete(marker);

        var linked = Path.Combine(_root, "cwd-linked");
        RunGit(main, "worktree", "add", "-q", "--detach", linked);

        Assert.That(Launch(linked).Outcome, Is.EqualTo(HookLaunchOutcome.Launched));
        Assert.That(Canon(WaitForMarker(marker, MarkerTimeout)["cwd"]), Is.EqualTo(Canon(linked)).IgnoreCase, "a linked worktree runs the common directory's hook in its own root");
    }

    [Test]
    public void RelativeHooksPath_RunsTheHookFromTheConfiguredDirectory()
    {
        var repo = NewRepo(_root, "relative");
        RunGit(repo, "config", "core.hooksPath", ".githooks");
        var marker = Path.Combine(_root, "relative.marker");
        WriteHook(Path.Combine(repo, ".githooks"), "post-commit", MarkerScript(marker));

        Assert.That(Launch(repo).Outcome, Is.EqualTo(HookLaunchOutcome.Launched));
        Assert.That(Canon(WaitForMarker(marker, MarkerTimeout)["cwd"]), Is.EqualTo(Canon(repo)).IgnoreCase);
    }

    [Test]
    public void GitVariables_AreScrubbedFromTheChild_AndTheRestOfTheEnvironmentPassesThrough()
    {
        var repo = RepoWithMarkerHook("scrub", "", out var marker);
        // None of these is in the reader's decline list (that would decline the launch); all of them are removed from the child. RS_SENTINEL is not GIT_* and must survive.
        var options = new GitHookOptions
        {
            AmbientEnvironment = AmbientWith(("GIT_AUTHOR_NAME", "someone"), ("GIT_TERMINAL_PROMPT", "0"), ("GIT_EXEC_PATH", @"C:\nowhere"), ("git_lowercase", "x"), ("RS_SENTINEL", "kept")),
        };

        var result = Launch(repo, options: options);

        Assert.That(result.Outcome, Is.EqualTo(HookLaunchOutcome.Launched), result.Detail);
        var lines = WaitForMarker(marker, MarkerTimeout);
        Assert.That(lines["gitvars"], Is.EqualTo("0"));
        Assert.That(lines["sentinel"], Is.EqualTo("kept"));
        Assert.That(lines["tool"], Is.EqualTo("ok"));
    }

    [Test]
    public void LargeOutputOnStdoutAndStderr_NeitherBlocksTheHookNorTheCaller()
    {
        var repo = RepoWithMarkerHook("loud", "head -c 1048576 /dev/zero | tr '\\000' 'x'\nhead -c 1048576 /dev/zero | tr '\\000' 'y' >&2", out var marker);

        var clock = Stopwatch.StartNew();
        var result = Launch(repo);
        clock.Stop();

        Assert.That(result.Outcome, Is.EqualTo(HookLaunchOutcome.Launched), result.Detail);
        Assert.That(clock.ElapsedMilliseconds, Is.LessThan(1500));
        WaitForMarker(marker, MarkerTimeout); // written after the output: only reached when nothing blocked on a full pipe
    }

    [Test]
    public void Stdin_IsClosed_SoAHookReadingItSeesEndOfFile()
    {
        var repo = RepoWithMarkerHook("stdin", "cat > /dev/null", out var marker);

        Assert.That(Launch(repo).Outcome, Is.EqualTo(HookLaunchOutcome.Launched));

        WaitForMarker(marker, MarkerTimeout);
    }

    [Test]
    public void Arguments_ArePassedToTheHook()
    {
        var repo = RepoWithMarkerHook("args", "", out var marker, hookName: "post-rewrite");

        var result = Launch(repo, "post-rewrite", ["amend", "two words"]);

        Assert.That(result.Outcome, Is.EqualTo(HookLaunchOutcome.Launched), result.Detail);
        Assert.That(WaitForMarker(marker, MarkerTimeout)["args"], Is.EqualTo("amend two words"));
    }

    [Test]
    public void NoHook_IsNotFound()
    {
        var repo = NewRepo(_root, "nohook");

        Assert.That(Launch(repo), Is.EqualTo(new HookLaunchResult(HookLaunchOutcome.NotFound)));
    }

    [Test]
    public void ShellNotFound_IsTypedAndCarriesTheProbeDetail()
    {
        var repo = RepoWithMarkerHook("nosh", "", out var marker);

        var result = Launch(repo, options: new GitHookOptions { LocateShell = () => new ShLocateResult(null, "probed nothing") });

        Assert.That(result.Outcome, Is.EqualTo(HookLaunchOutcome.ShNotFound));
        Assert.That(result.Detail, Is.EqualTo("probed nothing"));
        Assert.That(File.Exists(marker), Is.False);
    }

    [Test]
    public void ShellThatCannotBeStarted_IsLaunchFailed_NeverAnException()
    {
        var repo = RepoWithMarkerHook("badsh", "", out _);
        var options = new GitHookOptions { LocateShell = () => new ShLocateResult(Path.Combine(_root, "no", "such", "sh.exe")) };

        HookLaunchResult? result = null;
        Assert.That(() => result = Launch(repo, options: options), Throws.Nothing);

        Assert.That(result!.Outcome, Is.EqualTo(HookLaunchOutcome.LaunchFailed));
        Assert.That(result.ProcessId, Is.Null);
        Assert.That(result.Detail, Is.Not.Empty);
    }

    [Test]
    public void WorkTreeThatDoesNotExist_IsLaunchFailed_NeverAnException()
    {
        var hooks = Path.Combine(_root, "hooks");
        WriteHook(hooks, "post-commit", "#!/bin/sh\nexit 0\n");
        var layout = new HookRepositoryLayout(Path.Combine(_root, "vanished"), Path.Combine(_root, "vanished", ".git"), ForSh(hooks), false);

        var result = Launch(_root, options: new GitHookOptions { ReadLayout = _ => layout });

        Assert.That(result.Outcome, Is.EqualTo(HookLaunchOutcome.LaunchFailed));
    }

    [TestCase("pre-commit")]
    [TestCase("commit-msg")]
    [TestCase("prepost-commit")]
    public void OnlyPostHooksAreLaunched(string name)
    {
        var repo = RepoWithMarkerHook("pre-" + name, "", out var marker, hookName: name);

        var result = Launch(repo, name);

        Assert.That(result.Outcome, Is.EqualTo(HookLaunchOutcome.Declined));
        Assert.That(File.Exists(marker), Is.False);
    }

    [Test]
    public void AmbientGitDir_Declines_AndNothingRuns()
    {
        var repo = RepoWithMarkerHook("decline", "", out var marker);

        var result = Launch(repo, options: new GitHookOptions { AmbientEnvironment = AmbientWith(("GIT_DIR", Path.Combine(_root, "elsewhere"))) });

        Assert.That(result.Outcome, Is.EqualTo(HookLaunchOutcome.Declined));
        Assert.That(result.Detail, Does.Contain("GIT_DIR"));
        Thread.Sleep(300);
        Assert.That(File.Exists(marker), Is.False);
    }

    [Test]
    public void ConfigDefinedHooks_Decline_BecauseTheyCannotBeReproduced()
    {
        var repo = RepoWithMarkerHook("cfghook", "", out var marker);
        RunGit(repo, "config", "hook.mine.event", "post-commit");
        RunGit(repo, "config", "hook.mine.command", "echo hi");

        var result = Launch(repo);

        Assert.That(result.Outcome, Is.EqualTo(HookLaunchOutcome.Declined));
        Assert.That(File.Exists(marker), Is.False);
    }

    [TestCase("#!/bin/sh\nexit 0\n", true)]
    [TestCase("#!/bin/bash -e\nexit 0\n", true)]
    [TestCase("#!/usr/bin/env bash\nexit 0\n", true)]
    [TestCase("#!/usr/bin/env -S sh\nexit 0\n", true)]
    [TestCase("exit 0\n", true)]
    [TestCase("#!/usr/bin/env python3\nprint(1)\n", false)]
    [TestCase("#!/usr/bin/pwsh\nexit 0\n", false)]
    [TestCase("#!\nexit 0\n", false)]
    public void Shebang_DecidesWhetherShExeIsAFaithfulStandIn(string script, bool shExeStandsIn)
    {
        // The shell is a nonexistent file: LaunchFailed means the shebang check passed and the start was attempted, Declined means the shebang was rejected. No process runs.
        var repo = NewRepo(_root, "shebang");
        WriteHook(Path.Combine(repo, ".git", "hooks"), "post-commit", script);
        var options = new GitHookOptions { LocateShell = () => new ShLocateResult(Path.Combine(_root, "no", "such", "sh.exe")) };

        Assert.That(Launch(repo, options: options).Outcome, Is.EqualTo(shExeStandsIn ? HookLaunchOutcome.LaunchFailed : HookLaunchOutcome.Declined));
    }
}
