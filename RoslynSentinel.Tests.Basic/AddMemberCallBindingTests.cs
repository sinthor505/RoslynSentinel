using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// MemberRefactoringEngine.AddMemberAsync / InsertMemberAfterAsync / InsertMemberBeforeAsync and the W-add call-binding gate:
/// an added method or property that would re-point an existing call is refused before anything is written; an add that keeps
/// every binding (or that no call can reach) still succeeds.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
[Category("AddMemberCallBinding")]
public class AddMemberCallBindingTests
{
    private IWorkspaceManager _workspaceManager;

    private MemberRefactoringEngine _memberRefactoringEngine;

    private SymbolNavigationEngine _symbolNavigationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _memberRefactoringEngine = Battery.Basic.TestEngines.MemberRefactoringEngine(
            _workspaceManager,
            _symbolNavigationEngine,
            Battery.Basic.TestEngines.ValidationEngine(_workspaceManager));
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private static string Lines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private void SetFiles(params (string FileName, string Source)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    private async Task<string> CurrentTextAsync(string fileName)
    {
        var document = _workspaceManager.CurrentSolution!.Projects.SelectMany(p => p.Documents).Single(d => d.Name == fileName);
        return (await document.GetTextAsync()).ToString();
    }

    [Test]
    public async Task AddMember_RebindsExistingCall_RefusedNamingCallAndBindingsAsync()
    {
        var source = Lines(
            "public class T",
            "{",
            "    public void Save(long v) { }",
            "    public void Run() => Save(1);",
            "}");
        SetFiles(("T.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddMemberAsync("T.cs", "T", "public void Save(int v) { }"));

        Assert.That(error!.Message, Does.Contain("Save(1)"), "The refusal quotes the existing call");
        Assert.That(error.Message, Does.Contain("T.cs:"), "The site is reported with its file and line");
        Assert.That(error.Message, Does.Contain("Save(long)"), "The old binding is named");
        Assert.That(error.Message, Does.Contain("Save(int)"), "The new binding is named");
        Assert.That(error.Message, Does.Contain("cast the captured call's argument to the original parameter type"), "The fix hint offers a cast");
        Assert.That(error.Message, Does.Contain("rename the new member, or cast the captured call's argument to the original parameter type"), "The add refusal offers the add-specific fix");
        Assert.That(error.Message, Does.Not.Contain("leave the member where it is"), "The add refusal must not offer the default leave-in-place fix");
        Assert.That(error.Message, Does.Contain("Nothing was written."));
        Assert.That(await CurrentTextAsync("T.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task InsertMemberAfterAndBefore_RebindingAdd_RefusedAndNothingWrittenAsync()
    {
        var source = Lines(
            "public class T",
            "{",
            "    public void Save(long v) { }",
            "    public void Run() => Save(1);",
            "}");
        SetFiles(("T.cs", source));

        var after = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.InsertMemberAfterAsync("T.cs", "T", "Save", "public void Save(int v) { }"));
        Assert.That(after!.Message, Does.Contain("Save(1)"), "insertAfter refusal quotes the existing call");
        Assert.That(after.Message, Does.Contain("Nothing was written."));
        Assert.That(await CurrentTextAsync("T.cs"), Is.EqualTo(source), "insertAfter must leave the document unchanged");

        var before = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.InsertMemberBeforeAsync("T.cs", "T", "Run", "public void Save(int v) { }"));
        Assert.That(before!.Message, Does.Contain("Save(1)"), "insertBefore refusal quotes the existing call");
        Assert.That(before.Message, Does.Contain("Nothing was written."));
        Assert.That(await CurrentTextAsync("T.cs"), Is.EqualTo(source), "insertBefore must leave the document unchanged");
    }

    [Test]
    public async Task AddInstanceMember_BeatsExistingExtensionCall_RefusedAndNothingWrittenAsync()
    {
        var source = Lines(
            "public static class Ext",
            "{",
            "    public static void Save(this T t, int v) { }",
            "}",
            "public class T",
            "{",
            "    public void Run()",
            "    {",
            "        var t = new T();",
            "        t.Save(1);",
            "    }",
            "}");
        SetFiles(("T.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddMemberAsync("T.cs", "T", "public void Save(int v) { }"));

        Assert.That(error!.Message, Does.Contain("Save(1)"), "The refusal quotes the extension call that the instance member would capture");
        Assert.That(error.Message, Does.Contain("T.cs:"), "The site is reported with its file and line");
        Assert.That(error.Message, Does.Contain("Nothing was written."));
        Assert.That(await CurrentTextAsync("T.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task AddOverload_NotCapturingAnyCall_SucceedsAsync()
    {
        SetFiles(("T.cs", Lines(
            "public class T",
            "{",
            "    public void Save(long v) { }",
            "    public void Run()",
            "    {",
            "        Save(1);",
            "        Save(2L);",
            "    }",
            "}")));

        var result = await _memberRefactoringEngine.AddMemberAsync("T.cs", "T", "public void Save(string s) { }");

        Assert.That(result.UpdatedText, Does.Contain("public void Save(string s) { }"), "The overload is added");
        Assert.That(result.UpdatedText, Does.Contain("public void Save(long v) { }"), "The existing overload is kept");
        Assert.That(result.UpdatedText, Does.Contain("Save(1);"), "Existing int call is untouched");
        Assert.That(result.UpdatedText, Does.Contain("Save(2L);"), "Existing long call is untouched");
    }

    [Test]
    public async Task AddOverload_WithNameofOfMethodGroup_SucceedsAsync()
    {
        SetFiles(("T.cs", Lines(
            "public class T",
            "{",
            "    public void Save(long v) { }",
            "    public string Name => nameof(Save);",
            "}")));

        var result = await _memberRefactoringEngine.AddMemberAsync("T.cs", "T", "public void Save(string s) { }");

        Assert.That(result.UpdatedText, Does.Contain("public void Save(string s) { }"), "The overload is added");
        Assert.That(result.UpdatedText, Does.Contain("nameof(Save)"), "The nameof stays as written");
    }

    [Test]
    public async Task NameAppearsAsync_ReturnsFalseForUnusedName_TrueForUsedName()
    {
        SetFiles(("T.cs", Lines(
            "public class T",
            "{",
            "    public void Save(long v) { }",
            "    public void Run() => Save(1);",
            "}")));
        var solution = _workspaceManager.CurrentSolution!;

        Assert.That(await AddMemberCallBindingCheck.NameAppearsAsync(solution, "Unused"), Is.False, "A name in no document cannot be captured");
        Assert.That(await AddMemberCallBindingCheck.NameAppearsAsync(solution, "Save"), Is.True, "A name in a document must be checked");
    }

    [Test]
    public async Task AddUniquelyNamedMethod_SkipsCheckAndSucceedsAsync()
    {
        SetFiles(("T.cs", Lines(
            "public class T",
            "{",
            "    public void Run() { }",
            "}")));

        var result = await _memberRefactoringEngine.AddMemberAsync("T.cs", "T", "public void Uniquely() { }");

        Assert.That(result.UpdatedText, Does.Contain("public void Uniquely() { }"), "The method is added");
    }

    [Test]
    public async Task CheckAsync_NameOnlyInCommentStringAndDeclaration_IsOkWithNoSitesChecked()
    {
        var source = Lines(
            "public class T",
            "{",
            "    // Save(1) is mentioned only in this comment",
            "    public string Text => \"Save(2)\";",
            "    public void Save(long v) { }",
            "}");
        var updated = Lines(
            "public class T",
            "{",
            "    // Save(1) is mentioned only in this comment",
            "    public string Text => \"Save(2)\";",
            "    public void Save(long v) { }",
            "    public void Save(int v) { }",
            "}");
        SetFiles(("T.cs", source));
        var solution = _workspaceManager.CurrentSolution!;
        var document = solution.Projects.Single().Documents.Single(d => d.Name == "T.cs");
        var root = await document.GetSyntaxRootAsync();
        var container = root!.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var newMember = SyntaxFactory.ParseMemberDeclaration("public void Save(int v) { }")!;

        var check = await AddMemberCallBindingCheck.CheckAsync(solution, document.Id, container, newMember, container.Members.Count, updated);

        Assert.That(check.Status, Is.EqualTo(CallBindingCheckStatus.Clean), "A name with no bound call cannot be re-pointed");
        Assert.That(check.IsRefusal, Is.False);
        Assert.That(check.SitesChecked, Is.EqualTo(0), "Comments, strings and declarations are not call sites");
    }

    [Test]
    public async Task AddProperty_HidingBaseMemberThatExistingCodeReads_RefusedAndNothingWrittenAsync()
    {
        var source = Lines(
            "public class Base",
            "{",
            "    public int Count { get; }",
            "}",
            "public class Derived : Base",
            "{",
            "    public int Read() => Count;",
            "}");
        SetFiles(("T.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddMemberAsync("T.cs", "Derived", "public int Count => 0;"));

        Assert.That(error!.Message, Does.Contain("'Count'"), "The refusal names the property");
        Assert.That(error.Message, Does.Contain("T.cs:"), "The site is reported with its file and line");
        Assert.That(error.Message, Does.Contain("Nothing was written."));
        Assert.That(await CurrentTextAsync("T.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task AddProperty_CollidingWithNothing_SucceedsAsync()
    {
        SetFiles(("T.cs", Lines(
            "public class T",
            "{",
            "    public void Run() { }",
            "}")));

        var result = await _memberRefactoringEngine.AddMemberAsync("T.cs", "T", "public int Total { get; set; }");

        Assert.That(result.UpdatedText, Does.Contain("public int Total { get; set; }"), "The property is added");
    }
}
