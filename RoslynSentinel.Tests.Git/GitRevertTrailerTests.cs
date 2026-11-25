// Tests for Git(revert) with the opt-in commit trailers (git config roslynsentinel.commitTrailers): the revert is staged with
// --no-commit and committed with Tool-Call-Id / Tool-Agent trailers. The requirement checked here is that nothing else changes:
// the revert message is today's plus the trailer block, a conflicting / empty / refused revert answers and leaves the repository exactly
// as the plain single `git revert` does, and with the config off the plain single call is what runs (prepare-commit-msg's source
// argument is the witness: "message" for git revert, "merge" for git commit picking up MERGE_MSG). Racing commits from another session are
// simulated with a post-commit hook as in GitCommitHashRaceTests.

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
public class GitRevertTrailerTests
{
    private const string ForeignSubject = "foreign commit by another session";

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
        if (_repoDir is null || !Directory.Exists(_repoDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_repoDir, recursive: true);
    }

    // A repo with "initial commit" (README.md) and "add b" (b.txt, with a body); returns the sha of "add b".
    private string NewRepo(bool trailers)
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitRevertTrailer_" + Guid.NewGuid());
        GitTestRepo.InitRepo(_repoDir);
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "core.autocrlf", "false");
        Directory.CreateDirectory(HooksDir);
        Git("config", "core.hooksPath", HooksDir.Replace('\\', '/'));
        if (trailers)
        {
            Git("config", GitCommitTrailers.ConfigKey, "true");
        }

        WriteFile("README.md", "hello");
        Git("add", "-A");
        Git("commit", "-m", "initial commit");
        WriteFile("b.txt", "b");
        Git("add", "-A");
        Git("commit", "-m", "add b", "-m", "body line");

        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitTools = new GitTools(workspaceManager, NullLogger<GitTools>.Instance);
        return Git("rev-parse", "HEAD");
    }

    private string HooksDir => Path.Combine(_repoDir, ".git", "hooks");

    private string HookLog => Path.Combine(_repoDir, ".git", "hooklog");

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

    // Logs which hooks ran, and prepare-commit-msg's source argument ("message" from git revert, "merge" from git commit with MERGE_MSG).
    private void InstallHookLogging()
    {
        InstallHook("pre-commit", "#!/bin/sh\necho \"pre-commit\" >> .git/hooklog");
        InstallHook("prepare-commit-msg", "#!/bin/sh\necho \"prepare-commit-msg src=$2\" >> .git/hooklog");
        InstallHook("commit-msg", "#!/bin/sh\necho \"commit-msg\" >> .git/hooklog");
        InstallHook("post-commit", "#!/bin/sh\necho \"post-commit\" >> .git/hooklog");
        File.Delete(HookLog);
    }

    private string[] HooksThatRan() => File.Exists(HookLog) ? File.ReadAllLines(HookLog) : [];

    private static void AsCall(string callId, string? agentId = null)
    {
        var context = new ToolCallContext(agentId, null, "Git", null, 0);
        context.AssignToolCallId(callId);
        ToolCallContext.Set(context);
    }

    private async Task<SentinelCallToolResult<object>> RevertAsync(string sha, int? mainline = null, bool noCommit = false) =>
        await _gitTools.GitTool(reason: "revert trailer test", GitOperation.revert, @ref: sha, mainline: mainline, noCommit: noCommit);

    private string MessageOf(string commit) => Git("log", "-1", "--format=%B", commit);

    private string RevertMessage(string subject, string sha) => $"Revert \"{subject}\"\n\nThis reverts commit {sha}.";

    // Everything a caller can observe about a revert attempt: the tool's answer and the repository state it left behind.
    private string Observe(SentinelCallToolResult<object> result)
    {
        var state = string.Join(",", new[] { "REVERT_HEAD", "MERGE_MSG", "CHERRY_PICK_HEAD", "MERGE_HEAD" }
            .Where(f => File.Exists(Path.Combine(_repoDir, ".git", f))));
        var answer = result.IsError ? "ERROR: " + result.ErrorData?.Message : "OK";
        return $"{answer}\nstate files: {state}\nporcelain:\n{Git("status", "--porcelain")}\nHEAD subject: {Git("log", "-1", "--format=%s")}";
    }

    // ----- message bytes, trailers, hooks -----

    [Test]
    public async Task TrailersOff_RunsThePlainRevert_MessageAndHooksAreTodaysAsync()
    {
        var sha = NewRepo(trailers: false);
        InstallHookLogging();
        AsCall("0123456789ab", "agent-1");

        var result = await RevertAsync(sha);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var revert = (GitRevertResult)result.SuccessData!;
        Assert.That(MessageOf(revert.CommitHash), Is.EqualTo(RevertMessage("add b", sha)), "no trailer when off.");
        Assert.That(HooksThatRan(), Is.EqualTo(new[] { "prepare-commit-msg src=message", "post-commit" }),
            "a single git revert: prepare-commit-msg from the message source, then post-commit; no pre-commit, no commit-msg.");
        Assert.That(result.Findings, Is.Null.Or.Empty);
    }

    [Test]
    public async Task TrailersOn_MessageIsTodaysPlusAProperTrailerBlock_HooksMatchAPlainRevertExceptThePrepareSourceAsync()
    {
        var sha = NewRepo(trailers: true);
        InstallHookLogging();
        AsCall("0123456789ab", "agent-1");

        var result = await RevertAsync(sha);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var revert = (GitRevertResult)result.SuccessData!;
        Assert.That(MessageOf(revert.CommitHash), Is.EqualTo(RevertMessage("add b", sha) + "\n\nTool-Call-Id: 0123456789ab\nTool-Agent: agent-1"));
        Assert.That(revert.CommitHash, Is.EqualTo(Git("rev-parse", "HEAD")));
        Assert.That(revert.Message, Does.Contain("Revert \"add b\""));
        Assert.That(revert.PendingCommit, Is.False);
        // --no-verify keeps a plain revert's hook set; only prepare-commit-msg's source differs (git commit picks MERGE_MSG up as "merge").
        Assert.That(HooksThatRan(), Is.EqualTo(new[] { "prepare-commit-msg src=merge", "post-commit" }));
        Assert.That(Git("status", "--porcelain"), Is.Empty);
        Assert.That(File.Exists(Path.Combine(_repoDir, ".git", "REVERT_HEAD")), Is.False, "no revert left in progress.");
    }

    [Test]
    public async Task TrailersOn_NoAgentId_OnlyToolCallIdAsync()
    {
        var sha = NewRepo(trailers: true);
        AsCall("0123456789ab");

        var result = await RevertAsync(sha);

        Assert.That(MessageOf(((GitRevertResult)result.SuccessData!).CommitHash), Is.EqualTo(RevertMessage("add b", sha) + "\n\nTool-Call-Id: 0123456789ab"));
    }

    [Test]
    public async Task MergeRevert_WithMainline_MessageEqualsThePlainRevertsPlusTheTrailersAsync()
    {
        NewRepo(trailers: true);
        Git("checkout", "-b", "feature");
        WriteFile("f.txt", "f");
        Git("add", "-A");
        Git("commit", "-m", "feature work");
        Git("checkout", "-");
        WriteFile("m.txt", "m");
        Git("add", "-A");
        Git("commit", "-m", "main work");
        Git("merge", "--no-ff", "feature", "-m", "Merge feature");
        var merge = Git("rev-parse", "HEAD");
        AsCall("0123456789ab");

        var on = await RevertAsync(merge, mainline: 1);
        Assert.That(on.IsError, Is.False, on.ErrorData?.Message);
        var messageOn = MessageOf("HEAD");

        // The same revert again through the plain path (config off): its message is the reference.
        Git("reset", "--hard", "HEAD~1");
        Git("config", GitCommitTrailers.ConfigKey, "false");
        var off = await RevertAsync(merge, mainline: 1);
        Assert.That(off.IsError, Is.False, off.ErrorData?.Message);
        var messageOff = MessageOf("HEAD");

        Assert.That(messageOff, Does.Contain("reversing"), "precondition: a merge revert has extra text.");
        Assert.That(messageOn, Is.EqualTo(messageOff + "\n\nTool-Call-Id: 0123456789ab"));
        Assert.That(Git("show", "--name-only", "--format=", "HEAD"), Is.EqualTo("f.txt"));
    }

    // ----- races -----

    [Test]
    public async Task TrailersOn_ForeignCommitsLandDuringTheCommit_ReturnsTheRevertCommitAsync()
    {
        var sha = NewRepo(trailers: true);
        InstallHook("post-commit", ForeignCommitsHook);
        AsCall("0123456789ab");

        var result = await RevertAsync(sha);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var revert = (GitRevertResult)result.SuccessData!;
        Assert.That(Git("log", "-1", "--format=%s", "HEAD"), Is.EqualTo(ForeignSubject), "precondition: the hook moved HEAD onto foreign commits.");
        Assert.That(revert.CommitHash, Is.Not.EqualTo(Git("rev-parse", "HEAD")));
        Assert.That(Git("rev-list", "--count", revert.CommitHash + "..HEAD"), Is.EqualTo("3"));
        Assert.That(MessageOf(revert.CommitHash), Is.EqualTo(RevertMessage("add b", sha) + "\n\nTool-Call-Id: 0123456789ab"));
        Assert.That(Git("rev-parse", revert.CommitHash + "^"), Is.EqualTo(sha));
        Assert.That(Git("show", "--name-only", "--format=", revert.CommitHash), Is.EqualTo("b.txt"));
    }

    [Test]
    public async Task TrailersOn_TrailerRewrittenByAHook_FallsBackToTheDerivationAndStillReturnsTheRightHashAsync()
    {
        var sha = NewRepo(trailers: true);
        // prepare-commit-msg runs after the trailers are appended and before the commit: it removes them, so the lookup finds nothing.
        InstallHook("prepare-commit-msg", "#!/bin/sh\nsed -i '/^Tool-/d' \"$1\"");
        InstallHook("post-commit", ForeignCommitsHook);
        AsCall("0123456789ab");

        var result = await RevertAsync(sha);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var revert = (GitRevertResult)result.SuccessData!;
        Assert.That(Git("log", "-1", "--format=%s", "HEAD"), Is.EqualTo(ForeignSubject));
        Assert.That(MessageOf(revert.CommitHash), Is.EqualTo(RevertMessage("add b", sha)), "precondition: the trailer really was removed.");
        Assert.That(revert.CommitHash, Is.Not.EqualTo(Git("rev-parse", "HEAD")));
        Assert.That(Git("rev-parse", revert.CommitHash + "^"), Is.EqualTo(sha));
    }

    // ----- unchanged behaviour: conflict, empty, refused, noCommit -----

    private string NewConflictRepo(bool trailers)
    {
        NewRepo(trailers);
        WriteFile("a.txt", "1\n");
        Git("add", "-A");
        Git("commit", "-m", "base a");
        WriteFile("a.txt", "2\n");
        Git("commit", "-am", "change to 2");
        var target = Git("rev-parse", "HEAD");
        WriteFile("a.txt", "3\n");
        Git("commit", "-am", "change to 3");
        return target;
    }

    [Test]
    public async Task Conflict_LeavesRevertInProgressWithTodaysErrorTextAndAbortWorksAsync()
    {
        var target = NewConflictRepo(trailers: true);
        AsCall("0123456789ab");

        var on = await RevertAsync(target);
        var observedOn = Observe(on);
        var status = await _gitTools.GitTool(reason: "revert trailer test", GitOperation.status);
        Assert.That(on.IsError, Is.True);
        Assert.That(((GitStatusResult)status.SuccessData!).InProgress, Is.EqualTo("revert"));
        Assert.That(Git("status", "--porcelain"), Does.Contain("UU a.txt"));

        var abort = await _gitTools.GitTool(reason: "revert trailer test", GitOperation.abort);
        Assert.That(abort.IsError, Is.False, abort.ErrorData?.Message);
        Assert.That(Git("status", "--porcelain"), Is.Empty, "abort restores the clean tree.");

        // The same revert with the config off is the plain single git revert: its answer and state are the reference.
        Git("config", GitCommitTrailers.ConfigKey, "false");
        var off = await RevertAsync(target);
        var observedOff = Observe(off);

        Assert.That(observedOn, Is.EqualTo(observedOff), "a conflicting revert answers and leaves the repository exactly as before, trailers or not.");
        Assert.That(observedOn, Does.Contain("REVERT_HEAD"));
    }

    [Test]
    public async Task Conflict_ThenResolveAndGitCommit_UsesTheExistingPathAsync()
    {
        var target = NewConflictRepo(trailers: true);
        AsCall("0123456789ab");
        var conflicted = await RevertAsync(target);
        Assert.That(conflicted.IsError, Is.True);

        WriteFile("a.txt", "resolved\n");
        var commit = await _gitTools.GitTool(reason: "revert trailer test", GitOperation.commit, message: "Resolve the revert", scope: GitStageScope.all);

        Assert.That(commit.IsError, Is.False, commit.ErrorData?.Message);
        var hash = ((GitCommitResult)commit.SuccessData!).CommitHash;
        Assert.That(hash, Is.EqualTo(Git("rev-parse", "HEAD")));
        Assert.That(MessageOf(hash), Does.StartWith("Resolve the revert").And.Contain("Tool-Call-Id: 0123456789ab"));
        Assert.That(Git("status", "--porcelain"), Is.Empty);
        Assert.That(File.Exists(Path.Combine(_repoDir, ".git", "REVERT_HEAD")), Is.False);
    }

    [Test]
    public async Task EmptyRevert_AlreadyRevertedChange_AnswersAndLeavesTheRepositoryAsThePlainRevertDoesAsync()
    {
        var sha = NewRepo(trailers: true);
        Git("revert", "--no-edit", sha);
        AsCall("0123456789ab");

        var on = await RevertAsync(sha);
        var observedOn = Observe(on);
        Git("reset", "--merge");
        File.Delete(Path.Combine(_repoDir, ".git", "MERGE_MSG"));

        Git("config", GitCommitTrailers.ConfigKey, "false");
        var off = await RevertAsync(sha);
        var observedOff = Observe(off);

        Assert.That(off.IsError, Is.True, "precondition: reverting an already reverted change is refused by plain git revert.");
        Assert.That(observedOn, Is.EqualTo(observedOff));
    }

    [Test]
    public async Task RefusedRevert_UnrelatedStagedChange_AnswersAsToday_AndTheStagedFileIsNotCommittedAsync()
    {
        var sha = NewRepo(trailers: true);
        WriteFile("x.txt", "x");
        Git("add", "x.txt");
        var headBefore = Git("rev-parse", "HEAD");
        AsCall("0123456789ab");

        var on = await RevertAsync(sha);
        var observedOn = Observe(on);
        Git("config", GitCommitTrailers.ConfigKey, "false");
        var off = await RevertAsync(sha);
        var observedOff = Observe(off);

        Assert.That(off.IsError, Is.True, "precondition: plain git revert refuses with staged changes.");
        Assert.That(observedOn, Is.EqualTo(observedOff));
        Assert.That(Git("rev-parse", "HEAD"), Is.EqualTo(headBefore), "nothing was committed.");
        Assert.That(Git("diff", "--cached", "--name-only"), Is.EqualTo("x.txt"), "the staged file is still staged, not swept into a revert commit.");
    }

    [Test]
    public async Task RevertWhileAnotherOperationIsInProgress_AnswersAsTodayAsync()
    {
        var target = NewConflictRepo(trailers: true);
        // A merge waiting for resolution: reset --merge must never be what undoes anything here.
        Git("checkout", "-b", "side", "HEAD~2");
        WriteFile("s.txt", "s");
        Git("add", "-A");
        Git("commit", "-m", "side work");
        Git("checkout", "-");
        var merge = GitTestRepo.RunGitRaw(_repoDir, "merge", "side", "--no-commit", "--no-ff");
        Assert.That(File.Exists(Path.Combine(_repoDir, ".git", "MERGE_HEAD")), Is.True, "precondition: a merge is in progress " + merge.StdOut);
        AsCall("0123456789ab");

        var on = await RevertAsync(target);
        var observedOn = Observe(on);
        Git("config", GitCommitTrailers.ConfigKey, "false");
        var off = await RevertAsync(target);

        Assert.That(observedOn, Is.EqualTo(Observe(off)));
        Assert.That(File.Exists(Path.Combine(_repoDir, ".git", "MERGE_HEAD")), Is.True, "the user's merge is untouched.");
        Assert.That(Git("status", "--porcelain"), Does.Contain("s.txt"));
    }

    [Test]
    public async Task RootCommitRevert_SameOutcomeWithAndWithoutTrailersAsync()
    {
        NewRepo(trailers: true);
        var root = Git("rev-list", "--max-parents=0", "HEAD");
        AsCall("0123456789ab");

        var on = await RevertAsync(root);
        var observedOn = Observe(on);
        var messageOn = on.IsError ? null : MessageOf("HEAD");
        if (!on.IsError)
        {
            Git("reset", "--hard", "HEAD~1");
        }
        else
        {
            Git("reset", "--merge");
            File.Delete(Path.Combine(_repoDir, ".git", "MERGE_MSG"));
        }

        Git("config", GitCommitTrailers.ConfigKey, "false");
        var off = await RevertAsync(root);

        Assert.That(on.IsError, Is.EqualTo(off.IsError));
        if (off.IsError)
        {
            Assert.That(observedOn, Is.EqualTo(Observe(off)));
        }
        else
        {
            Assert.That(messageOn, Is.EqualTo(MessageOf("HEAD") + "\n\nTool-Call-Id: 0123456789ab"));
        }
    }

    [Test]
    public async Task NoCommit_WithTrailersOn_StagesOnlyAndCommitsNothingAsync()
    {
        var sha = NewRepo(trailers: true);
        InstallHookLogging();
        AsCall("0123456789ab");

        var result = await RevertAsync(sha, noCommit: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var revert = (GitRevertResult)result.SuccessData!;
        Assert.That(revert.PendingCommit, Is.True);
        Assert.That(revert.CommitHash, Is.Empty);
        Assert.That(revert.Message, Is.EqualTo("Revert staged - call commit to finalise."));
        Assert.That(Git("rev-parse", "HEAD"), Is.EqualTo(sha), "nothing was committed.");
        Assert.That(Git("diff", "--cached", "--name-only"), Is.EqualTo("b.txt"));
        Assert.That(HooksThatRan(), Is.Empty);
        Assert.That(result.Findings, Is.Null.Or.Empty);
    }
}
