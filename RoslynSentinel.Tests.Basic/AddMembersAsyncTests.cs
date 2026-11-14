using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// MemberRefactoringEngine.AddMembersAsync: one write for N declarations (source order, one blank line between members),
/// parity with AddMemberAsync for N = 1, the declaration-count and brace rejections, and the single-declaration guard that
/// the typed AddFieldAsync/AddPropertyAsync callers keep. None of these tests write to the workspace; each asserts the
/// document is unchanged after a refusal.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
[Category("AddMembers")]
public class AddMembersAsyncTests
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

    private static string BaseSource() => Lines(
        "namespace Scratch;",
        "",
        "public class Svc",
        "{",
        "    public int Existing() => 0;",
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

    [TestCase("public void M() { }", TestName = "ParityN1_Method")]
    [TestCase("public int P { get; set; }", TestName = "ParityN1_Property")]
    [TestCase("private int f = 1;", TestName = "ParityN1_Field")]
    [TestCase("public int E() =>\r\n        2;", TestName = "ParityN1_ExpressionBodiedTwoLines")]
    public async Task ParityN1_AddMembersAsyncMatchesAddMemberAsync(string memberSource)
    {
        SetFiles(("Svc.cs", BaseSource()));

        var multi = await _memberRefactoringEngine.AddMembersAsync("Svc.cs", "Svc", memberSource);
        var single = await _memberRefactoringEngine.AddMemberAsync("Svc.cs", "Svc", memberSource);

        Assert.That(multi.UpdatedText, Is.EqualTo(single.UpdatedText), "N = 1 through AddMembersAsync must match AddMemberAsync");
        Assert.That(multi.Message, Is.EqualTo(single.Message), "N = 1 message must match AddMemberAsync");
        Assert.That(single.Message, Does.StartWith("// Added "));
        Assert.That(multi.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(single.Outcome, Is.EqualTo(EditOutcome.Modified));
    }

    [Test]
    public async Task AppendTwo_ByteExact_SourceOrderAfterLastSiblingAsync()
    {
        SetFiles(("Svc.cs", BaseSource()));

        var result = await _memberRefactoringEngine.AddMembersAsync("Svc.cs", "Svc",
            Lines("public void A()", "    {", "    }", "public void B()", "    {", "    }"));

        Assert.That(result.UpdatedText, Is.EqualTo(Lines(
            "namespace Scratch;",
            "",
            "public class Svc",
            "{",
            "    public int Existing() => 0;",
            "",
            "    public void A()",
            "    {",
            "    }",
            "",
            "    public void B()",
            "    {",
            "    }",
            "}")));
        Assert.That(result.Message, Is.EqualTo("// Added 2 members: Method 'A', Method 'B'."));
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
    }

    [Test]
    public async Task AppendTwo_CallerBeforeHelper_KeepsSourceOrderAsync()
    {
        SetFiles(("Svc.cs", BaseSource()));

        var result = await _memberRefactoringEngine.AddMembersAsync("Svc.cs", "Svc",
            Lines("public int Run() => Helper() + 1;", "private int Helper() => 41;"));

        Assert.That(result.UpdatedText, Is.EqualTo(Lines(
            "namespace Scratch;",
            "",
            "public class Svc",
            "{",
            "    public int Existing() => 0;",
            "",
            "    public int Run() => Helper() + 1;",
            "",
            "    private int Helper() => 41;",
            "}")));
        Assert.That(result.Message, Is.EqualTo("// Added 2 members: Method 'Run', Method 'Helper'."));
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
    }

    [Test]
    public async Task AppendThree_ByteExact_SourceOrderAfterLastSiblingAsync()
    {
        SetFiles(("Svc.cs", BaseSource()));

        var result = await _memberRefactoringEngine.AddMembersAsync("Svc.cs", "Svc",
            Lines("public void X()", "    {", "    }", "public void Y()", "    {", "    }", "public void Z()", "    {", "    }"));

        Assert.That(result.UpdatedText, Is.EqualTo(Lines(
            "namespace Scratch;",
            "",
            "public class Svc",
            "{",
            "    public int Existing() => 0;",
            "",
            "    public void X()",
            "    {",
            "    }",
            "",
            "    public void Y()",
            "    {",
            "    }",
            "",
            "    public void Z()",
            "    {",
            "    }",
            "}")));
        Assert.That(result.Message, Is.EqualTo("// Added 3 members: Method 'X', Method 'Y', Method 'Z'."));
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
    }

    [Test]
    public async Task EnumContainer_RefusedNamingModifyEnumAndNothingWrittenAsync()
    {
        var source = Lines("namespace Scratch;", "", "public enum Color", "{", "    Red,", "    Blue", "}");
        SetFiles(("Svc.cs", source));

        var error = await Assert.ThrowsAsync<ToolTargetIneligibleException>(async () => await _memberRefactoringEngine.AddMembersAsync("Svc.cs", "Color", "public void A() { }"));

        Assert.That(error!.Message, Does.Contain("ModifyEnum"));
        Assert.That(await CurrentTextAsync("Svc.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task UnknownContainer_NotFoundAndNothingWrittenAsync()
    {
        var source = BaseSource();
        SetFiles(("Svc.cs", source));

        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _memberRefactoringEngine.AddMembersAsync("Svc.cs", "Missing", "public void A() { }"));

        Assert.That(await CurrentTextAsync("Svc.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task MalformedSecondDeclaration_NamesDeclarationTwoAndNothingWrittenAsync()
    {
        var source = BaseSource();
        SetFiles(("Svc.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddMembersAsync("Svc.cs", "Svc",
            Lines("public void A() { }", "public int B() => ;", "public void C() { }")));

        Assert.That(error!.Message, Does.Contain("declaration 2"));
        Assert.That(await CurrentTextAsync("Svc.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task ValidMethodPlusStatement_RefusedAsTrailingDeclarationAndNothingWrittenAsync()
    {
        var source = BaseSource();
        SetFiles(("Svc.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddMembersAsync("Svc.cs", "Svc",
            Lines("public void A() { }", "return 1;")));

        Assert.That(error!.Message, Does.StartWith("newMemberSource is invalid:"));
        Assert.That(error.Message, Does.Not.Contain("declaration 2"), "A single valid declaration plus a statement is not a malformed second declaration");
        Assert.That(await CurrentTextAsync("Svc.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task StrayClosingBraceAfterTwoDeclarations_RefusedAndNothingWrittenAsync()
    {
        var source = BaseSource();
        SetFiles(("Svc.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddMembersAsync("Svc.cs", "Svc",
            Lines("public void A() { }", "public void B() { }", "}")));

        Assert.That(error!.Message, Does.Contain("extra or unbalanced '}'"));
        Assert.That(await CurrentTextAsync("Svc.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task TypedFieldInitializerWithTwoDeclarations_RefusedAndNothingWrittenAsync()
    {
        var source = BaseSource();
        SetFiles(("Svc.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddFieldAsync(
            "Svc.cs", "Svc", "x", "int", initializer: "1; private int z = 2"));

        Assert.That(error!.Message, Does.Contain("2 member declarations"));
        Assert.That(error.Message, Does.Contain("addMember takes exactly one member per call"));
        Assert.That(await CurrentTextAsync("Svc.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task TypedConstFieldInitializerWithTwoDeclarations_RefusedAndNothingWrittenAsync()
    {
        var source = BaseSource();
        SetFiles(("Svc.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddFieldAsync(
            "Svc.cs", "Svc", "x", "int", isConst: true, initializer: "1; private const int z = 2"));

        Assert.That(error!.Message, Does.Contain("2 member declarations"));
        Assert.That(error.Message, Does.Contain("addMember takes exactly one member per call"));
        Assert.That(await CurrentTextAsync("Svc.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task TypedPropertyNameWithTwoDeclarations_RefusedAndNothingWrittenAsync()
    {
        var source = BaseSource();
        SetFiles(("Svc.cs", source));

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await _memberRefactoringEngine.AddPropertyAsync(
            "Svc.cs", "Svc", "P { get; set; } public int Q", "int"));

        Assert.That(error!.Message, Does.Contain("2 member declarations"));
        Assert.That(error.Message, Does.Contain("addMember takes exactly one member per call"));
        Assert.That(await CurrentTextAsync("Svc.cs"), Is.EqualTo(source), "The document must be unchanged after a refused add");
    }

    [Test]
    public async Task TypedCallersWithSingleDeclaration_StillAddAsync()
    {
        SetFiles(("Svc.cs", BaseSource()));

        var field = await _memberRefactoringEngine.AddFieldAsync("Svc.cs", "Svc", "x", "int", initializer: "1");
        Assert.That(field.UpdatedText, Does.Contain("private int x = 1;"));

        var constField = await _memberRefactoringEngine.AddFieldAsync("Svc.cs", "Svc", "k", "int", isConst: true, initializer: "1");
        Assert.That(constField.UpdatedText, Does.Contain("private const int k = 1;"));

        var property = await _memberRefactoringEngine.AddPropertyAsync("Svc.cs", "Svc", "P", "int");
        Assert.That(property.UpdatedText, Does.Contain("public int P { get; set; }"));
    }
}
