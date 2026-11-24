using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Member(remove) on a local function: the engine's semantic usage check decides, not the name-based precheck.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberLocalFunctionRemoveTests
{
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_UnusedLocalFunction_RemovesItAndReportsVerified()
    {
        const string src = "public class Order\n{\n    public void Run()\n    {\n        int Helper() => 1;\n    }\n\n    public int Other() => 2;\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Order.cs", src));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.remove, memberName: "Helper");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var orderText = workspace.ReadText("Order.cs");
        Assert.That(orderText, Does.Not.Contain("Helper"));
        Assert.That(orderText, Does.Contain("public int Other()"));
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Contain("Verified"), "A removed local function must be confirmed by the repeat lookup.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_UsedLocalFunction_RefusedWithUsageAndFileUnchanged()
    {
        const string src = "public class Order\n{\n    public int Run()\n    {\n        int Helper() => 1;\n        return Helper();\n    }\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Order.cs", src));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.remove, memberName: "Helper");
        Assert.That(result.IsError, Is.True, "A local function with a call inside its enclosing method must not be removed.");
        Assert.That(result.ErrorData?.Message, Does.Contain("usages"), "The refusal must name the usage.");
        Assert.That(result.ErrorData?.Message, Does.Contain("inside Order.Run").And.Contain("at line 6 of this file"), "The refusal lists the usage lines.");
        Assert.That(result.ErrorData?.Message, Does.Not.Contain("Search("), "Search cannot resolve a local function, so the refusal must not point at it.");
        var orderText = workspace.ReadText("Order.cs");
        Assert.That(orderText, Does.Contain("int Helper() => 1;"));
        Assert.That(orderText, Does.Contain("return Helper();"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_UnusedLocalFunction_IgnoresSameNamedMethodWithCallerElsewhere()
    {
        // Other.Helper has a caller (Other.Use), so the name-based precheck would refuse a plain Helper target.
        // containerName = "Run" (the enclosing method) selects the local function in Order and filters out Other.Helper.
        const string src = "public class Other\n{\n    public int Helper() => 1;\n    public int Use() => Helper();\n}\n\npublic class Order\n{\n    public void Run()\n    {\n        int Helper() => 2;\n    }\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Order.cs", src));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.remove, memberName: "Helper", containerName: "Run");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var orderText = workspace.ReadText("Order.cs");
        Assert.That(orderText, Does.Not.Contain("int Helper() => 2;"));
        Assert.That(orderText, Does.Contain("public int Helper() => 1;"));
        Assert.That(orderText, Does.Contain("public int Use() => Helper();"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_MethodAndUnusedLocalFunctionSameName_RemovesMethodAndHintsRetry()
    {
        // The method wins over the local function (no containerName), so the local function survives and the
        // success message points at it with a containerName retry.
        const string src = "public class Order\n{\n    public int Helper() => 1;\n\n    public void Run()\n    {\n        int Helper() => 2;\n    }\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Order.cs", src));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.remove, memberName: "Helper");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var orderText = workspace.ReadText("Order.cs");
        Assert.That(orderText, Does.Not.Contain("public int Helper() => 1;"));
        Assert.That(orderText, Does.Contain("int Helper() => 2;"));
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Contain("retry with containerName"), "The success message must name the shadowed local function and the containerName retry.");
    }
}
