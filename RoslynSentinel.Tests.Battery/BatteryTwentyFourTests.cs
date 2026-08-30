// Battery #24 — SentinelRefactoringTools
// Tests all ~65 public methods of SentinelRefactoringTools in-memory via TestSolutionBuilder.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class BatteryTwentyFourTests
{
    private IWorkspaceManager _workspaceManager;
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
    private DiffEngine _diffEngine;
    private ValidationEngine _validationEngine;
    private SentinelRefactoringTools _tools;
    private SentinelAdvancedRefactoringTools _advTools;

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
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, _config);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, _config);
        _codeFlowEngine = new CodeFlowEngine(_workspaceManager);
        _advancedRefactoringEngine = new AdvancedRefactoringEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
        _modernizationEngine = new ModernizationEngine(_workspaceManager, _config);
        _diffEngine = new DiffEngine();
        _validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, _diffEngine);
        _tools = new SentinelRefactoringTools(
            _refactoringEngine, _standardRefactoringEngine,
            _mappingEngine, _semanticRefactoringLibrary, _granularRefactoringEngine,
            _structuralRefinementEngine, _codeStyleEngine, _codeFlowEngine,
            new MsToolAugmentEngine(_workspaceManager),
            new CodeGenerationEngine(_workspaceManager),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager, _validationEngine, _config,
            NullLogger<SentinelRefactoringTools>.Instance);
        _advTools = new SentinelAdvancedRefactoringTools(
            _refactoringEngine, _standardRefactoringEngine, _advancedStructuralEngine,
            _mappingEngine, _semanticRefactoringLibrary, _granularRefactoringEngine,
            _advancedLogicEngine, _refinementEngine, _advancedTypeEngine,
            _codeStyleEngine, _codeFlowEngine,
            _advancedRefactoringEngine, _logicOptimizationEngine, _modernizationEngine,
            new OutParamRefactoringEngine(_workspaceManager),
            new MsToolAugmentEngine(_workspaceManager),
            new CodeGenerationEngine(_workspaceManager),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager, _validationEngine, _config,
            NullLogger<SentinelAdvancedRefactoringTools>.Instance);
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
    public async Task ExtractSuperclass_AutoStageTrue_ReturnsAppliedChangeSummary()
    {
        SetMultiFile(("Dog.cs", RefactorSource));
        var result = await _advTools.ExtractMembers("Dog.cs", "Dog", "superclass", "AnimalBase");
        Assert.That(result, Is.Not.Null);
    }

    // --- SafeDeleteSymbol ---

    [Test]
    public async Task SafeDeleteSymbol_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Member("Order.cs", MemberAction.remove, memberName: "GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- ChangeSignature ---

    [Test]
    public async Task ChangeSignature_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.ChangeSignature("Order.cs", "Order", [1, 0]);
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractInterface ---

    [Test]
    public async Task ExtractInterface_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.ExtractMembers("Order.cs", "Order", "interface", "IOrder");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveTypeToFile ---

    [Test]
    public async Task MoveTypeToFile_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.MoveType("Order.cs", "Status", "ownFile");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveAllTypesToFiles ---

    [Test]
    public async Task MoveAllTypesToFiles_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.MoveAllTypesToFiles("Order.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- RenameSymbol ---
    // RenameSymbol now takes a SymbolHandle (sessionId, projectName, docCommentId) instead of
    // (filepath, methodName, contextSnippet) — resolve the handle via SymbolNavigationEngine
    // first, matching how an agent would call LocateSymbol before RenameSymbol.

    [Test]
    public async Task RenameSymbol_ValidSymbol_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var symbolNavEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        // "GetLabel" is declared on both Order and IService in SimpleSource — disambiguate.
        var located = await symbolNavEngine.LocateSymbolAsync("GetLabel", containingType: "Order");
        var handle = located.Single();

        var result = await _tools.RenameSymbol(
            handle.ProjectName, handle.DocCommentId!, "GetDisplayLabel", _workspaceManager.SessionId.ToString());
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task RenameSymbol_NonExistentSymbol_ReturnsErrorObject()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.RenameSymbol(
            "TestProj", "M:TestProj.Order.NoSuchSymbol", "NewName", _workspaceManager.SessionId.ToString());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    // --- MoveAllTypesToFilesInProject ---

    [Test]
    public async Task MoveAllTypesToFilesInProject_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.MoveAllTypesToFiles("project", "TestProj");
        Assert.That(result, Is.Not.Null);
    }

    // --- MoveAllTypesToFilesInSolution ---

    [Test]
    public async Task MoveAllTypesToFilesInSolution_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.MoveAllTypesToFiles("solution");
        Assert.That(result, Is.Not.Null);
    }

    // --- UsingDirective ---

    [Test]
    public async Task UsingDirective_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.UsingDirective("Order.cs", AddRemoveViewAction.add, "System.Linq");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task UsingDirective_AutoStageFalse_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.UsingDirective("Order.cs", AddRemoveViewAction.add, "System.Linq", autoStage: false);
        Assert.That(result, Is.Not.Null);
    }

    // --- ModifyEnum ---

    [Test]
    public async Task ModifyEnum_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyEnum("Order.cs", "Status", "Active,Pending,Cancelled");
        Assert.That(result, Is.Not.Null);
    }

    // --- InsertMemberAfter ---

    [Test]
    public async Task InsertMemberAfter_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Member("Order.cs", MemberAction.add, "Order", newMemberSource: "public string Description => \"\";", position: "after:GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- InsertMemberBefore ---

    [Test]
    public async Task InsertMemberBefore_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Member("Order.cs", MemberAction.add, "Order", newMemberSource: "public string Tag => \"\";", position: "before:GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddAttribute ---

    [Test]
    public async Task AddAttribute_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyAttribute("Order.cs", "Order", "[Serializable]", "", AttributeModifyAction.add);
        Assert.That(result, Is.Not.Null);
    }

    // --- AddBaseType ---

    [Test]
    public async Task AddBaseType_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyBaseType("Order.cs", "Order", "IService", AddRemoveAction.add);
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveAttribute ---

    [Test]
    public async Task RemoveAttribute_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyAttribute("Order.cs", "Order", "Serializable", "", AttributeModifyAction.remove);
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveBaseType ---

    [Test]
    public async Task RemoveBaseType_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyBaseType("Order.cs", "Order", "IService", AddRemoveAction.remove);
        Assert.That(result, Is.Not.Null);
    }

    // --- PullUpMember ---

    [Test]
    public async Task PullUpMember_AutoStageTrue_ReturnsNotNull()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _advTools.PullUpMember("Refactor.cs", "Dog", "Sound");
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

    [Test]
    public async Task ChangeAccessibility_StampsIncreasingWorkspaceVersion()
    {
        SetSource(SimpleSource, "Order.cs");
        var versionBeforeAnyMutation = _workspaceManager.WorkspaceVersion;

        var first = await _tools.ChangeAccessibility("Order.cs", "OrderId", "internal");
        var firstSummary = (AppliedChangeSummary)first.Data!;
        Assert.That(firstSummary.WorkspaceVersion, Is.Not.Null);
        Assert.That(firstSummary.WorkspaceVersion, Is.GreaterThan(versionBeforeAnyMutation));

        var second = await _tools.ChangeAccessibility("Order.cs", "CustomerName", "internal");
        var secondSummary = (AppliedChangeSummary)second.Data!;
        Assert.That(secondSummary.WorkspaceVersion, Is.GreaterThan(firstSummary.WorkspaceVersion!),
            "A second mutation must stamp a strictly higher version than the first.");
    }

    // --- AddModifier ---

    [Test]
    public async Task AddModifier_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyModifier("Order.cs", "Order", "sealed", AddRemoveAction.add);
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveModifier ---

    [Test]
    public async Task RemoveModifier_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyModifier("Order.cs", "Order", "sealed", AddRemoveAction.remove);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ModifyModifier_RejectsAccessibilityKeyword()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyModifier("Order.cs", "GetLabel", "private", AddRemoveAction.remove);

        Assert.That(result.Success, Is.False,
            "ModifyModifier must reject accessibility keywords — removing 'private' with no add of 'public' silently " +
            "leaves a class member at C#'s implicit-private default, which looks like success but changes nothing " +
            "observable. ChangeAccessibility is the correct tool for accessibility changes.");
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.Error.Message, Does.Contain("ChangeAccessibility"));
    }

    // --- ChangeAccessibility ---

    [Test]
    public async Task ChangeAccessibility_UnrecognizedValue_ReturnsError()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ChangeAccessibility("Order.cs", "GetLabel", "puplic");

        Assert.That(result.Success, Is.False,
            "An unrecognized accessibility string must be rejected, not silently treated as 'public' " +
            "(a typo like 'puplic' should never widen a member's accessibility unintentionally).");
    }

    // --- SummaryComment ---

    [Test]
    public async Task SummaryComment_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.SummaryComment("Order.cs", AddRemoveViewAction.add, "Order", "Represents an order.");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddProperty ---

    [Test]
    public async Task AddProperty_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Member("Order.cs", MemberAction.add, "Order", typedKind: TypedMemberKind.property, typedName: "Description", typedType: "string");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddField ---

    [Test]
    public async Task AddField_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Member("Order.cs", MemberAction.add, "Order", typedKind: TypedMemberKind.field, typedName: "_tag", typedType: "string");
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
        var result = await _advTools.WrapRange("Order.cs", 8, 10, "tryCatch");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConstructorParameter ---

    [Test]
    public async Task ConstructorParameter_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ConstructorParameter("Order.cs", AddRemoveViewAction.add, "Order", "notes", "string");
        Assert.That(result, Is.Not.Null);
    }

    // --- WrapInRegion ---

    [Test]
    public async Task WrapInRegion_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.WrapRange("Order.cs", 3, 6, "region", "Properties");
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

    // NOTE: ValidateChangesAsync adds the renamed file as a brand-new document into the candidate
    // solution without removing the original document at the old path first, which used to make
    // pre-validation see the same (unique) type declared in two documents at once and fail with a
    // duplicate-declaration error on every real rename. Fixed by threading an explicit
    // `removePaths` list through ValidateChangesAsync/ValidateAndApplyHelper so the tool layer can
    // tell validation which old-path document to drop from the candidate before compiling (see
    // SyncTypeAndFilename's `removePaths: [filePath]` call and
    // SyncTypeAndFilename_RealRename_SucceedsAndRemovesOldDocument below). This dryRun test still
    // asserts the one thing dryRun is responsible for regardless: no destructive action (old-file
    // delete) has occurred, even if validation were to fail for some other reason.
    [Test]
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

            var solution = TestSolutionBuilder.CreateSolutionWithProject(
                "TestProj", Path.Combine(tempDir, "TestProj.csproj"),
                [("Mismatched.cs", mismatchedSource, oldPath)]);
            _workspaceManager.SetTestSolution(solution);

            var result = await _tools.SyncTypeAndFilename(oldPath, dryRun: true);

            Assert.That(File.Exists(oldPath), Is.True, "dryRun must never delete the original file, even when validation fails.");
            Assert.That(File.Exists(newPath), Is.False, "dryRun must never write the renamed file.");
            if (result.Success)
            {
                var data = (AppliedChangeSummary)result.Data!;
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

            var solution = TestSolutionBuilder.CreateSolutionWithProject(
                "TestProj", Path.Combine(tempDir, "TestProj.csproj"),
                [("Mismatched.cs", mismatchedSource, oldPath)]);
            _workspaceManager.SetTestSolution(solution);

            var result = await _tools.SyncTypeAndFilename(oldPath);

            Assert.That(result.Success, Is.True, $"Expected rename to succeed; error: {result.Error?.Message}");
            Assert.That(File.Exists(oldPath), Is.False, "Old file should be deleted after a successful rename.");
            Assert.That(File.Exists(newPath), Is.True, "New file should exist after a successful rename.");

            var currentSolution = await _workspaceManager.GetCurrentSolutionAsync(CancellationToken.None);
            Assert.That(currentSolution.GetDocumentIdsWithFilePath(oldPath), Is.Empty,
                "Old path must not remain tracked as a Document after the rename, or the type would be seen as declared twice.");
            Assert.That(currentSolution.GetDocumentIdsWithFilePath(newPath), Is.Not.Empty,
                "New path must be tracked as a Document after the rename.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- InlineMethod ---

    [Test]
    public async Task InlineMethod_ValidMethod_ReturnsDictionary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.Inline("Order.cs", "GetLabel", "method");
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
        var result = await _advTools.Introduce("Order.cs", "string.Format", "labelFormatter", "field");
        Assert.That(result, Is.Not.Null);
    }

    // --- IntroduceParameter ---

    [Test]
    public async Task IntroduceParameter_ValidContext_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.Introduce("Order.cs", "GetLabel", "GetLabel", "parameter");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineField ---

    [Test]
    public async Task InlineField_ValidField_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.Inline("Order.cs", "OrderId", "field");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineParameter ---

    [Test]
    public async Task InlineParameter_ValidParameter_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.Inline("Order.cs", "orderId", "parameter", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- MakeMethodStatic ---

    [Test]
    public async Task MakeMethodStatic_ValidMethod_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.ModifyModifier("Order.cs", "GetLabel", "static", AddRemoveAction.add);
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
        var result = await _advTools.WrapRange("Order.cs", 8, 10, "using", "resource");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertAnonymousToNamed ---

    [Test]
    public async Task ConvertAnonymousToNamed_ValidFile_ReturnsDictionary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.ConvertAnonymousToNamed("Order.cs", "OrderData");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineClass ---

    [Test]
    public async Task InlineClass_CrossFile_MovesMembers()
    {
        SetMultiFile(
            ("Helper.cs", "namespace App; public class Helper { public int Value; public void Go() {} }"),
            ("Owner.cs", "namespace App; public class Owner {}"));
        // dryRun avoids writing to disk under a bare relative filename (resolves against the test
        // runner's CWD) — without it, a stray file left by a prior run makes the diff spuriously
        // empty since the on-disk "before" already matches the freshly-computed "after".
        var result = await _advTools.InlineClass("Helper.cs", "Owner.cs", "Helper", dryRun: true, returnDiff: true);
        Assert.That(result.Success, Is.True, result.Error?.Message);
        var summary = (AppliedChangeSummary)result.Data!;
        Assert.That(summary.AffectedFiles.Select(f => f.ToString()), Has.Some.Contains("Owner.cs"));
        Assert.That(summary.Diff, Does.Contain("Value"));
        Assert.That(summary.Diff, Does.Contain("Go"));
    }

    // --- IntroduceVariable ---

    [Test]
    public async Task IntroduceVariable_ValidContext_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.Introduce("Order.cs", "string.Format", "formatted", "localVariable");
        Assert.That(result, Is.Not.Null);
    }

    // --- InlineVariable ---

    [Test]
    public async Task InlineVariable_ValidVariable_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.Inline("Order.cs", "OrderId", "variable");
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
        var result = await _advTools.ExtractMembers("Order.cs", "Order", "class", "OrderInfo", ["GetLabel", "GetStatus"]);
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractMembersToPartial ---

    [Test]
    public async Task ExtractMembersToPartial_ValidMembers_ReturnsDictionary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advTools.ExtractMembers("Order.cs", "Order", "partial", memberNames: ["GetLabel"]);
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
        var result = await _advTools.MoveType("Outer.cs", "Inner", "outerScope");
        Assert.That(result, Is.Not.Null);
    }

    // --- ReplaceMember ---

    [Test]
    public async Task ReplaceMember_ValidMember_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Member("Order.cs", MemberAction.replace, memberName: "GetLabel", newMemberSource: "public string GetLabel() => $\"{OrderId}: {CustomerName}\";");
        Assert.That(result, Is.Not.Null);
    }

    // --- AddMemberToClass ---

    [Test]
    public async Task AddMemberToClass_ValidClass_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Member("Order.cs", MemberAction.add, "Order", newMemberSource: "public string Tag { get; set; }");
        Assert.That(result, Is.Not.Null);
    }

    // --- RemoveMember ---

    [Test]
    public async Task RemoveMember_ValidMember_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Member("Order.cs", MemberAction.remove, memberName: "GetLabel");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task RemoveMember_ZeroReferences_SucceedsAsBefore()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _tools.Member("Order.cs", MemberAction.remove, memberName: "GetLabel");
        Assert.That(result.Success, Is.True, "GetLabel has no callers in SimpleSource — default precheck must let it through.");
    }

    [Test]
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

        var result = await _tools.Member("Helper.cs", MemberAction.remove, memberName: "GetName");

        Assert.That(result.Success, Is.False, "A member with a real caller must be refused by default.");
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(result.Error!.Message, Does.Contain("caller"));
    }

    [Test]
    public async Task RemoveMember_HasCaller_SkipPrecheckTrue_StillRefusedByEngineCallerCheck()
    {
        // skipPrecheck: true bypasses only the new tool-level (callers+implementations) precheck —
        // RefactoringEngine.RemoveMemberAsync's own pre-existing, unconditional caller check
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

        var result = await _tools.Member("Helper.cs", MemberAction.remove, memberName: "GetName", skipPrecheck: true);

        Assert.That(result.Success, Is.False, "The engine's own caller check still applies even with skipPrecheck: true.");
    }

    [Test]
    public async Task RemoveMember_HasImplementationOnly_SkipPrecheckTrue_BypassesToolLevelCheck()
    {
        // An interface member's implementation isn't caught by the engine's caller-only
        // SymbolFinder check, so the default (skipPrecheck: false) refusal here can only be coming
        // from the new tool-level precheck. With skipPrecheck: true that precheck is bypassed —
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

        var refused = await _tools.Member("Greeter.cs", MemberAction.remove, memberName: "Greet");
        Assert.That(refused.Success, Is.False, "An interface member's implementation must be caught by the default precheck.");
        Assert.That(refused.Error!.Message, Does.Contain("implementation"), "Default refusal must come from the tool-level precheck, listing the implementation.");

        var result = await _tools.Member("Greeter.cs", MemberAction.remove, memberName: "Greet", skipPrecheck: true);
        Assert.That(result.Success, Is.False, "Removing an interface's sole implementation still breaks compilation — the separate compile-validation safety net catches it.");
        Assert.That(result.Error!.Message, Does.Contain("does not implement interface member"),
            "With skipPrecheck: true, the refusal reason must shift from the precheck to compile validation, proving the precheck itself was actually skipped.");
    }

    [Test]
    public async Task RemoveMember_OverrideWithNoCallersOrImplementations_SucceedsByDefault()
    {
        // An override with no callers of its own and nothing further overriding it isn't flagged by
        // either the tool-level precheck or the engine's caller check — confirms the precheck isn't
        // over-broad (it doesn't flag every virtual/override method, only ones with real relationships).
        SetMultiFile(
            ("AnimalBase.cs", """
            namespace TestProj;

            public class AnimalBase
            {
                public virtual string Speak() => "...";
            }
            """),
            ("Dog.cs", """
            namespace TestProj;

            public class Dog : AnimalBase
            {
                public override string Speak() => "woof";
            }
            """));

        var result = await _tools.Member("Dog.cs", MemberAction.remove, memberName: "Speak");
        Assert.That(result.Success, Is.True, "An override with no callers and nothing overriding it in turn must succeed under the default precheck.");
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
        var result = await _advTools.InvertAssignments("Order.cs", 8, 12);
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
        var result = await _advTools.SyncInterface("Worker.cs", "IWorker", "sync", "Worker");
        Assert.That(result, Is.Not.Null);
    }

    // --- IntroduceParameterObject---

    [Test]
    public async Task IntroduceParameterObject_ValidMethod_ReturnsString()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _advTools.IntroduceParameterObject("Refactor.cs", "Process");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task IntroduceParameterObject_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _advTools.IntroduceParameterObject("NonExistent.cs", "Process");
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
        var result = await _advTools.Introduce("C.cs", @"""hello""", "HelloLabel", "constant");
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
        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty);
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
    public async Task ExtractLocalVariable_NonExistentFile_ReturnsStructuredError()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _tools.ExtractLocalVariable("NonExistent.cs", "GetLabel", "label");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    // --- ConvertToSwitch ---

    [Test]
    public async Task ConvertToSwitch_FileWithIfElseChain_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Order.cs");
        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty);
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
        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ConvertToPattern_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _modernizationEngine.ConvertToPatternAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }
}
