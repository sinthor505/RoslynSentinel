// Tests for the opt-in commit trailers (git config roslynsentinel.commitTrailers): Tool-Call-Id / Tool-Agent trailers on Git(commit),
// the lookup of the new commit by that id, and the fallbacks. Intervening commits from "another session" are simulated with a
// post-commit hook exactly as in GitCommitHashRaceTests (the hook runs inside `git commit`, after the commit exists and before
// the command returns; the fixture sets core.hooksPath explicitly so a user-level hooksPath cannot skip it).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCommitTrailerTests
{
    private const string ForeignSubject = "foreign commit by another session";

    // Three foreign commits (more than one) land on top of the new commit before git commit returns.
    private const string ForeignCommitsHook = """
        #!/bin/sh
        for n in 1 2 3; do
          tree=$(git rev-parse "HEAD^{tree}")
          parent=$(git rev-parse HEAD)
          foreign=$(git commit-tree -p "$parent" -m "foreign commit by another session" "$tree")
          git update-ref HEAD "$foreign"
        done
        """;

    private string _repoDir = null!;
    private GitTools _gitTools = null!;

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        DeleteDirectoryTree(_repoDir);
    }

    private static void DeleteDirectoryTree(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }

    private void NewRepo(bool withInitialCommit, bool trailers)
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitTrailer_" + Guid.NewGuid());
        GitTestRepo.InitRepo(_repoDir);
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Directory.CreateDirectory(HooksDir);
        Git("config", "core.hooksPath", HooksDir.Replace('\\', '/'));
        if (trailers)
        {
            Git("config", GitCommitTrailers.ConfigKey, "true");
        }

        if (withInitialCommit)
        {
            WriteFile("README.md", "hello");
            Git("add", "-A");
            Git("commit", "-m", "initial commit");
        }

        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitTools = new GitTools(workspaceManager, NullLogger<GitTools>.Instance);
    }

    private string HooksDir => Path.Combine(_repoDir, ".git", "hooks");

    private string Git(params string[] args) => GitTestRepo.RunGit(_repoDir, args);

    private void WriteFile(string relPath, string content) => File.WriteAllText(Path.Combine(_repoDir, relPath), content);

    private void InstallHook(string name, string script)
    {
        var path = Path.Combine(HooksDir, name);
        File.WriteAllText(path, script.ReplaceLineEndings("\n") + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The tool call this test plays: the echoed call id (null = no id assigned) and an optional agent id.
    private static void AsCall(string? callId, string? agentId = null)
    {
        var context = new ToolCallContext(agentId, null, "Git", null, 0);
        if (callId is not null)
        {
            context.AssignToolCallId(callId);
        }

        ToolCallContext.Set(context);
    }

    private async Task<GitCommitResult> CommitAsync(string? message, string? paths = null, GitStageScope? scope = null, bool amend = false)
    {
        var result = await _gitTools.GitTool(reason: "trailer test", GitOperation.commit, message: message, paths: paths, scope: scope, amend: amend);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return (GitCommitResult)result.SuccessData!;
    }

    private string MessageOf(string commit) => Git("log", "-1", "--format=%B", commit);

    private void AssertHeadIsForeign() =>
        Assert.That(Git("log", "-1", "--format=%s", "HEAD"), Is.EqualTo(ForeignSubject),
            "precondition: the post-commit hook must have moved HEAD onto a foreign commit, or this test proves nothing.");

    private GitImpl NewGitImpl() =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") });

    // ----- on / off -----

    [Test]
    public async Task TrailersOff_MessageIsByteIdenticalToAPlainGitCommitAsync()
    {
        NewRepo(withInitialCommit: true, trailers: false);
        AsCall("aaaaaaaaaaaa", "agent-1");
        WriteFile("a.txt", "a");
        const string message = "own subject\n\nown body";

        var commit = await CommitAsync(message, paths: "a.txt");

        // The control: the same message committed by plain git, the way every commit was made before trailers existed.
        Git("commit", "--allow-empty", "-m", message);
        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo(MessageOf("HEAD")));
        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo(message));
        // GitCommitResult.Message has always gone through RunGitAsync's line re-join (CRLF on Windows): unchanged, so compare modulo line ending.
        Assert.That(commit.Message.ReplaceLineEndings("\n"), Is.EqualTo(message));
    }

    [Test]
    public async Task TrailersOnlyWhenConfigIsTrue_UnsetAndInvalidValuesMeanOffAsync()
    {
        NewRepo(withInitialCommit: true, trailers: false);
        AsCall("aaaaaaaaaaaa");
        Git("config", GitCommitTrailers.ConfigKey, "maybe");
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("invalid config value", paths: "a.txt");

        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("invalid config value"), "a value that is not a boolean is off, not an error.");

        Git("config", GitCommitTrailers.ConfigKey, "false");
        WriteFile("b.txt", "b");
        commit = await CommitAsync("explicitly off", paths: "b.txt");
        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("explicitly off"));
    }

    [Test]
    public async Task TrailersOn_AddsToolCallIdWithTheEchoedId_AndNoAgentTrailerWithoutAnAgentAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        AsCall("0123456789ab");
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("own subject\n\nown body", paths: "a.txt");

        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("own subject\n\nown body\n\nTool-Call-Id: 0123456789ab"));
        Assert.That(commit.CommitHash, Is.EqualTo(Git("rev-parse", "HEAD")));
        Assert.That(commit.Message, Does.EndWith("Tool-Call-Id: 0123456789ab"));
    }

    [Test]
    public async Task TrailersOn_AddsToolAgentOnlyWhenTheCallHasAnAgentIdAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        AsCall("0123456789ab", agentId: "agent-7");
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("with agent", paths: "a.txt");

        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("with agent\n\nTool-Call-Id: 0123456789ab\nTool-Agent: agent-7"));
    }

    [Test]
    public async Task TrailersOn_CallWithoutAnEchoIdStillGetsAFreshRandomIdAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        ToolCallContext.Set(null);
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("no context", paths: "a.txt");

        var ids = GitCommitTrailers.GetValues(MessageOf(commit.CommitHash), GitCommitTrailers.ToolCallIdKey);
        Assert.That(ids, Has.Count.EqualTo(1));
        Assert.That(ids[0], Does.Match("^[0-9a-f]{12}$"));
    }

    [Test]
    public async Task TrailersOn_NoRaceCommitHashEqualsWhatTheSummaryDerivationGivesAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        AsCall("0123456789ab");
        var before = Git("rev-list", "--parents", "-n", "1", "HEAD").Split(' ');
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("own commit", paths: "a.txt");

        var viaSummary = await NewGitImpl().DeriveNewCommitHashAsync(_repoDir, $"[master {commit.CommitHash[..7]}] own commit\n", before, amend: false, CancellationToken.None);
        Assert.That(commit.CommitHash, Is.EqualTo(viaSummary));
    }

    // ----- amend -----

    [Test]
    public async Task Amend_WithMessage_ReplacesTheTrailersAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        AsCall("111111111111", "agent-1");
        WriteFile("a.txt", "a");
        await CommitAsync("first", paths: "a.txt");

        // The new message itself quotes an old trailer: it must be replaced, not duplicated.
        AsCall("222222222222");
        var amended = await CommitAsync("second\n\nTool-Call-Id: stale", scope: GitStageScope.all, amend: true);

        Assert.That(MessageOf(amended.CommitHash), Is.EqualTo("second\n\nTool-Call-Id: 222222222222"));
    }

    [Test]
    public async Task Amend_WithoutMessage_KeepsHeadsMessageButReplacesBothTrailersAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        AsCall("111111111111", "agent-1");
        WriteFile("a.txt", "a");
        await CommitAsync("subject\n\nbody text\n\nCo-Authored-By: Pat <pat@example.com>", paths: "a.txt");
        Assert.That(MessageOf("HEAD"), Does.EndWith("Tool-Call-Id: 111111111111\nTool-Agent: agent-1"), "precondition");

        // The amending call has no agent: the old Tool-Agent must not survive next to the new id.
        AsCall("222222222222");
        WriteFile("b.txt", "b");
        var amended = await CommitAsync(null, scope: GitStageScope.all, amend: true);

        Assert.That(MessageOf(amended.CommitHash), Is.EqualTo("subject\n\nbody text\n\nCo-Authored-By: Pat <pat@example.com>\nTool-Call-Id: 222222222222"));
        Assert.That(amended.CommitHash, Is.EqualTo(Git("rev-parse", "HEAD")));
    }

    [Test]
    public async Task Amend_WithoutMessage_TrailersOff_LeavesOldTrailersAloneAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        AsCall("111111111111");
        WriteFile("a.txt", "a");
        await CommitAsync("subject", paths: "a.txt");
        Git("config", GitCommitTrailers.ConfigKey, "false");

        WriteFile("b.txt", "b");
        var amended = await CommitAsync(null, scope: GitStageScope.all, amend: true);

        Assert.That(MessageOf(amended.CommitHash), Is.EqualTo("subject\n\nTool-Call-Id: 111111111111"), "off = today's --no-edit behaviour.");
    }

    // ----- lookup and fallbacks under racing commits -----

    [Test]
    public async Task Lookup_FindsTheCommitDespiteSeveralForeignCommitsOnTopAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        InstallHook("post-commit", ForeignCommitsHook);
        AsCall("0123456789ab");
        WriteFile("a.txt", "a");
        WriteFile("b.txt", "b");

        var commit = await CommitAsync("own commit", paths: "a.txt");

        AssertHeadIsForeign();
        Assert.That(Git("rev-list", "--count", commit.CommitHash + "..HEAD"), Is.EqualTo("3"), "three foreign commits sit on top of the new one.");
        Assert.That(commit.CommitHash, Is.Not.EqualTo(Git("rev-parse", "HEAD")));
        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("own commit\n\nTool-Call-Id: 0123456789ab"));
        Assert.That(Git("show", "--name-only", "--format=", commit.CommitHash), Is.EqualTo("a.txt"));
    }

    [Test]
    public async Task StrippedTrailer_FallsBackToTheSummaryDerivation_AndStillReturnsTheRightHashAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        // A commit-msg hook that removes the trailers, then foreign commits: the lookup finds nothing, so the G2 derivation must answer.
        InstallHook("commit-msg", "#!/bin/sh\nsed -i '/^Tool-/d' \"$1\"");
        InstallHook("post-commit", ForeignCommitsHook);
        AsCall("0123456789ab");
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("own commit", paths: "a.txt");

        AssertHeadIsForeign();
        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("own commit"), "precondition: the hook really removed the trailer.");
        Assert.That(commit.CommitHash, Is.Not.EqualTo(Git("rev-parse", "HEAD")));
        Assert.That(Git("show", "--name-only", "--format=", commit.CommitHash), Is.EqualTo("a.txt"));
        Assert.That(commit.Message, Is.EqualTo("own commit"));
    }

    [Test]
    public async Task UnbornBranch_RootCommitWithTrailers_ReturnsTheRootCommitAsync()
    {
        NewRepo(withInitialCommit: false, trailers: true);
        InstallHook("post-commit", ForeignCommitsHook);
        AsCall("0123456789ab", "agent-2");
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("first ever", scope: GitStageScope.all);

        AssertHeadIsForeign();
        Assert.That(Git("rev-list", "--parents", "-n", "1", commit.CommitHash), Is.EqualTo(commit.CommitHash), "the root commit has no parent.");
        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("first ever\n\nTool-Call-Id: 0123456789ab\nTool-Agent: agent-2"));
    }

    [Test]
    public async Task DetachedHead_WithTrailers_ReturnsTheNewCommitAsync()
    {
        NewRepo(withInitialCommit: true, trailers: true);
        Git("checkout", "--detach");
        InstallHook("post-commit", ForeignCommitsHook);
        AsCall("0123456789ab");
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("detached", paths: "a.txt");

        AssertHeadIsForeign();
        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("detached\n\nTool-Call-Id: 0123456789ab"));
    }

    // ----- item 5: bracketed branch names, merge resolution -----

    [Test]
    public async Task BranchNameContainingABracket_SummaryLineStillParsesUnderARaceAsync()
    {
        NewRepo(withInitialCommit: true, trailers: false);
        Git("checkout", "-b", "a]b");
        InstallHook("post-commit", ForeignCommitsHook);
        WriteFile("a.txt", "a");

        // Trailers off, so only the summary line "[a]b 1a2b3c4] ..." can identify the commit; a foreign commit sits on top.
        var commit = await CommitAsync("on a bracketed branch", paths: "a.txt");

        AssertHeadIsForeign();
        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("on a bracketed branch"));
        Assert.That(commit.CommitHash, Is.Not.EqualTo(Git("rev-parse", "HEAD")));
    }

    private (string MergeHead, string[] HeadBefore) StartConflictingMerge()
    {
        WriteFile("a.txt", "base\n");
        Git("add", "-A");
        Git("commit", "-m", "base");
        Git("checkout", "-b", "feature");
        WriteFile("a.txt", "feature side\n");
        Git("commit", "-am", "feature change");
        var mergeHead = Git("rev-parse", "HEAD");
        Git("checkout", "-");
        WriteFile("a.txt", "main side\n");
        Git("commit", "-am", "main change");
        var headBefore = Git("rev-list", "--parents", "-n", "1", "HEAD").Split(' ');
        var merge = GitTestRepo.RunGitRaw(_repoDir, "merge", "feature");
        Assert.That(merge.ExitCode, Is.Not.EqualTo(0), "precondition: the merge must conflict and wait for a commit.");
        WriteFile("a.txt", "resolved\n");
        return (mergeHead, headBefore);
    }

    [Test]
    public async Task MergeResolutionCommit_WithAForeignCommitOnTop_ReturnsTheMergeCommitAsync()
    {
        NewRepo(withInitialCommit: true, trailers: false);
        StartConflictingMerge();
        InstallHook("post-commit", ForeignCommitsHook);

        var commit = await CommitAsync("merge resolution", scope: GitStageScope.all);

        AssertHeadIsForeign();
        Assert.That(MessageOf(commit.CommitHash), Is.EqualTo("merge resolution"));
        Assert.That(Git("rev-list", "--parents", "-n", "1", commit.CommitHash).Split(' '), Has.Length.EqualTo(3), "a merge commit: itself and two parents.");
    }

    [Test]
    public async Task Derive_UnparsableOutput_MergeResolution_ProvenOnlyWhenMergeHeadIsExpectedAsSecondParentAsync()
    {
        NewRepo(withInitialCommit: true, trailers: false);
        var (mergeHead, headBefore) = StartConflictingMerge();
        Git("add", "-A");
        Git("commit", "-m", "merge resolution");
        var merge = Git("rev-parse", "HEAD");

        var withMergeHead = await NewGitImpl().DeriveNewCommitHashAsync(_repoDir, "no summary", headBefore, amend: false, mergeHead, CancellationToken.None);
        var withoutMergeHead = await NewGitImpl().DeriveNewCommitHashAsync(_repoDir, "no summary", headBefore, amend: false, mergeHead: null, CancellationToken.None);

        Assert.That(withMergeHead, Is.EqualTo(merge));
        Assert.That(withoutMergeHead, Is.Empty, "a merge commit has two parents, so it is not provable without MERGE_HEAD.");
    }

    [Test]
    public void IsOwnHead_MergeHeadIsTheSecondExpectedParent()
    {
        string[] before = ["old", "older"];

        Assert.That(GitImpl.IsOwnHead(before, ["merge", "old", "theirs"], replacesHead: false, mergeHead: "theirs"), Is.True);
        Assert.That(GitImpl.IsOwnHead(before, ["merge", "old", "other"], replacesHead: false, mergeHead: "theirs"), Is.False, "a different second parent.");
        Assert.That(GitImpl.IsOwnHead(before, ["merge", "old"], replacesHead: false, mergeHead: "theirs"), Is.False, "a merge was expected but the commit has one parent.");
        Assert.That(GitImpl.IsOwnHead(before, ["foreign", "merge"], replacesHead: false, mergeHead: "theirs"), Is.False, "a foreign commit on top of the merge.");
        Assert.That(GitImpl.IsOwnHead(before, ["merge", "old", "theirs"], replacesHead: false), Is.False, "two parents without a MERGE_HEAD snapshot are not provable.");
    }
}
