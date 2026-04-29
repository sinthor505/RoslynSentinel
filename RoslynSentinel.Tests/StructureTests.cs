using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Server;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

public class StructureTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ProjectStructureEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ProjectStructureEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
    }

    [Test]
    public async Task FindStructuralSmells_ShouldIdentify_MultipleTypes_InOneFile()
    {
        // 1. Create In-Memory Solution
        using var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        
        solution = solution.AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp);
        
        // 2. Add a document with TWO classes (the smell)
        var sourceCode = @"
            public class ClassOne {}
            public class ClassTwo {}
        ";
        var docId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(docId, "ClassOne.cs", SourceText.From(sourceCode));

        // 3. Inject into manager
        _workspaceManager.SetTestSolution(solution);

        // 4. Act
        var smells = await _engine.FindStructuralSmellsAsync();

        // 5. Assert
        Assert.That(smells, Has.Some.Contains("[MULTI_TYPE]"));
        Assert.That(smells, Has.Some.Contains("ClassOne.cs"));
    }

    [Test]
    public async Task FindStructuralSmells_ShouldIdentify_NameMismatch()
    {
        using var adhocWorkspace = new AdhocWorkspace();
        var solution = adhocWorkspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp);

        // Class name is MyClass, but file is WrongName.cs
        var sourceCode = "public class MyClass {}";
        var docId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(docId, "WrongName.cs", SourceText.From(sourceCode), filePath: "E:\\source\\repos\\WrongName.cs");

        _workspaceManager.SetTestSolution(solution);

        var smells = await _engine.FindStructuralSmellsAsync();

        Assert.That(smells, Has.Some.Contains("[NAME_MISMATCH]"));
        Assert.That(smells, Has.Some.Contains("MyClass"));
    }
}
