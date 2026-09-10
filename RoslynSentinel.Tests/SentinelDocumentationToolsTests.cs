using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

[TestFixture]
public class SentinelDocumentationToolsTests
{
    private string _solutionRoot = "";
    private SentinelDocumentationTools _tools = null!;

    [SetUp]
    public void Setup()
    {
        _solutionRoot = Path.Combine(Path.GetTempPath(), "SentinelDocumentationToolsTests", Path.GetRandomFileName());
        Directory.CreateDirectory(_solutionRoot);

        _tools = BuildTools(OperatingMode.Production);
    }

    /// <summary>
    /// Builds a tool instance for <paramref name="operatingMode"/> against the same temp solution
    /// root. Because OperatingMode is injected rather than static, both modes can be exercised from
    /// this one fixture with no [NonParallelizable] — see SentinelHostOptions' remarks.
    /// </summary>
    private SentinelDocumentationTools BuildTools(OperatingMode operatingMode)
    {
        var workspaceManager = new FakeWorkspaceManager
        {
            SolutionPath = Path.Combine(_solutionRoot, "Fake.slnx")
        };
        return new SentinelDocumentationTools(
            workspaceManager,
            new SentinelHostOptions { OperatingMode = operatingMode },
            NullLogger<SentinelDocumentationTools>.Instance);
    }

    [TearDown]
    public void Cleanup()
    {
        try { Directory.Delete(_solutionRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private void WriteDoc(string relativePathUnderDocs, string content)
    {
        var fullPath = Path.Combine(_solutionRoot, "docs", relativePathUnderDocs);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    // ── docs/current/ detection ───────────────────────────────────────────────

    [Test]
    public void Read_NestedPathUnderDocsCurrentPlans_Succeeds()
    {
        WriteDoc(Path.Combine("current", "plans", "plan-x-steps", "01-baseline.md"), "baseline content");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "plan-x-steps/01-baseline.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("baseline content"));
    }

    [Test]
    public void Read_NoCurrentDir_FallsBackToDocsPlansDirectly()
    {
        // No docs/current/ at all — only docs/plans/ directly, matching the original assumed layout.
        WriteDoc(Path.Combine("plans", "01-baseline.md"), "flat layout content");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "01-baseline.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("flat layout content"));
    }

    // ── basename fallback ──────────────────────────────────────────────────────

    [Test]
    public void Read_BareNameSingleMatch_AutoResolves()
    {
        WriteDoc(Path.Combine("current", "plans", "plan-x-steps", "01-baseline.md"), "baseline content");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "01-baseline");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("baseline content"));
        Assert.That(result.Filename, Is.EqualTo("plan-x-steps/01-baseline.md"));
    }

    [Test]
    public void Read_WrongExtension_AutoResolvesViaBasenameFallback()
    {
        WriteDoc(Path.Combine("current", "plans", "01-baseline.md"), "baseline content");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "01-baseline.txt");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("baseline content"));
    }

