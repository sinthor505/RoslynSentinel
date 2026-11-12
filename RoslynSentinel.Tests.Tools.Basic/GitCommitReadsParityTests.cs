using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The reads <c>GitImpl.CommitAsync</c> and <c>RevertAsync</c> make around <c>git commit</c> (plan 5a): HEAD with its parents (<see cref="IGitReader.GetHeadWithParents"/>),
/// MERGE_HEAD (<see cref="IGitReader.GetMergeHead"/>) and the opt-in trailer config (<see cref="IGitReader.GetBoolConfig"/>, covered by GitCommitTrailerReaderTests at the reader level).
/// Reader level: the CLI-only and libgit2-only readers agree on repositories of every shape the commit flow meets (unborn, root, merge, detached, linked worktree,
/// MERGE_HEAD of every kind), the composite SERVES the normal cases from libgit2 (counter delta, no decline, no recorded failure) and DECLINES the unusual ones
/// (shallow clone, replace refs, a MERGE_HEAD git would peel or resolve differently) with exactly the CLI answer. Tool level: the Git tool's commit and revert DTOs and the
/// repository state they leave are the same whether the composite reader or the CLI-only reader backs <see cref="GitImpl"/>, in the scenarios the plan names
/// (trailers on and off, amend, unborn branch, detached HEAD, merge resolution, a post-commit hook moving HEAD, a linked worktree).
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCommitReadsParityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Runs inside git commit after the new commit exists: parks a commit with the same tree on top and moves HEAD there, as a racing session would.
    private const string ForeignCommitHook = """
        #!/bin/sh
        tree=$(git rev-parse "HEAD^{tree}")
        parent=$(git rev-parse HEAD)
        foreign=$(git commit-tree -p "$parent" -m "foreign commit by another session" "$tree")
        git update-ref HEAD "$foreign"
        """;

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-commitreads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        // git marks pack/object files read-only; clear it so the delete succeeds on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // ---- helpers ----

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static FallbackGitReader Composite() => new(new Libgit2GitReader(), new CliGitReader(), _ => { });

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A repository with its config pinned repo-locally (no autocrlf, no signing, no gc, hooks only from its own .git/hooks) and, optionally, one commit with README.md and a.txt.</summary>
    private string NewRepo(string name, bool initialCommit = true, bool trailers = false)
    {
        var directory = Path.Combine(_root, name);
        InitRepo(directory);
        RunGit(directory, "config", "core.autocrlf", "false");
        RunGit(directory, "config", "user.name", "Test");
        RunGit(directory, "config", "user.email", "test@example.com");
        RunGit(directory, "config", "commit.gpgsign", "false");
        RunGit(directory, "config", "gc.auto", "0");
        var hooks = Path.Combine(directory, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        RunGit(directory, "config", "core.hooksPath", hooks.Replace('\\', '/'));
        if (trailers)
        {
            RunGit(directory, "config", GitCommitTrailers.ConfigKey, "true");
        }

        if (initialCommit)
        {
            Write(directory, "README.md", "hello\n");
            Write(directory, "a.txt", "a\n");
            CommitAll(directory, "initial commit");
        }

        return directory;
    }

    private static string Head(string directory) => RunGit(directory, "rev-parse", "HEAD");

    // TODO use GitTestRepo.AddLinkedWorktree(main, _root, name, branch) instead of this private copy
    /// <summary>A linked worktree of <paramref name="main"/> on a new branch (absolute paths even when the machine's git config asks for relative ones, which libgit2 cannot open).</summary>
    private string AddLinkedWorktree(string main, string name)
    {
        var worktree = Path.Combine(_root, name);
        RunGit(main, "-c", "worktree.useRelativePaths=false", "worktree", "add", "--quiet", worktree, "-b", name);
        return worktree;
    }

    /// <summary>A branch with one commit changing a.txt, then the original branch with a conflicting change to a.txt, then the conflicted merge (exit 1, MERGE_HEAD written).</summary>
    private static string StartConflictedMerge(string directory)
    {
        var branch = RunGit(directory, "rev-parse", "--abbrev-ref", "HEAD");
        RunGit(directory, "checkout", "--quiet", "-b", "other");
        Write(directory, "a.txt", "other\n");
        var other = CommitAll(directory, "other change");
        RunGit(directory, "checkout", "--quiet", branch);
        Write(directory, "a.txt", "main\n");
        CommitAll(directory, "main change");
        var merge = RunGitRaw(directory, "merge", "--no-edit", "other");
        Assert.That(merge.ExitCode, Is.EqualTo(1), "premise: the merge stops on a conflict. " + merge.StdOut + merge.StdErr);
        return other;
    }

    /// <summary>The composite served exactly <paramref name="calls"/> reads from libgit2: no decline, no recorded failure.</summary>
    private static void AssertServedByLibgit2(FallbackGitReader composite, long calls = 1)
    {
        Assert.Multiple(() =>
        {
            Assert.That(composite.Libgit2Count, Is.EqualTo(calls), "libgit2 should have answered (last decline: " + composite.LastDeclineReason + ", first failure: " + composite.FirstFallbackReason + ")");
            Assert.That(composite.DeclineCount, Is.Zero, "last decline: " + composite.LastDeclineReason);
            Assert.That(composite.CliFallbackCount, Is.Zero, "first failure: " + composite.FirstFallbackReason);
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
        });
    }

    /// <summary>The composite declined the read (a planned decline, not a failure) and the CLI answered.</summary>
    private static void AssertDeclinedToTheCli(FallbackGitReader composite)
    {
        Assert.Multiple(() =>
        {
            Assert.That(composite.DeclineCount, Is.EqualTo(1));
            Assert.That(composite.Libgit2Count, Is.Zero);
            Assert.That(composite.CliFallbackCount, Is.Zero, "a decline is not a failure");
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
        });
    }

    // ---- GetHeadWithParents ----

    [Test]
    public void GetHeadWithParents_UnbornBranch_IsNullFromBothReaders_AndServedByLibgit2()
    {
        var repo = NewRepo("unborn", initialCommit: false);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetHeadWithParents(repo, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().GetHeadWithParents(repo, Timeout), Is.Null);
            Assert.That(composite.GetHeadWithParents(repo, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetHeadWithParents_RootNormalAndMergeCommits_BothReadersAgreeWithGit_AndLibgit2Serves()
    {
        var repo = NewRepo("shapes", initialCommit: false);
        var cli = new CliGitReader();
        var lib = new Libgit2GitReader();
        var composite = Composite();
        var branch = "";

        Write(repo, "a.txt", "1\n");
        var root = CommitAll(repo, "root");
        branch = RunGit(repo, "rev-parse", "--abbrev-ref", "HEAD");
        var rootCase = (cli.GetHeadWithParents(repo, Timeout), lib.GetHeadWithParents(repo, Timeout), composite.GetHeadWithParents(repo, Timeout));

        Write(repo, "a.txt", "2\n");
        var second = CommitAll(repo, "second");
        var normalCase = (cli.GetHeadWithParents(repo, Timeout), lib.GetHeadWithParents(repo, Timeout), composite.GetHeadWithParents(repo, Timeout));

        RunGit(repo, "checkout", "--quiet", "-b", "side", root);
        Write(repo, "side.txt", "s\n");
        var side = CommitAll(repo, "side");
        RunGit(repo, "checkout", "--quiet", branch);
        RunGit(repo, "merge", "--quiet", "--no-ff", "--no-edit", "side");
        var mergeCase = (cli.GetHeadWithParents(repo, Timeout), lib.GetHeadWithParents(repo, Timeout), composite.GetHeadWithParents(repo, Timeout));

        Assert.Multiple(() =>
        {
            Assert.That(Json(rootCase.Item1), Is.EqualTo(Json(new HeadWithParents(root, []))), "git: a root commit has no parents");
            Assert.That(Json(rootCase.Item2), Is.EqualTo(Json(rootCase.Item1)));
            Assert.That(Json(rootCase.Item3), Is.EqualTo(Json(rootCase.Item1)));
            Assert.That(Json(normalCase.Item1), Is.EqualTo(Json(new HeadWithParents(second, [root]))));
            Assert.That(Json(normalCase.Item2), Is.EqualTo(Json(normalCase.Item1)));
            Assert.That(Json(normalCase.Item3), Is.EqualTo(Json(normalCase.Item1)));
            Assert.That(mergeCase.Item1!.ParentShas, Is.EqualTo(new[] { second, side }), "git: a merge commit lists the branch merged into first");
            Assert.That(Json(mergeCase.Item2), Is.EqualTo(Json(mergeCase.Item1)));
            Assert.That(Json(mergeCase.Item3), Is.EqualTo(Json(mergeCase.Item1)));
        });
        AssertServedByLibgit2(composite, calls: 3);
    }

    [Test]
    public void GetHeadWithParents_DetachedHead_BothReadersAgree_AndLibgit2Serves()
    {
        var repo = NewRepo("detached");
        Write(repo, "a.txt", "2\n");
        var second = CommitAll(repo, "second");
        RunGit(repo, "checkout", "--quiet", "--detach");
        var composite = Composite();

        var viaCli = new CliGitReader().GetHeadWithParents(repo, Timeout);
        var viaLib = new Libgit2GitReader().GetHeadWithParents(repo, Timeout);
        var viaComposite = composite.GetHeadWithParents(repo, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(viaCli!.Sha, Is.EqualTo(second));
            Assert.That(Json(viaLib), Is.EqualTo(Json(viaCli)));
            Assert.That(Json(viaComposite), Is.EqualTo(Json(viaCli)));
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetHeadWithParents_MainAndLinkedWorktree_EachHasItsOwnHead_BothReadersAgree_AndLibgit2Serves()
    {
        var main = NewRepo("main");
        var worktree = AddLinkedWorktree(main, "linked");
        Write(worktree, "wt.txt", "w\n");
        CommitAll(worktree, "linked commit");
        var composite = Composite();

        var results = new[] { main, worktree }.Select(directory => (
            Cli: new CliGitReader().GetHeadWithParents(directory, Timeout),
            Lib: new Libgit2GitReader().GetHeadWithParents(directory, Timeout),
            Composite: composite.GetHeadWithParents(directory, Timeout))).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Cli!.Sha, Is.EqualTo(Head(main)));
            Assert.That(results[1].Cli!.Sha, Is.EqualTo(Head(worktree)).And.Not.EqualTo(results[0].Cli!.Sha), "the worktree has its own HEAD");
            foreach (var (cli, lib, viaComposite) in results)
            {
                Assert.That(Json(lib), Is.EqualTo(Json(cli)));
                Assert.That(Json(viaComposite), Is.EqualTo(Json(cli)));
            }
        });
        AssertServedByLibgit2(composite, calls: 2);
    }

    [Test]
    public void GetHeadWithParents_NotARepository_IsNullFromBothReaders_AndServedByLibgit2()
    {
        var directory = Path.Combine(_root, "plain-directory");
        Directory.CreateDirectory(directory);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetHeadWithParents(directory, Timeout), Is.Null);
            Assert.That(composite.GetHeadWithParents(directory, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetHeadWithParents_ShallowClone_Libgit2Declines_AndTheCliAnswerWithTheBoundaryCommitHasNoParents()
    {
        var source = NewRepo("source");
        Write(source, "a.txt", "2\n");
        CommitAll(source, "second");
        var clone = Path.Combine(_root, "shallow");
        RunGit(_root, "clone", "--quiet", "--depth", "1", "file:///" + source.Replace('\\', '/'), clone);
        var composite = Composite();

        var viaCli = new CliGitReader().GetHeadWithParents(clone, Timeout);
        var thrown = Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().GetHeadWithParents(clone, Timeout));
        var viaComposite = composite.GetHeadWithParents(clone, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(viaCli!.ParentShas, Is.Empty, "premise: git hides the parent of a shallow boundary commit, libgit2's Commit.Parents does not");
            Assert.That(thrown!.Message, Does.Contain("shallow"));
            Assert.That(Json(viaComposite), Is.EqualTo(Json(viaCli)));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetHeadWithParents_ReplaceRef_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("replaced", initialCommit: false);
        Write(repo, "a.txt", "1\n");
        var first = CommitAll(repo, "first");
        Write(repo, "a.txt", "2\n");
        var second = CommitAll(repo, "second");
        Write(repo, "a.txt", "3\n");
        CommitAll(repo, "third");
        RunGit(repo, "replace", second, first);
        var composite = Composite();

        var viaCli = new CliGitReader().GetHeadWithParents(repo, Timeout);
        var thrown = Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().GetHeadWithParents(repo, Timeout));
        var viaComposite = composite.GetHeadWithParents(repo, Timeout);

        Assert.That(thrown!.Message, Does.Contain("replace"));
        Assert.That(Json(viaComposite), Is.EqualTo(Json(viaCli)));
        AssertDeclinedToTheCli(composite);
    }

    // ---- GetMergeHead ----

    [Test]
    public void GetMergeHead_NoMergeInProgress_IsNullFromBothReaders_AndServedByLibgit2()
    {
        var repo = NewRepo("nomerge");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetMergeHead(repo, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().GetMergeHead(repo, Timeout), Is.Null);
            Assert.That(composite.GetMergeHead(repo, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetMergeHead_ConflictedMerge_NamesTheMergedCommit_BothReadersAgree_AndLibgit2Serves()
    {
        var repo = NewRepo("conflicted");
        var other = StartConflictedMerge(repo);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetMergeHead(repo, Timeout), Is.EqualTo(other));
            Assert.That(new Libgit2GitReader().GetMergeHead(repo, Timeout), Is.EqualTo(other));
            Assert.That(composite.GetMergeHead(repo, Timeout), Is.EqualTo(other));
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetMergeHead_ConflictedMergeInALinkedWorktree_IsPerWorktree_BothReadersAgree_AndLibgit2Serves()
    {
        var main = NewRepo("main");
        var worktree = AddLinkedWorktree(main, "linked");
        var other = StartConflictedMerge(worktree);
        var composite = Composite();

        var results = new[] { main, worktree }.Select(directory => (
            Cli: new CliGitReader().GetMergeHead(directory, Timeout),
            Lib: new Libgit2GitReader().GetMergeHead(directory, Timeout),
            Composite: composite.GetMergeHead(directory, Timeout))).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(results[0], Is.EqualTo((Cli: (string?)null, Lib: (string?)null, Composite: (string?)null)), "the main working tree has no merge in progress");
            Assert.That(results[1], Is.EqualTo((Cli: (string?)other, Lib: (string?)other, Composite: (string?)other)), "the linked worktree's own MERGE_HEAD");
        });
        AssertServedByLibgit2(composite, calls: 2);
    }

    [Test]
    public void GetMergeHead_FileListingTwoCommits_TakesTheFirst_BothReadersAgree_AndLibgit2Serves()
    {
        var repo = NewRepo("octopus");
        var other = StartConflictedMerge(repo);
        var head = Head(repo);
        File.WriteAllText(Path.Combine(repo, ".git", "MERGE_HEAD"), other + "\n" + head + "\n");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetMergeHead(repo, Timeout), Is.EqualTo(other), "git reads the first object name of the file");
            Assert.That(new Libgit2GitReader().GetMergeHead(repo, Timeout), Is.EqualTo(other));
            Assert.That(composite.GetMergeHead(repo, Timeout), Is.EqualTo(other));
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetMergeHead_NamesAnObjectThatDoesNotExist_IsNullFromBothReaders_AndLibgit2Serves()
    {
        var repo = NewRepo("missingobject");
        File.WriteAllText(Path.Combine(repo, ".git", "MERGE_HEAD"), new string('1', 40) + "\n");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetMergeHead(repo, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().GetMergeHead(repo, Timeout), Is.Null);
            Assert.That(composite.GetMergeHead(repo, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetMergeHead_NamesAnAnnotatedTag_Libgit2Declines_AndTheCliAnswersWithThePeeledCommit()
    {
        var repo = NewRepo("tagobject");
        var commit = Head(repo);
        RunGit(repo, "tag", "-a", "v1", "-m", "tag message");
        var tagObject = RunGit(repo, "rev-parse", "v1");
        File.WriteAllText(Path.Combine(repo, ".git", "MERGE_HEAD"), tagObject + "\n");
        var composite = Composite();

        var viaCli = new CliGitReader().GetMergeHead(repo, Timeout);
        var thrown = Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().GetMergeHead(repo, Timeout));
        var viaComposite = composite.GetMergeHead(repo, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(tagObject, Is.Not.EqualTo(commit), "premise: the tag is an object of its own");
            Assert.That(viaCli, Is.EqualTo(commit), "git peels the tag to its commit");
            Assert.That(thrown!.Message, Does.Contain("not a commit"));
            Assert.That(viaComposite, Is.EqualTo(viaCli));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetMergeHead_FileIsASymbolicRef_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("symbolic");
        var branch = RunGit(repo, "rev-parse", "--abbrev-ref", "HEAD");
        File.WriteAllText(Path.Combine(repo, ".git", "MERGE_HEAD"), "ref: refs/heads/" + branch + "\n");
        var composite = Composite();

        var viaCli = new CliGitReader().GetMergeHead(repo, Timeout);
        var thrown = Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().GetMergeHead(repo, Timeout));
        var viaComposite = composite.GetMergeHead(repo, Timeout);

        Assert.That(thrown!.Message, Does.Contain("plain"));
        Assert.That(viaComposite, Is.EqualTo(viaCli));
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetMergeHead_NoFileButATagNamedMergeHead_Libgit2Declines_AndTheCliResolvesTheTag()
    {
        var repo = NewRepo("competingtag");
        var commit = Head(repo);
        RunGit(repo, "tag", "MERGE_HEAD");
        var composite = Composite();

        var viaCli = new CliGitReader().GetMergeHead(repo, Timeout);
        var thrown = Assert.Throws<NotSupportedException>(() => new Libgit2GitReader().GetMergeHead(repo, Timeout));
        var viaComposite = composite.GetMergeHead(repo, Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(viaCli, Is.EqualTo(commit), "premise: git's ref-name rules turn the tag into a 'MERGE_HEAD'; declining is what keeps that identical");
            Assert.That(thrown!.Message, Does.Contain("named MERGE_HEAD"));
            Assert.That(viaComposite, Is.EqualTo(viaCli));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetMergeHead_FileAndATagNamedMergeHead_TheFileWinsInBothReaders_AndLibgit2Serves()
    {
        var repo = NewRepo("filewins");
        var other = StartConflictedMerge(repo);
        RunGit(repo, "tag", "MERGE_HEAD", Head(repo));
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetMergeHead(repo, Timeout), Is.EqualTo(other), "git tries $GIT_DIR/MERGE_HEAD before refs/tags/MERGE_HEAD");
            Assert.That(new Libgit2GitReader().GetMergeHead(repo, Timeout), Is.EqualTo(other));
            Assert.That(composite.GetMergeHead(repo, Timeout), Is.EqualTo(other));
        });
        AssertServedByLibgit2(composite);
    }

    // ---- the Git tool: commit and revert DTOs are the same on the composite reader and the CLI-only reader ----

    // The tool call this test plays: the echoed call id (the trailer's value) and an agent id.
    private static void AsCall(string callId, string agentId)
    {
        var context = new ToolCallContext(agentId, null, "Git", null, 0);
        context.AssignToolCallId(callId);
        ToolCallContext.Set(context);
    }

    // Twin repositories differ in every hash and in the author date git prints on a revert's summary line.
    internal static string MaskHashes(string text) => Regex.Replace(Regex.Replace(text, "[0-9a-f]{7,40}", "<hash>"), "Date:[^\\r\\n]*", "Date: <date>");

    /// <summary>What a commit left behind, with the hashes of twin repositories made comparable: the object type, the parents (named when they are the old HEAD or MERGE_HEAD), who HEAD is, the files and the message.</summary>
    internal static string Facts(string directory, string hash, IReadOnlyDictionary<string, string> named)
    {
        if (hash.Length == 0)
        {
            return "no hash";
        }

        var parents = RunGit(directory, "rev-list", "--parents", "-n", "1", hash).Split(' ').Skip(1).Select(parent => named.GetValueOrDefault(parent, "other"));
        var files = RunGit(directory, "-c", "core.quotePath=false", "show", "--no-renames", "--name-only", "--format=", hash).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Order();
        var head = Head(directory) == hash ? "own" : "moved";
        return $"{RunGit(directory, "cat-file", "-t", hash)}|parents={string.Join(',', parents)}|head={head}|files={string.Join(',', files)}|message={MaskHashes(RunGit(directory, "log", "-1", "--format=%B", hash))}";
    }

    internal static string CommitJson(GitCommitResult result) =>
        Json(result with { CommitHash = result.CommitHash.Length == 40 ? "<hash>" : result.CommitHash });

    /// <summary>
    /// Plays one scenario against a fresh repository with <paramref name="reader"/> behind a <see cref="GitImpl"/> and returns the normalized DTO and the facts about the new commit.
    /// Every scenario ends with the commit (or revert) the Git tool makes; the arrange steps use plain git.
    /// </summary>
    private async Task<(string Json, string Facts)> RunScenarioAsync(string scenario, IGitReader reader)
    {
        var impl = new GitImpl(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, reader);
        var name = scenario + (reader is FallbackGitReader ? "-composite" : "-cli");
        var trailers = scenario.Contains("trailers", StringComparison.Ordinal);
        if (trailers)
        {
            AsCall("T-" + scenario, "agent-1");
        }

        GitCommitResult commit;
        string directory;
        var named = new Dictionary<string, string>();
        switch (scenario.Replace("-trailers", string.Empty, StringComparison.Ordinal))
        {
            case "plain":
                directory = NewRepo(name, trailers: trailers);
                Write(directory, "a.txt", "changed\n");
                named[Head(directory)] = "before";
                commit = await impl.CommitAsync(directory, "plain subject\n\nplain body", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "untracked":
                directory = NewRepo(name, trailers: trailers);
                Write(directory, "new.txt", "new\n");
                named[Head(directory)] = "before";
                commit = await impl.CommitAsync(directory, "add an untracked file", GitStageScope.listed, "new.txt", amend: false, CancellationToken.None);
                break;
            case "amend-keep":
                directory = NewRepo(name, trailers: trailers);
                Write(directory, "a.txt", "second\n");
                CommitAll(directory, "second subject\n\nsecond body\n\nCo-Authored-By: Pat <pat@example.com>");
                Write(directory, "a.txt", "amended\n");
                named[RunGit(directory, "rev-parse", "HEAD~1")] = "grandparent";
                commit = await impl.CommitAsync(directory, null, GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "amend-message":
                directory = NewRepo(name, trailers: trailers);
                Write(directory, "a.txt", "second\n");
                CommitAll(directory, "second");
                Write(directory, "a.txt", "amended\n");
                named[RunGit(directory, "rev-parse", "HEAD~1")] = "grandparent";
                commit = await impl.CommitAsync(directory, "amended subject", GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "unborn":
                directory = NewRepo(name, initialCommit: false, trailers: trailers);
                Write(directory, "a.txt", "first\n");
                commit = await impl.CommitAsync(directory, "first ever commit", GitStageScope.all, null, amend: false, CancellationToken.None);
                break;
            case "detached":
                directory = NewRepo(name, trailers: trailers);
                RunGit(directory, "checkout", "--quiet", "--detach");
                Write(directory, "a.txt", "detached\n");
                named[Head(directory)] = "before";
                commit = await impl.CommitAsync(directory, "detached commit", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "merge":
                directory = NewRepo(name, trailers: trailers);
                var other = StartConflictedMerge(directory);
                Write(directory, "a.txt", "resolved\n");
                RunGit(directory, "add", "a.txt");
                named[Head(directory)] = "before";
                named[other] = "merge-head";
                commit = await impl.CommitAsync(directory, "merge resolution", null, null, amend: false, CancellationToken.None);
                break;
            case "foreign-hook":
                directory = NewRepo(name, trailers: trailers);
                File.WriteAllText(Path.Combine(directory, ".git", "hooks", "post-commit"), ForeignCommitHook.ReplaceLineEndings("\n") + "\n");
                Write(directory, "a.txt", "own\n");
                named[Head(directory)] = "before";
                commit = await impl.CommitAsync(directory, "own commit", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "linked-worktree":
                var main = NewRepo(name + "-main", trailers: trailers);
                directory = AddLinkedWorktree(main, name);
                Write(directory, "a.txt", "in the worktree\n");
                named[Head(directory)] = "before";
                commit = await impl.CommitAsync(directory, "worktree commit", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "revert":
                directory = NewRepo(name, trailers: trailers);
                Write(directory, "a.txt", "to be reverted\n");
                var toRevert = CommitAll(directory, "change to revert");
                named[toRevert] = "before";
                var revert = await impl.RevertAsync(directory, toRevert, noCommit: false, mainline: null, CancellationToken.None);
                Assert.That(revert.IsError, Is.False, revert.Error);
                return (Json(revert with { CommitHash = revert.CommitHash.Length == 40 ? "<hash>" : revert.CommitHash, Message = MaskHashes(revert.Message) }), Facts(directory, revert.CommitHash, named));
            default:
                throw new ArgumentException("no scenario " + scenario);
        }

        Assert.That(commit.IsError, Is.False, commit.Error);
        Assert.That(commit.CommitHash, Has.Length.EqualTo(40), "the new commit's hash is established");
        return (CommitJson(commit), Facts(directory, commit.CommitHash, named));
    }

    private static readonly string[] ToolScenarios =
    [
        "plain", "plain-trailers", "untracked", "untracked-trailers", "amend-keep", "amend-keep-trailers", "amend-message", "amend-message-trailers",
        "unborn", "unborn-trailers", "detached", "detached-trailers", "merge", "merge-trailers", "foreign-hook", "foreign-hook-trailers",
        "linked-worktree", "linked-worktree-trailers", "revert", "revert-trailers",
    ];

    [TestCaseSource(nameof(ToolScenarios))]
    public async Task GitTool_CommitAndRevert_CompositeReaderEqualsTheCliOnlyReader_AndLibgit2StaysHealthyAsync(string scenario)
    {
        var viaCli = await RunScenarioAsync(scenario, new CliGitReader());
        var composite = Composite();
        var viaComposite = await RunScenarioAsync(scenario, composite);

        Assert.Multiple(() =>
        {
            Assert.That(viaComposite.Json, Is.EqualTo(viaCli.Json), "tool DTO differs between the composite and the CLI-only reader");
            Assert.That(viaComposite.Facts, Is.EqualTo(viaCli.Facts), "the repository state the commit left differs");
            Assert.That(composite.Libgit2Count, Is.GreaterThan(0), "libgit2 served part of the flow");
            Assert.That(composite.CliFallbackCount, Is.Zero, "no real libgit2 failure: " + composite.FirstFallbackReason);
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(composite.DeclineCount, Is.Zero, "nothing in these ordinary repositories is declined: " + composite.LastDeclineReason);
        });
    }

    [Test]
    public async Task GitTool_Merge_RecordsTheTwoParents_AndThePostCommitHookScenarioKeepsTheOwnCommitAsync()
    {
        var merge = await RunScenarioAsync("merge", Composite());
        var hook = await RunScenarioAsync("foreign-hook", Composite());

        Assert.Multiple(() =>
        {
            Assert.That(merge.Facts, Does.Contain("parents=before,merge-head"), "the commit concludes the merge: old HEAD first, MERGE_HEAD second");
            Assert.That(hook.Facts, Does.Contain("head=moved"), "premise: the hook moved HEAD onto a foreign commit, so a HEAD readback would be wrong");
            Assert.That(hook.Facts, Does.Contain("parents=before").And.Contain("message=own commit"));
        });
    }
}
