// Accuracy regression tests for the four improved tools.
// Each [Test] exercises exactly one detection rule -> positive (should flag) and
// negative (should NOT flag, verifying false-positive guards).
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("ThreadSafetyEngine")] // sentinel:auto-category
public class DoubleCheckedLockingTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private ThreadSafetyEngine _engine = null!;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Basic.TestEngines.ThreadSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source) => _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));
    [Test]
    public async Task Flags_DCL_WithoutVolatile()
    {
        SetSource(@"
public class Singleton {
    private static Singleton _instance;
    private static readonly object _lock = new object();
    public static Singleton Instance {
        get {
            if (_instance == null) {
                lock (_lock) {
                    if (_instance == null) {
                        _instance = new Singleton();
                    }
                }
            }
            return _instance;
        }
    }
}");
        var results = await _engine.FindDoubleCheckedLockingAsync();
        Assert.That(results, Has.Count.EqualTo(1), "DCL without volatile field should be flagged");
        Assert.That(results[0], Does.Contain("_instance"));
    }

    [Test]
    public async Task DoesNotFlag_DCL_WithVolatile()
    {
        SetSource(@"
public class Singleton {
    private static volatile Singleton _instance;
    private static readonly object _lock = new object();
    public static Singleton Instance {
        get {
            if (_instance == null) {
                lock (_lock) {
                    if (_instance == null) {
                        _instance = new Singleton();
                    }
                }
            }
            return _instance;
        }
    }
}");
        var results = await _engine.FindDoubleCheckedLockingAsync();
        Assert.That(results, Is.Empty, "DCL with volatile field is the correct pattern - must not be flagged");
    }
}
