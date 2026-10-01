#pragma warning disable CS8618
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class DeepFunctionalVerificationTests
{
    private IWorkspaceManager _workspaceManager;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private CodeStyleEngine _codeStyleEngine;
    private CodeHealingEngine _codeHealingEngine;
    private SolutionStructureEngine _projectStructureEngine;
    private BasicRefactoringEngine _refactoringEngine;
    private DependencyEngine _dependencyEngine;
    private SyntaxModernizationEngine _modernizationEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(new NullLogger<IWorkspaceManager>());
        var config = new SentinelConfiguration();
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, config);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, config);
        _codeHealingEngine = new CodeHealingEngine(_workspaceManager, config);
        _projectStructureEngine = new SolutionStructureEngine(_workspaceManager, config);
        _refactoringEngine = new BasicRefactoringEngine(_workspaceManager, new NullLogger<BasicRefactoringEngine>(), config);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _modernizationEngine = new SyntaxModernizationEngine(_workspaceManager, config);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, config);
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

        var currentSolution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var projectId = currentSolution?.ProjectIds[0] ?? throw new InvalidOperationException("No project found.");
        var callerDocId = DocumentId.CreateNewId(projectId);
        var solution = currentSolution!.AddDocument(callerDocId, "Caller.cs", "public class Caller { void M() { var name = \"DeadMethod\"; } }");
        _workspaceManager.SetTestSolution(solution);

        // Act & Assert
        var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync("Target.cs", "DeadMethod", "public void DeadMethod()", null, null);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit));
        Assert.That(result.Message, Does.Contain("Potential reflection risk"));
        Assert.That(result.Message, Does.Contain("Caller.cs"));
    }

    [Test]
    public async Task RecordToClass_ShouldPreserveImmutability()
    {
        // Arrange
        SetSource("public record MyRecord(string Name, int Age);");

        // Act
        var result = await _modernizationEngine.RecordToClassAsync("Test.cs", "MyRecord");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("public class MyRecord"));
        Assert.That(result.UpdatedText!, Contains.Substring("public string Name { get; init; }"));
        Assert.That(result.UpdatedText!, Contains.Substring("public int Age { get; init; }"));
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
        var result = await _syntaxUpgradeEngine.UseNameofExpressionAsync("Test.cs", "\"MyType\"");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("var s = nameof(MyType);"));
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
        var smells = await _projectStructureEngine.FindStructuralSmellsAsync(SolutionStructureEngine.StructuralSmellType.ThreadSafety);

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
        await File.WriteAllTextAsync(tempCsproj, csproj);

        var projectId = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "MyProj", "MyProj", LanguageNames.CSharp, filePath: tempCsproj));

        _workspaceManager.SetTestSolution(solution);

        // Act
        var report = await _dependencyEngine.GetProjectDependenciesAsync("MyProj", CancellationToken.None);

        // Assert
        Assert.That(report.PackageReferences, Contains.Item("Newtonsoft.Json"));

        // Cleanup
        File.Delete(tempCsproj);
    }

    [Test]
    public async Task RemoveUsingDirective_RemovesDirective_PreservesOthersAndTrivia()
    {
        SetSource(@"
using System;
using System.Linq;
using System.Text;

namespace Demo;

public class Foo
{
    public void Bar() { }
}
", "Foo.cs");

        var result = await _refactoringEngine.RemoveUsingDirectiveAsync("Foo.cs", "System.Linq");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Not.Contain("System.Linq"));
        Assert.That(result.UpdatedText, Does.Contain("using System;"));
        Assert.That(result.UpdatedText, Does.Contain("using System.Text;"));
        Assert.That(result.UpdatedText, Does.Contain("public void Bar() { }"));
    }

    private const string ApplyDiscountLikeSource = @"
namespace ContosoOrders.Core;
public class Order
{
    public decimal ApplyDiscount(decimal percentage)
    {
        // NOTE: this method uses DiscountCalculator, but the using directive for
        // ContosoOrders.Core.Discounts is intentionally missing from this file (fully qualified below
        // as a workaround) to create a scenario for AddUsingDirective.
        return ContosoOrders.Core.Discounts.DiscountCalculator.ApplyPercentage(CalculateTotal(), percentage);
    }
}";



    [Test]
    public async Task FixDangerousLock_ShouldInjectLockObject()
    {
        // Arrange
        SetSource("public class C { public void M() { lock(this) { } } }", "C.cs");

        // Act
        var result = await _codeStyleEngine.FixDangerousLockAsync("C.cs");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("private readonly Lock _lockObj = new Lock()"));
        Assert.That(result.UpdatedText!, Contains.Substring("lock (_lockObj)"));
        Assert.That(result.UpdatedText!, Contains.Substring("using System.Threading;"));
    }

    [Test]
    public async Task ConvertPropertyToMethods_ShouldCreateGetSet()
    {
        // Arrange
        SetSource("public class C { public string Name { get; set; } }", "C.cs");

        // Act
        var result = await _codeStyleEngine.ConvertPropertyToMethodsAsync("C.cs", "Name");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("public string GetName()"));
        Assert.That(result.UpdatedText!, Contains.Substring("public void SetName(string value)"));
        Assert.That(result.UpdatedText!, Contains.Substring("private string _name;"));
    }

    [Test]
    public async Task CleanupImplicitSpans_ShouldRemoveAsSpan()
    {
        // Arrange
        SetSource(@"
using System;
public class C {
    public void M(byte[] data) {
        ReadOnlySpan<byte> span = data.AsSpan();
    }
}", "C.cs");

        // Act
        var result = await _syntaxUpgradeEngine.CleanupImplicitSpansAsync("C.cs");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("ReadOnlySpan<byte> span = data;"));
        Assert.That(result.UpdatedText!, Does.Not.Contain(".AsSpan()"));
    }
}
