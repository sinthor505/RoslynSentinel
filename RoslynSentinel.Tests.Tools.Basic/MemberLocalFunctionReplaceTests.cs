using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using static RoslynSentinel.Tests.Support.Sources;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) Member(replace) tests for a local function target (plan_member_local_function_targets, Step 4).
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberLocalFunctionReplaceTests
{
    private static readonly string Source = Crlf("""
        namespace Shop;

        public class Order
        {
            public int Total()
            {
                int Helper()
                {
                    return 1;
                }
                return Helper();
            }
        }
        """);

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ReplaceLocalFunction_BodyChange_AppliedAndReindented()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", Source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.replace, memberName: "Helper", newMemberSource: "int Helper()\n{\n    return 2;\n}");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Contain("            return 2;"));
        Assert.That(text, Does.Not.Contain("return 1;"));
        Assert.That(text, Does.Contain("\r\n        int Helper()\r\n        {\r\n            return 2;\r\n        }\r\n"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ReplaceLocalFunction_RenameBreaksCall_RejectedByCompileGate()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", Source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.replace, memberName: "Helper", newMemberSource: "int Renamed()\n{\n    return 2;\n}");
        Assert.That(result.IsError, Is.True);
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(Source));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ReplaceLocalFunction_AccessModifier_RejectedWithLocalFunctionMessage()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", Source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.replace, memberName: "Helper", newMemberSource: "public int Helper() => 2;");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("local function"));
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(Source));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ReplaceLocalFunction_TwoLocalFunctions_RejectedWithLocalFunctionMessage()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", Source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.replace, memberName: "Helper", newMemberSource: "int Helper() => 2;\nint Other() => 3;");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("local function"));
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(Source));
    }
}
