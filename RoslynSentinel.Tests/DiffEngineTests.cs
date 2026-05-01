using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests;

[TestFixture]
public class DiffEngineTests
{
    private DiffEngine _diffEngine;

    [SetUp]
    public void Setup()
    {
        var workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        _diffEngine = new DiffEngine(workspaceManager);
    }

    [Test]
    public void ApplyDiff_SingleHunk_Addition_ShouldSucceed()
    {
        var nl = Environment.NewLine;
        var oldText = SourceText.From("line1" + nl + "line2" + nl + "line3");
        var diff = "@@ -1,3 +1,4 @@\n line1\n+added\n line2\n line3";
        
        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();
        
        Assert.That(newText, Is.EqualTo("line1" + Environment.NewLine + "added" + Environment.NewLine + "line2" + Environment.NewLine + "line3"));
    }

    [Test]
    public void ApplyDiff_MultipleHunks_ShouldSucceed()
    {
        var nl = Environment.NewLine;
        var oldText = SourceText.From("line1" + nl + "line2" + nl + "line3" + nl + "line4" + nl + "line5");
        // Hunk 1 starts at 1, Hunk 2 starts at 4 (relative to original)
        var diff = "@@ -1,3 +1,4 @@\n line1\n+added1\n line2\n line3\n@@ -4,2 +5,3 @@\n line4\n+added2\n line5";
        
        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();
        
        var expected = string.Join(Environment.NewLine, new[] { "line1", "added1", "line2", "line3", "line4", "added2", "line5" });
        Assert.That(newText, Is.EqualTo(expected));
    }

    [Test]
    public async Task ValidateProposedDiff_ShouldReturnNoErrors_ForValidDiff()
    {
        var source = "public class C { public void M() {} }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { ("C.cs", source) });
        var workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        workspaceManager.SetTestSolution(solution);
        
        var diffEngine = new DiffEngine(workspaceManager);
        var validationEngine = new ValidationEngine(new NullLogger<ValidationEngine>(), workspaceManager, diffEngine);
        
        var diff = "@@ -1,1 +1,1 @@\n-public class C { public void M() {} }\n+public class C { public void M() { int x = 1; } }";
        
        var report = await validationEngine.ValidateDiffAsync("C.cs", diff);
        
        Assert.That(report.Success, Is.True);
        Assert.That(report.Diagnostics.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task ValidateProposedDiff_ShouldReturnErrors_ForInvalidDiff()
    {
        var source = "public class C { public void M() {} }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { ("C.cs", source) });
        var workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        workspaceManager.SetTestSolution(solution);
        
        var diffEngine = new DiffEngine(workspaceManager);
        var validationEngine = new ValidationEngine(new NullLogger<ValidationEngine>(), workspaceManager, diffEngine);
        
        // Introducing a syntax error (missing semicolon)
        var diff = "@@ -1,1 +1,1 @@\n-public class C { public void M() {} }\n+public class C { public void M() { int x = 1 } }";
        
        var report = await validationEngine.ValidateDiffAsync("C.cs", diff);
        
        Assert.That(report.Success, Is.False);
        Assert.That(report.Diagnostics.Any(d => d.Severity == "Error"), Is.True);
    }
}
