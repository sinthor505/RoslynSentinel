using RoslynSentinel.Engines.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="DocTrailerEngine"/> against throwaway git repos (one GUID temp dir per test, so parallel-safe).
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class DocTrailerEngineTests
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _repo = Path.Combine(Path.GetTempPath(), "rs-doctrailer-" + Guid.NewGuid().ToString("N"));
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

    private void WriteDoc(string relativePath, string content)
    {
        var path = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Stages everything and commits (empty commits allowed) with the subject and an optional trailer paragraph; returns the full sha.</summary>
    private string Commit(string subject, string? trailers = null)
    {
        RunGit(_repo, "add", "-A");
        var args = new List<string> { "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "--allow-empty", "-m", subject };
        if (trailers is not null)
        {
            args.Add("-m");
            args.Add(trailers);
        }

        RunGit(_repo, [.. args]);
        return RunGit(_repo, "rev-parse", "HEAD");
    }

    /// <summary>plan_a (open), plan_b, closed/issue_c; trailers: Plan plan_a, Closes plan_b, a Plan list, Closes unknown, Closes issue_c.</summary>
    private string BuildFixture()
    {
        WriteDoc("docs/current/plans/plan_a.md", "# Plan A\n\n**Status:** open\n");
        WriteDoc("docs/current/plans/plan_b.md", "# Plan B\n\n**Status**: shipped in slices\n");
        WriteDoc("docs/current/issues/closed/issue_c.md", "# Issue C\n\n**Status:** closed\n");
        Commit("add docs");
        Commit("work on a", "Plan: plan_a");
        Commit("finish b", "Closes: plan_b");
        Commit("both", "Plan: plan_a, plan_b");
        Commit("unknown", "Closes: no_such_doc");
        return Commit("close c", "Closes: issue_c");
    }

    [Test]
    public void Scan_ClosesTrailerMakesClosureCandidate_PlanOnlyDoesNot()
    {
        BuildFixture();

        var scan = DocTrailerEngine.ScanTrailers(_repo, GitTimeout);

        Assert.That(scan.Error, Is.Null);
        var a = scan.Docs.Single(d => d.Id == "plan_a");
        var b = scan.Docs.Single(d => d.Id == "plan_b");
        Assert.That(a.Path, Is.EqualTo("docs/current/plans/plan_a.md"));
        Assert.That(a.ClosureCandidate, Is.False);
        Assert.That(a.ClosesCommits, Is.Empty);
        Assert.That(a.PlanCommits, Has.Count.EqualTo(2));
        Assert.That(a.Status, Is.EqualTo("open"));
        Assert.That(b.ClosureCandidate, Is.True);
        Assert.That(b.ClosesCommits, Has.Count.EqualTo(1));
        Assert.That(b.PlanCommits, Has.Count.EqualTo(1));
        Assert.That(b.Status, Is.EqualTo("shipped in slices"));
        Assert.That(scan.ClosureCandidateCount, Is.EqualTo(1));
        Assert.That(a.PlanCommits.Concat(b.ClosesCommits), Has.All.Length.EqualTo(8));
    }

    [Test]
    public void Scan_ClosedFolderDoc_IsInClosedAndNeverACandidate()
    {
        BuildFixture();

        var scan = DocTrailerEngine.ScanTrailers(_repo, GitTimeout);

        var c = scan.Docs.Single(d => d.Id == "issue_c");
        Assert.That(c.InClosed, Is.True);
        Assert.That(c.ClosureCandidate, Is.False);
        Assert.That(c.ClosesCommits, Has.Count.EqualTo(1));
        Assert.That(c.Path, Is.EqualTo("docs/current/issues/closed/issue_c.md"));
    }

    [Test]
    public void Scan_UnknownTrailerValue_IsListedWithItsCommit()
    {
        var head = BuildFixture();
        var unknownCommit = RunGit(_repo, "rev-parse", "HEAD~1");

        var scan = DocTrailerEngine.ScanTrailers(_repo, GitTimeout);

        var unknown = scan.UnknownTrailers.Single();
        Assert.That(unknown.Kind, Is.EqualTo("Closes"));
        Assert.That(unknown.Id, Is.EqualTo("no_such_doc"));
        Assert.That(unknownCommit, Does.StartWith(unknown.Commit));
        Assert.That(head, Is.Not.EqualTo(unknownCommit));
        Assert.That(scan.Docs.Select(d => d.Id), Does.Not.Contain("no_such_doc"));
        Assert.That(scan.Docs.Select(d => d.Path), Is.Ordered.Using<string>(StringComparer.Ordinal));
        Assert.That(scan.CommitsWithTrailers, Is.EqualTo(5));
    }

    [Test]
    public void Scan_DocWithoutTrailers_IsOmitted()
    {
        BuildFixture();
        WriteDoc("docs/current/plans/plan_untouched.md", "# Untouched\n");
        Commit("add untouched");

        var scan = DocTrailerEngine.ScanTrailers(_repo, GitTimeout);

        Assert.That(scan.Docs.Select(d => d.Id), Does.Not.Contain("plan_untouched"));
        Assert.That(scan.DocsScanned, Is.EqualTo(4));
    }

    [Test]
    public void Scan_StatusLine_IsTrimmedToSixtyCharsAndMissingIsReported()
    {
        WriteDoc("docs/current/plans/plan_long.md", "**Status:** " + new string('x', 100) + "\n");
        WriteDoc("docs/current/plans/plan_bare.md", "# No status here\n");
        Commit("docs", "Closes: plan_long, plan_bare");

        var scan = DocTrailerEngine.ScanTrailers(_repo, GitTimeout);

        Assert.That(scan.Docs.Single(d => d.Id == "plan_long").Status, Has.Length.EqualTo(60));
        Assert.That(scan.Docs.Single(d => d.Id == "plan_bare").Status, Is.EqualTo("(no Status line)"));
    }

    [Test]
    public void Scan_NonCandidateFolders_AreNotClosureCandidates()
    {
        WriteDoc("docs/current/obsolete/plan_old.md", "# Old\n");
        WriteDoc("docs/current/upstream/plan_up.md", "# Up\n");
        WriteDoc("docs/reference/plan_outside.md", "# Outside docs/current\n");
        Commit("docs", "Closes: plan_old, plan_up, plan_outside");

        var scan = DocTrailerEngine.ScanTrailers(_repo, GitTimeout);

        Assert.That(scan.Docs, Has.Count.EqualTo(3));
        Assert.That(scan.Docs.Select(d => d.ClosureCandidate), Has.All.False);
    }

    [Test]
    public void Scan_WorktreeFolderDocs_AreIgnored()
    {
        WriteDoc("docs/current/Worktree/clone/plan_w.md", "# W\n");
        Commit("docs", "Closes: plan_w");

        var scan = DocTrailerEngine.ScanTrailers(_repo, GitTimeout);

        Assert.That(scan.DocsScanned, Is.EqualTo(0));
        Assert.That(scan.UnknownTrailers.Single().Id, Is.EqualTo("plan_w"));
    }

    [Test]
    public void Scan_NotAGitRepo_ReturnsActionableErrorWithoutThrowing()
    {
        // A sibling of the fixture repo under the temp root (assumed not inside any git work tree).
        var plain = Path.Combine(Path.GetTempPath(), "rs-doctrailer-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(plain, "docs"));
        try
        {
            var scan = DocTrailerEngine.ScanTrailers(plain, GitTimeout);

            Assert.That(scan.Error, Does.Contain("git"));
            Assert.That(scan.Docs, Is.Empty);
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    [Test]
    public void Scan_NoDocsDirectoryOrMissingRoot_ReturnsError()
    {
        Assert.That(DocTrailerEngine.ScanTrailers(_repo, GitTimeout).Error, Does.Contain("docs/"));
        Assert.That(DocTrailerEngine.ScanTrailers(Path.Combine(_repo, "nope"), GitTimeout).Error, Is.Not.Null);
    }

    [Test]
    public void ValidateId_ExistingDoc_ReportsPathAndCommits()
    {
        BuildFixture();

        var result = DocTrailerEngine.ValidateId(_repo, "plan_a", GitTimeout);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.Exists, Is.True);
        Assert.That(result.Message, Is.Null);
        var match = result.Matches.Single();
        Assert.That(match.Path, Is.EqualTo("docs/current/plans/plan_a.md"));
        Assert.That(match.PlanCommits, Has.Count.EqualTo(2));
        Assert.That(match.ClosureCandidate, Is.False);
    }

    [Test]
    public void ValidateId_UnknownId_IsNotAnErrorAndSaysWhatWasSearched()
    {
        BuildFixture();

        var result = DocTrailerEngine.ValidateId(_repo, "no_such_doc", GitTimeout);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.Exists, Is.False);
        Assert.That(result.Matches, Is.Empty);
        Assert.That(result.Message, Does.Contain("docs/").And.Contain("without its extension").And.Contain("case-sensitive").And.Contain("ProjectDoc"));
    }

    [Test]
    public void ValidateId_IdWithMdExtension_HintsToDropIt()
    {
        BuildFixture();

        var result = DocTrailerEngine.ValidateId(_repo, "plan_a.md", GitTimeout);

        Assert.That(result.Exists, Is.False);
        Assert.That(result.Message, Does.Contain("WITHOUT '.md'"));
    }

    [Test]
    public void ValidateId_CaseDiffers_IsNotFound()
    {
        BuildFixture();

        Assert.That(DocTrailerEngine.ValidateId(_repo, "PLAN_A", GitTimeout).Exists, Is.False);
    }

    [TestCase("")]
    [TestCase("  ")]
    [TestCase("../plan_a")]
    [TestCase("docs/plan_a")]
    [TestCase("..")]
    [TestCase("plan a")]
    public void ValidateId_InvalidShape_ReturnsError(string id)
    {
        BuildFixture();

        var result = DocTrailerEngine.ValidateId(_repo, id, GitTimeout);

        Assert.That(result.Error, Does.Contain("file name without extension"));
        Assert.That(result.Exists, Is.False);
    }

    [Test]
    public void ValidateId_SameStemInTwoFolders_ReportsBothAsAmbiguous()
    {
        WriteDoc("docs/current/plans/shared.md", "# Shared plan\n");
        WriteDoc("docs/current/issues/shared.md", "# Shared issue\n");
        Commit("docs", "Closes: shared");

        var result = DocTrailerEngine.ValidateId(_repo, "shared", GitTimeout);

        Assert.That(result.Exists, Is.True);
        Assert.That(result.Matches.Select(m => m.Path), Is.EqualTo(new[] { "docs/current/issues/shared.md", "docs/current/plans/shared.md" }));
        Assert.That(result.Message, Does.StartWith("Ambiguous: 2 docs"));
    }

    [Test]
    public void TryFindDocPaths_ReturnsAllMatchesAndRejectsUnsafeIds()
    {
        WriteDoc("docs/current/plans/shared.md", "# a\n");
        WriteDoc("docs/current/issues/closed/shared.md", "# b\n");

        Assert.That(DocTrailerEngine.TryFindDocPaths(_repo, "shared", out var paths, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(paths, Is.EqualTo(new[] { "docs/current/issues/closed/shared.md", "docs/current/plans/shared.md" }));

        Assert.That(DocTrailerEngine.TryFindDocPaths(_repo, "missing", out paths, out _), Is.True);
        Assert.That(paths, Is.Empty);

        Assert.That(DocTrailerEngine.TryFindDocPaths(_repo, "..\\..\\secret", out paths, out error), Is.False);
        Assert.That(paths, Is.Empty);
        Assert.That(error, Is.Not.Null);
    }
}
