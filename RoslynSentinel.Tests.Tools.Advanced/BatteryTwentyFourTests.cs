// Battery #24 -> RefactoringStructuralTools
// Tests all ~65 public methods of RefactoringStructuralTools in-memory via TestSolutionBuilder.
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Support;
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
    private static readonly string RefactorSource = Sources.AnimalHierarchy;
    private static readonly string SimpleSource = Sources.Order;
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

        _refactoringStructuralTools = BatteryToolFactory.CreateRefactoringStructuralTools(_workspaceManager);
        _typedMemberTools = BatteryToolFactory.CreateTypedMemberTools(_workspaceManager);
        _refactoringSignatureTools = BatteryToolFactory.CreateRefactoringSignatureTools(_workspaceManager);
        _refactoringExtractionDocsTools = BatteryToolFactory.CreateRefactoringExtractionDocsTools(_workspaceManager);
        _advTools = BatteryToolFactory.CreateAdvancedRefactoringTools(_workspaceManager);
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

    // Seeds a stale CRLF Order.cs, re-runs Setup (whose wipe removes it), then checks the write succeeds.
    // This proves the Setup wipe, not that the write path tolerates a stale pre-image; WorkingDirectoryLeakGuardTests covers the guard itself.
    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task Setup_WipesStaleCrlfOrderCs_ThenMethodSignatureRemoveStillUpdatesCallSite()
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
        var source = "namespace TestProj;\n\npublic class Order\n{\n    public int Twice(int x)\n    {\n        return x * 2;\n    }\n}\n";
        SetSource(source, "Order.cs");
        var result = await _refactoringStructuralTools.ModifyModifierTool(reason: "test message", "Order.cs", "Twice", NonAccessibilityModifier.@static, AddRemoveAction.add);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Status, Is.EqualTo("applied"));
        Assert.That(summary.AffectedFiles.Select(f => f.ToString()), Does.Contain("Order.cs"));
        var text = await ReadCommittedText("Order.cs");
        Assert.That(text, Does.Contain("public static int Twice(int x)"));
        Assert.That(text, Does.Not.Contain("public int Twice(int x)"));
    }

    // --- ExtensionToStatic ---
    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ExtensionToStatic_ValidMethod_ReturnsString()
    {
        const string src = "namespace TestProj; public static class Helper { public static string Trim(this string s) => s.Trim(); }";
        SetSource(src, "Helper.cs");
        var result = await _logicOptimizationEngine.ExtensionToStaticAsync("Helper.cs", "Trim");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("Trim(string s)"));
    }

    // --- ConvertAbstractToInterface ---
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task ConvertAbstractToInterface_AbstractClass_ReturnsString()
    {
        SetMultiFile(("Refactor.cs", RefactorSource));
        var result = await _advancedStructuralEngine.ConvertAbstractClassToInterfaceAsync("Refactor.cs", "Animal");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("interface IAnimal"));
    }

    // --- ConvertPropertyToMethods ---
    [Test]
    [Category("CodeStyleEngine")] // sentinel:auto-category
    public async Task ConvertPropertyToMethods_ValidProperty_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _codeStyleEngine.ConvertPropertyToMethodsAsync("Order.cs", "OrderId");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("GetOrderId"));
    }

    // --- ConvertMethodToIndexer ---
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task ConvertMethodToIndexer_ValidMethod_ReturnsString()
    {
        SetSource(@"namespace TestProj; public class C { public string Get(int i) { return ""x"" + i; } }", "Order.cs");
        var result = await _advancedStructuralEngine.ConvertMethodToIndexerAsync("Order.cs", "Get");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("this["));
    }

    // --- ReplaceConstructorWithFactory ---
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task ReplaceConstructorWithFactory_ValidClass_ReturnsString()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _advancedStructuralEngine.ReplaceConstructorWithFactoryAsync("Order.cs", "Order");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public static Order Create("));
        Assert.That(result.UpdatedText, Does.Contain("private Order("));
    }

    // --- ReduceBlockDepth ---
    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ReduceBlockDepth_ValidMethod_ReturnsString()
    {
        const string src = @"namespace TestProj; public class C { public string GetStatus(int a) { if (a > 0) { if (a > 1) { if (a > 2) { return ""x""; } } } return ""y""; } }";
        SetSource(src, "Order.cs");
        var result = await _logicOptimizationEngine.ReduceBlockDepthAsync("Order.cs", "GetStatus");
        // TODO fixture does not trigger ReduceBlockDepth: nested-if method returns CannotOptimize; replace with a fixture the engine reduces, then assert Modified
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.CannotOptimize));
        // TODO pin UpdatedText for ReduceBlockDepth once a reducible fixture is found (CannotOptimize leaves no text change to assert)
    }

    // --- OptimizeTaskWait ---
    [Test]
    [Category("AdvancedRefactoringEngine")] // sentinel:auto-category
    public async Task OptimizeTaskWait_ValidFile_ReturnsString()
    {
        SetSource(@"namespace TestProj; public class C { public void M() { System.Threading.Tasks.Task.Delay(1).Wait(); } }", "Order.cs");
        var result = await _advancedRefactoringEngine.OptimizeTaskWaitAsync("Order.cs");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("await"));
    }

    // --- UpdateXmlDocsFromSignature---
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task UpdateXmlDocsFromSignature_ValidMethod_ReturnsString()
    {
        SetSource(@"namespace TestProj; public class C { public string Get(int a, string b) { return b + a; } }", "C.cs");
        var result = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync("C.cs", "Get");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("<param"));
    }

    // --- ConvertExpressionBody ---
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task ConvertExpressionBody_ToBlockBody_ReturnsString()
    {
        SetSource(@"namespace TestProj; public class C { public string Sound() => ""woof""; }", "Refactor.cs");
        var result = await _advancedStructuralEngine.ConvertExpressionBodyAsync("Refactor.cs", "Sound", "ToBlockBody");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("{"));
    }

    // --- ExtractConstant ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ExtractConstant_WithLiteralSnippet_ReturnsSentinelResult()
    {
        const string src = @"namespace TestProj; public class C { public string GetLabel() { return ""hello""; } }";
        SetSource(src, "C.cs");
        var result = await _advTools.IntroduceSymbolTool(reason: "test message", "C.cs", @"""hello""", "HelloLabel", IntroduceAsType.@constant, returnDiff: true);
        // TODO ExtractConstant via IntroduceSymbolTool fails on an in-memory document - assert the applied change once it works
        Assert.That(result.Code, Is.EqualTo("Exception"));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Message, Does.StartWith("Could not read file 'C.cs'"));
    }

    // --- AnalyzeControlFlow ---
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task AnalyzeControlFlow_ValidMethod_ReturnsSummary()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _refactoringEngine.AnalyzeControlFlowAsync("Order.cs", "GetStatus");
        Assert.That(result.MethodName, Is.EqualTo("GetStatus"));
        Assert.That(result.AlwaysReturns, Is.True);
        Assert.That(result.NeverReturns, Is.False);
        Assert.That(result.ReturnPoints, Has.Count.EqualTo(3));
        Assert.That(result.ThrowPoints, Is.Empty);
    }

    // --- AnalyzeDataFlow ---
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task AnalyzeDataFlow_ValidMethod_ReturnsSummary()
    {
        var source = "namespace TestProj;\n\npublic class Order\n{\n    public int Sum(int a)\n    {\n        var total = a;\n        total += 1;\n        return total;\n    }\n}\n";
        SetSource(source, "Order.cs");
        var result = await _refactoringEngine.AnalyzeDataFlowAsync("Order.cs", "Sum");
        Assert.That(result.MethodName, Is.EqualTo("Sum"));
        Assert.That(result.WrittenInside, Does.Contain("total"));
        Assert.That(result.ReadInside, Does.Contain("total"));
        Assert.That(result.WrittenInside, Does.Not.Contain("a"));
    }

    // --- FormatDocumentPreview ---
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task FormatDocumentPreview_ValidFile_ReturnsPreviewResult()
    {
        SetSource("namespace TestProj;\n\npublic class Order{public int X(){return 1;}}\n", "Order.cs");
        var result = await _refactoringEngine.FormatDocumentPreviewAsync("Order.cs");
        Assert.That(result.Changed, Is.True);
        Assert.That(result.TotalHunks, Is.GreaterThan(0));
        Assert.That(result.Hunks, Has.Count.EqualTo(result.TotalHunks));
    }

    // --- ConvertToNullCoalescing ---
    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ConvertToNullCoalescing_ValidFile_ReturnsString()
    {
        const string src = @"namespace TestProj; public class C { public string Get(string s) { if (s == null) s = ""default""; return s; } }";
        SetSource(src, "C.cs");
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("C.cs");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("??="));
    }

    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ConvertToNullCoalescing_NonExistentFile_ReportsNotFound()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _logicOptimizationEngine.ConvertToNullCoalescingAsync("NonExistent.cs");
        // TODO throw-migration: assert the typed exception once LogicSimplificationEngine throws
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.DocumentNotFound));
        // TODO message defect: DocumentNotFound message is the comment-style "// Document not found." with no file name; pin the real wording once fixed
        Assert.That(result.Message, Is.EqualTo("// Document not found."));
    }

    // --- ConvertToSwitch ---
    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ConvertToSwitch_FileWithIfElseChain_ReturnsString()
    {
        SetSource(@"namespace TestProj; public class C { public string Get(int x) { if (x == 1) return ""a""; else if (x == 2) return ""b""; return ""c""; } }", "Order.cs");
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("Order.cs");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.AlreadyInTargetState));
        Assert.That(result.UpdatedText, Is.Null.Or.Empty);
        
    }

    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ConvertToSwitch_NonExistentFile_ReportsNotFound()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _logicOptimizationEngine.ConvertToSwitchAsync("NonExistent.cs");
        // TODO throw-migration: assert the typed exception once LogicSimplificationEngine throws
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.DocumentNotFound));
        // TODO message defect: DocumentNotFound message is the comment-style "// Document not found." with no file name; pin the real wording once fixed
        Assert.That(result.Message, Is.EqualTo("// Document not found."));
    }

    // --- ConvertToPattern ---
    [Test]
    [Category("SyntaxModernizationEngine")] // sentinel:auto-category
    public async Task ConvertToPattern_ValidFile_ReturnsString()
    {
        SetSource(@"namespace TestProj; public class C { public bool Check(object o) { var s = o as string; return s != null && s.Length > 0; } }", "Order.cs");
        var result = await _modernizationEngine.ConvertToPatternAsync("Order.cs");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        // TODO no-op returns Modified - assert NoChange once the engine distinguishes it (as-cast null check is not rewritten to an is-pattern)
        Assert.That(result.UpdatedText, Does.Contain("o as string"));
    }

    [Test]
    [Category("SyntaxModernizationEngine")] // sentinel:auto-category
    public async Task ConvertToPattern_NonExistentFile_ReportsNotFound()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _modernizationEngine.ConvertToPatternAsync("NonExistent.cs");
        // TODO throw-migration: assert the typed exception once SyntaxModernizationEngine throws
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.DocumentNotFound));
        // TODO message defect: DocumentNotFound message is the comment-style "// Document not found." with no file name; pin the real wording once fixed
        Assert.That(result.Message, Is.EqualTo("// Document not found."));
    }

    [Test]
    public void IsRefusalThatMustNotBeMasked_SelectsCollateralAndEolRefusalsOnly()
    {
        Assert.That(AdvancedRefactoringTools.IsRefusalThatMustNotBeMasked(RoslynSentinel.Common.ToolErrorCode.CollateralChangeRefused), Is.True);
        Assert.That(AdvancedRefactoringTools.IsRefusalThatMustNotBeMasked(RoslynSentinel.Common.ToolErrorCode.EolChangeRefused), Is.True);
        Assert.That(AdvancedRefactoringTools.IsRefusalThatMustNotBeMasked(RoslynSentinel.Common.ToolErrorCode.UnresolvedCallSites), Is.False);
    }
}