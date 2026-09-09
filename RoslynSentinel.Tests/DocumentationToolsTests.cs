using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

[TestFixture]
public class DocumentationToolsTests
{
    private string _solutionRoot = "";
    private DocumentationTools _tools = null!;

    [SetUp]
    public void Setup()
    {
        _solutionRoot = Path.Combine(Path.GetTempPath(), "DocumentationToolsTests", Path.GetRandomFileName());
        Directory.CreateDirectory(_solutionRoot);

        var workspaceManager = new FakeWorkspaceManager
        {
            SolutionPath = Path.Combine(_solutionRoot, "Fake.slnx")
        };
        _tools = new DocumentationTools(workspaceManager, NullLogger<DocumentationTools>.Instance);
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
    public void Read_NoMatchAnywhere_ReportsScopeAlreadySearched()
    {
        Directory.CreateDirectory(Path.Combine(_solutionRoot, "docs", "current", "plans"));

        var result = (DocReadResult)_tools.ProjectDoc("test", DocAction.read, DocType.plan,
            name: "does-not-exist.md");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Error, Does.Contain("already searched"));
        Assert.That(result.Error, Does.Contain("docType='plan'"));
    }
}
