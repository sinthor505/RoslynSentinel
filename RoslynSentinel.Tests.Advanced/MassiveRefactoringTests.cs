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
    private string _tempDir;

    [SetUp]
    public void Setup()
    {
        // unique temp dir; a bare path wrote into the working directory (leak)
        _tempDir = Path.Combine(Path.GetTempPath(), "MassiveRefactoring_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        var sr = Battery.Basic.TestEngines.StructuralRefinementEngine(_workspaceManager, config);
        var mapping = Battery.Advanced.TestEnginesAdvanced.MappingEngine(_workspaceManager);
        var semLib = Battery.Basic.TestEngines.SemanticRefactoringEngine(_workspaceManager);
        var advLogic = Battery.Advanced.TestEnginesAdvanced.LogicSimplificationEngine(_workspaceManager);
        var style = Battery.Advanced.TestEnginesAdvanced.CodeStyleEngine(_workspaceManager, config);
        var codeFlow = Battery.Advanced.TestEnginesAdvanced.LogicSimplificationEngine(_workspaceManager);
        var advRefactoring = Battery.Advanced.TestEnginesAdvanced.AdvancedRefactoringEngine(_workspaceManager);
        var logicOpt = Battery.Advanced.TestEnginesAdvanced.LogicSimplificationEngine(_workspaceManager);
        var modernization = Battery.Advanced.TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, config);
        var nav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);

        _basicRefactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        _advancedRefactoringEngine = Battery.Advanced.TestEnginesAdvanced.AdvancedRefactoringEngine(_workspaceManager);
        _memberRefactoringEngine = Battery.Basic.TestEngines.MemberRefactoringEngine(_workspaceManager, nav, validation, config);
        _structuralRefactoringEngine = Battery.Advanced.TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager);

        _refactoringStructuralTools = Battery.Basic.TestTools.RefactoringStructuralTools(Battery.Basic.TestTools.RefactoringStructuralImpl(
            _basicRefactoringEngine,
            _memberRefactoringEngine,
            sr,
            Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager,
            Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringStructuralImpl>.Instance));
        _refactoringSignatureTools = new RefactoringSignatureTools(new RefactoringSignatureImpl(
            _basicRefactoringEngine,
            _memberRefactoringEngine,
            _workspaceManager,
            Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance),
            Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            NullLogger<RefactoringSignatureImpl>.Instance));

        // ExtractMembersTool / MoveTypeTool moved to AdvancedRefactoringTools in the server split.
        _advancedRefactoringTools = Battery.Advanced.TestToolsAdvanced.AdvancedRefactoringTools(_workspaceManager);
    }

    [TearDown]
    // unique temp dir; a bare path wrote into the working directory (leak)
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file must not fail the fixture teardown.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; a locked file must not fail the fixture teardown.
        }
    }

    // unique temp dir; a bare path wrote into the working directory (leak)
    private string PathOf(string fileName) => Path.Combine(_tempDir, fileName);

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
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
        // unique temp dir; a bare path wrote into the working directory (leak)
        var path = PathOf($"C{id}.cs");
        File.WriteAllText(path, source);
        SetSource(source, path);

        // RenameSymbolTool takes a SymbolHandle (projectName, docCommentId) -> resolve it
        // via SymbolNavigationEngine first, as an agent would via LocateSymbolTool.
        var symbolNavEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var handle = (await symbolNavEngine.LocateSymbolAsync($"OldM{id}")).Single();

        var result = await _refactoringSignatureTools.RenameSymbolTool(
            reason: "test message", projectName: handle.ProjectName, docCommentId: handle.DocCommentId!,
            newName: $"NewM{id}");
        Assert.That(!result.IsError, Is.True);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.That(json, Contains.Substring($"NewM{id}"));
    }
}
