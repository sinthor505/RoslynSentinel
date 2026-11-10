using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// MoveMember and ChangeSignature halves of the ambiguous-name refusal tests (InMemoryWorkspace).
// The SyncInterface halves stay in Tests.Tools.Advanced (AmbiguousNameTargetToolTests).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("AmbiguousNameTarget")]
public sealed class AmbiguousNameTargetCoreToolTests
{
    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_TargetNameDeclaredTwice_ReturnsAmbiguousAndWritesNothing()
    {
        var original = "namespace A\n{\n    public class Target\n    {\n    }\n}\n\nnamespace B\n{\n    public class Target\n    {\n    }\n\n    public class Source\n    {\n        public int Get() => 1;\n    }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Target"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ChangeSignature_MethodNameIsTwoOverloads_ReturnsAmbiguousAndWritesNothing()
    {
        // Substitute for AddGuardClauses: AddGuardClauses lives in Tools.Experimental, which this factory cannot reach.
        var original = "public class Worker\n{\n    public int Run(int a) => a;\n    public int Run(string s) => s.Length;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", [new ChangeSignatureParameterInput(originalIndex: 0)]);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Run"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ChangeSignature_SingleStatementCalcInTwoClasses_ReturnsAmbiguousAndWritesNothing()
    {
        // Substitute for ConvertMethodToExpressionBody: no such tool is reachable from this factory. Same name-keyed
        // refusal shape, with the two single-statement candidates in different classes.
        var original = "public class First\n{\n    public int Calc(int a) { return a; }\n}\n\npublic class Second\n{\n    public int Calc(string s) { return s.Length; }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Calc", [new ChangeSignatureParameterInput(originalIndex: 0)]);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Calc"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ChangeSignature_OverloadsWithContextSnippet_ChangesOnlyTheSelectedOverload()
    {
        var original = "public class Worker\n{\n    public int Run(int a, int b) => a - b;\n    public int Run(string s, string t) => s.Length - t.Length;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", [new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0)], contextSnippet: "public int Run(string s, string t)");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var updated = workspace.ReadText("Refactor.cs");
        Assert.That(updated, Does.Contain("Run(string t, string s)"), "The selected overload must be reordered.");
        Assert.That(updated, Does.Contain("Run(int a, int b)"), "The other overload must be untouched.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ChangeSignature_ConstructorsWithContextSnippet_ChangesOnlyTheSelectedConstructor()
    {
        var original = "public class Box\n{\n    public Box(int a, int b) { }\n    public Box(string s, string t) { }\n    public Box(double d, double e) { }\n    public Box(long x, long y) { }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Box", [new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0)], contextSnippet: "public Box(double d, double e)");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var updated = workspace.ReadText("Refactor.cs");
        Assert.That(updated, Does.Contain("Box(double e, double d)"), "The selected constructor must be reordered.");
        Assert.That(updated, Does.Contain("Box(int a, int b)").And.Contain("Box(string s, string t)").And.Contain("Box(long x, long y)"), "The other constructors must be untouched.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ChangeSignature_OverloadsWithoutSnippet_AmbiguousHintIsAValidChangeSignatureCall()
    {
        var original = "public class Worker\n{\n    public int Run(int a, int b) => a - b;\n    public int Run(string s, string t) => s.Length - t.Length;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", [new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0)]);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("ChangeSignature(").And.Contain("contextSnippet: \"public int Run(int a, int b)"), "The retry hint must be a ChangeSignature call carrying the real parameter.");
        Assert.That(ToolCallHint.Validate("ChangeSignature", ("contextSnippet", "x")), Is.Empty, "contextSnippet must be a real ChangeSignature parameter.");
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ChangeSignature_OverloadsWithUnmatchedContextSnippet_ReturnsSnippetNotFoundAndWritesNothing()
    {
        var original = "public class Worker\n{\n    public int Run(int a, int b) => a - b;\n    public int Run(string s, string t) => s.Length - t.Length;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", [new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0)], contextSnippet: "public int Run(double d, double e)");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData?.Message, Does.Contain("contextSnippet"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }
}
