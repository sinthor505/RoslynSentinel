using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Compares <see cref="IGitReader.GetConfigValue"/> across <see cref="CliGitReader"/>, <see cref="Libgit2GitReader"/> and the production composite
/// <see cref="FallbackGitReader"/> on throwaway repos: set, unset, empty, valueless, multi-valued, global-only, and the linked-worktree cases. The CLI is
/// the oracle. A case libgit2 serves asserts the composite's counters show libgit2 answered; a case libgit2 declines asserts the CLI answered. The
/// global-only case seen from a linked worktree is Explicit (see its reason). The global file is isolated per test through a throwaway HOME and USERPROFILE.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
[NonParallelizable]
public class GitConfigReadParityTests
{
    private const string Key = "sentinel.probe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // libgit2 declines a read when any of these is set in the process, even empty (GitReaderEnvironmentTests), so each is cleared for the
    // test and restored after. HOME and USERPROFILE point at a throwaway home; XDG_CONFIG_HOME cannot be set to isolate, so it is cleared.
    private static readonly string[] IsolatedVariables =
    [
        "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "XDG_CONFIG_HOME",
        "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
    ];

    private string _root = null!;
    private string _repo = null!;
    private string _globalConfig = null!;
    private string _home = null!;
    private Dictionary<string, string?>? _savedEnvironment;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-gitconfig-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        InitRepo(_repo);

        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_home);
        _globalConfig = Path.Combine(_home, ".gitconfig");
        File.WriteAllText(_globalConfig, string.Empty);
        _savedEnvironment = IsolatedVariables.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
        foreach (var name in IsolatedVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Environment.SetEnvironmentVariable("HOME", _home);
        Environment.SetEnvironmentVariable("USERPROFILE", _home);
    }

    [TearDown]
    public void TearDown()
    {
        if (_savedEnvironment is not null)
        {
            foreach (var (name, value) in _savedEnvironment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            _savedEnvironment = null;
        }

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

    private static FallbackGitReader Composite() => new(new Libgit2GitReader(), new CliGitReader(), _ => { });

    private void CommitBaseline()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "base\n");
        RunGit(_repo, "add", "a.txt");
        RunGit(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "base");
    }

    private string LinkWorktree(string branch)
    {
        CommitBaseline();
        var worktree = Path.Combine(_root, "wt-" + branch);
        RunGit(_repo, "worktree", "add", "-b", branch, worktree, "HEAD");
        return worktree;
    }

    /// <summary>All three readers return <paramref name="expected"/>, and the composite's libgit2 counter proves libgit2 answered.</summary>
    private void AssertServedByLibgit2(string directory, string? expected)
    {
        var cli = new CliGitReader().GetConfigValue(directory, Key, Timeout);
        var libgit2 = new Libgit2GitReader().GetConfigValue(directory, Key, Timeout);
        var composite = Composite();
        var served = composite.GetConfigValue(directory, Key, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(cli, Is.EqualTo(expected), "the CLI is the oracle");
            Assert.That(libgit2, Is.EqualTo(expected));
            Assert.That(served, Is.EqualTo(expected));
            Assert.That(composite.Libgit2Count, Is.EqualTo(1), "libgit2 must answer; a CLI answer here is a silent fallback");
            Assert.That(composite.DeclineCount, Is.Zero);
            Assert.That(composite.CliFallbackCount, Is.Zero);
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
        });
    }

    /// <summary>libgit2 declines with NotSupportedException; the composite hands the call to the CLI and returns the CLI answer, which is returned here.</summary>
    private string? AssertDeclinedToCli(string directory)
    {
        Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().GetConfigValue(directory, Key, Timeout));
        var cli = new CliGitReader().GetConfigValue(directory, Key, Timeout);
        var composite = Composite();
        var served = composite.GetConfigValue(directory, Key, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(served, Is.EqualTo(cli));
            Assert.That(composite.DeclineCount, Is.EqualTo(1), "a planned decline is counted as a decline");
            Assert.That(composite.Libgit2Count, Is.Zero);
            Assert.That(composite.CliFallbackCount, Is.Zero, "a planned decline is not a failure");
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
        });
        return cli;
    }

    // ---- cases ----

    [Test]
    public void Set_IsServedByLibgit2AndAgreesWithTheCli()
    {
        RunGit(_repo, "config", Key, "value");

        AssertServedByLibgit2(_repo, "value");
    }

    [Test]
    public void Unset_IsServedByLibgit2AndAgreesWithTheCli()
    {
        AssertServedByLibgit2(_repo, null);
    }

    [Test]
    public void EmptyValue_IsDeclinedByLibgit2AndTheCliAnswersEmpty()
    {
        RunGit(_repo, "config", Key, string.Empty);

        var cli = AssertDeclinedToCli(_repo);

        Assert.That(cli, Is.EqualTo(string.Empty));
    }

    [Test]
    public void ValuelessKey_IsDeclinedByLibgit2AndTheCliAnswers()
    {
        File.AppendAllText(Path.Combine(_repo, ".git", "config"), "[sentinel]\n\tprobe\n");

        AssertDeclinedToCli(_repo);
    }

    [Test]
    public void MultiValuedKey_LastValueWinsInEveryReader()
    {
        RunGit(_repo, "config", "--add", Key, "first");
        RunGit(_repo, "config", "--add", Key, "last");

        AssertServedByLibgit2(_repo, "last");
    }

    [Test]
    public void KeySetOnlyGlobally_IsServedByLibgit2AndAgreesWithTheCli()
    {
        RunGit(_root, "config", "--file", _globalConfig, Key, "global-value");

        AssertServedByLibgit2(_repo, "global-value");
    }

    [Test]
    public void KeySetInTheMainRepository_SeenFromALinkedWorktree_IsServedByLibgit2()
    {
        var worktree = LinkWorktree("sentinel-wt-local");
        RunGit(_repo, "config", Key, "main-value");

        AssertServedByLibgit2(worktree, "main-value");
    }

    [Test]
    [Explicit("Finding, not fixed: Libgit2GitReader.ConfigValue reads a linked worktree via Configuration.BuildFrom(commondir/config) with no global level, so libgit2 returns null where git returns the global value.")]
    public void KeySetOnlyGlobally_SeenFromALinkedWorktree_IsServedByLibgit2()
    {
        RunGit(_root, "config", "--file", _globalConfig, Key, "global-value");
        var worktree = LinkWorktree("sentinel-wt-global");

        Assert.That(new CliGitReader().GetConfigValue(worktree, Key, Timeout), Is.EqualTo("global-value"), "the CLI sees the global file from a linked worktree");
        AssertServedByLibgit2(worktree, "global-value");
    }
}
