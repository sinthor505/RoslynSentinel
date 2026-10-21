using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests for the ConstructorParameter and MethodSignature tools; split out of
// BatteryTwentyFourTests so they cannot leak files into the bin dir. Call sites are a second in-memory document.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MethodSignatureCallSiteTests
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

    private const string RenameSource = "namespace TestProj;\npublic class Order\n{\n    public string CustomerName { get; set; }\n    public void Rename(string first, string last) { CustomerName = first; }\n    public string GetLabel() => CustomerName;\n    public string GetStatus() => CustomerName;\n}\n";

    private const string OrderCallerSource = "namespace TestProj;\npublic class Caller { public Order Make() => new Order(1, \"a\"); }";

    private const string StatusCallerSource = "namespace TestProj;\npublic class Caller { public string Use(Order o) => o.GetStatus(); }";

    private const string RenameCallerSource = "namespace TestProj;\npublic class Caller { public void Use(Order o) => o.Rename(\"a\", \"b\"); }";

    private const string NamedRenameCallerSource = "namespace TestProj;\npublic class Caller { public void Use(Order o) => o.Rename(first: \"a\", last: \"b\"); }";

    private const string ProcessSource = "namespace RefactorNs;\npublic class Dog\n{\n    public void Process(int a, int b, int c, int d, int e) { }\n}\n";

    private static readonly string CrlfSimpleSource = Crlf(SimpleSource);

    private static string Crlf(string source) => source.ReplaceLineEndings("\r\n");

    // --- ConstructorParameterTool ---
    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_Add_NoExistingCallers_AddsParameterToConstructor()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.ConstructorParameterTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.add, "Order", "notes", "string");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("string notes"));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_Add_WithDefaultValue_BackwardCompatibleWithExistingCaller()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource), ("Caller.cs", Crlf(OrderCallerSource)));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.ConstructorParameterTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.add, "Order", "notes", "string", defaultValue: "\"\"");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("string notes = \"\""));
        Assert.That(workspace.ReadText("Caller.cs"), Does.Contain("new Order(1, \"a\")"));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_Add_WithNullDefault_BackwardCompatibleWithExistingCaller()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource), ("Caller.cs", Crlf(OrderCallerSource)));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.ConstructorParameterTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.add, "Order", "notes", "string", nullDefault: true);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("notes = null"));
        Assert.That(workspace.ReadText("Caller.cs"), Does.Contain("new Order(1, \"a\")"));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_Add_NullDefaultAndDefaultValueBothSet_Refused()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.ConstructorParameterTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.add, "Order", "notes", "string", defaultValue: "\"\"", nullDefault: true);
        Assert.That(result.IsError, Is.True, "Setting both nullDefault and defaultValue must be refused.");
        Assert.That(result.ErrorData?.Message, Does.Contain("mutually exclusive"));
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(CrlfSimpleSource));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_Add_NoDefaultValue_ExistingCallerBreaksAndIsRefused()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource), ("Caller.cs", Crlf(OrderCallerSource)));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.ConstructorParameterTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.add, "Order", "notes", "string");
        Assert.That(result.IsError, Is.True, "An existing caller would break without a default, so the add must be refused.");
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(CrlfSimpleSource));
        Assert.That(workspace.ReadText("Caller.cs"), Is.EqualTo(Crlf(OrderCallerSource)));
    }

    // --- MethodSignatureTool ---
    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_View_ListsExistingParameters()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.MethodSignatureTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.view, "GetLabel");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(CrlfSimpleSource), "A view must not modify the file.");
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Add_AppendsRequiredParameter_NoExistingCallers()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.MethodSignatureTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.add, "GetStatus", "verbose", "bool");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("GetStatus(bool verbose)"));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Add_WithDefaultValue_BackwardCompatibleWithExistingCaller()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource), ("Caller.cs", Crlf(StatusCallerSource)));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.MethodSignatureTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.add, "GetStatus", "verbose", "bool", defaultValue: "false");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("GetStatus(bool verbose = false)"));
        Assert.That(workspace.ReadText("Caller.cs"), Does.Contain("o.GetStatus()"));
    }

    // Caller.cs is written with CRLF to match the fixture's line endings; the rewritten call site must keep them.
    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Remove_LastParameter_UpdatesCallSite()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", Crlf(RenameSource)), ("Caller.cs", Crlf(RenameCallerSource)));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.MethodSignatureTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.remove, "Rename", "last");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("Rename(string first)").And.Not.Contain("string last"));
        Assert.That(workspace.ReadText("Caller.cs"), Does.Contain("o.Rename(\"a\")"));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Remove_NonLastParameter_Refused()
    {
        using var workspace = InMemoryWorkspace.Create(("Dog.cs", Crlf(ProcessSource)));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.MethodSignatureTool(reason: "test message", workspace.PathOf("Dog.cs"), AddRemoveViewAction.remove, "Process", "a");
        Assert.That(result.IsError, Is.True, "Removing a non-trailing parameter must be refused, not silently applied.");
        Assert.That(result.ErrorData?.Message, Does.Contain("last parameter"));
        Assert.That(workspace.ReadText("Dog.cs"), Is.EqualTo(Crlf(ProcessSource)));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Remove_NamedArgumentCallSite_Refused()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", Crlf(RenameSource)), ("Caller.cs", Crlf(NamedRenameCallerSource)));
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(workspace.Manager);
        var result = await tools.MethodSignatureTool(reason: "test message", workspace.PathOf("Order.cs"), AddRemoveViewAction.remove, "Rename", "last");
        Assert.That(result.IsError, Is.True, "A named-argument call site cannot be safely rewritten and must refuse the whole operation.");
        Assert.That(result.ErrorData?.Message, Does.Contain("named arguments"));
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(Crlf(RenameSource)));
        Assert.That(workspace.ReadText("Caller.cs"), Is.EqualTo(Crlf(NamedRenameCallerSource)));
    }
}
