using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class MassiveModernizationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private RefactoringEngine _refactoringEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private SentinelModernizationTools _modernizationTools;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, config);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, config);
        
        var modern = new ModernizationEngine(_workspaceManager, config);
        var upgrade = new ModernizationUpgradeEngine(_workspaceManager);
        var logging = new ModernLoggingEngine(_workspaceManager);
        var analysis = new AnalysisEngine(_workspaceManager, config);
        var logic = new LogicOptimizationEngine(_workspaceManager);
        var style = new CodeStyleEngine(_workspaceManager, config);
        var healing = new CodeHealingEngine(_workspaceManager, config);
        var advLogic = new AdvancedLogicEngine(_workspaceManager);
        var ideStyle = new IDEStyleEngine(_workspaceManager);
        var immutability = new ImmutabilityEngine(_workspaceManager);
        var asyncOpt = new AsyncOptimizationEngine(_workspaceManager);
        
        _modernizationTools = new SentinelModernizationTools(modern, upgrade, logging, _syntaxUpgradeEngine, analysis, logic, style, healing, advLogic, ideStyle, immutability, asyncOpt, _workspaceManager, config, NullLogger<SentinelModernizationTools>.Instance);
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
    public async Task UpgradePatternMatching_ShouldSimplifyIfStatements(int id)
    {
        SetSource($@"public class C{id} {{ 
            public int M(object o) {{ 
                if (o is string) {{ var s = (string)o; return s.Length; }} 
                return 0; 
            }} 
        }}", $"C{id}.cs");
        var result = await _syntaxUpgradeEngine.UpgradePatternMatchingAsync($"C{id}.cs");
        Assert.That(result, Contains.Substring("is string s"));
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task UsePrimaryConstructors_ShouldModernizeClass(int id)
    {
        SetSource($@"public class C{id} {{ 
            private readonly int _x; 
            public C{id}(int x) {{ _x = x; }} 
            public int X => _x; 
        }}", $"C{id}.cs");
        var result = await _refactoringEngine.ConvertToPrimaryConstructorAsync($"C{id}.cs", $"C{id}");
        Assert.That(result, Contains.Substring($"class C{id}(int x)"));
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public async Task ClassToRecord_ShouldModernize(int id)
    {
        SetSource($@"public class C{id} {{ public int Id {{ get; set; }} }}", $"C{id}.cs");
        var result = await _modernizationTools.ClassToRecord($"C{id}.cs", $"C{id}");
        Assert.That(result, Contains.Substring("record C"));
    }
}
