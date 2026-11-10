using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ChangeSignatureDescriptionAndRefusalTests
{
    private static readonly string CrlfSimpleSource = Sources.Order.ReplaceLineEndings("\r\n");

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_ReversesConstructorParameters()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", new[] { new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0) });
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("Order(string customerName, int orderId)"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_DryRun_ReportsWrapperAndLeavesFileUnchanged()
    {
        using var workspace = InMemoryWorkspace.Create(("Calc.cs", "public class Calc\r\n{\r\n    public int Add(int a, string b) => a + b.Length;\r\n}"));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var before = workspace.ReadText("Calc.cs");
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Calc.cs"), "Add", new[] { new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0) }, dryRun: true, oldSignature: ChangeSignatureOldSignature.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(result.SuccessData?.Description, Does.Contain("wrapper"));
        Assert.That(workspace.ReadText("Calc.cs"), Is.EqualTo(before));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_AutoOldSignature_MethodGroupCaller_DryRunAndApplyAgree()
    {
        const string saverSource = "public class Saver { public int Save(int id, string name) => id; }";
        const string callerSource = "public class Caller\r\n{\r\n    public void Run(Saver s) { System.Func<int, string, int> f = s.Save; }\r\n}";
        var parameters = new[] { new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0) };

        using var dryWorkspace = InMemoryWorkspace.Create(("Saver.cs", saverSource), ("Caller.cs", callerSource));
        var dryTools = Battery.Basic.TestTools.CoreRefactoringTools(dryWorkspace.Manager);
        var dryResult = await dryTools.ChangeSignatureTool(reason: "test message", dryWorkspace.PathOf("Saver.cs"), "Save", parameters, dryRun: true);

        Assert.That(dryResult.IsError, Is.False, $"error: {dryResult.ErrorData?.Message}");
        Assert.That(dryResult.SuccessData?.Description, Does.Contain("as an [Obsolete] wrapper"));
        Assert.That(string.Join("\n", dryResult.Findings.Select(f => f.Message)), Does.Contain("Left on deprecated wrapper").And.Contain("Caller.cs:3"));
        Assert.That(dryWorkspace.ReadText("Saver.cs"), Is.EqualTo(saverSource));
        Assert.That(dryWorkspace.ReadText("Caller.cs"), Is.EqualTo(callerSource));

        using var applyWorkspace = InMemoryWorkspace.Create(("Saver.cs", saverSource), ("Caller.cs", callerSource));
        var applyTools = Battery.Basic.TestTools.CoreRefactoringTools(applyWorkspace.Manager);
        var applyResult = await applyTools.ChangeSignatureTool(reason: "test message", applyWorkspace.PathOf("Saver.cs"), "Save", parameters, dryRun: false);

        Assert.That(applyResult.IsError, Is.False, $"error: {applyResult.ErrorData?.Message}");
        Assert.That(applyResult.SuccessData?.Description, Is.EqualTo(dryResult.SuccessData?.Description));
        Assert.That(string.Join("\n", applyResult.Findings.Select(f => f.Message)), Does.Contain("Left on deprecated wrapper").And.Contain("Caller.cs:3"));
        Assert.That(applyWorkspace.ReadText("Saver.cs"), Is.Not.EqualTo(saverSource));
    }

    // The ChangeSignature summary says what the change did (adds / removes / reorders), not always "Reorders".
    private static async Task<string?> ChangeSignatureDescriptionAsync(string fileName, string source, string methodName, params ChangeSignatureParameterInput[] parameters)
    {
        using var workspace = InMemoryWorkspace.Create((fileName, source));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf(fileName), methodName, parameters);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        return result.SuccessData?.Description;
    }

    private const string TwoParameterCalc = "public class Calc\r\n{\r\n    public int Add(int a, int b) => a;\r\n}";

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_Description_AddedParameter_SaysAdds()
    {
        var description = await ChangeSignatureDescriptionAsync("Calc.cs", TwoParameterCalc, "Add", new ChangeSignatureParameterInput(originalIndex: 0), new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(name: "c", type: "int", defaultValue: "0"));
        Assert.That(description, Does.StartWith("Adds parameters of 'Add' in Calc.cs."));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_Description_RemovedTrailingParameter_SaysRemoves()
    {
        var description = await ChangeSignatureDescriptionAsync("Calc.cs", TwoParameterCalc, "Add", new ChangeSignatureParameterInput(originalIndex: 0));
        Assert.That(description, Does.StartWith("Removes parameters of 'Add' in Calc.cs."));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_Description_ReorderedParameters_SaysReorders()
    {
        var description = await ChangeSignatureDescriptionAsync("Calc.cs", TwoParameterCalc, "Add", new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0));
        Assert.That(description, Does.StartWith("Reorders parameters of 'Add' in Calc.cs."));
        Assert.That(description, Does.Not.Contain("Adds").And.Not.Contain("Removes"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_Description_AddAndReorder_NamesBoth()
    {
        var description = await ChangeSignatureDescriptionAsync("Calc.cs", TwoParameterCalc, "Add", new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(name: "c", type: "int", defaultValue: "0"), new ChangeSignatureParameterInput(originalIndex: 0));
        Assert.That(description, Does.StartWith("Adds and reorders parameters of 'Add' in Calc.cs."));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_Description_AddAndRemove_NamesBoth()
    {
        var description = await ChangeSignatureDescriptionAsync("Calc.cs", TwoParameterCalc, "Add", new ChangeSignatureParameterInput(originalIndex: 0), new ChangeSignatureParameterInput(name: "c", type: "int", defaultValue: "0"));
        Assert.That(description, Does.StartWith("Adds and removes parameters of 'Add' in Calc.cs."));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_Description_Constructor_NamesTheConstructor()
    {
        var description = await ChangeSignatureDescriptionAsync("Order.cs", CrlfSimpleSource, "Order", new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0));
        Assert.That(description, Does.StartWith("Reorders parameters of the 'Order' constructor in Order.cs."));
    }

    // Deliberate refusals throw TargetIneligible from the engine before anything is planned, so dry run and apply refuse alike and nothing is written.
    [TestCase(true)]
    [TestCase(false)]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_RemovedParameterStillUsed_IsTargetIneligibleAndWritesNothing(bool dryRun)
    {
        const string source = "public class Calc\r\n{\r\n    public int Add(int a, int b) => a + b;\r\n}";
        using var workspace = InMemoryWorkspace.Create(("Calc.cs", source));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Calc.cs"), "Add", new[] { new ChangeSignatureParameterInput(originalIndex: 0) }, dryRun: dryRun);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData?.Message, Does.Contain("Cannot remove parameter 'b' of 'Add'"));
        Assert.That(workspace.ReadText("Calc.cs"), Is.EqualTo(source));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_EmptyParameterList_IsTargetIneligibleAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create(("Calc.cs", TwoParameterCalc));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Calc.cs"), "Add", Array.Empty<ChangeSignatureParameterInput>());
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData?.Message, Does.Contain("Edit the declaration and its call sites directly."));
        Assert.That(workspace.ReadText("Calc.cs"), Is.EqualTo(TwoParameterCalc));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_ParameterlessMethod_IsTargetIneligibleAndWritesNothing()
    {
        const string source = "public class Calc\r\n{\r\n    public void Reset() { }\r\n}";
        using var workspace = InMemoryWorkspace.Create(("Calc.cs", source));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Calc.cs"), "Reset", new[] { new ChangeSignatureParameterInput(name: "x", type: "int", defaultValue: "0") });
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData?.Message, Does.Contain("declares no parameters"));
        Assert.That(workspace.ReadText("Calc.cs"), Is.EqualTo(source));
    }
}
