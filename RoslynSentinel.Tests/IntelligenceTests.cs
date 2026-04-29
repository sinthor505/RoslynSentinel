using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Server;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

public class IntelligenceTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AnalysisEngine _analysisEngine;
    private DocumentationEngine _docEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _analysisEngine = new AnalysisEngine(_workspaceManager);
        _docEngine = new DocumentationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Solution CreateSolution(string source, string fileName = "Test.cs")
    {
        var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp);
        var docId = DocumentId.CreateNewId(projectId);
        return solution.AddDocument(docId, fileName, SourceText.From(source), filePath: fileName);
    }

    [Test]
    public async Task DetectLongParameterLists_Should_Flag_Method_With_Many_Params()
    {
        var source = "public class C { public void M(int a, int b, int c, int d, int e, int f) {} }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var issues = await _analysisEngine.DetectLongParameterListsAsync(threshold: 5);
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(issues[0], Contains.Substring("6 parameters"));
    }

    [Test]
    public async Task FindLargeSwitchStatements_Should_Flag_Complex_Switches()
    {
        var source = "public class C { public void M(int x) { switch(x) { case 1: break; case 2: break; case 3: break; } } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var issues = await _analysisEngine.FindLargeSwitchStatementsAsync(threshold: 2);
        Assert.That(issues.Count, Is.EqualTo(1));
        Assert.That(issues[0], Contains.Substring("Large switch statement"));
    }

    [Test]
    public async Task DocumentPocoFields_Should_Add_Description_Attributes()
    {
        var source = "public class Person { public string Name { get; set; } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "Person.cs"));
        var result = await _docEngine.DocumentPocoFieldsAsync("Person.cs", "Person");
        Assert.That(result, Contains.Substring("[Description(\"Gets or sets the Name.\")]"));
    }

    [Test]
    public async Task FindUninstantiatedTypes_Should_Flag_Unused_Classes()
    {
        var source = "public class UnusedClass { } public class UsedClass { } public class Consumer { void M() { var x = new UsedClass(); } }";
        _workspaceManager.SetTestSolution(CreateSolution(source, "C.cs"));
        var issues = await _analysisEngine.FindUninstantiatedTypesAsync();
        Assert.That(issues, Has.Some.Contains("UnusedClass"));
    }
    
    [Test]
    public async Task FindCircularDependencies_Should_Identify_Linear_Chain_Properly()
    {
        using var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        
        var p1Id = ProjectId.CreateNewId();
        var p2Id = ProjectId.CreateNewId();
        
        solution = solution.AddProject(p1Id, "P1", "P1", LanguageNames.CSharp)
                           .AddProject(p2Id, "P2", "P2", LanguageNames.CSharp);
                           
        solution = solution.AddProjectReference(p1Id, new ProjectReference(p2Id));
                           
        _workspaceManager.SetTestSolution(solution);

        var cycles = await _analysisEngine.FindCircularDependenciesAsync();

        Assert.That(cycles.Count, Is.EqualTo(0), "Linear dependency should not be flagged as circular.");
    }
}
