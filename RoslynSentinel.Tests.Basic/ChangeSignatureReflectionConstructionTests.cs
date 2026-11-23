using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// ChangeSignature on a constructor reports, and never rewrites, reflective construction calls that name the type
/// (ActivatorUtilities.CreateInstance / CreateFactory, System.Activator.CreateInstance with arguments). Method changes report nothing.
/// </summary>
[TestFixture]
public class ChangeSignatureReflectionConstructionTests
{
    private const string SvcSource = """
        class Svc { public Svc(int a, string b) {} public void Run(int a, int b) {} }
        class Other { public Other(int a, string b) {} }
        """;

    // Lines: 1 stub, 2 blank, 3 class, 4 brace, 5 method, 6 brace, 7 Svc generic CreateInstance, 8 CreateFactory,
    // 9 Activator with args, 10 Other generic CreateInstance, 11 Activator(Type, bool) (no args overload, not reported).
    private const string UseSource = """
        using Microsoft.Extensions.DependencyInjection; namespace Microsoft.Extensions.DependencyInjection { public static class ActivatorUtilities { public static T CreateInstance<T>(System.IServiceProvider p, params object[] a) => default!; public static object CreateInstance(System.IServiceProvider p, System.Type t, params object[] a) => null!; public static object CreateFactory(System.Type t, System.Type[] a) => null!; } }

        class Use
        {
            public void Build(System.IServiceProvider sp)
            {
                var a = ActivatorUtilities.CreateInstance<Svc>(sp, 1, "x");
                var f = ActivatorUtilities.CreateFactory(typeof(Svc), new[] { typeof(int) });
                var r = System.Activator.CreateInstance(typeof(Svc), 1, "x");
                var o = ActivatorUtilities.CreateInstance<Other>(sp, 1, "x");
                var q = System.Activator.CreateInstance(typeof(Svc), true);
            }
        }
        """;

    private IWorkspaceManager _workspaceManager = null!;
    private BasicRefactoringEngine _refactoringEngine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        _refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ConstructorReorder_ReportsReflectiveCallsAtTheirLines_WithoutRewritingThem()
    {
        SetFiles(("Svc.cs", SvcSource), ("Use.cs", UseSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Svc",
            new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) });

        Assert.That(result.Error, Is.Null, result.Error);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain("public Svc(string b, int a)"), "The constructor parameters are reordered");
        Assert.That(result.Changes.Keys.Select(k => k.ToString()).Any(k => k.Contains("Use.cs")), Is.False, "The reflective call sites are never rewritten");

        Assert.That(result.ReflectiveConstructionSites, Is.Not.Null);
        var sites = result.ReflectiveConstructionSites!;
        Assert.That(sites, Has.Count.EqualTo(3), "Svc is named by three reflective calls: the generic CreateInstance, CreateFactory and Activator with args");
        Assert.That(sites.Select(s => s.LineNumber).ToList(), Is.EqualTo(new[] { 7, 8, 9 }), "Reported at the 1-based lines of the three calls");
        Assert.That(sites.All(s => s.FilePath.ToString().Contains("Use.cs")), Is.True, "Every reflective site is in Use.cs");
        Assert.That(sites[0].Reason, Does.StartWith("ActivatorUtilities.CreateInstance names 'Svc'"));
        Assert.That(sites[1].Reason, Does.StartWith("ActivatorUtilities.CreateFactory names 'Svc'"));
        Assert.That(sites[2].Reason, Does.StartWith("Activator.CreateInstance names 'Svc'"));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task MethodChange_ReflectiveConstructionSitesIsNull()
    {
        SetFiles(("Svc.cs", SvcSource), ("Use.cs", UseSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Run",
            new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) });

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Changes["Svc.cs"], Does.Contain("public void Run(int b, int a)"));
        Assert.That(result.ReflectiveConstructionSites, Is.Null, "Only a constructor change reports reflective construction sites");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task OtherConstructorChange_ReportsOnlyTheCreateInstanceOfOther()
    {
        SetFiles(("Svc.cs", SvcSource), ("Use.cs", UseSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Svc.cs", "Other",
            new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) });

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.Changes["Svc.cs"], Does.Contain("public Other(string b, int a)"));
        Assert.That(result.ReflectiveConstructionSites, Is.Not.Null);
        var sites = result.ReflectiveConstructionSites!;
        Assert.That(sites, Has.Count.EqualTo(1), "Only ActivatorUtilities.CreateInstance<Other> names Other");
        Assert.That(sites[0].LineNumber, Is.EqualTo(10));
        Assert.That(sites[0].Reason, Does.StartWith("ActivatorUtilities.CreateInstance names 'Other'"));
    }
}
