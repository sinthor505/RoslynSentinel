using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests; split out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class UsingDocsTypedMemberTests
{
    private static readonly string SimpleSource = Sources.Order;

    // The original fixture was CRLF (a verbatim literal inside a CRLF file); the EOL guard compares against the document's endings.
    private static readonly string CrlfSimpleSource = SimpleSource.ReplaceLineEndings("\r\n");

    // --- UsingDirectiveTool ---
    [Test]
    [Category("RefactoringExtractionDocsTools")] // sentinel:auto-category
    public async Task UsingDirective_AddSystemLinq_AddsUsingLine()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);
        var result = await tools.UsingDirectiveTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.add, "System.Linq");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("using System.Linq;"));
    }

    // --- SummaryCommentTool ---
    [Test]
    [Category("RefactoringExtractionDocsTools")] // sentinel:auto-category
    public async Task SummaryComment_AddOnClass_InsertsSummaryText()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);
        var result = await tools.SummaryCommentTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.add, "Order", "Represents an order.");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("Represents an order."));
    }

    // --- ExtractLocalVariableTool ---
    [Test]
    [Category("RefactoringExtractionDocsTools")] // sentinel:auto-category
    public async Task ExtractLocalVariable_ValidContext_DeclaresLocal()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);
        var result = await tools.ExtractLocalVariableTool(reason: "test message", workspace.PathOf("Order.cs"), "OrderId == 1", "isActive");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Match(@"isActive\s*=\s*OrderId == 1;"));
    }

    [Test]
    [Category("RefactoringExtractionDocsTools")] // sentinel:auto-category
    public async Task ExtractLocalVariable_NonExistentFile_ReturnsStructuredError()
    {
        using var workspace = InMemoryWorkspace.Create(("Test.cs", "public class C {}".ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);
        var result = await tools.ExtractLocalVariableTool(reason: "test message", workspace.PathOf("NonExistent.cs"), "GetLabel", "label");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("NonExistent.cs"));
    }

    // --- AddProperty (TypedMemberTool) ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddProperty_NewStringProperty_PropertyIsPresent()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateTypedMemberTools(workspace.Manager);
        var result = await tools.TypedMemberTool(reason: "test message", workspace.PathOf("Order.cs"), containerName: "Order", typedKind: TypedMemberKind.property, typedName: "Description", typedType: "string");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("string Description"));
    }

    // --- AddField (TypedMemberTool) ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddField_NewStringField_FieldIsPresent()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateTypedMemberTools(workspace.Manager);
        var result = await tools.TypedMemberTool(reason: "test message", workspace.PathOf("Order.cs"), containerName: "Order", typedKind: TypedMemberKind.field, typedName: "_tag", typedType: "string");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("string _tag"));
    }
}
