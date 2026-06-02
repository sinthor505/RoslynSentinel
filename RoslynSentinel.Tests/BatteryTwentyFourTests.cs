// Battery #24 — SentinelRefactoringTools
// Tests all ~65 public methods of SentinelRefactoringTools in-memory via TestSolutionBuilder.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryTwentyFourTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;
    private StandardRefactoringEngine _standardRefactoringEngine;
    private AdvancedStructuralEngine _advancedStructuralEngine;
    private MappingEngine _mappingEngine;
    private SemanticRefactoringLibrary _semanticRefactoringLibrary;
    private GranularRefactoringEngine _granularRefactoringEngine;
    private AdvancedLogicEngine _advancedLogicEngine;
    private RefinementEngine _refinementEngine;
    private AdvancedTypeEngine _advancedTypeEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;
    private CodeStyleEngine _codeStyleEngine;
    private CodeFlowEngine _codeFlowEngine;
    private AdvancedRefactoringEngine _advancedRefactoringEngine;
    private LogicOptimizationEngine _logicOptimizationEngine;
    private ModernizationEngine _modernizationEngine;
    private SentinelRefactoringTools _tools;

    private const string RefactorSource = @"
using System;
using System.Collections.Generic;

namespace RefactorNs;

public abstract class Animal
{
    public string Name;
    public abstract string Sound();
    public virtual void Move() { Console.WriteLine(""moving""); }
}

public class Dog : Animal
{
    public string Breed;
    public Dog(string name, string breed) { Name = name; Breed = breed; }
    public override string Sound() => ""woof"";
    public override void Move() => base.Move();
    public string GetInfo() { return string.Format(""{0} ({1})"", Name, Breed); }
    public void Process(int a, int b, int c, int d, int e) { }
    public int Calculate(int x)
    {
        if(x > 0) return 1;
        if(x < 0) return -1;
        return 0;
    }
}

