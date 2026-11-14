using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// MemberRefactoringEngine.AddMembersAsync / InsertMemberAfterAsync with a multi-declaration newMemberSource: the call-binding
/// gate compares every added declaration in one trial. A batch that re-points an existing call is refused and names all the
/// added members; a batch that keeps every binding, or whose names no existing call can reach, succeeds.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
[Category("AddMemberCallBinding")]
public class AddMemberManyCallBindingTests
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

    private static string RebindingSource() => Lines(
        "public class T",
        "{",
        "    public void Save(long v) { }",
        "    public void Run() => Save(1);",
        "}");

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
    public async Task AddMembers_BatchRebindsExistingCall_RefusedNamingAllAddedMembersAsync()
    {
        var source = RebindingSource();
        SetFiles(("T.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddMembersAsync(
            "T.cs", "T", Lines("public void Save(int v) { }", "public void Other() { }")));

        Assert.That(error!.Message, Does.Contain("add members method 'Save', method 'Other' to 'T'"), "The refusal names every added member in the plural form");
        Assert.That(error.Message, Does.Contain("Save(1)"), "The refusal quotes the existing call");
        Assert.That(error.Message, Does.Contain("Save(long)"), "The old binding is named");
        Assert.That(error.Message, Does.Contain("Save(int)"), "The new binding is named");
        Assert.That(error.Message, Does.Contain("rename the new member or members, or cast the captured call's argument to the original parameter type"), "The batch refusal offers the plural fix");
        Assert.That(error.Message, Does.Contain("Nothing was written."));
        Assert.That(await CurrentTextAsync("T.cs"), Is.EqualTo(source), "The document must be unchanged after a refused batch add");
    }

    [Test]
    public async Task AddMembers_CapturingMemberIsSecondDeclaration_RefusedAsync()
    {
        var source = RebindingSource();
        SetFiles(("T.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddMembersAsync(
            "T.cs", "T", Lines("public void Other() { }", "public void Save(int v) { }")));

        Assert.That(error!.Message, Does.Contain("add members method 'Other', method 'Save' to 'T'"), "The refusal names every added member in source order");
        Assert.That(error.Message, Does.Contain("Save(1)"), "The refusal quotes the existing call captured by the second declaration");
        Assert.That(error.Message, Does.Contain("Save(long)"), "The old binding is named");
        Assert.That(error.Message, Does.Contain("Save(int)"), "The new binding is named");
        Assert.That(error.Message, Does.Contain("Nothing was written."));
        Assert.That(await CurrentTextAsync("T.cs"), Is.EqualTo(source), "The document must be unchanged after a refused batch add");
    }

    [Test]
    public async Task AddMembers_NewCallerAndNewHelper_NoExistingCallReferstoEither_SucceedsAsync()
    {
        SetFiles(("T.cs", Lines(
            "public class T",
            "{",
            "    public void Run() => Helper(5);",
            "    private int Helper(long v) => 0;",
            "}")));

        var result = await _memberRefactoringEngine.AddMembersAsync("T.cs", "T", Lines(
            "public int Compute() => Helper() + 1;",
            "private int Helper() => 41;"));

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public int Compute() => Helper() + 1;"), "The new caller is added");
        Assert.That(result.UpdatedText, Does.Contain("private int Helper() => 41;"), "The new helper is added");
        Assert.That(result.UpdatedText, Does.Contain("Helper(5);"), "The existing call is untouched");
    }

    [Test]
    public async Task AddMembers_NewFieldInitializerCallsAddedHelper_SucceedsAsync()
    {
        SetFiles(("T.cs", Lines(
            "public class T",
            "{",
            "    public void Run() => Helper(5);",
            "    private int Helper(long v) => 0;",
            "}")));

        var result = await _memberRefactoringEngine.AddMembersAsync("T.cs", "T", Lines(
            "private int _cached = Helper();",
            "private int Helper() => 41;"));

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("private int _cached = Helper();"), "The new field is added");
        Assert.That(result.UpdatedText, Does.Contain("private int Helper() => 41;"), "The new helper is added");
        Assert.That(result.UpdatedText, Does.Contain("Helper(5);"), "The existing call is untouched");
    }

    [Test]
    public async Task AddMembers_FieldsOnly_UncheckedKind_SucceedsEvenWhenNameIsBoundAsync()
    {
        // Save(1) is bound today; fields are not a checked kind, so the batch needs no comparison.
        SetFiles(("T.cs", RebindingSource()));

        var result = await _memberRefactoringEngine.AddMembersAsync("T.cs", "T", Lines(
            "private int _a = 1;",
            "private string _b = \"x\";"));

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("private int _a = 1;"), "The first field is added");
        Assert.That(result.UpdatedText, Does.Contain("private string _b = \"x\";"), "The second field is added");
        Assert.That(result.UpdatedText, Does.Contain("Save(1)"), "The existing call is untouched");
    }

    [Test]
    public async Task AddMembers_TwoUniquelyNamedMethods_SkipsCheckAndSucceedsAsync()
    {
        SetFiles(("T.cs", Lines(
            "public class T",
            "{",
            "    public void Run() { }",
            "}")));

        var result = await _memberRefactoringEngine.AddMembersAsync("T.cs", "T", Lines(
            "public void UniqueOne() { }",
            "public void UniqueTwo() { }"));

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public void UniqueOne() { }"), "The first method is added");
        Assert.That(result.UpdatedText, Does.Contain("public void UniqueTwo() { }"), "The second method is added");
    }

    [Test]
    public async Task InsertMemberAfter_BatchRebindsExistingCall_RefusedIdenticallyToAddMembersAsync()
    {
        var source = RebindingSource();
        SetFiles(("T.cs", source));
        var batch = Lines("public void Save(int v) { }", "public void Other() { }");

        var fromAdd = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddMembersAsync("T.cs", "T", batch));
        var fromInsert = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.InsertMemberAfterAsync("T.cs", "T", "Run", batch));

        Assert.That(fromInsert!.Message, Does.Contain("add members method 'Save', method 'Other' to 'T'"), "insertAfter refusal names every added member");
        Assert.That(fromInsert.Message, Does.Contain("Save(1)"), "insertAfter refusal quotes the existing call");
        Assert.That(fromInsert.Message, Does.Contain("Nothing was written."));
        Assert.That(fromInsert.Message, Is.EqualTo(fromAdd!.Message), "insertAfter refusal must match the AddMembersAsync refusal");
        Assert.That(await CurrentTextAsync("T.cs"), Is.EqualTo(source), "insertAfter must leave the document unchanged");
    }
}
