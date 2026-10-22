using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests split out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberAddReplaceViewTests
{
    private static readonly string SimpleSource = Sources.Order;

    // The original fixture was CRLF (verbatim literals inside a CRLF file); the EOL guard compares against the document's endings.
    private static readonly string CrlfSimpleSource = SimpleSource.ReplaceLineEndings("\r\n");

    // --- MemberTool on enum containers (routes to AddEnumMemberAsync/RemoveEnumMemberAsync/ReplaceEnumMemberAsync) ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_Add_OnEnumContainer_AddsEnumMemberToEnum()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.addMember, "Status", newMemberSource: "Cancelled");
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("Cancelled"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_Remove_OnEnumMember_RemovesPendingFromEnum()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.remove, memberName: "Pending", skipPrecheck: true);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Not.Contain("Pending = 2"));
        Assert.That(text, Does.Contain("Active = 1"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_Replace_OnEnumMember_ReplacesPendingWithInProgress()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.replace, memberName: "Pending", newMemberSource: "InProgress=2");
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Not.Contain("Pending = 2"));
        Assert.That(text, Does.Contain("InProgress"));
    }

    [Test]
    [Category("MemberSourceInfo")] // sentinel:auto-category
    [Category("MemberSourceViewResult")] // sentinel:auto-category
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_View_WithMemberName_ReturnsThatMembersSource()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.view, memberName: "GetStatus");
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var view = result.SuccessData as MemberSourceViewResult;
        Assert.That(view, Is.Not.Null);
        Assert.That(view!.Member.Name, Is.EqualTo("GetStatus"));
        Assert.That(view.Member.Kind, Is.EqualTo("method"));
        Assert.That(view.Member.Source, Does.Contain("public string GetStatus()"));
        Assert.That(view.Member.Source, Does.Contain("return \"Unknown\";"));
        Assert.That(view.Member.IsComplete, Is.True);
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_View_WithMissingMemberName_ReturnsNotFound()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.view, memberName: "NoSuchMember");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_View_OnEnumContainer_ReturnsEnumMembers()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        // A container-only view (no memberName) returns MemberViewResult with the container's direct members, not MemberSourceViewResult.
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.view, "Status");
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var view = result.SuccessData as MemberViewResult;
        Assert.That(view, Is.Not.Null);
        Assert.That(view!.Members.Select(m => m.Name), Is.EqualTo(new[] { "Active", "Pending" }));
        Assert.That(view.Members.Select(m => m.Kind).Distinct(), Is.EqualTo(new[] { "enumMember" }));
    }

    // --- InsertMemberAfter ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task InsertMemberAfter_GetLabel_PlacesNewMemberAfterGetLabel()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.addMember, "Order", newMemberSource: "public string Description => \"\";", position: "after:GetLabel");
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Contain("public string Description => \"\";"));
        Assert.That(text.IndexOf("public string Description", StringComparison.Ordinal), Is.GreaterThan(text.IndexOf("public string GetLabel()", StringComparison.Ordinal)));
    }

    // --- InsertMemberBefore ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task InsertMemberBefore_GetLabel_PlacesNewMemberBeforeGetLabel()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.addMember, "Order", newMemberSource: "public string Tag => \"\";", position: "before:GetLabel");
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Contain("public string Tag => \"\";"));
        Assert.That(text.IndexOf("public string Tag", StringComparison.Ordinal), Is.LessThan(text.IndexOf("public string GetLabel()", StringComparison.Ordinal)));
    }

    // --- AddAttribute ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddAttribute_SerializableOnOrder_AddsAttributeToClass()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyAttributeTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", "[System.Serializable]", AttributeModifyAction.add);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(workspace.ReadText("Order.cs"), Does.Match(@"\[System\.Serializable\]\s*\r?\npublic class Order"));
    }

    // --- AddBaseType ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddBaseType_OrderClass_AddsIServiceBaseType()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyBaseTypeTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", "IService", AddRemoveAction.add);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        // The tool may place the colon on the line after the class name, so the separator is matched loosely.
        Assert.That(workspace.ReadText("Order.cs"), Does.Match(@"class Order\s*:\s*IService"));
    }

    // --- RemoveAttribute ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveAttribute_SerializableOnOrder_RemovesAttribute()
    {
        var source = CrlfSimpleSource.Replace("public class Order", "[Serializable]\r\npublic class Order", StringComparison.Ordinal);
        using var workspace = InMemoryWorkspace.Create(("Order.cs", source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyAttributeTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", "Serializable", AttributeModifyAction.remove);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Not.Contain("[Serializable]"));
        Assert.That(text, Does.Contain("public class Order"));
    }

    // --- RemoveBaseType ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveBaseType_OrderWithIServiceBase_RemovesIServiceBaseType()
    {
        var source = CrlfSimpleSource.Replace("public class Order", "public class Order : IService", StringComparison.Ordinal);
        using var workspace = InMemoryWorkspace.Create(("Order.cs", source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyBaseTypeTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", "IService", AddRemoveAction.remove);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Contain("public class Order"));
        Assert.That(text, Does.Not.Match(@"class Order\s*:\s*IService"));
    }
}
