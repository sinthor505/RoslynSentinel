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
public sealed class IntroduceInlineTests
{
    private static readonly string SimpleSource = Sources.Order;

    private static readonly string RefactorSource = Sources.AnimalHierarchy;

    // A class with a real const field, used in a method body.
    private const string ConstFieldSource = @"
namespace TestProj;

public class Counter
{
    private const int Step = 2;

    public int Next(int value)
    {
        return value + Step;
    }
}
";

    // A method with a parameter used in its body, plus a call site that supplies the argument.
    private const string ParameterSource = @"
namespace TestProj;

public class Calc
{
    public int Add(int amount)
    {
        return amount + 1;
    }

    public int Run()
    {
        return Add(2);
    }
}
";

    // A local variable assigned once and used.
    private const string LocalVariableSource = @"
namespace TestProj;

public class Calc
{
    public int Run()
    {
        var total = 2 + 3;
        return total;
    }
}
";

    // Sources are written CRLF, matching the on-disk fixtures the original tests ran against.
    private static readonly string CrlfSimpleSource = SimpleSource.ReplaceLineEndings("\r\n");
    private static readonly string CrlfRefactorSource = RefactorSource.ReplaceLineEndings("\r\n");
    private static readonly string CrlfConstFieldSource = ConstFieldSource.ReplaceLineEndings("\r\n");
    private static readonly string CrlfParameterSource = ParameterSource.ReplaceLineEndings("\r\n");
    private static readonly string CrlfLocalVariableSource = LocalVariableSource.ReplaceLineEndings("\r\n");

    // --- InlineSymbol (method) ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineMethod_ValidMethod_ChangesSource()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.InlineSymbolTool(reason: "test message", workspace.PathOf("Order.cs"), "GetLabel", InlineKind.method);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Is.Not.EqualTo(CrlfSimpleSource));
    }

    // --- IntroduceField ---
    // contextSnippet is the literal "Unknown" (a constant, so the field initialiser needs no instance members).
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceField_ValidContext_AddsNamedField()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.IntroduceSymbolTool(reason: "test message", workspace.PathOf("Order.cs"), "\"Unknown\"", "unknownStatus", IntroduceAsType.field);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("unknownStatus"));
    }

    // --- IntroduceParameter ---
    // contextSnippet is the literal "Active" inside GetStatus; the new parameter is named activeLabel.
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceParameter_ValidContext_AddsNamedParameter()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.IntroduceSymbolTool(reason: "test message", workspace.PathOf("Order.cs"), "\"Active\"", "activeLabel", IntroduceAsType.parameter);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("activeLabel"));
    }

    // --- InlineField ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineField_ValidField_InlinesConstantValue()
    {
        using var workspace = InMemoryWorkspace.Create(("Counter.cs", CrlfConstFieldSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.InlineSymbolTool(reason: "test message", workspace.PathOf("Counter.cs"), "Step", InlineKind.field);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Counter.cs");
        Assert.That(text, Does.Contain("value + 2"));
        Assert.That(text, Does.Not.Contain("+ Step"));
    }

    // --- InlineParameter ---
    // TODO InlineParameter (AdvancedRefactoringTools.InlineSymbolTool kind=parameter) reports success but leaves the file unchanged
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineParameter_ValidParameter_ReportsSuccessButLeavesFileUnchanged()
    {
        using var workspace = InMemoryWorkspace.Create(("Calc.cs", CrlfParameterSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.InlineSymbolTool(reason: "test message", workspace.PathOf("Calc.cs"), "amount", InlineKind.parameter, "Add");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Calc.cs"), Is.EqualTo(CrlfParameterSource));
    }

    // --- InlineClass ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineClass_CrossFile_MovesMembers()
    {
        using var workspace = InMemoryWorkspace.Create(
            ("Helper.cs", "namespace App; public class Helper { public int Value; public void Go() {} }".ReplaceLineEndings("\r\n")),
            ("Owner.cs", "namespace App; public class Owner {}".ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.InlineClassTool(reason: "test message", workspace.PathOf("Helper.cs"), workspace.PathOf("Owner.cs"), "Helper", dryRun: false, returnDiff: false);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var owner = workspace.ReadText("Owner.cs");
        Assert.That(owner, Does.Contain("Value"));
        Assert.That(owner, Does.Contain("Go"));
    }

    // --- IntroduceVariable ---
    // contextSnippet is the string.Format call in GetLabel; it is a string-valued expression, so the local is well typed.
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceVariable_ValidContext_IntroducesLocalVariable()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.IntroduceSymbolTool(reason: "test message", workspace.PathOf("Order.cs"), "string.Format(\"{0}: {1}\", OrderId, CustomerName)", "formatted", IntroduceAsType.localVariable);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("formatted"));
    }

    // --- InlineVariable ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineVariable_ValidVariable_InlinesInitializer()
    {
        using var workspace = InMemoryWorkspace.Create(("Calc.cs", CrlfLocalVariableSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.InlineSymbolTool(reason: "test message", workspace.PathOf("Calc.cs"), "total", InlineKind.variable);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Calc.cs");
        Assert.That(text, Does.Contain("return (2 + 3);"));
        Assert.That(text, Does.Not.Contain("var total"));
    }

    // --- IntroduceParameterObject ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceParameterObject_ValidMethod_CreatesParameterObject()
    {
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", CrlfRefactorSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.IntroduceParameterObjectTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Process");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Refactor.cs"), Is.Not.EqualTo(CrlfRefactorSource));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceParameterObject_InterfaceOnly_SummaryWarnsImplementationsNotUpdated()
    {
        const string interfaceOnlySource = @"
public interface IProcessor
{
    void Process(string name, int age, bool active);
}";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", interfaceOnlySource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.IntroduceParameterObjectTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Process");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var summary = (RoslynSentinel.Common.AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Contain("WARNING:"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceParameterObject_NonExistentFile_IsRefused()
    {
        const string source = "public class C {}";
        using var workspace = InMemoryWorkspace.Create(("Test.cs", source));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.IntroduceParameterObjectTool(reason: "test message", workspace.PathOf("NonExistent.cs"), "Process");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Is.Not.Null.And.Not.Empty);
        Assert.That(workspace.ReadText("Test.cs"), Is.EqualTo(source));
    }
}
