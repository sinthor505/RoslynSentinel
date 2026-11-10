using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (InMemoryWorkspace) refusal tests proving that name-keyed tools refuse an ambiguous name with a
// structured Ambiguous error and leave the file untouched. Each source holds exactly two candidates for the name.
// Substitutions for tools not reachable from this project's BatteryToolFactory are noted on the test.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("AmbiguousNameTarget")]
public sealed class AmbiguousNameTargetToolTests
{

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task InlineSymbol_MethodNameDeclaredTwice_ReturnsErrorNamingTargetAndWritesNothing()
    {
        // InlineMethod reports >1 declaration as ToolTargetIneligible rather than Ambiguous; that is accepted here,
        // so this test asserts only that the call is refused, names 'Calc', and leaves the file untouched.
        var original = "public class First\n{\n    public int Calc() => 1;\n}\n\npublic class Second\n{\n    public int Calc() => 2;\n    public int Use() => Calc();\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.InlineSymbolTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Calc", InlineKind.method);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("Calc"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task InlineSymbol_FieldNameDeclaredTwice_ReturnsAmbiguousAndWritesNothing()
    {
        var original = "public class First\n{\n    private int _value = 1;\n    public int Get() => _value;\n}\n\npublic class Second\n{\n    private int _value = 2;\n    public int Get() => _value;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.InlineSymbolTool(reason: "test message", workspace.PathOf("Refactor.cs"), "_value", InlineKind.field);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("_value"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task SyncInterface_InterfaceNameDeclaredTwice_ReturnsAmbiguousAndWritesNothing()
    {
        // Substitute for ExtractInterface: no ExtractInterface tool is reachable from this project's factory.
        var original = "namespace A\n{\n    public interface IService\n    {\n        int Run();\n    }\n}\n\nnamespace B\n{\n    public interface IService\n    {\n        int Run();\n    }\n}\n\npublic class Impl\n{\n    public int Run() => 1;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.SyncInterfaceTool(reason: "test message", workspace.PathOf("Refactor.cs"), "IService", SyncInterfaceAction.sync, className: "Impl");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("IService"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveType_TypeNameDeclaredTwice_ReturnsAmbiguousAndWritesNothing()
    {
        // Substitute for ClassToRecord: ClassToRecord lives in Tools.Experimental, which this factory cannot reach.
        var original = "namespace A\n{\n    public class Dto\n    {\n    }\n}\n\nnamespace B\n{\n    public class Dto\n    {\n    }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveTypeTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Dto", "ownFile");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Dto"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task SyncInterface_ImplementClassNameDeclaredTwice_ReturnsAmbiguousAndWritesNothing()
    {
        var original = "public interface IHandler\n{\n    void Handle();\n}\n\nnamespace A\n{\n    public class Worker : IHandler\n    {\n    }\n}\n\nnamespace B\n{\n    public class Worker : IHandler\n    {\n    }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.SyncInterfaceTool(reason: "test message", workspace.PathOf("Refactor.cs"), "IHandler", SyncInterfaceAction.implement, className: "Worker");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Worker"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("IntelligenceTools")]
    [TestCase("forward")]
    [TestCase("reverse")]
    [TestCase("tree")]
    public async Task GetCallGraph_OverloadsWithoutSnippet_AmbiguousHintIsAValidGetCallGraphCall(string direction)
    {
        var original = "public class Worker\n{\n    public int Run(int a) => Add(a);\n    public int Run(string s) => Add(s.Length);\n    private int Add(int x) => x + 1;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateIntelligenceTools(workspace.Manager);
        var result = await tools.GetCallGraphTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", direction);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("GetCallGraph(").And.Contain("contextSnippet: \"public int Run(int a)"), "The retry hint must be a GetCallGraph call carrying the real parameter.");
        Assert.That(ToolCallHint.Validate("GetCallGraph", ("contextSnippet", "x"), ("direction", direction)), Is.Empty, "contextSnippet must be a real GetCallGraph parameter.");
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("IntelligenceTools")]
    public async Task GetCallGraph_OverloadsWithContextSnippet_ReturnsTheGraphOfThatOverloadOnly()
    {
        var original = "public class Worker\n{\n    public int Run(int a) => AddOne(a);\n    public int Run(string s) => Measure(s);\n    private int AddOne(int x) => x + 1;\n    private int Measure(string s) => s.Length;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateIntelligenceTools(workspace.Manager);
        var result = await tools.GetCallGraphTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", contextSnippet: "public int Run(string s)");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var node = result.SuccessData as CallGraphNode;
        Assert.That(node, Is.Not.Null);
        var callees = node!.Callees.Select(c => c.MethodName).ToList();
        Assert.That(callees, Is.EquivalentTo(new[] { "Measure" }), "Only the selected overload's callees belong in the graph.");
    }

    [Test]
    [Category("IntelligenceTools")]
    public async Task GetCallGraph_TreeOverloadsWithContextSnippet_ReturnsTheTreeOfThatOverloadOnly()
    {
        var original = "public class Worker\n{\n    public int Run(int a) => AddOne(a);\n    public int Run(string s) => Measure(s);\n    private int AddOne(int x) => x + 1;\n    private int Measure(string s) => s.Length;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateIntelligenceTools(workspace.Manager);
        var result = await tools.GetCallGraphTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", "tree", contextSnippet: "public int Run(string s)");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var tree = (result.SuccessData as DocumentEditResult)?.UpdatedText ?? string.Empty;
        Assert.That(tree, Does.Contain("Measure"), "The selected overload's callee belongs in the tree.");
        Assert.That(tree, Does.Not.Contain("AddOne"), "The other overload's callee must not appear.");
    }

    [Test]
    [Category("IntelligenceTools")]
    public async Task GetCallGraph_TreeOverloadsWithUnmatchedContextSnippet_ReturnsSnippetNotFound()
    {
        var original = "public class Worker\n{\n    public int Run(int a) => AddOne(a);\n    public int Run(string s) => Measure(s);\n    private int AddOne(int x) => x + 1;\n    private int Measure(string s) => s.Length;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateIntelligenceTools(workspace.Manager);
        var result = await tools.GetCallGraphTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", "tree", contextSnippet: "public int Run(double d)");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData?.Message, Does.Contain("contextSnippet"));
    }

    [Test]
    [Category("IntelligenceTools")]
    public async Task GetCallGraph_TreeSingleMethodWithoutSnippet_StillReturnsItsTree()
    {
        var original = "public class Worker\n{\n    public int Run(int a) => AddOne(a);\n    private int AddOne(int x) => x + 1;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateIntelligenceTools(workspace.Manager);
        var result = await tools.GetCallGraphTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", "tree");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That((result.SuccessData as DocumentEditResult)?.UpdatedText, Does.Contain("AddOne"));
    }

    [Test]
    [Category("IntelligenceTools")]
    public async Task GetCallGraph_SingleMethodWithoutSnippet_StillReturnsItsGraph()
    {
        var original = "public class Worker\n{\n    public int Run(int a) => AddOne(a);\n    private int AddOne(int x) => x + 1;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateIntelligenceTools(workspace.Manager);
        var result = await tools.GetCallGraphTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That((result.SuccessData as CallGraphNode)?.Callees.Select(c => c.MethodName), Is.EquivalentTo(new[] { "AddOne" }));
    }
}
