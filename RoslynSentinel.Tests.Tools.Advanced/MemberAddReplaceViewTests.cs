using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests split out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberAddReplaceViewTests
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
    // TODO ModifyAttribute add of [Serializable] on a plain class is refused with compiler errors (diagnostics not in the message; cause unconfirmed)
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddAttribute_SerializableOnOrder_IsRefusedWithCompilerErrors()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyAttributeTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", "[System.Serializable]", AttributeModifyAction.add);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("new compiler errors"));
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(CrlfSimpleSource));
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
    // TODO RemoveAttribute reports success but leaves the attribute (existingAttribute "Serializable", targetName "Order", action remove)
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveAttribute_SerializableOnOrder_ReportsSuccessButLeavesAttribute()
    {
        var source = CrlfSimpleSource.Replace("public class Order", "[Serializable]\r\npublic class Order", StringComparison.Ordinal);
        using var workspace = InMemoryWorkspace.Create(("Order.cs", source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyAttributeTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", "Serializable", AttributeModifyAction.remove);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Contain("[Serializable]"));
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
