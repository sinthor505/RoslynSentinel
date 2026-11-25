using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Tools.Basic.GitHookTestSupport;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="GitShellLocator.Locate"/>: the real git installation (no hard-coded path), and fake installation layouts under a temp directory for the probe order, the
/// <c>git --exec-path</c> fallback, the not-found result and the memo's re-check. The fakes are empty files: only existence is probed.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // may start git (--exec-path) and resolves the installation; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitShellLocatorTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = NewRoot("rs-shloc-");

    [TearDown]
    public void TearDown() => DeleteTreeWithRetries(_root);

    private string Touch(params string[] relativeParts)
    {
        var path = Path.Combine([_root, .. relativeParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static string Other(string path) => Canon(path);

    [Test]
    public void RealGitInstallation_HasAShExe_FoundWithoutAHardCodedPath()
    {
        var gitExe = GitProcess.ResolveGitExecutablePath();
        if (!File.Exists(gitExe))
        {
            Assert.Ignore("git.exe is not resolvable from PATH on this machine.");
        }

        var result = GitShellLocator.Locate();

        Assert.That(result.Found, Is.True, result.Detail);
        Assert.That(File.Exists(result.Path), Is.True);
        Assert.That(Path.GetFileName(result.Path), Is.EqualTo("sh.exe").IgnoreCase);
        Assert.That(GitShellLocator.Locate().Path, Is.EqualTo(result.Path), "the memoized answer is the same");
    }

    [Test]
    public void LauncherLayout_CmdGitExe_FindsUsrBinSh()
    {
        var git = Touch("g1", "cmd", "git.exe");
        var sh = Touch("g1", "usr", "bin", "sh.exe");

        Assert.That(Other(GitShellLocator.Locate(git, () => null).Path!), Is.EqualTo(Other(sh)).IgnoreCase);
    }

    [Test]
    public void Mingw64Layout_FindsTheShTwoLevelsUp()
    {
        var git = Touch("g2", "mingw64", "bin", "git.exe");
        var sh = Touch("g2", "usr", "bin", "sh.exe");

        Assert.That(Other(GitShellLocator.Locate(git, () => null).Path!), Is.EqualTo(Other(sh)).IgnoreCase);
    }

    [Test]
    public void UsrBinIsPreferredOverBin_AndBinIsTheFallback()
    {
        // Separate installations: the memo keeps the first answer per git executable, so one git path cannot show both outcomes.
        var onlyBinGit = Touch("g3a", "cmd", "git.exe");
        var bin = Touch("g3a", "bin", "sh.exe");
        Assert.That(Other(GitShellLocator.Locate(onlyBinGit, () => null).Path!), Is.EqualTo(Other(bin)).IgnoreCase, "only bin\\sh.exe exists");

        var bothGit = Touch("g3b", "cmd", "git.exe");
        Touch("g3b", "bin", "sh.exe");
        var usr = Touch("g3b", "usr", "bin", "sh.exe");
        Assert.That(Other(GitShellLocator.Locate(bothGit, () => null).Path!), Is.EqualTo(Other(usr)).IgnoreCase, "usr\\bin\\sh.exe beats bin\\sh.exe");
    }

    [Test]
    public void ShBesideGitExe_IsTheLastDirectProbe()
    {
        var git = Touch("g4", "tools", "git.exe");
        var sh = Touch("g4", "tools", "sh.exe");

        Assert.That(Other(GitShellLocator.Locate(git, () => null).Path!), Is.EqualTo(Other(sh)).IgnoreCase);
    }

    [Test]
    public void ExecPathFallback_WalksTheAncestorsOfGitCore_WhenGitExeIsAShim()
    {
        var shim = Touch("shims", "git.exe");
        var gitCore = Path.Combine(_root, "install", "mingw64", "libexec", "git-core");
        Directory.CreateDirectory(gitCore);
        var sh = Touch("install", "usr", "bin", "sh.exe");

        var result = GitShellLocator.Locate(shim, () => gitCore.Replace('\\', '/') + "\n");

        Assert.That(Other(result.Path!), Is.EqualTo(Other(sh)).IgnoreCase);
    }

    [Test]
    public void ExecPathFallback_AlsoRunsWhenGitExeIsNotAFile()
    {
        var gitCore = Path.Combine(_root, "install2", "mingw64", "libexec", "git-core");
        Directory.CreateDirectory(gitCore);
        var sh = Touch("install2", "bin", "sh.exe");

        var result = GitShellLocator.Locate(Path.Combine(_root, "nowhere", "git.exe"), () => gitCore);

        Assert.That(Other(result.Path!), Is.EqualTo(Other(sh)).IgnoreCase);
    }

    [Test]
    public void ExecPath_IsNotAsked_WhenADirectProbeSucceeds()
    {
        var git = Touch("g5", "cmd", "git.exe");
        Touch("g5", "usr", "bin", "sh.exe");
        var asked = 0;

        GitShellLocator.Locate(git, () =>
        {
            asked++;
            return null;
        });

        Assert.That(asked, Is.Zero);
    }

    [Test]
    public void NothingFound_IsATypedResultNamingTheProbes()
    {
        var git = Touch("g6", "cmd", "git.exe");
        var emptyCore = Path.Combine(_root, "g6", "mingw64", "libexec", "git-core");
        Directory.CreateDirectory(emptyCore);

        foreach (var probe in new Func<string?>[] { () => null, () => "", () => emptyCore })
        {
            var result = GitShellLocator.Locate(git, probe);

            Assert.That(result.Found, Is.False);
            Assert.That(result.Path, Is.Null);
            Assert.That(result.Detail, Does.Contain("not found").And.Contain("sh.exe"));
        }

        Assert.That(GitShellLocator.Locate(Path.Combine(_root, "nowhere", "git.exe"), () => null).Found, Is.False, "git.exe itself missing");
    }

    [Test]
    public void MemoizedPath_IsRecheckedOnEveryCall()
    {
        var git = Touch("g7", "cmd", "git.exe");
        var first = Touch("g7", "usr", "bin", "sh.exe");
        Assert.That(Other(GitShellLocator.Locate(git, () => null).Path!), Is.EqualTo(Other(first)).IgnoreCase);

        File.Delete(first);
        Assert.That(GitShellLocator.Locate(git, () => null).Found, Is.False, "the removed sh.exe must not be served from the memo");

        var second = Touch("g7", "bin", "sh.exe");
        Assert.That(Other(GitShellLocator.Locate(git, () => null).Path!), Is.EqualTo(Other(second)).IgnoreCase);
    }
}
