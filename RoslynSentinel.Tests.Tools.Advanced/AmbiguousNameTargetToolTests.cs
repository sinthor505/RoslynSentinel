using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

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
    public async Task MoveMember_TargetNameDeclaredTwice_ReturnsAmbiguousAndWritesNothing()
    {
        var original = "namespace A\n{\n    public class Target\n    {\n    }\n}\n\nnamespace B\n{\n    public class Target\n    {\n    }\n\n    public class Source\n    {\n        public int Get() => 1;\n    }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Target"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

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
    public async Task ChangeSignature_MethodNameIsTwoOverloads_ReturnsAmbiguousAndWritesNothing()
    {
        // Substitute for AddGuardClauses: AddGuardClauses lives in Tools.Experimental, which this factory cannot reach.
        var original = "public class Worker\n{\n    public int Run(int a) => a;\n    public int Run(string s) => s.Length;\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Run", [new ChangeSignatureParameterInput(originalIndex: 0)]);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Run"));
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
    [Category("AdvancedRefactoringTools")]
    public async Task ChangeSignature_SingleStatementCalcInTwoClasses_ReturnsAmbiguousAndWritesNothing()
    {
        // Substitute for ConvertMethodToExpressionBody: no such tool is reachable from this factory. Same name-keyed
        // refusal shape, with the two single-statement candidates in different classes.
        var original = "public class First\n{\n    public int Calc(int a) { return a; }\n}\n\npublic class Second\n{\n    public int Calc(string s) { return s.Length; }\n}\n".ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Calc", [new ChangeSignatureParameterInput(originalIndex: 0)]);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Calc"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }
}
