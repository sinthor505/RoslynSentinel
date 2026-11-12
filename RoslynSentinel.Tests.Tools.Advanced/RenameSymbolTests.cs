using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using static RoslynSentinel.Tests.Support.Sources;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests for RenameSymbolTool; moved out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class RenameSymbolTests
{
    private static readonly string SimpleSource = Sources.Order;

    private static readonly string CrlfSimpleSource = Crlf(SimpleSource);

    // RenameSymbolTool takes a SymbolHandle (projectName, docCommentId) instead of (filepath, methodName, contextSnippet),
    // so the handle is located via SymbolNavigationEngine first, matching how an agent calls LocateSymbolTool before RenameSymbolTool.
    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    [Category("SymbolLocation")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task RenameSymbol_ValidSymbol_RewritesDeclaration()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var symbolNavEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        // "GetLabel" is declared on both Order and IService in SimpleSource -> disambiguate.
        var located = await symbolNavEngine.LocateSymbolAsync("GetLabel", containingType: "Order");
        var handle = located.Single();
        var result = await tools.RenameSymbolTool(reason: "test message", projectName: handle.ProjectName, docCommentId: handle.DocCommentId!, newName: "GetDisplayLabel");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var orderText = workspace.ReadText("Order.cs");
        Assert.That(orderText, Does.Contain("public string GetDisplayLabel()"));
        Assert.That(orderText, Does.Not.Contain("public string GetLabel()"));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task RenameSymbol_NonExistentSymbol_ReturnsErrorObject()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.RenameSymbolTool(reason: "test message", projectName: "TestProj", docCommentId: "M:TestProj.Order.NoSuchSymbol", newName: "NewName");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
        // FakeWorkspaceManager shares the production not-found path (SymbolWireResolver): the wanted name is echoed and, with no
        // similar declared name in the project, the text says so and offers to report the symbol as non-existent.
        Assert.That(result.ErrorData!.Message, Does.Contain("NoSuchSymbol"));
        Assert.That(result.ErrorData!.Message, Does.Contain("No symbol in the solution has a similar name").And.Contain("the symbol does not exist in this solution.").And.Not.Contain("tell the user"));
        Assert.That(result.ErrorData!.Message, Does.Not.Contain("no longer resolves"));
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("NotFound"));
    }

    // A rename onto an existing signature must be refused by the compile gate, with the duplicate-definition hint, and leave the file untouched.
    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_NewNameCollidesWithExistingMemberSignature_IsRefusedAndWritesNothing()
    {
        const string collideSource = "namespace TestProj;\npublic class Widget\n{\n    public int Alpha() => 1;\n    public int Beta() => 2;\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Collide.cs", Crlf(collideSource)));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var symbolNavEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var located = await symbolNavEngine.LocateSymbolAsync("Alpha", containingType: "Widget");
        var handle = located.Single();
        var result = await tools.RenameSymbolTool(reason: "test message", projectName: handle.ProjectName, docCommentId: handle.DocCommentId!, newName: "Beta");
        var errorText = $"{result.ErrorData?.Message}\n{result.ErrorData?.Detail}";
        var diag = $"errorText=[{errorText}]";
        Assert.That(result.IsError, Is.True, diag);
        Assert.That(errorText, Does.Contain("collides with an existing declaration"), diag);
        Assert.That(errorText, Does.Contain("CS0111"), diag);
        var collideText = workspace.ReadText("Collide.cs");
        Assert.That(collideText, Does.Contain("public int Alpha()"), "The refused rename must leave Alpha in place.");
        Assert.That(collideText.Split("Beta(").Length - 1, Is.EqualTo(1), "The refused rename must not write a second Beta.");
    }
}
