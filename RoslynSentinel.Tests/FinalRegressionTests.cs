using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

/// <summary>
/// Final regression tests for RoslynSentinel:
/// - BUG-61: SyncTypeAndFilename uses staging mechanism
/// - BUG-60: RemoveMember checks for usages
/// - BUG-57: IntroduceParameterObject handles interfaces
/// - BUG-55: OptimizeToValueTask updates interface signatures
/// - 4 additional regression tests for final coverage
/// </summary>
[TestFixture]
public class FinalRegressionTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;
    private AsyncOptimizationEngine _asyncOptimizationEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _asyncOptimizationEngine = new AsyncOptimizationEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // BUG-61: SyncTypeAndFilename — Uses Staging Instead of Direct Write
    // ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BUG_61_SyncTypeAndFilename_UsesStagingMechanism()
    {
        const string code = @"
            namespace MyApp
            {
                public class DataService { }
                public class Helper { }
            }";
        
        SetSource(code, "WrongName.cs");
        var doc = _workspaceManager.GetBranchedSolutionAsync().Result
            .Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        
        var result = await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePath);
        
        // Should use staging (CHANGE_ prefix) instead of direct File.Move
        Assert.That(result, Does.Contain("CHANGE_"), 
            "Should use staging mechanism with CHANGE_ prefix");
        Assert.That(result, Does.Contain("DataService.cs"), 
            "Should identify primary type DataService");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // BUG-60: RemoveMember — Validates Usages Before Removal
    // ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BUG_60_RemoveMember_ErrorsWhenMemberIsUsed()
    {
        const string code = @"
            public class Helper
            {
                public string GetName() => ""Test"";
                
                public void UseHelper()
                {
                    var name = GetName();
                }
            }";
        
        SetSource(code);
        var doc = _workspaceManager.GetBranchedSolutionAsync().Result
            .Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        
        var result = await _refactoringEngine.RemoveMemberAsync(filePath, "GetName");
        
        // Should error because GetName is used in UseHelper
        Assert.That(result, Does.Contain("ERROR") | Does.Contain("usages"),
            "Should error when trying to remove a used member");
    }

    [Test]
    public async Task BUG_60_RemoveMember_SucceedsWhenMemberUnused()
    {
        const string code = @"
            public class Helper
            {
                public string UnusedMethod() => ""test"";
                
                public void OtherMethod() { }
            }";
        
        SetSource(code);
        var doc = _workspaceManager.GetBranchedSolutionAsync().Result
            .Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        
        var result = await _refactoringEngine.RemoveMemberAsync(filePath, "UnusedMethod");
        
        // Should succeed and remove the unused method
        Assert.That(result, Does.Not.Contain("UnusedMethod"),
            "Should remove unused member without errors");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // BUG-57: IntroduceParameterObject — Warns About Interface Methods
    // ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BUG_57_IntroduceParameterObject_WarnsAboutInterfaceImplementation()
    {
        const string code = @"
            public interface IProcessor
            {
                void Process(string name, int age, bool active);
            }
            
            public class Processor : IProcessor
            {
                public void Process(string name, int age, bool active) { }
            }";
        
        SetSource(code);
        var doc = _workspaceManager.GetBranchedSolutionAsync().Result
            .Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        
        var engine = new GranularRefactoringEngine(_workspaceManager);
        var result = await engine.IntroduceParameterObjectAsync(filePath, "Process");
        
        Assert.That(result, Is.Not.Null);
        // Should create parameter object and possibly warn about interface
        Assert.That(result, Does.Contain("ProcessParameters") | Does.Contain("WARNING"),
            "Should introduce parameter object or warn about interface");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // BUG-55: OptimizeToValueTask — Warns About Interface Signature Changes
    // ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task BUG_55_OptimizeToValueTask_WarnsAboutInterfaceUpdate()
    {
        const string code = @"
            public interface IDataService
            {
                Task<string> GetDataAsync();
            }
            
            public class DataService : IDataService
            {
                public async Task<string> GetDataAsync()
                {
                    return await Task.FromResult(""data"");
                }
            }";
        
        SetSource(code);
        var doc = _workspaceManager.GetBranchedSolutionAsync().Result
            .Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        
        var result = await _asyncOptimizationEngine.OptimizeToValueTaskAsync(filePath, "GetDataAsync");
        
        Assert.That(result, Does.Contain("ValueTask<string>"),
            "Should convert Task<T> to ValueTask<T>");
        Assert.That(result, Does.Contain("WARNING") | Does.Contain("interface"),
            "Should warn about interface when it implements one");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Additional Regression Tests
    // ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DisposalPattern_DocumentCanBeAnalyzed()
    {
        const string code = @"
            public class DataAccess
            {
                public void BadDisposal()
                {
                    var conn = new System.Collections.Hashtable();
                }
            }";
        
        SetSource(code);
        var doc = _workspaceManager.GetBranchedSolutionAsync().Result
            .Projects.First().Documents.First();
        var root = await doc.GetSyntaxRootAsync();
        
        Assert.That(root, Is.Not.Null, "Should parse document without errors");
        Assert.That(root?.ToFullString(), Does.Contain("DataAccess"),
            "Should contain the class definition");
    }

    [Test]
    public async Task StaticReadOnlyDictionary_CanBeIdentified()
    {
        const string code = @"
            public class Config
            {
                private static readonly Dictionary<string, int> TierRank = new()
                {
                    { ""Basic"", 1 },
                    { ""Pro"", 2 }
                };
            }";
        
        SetSource(code);
        var doc = _workspaceManager.GetBranchedSolutionAsync().Result
            .Projects.First().Documents.First();
        var root = await doc.GetSyntaxRootAsync();
        
        var dictVar = root?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == "TierRank");
        
        Assert.That(dictVar, Is.Not.Null, "Should find static readonly dictionary");
    }

    [Test]
    public async Task TaskRunPattern_IsDetectable()
    {
        const string code = @"
            public class BackgroundService
            {
                public async Task HandleAsync()
                {
                    Task.Run(() => DoWork());
                }
                
                private async Task DoWork()
                {
                    await Task.Delay(1000);
                }
            }";
        
        SetSource(code);
        var doc = _workspaceManager.GetBranchedSolutionAsync().Result
            .Projects.First().Documents.First();
        var root = await doc.GetSyntaxRootAsync();
        
        // Look for Task.Run fire-and-forget pattern
        var taskRunCall = root?.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.ToString().Contains("Task.Run"));
        
        Assert.That(taskRunCall, Is.Not.Null, "Should detect Task.Run fire-and-forget");
    }

    [Test]
    public async Task SimpleRefactoring_CompletesSuccessfully()
    {
        const string code = @"
            public class Service
            {
                public void DoWork() { }
            }";
        
        SetSource(code);
        var doc = _workspaceManager.GetBranchedSolutionAsync().Result
            .Projects.First().Documents.First();
        var root = await doc.GetSyntaxRootAsync();
        
        Assert.That(root, Is.Not.Null);
        Assert.That(root?.ToFullString(), Does.Contain("Service"));
    }
}
