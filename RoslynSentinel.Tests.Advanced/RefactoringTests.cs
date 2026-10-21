using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Support;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

public class RefactoringTests
{
    private IWorkspaceManager _workspaceManager;
    private BasicRefactoringEngine _refactoringEngine;
    private LogicSimplificationEngine _advancedLogicEngine;
    private CodeHealingEngine _healingEngine;

    private static readonly string RichSource = Sources.RichOrderService;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _refactoringEngine = new BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        _advancedLogicEngine = new LogicSimplificationEngine(_workspaceManager);
        _healingEngine = new CodeHealingEngine(_workspaceManager, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Solution CreateSolution(string source, string fileName = "Test.cs")
    {
        var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(projectId, "TestProj", "TestProj", LanguageNames.CSharp);
        var docId = DocumentId.CreateNewId(projectId);
        return solution.AddDocument(docId, fileName, SourceText.From(source), filePath: fileName);
    }

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task MoveTypeToFile_Should_ExtractClass_And_RemoveFromOriginal()
    {
        using var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(projectId, "TestProj", "TestProj", LanguageNames.CSharp);
        var filePath = "E:\\source\\repos\\Mixed.cs";
        var sourceCode = "namespace MyNamespace; public class ClassOne {} public class ClassTwo {}";
        var docId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(docId, "Mixed.cs", SourceText.From(sourceCode), filePath: filePath);
        _workspaceManager.SetTestSolution(solution);
        var results = await _refactoringEngine.MoveTypeToFileAsync(filePath, "ClassTwo");
        Assert.That(results.Count, Is.EqualTo(2));
        Assert.That(results[filePath], Does.Not.Contain("class ClassTwo"));
        Assert.That(results["E:\\source\\repos\\ClassTwo.cs"], Contains.Substring("class ClassTwo"));
    }

    //[Ignore("API changed: RenameSymbolAsync now requires SymbolHandle and ISymbol")]
    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("RenameSymbolResult")] // sentinel:auto-category
    [Category("SymbolLocation")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task RenameSymbol_Should_UpdateAllReferences()
    {
        //Assert.Ignore("API changed: RenameSymbolAsync now requires SymbolHandle and ISymbol");
        _workspaceManager.SetTestSolution(CreateSolution(RichSource, "Order.cs"));
        var navigationEngine = new SymbolNavigationEngine(_workspaceManager);
        var candidates = await navigationEngine.LocateSymbolAsync("OrderId", "All", "Order");
        var symbol = candidates.FirstOrDefault();
        Assert.That(symbol?.DocCommentId, Is.Not.Null, "Symbol must have a DocCommentId for resolution");
        var resolved = await _workspaceManager.ResolveFromWireAsync("TestProj", symbol.DocCommentId, CancellationToken.None);
        Assert.That(resolved.Symbol, Is.Not.Null, "Resolved symbol must not be null");
        var result = await _refactoringEngine.RenameSymbolAsync(resolved.Handle, resolved.Symbol, "OrderId2");

        var docText = result.PendingChanges.FirstOrDefault().Value;

        Assert.That(docText, Contains.Substring("OrderId2"));
        Assert.That(result.ResidualMentions?.Count == 0);
    }

    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task InlineMethod_Should_ReplaceCallSites_With_Expression()
    {
        var source = "public class C { public int GetTen() { return 10; } public void M() { var x = GetTen(); } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await new StructuralRefactoringEngine(_workspaceManager).InlineMethodAsync("C.cs", "GetTen");
        var updatedContent = result.Values.FirstOrDefault(v => !v.StartsWith("// ErrorDetails:")) ?? "";
        Assert.That(updatedContent, Contains.Substring("var x = 10;"));
        Assert.That(updatedContent, Does.Not.Contain("GetTen()"));
    }

    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ExtensionToStatic_Should_Remove_This_Modifier()
    {
        var source = "public static class Ext { public static void M(this string s) { } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Ext.cs"));
        var result = await new LogicSimplificationEngine(_workspaceManager).ExtensionToStaticAsync("Ext.cs", "M");
        Assert.That(result.UpdatedText!, Contains.Substring("public static void M(string s)"));
        Assert.That(result.UpdatedText!, Does.Not.Contain("this string"));
    }

    [Test]
    [Category("LogicSimplificationEngine")] // sentinel:auto-category
    public async Task ConvertStaticToExtension_Should_Add_This_Modifier()
    {
        var source = "public static class Ext { public static void M(string s) { } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Ext.cs"));
        var result = await new LogicSimplificationEngine(_workspaceManager).ConvertStaticToExtensionAsync("Ext.cs", "M");
        Assert.That(result.UpdatedText!, Contains.Substring("public static void M(this string s)"));
    }

    [Test]
    [Category("LogicSimplificationEngine")]
    [TestCase("public partial class Ext { public static void M(string s) { } }", "public static partial class Ext")]
    [TestCase("partial class Ext { public static void M(string s) { } }", "static partial class Ext")]
    [TestCase("public static partial class Ext { public static void M(string s) { } }", "public static partial class Ext")]
    public async Task ConvertStaticToExtension_PartialClass_PutsStaticBeforePartial_NoCS0267(string source, string expectedDeclaration)
    {
        _workspaceManager.SetTestSolution(CreateSolution(source, "Ext.cs"));
        var result = await new LogicSimplificationEngine(_workspaceManager).ConvertStaticToExtensionAsync("Ext.cs", "M");
        Assert.That(result.UpdatedText!, Does.Contain(expectedDeclaration));
        Assert.That(result.UpdatedText!, Does.Not.Contain("partial static"));
        var syntaxErrors = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result.UpdatedText!)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id)
            .ToList();
        Assert.That(syntaxErrors, Does.Not.Contain("CS0267"));
        Assert.That(syntaxErrors, Is.Empty);
    }

    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task InlineField_Should_Replace_Field_With_Value()
    {
        var source = "public class C { private const int X = 42; public int M() => X; }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await new StructuralRefactoringEngine(_workspaceManager).InlineFieldAsync("C.cs", "X");
        Assert.That(result.UpdatedText!, Contains.Substring("=> 42;"));
        Assert.That(result.UpdatedText!, Does.Not.Contain("const int X"));
    }

    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task ExtractMembersToPartial_Should_Split_Class()
    {
        var source = "public class C { public void M1() {} public void M2() {} }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var result = await new StructuralRefactoringEngine(_workspaceManager).ExtractMembersToPartialAsync("C.cs", "C", new[] { "M2" });
        Assert.That(result.Count, Is.EqualTo(1));
        var partialCode = result.Values.First();
        Assert.That(partialCode, Contains.Substring("partial class C"));
        Assert.That(partialCode, Contains.Substring("void M2()"));
    }
}