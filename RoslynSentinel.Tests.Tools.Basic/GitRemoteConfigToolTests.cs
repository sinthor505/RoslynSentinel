using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Tool-layer coverage for <c>GitRemote(operation: config)</c>: parameter routing, the action guard, repoPath
/// resolution and the result envelope. The get, set and unset semantics are covered at the impl layer in
/// <c>GitConfigOperationTests</c> and are not repeated here.
/// </summary>
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class GitRemoteConfigToolTests
{
    private const string Probe = "roslynsentinel.probe";
    private const string TestKey = "roslynsentinel.testkey";

    private string _root = null!;
    private string _repo = null!;
    private GitRemoteTools _tool = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        // Short directory names: a linked-worktree git dir under a long name can pass MAX_PATH.
        _root = Path.Combine(Path.GetTempPath(), "rs-cfgtool-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
        _repo = NewRepo("repo");
        _tool = NewTool(_repo);
    }

    [TearDown]
    public void TearDown()
    {
        if (!Directory.Exists(_root))
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

    /// <summary>A tool instance whose loaded solution (and therefore its implicit working repo) lives under <paramref name="solutionDirectory"/>.</summary>
    private static GitRemoteTools NewTool(string solutionDirectory)
    {
        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(solutionDirectory, "Fake.sln") };
        return new GitRemoteTools(workspaceManager, NullLogger<GitRemoteTools>.Instance);
    }

    private static GitConfigResult ConfigSuccess(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return (GitConfigResult)result.SuccessData!;
    }

    // ---- get / set / unset round trip ----

    [Test]
    public async Task Config_Get_KeySetInRepo_ReturnsTheValueWithNoErrorAsync()
    {
        RunGit(_repo, "config", Probe, "tool-value");

        var result = await _tool.GitRemoteTool(reason: "read a configured key", GitRemoteOperation.config, action: GitAction.get, configKey: Probe);

        var config = ConfigSuccess(result);
        Assert.That(config.Value, Is.EqualTo("tool-value"));
    }

    [Test]
    public async Task Config_Get_UnsetKey_ReturnsSuccessWithNullValueAsync()
    {
        var result = await _tool.GitRemoteTool(reason: "read an unset key", GitRemoteOperation.config, action: GitAction.get, configKey: "roslynsentinel.absent");

        var config = ConfigSuccess(result);
        Assert.That(config.Value, Is.Null);
    }

    [Test]
    public async Task Config_SetThenGetThenUnset_RoundTripsAllowlistedKeyAsync()
    {
        var set = await _tool.GitRemoteTool(reason: "set an allowlisted key", GitRemoteOperation.config, action: GitAction.set, configKey: TestKey, configValue: "round-trip");
        var setResult = ConfigSuccess(set);
        Assert.Multiple(() =>
        {
            Assert.That(setResult.PreviousValue, Is.Null, "the key was not set before");
            Assert.That(setResult.Changed, Is.True);
        });

        var get = await _tool.GitRemoteTool(reason: "read back the set key", GitRemoteOperation.config, action: GitAction.get, configKey: TestKey);
        Assert.That(ConfigSuccess(get).Value, Is.EqualTo("round-trip"));

        var unset = await _tool.GitRemoteTool(reason: "remove the set key", GitRemoteOperation.config, action: GitAction.unset, configKey: TestKey);
        var unsetResult = ConfigSuccess(unset);
        Assert.Multiple(() =>
        {
            Assert.That(unsetResult.Changed, Is.True);
            Assert.That(unsetResult.PreviousValue, Is.EqualTo("round-trip"));
            Assert.That(File.ReadAllText(LocalConfig(_repo)), Does.Not.Contain("testkey"), "the entry is gone from .git/config");
        });

        var afterUnset = await _tool.GitRemoteTool(reason: "read the key after unset", GitRemoteOperation.config, action: GitAction.get, configKey: TestKey);
        Assert.That(ConfigSuccess(afterUnset).Value, Is.Null);
    }

    // ---- refusals ----

    [Test]
    public async Task Config_Set_NotAllowlistedCoreHooksPath_IsRefused_AndConfigBytesUnchangedAsync()
    {
        var before = File.ReadAllBytes(LocalConfig(_repo));

        var result = await _tool.GitRemoteTool(reason: "try to set a hook path", GitRemoteOperation.config, action: GitAction.set, configKey: "core.hooksPath", configValue: "hooks");

        var after = File.ReadAllBytes(LocalConfig(_repo));
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(GitErrorCodes.Refused));
            Assert.That(result.SuccessData, Is.Null);
            Assert.That(after, Is.EqualTo(before), ".git/config bytes are unchanged");
        });
    }

    [Test]
    public async Task Config_Get_CredentialHelper_IsRefusedWithoutAValueAsync()
    {
        const string secret = "s3cret-tool-value";
        RunGit(_repo, "config", "credential.helper", secret);

        var result = await _tool.GitRemoteTool(reason: "read a credential-bearing key", GitRemoteOperation.config, action: GitAction.get, configKey: "credential.helper");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(GitErrorCodes.Refused));
            Assert.That(result.SuccessData, Is.Null, "a refused get returns no value");
            Assert.That(result.ErrorData?.Message, Does.Not.Contain(secret));
        });
    }

    // ---- repoPath ----

    [Test]
    public async Task Config_Get_WithRepoPath_ReadsThatRepo_FromAToolWhoseWorkingDirectoryIsElsewhereAsync()
    {
        RunGit(_repo, "config", Probe, "repo-value");
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var other = NewRepo("other");
        var tool = NewTool(elsewhere);

        var fromRepo = await tool.GitRemoteTool(reason: "read the key in the named repo", GitRemoteOperation.config, action: GitAction.get, configKey: Probe, repoPath: _repo);
        var fromOther = await tool.GitRemoteTool(reason: "read the key in the other repo", GitRemoteOperation.config, action: GitAction.get, configKey: Probe, repoPath: other);

        Assert.Multiple(() =>
        {
            Assert.That(ConfigSuccess(fromRepo).Value, Is.EqualTo("repo-value"));
            Assert.That(ConfigSuccess(fromOther).Value, Is.Null, "the other repo does not carry the key");
        });
    }

    [Test]
    public async Task Config_Set_WithRepoPath_IsRefused_AndNothingIsWrittenAsync()
    {
        var before = File.ReadAllBytes(LocalConfig(_repo));

        var result = await _tool.GitRemoteTool(reason: "try to set a key in another repo", GitRemoteOperation.config, action: GitAction.set, configKey: TestKey, configValue: "v", repoPath: _repo);

        var after = File.ReadAllBytes(LocalConfig(_repo));
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
            Assert.That(result.ErrorData?.Message, Does.Contain("repoPath"));
            Assert.That(after, Is.EqualTo(before), ".git/config bytes are unchanged");
        });
    }

    // ---- action guard ----

    [Test]
    public async Task Config_WithoutAction_ReturnsActionRequiredErrorAsync()
    {
        var result = await _tool.GitRemoteTool(reason: "config call without action", GitRemoteOperation.config, configKey: Probe);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(GitErrorCodes.ActionRequired));
            Assert.That(result.ErrorData?.Message, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task Branch_WithActionGet_IsStillRefusedAsync()
    {
        var result = await _tool.GitRemoteTool(reason: "branch with an action", GitRemoteOperation.branch, action: GitAction.get);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo("InvalidArguments"));
            Assert.That(result.ErrorData?.Message, Does.Contain("action"));
        });
    }

    // ---- existing operations unchanged ----

    [Test]
    public async Task Tag_List_OnFreshRepo_SucceedsAsync()
    {
        var result = await _tool.GitRemoteTool(reason: "list tags in a fresh repo", GitRemoteOperation.tag, action: GitAction.list);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
    }

    [Test]
    public async Task Branch_List_OnFreshRepo_SucceedsAsync()
    {
        var result = await _tool.GitRemoteTool(reason: "list branches in a fresh repo", GitRemoteOperation.branch);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            Assert.That(result.SuccessData, Is.Not.Null);
        });
    }
}
