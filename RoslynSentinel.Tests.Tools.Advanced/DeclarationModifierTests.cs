using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests split out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
// Covers declaration-level edits: member remove/replace, signature, accessibility, modifiers and enum members.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class DeclarationModifierTests
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

    private static readonly string CrlfSimpleSource = SimpleSource.ReplaceLineEndings("\r\n");

    private static RefactoringStructuralTools BuildStructuralTools(IWorkspaceManager workspaceManager)
    {
        // Mirrors the engine chain in the BatteryTwentyFourTests [SetUp]: DiffEngine before ValidationEngine.
        var config = new SentinelConfiguration();
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var refactoringEngine = new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var memberRefactoringEngine = new MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var impl = new RefactoringStructuralImpl(refactoringEngine, memberRefactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringStructuralImpl>.Instance);
        return new RefactoringStructuralTools(impl);
    }

    private static RefactoringSignatureTools BuildSignatureTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var refactoringEngine = new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var memberRefactoringEngine = new MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine);
        var impl = new RefactoringSignatureImpl(refactoringEngine, memberRefactoringEngine, workspaceManager, validationEngine, symbolNavigationEngine, NullLogger<RefactoringSignatureImpl>.Instance);
        return new RefactoringSignatureTools(impl);
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task SafeDeleteSymbol_RemovesOrderGetLabelBody()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.remove, memberName: "GetLabel");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Not.Contain("return string.Format"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_ReversesConstructorParameters()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = new AdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", new[] { new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0) });
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("Order(string customerName, int orderId)"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ModifyEnum_AddsCancelledMember()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildStructuralTools(workspace.Manager);
        var result = await tools.ModifyEnumTool(reason: "test message", workspace.PathOf("Order.cs"), "Status", "Active,Pending,Cancelled");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("Cancelled"));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ChangeAccessibility_OrderIdBecomesInternal()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildSignatureTools(workspace.Manager);
        var result = await tools.ChangeAccessibilityTool(reason: "test message", workspace.PathOf("Order.cs"), "OrderId", AccessibilityLevel.@internal);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Contain("internal int OrderId"));
        Assert.That(text, Does.Not.Contain("public int OrderId"));
    }

    // ModifyModifier with targetName "Order" resolves to the class's constructor (same name), so a class-level
    // modifier cannot be targeted by class name; a uniquely named field is used instead.
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddModifier_AddsReadonlyToCount()
    {
        var fieldSource = "namespace TestProj;\n\npublic class Widget\n{\n    public int Count;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Widget.cs", fieldSource));
        var tools = BuildStructuralTools(workspace.Manager);
        var result = await tools.ModifyModifierTool(reason: "test message", workspace.PathOf("Widget.cs"), "Count", NonAccessibilityModifier.@readonly, AddRemoveAction.add);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Widget.cs"), Does.Contain("public readonly int Count;"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveModifier_RemovesReadonlyFromCount()
    {
        var fieldSource = "namespace TestProj;\n\npublic class Widget\n{\n    public readonly int Count;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Widget.cs", fieldSource));
        var tools = BuildStructuralTools(workspace.Manager);
        var result = await tools.ModifyModifierTool(reason: "test message", workspace.PathOf("Widget.cs"), "Count", NonAccessibilityModifier.@readonly, AddRemoveAction.remove);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Widget.cs");
        Assert.That(text, Does.Contain("public int Count;"));
        Assert.That(text, Does.Not.Contain("readonly"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ReplaceMember_ValidMember_ReplacesGetLabelBody()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.replace, memberName: "GetLabel", newMemberSource: "public string GetLabel() => $\"{OrderId}: {CustomerName}\";");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Contain("public string GetLabel() => $\"{OrderId}: {CustomerName}\";"));
        Assert.That(text, Does.Not.Contain("return string.Format"));
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ReplaceMember_SingleCandidateWithMismatchedContextSnippet_SurfacesWarningDetails()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.replace, memberName: "GetStatus", newMemberSource: "public string GetStatus() => \"Active\";", contextSnippet: "THIS_SNIPPET_DOES_NOT_EXIST_IN_FILE", dryRun: true);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.WarningDetails, Does.Contain("GetStatus"));
        // dryRun must not write anything.
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(CrlfSimpleSource));

        var matching = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.replace, memberName: "GetStatus", newMemberSource: "public string GetStatus() => \"Active\";", contextSnippet: "if (OrderId == 1)", dryRun: true);
        Assert.That(matching.IsError, Is.False, matching.ErrorData?.Message);
        Assert.That(matching.WarningDetails, Is.Null);
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ReplaceMember_InterfaceMethod_ReplacesGreetSignature()
    {
        var greeterSource = @"
namespace TestProj;

public interface IGreeter
{
    string Greet();
}
".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("IGreeter.cs", greeterSource));
        var tools = BuildStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("IGreeter.cs"), MemberAction.replace, memberName: "Greet", newMemberSource: "string Greet(string name);");
        // Member(replace) must be able to target a method declared directly on an interface.
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("IGreeter.cs"), Does.Contain("string Greet(string name);"));
    }
}
