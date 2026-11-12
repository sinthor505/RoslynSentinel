using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The in-process commit route of <c>GitImpl.CommitAsync</c> (step 5c-4 of proposal_libgit2sharp_in_process_git.md), switched on per repository by git config <c>roslynsentinel.inProcessCommit=true</c>
/// (default off). Taken: a whole-index commit and a commit of listed paths (tracked, untracked, deleted, with trailers) leave the same hash, message bytes, tree, index, status and remaining-staged list
/// as the CLI route does in a twin repository, an fsck-clean repository, the reflog entry <c>commit: &lt;subject&gt;</c> on HEAD and the branch, a post-commit hook that runs, and no git process at all
/// (git's trace2 log). Declined to the CLI, each counted with its reason on the route (<see cref="InProcessCommitRoute.DeclineCount"/>): a blocking hook, an amend, a merge in progress, signing, a filter
/// attribute, a branch that moved while the commit was built. Off: the route is not entered, its counters do not move and the CLI commits.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[NonParallelizable] // GIT_TRACE2_EVENT is a process-wide variable: no other fixture's git process may start while a trace window is open.
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitInProcessCommitRouteTests
{
    private string _root = null!;
    private readonly List<string> _traceFiles = [];

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-ipc-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        foreach (var traceFile in _traceFiles)
        {
            try
            {
                File.Delete(traceFile);
            }
            catch (IOException)
            {
                // A git process that inherited the variable may still hold it; a small temp file is harmless.
            }
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

    private static GitImpl NewImpl(InProcessCommitRoute route) =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, Composite(), route);

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A repository with its config pinned repo-locally (identity, no autocrlf, no signing, no gc, hooks only from its own .git/hooks), one commit with README.md and a.txt, and the route switched on or not.</summary>
    private string NewRepo(string name, bool inProcess, bool trailers = false)
    {
        var directory = Path.Combine(_root, name);
        InitRepo(directory);
        RunGit(directory, "config", "core.autocrlf", "false");
        RunGit(directory, "config", "user.name", "Test");
        RunGit(directory, "config", "user.email", "test@example.com");
        RunGit(directory, "config", "commit.gpgsign", "false");
        RunGit(directory, "config", "gc.auto", "0");
        RunGit(directory, "config", "maintenance.auto", "false");
        var hooks = Path.Combine(directory, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        RunGit(directory, "config", "core.hooksPath", hooks.Replace('\\', '/'));
        if (inProcess)
        {
            RunGit(directory, "config", InProcessCommitRoute.ConfigKey, "true");
        }

        if (trailers)
        {
            RunGit(directory, "config", GitCommitTrailers.ConfigKey, "true");
        }

        Write(directory, "README.md", "hello\n");
        Write(directory, "a.txt", "a\n");
        CommitAll(directory, "initial commit");
        return directory;
    }

    private static string Head(string directory) => RunGit(directory, "rev-parse", "HEAD");

    private static string BranchName(string directory) => RunGit(directory, "rev-parse", "--abbrev-ref", "HEAD");

    private static string Lines(string text) => text.ReplaceLineEndings(",");

    /// <summary>The commit exists and is HEAD, whichever route made it.</summary>
    private static void AssertCommitted(string directory, GitCommitResult result)
    {
        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.CommitHash, Is.EqualTo(Head(directory)), "the reported hash is `git rev-parse HEAD`");
    }

    /// <summary>The route handed exactly one commit to the CLI and made none itself; its reason names <paramref name="fragment"/>.</summary>
    private static void AssertDeclined(InProcessCommitRoute route, string fragment)
    {
        Assert.Multiple(() =>
        {
            Assert.That(route.TakenCount, Is.Zero, "nothing was committed in process");
            Assert.That(route.DeclineCount, Is.EqualTo(1), "last decline: " + route.LastDeclineReason);
            Assert.That(route.LastDeclineReason, Does.Contain(fragment).IgnoreCase);
        });
    }

    private static void AssertTaken(InProcessCommitRoute route)
    {
        Assert.Multiple(() =>
        {
            Assert.That(route.TakenCount, Is.EqualTo(1), "the route should have committed (last decline: " + route.LastDeclineReason + ")");
            Assert.That(route.DeclineCount, Is.Zero, "last decline: " + route.LastDeclineReason);
        });
    }

    private static string[] ReadCommitRouteLog(string directory) =>
        File.ReadAllLines(Path.Combine(directory, ".git", "roslynsentinel", "commit-route.jsonl"));

    // ---- parity with the CLI route ----

    /// <summary>Scenario name, and whether the route is expected to commit it itself (null: either route may, the facts must still be equal).</summary>
    private static readonly (string Name, bool? ExpectTaken)[] ParityScenarios =
    [
        ("whole-index", true), ("messy-message", true), ("listed-tracked", true), ("listed-untracked", true), ("listed-delete", null), ("listed-trailers", true),
        ("crlf-autocrlf", true), ("crlf-text-auto", true), ("mixed-eol-autocrlf", null), ("lone-cr-autocrlf", null),
    ];

    private static IEnumerable<TestCaseData> ParityCases() =>
        ParityScenarios.Select(entry => new TestCaseData(entry.Name, entry.ExpectTaken).SetName("InProcessCommit_" + entry.Name + "_EqualsTheCliRoute"));

    /// <summary>Plays one scenario in a fresh repository, with the route on or off, and returns what the repository and the tool's DTO hold afterwards (the hash itself differs between twins by timestamp).</summary>
    private async Task<(string Facts, InProcessCommitRoute Route, string Directory)> RunScenarioAsync(string scenario, bool inProcess)
    {
        var route = new InProcessCommitRoute();
        var impl = NewImpl(route);
        var directory = NewRepo(scenario + (inProcess ? "-ip" : "-cli"), inProcess, trailers: scenario.Contains("trailers", StringComparison.Ordinal));
        GitCommitResult result;
        switch (scenario)
        {
            case "whole-index":
                Write(directory, "a.txt", "changed\n");
                Write(directory, "b.txt", "new\n");
                RunGit(directory, "add", "a.txt", "b.txt");
                result = await impl.CommitAsync(directory, "whole index subject\n\nbody line one\nbody line two", null, null, amend: false, CancellationToken.None);
                break;
            case "messy-message":
                Write(directory, "a.txt", "changed\n");
                RunGit(directory, "add", "a.txt");
                result = await impl.CommitAsync(directory, "subject with trailing spaces   \n\n\n\nbody\t\n\n\n", null, null, amend: false, CancellationToken.None);
                break;
            case "listed-tracked":
                // Only a.txt is committed: README.md stays staged and d.txt stays modified in the working tree, exactly as `git commit --only` leaves them.
                Write(directory, "d.txt", "d\n");
                CommitAll(directory, "add d");
                Write(directory, "a.txt", "a changed\n");
                Write(directory, "d.txt", "d changed\n");
                Write(directory, "README.md", "readme staged\n");
                RunGit(directory, "add", "README.md");
                result = await impl.CommitAsync(directory, "only a", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "listed-untracked":
                Write(directory, "a.txt", "a changed\n");
                Write(directory, "new.txt", "new\n");
                result = await impl.CommitAsync(directory, "a and new", GitStageScope.listed, "a.txt,new.txt", amend: false, CancellationToken.None);
                break;
            case "listed-delete":
                File.Delete(Path.Combine(directory, "a.txt"));
                result = await impl.CommitAsync(directory, "remove a", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "listed-trailers":
                // The same call id and agent for both routes, so the trailer lines are comparable byte for byte.
                var context = new ToolCallContext("agent-7", null, "Git", null, 0);
                context.AssignToolCallId("0123456789ab");
                ToolCallContext.Set(context);
                Write(directory, "a.txt", "a changed\n");
                result = await impl.CommitAsync(directory, "trailer subject\n\ntrailer body", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "crlf-autocrlf":
                RunGit(directory, "config", "core.autocrlf", "true");
                Write(directory, "a.txt", "one\r\ntwo\r\n");
                result = await impl.CommitAsync(directory, "crlf with autocrlf", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "crlf-text-auto":
                // The attribute is committed first, so the listed CRLF file is normalised by git's own add, not by the route.
                Write(directory, ".gitattributes", "* text=auto\n");
                CommitAll(directory, "text=auto attributes");
                Write(directory, "a.txt", "one\r\ntwo\r\n");
                result = await impl.CommitAsync(directory, "crlf with text=auto", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "mixed-eol-autocrlf":
                RunGit(directory, "config", "core.autocrlf", "true");
                Write(directory, "a.txt", "a\r\nb\nc\r\n");
                result = await impl.CommitAsync(directory, "mixed eol", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "lone-cr-autocrlf":
                RunGit(directory, "config", "core.autocrlf", "true");
                Write(directory, "a.txt", "a\rb\n");
                result = await impl.CommitAsync(directory, "lone cr", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            default:
                throw new ArgumentException("no scenario " + scenario);
        }

        Assert.That(result.IsError, Is.False, result.Error);
        return (Facts(directory, result), route, directory);
    }

    private static string Facts(string directory, GitCommitResult result)
    {
        var stored = RunGitRaw(directory, "log", "-1", "--format=%B", "HEAD").StdOut.ReplaceLineEndings("\n");
        var parents = RunGit(directory, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length - 1;
        return string.Join(
            "|",
            $"hashIsHead={result.CommitHash == Head(directory)}",
            $"message={result.Message.ReplaceLineEndings("\n")}",
            $"remaining={string.Join(",", result.RemainingStaged)}",
            $"stored={stored}",
            $"tree={RunGit(directory, "rev-parse", "HEAD^{tree}")}",
            $"parents={parents}",
            $"subjects={Lines(RunGit(directory, "log", "--format=%s"))}",
            $"changed={Lines(RunGit(directory, "show", "--name-status", "--format=", "HEAD"))}",
            $"index={Lines(RunGit(directory, "ls-files", "-s"))}",
            $"status={Lines(RunGit(directory, "status", "--porcelain"))}");
    }

    [TestCaseSource(nameof(ParityCases))]
    public async Task InProcessCommit_ScenarioEqualsTheCliRoute_InHashMessageTreeIndexAndStatusAsync(string scenario, bool? expectTaken)
    {
        var viaCli = await RunScenarioAsync(scenario, inProcess: false);
        var viaRoute = await RunScenarioAsync(scenario, inProcess: true);

        Assert.Multiple(() =>
        {
            Assert.That(viaRoute.Facts, Is.EqualTo(viaCli.Facts), "the repository and DTO differ between the in-process route and the CLI route");
            Assert.That(viaCli.Route.TakenCount, Is.Zero, "route off: nothing in process");
            Assert.That(viaCli.Route.DeclineCount, Is.Zero, "route off: nothing counted");
            Assert.That(viaCli.Route.LastDeclineReason, Is.Null);
            if (expectTaken == true)
            {
                AssertTaken(viaRoute.Route);
            }
        });
        AssertFsckClean(viaRoute.Directory);
    }

    private static void AssertFsckClean(string directory) => CasCommitTestSupport.AssertFsckClean(directory);

    [Test]
    public async Task InProcessCommit_WritesTheReflogEntryGitWrites_ForHeadAndTheBranchAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("reflog", inProcess: true);
        Write(directory, "a.txt", "changed\n");
        RunGit(directory, "add", "a.txt");

        var result = await NewImpl(route).CommitAsync(directory, "reflog subject\n\nbody", null, null, amend: false, CancellationToken.None);

        AssertCommitted(directory, result);
        AssertTaken(route);
        Assert.Multiple(() =>
        {
            Assert.That(CasCommitTestSupport.Reflog(directory, "HEAD")[0], Is.EqualTo("Test|test@example.com|commit: reflog subject"));
            Assert.That(CasCommitTestSupport.Reflog(directory, "refs/heads/" + BranchName(directory))[0], Is.EqualTo("Test|test@example.com|commit: reflog subject"));
        });
    }

    // ---- no git process, post-commit hook ----

    private static readonly SemaphoreSlim TraceGate = new(1, 1);

    /// <summary>
    /// Runs <paramref name="run"/> with git's trace2 event log on (<c>GIT_TRACE2_EVENT</c>, inherited by every git process the call starts) and returns the command name of every TOP-LEVEL git process that
    /// ran in <paramref name="repoDirectory"/>, in start order (nested ones, such as a hook's git, have a session id with a slash and are not counted: the question is which git processes the server starts).
    /// Same technique as GitCommitSeamParityTests.
    /// </summary>
    private async Task<List<string>> SpawnedGitCommandsAsync(string repoDirectory, Func<Task> run)
    {
        var traceFile = Path.Combine(Path.GetTempPath(), "rs-ipc-" + Guid.NewGuid().ToString("N") + ".trace2");
        _traceFiles.Add(traceFile);
        await TraceGate.WaitAsync();
        try
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", traceFile);
            await run();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
            TraceGate.Release();
        }

        // git writes the file when its first process starts: no file means no git process ran at all.
        if (!File.Exists(traceFile))
        {
            return [];
        }

        var directoryName = Path.GetFileName(repoDirectory);
        var order = new List<string>();
        var worktrees = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = new FileStream(traceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("sid", out var sidElement) || !root.TryGetProperty("event", out var eventElement))
                {
                    continue;
                }

                var sid = sidElement.GetString()!;
                switch (eventElement.GetString())
                {
                    case "start":
                        order.Add(sid);
                        break;
                    case "def_repo" when root.TryGetProperty("worktree", out var worktree):
                        worktrees[sid] = worktree.GetString()!.Replace('\\', '/');
                        break;
                    case "cmd_name":
                        names[sid] = root.GetProperty("name").GetString()!;
                        break;
                }
            }
        }

        return [.. order
            .Where(sid => !sid.Contains('/') && worktrees.TryGetValue(sid, out var worktree) && worktree.EndsWith("/" + directoryName, StringComparison.OrdinalIgnoreCase))
            .Select(sid => names.GetValueOrDefault(sid, "?"))];
    }

    [TestCase(false, TestName = "InProcessCommit_WholeIndex_StartsNoGitProcessAsync")]
    [TestCase(true, TestName = "InProcessCommit_ListedPaths_StartsNoGitProcessAsync")]
    public async Task InProcessCommit_StartsNoGitProcessAtAllAsync(bool listed)
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("nospawn-" + (listed ? "l" : "w"), inProcess: true);
        Write(directory, "a.txt", "changed\n");
        if (!listed)
        {
            RunGit(directory, "add", "a.txt");
        }

        GitCommitResult? result = null;
        var impl = NewImpl(route);
        var spawned = await SpawnedGitCommandsAsync(directory, async () =>
            result = await impl.CommitAsync(directory, "no process", listed ? GitStageScope.listed : null, listed ? "a.txt" : null, amend: false, CancellationToken.None));

        AssertCommitted(directory, result!);
        AssertTaken(route);
        Assert.That(spawned, Is.Empty, "every read, stage and the commit itself are answered in process");
    }

    [Test]
    public async Task InProcessCommit_RunsThePostCommitHook_UnawaitedAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("posthook", inProcess: true);
        // Git runs a hook in the work tree root, so the relative path lands in .git.
        File.WriteAllText(Path.Combine(directory, ".git", "hooks", "post-commit"), "#!/bin/sh\necho done > .git/post-commit-marker\n");
        Write(directory, "a.txt", "changed\n");
        RunGit(directory, "add", "a.txt");

        var result = await NewImpl(route).CommitAsync(directory, "with a post-commit hook", null, null, amend: false, CancellationToken.None);

        AssertCommitted(directory, result);
        AssertTaken(route);
        var marker = Path.Combine(directory, ".git", "post-commit-marker");
        Assert.That(SpinWait.SpinUntil(() => File.Exists(marker), TimeSpan.FromSeconds(15)), Is.True, "the post-commit hook did not run within 15 seconds");
    }

    // ---- declines to the CLI ----

    [Test]
    public async Task InProcessCommit_ABlockingHookExists_DeclinesToTheCli_AndTheHookRunsAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("hook", inProcess: true);
        File.WriteAllText(Path.Combine(directory, ".git", "hooks", "pre-commit"), "#!/bin/sh\necho ran > .git/pre-commit-ran\nexit 0\n");
        Write(directory, "a.txt", "changed\n");

        var result = await NewImpl(route).CommitAsync(directory, "hooked", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);

        AssertCommitted(directory, result);
        AssertDeclined(route, "pre-commit");
        Assert.That(File.Exists(Path.Combine(directory, ".git", "pre-commit-ran")), Is.True, "the CLI commit ran the hook");
    }

    [Test]
    public async Task InProcessCommit_Amend_DeclinesToTheCliAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("amend", inProcess: true);
        Write(directory, "a.txt", "changed\n");

        var result = await NewImpl(route).CommitAsync(directory, "amended subject", GitStageScope.all, null, amend: true, CancellationToken.None);

        AssertCommitted(directory, result);
        AssertDeclined(route, "amend");
        Assert.Multiple(() =>
        {
            Assert.That(RunGit(directory, "rev-list", "--count", "HEAD"), Is.EqualTo("1"), "the amend replaced the initial commit");
            Assert.That(RunGit(directory, "log", "-1", "--format=%s"), Is.EqualTo("amended subject"));
        });
    }

    [Test]
    public async Task InProcessCommit_MergeInProgress_DeclinesToTheCli_AndTheCliConcludesTheMergeAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("merge", inProcess: true);
        var branch = BranchName(directory);
        RunGit(directory, "checkout", "--quiet", "-b", "other");
        Write(directory, "a.txt", "other\n");
        CommitAll(directory, "other change");
        RunGit(directory, "checkout", "--quiet", branch);
        Write(directory, "a.txt", "main\n");
        CommitAll(directory, "main change");
        var merge = RunGitRaw(directory, "merge", "--no-edit", "other");
        Assert.That(merge.ExitCode, Is.EqualTo(1), "premise: the merge stops on a conflict. " + merge.StdOut + merge.StdErr);
        Write(directory, "a.txt", "resolved\n");
        RunGit(directory, "add", "a.txt");

        var result = await NewImpl(route).CommitAsync(directory, "conclude the merge", null, null, amend: false, CancellationToken.None);

        AssertCommitted(directory, result);
        AssertDeclined(route, "merge");
        Assert.That(RunGit(directory, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length - 1, Is.EqualTo(2), "a merge commit has two parents");
    }

    [Test]
    public async Task InProcessCommit_GpgSignEnabled_DeclinesToTheCli_WhichIsTheOneThatTriesToSignAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("sign", inProcess: true);
        RunGit(directory, "config", "commit.gpgsign", "true");
        // No such program: the CLI commit fails fast (that failure is what proves the CLI, not this route, was asked to sign) and nothing real is ever signed.
        RunGit(directory, "config", "gpg.program", "rs-no-such-gpg-program");
        Write(directory, "a.txt", "changed\n");
        var before = Head(directory);

        GitCommitResult? result = null;
        var impl = NewImpl(route);
        var spawned = await SpawnedGitCommandsAsync(directory, async () =>
            result = await impl.CommitAsync(directory, "signed?", GitStageScope.all, null, amend: false, CancellationToken.None));

        AssertDeclined(route, "gpgsign");
        Assert.Multiple(() =>
        {
            Assert.That(spawned, Does.Contain("commit"), "the commit was left to the git CLI");
            if (result!.IsError)
            {
                Assert.That(Head(directory), Is.EqualTo(before), "the failed signing left HEAD alone");
            }
        });
    }

    [Test]
    public async Task InProcessCommit_AFilterAttributeApplies_DeclinesToTheCliAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("filter", inProcess: true);
        // An undefined, not-required filter driver does nothing to git itself; the route must still refuse to reason about it.
        File.WriteAllText(Path.Combine(directory, ".gitattributes"), "*.txt filter=rs-test-filter\n");
        Write(directory, "a.txt", "changed\n");

        var result = await NewImpl(route).CommitAsync(directory, "filtered", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);

        AssertCommitted(directory, result);
        AssertDeclined(route, "filter");
    }

    [Test]
    public async Task InProcessCommit_AListedCrlfFile_WithAutocrlf_IsCommittedInProcess_AndStoresTheLfBlobAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("crlf", inProcess: true);
        RunGit(directory, "config", "core.autocrlf", "true");
        File.WriteAllBytes(Path.Combine(directory, "a.txt"), Encoding.ASCII.GetBytes("one\r\ntwo\r\n"));

        var result = await NewImpl(route).CommitAsync(directory, "crlf file", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);

        AssertCommitted(directory, result);
        AssertTaken(route);
        Assert.Multiple(() =>
        {
            Assert.That(RunGitRaw(directory, "cat-file", "-p", "HEAD:a.txt").StdOut.ReplaceLineEndings("\n"), Is.EqualTo("one\ntwo\n"), "git stores the LF blob");
            Assert.That(RunGit(directory, "status", "--porcelain"), Is.Empty, "the working tree matches the index");
        });
    }

    [Test]
    public async Task InProcessCommit_TheBranchMovesWhileTheCommitIsBuilt_DeclinesAndTheCliStillCommitsOnTopAsync()
    {
        var directory = NewRepo("race", inProcess: true);
        var branch = BranchName(directory);
        // Built before the call (commit-tree touches neither the index nor a ref); the seam moves the branch to it after the in-process commit object exists.
        var racer = RunGit(directory, "commit-tree", "-m", "racer", "-p", "HEAD", "HEAD^{tree}");
        var route = new InProcessCommitRoute { BetweenCommitAndRefUpdate = () => RunGit(directory, "update-ref", "refs/heads/" + branch, racer) };
        Write(directory, "a.txt", "changed\n");
        RunGit(directory, "add", "a.txt");

        var result = await NewImpl(route).CommitAsync(directory, "after the race", null, null, amend: false, CancellationToken.None);

        AssertCommitted(directory, result);
        AssertDeclined(route, "branch moved");
        Assert.Multiple(() =>
        {
            Assert.That(Lines(RunGit(directory, "log", "--format=%s", "-3")), Is.EqualTo("after the race,racer,initial commit"), "the CLI commit sits on top of the racing commit");
            Assert.That(RunGit(directory, "show", "--name-only", "--format=", "HEAD"), Is.EqualTo("a.txt"));
        });
        AssertFsckClean(directory);
    }

    // ---- off by default ----

    [Test]
    public async Task InProcessCommit_ConfigAbsent_TheRouteIsNotTaken_NothingIsCounted_AndTheCliCommitsAsync()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("off", inProcess: false);
        Write(directory, "a.txt", "changed\n");

        GitCommitResult? result = null;
        var impl = NewImpl(route);
        var spawned = await SpawnedGitCommandsAsync(directory, async () =>
            result = await impl.CommitAsync(directory, "plain cli commit", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None));

        AssertCommitted(directory, result!);
        Assert.Multiple(() =>
        {
            Assert.That(route.TakenCount, Is.Zero);
            Assert.That(route.DeclineCount, Is.Zero);
            Assert.That(route.LastDeclineReason, Is.Null);
            Assert.That(spawned, Is.EqualTo(new[] { "commit" }), "exactly the CLI commit, as before the route existed");
        });
    }

    // ---- status block ----

    [Test]
    public void GitBackendBlock_ShowsTheInProcessCommitCounters()
    {
        var route = new InProcessCommitRoute();
        route.RecordDecline("a hook exists", NewRepo("counters", inProcess: false));
        var warmup = new GitBackendWarmup(() => "v1", _ => "ok", Composite(), log: null, lengths: null, commitRoute: route);
        warmup.Start();

        var block = warmup.Report(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(block.InProcessCommitCount, Is.Zero);
            Assert.That(block.InProcessCommitDeclineCount, Is.EqualTo(1));
            Assert.That(block.LastInProcessCommitDeclineReason, Is.EqualTo("a hook exists"));
        });
    }

    [Test]
    public void RecordDecline_AppendsADeclinedLine_ToTheCommitRouteLog()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("declined-line", inProcess: false);

        route.RecordDecline("a hook exists", directory);

        var lines = ReadCommitRouteLog(directory);
        Assert.That(lines, Has.Length.EqualTo(1));
        using var json = System.Text.Json.JsonDocument.Parse(lines[0]);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("declined"));
            Assert.That(json.RootElement.GetProperty("reason").GetString(), Is.EqualTo("a hook exists"));
        });
    }

    [Test]
    public void Commit_AppendsATakenLine_WithTheNewCommitSha()
    {
        var route = new InProcessCommitRoute();
        var directory = NewRepo("taken-line", inProcess: true);
        Write(directory, "a.txt", "changed\n");
        RunGit(directory, "add", "a.txt");

        var result = route.Commit(directory, "taken line", null, null, null, out _);

        Assert.That(result.IsCommitted, Is.True, result.Detail);
        var lines = ReadCommitRouteLog(directory);
        Assert.That(lines, Has.Length.EqualTo(1));
        using var json = System.Text.Json.JsonDocument.Parse(lines[0]);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("taken"));
            Assert.That(json.RootElement.GetProperty("commit").GetString(), Is.EqualTo(Head(directory)));
        });
    }
}
