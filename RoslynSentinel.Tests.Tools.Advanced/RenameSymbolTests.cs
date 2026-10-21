using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests for RenameSymbolTool; moved out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class RenameSymbolTests
{
    private const string SimpleSource = @"
namespace TestProj;

public class Order
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }

    public Order(int orderId, string customerName)
    {
        OrderId = orderId;
        CustomerName = customerName;
    }

    public string GetLabel()
    {
        return string.Format(""{0}: {1}"", OrderId, CustomerName);
    }

    public string GetStatus()
    {
        if (OrderId == 1) return ""Active"";
        if (OrderId == 2) return ""Pending"";
        return ""Unknown"";
    }
}

public interface IService
{
    string GetLabel();
}

public enum Status { Active = 1, Pending = 2 }
";

    private static readonly string CrlfSimpleSource = Crlf(SimpleSource);

    private static string Crlf(string source) => source.ReplaceLineEndings("\r\n");

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
        var symbolNavEngine = new SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
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
        Assert.That(result.ErrorData!.Message, Does.Contain("NoSuchSymbol").Or.Contain("no longer resolves"));
    }
}
