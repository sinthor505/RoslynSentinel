using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("AdvancedRefactoringTools")] // sentinel:auto-category
public class MassiveRefactoringTests
{
    private IWorkspaceManager _workspaceManager;
    private BasicRefactoringEngine _basicRefactoringEngine;
    private AdvancedRefactoringEngine _advancedRefactoringEngine;
    private MemberRefactoringEngine _memberRefactoringEngine;
    private RefactoringStructuralTools _refactoringStructuralTools;
    private RefactoringSignatureTools _refactoringSignatureTools;
    private AdvancedRefactoringTools _advancedRefactoringTools;
    private StructuralRefactoringEngine _structuralRefactoringEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        var sr = new StructuralRefinementEngine(_workspaceManager, config);
        var mapping = new MappingEngine(_workspaceManager);
        var semLib = new SemanticRefactoringEngine(_workspaceManager);
        var advLogic = new LogicSimplificationEngine(_workspaceManager);
        var style = new CodeStyleEngine(_workspaceManager, config);
        var codeFlow = new LogicSimplificationEngine(_workspaceManager);
        var advRefactoring = new AdvancedRefactoringEngine(_workspaceManager);
        var logicOpt = new LogicSimplificationEngine(_workspaceManager);
        var modernization = new SyntaxModernizationEngine(_workspaceManager, config);
        var nav = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);

        _basicRefactoringEngine = new BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        _advancedRefactoringEngine = new AdvancedRefactoringEngine(_workspaceManager);
        _memberRefactoringEngine = new MemberRefactoringEngine(_workspaceManager, nav, validation, config);
        _structuralRefactoringEngine = new StructuralRefactoringEngine(_workspaceManager);

        _refactoringStructuralTools = new RefactoringStructuralTools(new RefactoringStructuralImpl(
            _basicRefactoringEngine,
            _memberRefactoringEngine,
            sr,
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager,
            new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringStructuralImpl>.Instance));
        _refactoringSignatureTools = new RefactoringSignatureTools(new RefactoringSignatureImpl(
            _basicRefactoringEngine,
            _memberRefactoringEngine,
            _workspaceManager,
            new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            NullLogger<RefactoringSignatureImpl>.Instance));

        // ExtractMembers / MoveType moved to AdvancedRefactoringTools in the server split.
        _advancedRefactoringTools = new AdvancedRefactoringTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task ExtractInterface_ShouldCreateInterface(int id)
    {
        SetSource($"public class C{id} {{ public void M{id}() {{}} }}", $"C{id}.cs");
        var result = await _advancedRefactoringTools.ExtractMembers(reason: "test message", $"C{id}.cs", $"C{id}", ExtractAsType.@interface, $"IC{id}", autoStage: false);

        // With autoStage:false the tool returns SuccessDetails = AppliedChangeSummary { ChangedContent = Dictionary<FilePathWrapper, string> }.
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var changes = ((AppliedChangeSummary)result.SuccessData!).ChangedContent;
        Assert.That(changes, Is.Not.Null.And.Not.Empty);
    }

    // ── Bug 62: ExtractMembersToPartial -> Missing Namespace + Usings ─────────
    [Test]
    [Category("StructuralRefactoringEngine")] // sentinel:auto-category
    public async Task BUG_62_ExtractMembersToPartial_IncludesNamespaceAndUsings()
    {
        const string code = @"using System;
using System.Collections.Generic;

namespace MyApp.Services
{
    public partial class DataService
    {
        public void Method1() { }
        public void Method2() { }
    }
}";
        SetSource(code, "DataService.cs");
        var result = await _structuralRefactoringEngine.ExtractMembersToPartialAsync("DataService.cs", "DataService", new[] { "Method1" });
        Assert.That(result, Is.Not.Null, "Should return a result");
        Assert.That(result, Is.Not.Empty, "Should contain extracted file");
        // Get the extracted partial file content
        var partialFileContent = result.Values.First();
        // The result should contain namespace declaration
        Assert.That(partialFileContent, Does.Contain("namespace MyApp.Services"), "Extracted partial file must include the namespace");
        // Should also include usings
        Assert.That(partialFileContent, Does.Contain("using System;"), "Extracted partial file must include usings");
        // Should contain the extracted method
        Assert.That(partialFileContent, Does.Contain("Method1"), "Extracted partial file must contain the extracted method");
    }


    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Category("RefactoringSignatureTools")] // sentinel:auto-category
    [Category("SymbolLocation")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task RenameSymbol_ShouldUpdateReferences(int id)
    {
        var source = $"public class C{id} {{ public void OldM{id}() {{}} public void U() {{ OldM{id}(); }} }}";
        SetSource(source, $"C{id}.cs");

        // RenameSymbol takes a SymbolHandle (projectName, docCommentId) -> resolve it
        // via SymbolNavigationEngine first, as an agent would via LocateSymbol.
        var symbolNavEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var handle = (await symbolNavEngine.LocateSymbolAsync($"OldM{id}")).Single();

        var result = await _refactoringSignatureTools.RenameSymbol(
            reason: "test message", projectName: handle.ProjectName, docCommentId: handle.DocCommentId!,
            newName: $"NewM{id}");
        Assert.That(!result.IsError, Is.True);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.That(json, Contains.Substring($"NewM{id}"));
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task MoveTypeToFile_ShouldSeparateTypes(int id)
    {
        SetSource($"public class C{id} {{}} public class D{id} {{}}", $"C{id}.cs");
        var result = await _advancedRefactoringTools.MoveType(reason: "test message", $"C{id}.cs", $"D{id}", "ownFile", autoStage: false);

        // Dictionary keys are FilePathWrapper, not string, since the server split.
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var data = result.SuccessData?.ChangedContent;
        Assert.That(data?.Count, Is.GreaterThan(1));
    }
}
