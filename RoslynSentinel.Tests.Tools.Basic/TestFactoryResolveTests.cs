// Resolve test for the shared construction factories (TestEngines / TestTools): every overload returns a non-null
// instance of exactly the right type, and ActivatorUtilities picked the intended constructor (the explicit arguments
// the caller passed are the ones the instance holds; a manager the caller passed is the one shared by every engine the
// container resolved for the parameters the caller did not pass). Replaces the signature-drift guard: a constructor
// parameter the container cannot supply, or an explicit argument no constructor takes any more, fails here by name.

using System.Reflection;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("TestFactories")]
public class TestFactoryResolveTests
{
    private PersistentWorkspaceManager _wm = null!;

    [OneTimeSetUp]
    public void CreateManager() => _wm = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

    [OneTimeTearDown]
    public void DisposeManager() => _wm?.Dispose();

    // True when any instance field (public or private, any base class) of `instance` is exactly `expected`.
    private static bool Holds(object instance, object expected)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            if (type.GetFields(flags).Any(f => ReferenceEquals(f.GetValue(instance), expected)))
            {
                return true;
            }
        }

        return false;
    }

    private static T FieldOfType<T>(object instance) where T : class
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var field = type.GetFields(flags).FirstOrDefault(f => f.GetValue(instance) is T);
            if (field != null)
            {
                return (T)field.GetValue(instance)!;
            }
        }

        throw new AssertionException($"{instance.GetType().Name} holds no field of type {typeof(T).Name}.");
    }

    [Test]
    public void PersistentWorkspaceManager_Logger()
    {
        var logger = NullLogger<IWorkspaceManager>.Instance;
        var manager = TestEngines.PersistentWorkspaceManager(logger);
        try
        {
            Assert.That(manager, Is.TypeOf<PersistentWorkspaceManager>());
            Assert.That(Holds(manager, logger), Is.True, "the explicit logger must be the one the manager holds");
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Test]
    public void SentinelConfiguration_NoArgs() =>
        Assert.That(TestEngines.SentinelConfiguration(), Is.TypeOf<SentinelConfiguration>());

    [Test]
    public void DiffEngine_NoArgs_EachCallIsAFreshInstance()
    {
        var first = TestEngines.DiffEngine();
        Assert.That(first, Is.TypeOf<DiffEngine>());
        Assert.That(TestEngines.DiffEngine(), Is.Not.SameAs(first), "factories must not share state between calls");
    }

    [Test]
    public void SymbolNavigationEngine_ManagerOnly()
    {
        var engine = TestEngines.SymbolNavigationEngine(_wm);
        Assert.That(engine, Is.TypeOf<SymbolNavigationEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void SymbolNavigationEngine_ManagerAndLogger()
    {
        var logger = NullLogger<SymbolNavigationEngine>.Instance;
        var engine = TestEngines.SymbolNavigationEngine(_wm, logger);
        Assert.That(engine, Is.TypeOf<SymbolNavigationEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
        Assert.That(Holds(engine, logger), Is.True, "the (manager, logger) constructor must be chosen");
    }

    [Test]
    public void ValidationEngine_ManagerOnly()
    {
        var engine = TestEngines.ValidationEngine(_wm);
        Assert.That(engine, Is.TypeOf<ValidationEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void ValidationEngine_ManagerDiffAndLogger()
    {
        var diff = TestEngines.DiffEngine();
        var logger = NullLogger<ValidationEngine>.Instance;
        var engine = TestEngines.ValidationEngine(_wm, diff, logger);
        Assert.That(engine, Is.TypeOf<ValidationEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
        Assert.That(Holds(engine, diff), Is.True, "the explicit DiffEngine must be the one the engine holds");
        Assert.That(Holds(engine, logger), Is.True);
    }

    [Test]
    public void StructuralRefinementEngine_ManagerOnly()
    {
        var engine = TestEngines.StructuralRefinementEngine(_wm);
        Assert.That(engine, Is.TypeOf<StructuralRefinementEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void StructuralRefinementEngine_ManagerAndConfig()
    {
        var config = TestEngines.SentinelConfiguration();
        var engine = TestEngines.StructuralRefinementEngine(_wm, config);
        Assert.That(engine, Is.TypeOf<StructuralRefinementEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
        Assert.That(Holds(engine, config), Is.True, "the explicit configuration must be the one the engine holds");
    }

    [Test]
    public void DiagnosticEngine_ManagerOnly()
    {
        var engine = TestEngines.DiagnosticEngine(_wm);
        Assert.That(engine, Is.TypeOf<DiagnosticEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void BuildEngine_ManagerOnly()
    {
        var engine = TestEngines.BuildEngine(_wm);
        Assert.That(engine, Is.TypeOf<BuildEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void BuildEngine_ManagerAndDiagnosticEngine()
    {
        var diagnostic = TestEngines.DiagnosticEngine(_wm);
        var engine = TestEngines.BuildEngine(_wm, diagnostic);
        Assert.That(engine, Is.TypeOf<BuildEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
        Assert.That(Holds(engine, diagnostic), Is.True, "the explicit DiagnosticEngine must be the one the engine holds");
    }

    [Test]
    public void SolutionManagementEngine_ManagerOnly()
    {
        var engine = TestEngines.SolutionManagementEngine(_wm);
        Assert.That(engine, Is.TypeOf<SolutionManagementEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void DependencyEngine_ManagerOnly()
    {
        var engine = TestEngines.DependencyEngine(_wm);
        Assert.That(engine, Is.TypeOf<DependencyEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void ProjectConsistencyEngine_ManagerOnly()
    {
        var engine = TestEngines.ProjectConsistencyEngine(_wm);
        Assert.That(engine, Is.TypeOf<ProjectConsistencyEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void WorkspaceReadNavigationImpl_ManagerOnly()
    {
        var impl = TestTools.WorkspaceReadNavigationImpl(_wm);
        Assert.That(impl, Is.TypeOf<WorkspaceReadNavigationImpl>());
        Assert.That(Holds(impl, _wm), Is.True);
    }

    [Test]
    public void WorkspaceReadNavigationImpl_ManagerAndLogger()
    {
        var logger = NullLogger<WorkspaceReadNavigationImpl>.Instance;
        var impl = TestTools.WorkspaceReadNavigationImpl(_wm, logger);
        Assert.That(impl, Is.TypeOf<WorkspaceReadNavigationImpl>());
        Assert.That(Holds(impl, _wm), Is.True);
        Assert.That(Holds(impl, logger), Is.True);
    }

    [Test]
    public void WorkspaceTools_ManagerOnly_EnginesShareTheCallersManager()
    {
        var tools = TestTools.WorkspaceTools(_wm);
        Assert.That(tools, Is.TypeOf<WorkspaceTools>());
        Assert.That(Holds(tools, _wm), Is.True);

        // The engines resolved for the parameters the caller did not pass must use the caller's manager, not the
        // PersistentWorkspaceManager the flat registration would otherwise build for the fresh container.
        Assert.That(Holds(FieldOfType<ValidationEngine>(tools), _wm), Is.True);
        Assert.That(Holds(FieldOfType<SymbolNavigationEngine>(tools), _wm), Is.True);
    }

    [Test]
    public void WorkspaceTools_AllFifteenArgumentsExplicit()
    {
        var config = TestEngines.SentinelConfiguration();
        var diff = TestEngines.DiffEngine();
        var validation = TestEngines.ValidationEngine(_wm, diff, NullLogger<ValidationEngine>.Instance);
        var diagnostic = TestEngines.DiagnosticEngine(_wm);
        var navigation = TestEngines.SymbolNavigationEngine(_wm, NullLogger<SymbolNavigationEngine>.Instance);
        var readNav = TestTools.WorkspaceReadNavigationImpl(_wm, NullLogger<WorkspaceReadNavigationImpl>.Instance);

        var tools = TestTools.WorkspaceTools(
            _wm, validation, diff, diagnostic, TestEngines.SolutionManagementEngine(_wm),
            TestEngines.StructuralRefinementEngine(_wm, config), TestEngines.DependencyEngine(_wm),
            TestEngines.ProjectConsistencyEngine(_wm), config, NullLogger<WorkspaceTools>.Instance,
            TestEngines.BuildEngine(_wm, diagnostic), navigation, new TestRunEngine(_wm), readNav,
            WriteToolAdviceHelper.WithAllToolsExposed());

        Assert.That(tools, Is.TypeOf<WorkspaceTools>());
        Assert.That(Holds(tools, validation), Is.True, "explicit engines must be the ones the tool class holds");
        Assert.That(Holds(tools, navigation), Is.True);
        Assert.That(Holds(tools, readNav), Is.True);
    }

    [Test]
    public void EveryFactoryMethod_IsNonGenericAndNamedAfterItsReturnType()
    {
        // The naming rule is what lets replaceConstruction map `new X(...)` to a factory; guard it for future additions.
        foreach (var factoryClass in new[] { typeof(TestEngines), typeof(TestTools) })
        {
            var factories = factoryClass.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.That(factories, Is.Not.Empty, factoryClass.Name);
            foreach (var method in factories)
            {
                Assert.That(method.IsGenericMethod, Is.False, $"{factoryClass.Name}.{method.Name} must be non-generic");
                Assert.That(method.Name, Is.EqualTo(method.ReturnType.Name), $"{factoryClass.Name}.{method.Name} must be named after its return type");
            }
        }
    }
}
