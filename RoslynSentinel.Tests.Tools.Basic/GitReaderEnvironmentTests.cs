using System.Reflection;
using System.Text.Json;
using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// What the libgit2 reader must DECLINE (<see cref="NotSupportedException"/>, counted as a planned decline by <see cref="FallbackGitReader"/>, never a failure) because git sees something
/// libgit2 does not: process environment variables (GIT_DIR, GIT_INDEX_FILE, GIT_CONFIG_*, ...), a split index (libgit2 1.8.6 cannot read its mandatory 'link' extension) and
/// extensions.worktreeConfig with a config.worktree (libgit2 never reads it). Every test walks ALL <see cref="IGitReader"/> methods through a probe table, guarded by reflection so a new
/// method cannot be forgotten: the libgit2-only reader must throw the decline, and the composite must return exactly what the CLI-only reader returns, with
/// <c>DeclineCount &gt; 0</c> and no recorded failure (<c>CliFallbackCount == 0</c>, <c>FirstFallbackReason == null</c>). Controls prove libgit2 still serves the same calls when none of it applies.
/// <para>
/// Not testable here: system-level gitattributes (<c>etc/gitattributes</c> under the git installation) cannot be varied without writing into the machine's git installation, and GIT_ATTR_SOURCE /
/// GIT_ATTR_NOSYSTEM change nothing in a repository without attributes, so those variables are pinned as declines only. Nor is a per-method reason for GIT_OBJECT_DIRECTORY,
/// GIT_CEILING_DIRECTORIES and the like asserted beyond the decline itself: the probe repository has no alternates or ceiling for git to behave differently on.
/// </para>
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// NonParallelizable: the environment tests set process-wide GIT_* variables, which every parallel test's git children (and its libgit2 reader, which now declines on them) would see.
// The fixture saves and restores every variable it touches in TearDown, even on failure, and ignores itself when the host already sets one.
[TestFixture]
[NonParallelizable]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitReaderEnvironmentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // One scenario per variable in Libgit2GitReader.OverridingEnvironmentVariables (a reflection test keeps the two lists equal).
    private static readonly string[] ScenarioNames =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_CEILING_DIRECTORIES", "GIT_DISCOVERY_ACROSS_FILESYSTEM",
        "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "XDG_CONFIG_HOME",
        "GIT_ATTR_SOURCE", "GIT_ATTR_NOSYSTEM",
    ];

    // Every variable a scenario can set (GIT_CONFIG_COUNT also sets a key and a value), saved before and restored after each test.
    private static readonly string[] TouchedVariables = [.. ScenarioNames, "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0"];

    // Scenarios where git's own answer to one probe changes when the variable is set (probed 2026-11-11): the premise that declining is needed at all.
    private static readonly Dictionary<string, string> VisibleProbe = new()
    {
        ["GIT_DIR"] = "GetHeadSha(full)",
        ["GIT_INDEX_FILE"] = "GetStatus",
        ["GIT_CONFIG_GLOBAL"] = "GetBoolConfig(trailers)",
        ["GIT_CONFIG_SYSTEM"] = "GetBoolConfig(trailers)",
        ["GIT_CONFIG_COUNT"] = "GetBoolConfig(trailers)",
        ["GIT_CONFIG_PARAMETERS"] = "GetBoolConfig(trailers)",
        ["XDG_CONFIG_HOME"] = "GetBoolConfig(trailers)",
    };

    // The methods that load the index: the ones a split index must decline (GetNameStatus only for "staged").
    private static readonly string[] IndexReaders =
    [
        "GetStatus", "GetRepositoryStatus", "ListTrackedPaths", "ListIndexPathsMatching", "FindIgnoredPaths", "GetNameStatus(staged)", "HasStagedChanges", "StageWholeFiles", "UnstagePaths",
    ];

    private readonly Dictionary<string, string?> _savedEnvironment = [];
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        foreach (var name in TouchedVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                Assert.Ignore($"{name} is set in the test host's environment; this fixture owns the process environment and needs it unset.");
            }

            _savedEnvironment[name] = value;
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-gitenv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        // First thing, so a failed assertion can never leave a GIT_* variable behind for the next fixture.
        foreach (var (name, value) in _savedEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
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

    // ---- probe table ----

    /// <summary>One call per <see cref="IGitReader"/> method (two for GetHeadSha, GetNameStatus and GetBoolConfig-free ones as needed), each chosen so the CLI answer is a real one.</summary>
    private sealed record Probe(string Label, string Method, Func<IGitReader, string, object?> Call, bool MayBeNull = false);

    private static (int ExitCode, string StdErr) ExitAndStdErr(GitRunResult run) => (run.ExitCode, run.StdErr);

    private static Probe[] Probes(string firstSha) =>
    [
        new("GetHeadSha(short)", nameof(IGitReader.GetHeadSha), (r, d) => r.GetHeadSha(d, shortForm: true, Timeout)),
        new("GetHeadSha(full)", nameof(IGitReader.GetHeadSha), (r, d) => r.GetHeadSha(d, shortForm: false, Timeout)),
        new("GetCommitsSince", nameof(IGitReader.GetCommitsSince), (r, d) => r.GetCommitsSince(d, firstSha, Timeout)),
        new("GetStatus", nameof(IGitReader.GetStatus), (r, d) => r.GetStatus(d, pathspecs: null, Timeout)),
        new("GetRepositoryStatus", nameof(IGitReader.GetRepositoryStatus), (r, d) => r.GetRepositoryStatus(d, Timeout)),
        new("GetLog", nameof(IGitReader.GetLog), (r, d) => r.GetLog(d, 10, revision: null, paths: null, pickaxe: null, pickaxeRegex: false, follow: false, Timeout)),
        new("ReadBlobAtHead", nameof(IGitReader.ReadBlobAtHead), (r, d) => r.ReadBlobAtHead(d, "a.txt", Timeout)),
        new("ListTrackedPaths", nameof(IGitReader.ListTrackedPaths), (r, d) => r.ListTrackedPaths(d, Timeout)),
        new("ListIndexPathsMatching", nameof(IGitReader.ListIndexPathsMatching), (r, d) => r.ListIndexPathsMatching(d, ["a.txt", "sub"], Timeout)),
        new("ListHeadPathsMatching", nameof(IGitReader.ListHeadPathsMatching), (r, d) => r.ListHeadPathsMatching(d, ["a.txt", "sub"], Timeout)),
        new("FindIgnoredPaths", nameof(IGitReader.FindIgnoredPaths), (r, d) => r.FindIgnoredPaths(d, ["x.log", "a.txt"], Timeout)),
        new("ListBranches", nameof(IGitReader.ListBranches), (r, d) => r.ListBranches(d, Timeout)),
        new("ListStashes", nameof(IGitReader.ListStashes), (r, d) => r.ListStashes(d, Timeout)),
        new("CountStashes", nameof(IGitReader.CountStashes), (r, d) => r.CountStashes(d, Timeout)),
        new("GetNameStatus(range)", nameof(IGitReader.GetNameStatus), (r, d) => r.GetNameStatus(d, firstSha + "..HEAD", paths: null, Timeout)),
        new("GetNameStatus(staged)", nameof(IGitReader.GetNameStatus), (r, d) => r.GetNameStatus(d, "staged", paths: null, Timeout)),
        new("GetCommitNameStatus", nameof(IGitReader.GetCommitNameStatus), (r, d) => r.GetCommitNameStatus(d, "HEAD", paths: null, Timeout)),
        // The raw message differs only in its final newline between the readers (the Git tool trims it); compare it trimmed.
        new("GetCommitHeader", nameof(IGitReader.GetCommitHeader), (r, d) => r.GetCommitHeader(d, "HEAD", Timeout) is { } header ? header with { Message = header.Message.TrimEnd('\r', '\n') } : null),
        new("GetCommitMessages", nameof(IGitReader.GetCommitMessages), (r, d) => r.GetCommitMessages(d, Timeout)),
        new("FindCommitByTrailer", nameof(IGitReader.FindCommitByTrailer), (r, d) => r.FindCommitByTrailer(d, "Tool-Call-Id", "env-1", 50, Timeout)),
        // Null while the opt-in setting is unset: only the scenarios that set it (and the worktree config ones) make it a real answer.
        new("GetBoolConfig(trailers)", nameof(IGitReader.GetBoolConfig), (r, d) => r.GetBoolConfig(d, "roslynsentinel.commitTrailers", Timeout), MayBeNull: true),
        new("GetConfigValue(trailers)", nameof(IGitReader.GetConfigValue), (r, d) => r.GetConfigValue(d, "roslynsentinel.commitTrailers", Timeout), MayBeNull: true),
        new("GetHeadWithParents", nameof(IGitReader.GetHeadWithParents), (r, d) => r.GetHeadWithParents(d, Timeout)),
        // Null while no merge is in progress, which is the fixtures' state: the decline walk still proves libgit2 declines it and the CLI answers.
        new("GetMergeHead", nameof(IGitReader.GetMergeHead), (r, d) => r.GetMergeHead(d, Timeout), MayBeNull: true),
        // The abbreviation of the fixtures' first commit (a real answer); the message is compared as returned (both readers hand back git's %B text without the record's line break).
        new("ResolveCommitAbbreviation", nameof(IGitReader.ResolveCommitAbbreviation), (r, d) => r.ResolveCommitAbbreviation(d, firstSha[..12], Timeout)),
        new("GetCommitMessage", nameof(IGitReader.GetCommitMessage), (r, d) => r.GetCommitMessage(d, "HEAD", Timeout)),
        // Null: the fixtures have no upstream and nothing in progress. HasStagedChanges is a real answer (false) unless the scenario makes git itself fail (GIT_INDEX_FILE naming an unreadable index).
        new("GetUpstreamContainingHead", nameof(IGitReader.GetUpstreamContainingHead), (r, d) => r.GetUpstreamContainingHead(d, Timeout), MayBeNull: true),
        new("GetInProgressOperation", nameof(IGitReader.GetInProgressOperation), (r, d) => r.GetInProgressOperation(d, Timeout), MayBeNull: true),
        new("HasStagedChanges", nameof(IGitReader.HasStagedChanges), (r, d) => r.HasStagedChanges(d, paths: null, Timeout), MayBeNull: true),
        // The two index writes, projected to exit code and stderr (a GitRunResult carries a byte array, which does not compare or serialize usefully). a.txt is modified in the working
        // tree of the fixtures, so staging it is idempotent whichever reader runs first; the unstaged path is in neither the index nor HEAD, so that reset changes nothing.
        new("StageWholeFiles", nameof(IGitReader.StageWholeFiles), (r, d) => ExitAndStdErr(r.StageWholeFiles(d, IndexStageMode.AddListed, ["a.txt"], Timeout, CancellationToken.None))),
        new("UnstagePaths", nameof(IGitReader.UnstagePaths), (r, d) => ExitAndStdErr(r.UnstagePaths(d, ["never-existed.txt"], Timeout, CancellationToken.None))),
    ];

    [Test]
    public void ProbeTable_CoversEveryIGitReaderMethod()
    {
        var interfaceMethods = typeof(IGitReader).GetMethods().Select(method => method.Name).Distinct().Order().ToArray();
        var covered = Probes("0000000").Select(probe => probe.Method).Distinct().Order().ToArray();
        Assert.That(covered, Is.EqualTo(interfaceMethods), "add a Probe row for every IGitReader method");
    }

    [Test]
    public void EveryOverridingVariableInTheReader_HasAScenario()
    {
        var field = typeof(Libgit2GitReader).GetField("OverridingEnvironmentVariables", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null, "Libgit2GitReader.OverridingEnvironmentVariables was renamed or removed");
        var declined = (string[])field!.GetValue(null)!;
        Assert.That(ScenarioNames, Is.EquivalentTo(declined), "a variable added to or removed from the reader's list needs a scenario here");
    }

    // ---- fixtures ----

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static FallbackGitReader Composite() => new(new Libgit2GitReader(), new CliGitReader(), _ => { });

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// A repository (autocrlf and filemode pinned repo-locally) with two commits (the second carries a Tool-Call-Id trailer), a side branch, and a dirty tree: a.txt modified, staged.txt staged,
    /// new.txt untracked, x.log ignored. Returns its directory and the first commit's sha.
    /// </summary>
    private (string Directory, string FirstSha) Fixture(string name)
    {
        var directory = Path.Combine(_root, name);
        InitRepo(directory);
        RunGit(directory, "config", "core.autocrlf", "false");
        RunGit(directory, "config", "core.filemode", "false");
        Write(directory, "a.txt", "one\n");
        Write(directory, "sub/b.txt", "b\n");
        Write(directory, ".gitignore", "*.log\n");
        var first = CommitAll(directory, "first");
        Write(directory, "a.txt", "two\n");
        CommitAll(directory, "second", "Tool-Call-Id: env-1");
        RunGit(directory, "branch", "side");
        Dirty(directory);
        return (directory, first);
    }

    private static void Dirty(string directory)
    {
        Write(directory, "a.txt", "dirty\n");
        Write(directory, "staged.txt", "s\n");
        RunGit(directory, "add", "staged.txt");
        Write(directory, "new.txt", "n\n");
        Write(directory, "x.log", "ignored\n");
    }

    private static string GitDirectory(string workDirectory) => RunGit(workDirectory, "rev-parse", "--absolute-git-dir");

    private static void SetEnvironment(IEnumerable<(string Name, string Value)> variables)
    {
        foreach (var (name, value) in variables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    // ---- the assertions every scenario shares ----

    /// <summary>
    /// Every method declines: the libgit2-only reader throws <see cref="NotSupportedException"/> whose message contains <paramref name="fragment"/>, the composite returns what the CLI-only reader
    /// returns (a real answer, except where <see cref="Probe.MayBeNull"/>), and the counters show declines only: no libgit2 answer, no failure.
    /// </summary>
    private static FallbackGitReader AssertEveryMethodDeclines(string directory, string firstSha, string fragment)
    {
        var probes = Probes(firstSha);
        var libgit2 = new Libgit2GitReader();
        var cli = new CliGitReader();
        var composite = Composite();
        foreach (var probe in probes)
        {
            var thrown = Assert.Throws<NotSupportedException>(() => probe.Call(libgit2, directory), probe.Label + ": libgit2 must decline");
            Assert.That(thrown!.Message, Does.Contain(fragment), probe.Label);
            var viaCli = Json(probe.Call(cli, directory));
            var viaComposite = Json(probe.Call(composite, directory));
            Assert.That(viaComposite, Is.EqualTo(viaCli), probe.Label + ": composite differs from the CLI-only reader");
            if (!probe.MayBeNull)
            {
                Assert.That(viaCli, Is.Not.EqualTo("null"), probe.Label + ": the CLI must have a real answer for the comparison to mean anything");
            }

            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli), probe.Label);
        }

        Assert.Multiple(() =>
        {
            Assert.That(composite.DeclineCount, Is.EqualTo(probes.Length), "every call is a planned decline");
            Assert.That(composite.LastDeclineReason, Does.Contain(fragment));
            Assert.That(composite.CliFallbackCount, Is.Zero, "a decline is not a failure");
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(composite.Libgit2Count, Is.Zero, "libgit2 must not have answered anything");
        });
        return composite;
    }

    /// <summary>Control: libgit2 serves every method with the CLI-only answer, with no decline and no failure.</summary>
    private static void AssertEveryMethodServedByLibgit2(string directory, string firstSha)
    {
        var probes = Probes(firstSha);
        var cli = new CliGitReader();
        var composite = Composite();
        foreach (var probe in probes)
        {
            var viaComposite = Json(probe.Call(composite, directory));
            var viaCli = Json(probe.Call(cli, directory));
            Assert.That(viaComposite, Is.EqualTo(viaCli), probe.Label + ": composite differs from the CLI-only reader");
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2), probe.Label + ": libgit2 should answer this (last decline: " + composite.LastDeclineReason + ")");
        }

        Assert.Multiple(() =>
        {
            Assert.That(composite.Libgit2Count, Is.EqualTo(probes.Length));
            Assert.That(composite.DeclineCount, Is.Zero, "last decline: " + composite.LastDeclineReason);
            Assert.That(composite.CliFallbackCount, Is.Zero);
            Assert.That(composite.FirstFallbackReason, Is.Null);
        });
    }

    // ---- process environment ----

    private IEnumerable<(string Name, string Value)> Variables(string scenario, string e1, string e2)
    {
        switch (scenario)
        {
            case "GIT_DIR":
                // The second repository (a clone of the first with another HEAD, an extra tracked file and an extra staged one): git answers from it, libgit2 from the directory's own.
                return [("GIT_DIR", Path.Combine(e2, ".git"))];
            case "GIT_INDEX_FILE":
                return [("GIT_INDEX_FILE", Path.Combine(e2, ".git", "index"))];
            case "GIT_WORK_TREE":
                return [("GIT_WORK_TREE", e1)];
            case "GIT_COMMON_DIR":
                return [("GIT_COMMON_DIR", Path.Combine(e1, ".git"))];
            case "GIT_OBJECT_DIRECTORY":
                return [("GIT_OBJECT_DIRECTORY", Path.Combine(e1, ".git", "objects"))];
            case "GIT_ALTERNATE_OBJECT_DIRECTORIES":
                var alternate = Path.Combine(_root, "alternate");
                Directory.CreateDirectory(alternate);
                return [("GIT_ALTERNATE_OBJECT_DIRECTORIES", alternate)];
            case "GIT_CEILING_DIRECTORIES":
                return [("GIT_CEILING_DIRECTORIES", _root)];
            case "GIT_DISCOVERY_ACROSS_FILESYSTEM":
                return [("GIT_DISCOVERY_ACROSS_FILESYSTEM", "1")];
            case "GIT_CONFIG_GLOBAL":
                var global = Path.Combine(_root, "global.cfg");
                File.WriteAllText(global, "[roslynsentinel]\n\tcommitTrailers = true\n");
                return [("GIT_CONFIG_GLOBAL", global)];
            case "GIT_CONFIG_SYSTEM":
                var system = Path.Combine(_root, "system.cfg");
                File.WriteAllText(system, "[roslynsentinel]\n\tcommitTrailers = true\n");
                return [("GIT_CONFIG_SYSTEM", system)];
            case "GIT_CONFIG_NOSYSTEM":
                return [("GIT_CONFIG_NOSYSTEM", "1")];
            case "GIT_CONFIG_COUNT":
                return [("GIT_CONFIG_COUNT", "1"), ("GIT_CONFIG_KEY_0", "roslynsentinel.commitTrailers"), ("GIT_CONFIG_VALUE_0", "true")];
            case "GIT_CONFIG_PARAMETERS":
                return [("GIT_CONFIG_PARAMETERS", "'roslynsentinel.committrailers=true'")];
            case "XDG_CONFIG_HOME":
                var xdg = Path.Combine(_root, "xdg");
                Directory.CreateDirectory(Path.Combine(xdg, "git"));
                File.WriteAllText(Path.Combine(xdg, "git", "config"), "[roslynsentinel]\n\tcommitTrailers = true\n");
                return [("XDG_CONFIG_HOME", xdg)];
            case "GIT_ATTR_SOURCE":
                return [("GIT_ATTR_SOURCE", "HEAD")];
            case "GIT_ATTR_NOSYSTEM":
                return [("GIT_ATTR_NOSYSTEM", "1")];
            default:
                throw new ArgumentException("no scenario " + scenario);
        }
    }

    [TestCaseSource(nameof(ScenarioNames))]
    public void EnvironmentVariable_DeclinesEveryReaderMethod_AndTheCliAnswersWithGitsView(string scenario)
    {
        var (e1, firstSha) = Fixture("e1");
        var e2 = Path.Combine(_root, "e2");
        RunGit(_root, "clone", "--quiet", e1, e2);
        RunGit(e2, "config", "core.autocrlf", "false");
        RunGit(e2, "config", "core.filemode", "false");
        Write(e2, "only-in-e2.txt", "x\n");
        CommitAll(e2, "second in e2");
        Write(e2, "staged-in-e2.txt", "y\n");
        RunGit(e2, "add", "staged-in-e2.txt");

        var variables = Variables(scenario, e1, e2).ToArray();
        var cli = new CliGitReader();
        var visible = VisibleProbe.TryGetValue(scenario, out var label) ? Probes(firstSha).Single(probe => probe.Label == label) : null;
        var without = visible is null ? null : Json(visible.Call(cli, e1));

        SetEnvironment(variables);
        try
        {
            if (visible is not null)
            {
                // The premise: the variable changes git's own answer, so a libgit2 answer would be wrong rather than merely different.
                Assert.That(Json(visible.Call(cli, e1)), Is.Not.EqualTo(without), $"{scenario} should change git's answer to {visible.Label}");
            }

            var composite = AssertEveryMethodDeclines(e1, firstSha, variables[0].Name);
            if (scenario is "GIT_CONFIG_GLOBAL" or "GIT_CONFIG_SYSTEM" or "GIT_CONFIG_COUNT" or "GIT_CONFIG_PARAMETERS" or "XDG_CONFIG_HOME")
            {
                Assert.That(composite.GetBoolConfig(e1, "roslynsentinel.commitTrailers", Timeout), Is.True, "the setting only git can see must reach the caller");
            }
        }
        finally
        {
            foreach (var variable in variables)
            {
                Environment.SetEnvironmentVariable(variable.Name, null);
            }
        }
    }

    [Test]
    public void NoOverridingVariable_Libgit2ServesEveryMethod()
    {
        var (directory, firstSha) = Fixture("control");
        AssertEveryMethodServedByLibgit2(directory, firstSha);
    }

    // ---- split index ----

    [TestCase(false)]
    [TestCase(true)]
    public void SplitIndex_DeclinesTheIndexReaders_EveryOtherMethodStaysOnLibgit2(bool linkedWorktree)
    {
        var (main, firstSha) = Fixture("split");
        var directory = main;
        if (linkedWorktree)
        {
            directory = Path.Combine(_root, "split-linked");
            RunGit(main, "worktree", "add", "--quiet", directory, "-b", "linked");
            Dirty(directory);
        }

        // Add a file, split the index (a link extension plus a sharedindex file in THIS worktree's git directory), then change the index again so it has a delta.
        Write(directory, "c.txt", "c\n");
        RunGit(directory, "add", "c.txt");
        RunGit(directory, "update-index", "--split-index");
        Write(directory, "a.txt", "dirty again\n");
        RunGit(directory, "add", "a.txt");
        Write(directory, "a.txt", "dirty once more\n");
        Assert.That(Directory.GetFiles(GitDirectory(directory), "sharedindex.*"), Is.Not.Empty, "premise: the index is split");

        AssertIndexReadersDecline(directory, firstSha);
    }

    [Test]
    public void SplitIndexConfigOn_BeforeAnySharedIndexExists_DeclinesTheIndexReaders()
    {
        var (directory, firstSha) = Fixture("splitconfig");
        RunGit(directory, "config", "core.splitIndex", "true");
        Assert.That(Directory.GetFiles(GitDirectory(directory), "sharedindex.*"), Is.Empty, "premise: nothing has split the index yet");

        AssertIndexReadersDecline(directory, firstSha);
    }

    /// <summary>
    /// Exactly the methods that load the index decline (a split index in the message), each through the libgit2-only reader; every other method is served by libgit2 with the CLI-only answer,
    /// and the composite is never forced into a failure fallback.
    /// </summary>
    private static void AssertIndexReadersDecline(string directory, string firstSha)
    {
        var probes = Probes(firstSha);
        var libgit2 = new Libgit2GitReader();
        var cli = new CliGitReader();
        var composite = Composite();
        foreach (var probe in probes)
        {
            if (IndexReaders.Contains(probe.Label))
            {
                var thrown = Assert.Throws<NotSupportedException>(() => probe.Call(libgit2, directory), probe.Label + ": must decline a split index, not throw a libgit2 failure");
                Assert.That(thrown!.Message, Does.Contain("split"), probe.Label);
            }
            else
            {
                object? served = null;
                Assert.DoesNotThrow(() => served = probe.Call(libgit2, directory), probe.Label + ": never touches the index, so libgit2 serves it");
                Assert.That(Json(served), Is.EqualTo(Json(probe.Call(cli, directory))), probe.Label);
            }

            Assert.That(Json(probe.Call(composite, directory)), Is.EqualTo(Json(probe.Call(cli, directory))), probe.Label + ": composite differs from the CLI-only reader");
        }

        Assert.Multiple(() =>
        {
            Assert.That(composite.DeclineCount, Is.EqualTo(IndexReaders.Length));
            Assert.That(composite.Libgit2Count, Is.EqualTo(probes.Length - IndexReaders.Length));
            Assert.That(composite.CliFallbackCount, Is.Zero, "a split index must be a planned decline, not a LibGit2SharpException failure");
            Assert.That(composite.FirstFallbackReason, Is.Null);
        });
    }

    // ---- extensions.worktreeConfig ----

    [Test]
    public void WorktreeConfig_MainAndLinkedWorktreeWithAConfigWorktree_DeclineEverything()
    {
        var (main, firstSha) = Fixture("wtc");
        RunGit(main, "config", "extensions.worktreeConfig", "true");
        var linked = Path.Combine(_root, "wtc-linked");
        RunGit(main, "worktree", "add", "--quiet", linked, "-b", "linked");
        Dirty(linked);

        // Per-worktree overrides that differ from the shared config: the opt-in setting, an EOL setting and the abbreviation length (the linked worktree's differs from the main one's).
        RunGit(linked, "config", "--worktree", "core.abbrev", "12");
        RunGit(linked, "config", "--worktree", "core.autocrlf", "true");
        RunGit(linked, "config", "--worktree", "roslynsentinel.commitTrailers", "true");
        RunGit(main, "config", "--worktree", "roslynsentinel.commitTrailers", "true");
        RunGit(main, "config", "--worktree", "core.abbrev", "10");
        Assert.That(File.Exists(Path.Combine(GitDirectory(main), "config.worktree")), Is.True, "premise: the main working tree has its own config.worktree");
        Assert.That(File.Exists(Path.Combine(GitDirectory(linked), "config.worktree")), Is.True, "premise: the linked worktree has its own config.worktree");

        foreach (var (directory, abbreviation) in new[] { (main, 10), (linked, 12) })
        {
            var composite = AssertEveryMethodDeclines(directory, firstSha, "config.worktree");
            Assert.That(composite.GetBoolConfig(directory, "roslynsentinel.commitTrailers", Timeout), Is.True, "git sees the per-worktree setting; libgit2 would say unset");
            Assert.That(composite.GetHeadSha(directory, shortForm: true, Timeout)!.Length, Is.EqualTo(abbreviation), "core.abbrev from config.worktree");
        }
    }

    [Test]
    public void WorktreeConfig_ExtensionOnButNoConfigWorktreeFile_Libgit2StillServes()
    {
        var (main, firstSha) = Fixture("wtc-nofile");
        RunGit(main, "config", "extensions.worktreeConfig", "true");
        var linked = Path.Combine(_root, "wtc-nofile-linked");
        RunGit(main, "worktree", "add", "--quiet", linked, "-b", "linked");
        Dirty(linked);
        Assert.That(File.Exists(Path.Combine(GitDirectory(main), "config.worktree")), Is.False, "premise: no per-worktree config in the main working tree");
        Assert.That(File.Exists(Path.Combine(GitDirectory(linked), "config.worktree")), Is.False, "premise: no per-worktree config in the linked worktree");

        AssertEveryMethodServedByLibgit2(main, firstSha);
        AssertEveryMethodServedByLibgit2(linked, firstSha);
    }

    [Test]
    public void WorktreeConfig_FileWithoutTheExtension_GitIgnoresItAndLibgit2Serves()
    {
        var (main, firstSha) = Fixture("wtc-noext");
        File.WriteAllText(Path.Combine(GitDirectory(main), "config.worktree"), "[roslynsentinel]\n\tcommitTrailers = true\n");
        Assert.That(new CliGitReader().GetBoolConfig(main, "roslynsentinel.commitTrailers", Timeout), Is.Null, "premise: without the extension git ignores config.worktree");

        AssertEveryMethodServedByLibgit2(main, firstSha);
    }
}
