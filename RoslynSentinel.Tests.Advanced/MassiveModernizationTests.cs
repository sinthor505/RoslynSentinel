using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("SyntaxUpgradeEngine")] // sentinel:auto-category
public class MassiveModernizationTests
{
    private IWorkspaceManager _workspaceManager;
    private BasicRefactoringEngine _refactoringEngine;
    private AdvancedRefactoringEngine _advancedRefactoringEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private SyntaxModernizationEngine _modernizationEngine;
    private ModernizationTools _modernizationTools;

    [SetUp]
    public void Setup()
    {
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        _advancedRefactoringEngine = Battery.Advanced.TestEnginesAdvanced.AdvancedRefactoringEngine(_workspaceManager, NullLogger<AdvancedRefactoringEngine>.Instance, config);
        _syntaxUpgradeEngine = Battery.Basic.TestEngines.SyntaxUpgradeEngine(_workspaceManager, config);

        _modernizationEngine = Battery.Advanced.TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, config);

        _modernizationTools = Battery.Advanced.TestToolsAdvanced.ModernizationTools(_workspaceManager);
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
        Assert.That(result.UpdatedText, Contains.Substring("is string s"));
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
        var result = await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync($"C{id}.cs", $"C{id}");
        Assert.That(result.UpdatedText, Contains.Substring($"class C{id}(int x)"));
    }

    [Test]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [Category("SyntaxModernizationEngine")] // sentinel:auto-category
    public async Task ClassToRecord_ShouldModernize(int id)
    {
        SetSource($@"public class C{id} {{ public int Id {{ get; set; }} }}", $"C{id}.cs");
        var result = await _modernizationEngine.ClassToRecordAsync($"C{id}.cs", $"C{id}");
        Assert.That(result.UpdatedText, Contains.Substring("record C"));
    }
}
