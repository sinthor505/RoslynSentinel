using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Miss text of DocumentationTools (ProjectDoc action: read): a bare name that matches no file, a path-qualified name that matches no
// file, and a read of the fixed docType state file. The wanted name is ranked against the docs under docs/ (the docType's own files
// first) with a prefilled read retry, or the no-similar-name fact with the number of files searched, then the list option
// (plan_not_found_error_orientation, Step 5e slice 5). A read never writes, so each test also asserts the docs tree is unchanged.
[TestFixture]
[Category("DocumentationTools")]
public sealed class ProjectDocMissTests
{
    private string _solutionRoot = "";
    private DocumentationTools _tools = null!;

    [SetUp]
    public void SetUp()
    {
        _solutionRoot = Path.Combine(Path.GetTempPath(), "ProjectDocMissTests", Path.GetRandomFileName());
        Directory.CreateDirectory(_solutionRoot);
        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly)
        {
            SolutionPath = Path.Combine(_solutionRoot, "Fake.slnx")
        };
        _tools = new DocumentationTools(workspaceManager, new SentinelHostOptions { OperatingMode = OperatingMode.Production }, NullLogger<DocumentationTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_solutionRoot, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private void WriteDoc(string relativePathUnderDocs, string content)
    {
        var fullPath = Path.Combine(_solutionRoot, "docs", relativePathUnderDocs);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private IReadOnlyList<string> DocsTree() =>
        Directory.GetFiles(Path.Combine(_solutionRoot, "docs"), "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
        Assert.That(message, Does.Not.Contain("Call "), "Next calls are options (named as what they do), not orders.");
    }

    private SentinelCallToolResult<object> Read(DocType docType, string? name) =>
        _tools.ProjectDocTool(reason: "test message", DocAction.read, docType, name: name);

    private static string ListOption() =>
        $"{ToolCallHint.Call("ProjectDoc", ("action", "list"))} lists every file under docs/.";

    // ---- bare name ----

    [Test]
    public void Read_MisspeltBareName_OffersTheNearDocWithAPrefilledRetryThatResolves()
    {
        WriteDoc(Path.Combine("current", "plans", "order-pricing-plan.md"), "plan content");
        var before = DocsTree();

        var result = Read(DocType.plan, "order-pricing-plam.md");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No file matching 'order-pricing-plam' (with or without extension) was found under docType='plan'"));
        var retry = ToolCallHint.Call("ProjectDoc", ("action", "read"), ("docType", "plan"), ("name", "current/plans/order-pricing-plan.md"));
        Assert.That(message, Does.Contain($"Did you mean 'current/plans/order-pricing-plan.md'? Retry with {retry}."));
        Assert.That(message, Does.Contain(ListOption()));
        AssertFactsAndOptionsOnly(message);
        Assert.That(DocsTree(), Is.EqualTo(before), "A refused read must not write.");

        var retried = Read(DocType.plan, "current/plans/order-pricing-plan.md");
        Assert.That(retried.IsError, Is.False, retried.ErrorData?.Message);
        Assert.That(((DocReadResult)retried.SuccessData!).Content, Is.EqualTo("plan content"), "The named retry reads the offered doc.");
    }

    [Test]
    public void Read_NoSimilarBareName_StatesTheNoCandidateFactAndNamesTheListOption()
    {
        WriteDoc(Path.Combine("current", "plans", "order-pricing-plan.md"), "plan content");
        var before = DocsTree();

        var result = Read(DocType.plan, "zzz-unrelated.md");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("No file under docs/ has a name similar to 'zzz-unrelated' (1 file(s) searched)."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain(ListOption()));
        AssertFactsAndOptionsOnly(message);
        Assert.That(DocsTree(), Is.EqualTo(before), "A refused read must not write.");
    }

    // ---- path-qualified name ----

    [Test]
    public void Read_MisspeltPathQualifiedName_OffersTheNearDocAndStatesNoBasenameFallbackRan()
    {
        WriteDoc(Path.Combine("current", "plans", "order-pricing-plan.md"), "plan content");
        var before = DocsTree();

        var result = Read(DocType.plan, "plans/order-pricing-plam.md");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("'plans/order-pricing-plam.md' was not found under docType='plan', nor at that path relative to the docs root"));
        Assert.That(message, Does.Contain("no basename fallback was attempted"));
        Assert.That(message, Does.Contain("a same-named file in a different directory is not substituted"));
        var retry = ToolCallHint.Call("ProjectDoc", ("action", "read"), ("docType", "plan"), ("name", "current/plans/order-pricing-plan.md"));
        Assert.That(message, Does.Contain($"Did you mean 'current/plans/order-pricing-plan.md'? Retry with {retry}."));
        Assert.That(message, Does.Contain(ListOption()));
        AssertFactsAndOptionsOnly(message);
        Assert.That(DocsTree(), Is.EqualTo(before), "A refused read must not write.");
    }

    [Test]
    public void Read_NoSimilarPathQualifiedName_StatesTheNoCandidateFactAndNamesTheListOption()
    {
        WriteDoc(Path.Combine("current", "plans", "order-pricing-plan.md"), "plan content");

        var result = Read(DocType.plan, "plans/zzz-unrelated.md");

        Assert.That(result.IsError, Is.True);
        var message = result.ErrorData!.Message;
        Assert.That(message, Does.Contain("no basename fallback was attempted"));
        Assert.That(message, Does.Contain("No file under docs/ has a name similar to 'zzz-unrelated' (1 file(s) searched)."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain(ListOption()));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- docType state: the path is fixed, so the miss is a state-only fact plus the list option (and a near doc when one exists) ----

    [Test]
    public void Read_MissingStateFileWithANearDoc_StatesTheFixedPathAndOffersTheNearDocAsADocumentationRead()
    {
        WriteDoc("migration-state.yml", "state: other");
        var before = DocsTree();

        var result = Read(DocType.state, null);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData.Message;
        Assert.That(message, Does.Contain("'migration-state.yaml' (docType state) does not exist at docs/migration-state.yaml; the path is fixed and name is ignored for docType state."));
        var retry = ToolCallHint.Call("ProjectDoc", ("action", "read"), ("docType", "documentation"), ("name", "migration-state.yml"));
        Assert.That(message, Does.Contain($"Did you mean 'migration-state.yml'? Retry with {retry}."));
        Assert.That(message, Does.Contain(ListOption()));
        AssertFactsAndOptionsOnly(message);
        Assert.That(DocsTree(), Is.EqualTo(before), "A refused read must not write.");
    }
}
