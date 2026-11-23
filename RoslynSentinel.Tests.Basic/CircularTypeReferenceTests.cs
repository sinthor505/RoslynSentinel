// Accuracy regression tests for the four improved tools.
// Each [Test] exercises exactly one detection rule -> positive (should flag) and
// negative (should NOT flag, verifying false-positive guards).
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("SolutionStructureEngine")] // sentinel:auto-category
public class CircularTypeReferenceTests
{
    private IWorkspaceManager _workspaceManager = null!;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source) => _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]));
    [Test]
    public async Task Flags_DirectCircularDependency_ViaConstructor()
    {
        SetSource(@"
public class A { public A(B b) { } }
public class B { public B(A a) { } }
");
        var results = await Battery.Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Battery.Basic.TestEngines.SentinelConfiguration()).FindCircularTypeReferencesAsync();
        Assert.That(results, Is.Not.Empty, "A depends on B and B depends on A - circular dependency");
        Assert.That(results.Any(r => r.Contains("A") && r.Contains("B")), Is.True);
    }

    [Test]
    public async Task DoesNotFlag_LinearDependency()
    {
        SetSource(@"
public class A { public A(B b) { } }
public class B { public B() { } }
");
        var results = await Battery.Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Battery.Basic.TestEngines.SentinelConfiguration()).FindCircularTypeReferencesAsync();
        Assert.That(results, Is.Empty, "A->B with B having no deps should not be circular");
    }

    [Test]
    public async Task Flags_ThreeWayCycle()
    {
        SetSource(@"
public class X { public X(Y y) { } }
public class Y { public Y(Z z) { } }
public class Z { public Z(X x) { } }
");
        var results = await Battery.Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Battery.Basic.TestEngines.SentinelConfiguration()).FindCircularTypeReferencesAsync();
        Assert.That(results, Has.Count.EqualTo(3), "X->Y->Z->X is a three-way cycle - detector reports one finding per cycle member");
        Assert.That(string.Concat(results), Does.Contain("X").And.Contain("Y").And.Contain("Z"));
    }
}
