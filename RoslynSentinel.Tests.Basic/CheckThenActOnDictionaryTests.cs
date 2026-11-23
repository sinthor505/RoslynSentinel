// Accuracy regression tests for the four improved tools.
// Each [Test] exercises exactly one detection rule -> positive (should flag) and
// negative (should NOT flag, verifying false-positive guards).
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("ThreadSafetyEngine")] // sentinel:auto-category
public class CheckThenActOnDictionaryTests
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
    public async Task Flags_ContainsKey_ThenAdd_WithoutLock()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    private Dictionary<string, int> _d = new();
    public void Add(string key) {
        if (!_d.ContainsKey(key))
            _d.Add(key, 0);
    }
}");
        var results = await _engine.FindCheckThenActOnDictionaryAsync();
        Assert.That(results, Has.Count.EqualTo(1), "ContainsKey + Add without lock is a check-then-act race and should be flagged");
        Assert.That(results[0], Does.Contain("_d"));
    }

    [Test]
    public async Task DoesNotFlag_ContainsKey_ThenAdd_InsideLock()
    {
        SetSource(@"
using System.Collections.Generic;
public class C {
    private Dictionary<string, int> _d = new();
    private readonly object _lock = new();
    public void Add(string key) {
        lock (_lock) {
            if (!_d.ContainsKey(key))
                _d.Add(key, 0);
        }
    }
}");
        var results = await _engine.FindCheckThenActOnDictionaryAsync();
        Assert.That(results, Is.Empty, "ContainsKey + Add inside a lock is safe - must not be flagged");
    }
}
