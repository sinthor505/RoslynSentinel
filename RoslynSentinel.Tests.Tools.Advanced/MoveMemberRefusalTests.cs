using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (InMemoryWorkspace) refusal tests for MoveMemberTool, split out of BatteryTwentyFourTests so they cannot
// leak files into the bin dir. Each refusal must leave every document text unchanged.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MoveMemberRefusalTests
{
    // Same wiring as BatteryTwentyFourTests [SetUp] for the tool under test: MoveMemberTool lives on
    // AdvancedRefactoringTools, which takes only the workspace manager, so no engines are needed for these refusals.
    private static AdvancedRefactoringTools BuildTools(IWorkspaceManager workspaceManager)
    {
        return new AdvancedRefactoringTools(workspaceManager);
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_FieldMapValueNotTargetField_ReturnsInvalidArgument()
    {
        var source = "public class Source\n{\n    private int _a;\n    public int Get() => _a;\n}\n\npublic class Target\n{\n    private int _b;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", source));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target", fieldMap: new Dictionary<string, string> { ["_a"] = "_nope" });
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(source), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_ToBaseClass_ReturnsTargetIneligible()
    {
        var original = "public class Base\n{\n}\n\npublic class Source : Base\n{\n    public int Get() => 1;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Base", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_ForwarderReceiver_WithRemove_ReturnsInvalidArgument()
    {
        var original = "public class Source\n{\n    private Target _impl = new Target();\n    public int Get() => 1;\n}\n\npublic class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target", forwarderReceiver: "_impl");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_PartialSourceClass_ReturnsTargetIneligible()
    {
        var original = "public partial class Source\n{\n    public int Get() => 1;\n}\n\npublic partial class Source\n{\n    public int Other() => 2;\n}\n\npublic class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericMethod_ReturnsNotImplemented()
    {
        var original = "public class Source\n{\n    public int Get<T>(T x) => 1;\n}\n\npublic class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotImplemented));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_Property_ReturnsTargetIneligible()
    {
        var original = "public class Source\n{\n    public int Value => 1;\n}\n\npublic class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Value"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_OverrideMethod_ReturnsTargetIneligible()
    {
        var original = "public class Base\n{\n    public virtual int Get() => 1;\n}\n\npublic class Source : Base\n{\n    public override int Get() => 2;\n}\n\npublic class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_AbstractMethod_ReturnsTargetIneligible()
    {
        var original = "public abstract class Source\n{\n    public abstract int Get();\n}\n\npublic class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_VirtualMethod_ReturnsTargetIneligible()
    {
        var original = "public class Source\n{\n    public virtual int Get() => 1;\n}\n\npublic class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceTwoCandidateFields_NoForwarderReceiver_ReturnsInvalidArgument()
    {
        var original = "public class Source\n{\n    private readonly Target _a = new Target();\n    private readonly Target _b = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData?.Message, Does.Contain("forwarderReceiver"), "The refusal must name the parameter to pass.");
        Assert.That(result.ErrorData?.Message, Does.Contain("_a").And.Contain("_b"), "The refusal must list both candidate fields.");
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceToStaticClass_StillNotImplemented()
    {
        var original = "public class Source\n{\n    public int Foo(int x) { return x + 1; }\n}\n\npublic static class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotImplemented));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_SomeRequestedNamesMissing_ReturnsNotFoundAndWritesNothing()
    {
        var before = "public class Source\n{\n    public int Foo() => 1;\n    public int Bar() => 2;\n}\n\npublic class Target\n{\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", before));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo", "Missing"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData?.Message, Does.Contain("Missing"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(before), "A refused move must not write anything.");
    }
}
