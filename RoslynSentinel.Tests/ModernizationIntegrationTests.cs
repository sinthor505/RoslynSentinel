#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
public class ModernizationIntegrationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private ProjectStructureEngine _projectStructureEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private CodeStyleEngine _codeStyleEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
    }

    private void SetSource(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { ("Test.cs", source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task UpgradeToModernGuards_ShouldConvertAllPatterns()
    {
        // Arrange
        SetSource(@"
using System;
public class C {
    public void M(string s, int i) {
        if (s == null) throw new ArgumentNullException(nameof(s));
        if (string.IsNullOrEmpty(s)) throw new ArgumentException(""err"", nameof(s));
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
    }
}");

        // Act
        var result = await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync("Test.cs");

        // Assert
        Assert.That(result, Contains.Substring("ArgumentNullException.ThrowIfNull(s)"));
        Assert.That(result, Contains.Substring("ArgumentException.ThrowIfNullOrEmpty(s)"));
        Assert.That(result, Contains.Substring("ArgumentOutOfRangeException.ThrowIfNegative(i)"));
    }

    [Test]
    public async Task SimplifyVerbosity_ShouldApplyTargetTypedNewAndNullCoalesce()
    {
        // Arrange
        SetSource(@"
using System.Collections.Generic;
public class C {
    private Dictionary<string, string> _dict = new Dictionary<string, string>();
    public void M(List<string> input) {
        if (input == null) input = new List<string>();
    }
}");

        // Act
        var result = await _codeStyleEngine.SimplifyVerbosityAsync("Test.cs");

        // Assert
        Assert.That(result, Contains.Substring("private Dictionary<string, string> _dict = new();"));
        Assert.That(result, Contains.Substring("input ??= new();"));
    }

    [Test]
    public async Task UseCollectionExpressions_ShouldConvertListsAndArrays()
    {
        // Arrange
        SetSource(@"
using System.Collections.Generic;
public class C {
    public void M() {
        var a = new int[] { 1, 2, 3 };
        var b = new List<int> { 4, 5 };
        var c = new int[0];
    }
}");

        // Act
        var result = await _codeStyleEngine.UseCollectionExpressionsAsync("Test.cs");

        // Assert
        Assert.That(result, Contains.Substring("var a = [1, 2, 3];"));
        Assert.That(result, Contains.Substring("var b = [4, 5];"));
        Assert.That(result, Contains.Substring("var c = [];"));
    }

    [Test]
    public async Task UseTimeProvider_ShouldReplaceDateTimeUsage()
    {
        // Arrange
        SetSource(@"
using System;
public class C {
    public DateTime GetTime() => DateTime.UtcNow;
}");

        // Act
        var result = await _codeStyleEngine.UseTimeProviderAsync("Test.cs");

        // Assert
        Assert.That(result, Contains.Substring("_timeProvider.GetUtcNow()"));
    }

    [Test]
    public async Task FindStructuralSmells_ShouldDetectNewCategories()
    {
        // Arrange
        SetSource(@"
using System;
public class C {
    public void M(string s) {
        if (s == null) throw new ArgumentNullException(nameof(s));
        var time = DateTime.Now;
        lock(this) { }
    }
}");

        // Act
        var smells = await _projectStructureEngine.FindStructuralSmellsAsync();

        // Assert
        Assert.That(smells.Any(s => s.Contains("[LEGACY_GUARD]")), Is.True);
        Assert.That(smells.Any(s => s.Contains("[TIME_ABSTRACTION]")), Is.True);
        Assert.That(smells.Any(s => s.Contains("[THREAD_SAFETY]")), Is.True);
    }
}
