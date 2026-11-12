using System.Reflection;
using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;
using static RoslynSentinel.Tests.Tools.Basic.CasCommitTestSupport;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The commit primitive must DECLINE (typed result, nothing written, no index.lock left) while a process environment variable is set that git honours for a commit and libgit2 does not:
/// the identity, date and reflog-action variables of <c>Libgit2GitReader.CommitEnvironmentVariables</c>, and the repository-redirecting ones the reader already declines on
/// (<c>OverridingEnvironmentVariables</c>, probed with GIT_DIR). A reflection test keeps the scenario list equal to the reader's list.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// NonParallelizable: these tests set process-wide GIT_* variables, which every parallel test's git children (and its libgit2 reader, which declines on them) would see.
// The fixture restores every variable it touches in TearDown, even on failure, and ignores itself when the host already sets one.
[TestFixture]
[NonParallelizable]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCasCommitEnvironmentTests
{
    private static readonly string[] ScenarioNames =
    [
        "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_AUTHOR_DATE", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "GIT_COMMITTER_DATE",
        "GIT_REFLOG_ACTION", "GIT_NAMESPACE", "GIT_INDEX_VERSION", "GIT_DIR",
    ];

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        foreach (var name in ScenarioNames)
        {
            if (Environment.GetEnvironmentVariable(name) is not null)
            {
                Assert.Ignore($"{name} is set in the test host's environment; this fixture owns the process environment and needs it unset.");
            }
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-casenv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        // First thing, so a failed assertion can never leave a GIT_* variable behind for the next fixture.
        foreach (var name in ScenarioNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        DeleteTree(_root);
    }

    [Test]
    public void EveryCommitEnvironmentVariableInTheReader_HasAScenario()
    {
        var field = typeof(Libgit2GitReader).GetField("CommitEnvironmentVariables", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(field, Is.Not.Null, "Libgit2GitReader.CommitEnvironmentVariables was renamed or removed.");
        var declared = (string[])field!.GetValue(null)!;

        Assert.That(ScenarioNames.Except(["GIT_DIR"]), Is.EquivalentTo(declared));
    }

    [Test]
    public void EnvironmentVariableSet_DeclinesTheCommit_AndWritesNothing()
    {
        var dir = NewRepo(_root, "env");
        var first = Tip(dir);
        Write(dir, "b.txt", "b\n");
        RunGit(dir, "add", "b.txt");

        Assert.That(Libgit2GitReader.CommitWithCompareAndSwap(dir, "control\n", CasCommitTreeSource.WholeIndex, betweenCommitAndRefUpdate: () => { }).Outcome,
            Is.EqualTo(CasCommitOutcome.Committed), "the control without any variable must commit.");

        foreach (var name in ScenarioNames)
        {
            Write(dir, $"{name}.txt", name);
            RunGit(dir, "add", $"{name}.txt");
            var before = Tip(dir);
            Environment.SetEnvironmentVariable(name, name == "GIT_DIR" ? Path.Combine(_root, "elsewhere") : "1");
            CasCommitResult result;
            try
            {
                result = Libgit2GitReader.CommitWithCompareAndSwap(dir, "x\n", CasCommitTreeSource.WholeIndex);
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Assert.That(result.Outcome, Is.EqualTo(CasCommitOutcome.Declined), $"{name}: {result}");
            Assert.That(result.Detail, Does.Contain(name));
            Assert.That(Tip(dir), Is.EqualTo(before), $"{name}: the branch must not move.");
            Assert.That(File.Exists(IndexLockPath(dir)), Is.False, $"{name}: index.lock left behind.");
        }

        Assert.That(first, Is.Not.EqualTo(Tip(dir)));
    }
}
