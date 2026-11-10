using System.Text.RegularExpressions;

using RoslynSentinel.Engines.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="DocTrailerEngine.CloseDoc"/> against throwaway docs trees (one GUID temp dir per test, so parallel-safe).
/// CloseDoc reads commit trailers, so each repo is a real git repo; most tests need no commit.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class DocCloseTests
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    private static readonly DateTime Today = new(2026, 11, 10);
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _repo = Path.Combine(Path.GetTempPath(), "rs-docclose-" + Guid.NewGuid().ToString("N"));
        InitRepo(_repo);
    }

    [TearDown]
    public void TearDown()
    {
        if (_repo is null || !Directory.Exists(_repo))
        {
            return;
        }

        // git marks pack/object files read-only; clear it so the delete succeeds on Windows.
        foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_repo, recursive: true);
    }

    private void Commit(string subject, string? trailers = null)
    {
        RunGit(_repo, "add", "-A");
        var args = new List<string> { "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "--allow-empty", "-m", subject };
        if (trailers is not null)
        {
            args.Add("-m");
            args.Add(trailers);
        }

        RunGit(_repo, [.. args]);
    }

    /// <summary>Writes a file ('\n' in <paramref name="content"/> becomes CRLF when <paramref name="crlf"/>), UTF-8 without BOM.</summary>
    private void Write(string relativePath, string content, bool crlf = false)
    {
        var path = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(crlf ? content.Replace("\n", "\r\n") : content));
    }

    private string Read(string relativePath) => File.ReadAllText(Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private bool Exists(string relativePath) => File.Exists(Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Every file under the repo except .git, as relative path to hex content, to prove a refusal wrote nothing.</summary>
    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_repo, f).Replace('\\', '/'))
            .Where(p => !p.StartsWith(".git/", StringComparison.Ordinal))
            .ToDictionary(p => p, p => Convert.ToHexString(File.ReadAllBytes(Path.Combine(_repo, p))));

    private const string TodoWithPlanB =
        "# TODO\n\n- [OPEN] Plan B work (2026-11-01) - do things - [plan](plans/plan_b.md#goals)\n- [OPEN] Other item - [plan](plans/plan_a.md)\n";

    private const string ClosedHead = "# CLOSED\n\nintro\n\n## Older entry - resolved (2026-01-01)\n\nold body\n";

    private const string PlanB =
        "# Plan B title\n\n**Status:** open\n\nSee [issue](../issues/x.md), [self](plan_b.md#top), [web](https://example.com/a.md), [anchor](#goals) and [sibling](plan_a.md).\n";

    /// <summary>plan_b (CRLF) with a TODO line, CLOSED.md and TODO.md (CRLF), plus referrers: plan_a (LF), proposals/p.md (LF), CLAUDE.md (LF).</summary>
    private void BuildHappyFixture()
    {
        Write("docs/current/TODO.md", TodoWithPlanB, crlf: true);
        Write("docs/current/CLOSED.md", ClosedHead, crlf: true);
        Write("docs/current/plans/plan_b.md", PlanB, crlf: true);
        Write("docs/current/issues/x.md", "# X\n");
        Write("docs/current/plans/plan_a.md", "# Plan A\n\n**Status:** open\n\nDepends on [b](plan_b.md#goals).\n");
        Write("docs/current/proposals/p.md", "# P\n\nSee [b](../plans/plan_b.md#sec).\n");
        Write("CLAUDE.md", "# Rules\n\nSee [b](docs/current/plans/plan_b.md).\n");
    }

    private static bool HasBareLf(string text) => Regex.IsMatch(text, "(?<!\r)\n");

    [Test]
    public void Close_HappyPath_MovesDocFixesStatusLinksAndRecordsInClosedMd()
    {
        BuildHappyFixture();
        Commit("finish b", "Closes: plan_b");

        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (abcd1234)", GitTimeout, Today);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.FromPath, Is.EqualTo("docs/current/plans/plan_b.md"));
        Assert.That(result.ToPath, Is.EqualTo("docs/current/plans/closed/plan_b.md"));
        Assert.That(result.StatusLine, Is.EqualTo("**Status:** IMPLEMENTED (abcd1234)"));
        Assert.That(result.ClosesCommits, Has.Count.EqualTo(1));
        Assert.That(result.Warnings, Is.Empty);
        Assert.That(result.TodoEntryMoved, Is.True);
        Assert.That(result.ClosedMdUpdated, Is.True);
        // moved doc: ../issues/x.md and plan_a.md re-pointed; the self link keeps its name; URL and anchor untouched.
        // other files: plan_a, p.md, CLAUDE.md, and the TODO body = 4.
        Assert.That(result.LinksFixed, Is.EqualTo(6));

        Assert.That(Exists("docs/current/plans/plan_b.md"), Is.False);
        var moved = Read("docs/current/plans/closed/plan_b.md");
        Assert.That(moved, Does.Contain("**Status:** IMPLEMENTED (abcd1234)").And.Not.Contain("**Status:** open"));
        Assert.That(moved, Does.Contain("](../../issues/x.md)"));
        Assert.That(moved, Does.Contain("](plan_b.md#top)"));
        Assert.That(moved, Does.Contain("](../plan_a.md)"));
        Assert.That(moved, Does.Contain("](https://example.com/a.md)").And.Contain("](#goals)"));
        Assert.That(HasBareLf(moved), Is.False, "the moved doc keeps CRLF");

        Assert.That(Read("docs/current/plans/plan_a.md"), Does.Contain("](closed/plan_b.md#goals)"));
        Assert.That(Read("docs/current/proposals/p.md"), Does.Contain("](../plans/closed/plan_b.md#sec)"));
        Assert.That(Read("CLAUDE.md"), Does.Contain("](docs/current/plans/closed/plan_b.md)"));
    }

    [Test]
    public void Close_HappyPath_MovesTodoLineIntoClosedMdAboveFirstEntry_PreservingEachFilesEol()
    {
        BuildHappyFixture();
        Commit("finish b", "Closes: plan_b");

        DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (abcd1234)", GitTimeout, Today);

        var todo = Read("docs/current/TODO.md");
        Assert.That(todo, Is.EqualTo("# TODO\r\n\r\n- [OPEN] Other item - [plan](plans/plan_a.md)\r\n"));
        var closed = Read("docs/current/CLOSED.md");
        const string entry = "## Plan B work - resolved (2026-11-10)\r\n\r\n"
            + "Plan B work (2026-11-01) - do things - [plan](plans/closed/plan_b.md#goals)\r\n\r\n"
            + "Evidence: IMPLEMENTED (abcd1234)\r\n\r\n";
        Assert.That(closed, Is.EqualTo("# CLOSED\r\n\r\nintro\r\n\r\n" + entry + "## Older entry - resolved (2026-01-01)\r\n\r\nold body\r\n"));
        Assert.That(Read("docs/current/plans/plan_a.md"), Does.Not.Contain("\r"), "an LF file stays LF");
    }

    [Test]
    public void Close_NoTodoLine_UsesDocTitleAndLinkAsBody()
    {
        Write("docs/current/TODO.md", "# TODO\n\n- [OPEN] Unrelated - [plan](plans/plan_a.md)\n");
        Write("docs/current/CLOSED.md", ClosedHead);
        Write("docs/current/plans/plan_b.md", PlanB);
        Commit("docs", "Closes: plan_b");

        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.TodoEntryMoved, Is.False);
        Assert.That(result.ClosedMdUpdated, Is.True);
        Assert.That(Read("docs/current/CLOSED.md"), Does.Contain("## Plan B title - resolved (2026-11-10)\n\nPlan B title - [doc](plans/closed/plan_b.md)\n\nEvidence: IMPLEMENTED (x)\n\n## Older entry"));
        Assert.That(Read("docs/current/TODO.md"), Is.EqualTo("# TODO\n\n- [OPEN] Unrelated - [plan](plans/plan_a.md)\n"));
    }

    [Test]
    public void Close_NoClosesCommit_StillClosesWithWarning()
    {
        BuildHappyFixture();
        Commit("add docs");

        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.ClosesCommits, Is.Empty);
        Assert.That(result.Warnings, Has.Some.Contain("No Closes: commit"));
        Assert.That(Exists("docs/current/plans/closed/plan_b.md"), Is.True);
    }

    [Test]
    public void Close_GitUnavailableOrNoCommits_IsAWarningNotAnError()
    {
        BuildHappyFixture(); // repo has no commit yet, so git log fails

        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.Warnings, Has.Some.Contain("could not be listed"));
        Assert.That(Exists("docs/current/plans/closed/plan_b.md"), Is.True);
    }

    [Test]
    public void Close_MissingTodoMd_DoesTheRestWithWarning()
    {
        Write("docs/current/CLOSED.md", ClosedHead);
        Write("docs/current/plans/plan_b.md", PlanB);
        Commit("docs", "Closes: plan_b");

        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.TodoEntryMoved, Is.False);
        Assert.That(result.ClosedMdUpdated, Is.True);
        Assert.That(result.Warnings, Has.Some.Contain("TODO.md was not found"));
        Assert.That(Exists("docs/current/plans/closed/plan_b.md"), Is.True);
    }

    [Test]
    public void Close_MissingClosedMd_LeavesTodoLineInPlaceButRepointsItsLink()
    {
        Write("docs/current/TODO.md", TodoWithPlanB);
        Write("docs/current/plans/plan_b.md", PlanB);
        Commit("docs", "Closes: plan_b");

        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.TodoEntryMoved, Is.False);
        Assert.That(result.ClosedMdUpdated, Is.False);
        Assert.That(result.Warnings, Has.Some.Contain("CLOSED.md was not found"));
        Assert.That(Read("docs/current/TODO.md"), Does.Contain("Plan B work (2026-11-01) - do things - [plan](plans/closed/plan_b.md#goals)"));
    }

    [Test]
    public void Close_SeveralTodoLinesLinkToTheDoc_NoneMovedWarningAndAllRepointed()
    {
        Write("docs/current/TODO.md", "# TODO\n\n- [OPEN] One - [plan](plans/plan_b.md)\n- [OPEN] Two - [plan](plans/plan_b.md#x)\n");
        Write("docs/current/CLOSED.md", ClosedHead);
        Write("docs/current/plans/plan_b.md", PlanB);
        Commit("docs", "Closes: plan_b");

        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.TodoEntryMoved, Is.False);
        Assert.That(result.Warnings, Has.Some.Contain("2 docs/current/TODO.md lines"));
        Assert.That(Read("docs/current/TODO.md"), Is.EqualTo("# TODO\n\n- [OPEN] One - [plan](plans/closed/plan_b.md)\n- [OPEN] Two - [plan](plans/closed/plan_b.md#x)\n"));
    }

    [Test]
    public void Close_LinksInsideCodeFences_AreNotRewritten()
    {
        Write("docs/current/plans/plan_b.md", PlanB);
        var other = "# A\n\n```\n[b](plan_b.md)\n```\n\n[real](plan_b.md)\n";
        Write("docs/current/plans/plan_a.md", other);
        Commit("docs", "Closes: plan_b");

        DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(Read("docs/current/plans/plan_a.md"), Is.EqualTo("# A\n\n```\n[b](plan_b.md)\n```\n\n[real](closed/plan_b.md)\n"));
    }

    [Test]
    public void Close_Utf8Bom_IsPreserved()
    {
        var path = Path.Combine(_repo, "docs", "current", "plans", "plan_b.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. System.Text.Encoding.UTF8.GetPreamble(), .. new System.Text.UTF8Encoding(false).GetBytes(PlanB)]);
        Commit("docs", "Closes: plan_b");

        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(result.Error, Is.Null);
        var bytes = File.ReadAllBytes(Path.Combine(_repo, "docs", "current", "plans", "closed", "plan_b.md"));
        Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.That(bytes.Skip(3).Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "no doubled BOM");
    }

    private void AssertRefusedWithoutWriting(string id, string statusText, string expectedFragment)
    {
        var before = Snapshot();

        var result = DocTrailerEngine.CloseDoc(_repo, id, statusText, GitTimeout, Today);

        Assert.That(result.Error, Does.Contain(expectedFragment));
        Assert.That(result.TodoEntryMoved, Is.False);
        Assert.That(result.ClosedMdUpdated, Is.False);
        Assert.That(Snapshot(), Is.EqualTo(before), "a refusal must write nothing");
    }

    [Test]
    public void Close_AlreadyClosed_RefusesWithoutWriting()
    {
        BuildHappyFixture();
        Write("docs/current/plans/closed/plan_done.md", "# Done\n\n**Status:** IMPLEMENTED\n");
        Commit("docs", "Closes: plan_done");

        AssertRefusedWithoutWriting("plan_done", "IMPLEMENTED (x)", "already under a closed folder");
    }

    [Test]
    public void Close_AmbiguousId_RefusesWithoutWriting()
    {
        BuildHappyFixture();
        Write("docs/current/issues/plan_b.md", "# Same stem\n\n**Status:** open\n");
        Commit("docs");

        AssertRefusedWithoutWriting("plan_b", "IMPLEMENTED (x)", "ambiguous");
    }

    [Test]
    public void Close_DestinationAlreadyExists_RefusesWithoutWriting()
    {
        // The same file name in closed/ shares the doc's ID, so the ambiguity guard fires first; the explicit destination
        // check only matters when something other than an indexed .md (a directory or a differently-cased name) sits there.
        BuildHappyFixture();
        Write("docs/current/plans/closed/plan_b.md", "# Old closed copy\n");
        Commit("docs");

        var before = Snapshot();
        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(result.Error, Is.Not.Null);
        Assert.That(Snapshot(), Is.EqualTo(before));
    }

    [Test]
    public void Close_DestinationIsADirectory_RefusesWithoutWriting()
    {
        BuildHappyFixture();
        Directory.CreateDirectory(Path.Combine(_repo, "docs", "current", "plans", "closed", "plan_b.md"));
        Commit("docs");

        var result = DocTrailerEngine.CloseDoc(_repo, "plan_b", "IMPLEMENTED (x)", GitTimeout, Today);

        Assert.That(result.Error, Does.Contain("already exists"));
        Assert.That(Exists("docs/current/plans/plan_b.md"), Is.True);
        Assert.That(Read("docs/current/plans/plan_b.md"), Does.Contain("**Status:** open"));
    }

    [Test]
    public void Close_NoStatusLine_RefusesWithoutWriting()
    {
        BuildHappyFixture();
        Write("docs/current/plans/plan_nostatus.md", "# No status\n\nBody.\n");
        Commit("docs");

        AssertRefusedWithoutWriting("plan_nostatus", "IMPLEMENTED (x)", "no '**Status:**' line");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("line one\nline two")]
    public void Close_EmptyOrMultiLineStatusText_RefusesWithoutWriting(string statusText)
    {
        BuildHappyFixture();
        Commit("docs");

        AssertRefusedWithoutWriting("plan_b", statusText, "statusText must be one non-empty line");
    }

    [TestCase("docs/current/plans/sub/deep.md", "deep")]
    [TestCase("docs/current/loose.md", "loose")]
    [TestCase("docs/notes/n.md", "n")]
    public void Close_NotADirectChildOfAGenreFolder_RefusesWithoutWriting(string path, string id)
    {
        BuildHappyFixture();
        Write(path, "# T\n\n**Status:** open\n");
        Commit("docs");

        AssertRefusedWithoutWriting(id, "IMPLEMENTED (x)", "direct child of docs/current/<genre>/");
    }

    [Test]
    public void Close_UnknownId_RefusesWithNextStep()
    {
        BuildHappyFixture();
        Commit("docs");

        AssertRefusedWithoutWriting("no_such_doc", "IMPLEMENTED (x)", "ProjectDoc");
    }

    [TestCase("../plan_b")]
    [TestCase("")]
    public void Close_InvalidId_RefusesWithoutWriting(string id)
    {
        BuildHappyFixture();
        Commit("docs");

        AssertRefusedWithoutWriting(id, "IMPLEMENTED (x)", "file name without extension");
    }
}
