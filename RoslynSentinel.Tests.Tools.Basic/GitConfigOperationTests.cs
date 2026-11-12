using System.Reflection;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <c>GitImpl.ConfigAsync</c>: the get path (reads, deny-listed keys, userinfo values, malformed keys) and the set and
/// unset paths (allowlist, linked worktrees, no-op rules). Every write is checked against the real config file. Global
/// keys live in a throwaway HOME, isolated the way <c>GitConfigReadParityTests</c> isolates it.
/// </summary>
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
[NonParallelizable] // sets HOME and USERPROFILE for every test
public class GitConfigOperationTests
{
    private const string Probe = "roslynsentinel.probe";

    // libgit2 declines a read when any of these is set in the process, even empty, so each is cleared for the test and restored after.
    private static readonly string[] IsolatedVariables =
    [
        "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "XDG_CONFIG_HOME",
        "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
    ];

    private string _root = null!;
    private string _repo = null!;
    private string _home = null!;
    private string _globalConfig = null!;
    private Dictionary<string, string?>? _savedEnvironment;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        // Short directory names: a linked-worktree git dir under a long name can pass MAX_PATH.
        _root = Path.Combine(Path.GetTempPath(), "rs-cfg-" + Guid.NewGuid().ToString("N")[..12]);
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
        // libgit2 fixes its global search path process-wide; HOME alone does not move it.
        GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Global, _home);

        _repo = NewRepo("repo");
    }

    [TearDown]
    public void TearDown()
    {
        // First: a failed assertion must never leave the throwaway home as libgit2's global path. Null restores the defaults.
        GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Global, null!);

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

    private static GitImpl NewImpl(IGitReader? reader = null) =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, reader ?? Composite());

    private string NewRepo(string name)
    {
        var directory = Path.Combine(_root, name);
        InitRepo(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "a\n");
        RunGit(directory, "add", "a.txt");
        RunGit(directory, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "base");
        return directory;
    }

    private static string LocalConfig(string directory) => Path.Combine(directory, ".git", "config");

    private static IEnumerable<string> WorktreeConfigFiles(string main)
    {
        var worktrees = Path.Combine(main, ".git", "worktrees");
        return Directory.Exists(worktrees)
            ? Directory.EnumerateFiles(worktrees, "config", SearchOption.AllDirectories)
            : Array.Empty<string>();
    }

    private void SetGlobal(string key, string value) => RunGit(_home, "config", "--file", _globalConfig, key, value);

    /// <summary>An IGitReader whose every call fails the test and is counted, so a key that reaches the config read is visible.</summary>
    internal class ThrowingConfigReaderProxy : DispatchProxy
    {
        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            throw new InvalidOperationException("the test reader refuses " + targetMethod?.Name);
        }
    }

    // ---- get ----

    [Test]
    public async Task Get_KeySetLocally_ReturnsTheValueAsStoredWithNoNoteAsync()
    {
        RunGit(_repo, "config", Probe, "two words");

        var result = await NewImpl().ConfigAsync(_repo, "get", Probe, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Value, Is.EqualTo("two words"));
            Assert.That(result.Note, Is.Null);
            Assert.That(result.Changed, Is.False);
        });
    }

    [Test]
    public async Task Get_KeyNotSet_ReturnsSuccessWithNullValueAndANoteAsync()
    {
        var result = await NewImpl().ConfigAsync(_repo, "get", "roslynsentinel.absent", null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Note, Does.Contain("is not set"));
        });
    }

    [Test]
    public async Task Get_KeySetOnlyGlobally_IsReturnedAsync()
    {
        SetGlobal(Probe, "global-value");

        var result = await NewImpl().ConfigAsync(_repo, "get", Probe, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Value, Is.EqualTo("global-value"));
        });
    }

    [Test]
    public async Task Get_KeySetInMainRepository_ReadFromLinkedWorktree_IsReturnedAsync()
    {
        RunGit(_repo, "config", Probe, "main-value");
        var worktree = AddLinkedWorktree(_repo, _root, "get-wt", "get-branch");

        var result = await NewImpl().ConfigAsync(worktree, "get", Probe, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Value, Is.EqualTo("main-value"));
        });
    }

    [TestCase("credential.helper")]
    [TestCase("user.password")]
    [TestCase("foo.token")]
    [TestCase("http.extraheader")]
    [TestCase("http.https://x.example/.extraheader")]
    public async Task Get_DeniedKey_IsRefusedBeforeAnyReadAsync(string key)
    {
        var proxy = DispatchProxy.Create<IGitReader, ThrowingConfigReaderProxy>();

        var result = await NewImpl(proxy).ConfigAsync(_repo, "get", key, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorKind, Is.EqualTo(GitErrorCodes.Refused));
            Assert.That(result.Value, Is.Null);
            Assert.That(((ThrowingConfigReaderProxy)proxy).Calls, Is.Zero, "a refused key must not reach the config read");
        });
    }

    [TestCase("credential.helper")]
    [TestCase("user.password")]
    [TestCase("foo.token")]
    [TestCase("http.extraheader")]
    [TestCase("http.https://x.example/.extraheader")]
    public async Task Get_DeniedKey_SecretValueNeverAppearsInTheResultAsync(string key)
    {
        const string secret = "s3cret-value-xyz";
        RunGit(_repo, "config", key, secret);

        var result = await NewImpl().ConfigAsync(_repo, "get", key, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorKind, Is.EqualTo(GitErrorCodes.Refused));
            Assert.That(result.Error, Does.Not.Contain(secret));
            Assert.That(result.ErrorDetail, Does.Not.Contain(secret));
            Assert.That(result.Value, Is.Null);
        });
    }

    [Test]
    public async Task Get_UrlInsteadOfWithUserinfo_IsRefused_AndTheValueIsNotReturnedAsync()
    {
        RunGit(_repo, "config", "url.https://example.com/.insteadOf", "https://token@example.com/");

        var result = await NewImpl().ConfigAsync(_repo, "get", "url.https://example.com/.insteadOf", null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorKind, Is.EqualTo(GitErrorCodes.Refused));
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Error, Does.Not.Contain("token"));
            Assert.That(result.ErrorDetail, Does.Not.Contain("token"));
        });
    }

    [TestCase("nodot")]
    [TestCase("-x.y")]
    [TestCase("a b.c")]
    [TestCase("")]
    public async Task Get_MalformedKey_ReturnsInvalidNameAsync(string key)
    {
        var result = await NewImpl().ConfigAsync(_repo, "get", key, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorKind, Is.EqualTo(GitErrorCodes.InvalidName));
        });
    }

    // ---- set ----

    [Test]
    public async Task Set_FromLinkedWorktree_WritesTheMainRepositoryConfigAsync()
    {
        const string email = "linked@example.com";
        var worktree = AddLinkedWorktree(_repo, _root, "set-wt", "set-branch");

        var result = await NewImpl().ConfigAsync(worktree, "set", "user.email", email, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Value, Is.EqualTo(email));
            Assert.That(result.Changed, Is.True);
            Assert.That(File.ReadAllText(LocalConfig(_repo)), Does.Contain(email), "the main repository's .git/config holds the value");
        });
        foreach (var file in WorktreeConfigFiles(_repo))
        {
            Assert.That(File.ReadAllText(file), Does.Not.Contain(email), "the worktree's own git dir holds no entry: " + file);
        }
    }

    [Test]
    public async Task Set_CalledTwice_ReportsTheFirstValueAsPreviousOnTheSecondAsync()
    {
        var impl = NewImpl();

        var first = await impl.ConfigAsync(_repo, "set", Probe, "first-value", CancellationToken.None);
        Assert.That(first.IsError, Is.False, first.Error);
        Assert.That(File.ReadAllText(LocalConfig(_repo)), Does.Contain("probe = first-value"));

        var second = await impl.ConfigAsync(_repo, "set", Probe, "second-value", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.IsError, Is.False, second.Error);
            Assert.That(second.PreviousValue, Is.EqualTo("first-value"));
            Assert.That(second.Changed, Is.True);
            Assert.That(second.Value, Is.EqualTo("second-value"));
        });
        var text = File.ReadAllText(LocalConfig(_repo));
        Assert.That(text, Does.Contain("probe = second-value"));
        Assert.That(text, Does.Not.Contain("first-value"));
    }

    [TestCase("roslynsentinel.testkey")]
    [TestCase("user.name")]
    [TestCase("user.email")]
    [TestCase("core.autocrlf")]
    [TestCase("core.eol")]
    [TestCase("core.safecrlf")]
    [TestCase("core.longpaths")]
    [TestCase("pull.rebase")]
    [TestCase("push.default")]
    [TestCase("init.defaultBranch")]
    [TestCase("branch.main.remote")]
    [TestCase("branch.main.merge")]
    [TestCase("branch.main.rebase")]
    public async Task Set_AllowlistedKey_IsAcceptedAndWrittenAsync(string key)
    {
        const string value = "sentinel-value";

        var result = await NewImpl().ConfigAsync(_repo, "set", key, value, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Value, Is.EqualTo(value));
            Assert.That(result.Changed, Is.True);
            Assert.That(File.ReadAllText(LocalConfig(_repo)), Does.Contain(value), "the value is in .git/config");
        });
    }

    [TestCase("core.hooksPath")]
    [TestCase("alias.x")]
    [TestCase("core.fsmonitor")]
    [TestCase("branch.main.pushremote")]
    [TestCase("user.signingkey")]
    [TestCase("core.foo.autocrlf")]
    [TestCase("credential.helper")]
    public async Task Set_NearMissKey_IsRefused_AndTheConfigBytesAreUnchangedAsync(string key)
    {
        var before = File.ReadAllBytes(LocalConfig(_repo));

        var result = await NewImpl().ConfigAsync(_repo, "set", key, "sentinel-value", CancellationToken.None);

        var after = File.ReadAllBytes(LocalConfig(_repo));
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorKind, Is.EqualTo(GitErrorCodes.Refused));
            Assert.That(result.Value, Is.Null);
            Assert.That(after, Is.EqualTo(before), ".git/config bytes are unchanged");
        });
    }

    [Test]
    public async Task Set_WithoutValue_ReturnsActionRequired_AndTheConfigBytesAreUnchangedAsync()
    {
        var before = File.ReadAllBytes(LocalConfig(_repo));

        var result = await NewImpl().ConfigAsync(_repo, "set", "user.email", null, CancellationToken.None);

        var after = File.ReadAllBytes(LocalConfig(_repo));
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorKind, Is.EqualTo(GitErrorCodes.ActionRequired));
            Assert.That(after, Is.EqualTo(before), ".git/config bytes are unchanged");
        });
    }

    [Test]
    public async Task Set_ValueStartingWithDash_IsStoredLiterallyAsync()
    {
        var result = await NewImpl().ConfigAsync(_repo, "set", "roslynsentinel.testkey", "-x", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Value, Is.EqualTo("-x"));
            Assert.That(File.ReadAllText(LocalConfig(_repo)), Does.Contain("testkey = -x"));
        });
    }

    // ---- unset ----

    [Test]
    public async Task Unset_AbsentKey_ReturnsSuccessWithNoChange_AndTheConfigBytesAreUnchangedAsync()
    {
        var before = File.ReadAllBytes(LocalConfig(_repo));

        var result = await NewImpl().ConfigAsync(_repo, "unset", "roslynsentinel.absent", null, CancellationToken.None);

        var after = File.ReadAllBytes(LocalConfig(_repo));
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Changed, Is.False);
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Note, Does.Contain("Nothing was changed"));
            Assert.That(after, Is.EqualTo(before), ".git/config bytes are unchanged");
        });
    }

    [Test]
    public async Task Unset_SetKey_IsRemovedAndReportsThePreviousValueAsync()
    {
        RunGit(_repo, "config", Probe, "old-value");

        var result = await NewImpl().ConfigAsync(_repo, "unset", Probe, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Changed, Is.True);
            Assert.That(result.PreviousValue, Is.EqualTo("old-value"));
            Assert.That(result.Value, Is.Null);
            Assert.That(File.ReadAllText(LocalConfig(_repo)), Does.Not.Contain("old-value"), "the entry is gone from .git/config");
        });
    }

    [Test]
    public async Task Unset_KeySetOnlyGlobally_IsAReportedNoOp_AndTheGlobalFileIsUntouchedAsync()
    {
        SetGlobal(Probe, "global-value");
        var localBefore = File.ReadAllBytes(LocalConfig(_repo));
        var globalBefore = File.ReadAllBytes(_globalConfig);

        var result = await NewImpl().ConfigAsync(_repo, "unset", Probe, null, CancellationToken.None);

        var localAfter = File.ReadAllBytes(LocalConfig(_repo));
        var globalAfter = File.ReadAllBytes(_globalConfig);
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Error);
            Assert.That(result.Changed, Is.False);
            Assert.That(result.PreviousValue, Is.EqualTo("global-value"));
            Assert.That(result.Value, Is.EqualTo("global-value"));
            Assert.That(result.Note, Does.Contain("another scope"));
            Assert.That(localAfter, Is.EqualTo(localBefore), ".git/config bytes are unchanged");
            Assert.That(globalAfter, Is.EqualTo(globalBefore), "the global file is untouched");
        });
    }

    // ---- action ----

    [TestCase("list")]
    [TestCase(null)]
    public async Task UnknownAction_ReturnsActionRequiredAsync(string? action)
    {
        var result = await NewImpl().ConfigAsync(_repo, action, Probe, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorKind, Is.EqualTo(GitErrorCodes.ActionRequired));
        });
    }
}
