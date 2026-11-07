using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Common;

using System.Text.Json;

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

    [Test]
    [Category("RefactoringExtractionDocsTools")] // sentinel:auto-category
    public async Task SummaryComment_RemoveWithNoSummary_IsNoWriteSuccess()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);
        var result = await tools.SummaryCommentTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.remove, "Order");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(CrlfSimpleSource));
        Assert.That(result.StatusMessage, Does.Not.Contain("Removed"));
    }

    [Test]
    [Category("RefactoringExtractionDocsTools")]
    public async Task SummaryComment_ViewConstructorWithNoSummary_ReturnsExplanationNotAnEmptyObject()
    {
        var original = "public class Widget\n{\n    public Widget(int x) { }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Widget.cs", original));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);
        var result = await tools.SummaryCommentTool(reason: "test message", workspace.PathOf("Widget.cs"), AddRemoveViewAction.view, "Widget");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var view = result.SuccessData as SummaryCommentViewResult;
        Assert.That(view, Is.Not.Null);
        Assert.That(view!.SummaryText, Is.Null);
        Assert.That(view.Message, Does.Contain("No summary comment present").And.Contain("Widget"), "A missing summary must say so, not serialize as {}.");
        Assert.That(workspace.ReadText("Widget.cs"), Is.EqualTo(original), "A view must not write.");
    }

    [Test]
    [Category("RefactoringExtractionDocsTools")]
    public async Task SummaryComment_ViewMemberWithSummary_ReturnsTextAndNoMessage()
    {
        var original = "public class Widget\n{\n    /// <summary>Builds a widget.</summary>\n    public Widget(int x) { }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Widget.cs", original));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);
        var result = await tools.SummaryCommentTool(reason: "test message", workspace.PathOf("Widget.cs"), AddRemoveViewAction.view, "Widget");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var view = result.SuccessData as SummaryCommentViewResult;
        Assert.That(view, Is.Not.Null);
        Assert.That(view!.SummaryText, Is.EqualTo("Builds a widget."));
        Assert.That(view.Message, Is.Null);
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
        Assert.That(result.Code, Is.EqualTo("NotFound"));
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("NotFound"));
    }

    [Test]
    [Category("RefactoringExtractionDocsTools")] // sentinel:auto-category
    public async Task ExtractLocalVariable_ExpressionAlreadyLocal_IsNoWriteSuccess()
    {
        const string source = "public class C\r\n{\r\n    public int Compute(int n) => n;\r\n    public void M()\r\n    {\r\n        var x = Compute(7);\r\n    }\r\n}";
        using var workspace = InMemoryWorkspace.Create(("C.cs", source));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);
        var result = await tools.ExtractLocalVariableTool(reason: "test message", workspace.PathOf("C.cs"), "Compute(7)", "y");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(result.SuccessData, Is.TypeOf<AppliedChangeSummary>());
        Assert.That(((AppliedChangeSummary)result.SuccessData!).AffectedFiles, Is.Empty);
        Assert.That(result.StatusMessage, Does.Not.StartWith("Extracted"));
        Assert.That(workspace.ReadText("C.cs"), Is.EqualTo(source));
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

    // --- SummaryComment preferredTarget default (W15 part B) ---
    [Test]
    [Category("RefactoringExtractionDocsTools")]
    public void SummaryCommentTool_PreferredTargetParameter_IsEnumDefaultingToConstructor()
    {
        var parameter = typeof(RefactoringExtractionDocsTools).GetMethod(nameof(RefactoringExtractionDocsTools.SummaryCommentTool))!
            .GetParameters().Single(p => p.Name == "preferredTarget");
        Assert.That(parameter.ParameterType, Is.EqualTo(typeof(PreferredTarget)));
        Assert.That(parameter.HasDefaultValue, Is.True);
        Assert.That(parameter.DefaultValue, Is.EqualTo(PreferredTarget.constructor));
    }

    [Test]
    [Category("RefactoringExtractionDocsTools")]
    public void SummaryCommentImpl_PreferredTargetParameter_IsEnumDefaultingToConstructor()
    {
        var parameter = typeof(RefactoringExtractionDocsImpl).GetMethod(nameof(RefactoringExtractionDocsImpl.SummaryComment))!
            .GetParameters().Single(p => p.Name == "preferredTarget");
        Assert.That(parameter.ParameterType, Is.EqualTo(typeof(PreferredTarget)));
        Assert.That(parameter.HasDefaultValue, Is.True);
        Assert.That(parameter.DefaultValue, Is.EqualTo(PreferredTarget.constructor));
    }

    [Test]
    [Category("RefactoringExtractionDocsTools")]
    public async Task SummaryComment_PreferredTargetOmitted_AddsToConstructor()
    {
        var source = "namespace N;\npublic class Foo\n{\n    public Foo(int x) { }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Foo.cs", source));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);

        var result = await tools.SummaryCommentTool(reason: "test message", workspace.PathOf("Foo.cs"), AddRemoveViewAction.add, "Foo", "Foo summary text.");

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Foo.cs");
        Assert.That(text.IndexOf("Foo summary text.", StringComparison.Ordinal), Is.GreaterThan(text.IndexOf("public class Foo", StringComparison.Ordinal)).And.LessThan(text.IndexOf("public Foo(", StringComparison.Ordinal)));
    }

    [Test]
    [Category("RefactoringExtractionDocsTools")]
    public async Task SummaryComment_PreferredTargetClass_AddsToClass()
    {
        var source = "namespace N;\npublic class Foo\n{\n    public Foo(int x) { }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Foo.cs", source));
        var tools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(workspace.Manager);

        var result = await tools.SummaryCommentTool(reason: "test message", workspace.PathOf("Foo.cs"), AddRemoveViewAction.add, "Foo", "Foo summary text.", preferredTarget: PreferredTarget.@class);

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Foo.cs");
        Assert.That(text.IndexOf("Foo summary text.", StringComparison.Ordinal), Is.LessThan(text.IndexOf("public class Foo", StringComparison.Ordinal)));
    }

    [TestCase(PreferredTarget.constructor, "\"constructor\"")]
    [TestCase(PreferredTarget.@class, "\"class\"")]
    public void PreferredTarget_SerializesToStringName(PreferredTarget value, string expectedJson)
    {
        Assert.That(JsonSerializer.Serialize(value), Is.EqualTo(expectedJson));
    }
}
