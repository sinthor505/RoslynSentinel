using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("DocReadResult")] // sentinel:auto-category
[Category("DocumentationTools")] // sentinel:auto-category
public class DocumentationToolsTests
{
    private string _solutionRoot = "";
    private DocumentationTools _tools = null!;

    [SetUp]
    public void Setup()
    {
        _solutionRoot = Path.Combine(Path.GetTempPath(), "DocumentationToolsTests", Path.GetRandomFileName());
        Directory.CreateDirectory(_solutionRoot);

        _tools = BuildTools(OperatingMode.Production);
    }

    /// <summary>
    /// Builds a tool instance for <paramref name="operatingMode"/> against the same temp solution
    /// root. Because OperatingMode is injected rather than static, both modes can be exercised from
    /// this one fixture with no [NonParallelizable] -> see SentinelHostOptions' remarks.
    /// </summary>
    private DocumentationTools BuildTools(OperatingMode operatingMode)
    {
        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly)
        {
            SolutionPath = Path.Combine(_solutionRoot, "Fake.slnx")
        };
        return new DocumentationTools(
            workspaceManager,
            new SentinelHostOptions { OperatingMode = operatingMode },
            NullLogger<DocumentationTools>.Instance);
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

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "plan-x-steps/01-baseline.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("baseline content"));
    }

    [Test]
    public void Read_NoCurrentDir_FallsBackToDocsPlansDirectly()
    {
        // No docs/current/ at all -> only docs/plans/ directly, matching the original assumed layout.
        WriteDoc(Path.Combine("plans", "01-baseline.md"), "flat layout content");

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "01-baseline.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("flat layout content"));
    }

    // ── basename fallback ──────────────────────────────────────────────────────

    [Test]
    public void Read_BareNameSingleMatch_AutoResolves()
    {
        WriteDoc(Path.Combine("current", "plans", "plan-x-steps", "01-baseline.md"), "baseline content");

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "01-baseline");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("baseline content"));
        Assert.That(result.Filename, Is.EqualTo("plan-x-steps/01-baseline.md"));
    }

    [Test]
    public void Read_WrongExtension_AutoResolvesViaBasenameFallback()
    {
        WriteDoc(Path.Combine("current", "plans", "01-baseline.md"), "baseline content");

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "01-baseline.txt");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("baseline content"));
    }

    [Test]
    public void Read_AmbiguousBasename_ReportsCandidatesWithoutReadingEither()
    {
        WriteDoc(Path.Combine("current", "plans", "a", "01-baseline.md"), "content A");
        WriteDoc(Path.Combine("current", "plans", "b", "01-baseline.md"), "content B");

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "01-baseline");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.Error, Does.Contain("ambiguous"));
        Assert.That(result.Error, Does.Contain("a/01-baseline.md"));
        Assert.That(result.Error, Does.Contain("b/01-baseline.md"));
    }

    [Test]
    public void Read_NoMatchAnywhere_ReportsDocTypeAndFullTreeSearched()
    {
        Directory.CreateDirectory(Path.Combine(_solutionRoot, "docs", "current", "plans"));

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "does-not-exist.md");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.Error, Does.Contain("docType='plan'"));
        Assert.That(result.Error, Does.Contain("docs/"));
    }

    // ── docs/-wide fallback (file lives outside the requested docType's subdir) ──

    [Test]
    public void Read_ExactPathOutsideDocTypeSubdir_FallsBackToFullDocsTree()
    {
        // Mirrors a real layout: a plan file that lives under docs/current/tests/... instead
        // of docs/current/plans/... -> action:list would still surface it, so read must reach it.
        WriteDoc(Path.Combine("current", "tests", "plan-x-steps-runner", "02-step.md"), "runner step content");

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "tests/plan-x-steps-runner/02-step.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("runner step content"));
    }

    [Test]
    public void Read_BareNameOutsideDocTypeSubdir_FallsBackToFullTreeBasenameSearch()
    {
        WriteDoc(Path.Combine("current", "tests", "plan-x-steps-runner", "02-step.md"), "runner step content");

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
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

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
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

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "plan-x-steps/01-baseline.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("in-docType content"));
        Assert.That(result.Warning, Is.Null, "an exact-path hit is not a substitution");
    }

    [Test]
    public void Read_BareNameResolvedToDifferentPath_PopulatesWarning()
    {
        WriteDoc(Path.Combine("current", "plans", "plan-x-steps", "01-baseline.md"), "baseline content");

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "01-baseline");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Warning, Is.Not.Null, "a fallback that changed the path must say so");
        Assert.That(result.Warning, Does.Contain("01-baseline"));
        Assert.That(result.Warning, Does.Contain("plan-x-steps/01-baseline.md"));
    }

    // ── OperatingMode.Testing (docs/current/evals/, docType ignored) ─────────────────
    //
    // Both modes are exercised from this one fixture with no [NonParallelizable]: OperatingMode is
    // injected, so "production" and "testing" are two objects rather than two writes to a static.

    [Test]
    public void Read_TestingMode_IgnoresDocTypeAndResolvesAnywhereUnderDocsTesting()
    {
        // The run-398 request shape: docType=plan for a file that lives in no docType subdir.
        WriteDoc(Path.Combine("current", "evals", "plan-x-steps-runner", "01-baseline.md"), "runner baseline");
        var tools = BuildTools(OperatingMode.Testing);

        var result = (DocReadResult)tools.ProjectDocCore("test", DocAction.read, DocType.plan,
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

        var result = (DocReadResult)tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "01-baseline.md");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Content, Is.Null);
    }

    [Test]
    public void Read_TestingMode_AmbiguityWithinTestingRootStillErrors()
    {
        WriteDoc(Path.Combine("current", "evals", "run-a", "01-baseline.md"), "content A");
        WriteDoc(Path.Combine("current", "evals", "run-b", "01-baseline.md"), "content B");
        var tools = BuildTools(OperatingMode.Testing);

        var result = (DocReadResult)tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "01-baseline");

        Assert.That(result.Found, Is.False);
        Assert.That(result.Error, Does.Contain("ambiguous"));
        Assert.That(result.Error, Does.Contain("run-a/01-baseline.md"));
        Assert.That(result.Error, Does.Contain("run-b/01-baseline.md"));
    }

    [Test]
    public void Read_ProductionMode_DoesNotReachDocsTesting()
    {
        // The reverse containment check: docs/current/evals/ IS under docs/, so a production-mode bare
        // name could reach it via the docs/-wide fallback. That's acceptable (it is a real file
        // under docs/), but a production request must never prefer it over the docType subdir.
        WriteDoc(Path.Combine("current", "plans", "01-baseline.md"), "PRODUCTION plan");
        WriteDoc(Path.Combine("current", "evals", "01-baseline.md"), "TESTING fixture");

        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan,
            name: "01-baseline.md");

        Assert.That(result.Found, Is.True, result.Error);
        Assert.That(result.Content, Is.EqualTo("PRODUCTION plan"));
    }

    [Test]
    public void Write_MarkdownContent_UsesResolvedLineEndings()
    {
        var result = (DocWriteResult)_tools.ProjectDocCore("test", DocAction.write, DocType.documentation,
            name: "eol-write.md", content: "a\nb\n");

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(File.ReadAllText(result.FullPath!), Is.EqualTo("a" + Environment.NewLine + "b" + Environment.NewLine));
    }

    [Test]
    public void Append_MarkdownContent_HasNoMixedLineEndings()
    {
        var first = (DocWriteResult)_tools.ProjectDocCore("test", DocAction.write, DocType.completed_work,
            name: "eol-append.md", content: "a\n");
        Assert.That(first.IsError, Is.False, first.Error);

        var second = (DocWriteResult)_tools.ProjectDocCore("test", DocAction.append, DocType.completed_work,
            name: "eol-append.md", content: "b\n");

        Assert.That(second.IsError, Is.False, second.Error);
        Assert.That(File.ReadAllText(second.FullPath!), Is.EqualTo("a" + Environment.NewLine + "b" + Environment.NewLine));
    }

    // ProjectDocTool envelope (SentinelCallToolResult): success payload is the DTO, failures use ErrorData.
    [Test]
    public void Wrapper_ReadExistingPlan_ReturnsDocReadResultAsSuccessData()
    {
        WriteDoc(Path.Combine("current", "plans", "wrapper-plan.md"), "wrapper content");

        var result = _tools.ProjectDocTool("test", DocAction.read, DocType.plan, name: "wrapper-plan.md");

        Assert.That(result.IsError, Is.False, result.Message);
        Assert.That(result.ErrorData, Is.Null);
        var read = result.SuccessData as DocReadResult;
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.Found, Is.True);
        Assert.That(read.Content, Is.EqualTo("wrapper content"));
    }

    [Test]
    public void Wrapper_ReadMissingPlan_ReturnsErrorDataWithDtoCode()
    {
        Directory.CreateDirectory(Path.Combine(_solutionRoot, "docs", "current", "plans"));

        var result = _tools.ProjectDocTool("test", DocAction.read, DocType.plan, name: "does-not-exist.md");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null);
        Assert.That(result.ErrorData, Is.Not.Null);
        // DocReadResult.Code carries the NotFound code that ReadFile sets when the file is missing.
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("docType='plan'"));
    }

    [Test]
    public void Wrapper_ReadMissingState_ReturnsNotFoundWithActionableMessage()
    {
        Directory.CreateDirectory(Path.Combine(_solutionRoot, "docs"));

        var result = _tools.ProjectDocTool("test", DocAction.read, DocType.state);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("Document not found"));
        Assert.That(result.ErrorData.Message, Does.Contain("action: list"));
    }

    [Test]
    public void Wrapper_WriteThenList_ListSuccessDataContainsFile()
    {
        var write = _tools.ProjectDocTool("test", DocAction.write, DocType.documentation,
            name: "wrapper-list.md", content: "x\n");
        Assert.That(write.IsError, Is.False, write.Message);
        Assert.That(write.SuccessData, Is.TypeOf<DocWriteResult>());

        var list = _tools.ProjectDocTool("test", DocAction.list, DocType.documentation);

        Assert.That(list.IsError, Is.False, list.Message);
        var listed = list.SuccessData as DocListResult;
        Assert.That(listed, Is.Not.Null);
        Assert.That(listed!.Files, Has.Some.EndsWith("wrapper-list.md"));
    }

    [Test]
    public void Validate_UnknownId_ReturnsExistsFalse()
    {
        var result = (DocTrailerIdResult)_tools.ProjectDocCore("test", DocAction.validate, DocType.plan,
            name: "plan_does_not_exist");

        Assert.That(result.Exists, Is.False);
    }

    [Test]
    public void Validate_WithoutName_IsError()
    {
        var result = _tools.ProjectDocCore("test", DocAction.validate, DocType.plan);

        Assert.That(result, Is.InstanceOf<DocWriteResult>());
        Assert.That(((DocWriteResult)result).IsError, Is.True);
        Assert.That(((DocWriteResult)result).Code, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public void Read_WithoutName_IsInvalidArgument()
    {
        var result = (DocReadResult)_tools.ProjectDocCore("test", DocAction.read, DocType.plan);

        Assert.That(result.Found, Is.False);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public void Write_WithoutContent_IsInvalidArgument()
    {
        var result = (DocWriteResult)_tools.ProjectDocCore("test", DocAction.write, DocType.documentation, name: "no-content.md");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public void Close_MovesDocAndWritesClosedMdEntry()
    {
        WriteDoc(Path.Combine("current", "plans", "plan_x.md"), "# Plan X\n\n**Status:** OPEN\n");
        WriteDoc(Path.Combine("current", "CLOSED.md"), "# CLOSED\n\n## Older - resolved (2026-01-01)\n");

        var result = _tools.ProjectDocCore("test", DocAction.close, DocType.plan,
            name: "plan_x", content: "IMPLEMENTED 2026-11-10 (abc1234)");

        Assert.That(result, Is.InstanceOf<DocCloseResult>());
        var close = (DocCloseResult)result;
        Assert.That(close.Error, Is.Null);

        var closedPath = Path.Combine(_solutionRoot, "docs", "current", "plans", "closed", "plan_x.md");
        Assert.That(File.Exists(closedPath), Is.True);
        Assert.That(File.Exists(Path.Combine(_solutionRoot, "docs", "current", "plans", "plan_x.md")), Is.False);
        Assert.That(File.ReadAllText(closedPath), Does.Contain("IMPLEMENTED 2026-11-10 (abc1234)"));

        var closedMd = File.ReadAllText(Path.Combine(_solutionRoot, "docs", "current", "CLOSED.md"));
        Assert.That(closedMd.IndexOf("## ", StringComparison.Ordinal),
            Is.LessThan(closedMd.IndexOf("## Older", StringComparison.Ordinal)));
    }

    [Test]
    public void Trailers_InTestingMode_IsRefused()
    {
        var tools = BuildTools(OperatingMode.Testing);

        var result = tools.ProjectDocCore("test", DocAction.trailers, DocType.plan);

        Assert.That(result, Is.InstanceOf<DocWriteResult>());
        Assert.That(((DocWriteResult)result).IsError, Is.True);
    }
}