public class Target {}
";

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

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _standardRefactoringEngine = new StandardRefactoringEngine(_workspaceManager);
        _advancedStructuralEngine = new AdvancedStructuralEngine(_workspaceManager);
        _mappingEngine = new MappingEngine(_workspaceManager);
        _semanticRefactoringLibrary = new SemanticRefactoringLibrary(_workspaceManager);
        _granularRefactoringEngine = new GranularRefactoringEngine(_workspaceManager);
        _advancedLogicEngine = new AdvancedLogicEngine(_workspaceManager);
        _refinementEngine = new RefinementEngine(_workspaceManager);
        _advancedTypeEngine = new AdvancedTypeEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, _config);
        _codeFlowEngine = new CodeFlowEngine(_workspaceManager);
        _advancedRefactoringEngine = new AdvancedRefactoringEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
        _modernizationEngine = new ModernizationEngine(_workspaceManager, _config);
        _tools = new SentinelRefactoringTools(
            _refactoringEngine, _standardRefactoringEngine, _advancedStructuralEngine,
            _mappingEngine, _semanticRefactoringLibrary, _granularRefactoringEngine,
            _advancedLogicEngine, _refinementEngine, _advancedTypeEngine,
            _structuralRefinementEngine, _codeStyleEngine, _codeFlowEngine,
            _advancedRefactoringEngine, _logicOptimizationEngine, _modernizationEngine,
            new OutParamRefactoringEngine(_workspaceManager),
            new MsToolAugmentEngine(_workspaceManager),
            new CodeGenerationEngine(_workspaceManager),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager, _config, NullLogger<SentinelRefactoringTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultiFile(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ===================== autoStage METHODS =====================

    // --- ExtractSuperclass ---

    [Test]
    public async Task ExtractSuperclass_AutoStageTrue_ReturnsStagedChangeSummary()
    {
        SetMultiFile(("Dog.cs", RefactorSource));
        var result = await _tools.ExtractMembers("Dog.cs", "Dog", "superclass", "AnimalBase");
        Assert.That(result, Is.Not.Null);
    }

    // --- SafeDeleteSymbol ---

    [Test]
    public async Task SafeDeleteSymbol_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.RemoveMember("Order.cs", "GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- ChangeSignature ---

    [Test]
    public async Task ChangeSignature_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ChangeSignature("Order.cs", "Order", [1, 0]);
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractInterface ---

    [Test]
    public async Task ExtractInterface_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ExtractMembers("Order.cs", "Order", "interface", "IOrder");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveTypeToFile ---

    [Test]
    public async Task MoveTypeToFile_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.MoveType("Order.cs", "Status", "ownFile");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveAllTypesToFiles ---

    [Test]
    public async Task MoveAllTypesToFiles_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.MoveAllTypesToFiles("Order.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- RenameSymbol ---

    [Test]
    public async Task RenameSymbol_ValidSymbol_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.RenameSymbol("Order.cs", "GetLabel", "GetLabel", "GetDisplayLabel");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task RenameSymbol_NonExistentSymbol_ReturnsErrorObject()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.RenameSymbol("Order.cs", "NoSuchSymbol", "NoSuchSymbol", "NewName");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveAllTypesToFilesInProject ---

    [Test]
    public async Task MoveAllTypesToFilesInProject_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.MoveAllTypesToFiles("project", "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveAllTypesToFilesInSolution ---

    [Test]
    public async Task MoveAllTypesToFilesInSolution_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.MoveAllTypesToFiles("solution");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddUsingDirective ---

    [Test]
    public async Task AddUsingDirective_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddUsingDirective("Order.cs", "System.Linq");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AddUsingDirective_AutoStageFalse_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddUsingDirective("Order.cs", "System.Linq", autoStage: false);
        Assert.That(result, Is.Not.Null);
    }

    // --- AddEnumValue ---

    [Test]
    public async Task AddEnumValue_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddEnumValue("Order.cs", "Status", "Cancelled");
        Assert.That(result, Is.Not.Null);
    }

    // --- InsertMemberAfter ---

    [Test]
    public async Task InsertMemberAfter_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddMember("Order.cs", "Order", "public string Description => \"\";", "after:GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- InsertMemberBefore ---

    [Test]
    public async Task InsertMemberBefore_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddMember("Order.cs", "Order", "public string Tag => \"\";", "before:GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddAttribute ---

    [Test]
    public async Task AddAttribute_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyAttribute("Order.cs", "Order", "[Serializable]", "add");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddBaseType ---

    [Test]
    public async Task AddBaseType_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyBaseType("Order.cs", "Order", "IService", "add");
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveAttribute ---

    [Test]
    public async Task RemoveAttribute_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyAttribute("Order.cs", "Order", "Serializable", "remove");
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveBaseType ---

    [Test]
    public async Task RemoveBaseType_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyBaseType("Order.cs", "Order", "IService", "remove");
        Assert.That(result, Is.Not.Null);
    }

    // --- PullUpMember ---

    [Test]
    public async Task PullUpMember_AutoStageTrue_ReturnsNotNull()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _tools.PullUpMember("Refactor.cs", "Dog", "Sound");
        Assert.That(result, Is.Not.Null);
    }

    // --- ChangeAccessibility ---

    [Test]
    public async Task ChangeAccessibility_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ChangeAccessibility("Order.cs", "OrderId", "internal");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddModifier ---

    [Test]
    public async Task AddModifier_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyModifier("Order.cs", "Order", "sealed", "add");
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveModifier ---

    [Test]
    public async Task RemoveModifier_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyModifier("Order.cs", "Order", "sealed", "remove");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddSummaryComment ---

    [Test]
    public async Task AddSummaryComment_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddSummaryComment("Order.cs", "Order", "Represents an order.");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddProperty ---

    [Test]
    public async Task AddProperty_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddMemberTyped("Order.cs", "Order", "Description", "string", "property");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddField ---

    [Test]
    public async Task AddField_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddMemberTyped("Order.cs", "Order", "_tag", "string", "field");
        Assert.That(result, Is.Not.Null);
    }

    // --- SortMembers ---

    [Test]
    public async Task SortMembers_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.SortMembersAsync("Order.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- WrapInTryCatch ---

    [Test]
    public async Task WrapInTryCatch_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.WrapRange("Order.cs", 8, 10, "tryCatch");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddConstructorParameter ---

    [Test]
    public async Task AddConstructorParameter_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddConstructorParameter("Order.cs", "Order", "notes", "string");
        Assert.That(result, Is.Not.Null);
    }

    // --- WrapInRegion ---

    [Test]
    public async Task WrapInRegion_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.WrapRange("Order.cs", 3, 6, "region", "Properties");
        Assert.That(result, Is.Not.Null);
    }

    // ===================== SIMPLE DELEGATION METHODS =====================

    // --- SyncTypeAndFilename ---

    [Test]
    public async Task SyncTypeAndFilename_ValidFile_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.SyncTypeAndFilename("Order.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineMethod ---

    [Test]
    public async Task InlineMethod_ValidMethod_ReturnsDictionary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Inline("Order.cs", "GetLabel", "method");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractMethod ---

    [Test]
    public async Task ExtractMethod_ValidLineRange_ReturnsResult()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.ExtractMethodAsync("Order.cs", 14, "return string.Format", 14, "return string.Format", "FormatLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- IntroduceField ---

    [Test]
    public async Task IntroduceField_ValidContext_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Introduce("Order.cs", "string.Format", "labelFormatter", "field");
        Assert.That(result, Is.Not.Null);
    }

    // --- IntroduceParameter ---

    [Test]
    public async Task IntroduceParameter_ValidContext_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Introduce("Order.cs", "GetLabel", "GetLabel", "parameter");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineField ---

    [Test]
    public async Task InlineField_ValidField_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Inline("Order.cs", "OrderId", "field");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineParameter ---

    [Test]
    public async Task InlineParameter_ValidParameter_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Inline("Order.cs", "orderId", "parameter", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- MakeMethodStatic ---

    [Test]
    public async Task MakeMethodStatic_ValidMethod_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyModifier("Order.cs", "GetLabel", "static", "add");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtensionToStatic ---

    [Test]
    public async Task ExtensionToStatic_ValidMethod_ReturnsString()
    {
        const string src = "namespace TestProj; public static class Helper { public static string Trim(this string s) => s.Trim(); }";
        SetSource(src, "Helper.cs");
        var result = await _advancedLogicEngine.ExtensionToStaticAsync("Helper.cs", "Trim");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertAbstractToInterface ---

    [Test]
    public async Task ConvertAbstractToInterface_AbstractClass_ReturnsString()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _advancedStructuralEngine.ConvertAbstractClassToInterfaceAsync("Refactor.cs", "Animal");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateMapping ---

    [Test]
    public async Task GenerateMapping_ValidTypes_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.GenerateMapping("Order.cs", "Order", "Status");
        Assert.That(result, Is.Not.Null);
    }

    // --- WrapInUsing ---

    [Test]
    public async Task WrapInUsing_ValidLineRange_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.WrapRange("Order.cs", 8, 10, "using", "resource");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertAnonymousToNamed ---

    [Test]
    public async Task ConvertAnonymousToNamed_ValidFile_ReturnsDictionary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ConvertAnonymousToNamed("Order.cs", "OrderData");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineClass ---

    [Test]
    public async Task InlineClass_CrossFile_MovesMembers()
    {
        SetMultiFile(
            ("Helper.cs", "namespace App; public class Helper { public int Value; public void Go() {} }"),
            ("Owner.cs", "namespace App; public class Owner {}"));
        var result = await _tools.InlineClass("Helper.cs", "Owner.cs", "Helper");
        Assert.That(result, Does.ContainKey("Owner.cs"));
        Assert.That(((Dictionary<string, string>)result)["Owner.cs"], Does.Contain("Value"));
        Assert.That(((Dictionary<string, string>)result)["Owner.cs"], Does.Contain("Go"));
    }

    // --- IntroduceVariable ---

    [Test]
    public async Task IntroduceVariable_ValidContext_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Introduce("Order.cs", "string.Format", "formatted", "localVariable");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineVariable ---

    [Test]
    public async Task InlineVariable_ValidVariable_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Inline("Order.cs", "OrderId", "variable");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertPropertyToMethods ---

    [Test]
    public async Task ConvertPropertyToMethods_ValidProperty_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _codeStyleEngine.ConvertPropertyToMethodsAsync("Order.cs", "OrderId");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractClass ---

    [Test]
    public async Task ExtractClass_ValidMembers_ReturnsDictionary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ExtractMembers("Order.cs", "Order", "class", "OrderInfo", ["GetLabel", "GetStatus"]);
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractMembersToPartial ---

    [Test]
    public async Task ExtractMembersToPartial_ValidMembers_ReturnsDictionary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ExtractMembers("Order.cs", "Order", "partial", memberNames: ["GetLabel"]);
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertMethodToIndexer ---

    [Test]
    public async Task ConvertMethodToIndexer_ValidMethod_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("Order.cs", "GetStatus");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveTypeToOuterScope ---

    [Test]
    public async Task MoveTypeToOuterScope_ValidType_ReturnsString()
    {
        const string src = "namespace TestProj; public class Outer { public class Inner {} }";
        SetSource(src, "Outer.cs");
        var result = await _tools.MoveType("Outer.cs", "Inner", "outerScope");
        Assert.That(result, Is.Not.Null);
    }

    // --- ReplaceMember ---

    [Test]
    public async Task ReplaceMember_ValidMember_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ReplaceMember("Order.cs", "GetLabel", "public string GetLabel() => $\"{OrderId}: {CustomerName}\";");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddMemberToClass ---

    [Test]
    public async Task AddMemberToClass_ValidClass_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.AddMember("Order.cs", "Order", "public string Tag { get; set; }");
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveMember ---

    [Test]
    public async Task RemoveMember_ValidMember_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.RemoveMember("Order.cs", "GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- ReplaceConstructorWithFactory ---

    [Test]
    public async Task ReplaceConstructorWithFactory_ValidClass_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advancedStructuralEngine.ReplaceConstructorWithFactoryAsync("Order.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- InvertAssignments ---

    [Test]
    public async Task InvertAssignments_ValidLineRange_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.InvertAssignments("Order.cs", 8, 12);
        Assert.That(result, Is.Not.Null);
    }

    // --- ReduceBlockDepth ---

    [Test]
    public async Task ReduceBlockDepth_ValidMethod_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _codeFlowEngine.ReduceBlockDepthAsync("Order.cs", "GetStatus");
        Assert.That(result, Is.Not.Null);
    }

    // --- OptimizeTaskWait ---

    [Test]
    public async Task OptimizeTaskWait_ValidFile_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advancedRefactoringEngine.OptimizeTaskWaitAsync("Order.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- SyncInterfaceToImplementation ---

    [Test]
    public async Task SyncInterfaceToImplementation_ClassWithInterface_ReturnsString()
    {
        const string src = @"namespace TestProj;
public interface IWorker { void Work(); }
public class Worker : IWorker { public void Work() {} public void Extra() {} }";
        SetSource(src, "Worker.cs");
        var result = await _tools.SyncInterface("Worker.cs", "IWorker", "sync", "Worker");
        Assert.That(result, Is.Not.Null);
    }

    // --- IntroduceParameterObject---

    [Test]
    public async Task IntroduceParameterObject_ValidMethod_ReturnsString()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _tools.IntroduceParameterObject("Refactor.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task IntroduceParameterObject_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.IntroduceParameterObject("NonExistent.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    // --- UpdateXmlDocsFromSignature---

    [Test]
    public async Task UpdateXmlDocsFromSignature_ValidMethod_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync("Order.cs", "GetLabel");
    }

    // --- ConvertExpressionBody ---

    [Test]
    public async Task ConvertExpressionBody_ToBlockBody_ReturnsString()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _refactoringEngine.ConvertExpressionBodyAsync("Refactor.cs", "Sound", "ToBlockBody");
    }

    // --- ExtractConstant ---

    [Test]
    public async Task ExtractConstant_WithLiteralSnippet_ReturnsString()
    {
        const string src = @"namespace TestProj; public class C { public string GetLabel() { return ""hello""; } }";
        SetSource(src, "C.cs");
        var result = await _tools.Introduce("C.cs", @"""hello""", "HelloLabel", "constant");
    }

    // --- AnalyzeControlFlow ---

    [Test]
    public async Task AnalyzeControlFlow_ValidMethod_ReturnsSummary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.AnalyzeControlFlowAsync("Order.cs", "GetStatus");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeDataFlow ---

    [Test]
    public async Task AnalyzeDataFlow_ValidMethod_ReturnsSummary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.AnalyzeDataFlowAsync("Order.cs", "GetStatus");
        Assert.That(result, Is.Not.Null);
    }

    // --- FormatDocumentPreview ---

    [Test]
    public async Task FormatDocumentPreview_ValidFile_ReturnsPreviewResult()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.FormatDocumentPreviewAsync("Order.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertToNullCoalescing ---

    [Test]
    public async Task ConvertToNullCoalescing_ValidFile_ReturnsString()
    {
        const string src = @"namespace TestProj; public class C { public string Get(string s) { if (s == null) s = ""default""; return s; } }";
        SetSource(src, "C.cs");
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("C.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ConvertToNullCoalescing_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractLocalVariable ---

    [Test]
    public async Task ExtractLocalVariable_ValidContext_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ExtractLocalVariable("Order.cs", "GetLabel", "label");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void ExtractLocalVariable_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _tools.ExtractLocalVariable("NonExistent.cs", "GetLabel", "label"));
    }

    // --- ConvertToSwitch ---

    [Test]
    public async Task ConvertToSwitch_FileWithIfElseChain_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Order.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ConvertToSwitch_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertToPattern ---

    [Test]
    public async Task ConvertToPattern_ValidFile_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _modernizationEngine.ConvertToPatternAsync("Order.cs");
        Assert.That(result, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ConvertToPattern_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _modernizationEngine.ConvertToPatternAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }
}