    [Test]
    public void Read_AmbiguousBasename_ReportsCandidatesWithoutReadingEither()
    {
        WriteDoc(Path.Combine("current", "plans", "a", "01-baseline.md"), "content A");
        WriteDoc(Path.Combine("current", "plans", "b", "01-baseline.md"), "content B");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "01-baseline");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Error, Does.Contain("ambiguous"));
        Assert.That(result.Error, Does.Contain("a/01-baseline.md"));
        Assert.That(result.Error, Does.Contain("b/01-baseline.md"));
    }

    [Test]
    public void Read_NoMatchAnywhere_ReportsDocTypeAndFullTreeSearched()
    {
        Directory.CreateDirectory(Path.Combine(_solutionRoot, "docs", "current", "plans"));

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "does-not-exist.md");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Error, Does.Contain("docType='plan'"));
        Assert.That(result.Error, Does.Contain("docs/"));
    }

    // ── docs/-wide fallback (file lives outside the requested docType's subdir) ──

    [Test]
    public void Read_ExactPathOutsideDocTypeSubdir_FallsBackToFullDocsTree()
    {
        // Mirrors a real layout: a plan file that lives under docs/current/tests/... instead
        // of docs/current/plans/... — action:list would still surface it, so read must reach it.
        WriteDoc(Path.Combine("current", "tests", "plan-x-steps-runner", "02-step.md"), "runner step content");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "tests/plan-x-steps-runner/02-step.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("runner step content"));
    }

    [Test]
    public void Read_BareNameOutsideDocTypeSubdir_FallsBackToFullTreeBasenameSearch()
    {
        WriteDoc(Path.Combine("current", "tests", "plan-x-steps-runner", "02-step.md"), "runner step content");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "02-step");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("runner step content"));
        Assert.That(result.Filename, Is.EqualTo("current/tests/plan-x-steps-runner/02-step.md"));
    }

    // ── path-qualified names are never substituted (run 20260910-013550-398) ────

    [Test]
    public void Read_PathQualifiedName_NeverSubstitutesSameNamedFileElsewhere()
    {
        // The exact run-398 shape: a runner copy under tests/ and the production plan under plans/,
        // sharing a basename. The request names the tests/ path; the plans/ copy must not answer it.
        WriteDoc(Path.Combine("current", "plans", "01-baseline.md"), "PRODUCTION plan");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "tests/plan-x-steps-runner/01-baseline.md");

        Assert.That(result.Found, Is.False, "a path-qualified miss must not fall back to a basename search");
        Assert.That(result.Content, Is.Null);
        Assert.That(result.Error, Does.Contain("explicit relative path"));
    }

    [Test]
    public void Read_PathQualifiedNameThatExists_StillResolves()
    {
        // Guards the above from over-correcting: an explicit path that is actually present must
        // still read, both inside the docType subdir and elsewhere under docs/.
        WriteDoc(Path.Combine("current", "plans", "plan-x-steps", "01-baseline.md"), "in-docType content");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "plan-x-steps/01-baseline.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("in-docType content"));
        Assert.That(result.Warning, Is.Null, "an exact-path hit is not a substitution");
    }

    [Test]
    public void Read_BareNameResolvedToDifferentPath_PopulatesWarning()
    {
        WriteDoc(Path.Combine("current", "plans", "plan-x-steps", "01-baseline.md"), "baseline content");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "01-baseline");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Warning, Is.Not.Null, "a fallback that changed the path must say so");
        Assert.That(result.Warning, Does.Contain("01-baseline"));
        Assert.That(result.Warning, Does.Contain("plan-x-steps/01-baseline.md"));
    }

    // ── OperatingMode.Testing (docs/testing/, docType ignored) ─────────────────
    //
    // Both modes are exercised from this one fixture with no [NonParallelizable]: OperatingMode is
    // injected, so "production" and "testing" are two objects rather than two writes to a static.

    [Test]
    public void Read_TestingMode_IgnoresDocTypeAndResolvesAnywhereUnderDocsTesting()
    {
        // The run-398 request shape: docType=plan for a file that lives in no docType subdir.
        WriteDoc(Path.Combine("testing", "plan-x-steps-runner", "01-baseline.md"), "runner baseline");
        var tools = BuildTools(OperatingMode.Testing);

        var result = (DocReadResult)tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "plan-x-steps-runner/01-baseline.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("runner baseline"));
    }

    [Test]
    public void Read_TestingMode_CannotReachProductionDocs()
    {
        // The whole point of the separate root: a production file with the same name is invisible.
        WriteDoc(Path.Combine("current", "plans", "01-baseline.md"), "PRODUCTION plan");
        var tools = BuildTools(OperatingMode.Testing);

        var result = (DocReadResult)tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "01-baseline.md");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Content, Is.Null);
    }

    [Test]
    public void Read_TestingMode_AmbiguityWithinTestingRootStillErrors()
    {
        WriteDoc(Path.Combine("testing", "run-a", "01-baseline.md"), "content A");
        WriteDoc(Path.Combine("testing", "run-b", "01-baseline.md"), "content B");
        var tools = BuildTools(OperatingMode.Testing);

        var result = (DocReadResult)tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "01-baseline");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Error, Does.Contain("ambiguous"));
        Assert.That(result.Error, Does.Contain("run-a/01-baseline.md"));
        Assert.That(result.Error, Does.Contain("run-b/01-baseline.md"));
    }

    [Test]
    public void Read_ProductionMode_DoesNotReachDocsTesting()
    {
        // The reverse containment check: docs/testing/ IS under docs/, so a production-mode bare
        // name could reach it via the docs/-wide fallback. That's acceptable (it is a real file
        // under docs/), but a production request must never prefer it over the docType subdir.
        WriteDoc(Path.Combine("current", "plans", "01-baseline.md"), "PRODUCTION plan");
        WriteDoc(Path.Combine("testing", "01-baseline.md"), "TESTING fixture");

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "01-baseline.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("PRODUCTION plan"));
    }
}
