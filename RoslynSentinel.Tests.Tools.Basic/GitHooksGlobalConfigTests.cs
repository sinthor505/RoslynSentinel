using LibGit2Sharp;
using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;
using static RoslynSentinel.Tests.Tools.Basic.CasCommitTestSupport;
using static RoslynSentinel.Tests.Tools.Basic.GitHookTestSupport;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <c>core.hooksPath</c> set in the GLOBAL git config, by git's precedence (local over global). The real global config is never written: libgit2's global search path is pointed at a temporary
/// directory holding a <c>.gitconfig</c> (<see cref="GlobalSettings.SetConfigSearchPaths"/>, restored in TearDown), and the ground truth is a git child process whose
/// <c>GIT_CONFIG_GLOBAL</c> names the same file (set on that child only; the reader itself declines when GIT_CONFIG_GLOBAL is set in the server's environment).
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// NonParallelizable: libgit2's config search path is process-wide, so every parallel test's libgit2 reader would see the fake global config.
[TestFixture]
[NonParallelizable]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHooksGlobalConfigTests
{
    private string _root = null!;
    private string _globalDirectory = null!;
    private string _globalFile = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = NewRoot("rs-hookglobal-");
        _globalDirectory = Path.Combine(_root, "fake-home");
        Directory.CreateDirectory(_globalDirectory);
        _globalFile = Path.Combine(_globalDirectory, ".gitconfig");
        File.WriteAllText(_globalFile, string.Empty);
        GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Global, _globalDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        // First: a failed assertion must never leave the fake global config in place for the next fixture. Null restores libgit2's defaults.
        GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Global, null!);
        DeleteTreeWithRetries(_root);
    }

    private void SetGlobal(string value) => File.WriteAllText(_globalFile, $"[core]\n\thooksPath = {value.Replace('\\', '/')}\n");

    private Dictionary<string, string> GitGlobalEnvironment() => new() { ["GIT_CONFIG_GLOBAL"] = _globalFile };

    private void AssertMatchesGit(string workTreeRoot)
    {
        var result = GitHooks.ResolveHookPath(workTreeRoot, "post-commit");

        Assert.That(result.Outcome, Is.Not.EqualTo(HookPathOutcome.Declined), result.Detail);
        Assert.That(Canon(result.Path!), Is.EqualTo(GitSays(workTreeRoot, "post-commit", GitGlobalEnvironment())).IgnoreCase);
    }

    [Test]
    public void GlobalAbsoluteHooksPath_AppliesWhenNoLocalValue()
    {
        var repo = NewRepo(_root, "g-abs");
        var target = Path.Combine(_root, "global-hooks");
        SetGlobal(target);

        AssertMatchesGit(repo);
        Assert.That(Canon(GitHooks.ResolveHookPath(repo, "post-commit").HooksDirectory!), Is.EqualTo(Canon(target)).IgnoreCase);
    }

    [Test]
    public void GlobalRelativeHooksPath_IsRelativeToTheWorkTreeRoot()
    {
        var repo = NewRepo(_root, "g-rel");
        SetGlobal(".global-hooks");

        AssertMatchesGit(repo);
        Assert.That(Canon(GitHooks.ResolveHookPath(repo, "post-commit").HooksDirectory!), Is.EqualTo(Canon(Path.Combine(repo, ".global-hooks"))).IgnoreCase);
    }

    [Test]
    public void LocalHooksPath_OverridesGlobal()
    {
        var repo = NewRepo(_root, "g-local");
        SetGlobal(Path.Combine(_root, "global-hooks"));
        RunGit(repo, "config", "core.hooksPath", ".local-hooks");

        AssertMatchesGit(repo);
        Assert.That(Canon(GitHooks.ResolveHookPath(repo, "post-commit").HooksDirectory!), Is.EqualTo(Canon(Path.Combine(repo, ".local-hooks"))).IgnoreCase);
    }

    [Test]
    public void LinkedWorktree_GlobalApplies_AndLocalOfTheMainRepositoryOverridesIt()
    {
        var main = NewRepo(_root, "g-main");
        var linked = Path.Combine(_root, "g-linked");
        RunGit(main, "worktree", "add", "-q", "--detach", linked);
        var globalTarget = Path.Combine(_root, "global-hooks");
        SetGlobal(globalTarget);

        AssertMatchesGit(linked);
        Assert.That(Canon(GitHooks.ResolveHookPath(linked, "post-commit").HooksDirectory!), Is.EqualTo(Canon(globalTarget)).IgnoreCase, "only global is set");

        RunGit(main, "config", "core.hooksPath", ".local-hooks");

        AssertMatchesGit(linked);
        Assert.That(Canon(GitHooks.ResolveHookPath(linked, "post-commit").HooksDirectory!), Is.EqualTo(Canon(Path.Combine(linked, ".local-hooks"))).IgnoreCase, "local (main repo config) beats global");
    }

    [Test]
    public void NothingSetAnywhere_IsTheDefaultDirectory()
    {
        var repo = NewRepo(_root, "g-none");
        File.WriteAllText(_globalFile, "[user]\n\tname = nobody\n");

        AssertMatchesGit(repo);
        Assert.That(Canon(GitHooks.ResolveHookPath(repo, "post-commit").HooksDirectory!), Is.EqualTo(Canon(Path.Combine(repo, ".git", "hooks"))).IgnoreCase);
    }

    [Test]
    public void GlobalHookDirectory_MakesTheBlockingHookCheckLookThere()
    {
        var repo = NewRepo(_root, "g-blocking");
        var target = Path.Combine(_root, "global-hooks");
        SetGlobal(target);
        WriteHook(Path.Combine(repo, ".git", "hooks"), "pre-commit", "#!/bin/sh\nexit 0\n");

        Assert.That(GitHooks.HasBlockingHook(repo).Outcome, Is.EqualTo(BlockingHookOutcome.None), "the default directory is bypassed by the global setting");

        WriteHook(target, "commit-msg", "#!/bin/sh\nexit 0\n");

        Assert.That(GitHooks.HasBlockingHook(repo).HookName, Is.EqualTo("commit-msg"));
    }

    [Test]
    public void GlobalConfigDefinedHooks_AreSeenAsConfigHooks()
    {
        var repo = NewRepo(_root, "g-cfghooks");
        File.WriteAllText(_globalFile, "[hook \"shared\"]\n\tevent = pre-commit\n\tcommand = echo hi\n");

        Assert.That(GitHooks.HasBlockingHook(repo).Outcome, Is.EqualTo(BlockingHookOutcome.Unknown));
    }
}
