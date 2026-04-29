#pragma warning disable CS8618
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
public class DeepFunctionalVerificationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private CodeStyleEngine _codeStyleEngine;
    private CodeHealingEngine _codeHealingEngine;
    private ProjectStructureEngine _projectStructureEngine;
    private RefactoringEngine _refactoringEngine;
    private DependencyEngine _dependencyEngine;
    private ModernizationEngine _modernizationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager);
        _codeHealingEngine = new CodeHealingEngine(_workspaceManager);
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager);
        _refactoringEngine = new RefactoringEngine(new NullLogger<RefactoringEngine>(), _workspaceManager);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _modernizationEngine = new ModernizationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs", string projectName = "TestProj")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(projectName, new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ModernizeExceptions_ShouldReplaceAndGenerateClasses()
    {
        // Arrange
        SetSource(@"
namespace App;
public class Service {
    public void Run() {
        throw new Exception(""Database connection failed"");
    }
}", "Service.cs");

        var targets = new List<CodeHealingEngine.ExceptionTarget> {
            new CodeHealingEngine.ExceptionTarget("Service.cs", 5, "DatabaseException")
        };

        // Act
        var changes = await _codeHealingEngine.ModernizeExceptionsAsync(targets);

        // Assert
        Assert.That(changes.Count, Is.EqualTo(2), "Should produce 2 changes: the update and the new file.");
        Assert.That(changes["Service.cs"], Contains.Substring("throw new DatabaseException"));
        
        var newFile = changes.Keys.First(k => k.Contains("DatabaseException.cs"));
        Assert.That(changes[newFile], Contains.Substring("public class DatabaseException : Exception"));
        Assert.That(changes[newFile], Contains.Substring("namespace App;"));
    }

    [Test]
    public async Task SafeDelete_ShouldBlockOnReflectionRisk()
    {
        // Arrange
        SetSource("public class Target { public void DeadMethod() {} }", "Target.cs", "Proj");
        
        var projectId = _workspaceManager.CurrentSolution.ProjectIds.First();
        var callerDocId = DocumentId.CreateNewId(projectId);
        var solution = _workspaceManager.CurrentSolution.AddDocument(callerDocId, "Caller.cs", "public class Caller { void M() { var name = \"DeadMethod\"; } }");
        _workspaceManager.SetTestSolution(solution);

        // Act & Assert
        var sourceText = "public class Target { public void DeadMethod() {} }";
        var col = sourceText.IndexOf("DeadMethod") + 1; // 1-based column

        var ex = Assert.ThrowsAsync<Exception>(async () => 
            await _refactoringEngine.SafeDeleteSymbolAsync("Target.cs", 1, col));
        
        Assert.That(ex.Message, Does.Contain("Potential Reflection Risk"));
        Assert.That(ex.Message, Does.Contain("Caller.cs"));
    }

    [Test]
    public async Task RecordToClass_ShouldPreserveImmutability()
    {
        // Arrange
        SetSource("public record MyRecord(string Name, int Age);");

        // Act
        var result = await _modernizationEngine.RecordToClassAsync("Test.cs", "MyRecord");

        // Assert
        Assert.That(result, Contains.Substring("public class MyRecord"));
        Assert.That(result, Contains.Substring("public string Name { get; init; }"));
        Assert.That(result, Contains.Substring("public int Age { get; init; }"));
    }

    [Test]
    public async Task UseNameofExpression_ShouldReplaceExactMatches()
    {
        // Arrange
        SetSource(@"
public class MyType {
    public void M() {
        var s = ""MyType"";
    }
}");

        // Act
        var result = await _syntaxUpgradeEngine.UseNameofExpressionAsync("Test.cs", 4, 18);

        // Assert
        Assert.That(result, Contains.Substring("var s = nameof(MyType);"));
    }

    [Test]
    public async Task FindStructuralSmells_ShouldIdentifyThreadSafetyIssues()
    {
        // Arrange
        SetSource(@"
using System.Threading;
public class C {
    private SemaphoreSlim _s = new(1);
    public void M() {
        _s.Wait();
        // Missing finally release
    }
    public void L() {
        lock(this) { }
    }
}");

        // Act
        var smells = await _projectStructureEngine.FindStructuralSmellsAsync(ProjectStructureEngine.StructuralSmellType.ThreadSafety);

        // Assert
        Assert.That(smells.Any(s => s.Contains("SemaphoreSlim")), Is.True);
        Assert.That(smells.Any(s => s.Contains("lock object 'this'")), Is.True);
    }

    [Test]
    public async Task GetProjectDependencies_ShouldExtractFromCsproj()
    {
        // Arrange
        var csproj = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";
        
        var tempCsproj = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MyProj.csproj");
        File.WriteAllText(tempCsproj, csproj);

        var projectId = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "MyProj", "MyProj", LanguageNames.CSharp, filePath: tempCsproj));
        
        _workspaceManager.SetTestSolution(solution);

        // Act
        var report = await _dependencyEngine.GetProjectDependenciesAsync("MyProj");

        // Assert
        Assert.That(report.PackageReferences, Contains.Item("Newtonsoft.Json"));
        
        // Cleanup
        File.Delete(tempCsproj);
    }
}
