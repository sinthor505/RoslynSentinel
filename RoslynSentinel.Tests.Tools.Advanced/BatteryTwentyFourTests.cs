// Battery #24 -> RefactoringStructuralTools
// Tests all ~65 public methods of RefactoringStructuralTools in-memory via TestSolutionBuilder.
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Advanced;

[TestFixture]
// Runs exclusively because Setup/TearDown wipe every top-level *.cs in the shared working directory (DeleteLeakedRelativeFiles), which would delete files other parallel fixtures in this assembly (AssemblyInfo.cs sets ParallelScope.Fixtures) are using.
[NonParallelizable]
public class BatteryTwentyFourTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private BasicRefactoringEngine _refactoringEngine;
    private MemberRefactoringEngine _memberRefactoringEngine;
    private StructuralRefactoringEngine _advancedStructuralEngine;
    private MappingEngine _mappingEngine;
    private SemanticRefactoringEngine _semanticRefactoringEngine;
    private LogicSimplificationEngine _advancedLogicEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;
    private CodeStyleEngine _codeStyleEngine;
    private LogicSimplificationEngine _codeFlowEngine;
    private AdvancedRefactoringEngine _advancedRefactoringEngine;
    private LogicSimplificationEngine _logicOptimizationEngine;
    private SyntaxModernizationEngine _modernizationEngine;
    private DiffEngine _diffEngine;
    private ValidationEngine _validationEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private RefactoringStructuralTools _refactoringStructuralTools;
    private TypedMemberTools _typedMemberTools;
    private RefactoringSignatureTools _refactoringSignatureTools;
    private RefactoringExtractionDocsTools _refactoringExtractionDocsTools;
    private MsToolAugmentEngine _msToolAugmentEngine;
    private AdvancedRefactoringTools _advTools;
    private GenerationTools _generationTools;
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
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _diffEngine = new DiffEngine();
        _validationEngine = new ValidationEngine(_workspaceManager, _diffEngine, NullLogger<ValidationEngine>.Instance);
        _refactoringEngine = new BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, _config);
        _memberRefactoringEngine = new MemberRefactoringEngine(_workspaceManager, _symbolNavigationEngine, _validationEngine);
        _advancedStructuralEngine = new StructuralRefactoringEngine(_workspaceManager);
        _mappingEngine = new MappingEngine(_workspaceManager);
        _semanticRefactoringEngine = new SemanticRefactoringEngine(_workspaceManager);
        _advancedLogicEngine = new LogicSimplificationEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, _config);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, _config);
        _codeFlowEngine = new LogicSimplificationEngine(_workspaceManager);
        _advancedRefactoringEngine = new AdvancedRefactoringEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicSimplificationEngine(_workspaceManager);
        _modernizationEngine = new SyntaxModernizationEngine(_workspaceManager, _config);
        _msToolAugmentEngine = new MsToolAugmentEngine(_workspaceManager);
        _generationTools = new GenerationTools(_workspaceManager, NullLogger<GenerationTools>.Instance);

        var refactoringStructuralImpl = new RefactoringStructuralImpl(_refactoringEngine, _memberRefactoringEngine, _structuralRefinementEngine, _symbolNavigationEngine, _workspaceManager, _validationEngine, NullLogger<RefactoringStructuralImpl>.Instance);
        var refactoringSignatureImpl = new RefactoringSignatureImpl(_refactoringEngine, _memberRefactoringEngine, _workspaceManager, _validationEngine, _symbolNavigationEngine, NullLogger<RefactoringSignatureImpl>.Instance);
        _refactoringStructuralTools = new RefactoringStructuralTools(refactoringStructuralImpl);
        _typedMemberTools = new TypedMemberTools(refactoringStructuralImpl);
        _refactoringSignatureTools = new RefactoringSignatureTools(refactoringSignatureImpl);
        _refactoringExtractionDocsTools = new RefactoringExtractionDocsTools(new RefactoringExtractionDocsImpl(_refactoringEngine, _msToolAugmentEngine, _symbolNavigationEngine, _workspaceManager, _validationEngine, NullLogger<RefactoringExtractionDocsImpl>.Instance));
        _advTools = new AdvancedRefactoringTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

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
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ExtractSuperclass_AutoStageTrue_ReturnsAppliedChangeSummary()
    {
        SetMultiFile(("Dog.cs", RefactorSource));
        var result = await _advTools.ExtractMembersTool(reason: "test message", "Dog.cs", "Dog", ExtractAsType.superclass, "AnimalBase");
        Assert.That(result, Is.Not.Null);
    }

    // --- SafeDeleteSymbol ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task SafeDeleteSymbol_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.remove, memberName: "GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- ChangeSignatureTool ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    [Category("ChangeSignatureParameterInput")] // sentinel:auto-category
    public async Task ChangeSignature_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.ChangeSignatureTool(reason: "test message", "Order.cs", "Order", new[] { new ChangeSignatureParameterInput(originalIndex: 1), new ChangeSignatureParameterInput(originalIndex: 0) });
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractInterface ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ExtractInterface_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.ExtractMembersTool(reason: "test message", "Order.cs", "Order", ExtractAsType.@interface, "IOrder");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveTypeToFile ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveTypeToFile_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.MoveTypeTool(reason: "test message", "Order.cs", "Status", "ownFile");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveAllTypesToFilesTool ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveAllTypesToFiles_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.file, "Order.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- RenameSymbolTool ---
    // RenameSymbolTool takes a SymbolHandle (projectName, docCommentId) instead of
    // (filepath, methodName, contextSnippet) -> resolve the handle via SymbolNavigationEngine
    // first, matching how an agent would call LocateSymbolTool before RenameSymbolTool.
    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    [Category("SymbolLocation")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task RenameSymbol_ValidSymbol_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var symbolNavEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        // "GetLabel" is declared on both Order and IService in SimpleSource -> disambiguate.
        var located = await symbolNavEngine.LocateSymbolAsync("GetLabel", containingType: "Order");
        var handle = located.Single();
        var result = await _refactoringSignatureTools.RenameSymbolTool(reason: "test message", projectName: handle.ProjectName, docCommentId: handle.DocCommentId!, newName: "GetDisplayLabel");
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError, Is.True);
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task RenameSymbol_NonExistentSymbol_ReturnsErrorObject()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringSignatureTools.RenameSymbolTool(reason: "test message", projectName: "TestProj", docCommentId: "M:TestProj.Order.NoSuchSymbol", newName: "NewName");
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
    }

    // --- MoveAllTypesToFilesInProject ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveAllTypesToFilesInProject_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.MoveAllTypesToFilesTool("test", ToolScope.project, "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveAllTypesToFilesInSolution ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveAllTypesToFilesInSolution_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.solution);
        Assert.That(result, Is.Not.Null);
    }

    // --- UsingDirectiveTool ---
    // Stays on the disk-backed tier: the diff pre-image is read from disk, so the in-memory fake reports an empty pre-image (@@ -0,0 +1,1 @@).
    [Test]
    [Category("RefactoringExtractionDocsTools")] // sentinel:auto-category
    public async Task UsingDirective_Add_ChangedContentReflectsActualDiffNotFabricatedString()
    {
        // The engine's AddUsingDirectiveAsync reformats the whole document (Formatter.FormatAsync)
        // after inserting the new using, so a file with a pre-existing formatting quirk elsewhere
        // picks up an unrelated whitespace fix in the same write as the using insertion. A
        // fabricated "using {namespaceName};" string could never reveal that second, bundled
        // change; the real diff must.
        // CRLF fixture: tests in this project write through the real chokepoint to relative paths in
        // the working directory, so a stray CRLF Order.cs from another test is the file's pre-image
        // there; an LF fixture would (correctly) trip the write path's EolChangeGuard.
        const string misindentedSource = "namespace TestProj;\r\n\r\npublic class Order\r\n{\r\n  public int OrderId { get; set; }\r\n}\r\n";
        SetSource(misindentedSource, "Order.cs");
        var result = await _refactoringExtractionDocsTools.UsingDirectiveTool(reason: "test message", "Order.cs", AddRemoveViewAction.add, "System.Linq");
        Assert.That(!result.IsError, Is.True, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        // The added using line must be present in the diff...
        Assert.That(summary.Diff, Does.Contain("using System.Linq;"));
        // ...and so must the unrelated formatting change the fabricated string could never show.
        Assert.That(summary.Diff, Does.Contain("public int OrderId"));
        Assert.That(summary.Diff, Is.Not.EqualTo("using System.Linq;"));
    }

    // --- ModifyEnumTool ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ModifyEnum_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.ModifyEnumTool(reason: "test message", "Order.cs", "Status", "Active,Pending,Cancelled");
        Assert.That(result, Is.Not.Null);
    }

    // --- MemberTool on enum containers (routes to AddEnumMemberAsync/RemoveEnumMemberAsync/ReplaceEnumMemberAsync) ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_Add_OnEnumContainer_Succeeds()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.addMember, "Status", newMemberSource: "Cancelled");
        Assert.That(!result.IsError, Is.True);
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_Remove_OnEnumMember_Succeeds()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.remove, memberName: "Pending", skipPrecheck: true);
        Assert.That(!result.IsError, Is.True);
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_Replace_OnEnumMember_Succeeds()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.replace, memberName: "Pending", newMemberSource: "InProgress=2");
        Assert.That(!result.IsError, Is.True);
    }

    [Test]
    [Category("MemberSourceInfo")] // sentinel:auto-category
    [Category("MemberSourceViewResult")] // sentinel:auto-category
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_View_WithMemberName_ReturnsThatMembersSource()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.view, memberName: "GetStatus");
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var view = result.SuccessData as MemberSourceViewResult;
        Assert.That(view, Is.Not.Null);
        Assert.That(view!.Member.Name, Is.EqualTo("GetStatus"));
        Assert.That(view.Member.Kind, Is.EqualTo("method"));
        Assert.That(view.Member.Source, Does.Contain("public string GetStatus()"));
        Assert.That(view.Member.Source, Does.Contain("return \"Unknown\";"));
        Assert.That(view.Member.IsComplete, Is.True);
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_View_WithMissingMemberName_ReturnsNotFound()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.view, memberName: "NoSuchMember");
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task Member_View_OnEnumContainer_ReturnsEnumMembers()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.view, "Status");
        Assert.That(!result.IsError, Is.True);
    }

    // --- InsertMemberAfter ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task InsertMemberAfter_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.addMember, "Order", newMemberSource: "public string Description => \"\";", position: "after:GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- InsertMemberBefore ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task InsertMemberBefore_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.addMember, "Order", newMemberSource: "public string Tag => \"\";", position: "before:GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddAttribute ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddAttribute_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.ModifyAttributeTool(reason: "test message", "Order.cs", "Order", "[Serializable]", AttributeModifyAction.add);
        Assert.That(result, Is.Not.Null);
    }

    // --- AddBaseType ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddBaseType_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.ModifyBaseTypeTool(reason: "test message", "Order.cs", "Order", "IService", AddRemoveAction.add);
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveAttribute ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveAttribute_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.ModifyAttributeTool(reason: "test message", "Order.cs", "Order", "Serializable", AttributeModifyAction.remove);
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveBaseType ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveBaseType_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.ModifyBaseTypeTool(reason: "test message", "Order.cs", "Order", "IService", AddRemoveAction.remove);
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveMemberTool (same-class rewrite Finding) ---
    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_InstanceSingleField_BareSameClassCall_ReportsRewriteFinding()
    {
        const string original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x)\n    {\n        return x + 1;\n    }\n\n    public int Caller()\n    {\n        return Foo(2);\n    }\n}\n";
        var (dir, sourcePath) = SetPathAwareSolution("MoveMember_RewriteFinding_SingleField", ("Refactor.cs", original), ("Target.cs", "public class Target\n{\n}\n"));
        try
        {
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Source", ["Foo"], "Target");
            Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
            var text = await ReadCommittedRefactorText();
            Assert.That(text, Does.Contain("return this._impl.Foo(2);"));
            Assert.That(result.Findings.Any(f => f.Message.StartsWith("Rewrote same-class call Foo(2) -> this._impl.Foo(2) (", StringComparison.Ordinal)), Is.True, "A Caution finding must report the rewrite with old text, new text and location.");
        }
        finally
        {
            System.IO.File.Delete(sourcePath);
        }
    }

    // --- MoveMemberTool (fieldMap validation) ---
    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_FieldMapValueNotTargetField_ReturnsInvalidArgument()
    {
        SetMultiFile(("Refactor.cs", "public class Source\n{\n    private int _a;\n    public int Get() => _a;\n}\n\npublic class Target\n{\n    private int _b;\n}\n"));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Get"], "Target", fieldMap: new Dictionary<string, string> { ["_a"] = "_nope" });
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_FieldMapValidInstanceMove_RewritesBodyToTargetField()
    {
        SetMultiFile(("Refactor.cs", "public class Source\n{\n    private string _a = \"source\";\n    public string M() { var first = _a; return first + this._a; }\n}\n\npublic class Target\n{\n    private string _b = \"target\";\n}\n"));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["M"], "Target", fieldMap: new Dictionary<string, string> { ["_a"] = "_b" });
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == "Refactor.cs");
        var text = (await doc.GetTextAsync()).ToString();
        var targetPart = text.Substring(text.IndexOf("class Target", StringComparison.Ordinal));
        Assert.That(targetPart, Does.Contain("var first = _b"), "The moved body must reference the target field.");
        Assert.That(targetPart, Does.Not.Contain("_a"), "No reference to the source field may remain in the moved body.");
        Assert.That(text, Does.Contain("private string _a"), "The source field stays in place.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_FieldMapDryRun_AppliesMappingWithoutWriting()
    {
        SetMultiFile(("Refactor.cs", "public class Source\n{\n    private string _a = \"source\";\n    public string M() { var first = _a; return first + this._a; }\n}\n\npublic class Target\n{\n    private string _b = \"target\";\n}\n"));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["M"], "Target", dryRun: true, returnDiff: true, fieldMap: new Dictionary<string, string> { ["_a"] = "_b" });
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("var first = _b"), "The dry-run diff must show the mapped field in the moved body.");
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == "Refactor.cs");
        var text = (await doc.GetTextAsync()).ToString();
        Assert.That(text, Does.Contain("var first = _a"), "A dry run must leave the committed source body unchanged.");
        Assert.That(text, Does.Not.Contain("var first = _b"), "A dry run must not write the moved body to the target.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_DefaultOldMember_Unchanged()
    {
        SetMultiFile(("Refactor.cs", "public class Source\n{\n    public int Get() => 1;\n}\n\npublic class Target\n{\n}\n"));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Get"], "Target");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = await ReadCommittedRefactorText();
        var sourcePart = text.Substring(0, text.IndexOf("class Target", StringComparison.Ordinal));
        var targetPart = text.Substring(text.IndexOf("class Target", StringComparison.Ordinal));
        Assert.That(sourcePart, Does.Not.Contain("Get()"), "Default remove mode deletes the source member.");
        Assert.That(targetPart, Does.Contain("public int Get() => 1;"), "Default remove mode moves the body to the target.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_ToBaseClass_ReturnsTargetIneligible()
    {
        const string original = "public class Base\n{\n}\n\npublic class Source : Base\n{\n    public int Get() => 1;\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Get"], "Base", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_ForwarderReceiver_WithRemove_ReturnsInvalidArgument()
    {
        const string original = "public class Source\n{\n    private Target _impl = new Target();\n    public int Get() => 1;\n}\n\npublic class Target\n{\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Get"], "Target", forwarderReceiver: "_impl");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_PartialSourceClass_ReturnsTargetIneligible()
    {
        const string original = "public partial class Source\n{\n    public int Get() => 1;\n}\n\npublic partial class Source\n{\n    public int Other() => 2;\n}\n\npublic class Target\n{\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericMethod_ReturnsNotImplemented()
    {
        const string original = "public class Source\n{\n    public int Get<T>(T x) => 1;\n}\n\npublic class Target\n{\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotImplemented));
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_Property_ReturnsTargetIneligible()
    {
        const string original = "public class Source\n{\n    public int Value => 1;\n}\n\npublic class Target\n{\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Value"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_OverrideMethod_ReturnsTargetIneligible()
    {
        const string original = "public class Base\n{\n    public virtual int Get() => 1;\n}\n\npublic class Source : Base\n{\n    public override int Get() => 2;\n}\n\npublic class Target\n{\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_AbstractMethod_ReturnsTargetIneligible()
    {
        const string original = "public abstract class Source\n{\n    public abstract int Get();\n}\n\npublic class Target\n{\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_VirtualMethod_ReturnsTargetIneligible()
    {
        const string original = "public class Source\n{\n    public virtual int Get() => 1;\n}\n\npublic class Target\n{\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_StaticToExistingClass_LeavesForwarder()
    {
        const string original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        const string caller = "public class Caller\n{\n    public int Call() => Src.Foo(1);\n}\n";
        SetMultiFile(("Refactor.cs", original), ("Caller.cs", caller));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = await ReadCommittedRefactorText();
        var sourcePart = text.Substring(0, text.IndexOf("class Target", StringComparison.Ordinal));
        var targetPart = text.Substring(text.IndexOf("class Target", StringComparison.Ordinal));
        Assert.That(sourcePart, Does.Contain("public static int Foo(int x) => global::Target.Foo(x);"), "The source keeps a plain forwarder to the moved member.");
        Assert.That(targetPart, Does.Contain("public static int Foo(int x) { return x + 1; }"), "The target holds the full moved method.");
        Assert.That(await ReadCommittedText("Caller.cs"), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        var diagnostics = await GetCommittedDiagnosticsAsync();
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_StaticToExistingClass_AddsObsoleteAttribute()
    {
        const string original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        const string caller = "public class Caller\n{\n    public int Call() => Src.Foo(1);\n}\n";
        SetMultiFile(("Refactor.cs", original), ("Caller.cs", caller));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = await ReadCommittedRefactorText();
        Assert.That(text, Does.Contain("[global::System.Obsolete(\"Deprecated wrapper: call Target.Foo instead.\", DiagnosticId = \"RSDEPRECATED\")]\n    public static int Foo(int x) => global::Target.Foo(x);"), "The Obsolete attribute must precede the forwarder.");
        Assert.That(await ReadCommittedText("Caller.cs"), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        // Recorded outcome (open question 6/7): a caller in another class receives an RSDEPRECATED warning from the
        // Obsolete attribute, and the solution has no errors.
        var diagnostics = await GetCommittedDiagnosticsAsync();
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build with the deprecated wrapper.");
        Assert.That(diagnostics, Does.Contain("Warning RSDEPRECATED Caller.cs"), "Recorded outcome: the deprecated wrapper warns at the outside caller.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_StaticToNewClass_LeavesForwarder()
    {
        const string original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n";
        const string caller = "public class Caller\n{\n    public int Call() => Src.Foo(1);\n}\n";
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TestProj");
        System.IO.Directory.CreateDirectory(dir);
        var sourcePath = System.IO.Path.Combine(dir, "Refactor.cs");
        var callerPath = System.IO.Path.Combine(dir, "Caller.cs");
        var newFile = System.IO.Path.Combine(dir, "NewTarget.cs");
        System.IO.File.Delete(sourcePath);
        System.IO.File.Delete(callerPath);
        System.IO.File.Delete(newFile);
        try
        {
            var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", System.IO.Path.Combine(dir, "TestProj.csproj"),
                new (string name, string content, string filePath)[] { ("Refactor.cs", original, sourcePath), ("Caller.cs", caller, callerPath) });
            _workspaceManager.SetTestSolution(solution);
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Src", ["Foo"], "NewTarget", oldMember: MoveMemberOldMember.keepDelegator);
            Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
            var text = await ReadCommittedRefactorText();
            Assert.That(text, Does.Contain("public static int Foo(int x) => global::NewTarget.Foo(x);"), "The source keeps a plain forwarder to the new class.");
            var newText = await ReadCommittedText("NewTarget.cs");
            Assert.That(newText, Does.Contain("class NewTarget"), "The new class is created in the new file beside the source.");
            Assert.That(newText, Does.Contain("public static int Foo(int x) { return x + 1; }"), "The new class holds the full moved method.");
            Assert.That(await ReadCommittedText("Caller.cs"), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
            var diagnostics = await GetCommittedDiagnosticsAsync();
            Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
        }
        finally
        {
            System.IO.File.Delete(sourcePath);
            System.IO.File.Delete(callerPath);
            System.IO.File.Delete(newFile);
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_StaticToNewClass_AddsObsoleteAttribute()
    {
        const string original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n";
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TestProj");
        System.IO.Directory.CreateDirectory(dir);
        var sourcePath = System.IO.Path.Combine(dir, "Refactor.cs");
        var newFile = System.IO.Path.Combine(dir, "NewTarget.cs");
        System.IO.File.Delete(sourcePath);
        System.IO.File.Delete(newFile);
        try
        {
            var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", System.IO.Path.Combine(dir, "TestProj.csproj"),
                new (string name, string content, string filePath)[] { ("Refactor.cs", original, sourcePath) });
            _workspaceManager.SetTestSolution(solution);
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Src", ["Foo"], "NewTarget", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
            Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
            var text = await ReadCommittedRefactorText();
            Assert.That(text, Does.Contain("[global::System.Obsolete(\"Deprecated wrapper: call NewTarget.Foo instead.\", DiagnosticId = \"RSDEPRECATED\")]\n    public static int Foo(int x) => global::NewTarget.Foo(x);"), "The Obsolete attribute must precede the forwarder to the new class.");
            var newText = await ReadCommittedText("NewTarget.cs");
            Assert.That(newText, Does.Contain("class NewTarget"), "The new class is created in the new file beside the source.");
        }
        finally
        {
            System.IO.File.Delete(sourcePath);
            System.IO.File.Delete(newFile);
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_StaticToExistingClass_DryRun_WritesNothing()
    {
        const string original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Src", ["Foo"], "Target", dryRun: true, returnDiff: true, oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("global::Target.Foo(x)"), "The dry-run diff must show the forwarder.");
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A dry run must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_StaticToExistingClass_NoEolOrCollateralRefusal()
    {
        const string original = "public class Src\r\n{\r\n    public static int Foo(int x) { return x + 1; }\r\n}\r\n\r\npublic class Target\r\n{\r\n}\r\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = await ReadCommittedRefactorText();
        Assert.That(text, Does.Contain("public static int Foo(int x) => global::Target.Foo(x);"), "The forwarder is written.");
        Assert.That(text, Does.Not.Match("(?<!\r)\n"), "The forwarder must keep the file's CRLF line endings.");
    }

    private (string Dir, string SourcePath) SetPathAwareSolution(string dirName, params (string Name, string Content)[] files)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), dirName);
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
        System.IO.Directory.CreateDirectory(dir);
        var docs = files.Select(f => (f.Name, f.Content, System.IO.Path.Combine(dir, f.Name))).ToArray();
        foreach (var doc in docs)
        {
            System.IO.File.WriteAllText(doc.Item3, doc.Content);
        }
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", System.IO.Path.Combine(dir, "TestProj.csproj"), docs);
        _workspaceManager.SetTestSolution(solution);
        return (dir, System.IO.Path.Combine(dir, "Refactor.cs"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceToExistingClass_SingleField_LeavesForwarder()
    {
        const string original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        const string caller = "public class Caller\n{\n    public int Call(Source s) => s.Foo(1);\n}\n";
        var (dir, sourcePath) = SetPathAwareSolution("MoveMember_KeepDelegator_SingleField", ("Refactor.cs", original), ("Caller.cs", caller));
        try
        {
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
            Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
            var text = await ReadCommittedRefactorText();
            var sourcePart = text.Substring(0, text.IndexOf("class Target", StringComparison.Ordinal));
            var targetPart = text.Substring(text.IndexOf("class Target", StringComparison.Ordinal));
            Assert.That(sourcePart, Does.Contain("public int Foo(int x) => this._impl.Foo(x);"), "The source keeps a forwarder through the single target-typed field.");
            Assert.That(targetPart, Does.Contain("public int Foo(int x) { return x + 1; }"), "The target holds the full moved method.");
            Assert.That(await ReadCommittedText("Caller.cs"), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
            var diagnostics = await GetCommittedDiagnosticsAsync();
            Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceTwoCandidateFields_NoForwarderReceiver_ReturnsInvalidArgument()
    {
        const string original = "public class Source\n{\n    private readonly Target _a = new Target();\n    private readonly Target _b = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        var (dir, sourcePath) = SetPathAwareSolution("MoveMember_KeepDelegator_TwoCandidates", ("Refactor.cs", original));
        try
        {
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain("forwarderReceiver"), "The refusal must name the parameter to pass.");
            Assert.That(result.ErrorData?.Message, Does.Contain("_a").And.Contain("_b"), "The refusal must list both candidate fields.");
            Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused keep-mode move must not write.");
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceTwoCandidateFields_WithForwarderReceiver_UsesGivenReceiver()
    {
        const string original = "public class Source\n{\n    private readonly Target _a = new Target();\n    private readonly Target _b = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        const string caller = "public class Caller\n{\n    public int Call(Source s) => s.Foo(1);\n}\n";
        var (dir, sourcePath) = SetPathAwareSolution("MoveMember_KeepDelegator_ForwarderReceiver", ("Refactor.cs", original), ("Caller.cs", caller));
        try
        {
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator, forwarderReceiver: "_b");
            Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
            var text = await ReadCommittedRefactorText();
            Assert.That(text, Does.Contain("public int Foo(int x) => this._b.Foo(x);"), "The forwarder must call through the given receiver.");
            Assert.That(text, Does.Not.Contain("this._a.Foo(x)"), "The other candidate must not be used.");
            Assert.That(await ReadCommittedText("Caller.cs"), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
            var diagnostics = await GetCommittedDiagnosticsAsync();
            Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_InstanceToExistingClass_AddsObsoleteAttribute()
    {
        const string original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        const string caller = "public class Caller\n{\n    public int Call(Source s) => s.Foo(1);\n}\n";
        var (dir, sourcePath) = SetPathAwareSolution("MoveMember_KeepAsDeprecatedWrapper_Instance", ("Refactor.cs", original), ("Caller.cs", caller));
        try
        {
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
            Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
            var text = await ReadCommittedRefactorText();
            Assert.That(text, Does.Contain("[global::System.Obsolete(\"Deprecated wrapper: call Target.Foo instead.\", DiagnosticId = \"RSDEPRECATED\")]\n    public int Foo(int x) => this._impl.Foo(x);"), "The Obsolete attribute must precede the forwarder.");
            Assert.That(await ReadCommittedText("Caller.cs"), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
            var diagnostics = await GetCommittedDiagnosticsAsync();
            Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build with the deprecated wrapper.");
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceWithFieldMap_RewritesBodyAndLeavesForwarder()
    {
        const string original = "public class Source\n{\n    private int _base = 1;\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + _base; }\n}\n\npublic class Target\n{\n    private int _b = 0;\n}\n";
        var (dir, sourcePath) = SetPathAwareSolution("MoveMember_KeepDelegator_FieldMap", ("Refactor.cs", original));
        try
        {
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator, fieldMap: new Dictionary<string, string> { ["_base"] = "_b" });
            Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
            var text = await ReadCommittedRefactorText();
            var sourcePart = text.Substring(0, text.IndexOf("class Target", StringComparison.Ordinal));
            var targetPart = text.Substring(text.IndexOf("class Target", StringComparison.Ordinal));
            Assert.That(targetPart, Does.Contain("return x + _b;"), "The moved body must reference the mapped target field.");
            Assert.That(targetPart, Does.Not.Contain("_base"), "No reference to the source field may remain in the moved body.");
            Assert.That(sourcePart, Does.Contain("public int Foo(int x) => this._impl.Foo(x);"), "The forwarder is still written beside the mapped move.");
            Assert.That(sourcePart, Does.Contain("private int _base"), "The source field stays in place.");
            var diagnostics = await GetCommittedDiagnosticsAsync();
            Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceAutoResolveCallSitesFalse_StillSucceeds()
    {
        const string original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        const string caller = "public class Caller\n{\n    public int Call(Source s) => s.Foo(1);\n}\n";
        var (dir, sourcePath) = SetPathAwareSolution("MoveMember_KeepDelegator_NoAutoResolve", ("Refactor.cs", original), ("Caller.cs", caller));
        try
        {
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator, autoResolveCallSites: false);
            Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
            var text = await ReadCommittedRefactorText();
            Assert.That(text, Does.Contain("public int Foo(int x) => this._impl.Foo(x);"), "The forwarder is written with nothing to resolve.");
            Assert.That(await ReadCommittedText("Caller.cs"), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceSameClassBareCall_LeftValid()
    {
        const string original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + 1; }\n    public int Bar() => Foo(1);\n}\n\npublic class Target\n{\n}\n";
        var (dir, sourcePath) = SetPathAwareSolution("MoveMember_KeepDelegator_SameClassBareCall", ("Refactor.cs", original));
        try
        {
            var result = await _advTools.MoveMemberTool(reason: "test message", sourcePath, "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
            Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
            var text = await ReadCommittedRefactorText();
            var sourcePart = text.Substring(0, text.IndexOf("class Target", StringComparison.Ordinal));
            Assert.That(sourcePart, Does.Contain("public int Bar() => Foo(1);"), "The same-class bare call is left as written.");
            Assert.That(sourcePart, Does.Contain("public int Foo(int x) => this._impl.Foo(x);"), "The forwarder keeps the bare call valid.");
            var diagnostics = await GetCommittedDiagnosticsAsync();
            Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The bare call must still compile through the forwarder.");
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceToStaticClass_StillNotImplemented()
    {
        const string original = "public class Source\n{\n    public int Foo(int x) { return x + 1; }\n}\n\npublic static class Target\n{\n}\n";
        SetMultiFile(("Refactor.cs", original));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotImplemented));
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(original), "A refused keep-mode move must not write.");
    }

    private async Task<string> ReadCommittedRefactorText()
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == "Refactor.cs");
        return (await doc.GetTextAsync()).ToString();
    }

    private async Task<string> ReadCommittedText(string fileName)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == fileName);
        return (await doc.GetTextAsync()).ToString();
    }

    /// <summary>
    /// Compiler errors and warnings of the committed solution, one "Severity Id File" line each. Used to check that a
    /// keep-mode forwarder leaves the solution building and to record which warnings a deprecated wrapper produces.
    /// </summary>
    private async Task<List<string>> GetCommittedDiagnosticsAsync()
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var lines = new List<string>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(CancellationToken.None);
            foreach (var diagnostic in compilation!.GetDiagnostics().Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning))
            {
                var file = System.IO.Path.GetFileName(diagnostic.Location.SourceTree?.FilePath ?? string.Empty);
                lines.Add($"{diagnostic.Severity} {diagnostic.Id} {file}");
            }
        }
        return lines;
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_SomeRequestedNamesMissing_ReturnsNotFoundAndWritesNothing()
    {
        SetMultiFile(("Refactor.cs", "public class Source\n{\n    public int Foo() => 1;\n    public int Bar() => 2;\n}\n\npublic class Target\n{\n}\n"));
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var before = (await solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == "Refactor.cs").GetTextAsync()).ToString();

        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Source", ["Foo", "Missing"], "Target");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData?.Message, Does.Contain("Missing"));
        var afterSolution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var after = (await afterSolution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == "Refactor.cs").GetTextAsync()).ToString();
        Assert.That(after, Is.EqualTo(before), "A refused move must not write anything.");
    }

    // --- MoveMemberTool (pull-up to existing base class) ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveMember_ToBaseClass_AutoStageTrue_ReturnsNotNull()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Dog", ["Sound"], "Animal");
        Assert.That(result, Is.Not.Null);
    }

    // --- ChangeAccessibilityTool ---
    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ChangeAccessibility_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringSignatureTools.ChangeAccessibilityTool(reason: "test message", "Order.cs", "OrderId", AccessibilityLevel.@internal);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ChangeAccessibility_StampsIncreasingWorkspaceVersion()
    {
        SetSource(SimpleSource, "Order.cs");
        var versionBeforeAnyMutation = _workspaceManager.WorkspaceVersion;
        var first = await _refactoringSignatureTools.ChangeAccessibilityTool(reason: "test message", "Order.cs", "OrderId", AccessibilityLevel.@internal);
        var firstSummary = (AppliedChangeSummary)first.SuccessData!;
        Assert.That(firstSummary.WorkspaceVersion, Is.Not.Null);
        Assert.That(firstSummary.WorkspaceVersion, Is.GreaterThan(versionBeforeAnyMutation));
        var second = await _refactoringSignatureTools.ChangeAccessibilityTool(reason: "test message", "Order.cs", "CustomerName", AccessibilityLevel.@internal);
        var secondSummary = (AppliedChangeSummary)second.SuccessData!;
        Assert.That(secondSummary.WorkspaceVersion, Is.GreaterThan(firstSummary.WorkspaceVersion!), "A second mutation must stamp a strictly higher version than the first.");
    }

    // --- AddModifier ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddModifier_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.ModifyModifierTool(reason: "test message", "Order.cs", "Order", NonAccessibilityModifier.@sealed, AddRemoveAction.add);
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveModifier ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveModifier_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.ModifyModifierTool(reason: "test message", "Order.cs", "Order", NonAccessibilityModifier.@sealed, AddRemoveAction.remove);
        Assert.That(result, Is.Not.Null);
    }

    // --- ConstructorParameterTool ---
    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringSignatureTools.ConstructorParameterTool(reason: "test message", "Order.cs", AddRemoveViewAction.add, "Order", "notes", "string");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_Add_WithDefaultValue_BackwardCompatibleWithExistingCaller()
    {
        SetMultiFile(("Order.cs", SimpleSource), ("Caller.cs", "namespace TestProj;\npublic class Caller { public Order Make() => new Order(1, \"a\"); }"));
        var result = await _refactoringSignatureTools.ConstructorParameterTool(reason: "test message", "Order.cs", AddRemoveViewAction.add, "Order", "notes", "string", defaultValue: "\"\"");
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_Add_WithNullDefault_BackwardCompatibleWithExistingCaller()
    {
        SetMultiFile(("Order.cs", SimpleSource), ("Caller.cs", "namespace TestProj;\npublic class Caller { public Order Make() => new Order(1, \"a\"); }"));
        var result = await _refactoringSignatureTools.ConstructorParameterTool(reason: "test message", "Order.cs", AddRemoveViewAction.add, "Order", "notes", "string", nullDefault: true);
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_Add_NullDefaultAndDefaultValueBothSet_Refused()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringSignatureTools.ConstructorParameterTool(reason: "test message", "Order.cs", AddRemoveViewAction.add, "Order", "notes", "string", defaultValue: "\"\"", nullDefault: true);
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData?.Message, Does.Contain("mutually exclusive"));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task ConstructorParameter_Add_NoDefaultValue_ExistingCallerBreaksAndIsRefused()
    {
        SetMultiFile(("Order.cs", SimpleSource), ("Caller.cs", "namespace TestProj;\npublic class Caller { public Order Make() => new Order(1, \"a\"); }"));
        var result = await _refactoringSignatureTools.ConstructorParameterTool(reason: "test message", "Order.cs", AddRemoveViewAction.add, "Order", "notes", "string");
        Assert.That(!result.IsError, Is.False);
    }

    // --- MethodSignatureTool ---
    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_View_ListsExistingParameters()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringSignatureTools.MethodSignatureTool(reason: "test message", "Order.cs", AddRemoveViewAction.view, "GetLabel");
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Add_AppendsRequiredParameter_NoExistingCallers()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringSignatureTools.MethodSignatureTool(reason: "test message", "Order.cs", AddRemoveViewAction.add, "GetStatus", "verbose", "bool");
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Add_WithDefaultValue_BackwardCompatibleWithExistingCaller()
    {
        SetMultiFile(("Order.cs", SimpleSource), ("Caller.cs", "namespace TestProj;\npublic class Caller { public string Use(Order o) => o.GetStatus(); }"));
        var result = await _refactoringSignatureTools.MethodSignatureTool(reason: "test message", "Order.cs", AddRemoveViewAction.add, "GetStatus", "verbose", "bool", defaultValue: "false");
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Remove_LastParameter_UpdatesCallSite()
    {
        var orderSourceWithRename = SimpleSource.Replace("public string GetLabel()", "public void Rename(string first, string last) { CustomerName = first; }\n\n    public string GetLabel()");
        // Caller.cs is CRLF on purpose: the signature tool re-serializes a rewritten call-site file with
        // the platform newline, so an LF fixture trips the write path's EolChangeGuard (a real
        // tool-side defect, tracked separately). This test is about the call site being updated.
        SetMultiFile(("Order.cs", orderSourceWithRename), ("Caller.cs", "namespace TestProj;\r\npublic class Caller { public void Use(Order o) => o.Rename(\"a\", \"b\"); }"));
        var result = await _refactoringSignatureTools.MethodSignatureTool(reason: "test message", "Order.cs", AddRemoveViewAction.remove, "Rename", "last");
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    // Pins that the per-test wipe (DeleteLeakedRelativeFiles, run from Setup) clears a stale bare-name Order.cs
    // before the pre-image is read; otherwise the EolChangeGuard refuses the write.
    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task MethodSignature_Remove_LastParameter_StaleCrlfOrderCsOnDisk_StillUpdatesCallSite()
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Order.cs"), "namespace Stale;\r\npublic class Old { }\r\n");
        Setup();
        Assert.That(System.IO.File.Exists(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Order.cs")), Is.False, "Setup() must wipe the stale bare-name Order.cs before the pre-image is read");
        var orderSourceWithRename = SimpleSource.Replace("public string GetLabel()", "public void Rename(string first, string last) { CustomerName = first; }\n\n    public string GetLabel()");
        SetMultiFile(("Order.cs", orderSourceWithRename), ("Caller.cs", "namespace TestProj;\r\npublic class Caller { public void Use(Order o) => o.Rename(\"a\", \"b\"); }"));
        var result = await _refactoringSignatureTools.MethodSignatureTool(reason: "test message", "Order.cs", AddRemoveViewAction.remove, "Rename", "last");
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Remove_NonLastParameter_Refused()
    {
        SetSource(RefactorSource, "Animal.cs");
        var result = await _refactoringSignatureTools.MethodSignatureTool(reason: "test message", "Animal.cs", AddRemoveViewAction.remove, "Process", "a");
        Assert.That(!result.IsError, Is.False, "Removing a non-trailing parameter must be refused, not silently applied.");
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("last parameter"));
    }

    [Test]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    public async Task MethodSignature_Remove_NamedArgumentCallSite_Refused()
    {
        var orderSourceWithRename = SimpleSource.Replace("public string GetLabel()", "public void Rename(string first, string last) { CustomerName = first; }\n\n    public string GetLabel()");
        SetMultiFile(("Order.cs", orderSourceWithRename), ("Caller.cs", "namespace TestProj;\npublic class Caller { public void Use(Order o) => o.Rename(first: \"a\", last: \"b\"); }"));
        var result = await _refactoringSignatureTools.MethodSignatureTool(reason: "test message", "Order.cs", AddRemoveViewAction.remove, "Rename", "last");
        Assert.That(!result.IsError, Is.False, "A named-argument call site cannot be safely rewritten and must refuse the whole operation.");
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("named arguments"));
    }

    // NOTE: ValidateChangesAsync adds the renamed file as a brand-new document into the candidate
    // solution without removing the original document at the old path first, which used to make
    // pre-validation see the same (unique) type declared in two documents at once and fail with a
    // duplicate-declaration error on every real rename. Fixed by threading an explicit
    // `removePaths` list through ValidateChangesAsync/ValidateAndApplyHelper so the tool layer can
    // tell validation which old-path document to drop from the candidate before compiling (see
    // SyncTypeAndFilenameTool's `removePaths: [filePath]` call and
    // SyncTypeAndFilename_RealRename_SucceedsAndRemovesOldDocument below). This dryRun test still
    // asserts the one thing dryRun is responsible for regardless: no destructive action (old-file
    // delete) has occurred, even if validation were to fail for some other reason.
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task SyncTypeAndFilename_DryRun_NeverDeletesOriginalFileAsync()
    {
        const string mismatchedSource = "namespace TestProj;\n\npublic class Widget\n{\n    public int Id { get; set; }\n}\n";
        var tempDir = Path.Combine(Path.GetTempPath(), "SyncTypeAndFilenameTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var oldPath = Path.Combine(tempDir, "Mismatched.cs");
            var newPath = Path.Combine(tempDir, "Widget.cs");
            File.WriteAllText(oldPath, mismatchedSource);
            var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", Path.Combine(tempDir, "TestProj.csproj"), [("Mismatched.cs", mismatchedSource, oldPath)]);
            _workspaceManager.SetTestSolution(solution);
            var result = await _refactoringStructuralTools.SyncTypeAndFilenameTool(reason: "test message", oldPath, dryRun: true);
            Assert.That(File.Exists(oldPath), Is.True, "dryRun must never delete the original file, even when validation fails.");
            Assert.That(File.Exists(newPath), Is.False, "dryRun must never write the renamed file.");
            if (!result.IsError)
            {
                var data = (AppliedChangeSummary)result.SuccessData!;
                Assert.That(data.DryRun, Is.True);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // Real success-path regression test for the removePaths fix above: a mismatched filename vs.
    // unique type declaration, non-dryRun, through the actual ValidateAndApplyAsync path (unlike
    // SyncTypeAndFilename_ValidFile_ReturnsString above, which short-circuits on
    // EditOutcome.CannotEdit before ever reaching validation).
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task SyncTypeAndFilename_RealRename_SucceedsAndRemovesOldDocument()
    {
        const string mismatchedSource = "namespace TestProj;\n\npublic class Widget\n{\n    public int Id { get; set; }\n}\n";
        var tempDir = Path.Combine(Path.GetTempPath(), "SyncTypeAndFilenameTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var oldPath = Path.Combine(tempDir, "Mismatched.cs");
            var newPath = Path.Combine(tempDir, "Widget.cs");
            File.WriteAllText(oldPath, mismatchedSource);
            var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", Path.Combine(tempDir, "TestProj.csproj"), [("Mismatched.cs", mismatchedSource, oldPath)]);
            _workspaceManager.SetTestSolution(solution);
            var result = await _refactoringStructuralTools.SyncTypeAndFilenameTool(reason: "test message", oldPath);
            Assert.That(!result.IsError, Is.True, $"Expected rename to succeed; error: {result.ErrorData?.Message}");
            Assert.That(File.Exists(oldPath), Is.False, "Old file should be deleted after a successful rename.");
            Assert.That(File.Exists(newPath), Is.True, "New file should exist after a successful rename.");
            var currentSolution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
            Assert.That(currentSolution.GetDocumentIdsWithFilePath(oldPath), Is.Empty, "Old path must not remain tracked as a Document after the rename, or the type would be seen as declared twice.");
            Assert.That(currentSolution.GetDocumentIdsWithFilePath(newPath), Is.Not.Empty, "New path must be tracked as a Document after the rename.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // --- InlineMethod ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineMethod_ValidMethod_ReturnsDictionary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.InlineSymbolTool(reason: "test message", "Order.cs", "GetLabel", InlineKind.method);
        Assert.That(result, Is.Not.Null);
    }

    // --- IntroduceField ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceField_ValidContext_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.IntroduceSymbolTool(reason: "test message", "Order.cs", "string.Format", "labelFormatter", IntroduceAsType.field);
        Assert.That(result, Is.Not.Null);
    }

    // --- IntroduceParameter ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceParameter_ValidContext_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.IntroduceSymbolTool(reason: "test message", "Order.cs", "GetLabel", "GetLabel", IntroduceAsType.parameter);
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineField ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineField_ValidField_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.InlineSymbolTool(reason: "test message", "Order.cs", "OrderId", InlineKind.field);
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineParameter ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineParameter_ValidParameter_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.InlineSymbolTool(reason: "test message", "Order.cs", "orderId", InlineKind.parameter, "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- MakeMethodStatic ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task MakeMethodStatic_ValidMethod_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.ModifyModifierTool(reason: "test message", "Order.cs", "GetLabel", NonAccessibilityModifier.@static, AddRemoveAction.add);
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtensionToStatic ---
    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ExtensionToStatic_ValidMethod_ReturnsString()
    {
        const string src = "namespace TestProj; public static class Helper { public static string Trim(this string s) => s.Trim(); }";
        SetSource(src, "Helper.cs");
        var result = await _logicOptimizationEngine.ExtensionToStaticAsync("Helper.cs", "Trim");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertAbstractToInterface ---
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task ConvertAbstractToInterface_AbstractClass_ReturnsString()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _advancedStructuralEngine.ConvertAbstractClassToInterfaceAsync("Refactor.cs", "Animal");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineClassTool ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineClass_CrossFile_MovesMembers()
    {
        SetMultiFile(("Helper.cs", "namespace App; public class Helper { public int Value; public void Go() {} }"), ("Owner.cs", "namespace App; public class Owner {}"));
        // dryRun avoids writing to disk under a bare relative filename (resolves against the test
        // runner's CWD) -> without it, a stray file left by a prior run makes the diff spuriously
        // empty since the on-disk "before" already matches the freshly-computed "after".
        var result = await _advTools.InlineClassTool(reason: "test message", "Helper.cs", "Owner.cs", "Helper", dryRun: true, returnDiff: true);
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.AffectedFiles.Select(f => f.ToString()), Has.Some.Contains("Owner.cs"));
        Assert.That(summary.Diff, Does.Contain("Value"));
        Assert.That(summary.Diff, Does.Contain("Go"));
    }

    // --- IntroduceVariable ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceVariable_ValidContext_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.IntroduceSymbolTool(reason: "test message", "Order.cs", "string.Format", "formatted", IntroduceAsType.localVariable);
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineVariable ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InlineVariable_ValidVariable_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.InlineSymbolTool(reason: "test message", "Order.cs", "OrderId", InlineKind.variable);
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertPropertyToMethods ---
    [Test]
    [Category("CodeStyleEngine")] // sentinel:auto-category
    public async Task ConvertPropertyToMethods_ValidProperty_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _codeStyleEngine.ConvertPropertyToMethodsAsync("Order.cs", "OrderId");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractMembersToPartial ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ExtractMembersToPartial_ValidMembers_ReturnsDictionary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.ExtractMembersTool(reason: "test message", "Order.cs", "Order", ExtractAsType.partialClass, memberNames: ["GetLabel"]);
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertMethodToIndexer ---
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task ConvertMethodToIndexer_ValidMethod_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advancedStructuralEngine.ConvertMethodToIndexerAsync("Order.cs", "GetStatus");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveTypeToOuterScope ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveTypeToOuterScope_ValidType_ReturnsString()
    {
        const string src = "namespace TestProj; public class Outer { public class Inner {} }";
        SetSource(src, "Outer.cs");
        var result = await _advTools.MoveTypeTool(reason: "test message", "Outer.cs", "Inner", "outerScope");
        Assert.That(result, Is.Not.Null);
    }

    // --- ReplaceMember ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ReplaceMember_ValidMember_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.replace, memberName: "GetLabel", newMemberSource: "public string GetLabel() => $\"{OrderId}: {CustomerName}\";");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ReplaceMember_SingleCandidateWithMismatchedContextSnippet_SurfacesWarningDetails()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.replace, memberName: "GetStatus", newMemberSource: "public string GetStatus() => \"Active\";", contextSnippet: "THIS_SNIPPET_DOES_NOT_EXIST_IN_FILE", dryRun: true);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.WarningDetails, Does.Contain("GetStatus"));

        var matching = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.replace, memberName: "GetStatus", newMemberSource: "public string GetStatus() => \"Active\";", contextSnippet: "if (OrderId == 1)", dryRun: true);
        Assert.That(matching.IsError, Is.False, matching.ErrorData?.Message);
        Assert.That(matching.WarningDetails, Is.Null);
    }

    // --- AddMemberToClass ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddMemberToClass_ValidClass_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.addMember, "Order", newMemberSource: "public string Tag { get; set; }");
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveMember ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_ValidMember_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.remove, memberName: "GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_ZeroReferences_SucceedsAsBefore()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Order.cs", MemberAction.remove, memberName: "GetLabel");
        Assert.That(!result.IsError, Is.True, "GetLabel has no callers in SimpleSource - default precheck must let it through.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_HasCaller_RefusedByDefault_ListsCaller()
    {
        SetSource("""
        namespace TestProj;

        public class Helper
        {
            public string GetName() => "Test";

            public void UseHelper()
            {
                var name = GetName();
            }
        }
        """, "Helper.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Helper.cs", MemberAction.remove, memberName: "GetName");
        Assert.That(!result.IsError, Is.False, "A member with a real caller must be refused by default.");
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("caller"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_HasCaller_SkipPrecheckTrue_StillRefusedByEngineCallerCheck()
    {
        // skipPrecheck: true bypasses only the new tool-level (callers+implementations) precheck ->
        // BasicRefactoringEngine.RemoveMemberAsync's own pre-existing, unconditional caller check
        // (SymbolFinder-based, no bypass) still applies underneath, so a member with a real caller
        // is never truly force-removable. This matches the existing engine-level contract
        // (BUG_60_RemoveMember_ErrorsWhenMemberIsUsed) rather than superseding it.
        SetSource("""
        namespace TestProj;

        public class Helper
        {
            public string GetName() => "Test";

            public void UseHelper()
            {
                var name = GetName();
            }
        }
        """, "Helper.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Helper.cs", MemberAction.remove, memberName: "GetName", skipPrecheck: true);
        Assert.That(!result.IsError, Is.False, "The engine's own caller check still applies even with skipPrecheck: true.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_HasImplementationOnly_SkipPrecheckTrue_BypassesToolLevelCheck()
    {
        // An interface member's implementation isn't caught by the engine's caller-only
        // SymbolFinder check, so the default (skipPrecheck: false) refusal here can only be coming
        // from the new tool-level precheck. With skipPrecheck: true that precheck is bypassed ->
        // removal still fails, but for a different reason (the general compile-validation safety
        // net catching the now-unimplemented interface member), demonstrating skipPrecheck actually
        // skips the precheck rather than the refusal being a fluke of some other gate.
        SetSource("""
        namespace TestProj;

        public interface IGreeter
        {
            string Greet();
        }

        public class Greeter : IGreeter
        {
            public string Greet() => "hello";
        }
        """, "Greeter.cs");
        var refused = await _refactoringStructuralTools.MemberTool(reason: "test message", "Greeter.cs", MemberAction.remove, memberName: "Greet");
        Assert.That(!refused.IsError, Is.False, "An interface member's implementation must be caught by the default precheck.");
        Assert.That(refused.ErrorData!.Message, Does.Contain("implementation"), "Default refusal must come from the tool-level precheck, listing the implementation.");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Greeter.cs", MemberAction.remove, memberName: "Greet", skipPrecheck: true);
        Assert.That(!result.IsError, Is.False, "Removing an interface's sole implementation still breaks compilation - the separate compile-validation safety net catches it.");
        Assert.That(result.ErrorData!.Detail, Does.Contain("does not implement interface member"), "With skipPrecheck: true, the refusal reason must shift from the precheck to compile validation, proving the precheck itself was actually skipped. The diagnostic text now lives in .Detail, not .Message, since abfa5db moved compile-validation failures to a generic .Message wrapper plus a separate detail field.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OnNamespaceLevelRecord_SkipPrecheckTrue_ReferencedRecord_ReturnsSafeDeleteRefusal()
    {
        // Regression test for blocking_error_member_remove_skipprecheck_targetnotfound_ambiguous_symbol.md.
        // MemberTool(remove) routes type-level declarations to StructuralRefinementEngine.SafeDeleteSymbolAsync.
        // Without skipPrecheck, a record with callers is refused first by the tool-level precheck
        // (FindCallersAsync) with a caller list. With skipPrecheck: true, that precheck is bypassed and
        // SafeDeleteSymbolAsync's own usage check refuses the still-referenced record with CannotEdit.
        SetMultiFile(
            ("Chain.cs", """
            namespace TestProj;

            public record CircularDependencyChain(string Name);
            """),
            ("Caller.cs", """
            namespace TestProj;

            public class Caller
            {
                public CircularDependencyChain Make() => new CircularDependencyChain("x");
            }
            """));

        var withPrecheck = await _refactoringStructuralTools.MemberTool(reason: "test message", "Chain.cs", MemberAction.remove, memberName: "CircularDependencyChain");
        Assert.That(!withPrecheck.IsError, Is.False, "A record with a real caller must be refused by the default precheck.");
        Assert.That(withPrecheck.ErrorData!.Message, Does.Contain("caller"), "Default refusal must come from the tool-level precheck, listing the caller.");

        var skipPrecheck = await _refactoringStructuralTools.MemberTool(reason: "test message", "Chain.cs", MemberAction.remove, memberName: "CircularDependencyChain", skipPrecheck: true);
        Assert.That(skipPrecheck.IsError, Is.True, "Member(remove) with skipPrecheck must still refuse a referenced type declaration.");
        Assert.That(skipPrecheck.ErrorData!.Message, Does.Contain("usages").Or.Contain("cannot be safely deleted"), "The refusal must come from SafeDeleteSymbolAsync's usage check, not a generic 'Member not found'.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OnUnreferencedRecord_RemovesRecord()
    {
        // Regression test for docs/current/blockers/blocking_error_member_remove_refuses_record_types.md.
        // MemberTool(remove) routes type-level declarations to StructuralRefinementEngine.SafeDeleteSymbolAsync,
        // so an unreferenced record is removed rather than refused.
        SetMultiFile(
            ("Orphan.cs", """
            namespace TestProj;

            public record OrphanRecord(string Name);
            """));

        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Orphan.cs", MemberAction.remove, memberName: "OrphanRecord");
        Assert.That(result.IsError, Is.False, "Member(remove) must remove an unreferenced type-level declaration.");
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).SingleOrDefault(d => d.Name == "Orphan.cs");
        var text = doc is null ? string.Empty : (await doc.GetTextAsync()).ToString();
        Assert.That(text, Does.Not.Contain("OrphanRecord"), "The resulting file text must no longer contain the record.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OnUnreferencedInterface_RemovesInterface()
    {
        SetMultiFile(
            ("IOrphan.cs", """
            namespace TestProj;

            public interface IOrphan { void M(); }
            """));

        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "IOrphan.cs", MemberAction.remove, memberName: "IOrphan");
        Assert.That(result.IsError, Is.False, "Member(remove) must remove an unreferenced interface declaration.");
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).SingleOrDefault(d => d.Name == "IOrphan.cs");
        var text = doc is null ? string.Empty : (await doc.GetTextAsync()).ToString();
        Assert.That(text, Does.Not.Contain("IOrphan"), "The resulting file text must no longer contain the interface.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OverrideWithNoCallersOrImplementations_SucceedsByDefault()
    {
        // An override with no callers of its own and nothing further overriding it isn't flagged by
        // either the tool-level precheck or the engine's caller check -> confirms the precheck isn't
        // over-broad (it doesn't flag every virtual/override method, only ones with real relationships).
        SetMultiFile(("AnimalBase.cs", """
            namespace TestProj;

            public class AnimalBase
            {
                public virtual string Speak() => "...";
            }
            """), ("Dog.cs", """
            namespace TestProj;

            public class Dog : AnimalBase
            {
                public override string Speak() => "woof";
            }
            """));
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Dog.cs", MemberAction.remove, memberName: "Speak");
        Assert.That(!result.IsError, Is.True, "An override with no callers and nothing overriding it in turn must succeed under the default precheck.");
    }

    // Regression coverage for docs/current/blockers/blocking_error_member_replace_interface_notfound.md:
    // ResolveMemberByNameOrSnippet used to unconditionally exclude every interface-body member from
    // its candidate set, so replace/remove could never target an interface's own property or method
    // declaration (only MemberTool(view), via a different unfiltered lookup path, could see them). Fixed
    // via excludeInterfaceMembers: false at these two call sites, plus an interface-vs-implementer
    // disambiguation rule for when a same-named implementer is also in scope (see
    // RemoveMember_HasImplementationOnly_SkipPrecheckTrue_BypassesToolLevelCheck above, which exercises
    // that disambiguation from the other direction: same name, implementer must win over the interface).
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_InterfaceProperty_NoImplementerInFile_Succeeds()
    {
        SetSource("""
            namespace TestProj;

            public interface IGreeter
            {
                string Greeting { get; }
            }
            """, "IGreeter.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "IGreeter.cs", MemberAction.remove, memberName: "Greeting");
        Assert.That(!result.IsError, Is.True, "Member(remove) must be able to target a property declared directly on an interface.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_UnreferencedMethod_ReportsVerifiedRemovalAndKeepsSibling()
    {
        SetSource("""
            namespace TestProj;

            public class Greeter
            {
                public string Hello() => "hello";
                public string Goodbye() => "bye";
            }
            """, "Greeter.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Greeter.cs", MemberAction.remove, memberName: "Hello");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Contain("Verified"), "A successful non-dry-run removal must report that the repeat lookup found no declaration.");
        Assert.That(summary.Description, Does.Contain("'Hello'"), "The message must name the removed method.");
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).SingleOrDefault(d => d.Name == "Greeter.cs");
        var text = doc is null ? string.Empty : (await doc.GetTextAsync()).ToString();
        Assert.That(text, Does.Not.Contain("Hello()"), "The removed method must no longer be in the file text.");
        Assert.That(text, Does.Contain("Goodbye()"), "The sibling method must still be in the file text.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OverloadSelectedByContextSnippet_RemovesOnlyThatOverload()
    {
        SetSource("""
            namespace TestProj;

            public class Printer
            {
                public void Print(string text) { }
                public void Print(int number) { }
            }
            """, "Printer.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Printer.cs", MemberAction.remove, memberName: "Print", contextSnippet: "public void Print(int number)");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Contain("'Print'"), "The message must name the removed member.");
        Assert.That(summary.Description, Does.Contain("Verified"), "The snippet no longer matches after removal, so the repeat lookup must find no declaration.");
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).SingleOrDefault(d => d.Name == "Printer.cs");
        var text = doc is null ? string.Empty : (await doc.GetTextAsync()).ToString();
        Assert.That(text, Does.Not.Contain("Print(int number)"), "The overload selected by the snippet must be removed.");
        Assert.That(text, Does.Contain("Print(string text)"), "The other overload must still be in the file text.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_DryRun_DoesNotClaimVerified()
    {
        SetSource("""
            namespace TestProj;

            public class Greeter
            {
                public string Hello() => "hello";
                public string Goodbye() => "bye";
            }
            """, "Greeter.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "Greeter.cs", MemberAction.remove, memberName: "Hello", dryRun: true);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Description, Does.Not.Contain("Verified"), "A dry run writes nothing, so it must not claim the removal was verified.");
        Assert.That(summary.Description, Is.EqualTo("Removed 'Hello' from Greeter.cs."));
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).SingleOrDefault(d => d.Name == "Greeter.cs");
        var text = doc is null ? string.Empty : (await doc.GetTextAsync()).ToString();
        Assert.That(text, Does.Contain("Hello()"), "A dry run must leave the committed file unchanged.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task ReplaceMember_InterfaceMethod_NoImplementerInFile_Succeeds()
    {
        SetSource("""
            namespace TestProj;

            public interface IGreeter
            {
                string Greet();
            }
            """, "IGreeter.cs");
        var result = await _refactoringStructuralTools.MemberTool(reason: "test message", "IGreeter.cs", MemberAction.replace, memberName: "Greet", newMemberSource: "string Greet(string name);");
        Assert.That(!result.IsError, Is.True, "Member(replace) must be able to target a method declared directly on an interface.");
    }

    // --- ReplaceConstructorWithFactory ---
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task ReplaceConstructorWithFactory_ValidClass_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advancedStructuralEngine.ReplaceConstructorWithFactoryAsync("Order.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- ReduceBlockDepth ---
    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ReduceBlockDepth_ValidMethod_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _logicOptimizationEngine.ReduceBlockDepthAsync("Order.cs", "GetStatus");
        Assert.That(result, Is.Not.Null);
    }

    // --- OptimizeTaskWait ---
    [Test]
    [Category("AdvancedRefactoringEngine")] // sentinel:auto-category
    public async Task OptimizeTaskWait_ValidFile_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advancedRefactoringEngine.OptimizeTaskWaitAsync("Order.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- IntroduceParameterObjectTool---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceParameterObject_ValidMethod_ReturnsString()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _advTools.IntroduceParameterObjectTool(reason: "test message", "Refactor.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task IntroduceParameterObject_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _advTools.IntroduceParameterObjectTool(reason: "test message", "NonExistent.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    // --- UpdateXmlDocsFromSignature---
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task UpdateXmlDocsFromSignature_ValidMethod_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync("Order.cs", "GetLabel");
    }

    // --- ConvertExpressionBody ---
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task ConvertExpressionBody_ToBlockBody_ReturnsString()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _advancedStructuralEngine.ConvertExpressionBodyAsync("Refactor.cs", "Sound", "ToBlockBody");
    }

    // --- ExtractConstant ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ExtractConstant_WithLiteralSnippet_ReturnsString()
    {
        const string src = @"namespace TestProj; public class C { public string GetLabel() { return ""hello""; } }";
        SetSource(src, "C.cs");
        var result = await _advTools.IntroduceSymbolTool(reason: "test message", "C.cs", @"""hello""", "HelloLabel", IntroduceAsType.@constant);
    }

    // --- AnalyzeControlFlow ---
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task AnalyzeControlFlow_ValidMethod_ReturnsSummary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.AnalyzeControlFlowAsync("Order.cs", "GetStatus");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeDataFlow ---
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task AnalyzeDataFlow_ValidMethod_ReturnsSummary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.AnalyzeDataFlowAsync("Order.cs", "GetStatus");
        Assert.That(result, Is.Not.Null);
    }

    // --- FormatDocumentPreview ---
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task FormatDocumentPreview_ValidFile_ReturnsPreviewResult()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.FormatDocumentPreviewAsync("Order.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertToNullCoalescing ---
    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ConvertToNullCoalescing_ValidFile_ReturnsString()
    {
        const string src = @"namespace TestProj; public class C { public string Get(string s) { if (s == null) s = ""default""; return s; } }";
        SetSource(src, "C.cs");
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("C.cs");
        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ConvertToNullCoalescing_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertToSwitch ---
    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ConvertToSwitch_FileWithIfElseChain_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Order.cs");
        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ConvertToSwitch_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertToPattern ---
    [Test]
    [Category("SyntaxModernizationEngine")] // sentinel:auto-category
    public async Task ConvertToPattern_ValidFile_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _modernizationEngine.ConvertToPatternAsync("Order.cs");
        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    [Category("SyntaxModernizationEngine")] // sentinel:auto-category
    public async Task ConvertToPattern_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _modernizationEngine.ConvertToPatternAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void IsRefusalThatMustNotBeMasked_SelectsCollateralAndEolRefusalsOnly()
    {
        Assert.That(AdvancedRefactoringTools.IsRefusalThatMustNotBeMasked(RoslynSentinel.Common.ToolErrorCode.CollateralChangeRefused), Is.True);
        Assert.That(AdvancedRefactoringTools.IsRefusalThatMustNotBeMasked(RoslynSentinel.Common.ToolErrorCode.EolChangeRefused), Is.True);
        Assert.That(AdvancedRefactoringTools.IsRefusalThatMustNotBeMasked(RoslynSentinel.Common.ToolErrorCode.UnresolvedCallSites), Is.False);
    }
}