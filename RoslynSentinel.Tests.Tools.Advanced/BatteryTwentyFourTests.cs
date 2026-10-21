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

    // --- MoveMemberTool (pull-up to existing base class) ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveMember_ToBaseClass_AutoStageTrue_ReturnsNotNull()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _advTools.MoveMemberTool(reason: "test message", "Refactor.cs", "Dog", ["Sound"], "Animal");
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

    // --- ConvertPropertyToMethods ---
    [Test]
    [Category("CodeStyleEngine")] // sentinel:auto-category
    public async Task ConvertPropertyToMethods_ValidProperty_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _codeStyleEngine.ConvertPropertyToMethodsAsync("Order.cs", "OrderId");
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