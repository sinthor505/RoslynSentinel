using System.Reflection;
using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;
using static RoslynSentinel.Tests.Tools.Basic.CasCommitTestSupport;
using static RoslynSentinel.Tests.Tools.Basic.GitHookTestSupport;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="GitHooks.ResolveHookPath"/> and <see cref="GitHooks.HasBlockingHook"/> against <c>git rev-parse --path-format=absolute --git-path hooks/&lt;name&gt;</c> (run in the work tree root) as ground truth, in real temporary
/// repositories: default hooks directory, relative, absolute and <c>~</c> core.hooksPath, a linked worktree, a subdirectory; plus the declines (environment, worktree config, bare, no repository,
/// config-defined hooks) and the pure <see cref="GitHooks.ResolveHooksDirectory"/> decisions with an injected home directory. The global config level is in GitHooksGlobalConfigTests.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// No process-wide state is touched: the environment the resolver sees is injected through GitHookOptions.AmbientEnvironment.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHooksResolutionTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = NewRoot("rs-hookres-");
    }

    [TearDown]
    public void TearDown() => DeleteTreeWithRetries(_root);

    private void AssertMatchesGit(string workTreeRoot, string hookName = "post-commit", string? workingDirectory = null)
    {
        var result = GitHooks.ResolveHookPath(workingDirectory ?? workTreeRoot, hookName);

        Assert.That(result.Outcome, Is.Not.EqualTo(HookPathOutcome.Declined), result.Detail);
        Assert.That(Canon(result.Path!), Is.EqualTo(GitSays(workTreeRoot, hookName)).IgnoreCase);
        Assert.That(Canon(result.HooksDirectory!), Is.EqualTo(Canon(Path.GetDirectoryName(result.Path!)!)).IgnoreCase);
        Assert.That(Canon(result.WorkTreeRoot!), Is.EqualTo(Canon(workTreeRoot)).IgnoreCase);
    }

    private string LinkedWorktree(string main, string name)
    {
        var path = Path.Combine(_root, name);
        RunGit(main, "worktree", "add", "-q", "--detach", path);
        return path;
    }

    [Test]
    public void DefaultHooksDirectory_IsUnderTheGitDirectory()
    {
        var repo = NewRepo(_root, "plain");

        AssertMatchesGit(repo);
        Assert.That(Canon(GitHooks.ResolveHookPath(repo, "post-commit").HooksDirectory!), Is.EqualTo(Canon(Path.Combine(repo, ".git", "hooks"))).IgnoreCase);
    }

    [TestCase(".githooks")]
    [TestCase("nested/dir/hooks")]
    [TestCase("../outside-hooks")]
    [TestCase("./dot-hooks")]
    public void RelativeHooksPath_IsResolvedAgainstTheWorkTreeRoot(string configured)
    {
        var repo = NewRepo(_root, "rel");
        RunGit(repo, "config", "core.hooksPath", configured);

        AssertMatchesGit(repo);
    }

    [Test]
    public void RelativeHooksPath_FromASubdirectory_StillResolvesAgainstTheWorkTreeRoot()
    {
        var repo = NewRepo(_root, "sub");
        RunGit(repo, "config", "core.hooksPath", ".githooks");
        var sub = Path.Combine(repo, "deep", "er");
        Directory.CreateDirectory(sub);

        AssertMatchesGit(repo, workingDirectory: sub);
    }

    [Test]
    public void AbsoluteHooksPath_IsUsedAsIs([Values] bool backslashes)
    {
        var repo = NewRepo(_root, "abs");
        var target = Path.Combine(_root, "elsewhere", "hooks");
        RunGit(repo, "config", "core.hooksPath", backslashes ? target : ForSh(target));

        AssertMatchesGit(repo);
        Assert.That(Canon(GitHooks.ResolveHookPath(repo, "post-commit").HooksDirectory!), Is.EqualTo(Canon(target)).IgnoreCase);
    }

    [Test]
    public void TildeHooksPath_ExpandsToTheHomeDirectoryGitUses()
    {
        var repo = NewRepo(_root, "tilde");
        RunGit(repo, "config", "core.hooksPath", "~/rs-hook-test-" + Guid.NewGuid().ToString("N")[..8]);

        AssertMatchesGit(repo);
    }

    [Test]
    public void LinkedWorktree_DefaultHooksLiveInTheCommonDirectory()
    {
        var main = NewRepo(_root, "main");
        var linked = LinkedWorktree(main, "linked-default");

        AssertMatchesGit(linked);
        Assert.That(Canon(GitHooks.ResolveHookPath(linked, "post-commit").HooksDirectory!), Is.EqualTo(Canon(Path.Combine(main, ".git", "hooks"))).IgnoreCase);
    }

    [Test]
    public void LinkedWorktree_RelativeHooksPath_UsesTheLinkedWorktreesOwnRoot()
    {
        var main = NewRepo(_root, "main-rel");
        RunGit(main, "config", "core.hooksPath", ".githooks");
        var linked = LinkedWorktree(main, "linked-rel");

        AssertMatchesGit(linked);
        Assert.That(Canon(GitHooks.ResolveHookPath(linked, "post-commit").HooksDirectory!), Is.EqualTo(Canon(Path.Combine(linked, ".githooks"))).IgnoreCase);
        AssertMatchesGit(main);
    }

    [Test]
    public void LinkedWorktree_AbsoluteHooksPath_IsUsedAsIs()
    {
        var main = NewRepo(_root, "main-abs");
        RunGit(main, "config", "core.hooksPath", ForSh(Path.Combine(_root, "shared-hooks")));
        var linked = LinkedWorktree(main, "linked-abs");

        AssertMatchesGit(linked);
    }

    [Test]
    public void HookFile_Exists_IsFound_AndMissing_IsNotFound()
    {
        var repo = NewRepo(_root, "exists");
        RunGit(repo, "config", "core.hooksPath", ".githooks");

        Assert.That(GitHooks.ResolveHookPath(repo, "post-commit").Outcome, Is.EqualTo(HookPathOutcome.NotFound));

        WriteHook(Path.Combine(repo, ".githooks"), "post-commit", "#!/bin/sh\nexit 0\n");
        var found = GitHooks.ResolveHookPath(repo, "post-commit");

        Assert.That(found.Outcome, Is.EqualTo(HookPathOutcome.Found));
        Assert.That(found.Found, Is.True);
        Assert.That(GitHooks.ResolveHookPath(repo, "post-merge").Outcome, Is.EqualTo(HookPathOutcome.NotFound), "another name in the same directory");
    }

    [TestCase("")]
    [TestCase("a/b")]
    [TestCase("..")]
    [TestCase("post commit")]
    public void ImplausibleHookName_IsDeclined(string name)
    {
        var repo = NewRepo(_root, "name");

        Assert.That(GitHooks.ResolveHookPath(repo, name).Outcome, Is.EqualTo(HookPathOutcome.Declined));
    }

    [Test]
    public void WorktreeConfigWithConfigWorktreeFile_IsDeclined_WhereGitWouldHonourIt()
    {
        var repo = NewRepo(_root, "wtcfg");
        RunGit(repo, "config", "extensions.worktreeConfig", "true");
        RunGit(repo, "config", "--worktree", "core.hooksPath", "private-hooks");

        var result = GitHooks.ResolveHookPath(repo, "post-commit");

        Assert.That(GitSays(repo, "post-commit"), Does.Contain("private-hooks"), "ground truth: git does read config.worktree here.");
        Assert.That(result.Outcome, Is.EqualTo(HookPathOutcome.Declined));
        Assert.That(GitHooks.HasBlockingHook(repo).Outcome, Is.EqualTo(BlockingHookOutcome.Unknown));
    }

    [Test]
    public void WorktreeConfigExtensionWithoutConfigWorktreeFile_IsNotDeclined()
    {
        var repo = NewRepo(_root, "wtcfg-nofile");
        RunGit(repo, "config", "extensions.worktreeConfig", "true");

        AssertMatchesGit(repo);
    }

    [Test]
    public void BareRepository_NoRepository_AreDeclined()
    {
        var bare = Path.Combine(_root, "bare.git");
        Directory.CreateDirectory(bare);
        RunGit(bare, "init", "--bare", "--quiet");
        var nothing = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(nothing);

        Assert.That(GitHooks.ResolveHookPath(bare, "post-commit").Outcome, Is.EqualTo(HookPathOutcome.Declined));
        Assert.That(GitHooks.ResolveHookPath(Path.Combine(_root, "missing-dir"), "post-commit").Outcome, Is.EqualTo(HookPathOutcome.Declined));
        Assert.That(GitHooks.HasBlockingHook(bare).Outcome, Is.EqualTo(BlockingHookOutcome.Unknown));
    }

    [Test]
    public void EveryOverridingEnvironmentVariableOfTheReader_DeclinesTheResolution()
    {
        var repo = NewRepo(_root, "env");
        var field = typeof(Libgit2GitReader).GetField("OverridingEnvironmentVariables", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null, "Libgit2GitReader.OverridingEnvironmentVariables was renamed or removed.");
        var names = (string[])field!.GetValue(null)!;

        Assert.That(GitHooks.ResolveHookPath(repo, "post-commit", new GitHookOptions { AmbientEnvironment = AmbientWith() }).Outcome, Is.Not.EqualTo(HookPathOutcome.Declined), "control without a variable");
        foreach (var name in names)
        {
            var result = GitHooks.ResolveHookPath(repo, "post-commit", new GitHookOptions { AmbientEnvironment = AmbientWith((name, "1")) });
            Assert.That(result.Outcome, Is.EqualTo(HookPathOutcome.Declined), name);
            Assert.That(result.Detail, Does.Contain(name));
            Assert.That(GitHooks.HasBlockingHook(repo, new GitHookOptions { AmbientEnvironment = AmbientWith((name, "1")) }).Outcome, Is.EqualTo(BlockingHookOutcome.Unknown), name);
        }
    }

    [Test]
    public void OtherGitVariables_DoNotDecline()
    {
        var repo = NewRepo(_root, "env-other");
        var options = new GitHookOptions { AmbientEnvironment = AmbientWith(("GIT_AUTHOR_NAME", "x"), ("GIT_TERMINAL_PROMPT", "0")) };

        Assert.That(GitHooks.ResolveHookPath(repo, "post-commit", options).Outcome, Is.Not.EqualTo(HookPathOutcome.Declined));
    }

    [Test]
    public void ConfigDefinedHooks_AreReported_AndMakeTheBlockingQuestionUnknown()
    {
        var repo = NewRepo(_root, "cfghooks");
        Assert.That(GitHooks.HasBlockingHook(repo).Outcome, Is.EqualTo(BlockingHookOutcome.None), "control");
        RunGit(repo, "config", "hook.mine.event", "pre-commit");
        RunGit(repo, "config", "hook.mine.command", "echo hi");

        Assert.That(GitHooks.ResolveHookPath(repo, "post-commit").ConfigHooksPresent, Is.True);
        Assert.That(GitHooks.HasBlockingHook(repo).Outcome, Is.EqualTo(BlockingHookOutcome.Unknown));
    }

    [TestCase("pre-commit")]
    [TestCase("prepare-commit-msg")]
    [TestCase("commit-msg")]
    public void BlockingHook_PresentAndAbsent(string name)
    {
        var repo = NewRepo(_root, "blocking-" + name);

        Assert.That(GitHooks.HasBlockingHook(repo), Is.EqualTo(new BlockingHookResult(BlockingHookOutcome.None)));

        var file = WriteHook(Path.Combine(repo, ".git", "hooks"), name, "#!/bin/sh\nexit 0\n");
        var result = GitHooks.HasBlockingHook(repo);

        Assert.That(result.Outcome, Is.EqualTo(BlockingHookOutcome.Found));
        Assert.That(result.HookName, Is.EqualTo(name));
        Assert.That(Canon(result.HookPath!), Is.EqualTo(Canon(file)).IgnoreCase);
        Assert.That(result.IsCertainlyAbsent, Is.False);
    }

    [Test]
    public void NonBlockingHooks_DoNotCount()
    {
        var repo = NewRepo(_root, "nonblocking");
        foreach (var name in new[] { "post-commit", "post-merge", "pre-push", "pre-merge-commit", "applypatch-msg", "pre-commit.sample" })
        {
            WriteHook(Path.Combine(repo, ".git", "hooks"), name, "#!/bin/sh\nexit 0\n");
        }

        Assert.That(GitHooks.HasBlockingHook(repo).Outcome, Is.EqualTo(BlockingHookOutcome.None));
    }

    [Test]
    public void BlockingHook_RelativeHooksPath_LooksInTheConfiguredDirectoryOnly()
    {
        var repo = NewRepo(_root, "blocking-rel");
        WriteHook(Path.Combine(repo, ".git", "hooks"), "pre-commit", "#!/bin/sh\nexit 0\n");
        RunGit(repo, "config", "core.hooksPath", ".githooks");

        Assert.That(GitHooks.HasBlockingHook(repo).Outcome, Is.EqualTo(BlockingHookOutcome.None), "the default directory is not consulted once core.hooksPath is set");

        WriteHook(Path.Combine(repo, ".githooks"), "commit-msg", "#!/bin/sh\nexit 0\n");
        var result = GitHooks.HasBlockingHook(repo);

        Assert.That(result.Outcome, Is.EqualTo(BlockingHookOutcome.Found));
        Assert.That(result.HookName, Is.EqualTo("commit-msg"));
    }

    [Test]
    public void BlockingHook_LinkedWorktree_SeesTheCommonDirectorysHooks_AndItsOwnRelativeDirectory()
    {
        var main = NewRepo(_root, "blocking-main");
        var linked = LinkedWorktree(main, "blocking-linked");
        Assert.That(GitHooks.HasBlockingHook(linked).Outcome, Is.EqualTo(BlockingHookOutcome.None));

        WriteHook(Path.Combine(main, ".git", "hooks"), "pre-commit", "#!/bin/sh\nexit 0\n");
        Assert.That(GitHooks.HasBlockingHook(linked).HookName, Is.EqualTo("pre-commit"));

        RunGit(main, "config", "core.hooksPath", ".githooks");
        Assert.That(GitHooks.HasBlockingHook(linked).Outcome, Is.EqualTo(BlockingHookOutcome.None), "relative to the linked root: nothing there");
        WriteHook(Path.Combine(linked, ".githooks"), "prepare-commit-msg", "#!/bin/sh\nexit 0\n");
        Assert.That(GitHooks.HasBlockingHook(linked).HookName, Is.EqualTo("prepare-commit-msg"));
    }

    [Test]
    public void UnreadableRepositoryFacts_AreUnknownAndDeclined_NeverAnException()
    {
        var repo = NewRepo(_root, "unreadable");
        WriteHook(Path.Combine(repo, ".git", "hooks"), "pre-commit", "#!/bin/sh\nexit 0\n");

        foreach (Func<string, HookRepositoryLayout?> reader in new Func<string, HookRepositoryLayout?>[]
        {
            _ => throw new NotSupportedException("decline"),
            _ => throw new IOException("locked"),
            _ => throw new LibGit2Sharp.LibGit2SharpException("native"),
            _ => throw new DllNotFoundException("git2"),
            _ => null,
        })
        {
            var options = new GitHookOptions { ReadLayout = reader };

            Assert.That(GitHooks.HasBlockingHook(repo, options).Outcome, Is.EqualTo(BlockingHookOutcome.Unknown), "must not claim 'none' (a pre-commit hook exists here)");
            Assert.That(GitHooks.ResolveHookPath(repo, "post-commit", options).Outcome, Is.EqualTo(HookPathOutcome.Declined));
        }
    }

    [Test]
    public void ResolveHooksDirectory_Decisions_WithAnInjectedHomeDirectory()
    {
        const string workTree = @"C:\work\tree";
        const string common = @"C:\work\tree\.git";
        const string home = @"C:\Users\someone";

        string Resolve(string? configured, string? homeDirectory = home) =>
            GitHooks.ResolveHooksDirectory(new HookRepositoryLayout(workTree, common, configured, false), homeDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(Resolve(null), Is.EqualTo(@"C:\work\tree\.git\hooks"));
            Assert.That(Resolve("~"), Is.EqualTo(home));
            Assert.That(Resolve("~/"), Is.EqualTo(home));
            Assert.That(Resolve("~/hooks"), Is.EqualTo(@"C:\Users\someone\hooks"));
            Assert.That(Resolve(@"~\hooks\x"), Is.EqualTo(@"C:\Users\someone\hooks\x"));
            Assert.That(Resolve("~//hooks"), Is.EqualTo(@"C:\Users\someone\hooks"));
            Assert.That(Resolve("rel/hooks"), Is.EqualTo(@"C:\work\tree\rel\hooks"));
            Assert.That(Resolve("../up"), Is.EqualTo(@"C:\work\up"));
            Assert.That(Resolve("D:/abs/hooks"), Is.EqualTo(@"D:\abs\hooks"));
            Assert.That(Resolve(@"\\server\share\hooks"), Is.EqualTo(@"\\server\share\hooks"));
        });

        foreach (var (configured, homeDirectory) in new (string?, string?)[]
        {
            ("", home), ("~user/hooks", home), ("~", null), ("~/x", "relative-home"), ("%(prefix)/hooks", home), ("/rooted/no/drive", home), ("C:driverelative", home),
        })
        {
            Assert.That(() => Resolve(configured, homeDirectory), Throws.TypeOf<NotSupportedException>(), $"'{configured}' with home '{homeDirectory}'");
        }

        Assert.That(
            () => GitHooks.ResolveHooksDirectory(new HookRepositoryLayout(null, common, "relative", false), home), Throws.TypeOf<NotSupportedException>(), "a relative path needs a work tree");
    }
}
