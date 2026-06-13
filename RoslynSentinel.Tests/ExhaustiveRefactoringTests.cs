#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
public class ExhaustiveRefactoringTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private CodeStyleEngine _codeStyleEngine;

    [SetUp]
    public void Setup()
    {
        var config = new SentinelConfiguration();
        _workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, config);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { ("Test.cs", source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task UpgradeGuards_ShouldHandleAllRangeVariants()
    {
        SetSource(@"
using System;
public class C {
    public void M(int i, int limit) {
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(i));
        if (i <= 0) throw new ArgumentOutOfRangeException(nameof(i));
        if (i == 0) throw new ArgumentOutOfRangeException(nameof(i));
        if (i > limit) throw new ArgumentOutOfRangeException(nameof(i));
    }
}");
        var result = await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync("Test.cs");
        Assert.That(result.UpdatedText!, Contains.Substring("ArgumentOutOfRangeException.ThrowIfNegative(i)"));
        Assert.That(result.UpdatedText!, Contains.Substring("ArgumentOutOfRangeException.ThrowIfNegativeOrZero(i)"));
        Assert.That(result.UpdatedText!, Contains.Substring("ArgumentOutOfRangeException.ThrowIfZero(i)"));
        Assert.That(result.UpdatedText!, Contains.Substring("ArgumentOutOfRangeException.ThrowIfGreaterThan(i, limit)"));
    }

    [Test]
    public async Task UseTimeProvider_ShouldDetectExistingField()
    {
        SetSource(@"
using System;
public class C {
    private readonly TimeProvider _myCustomProvider;
    public DateTime M() => DateTime.UtcNow;
}");
        var result = await _codeStyleEngine.UseTimeProviderAsync("Test.cs");
        Assert.That(result.UpdatedText!, Contains.Substring("_myCustomProvider.GetUtcNow()"));
        Assert.That(result.UpdatedText!, Does.Not.Contain("private readonly TimeProvider _timeProvider;"), "Should not add new field if one exists.");
    }

    [Test]
    public async Task UseTimeProvider_ShouldHandleDateTimeOffsetAndLocal()
    {
        SetSource(@"
using System;
public class C {
    public void M() {
        var a = DateTimeOffset.UtcNow;
        var b = DateTime.Now;
    }
}");
        var result = await _codeStyleEngine.UseTimeProviderAsync("Test.cs");
        Assert.That(result.UpdatedText!, Contains.Substring("_timeProvider.GetUtcNow()"));
        Assert.That(result.UpdatedText!, Contains.Substring("_timeProvider.GetLocalNow()"));
        Assert.That(result.UpdatedText!, Contains.Substring("private readonly TimeProvider _timeProvider;"));
    }

    [Test]
    public async Task ClassToRecord_ShouldHandleAttributesAndMethods()
    {
        var config = new SentinelConfiguration();
        SetSource(@"
public class MyPoco {
    [Required]
    public string Name { get; set; }
    public void DoWork() {}
}");
        var modernizationEngine = new ModernizationEngine(_workspaceManager, config);
        var result = await modernizationEngine.ClassToRecordAsync("Test.cs", "MyPoco");
        // Bug 2 fix: properties with [Required] cannot use positional syntax (would drop attributes),
        // so class-body record with init accessors is generated instead.
        Assert.That(result.UpdatedText!, Contains.Substring("record MyPoco"));
        Assert.That(result.UpdatedText!, Contains.Substring("[Required]"), "Attribute must be preserved");
        Assert.That(result.UpdatedText!, Contains.Substring("init"), "set accessor must be converted to init");
        Assert.That(result.UpdatedText!, Contains.Substring("DoWork"), "Methods must be preserved");
    }
}
