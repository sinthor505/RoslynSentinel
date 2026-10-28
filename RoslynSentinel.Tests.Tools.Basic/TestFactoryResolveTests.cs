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
            TestEngines.BuildEngine(_wm, diagnostic), navigation, TestEngines.TestRunEngine(_wm), readNav,
            WriteToolAdviceHelper.WithAllToolsExposed());

        Assert.That(tools, Is.TypeOf<WorkspaceTools>());
        Assert.That(Holds(tools, validation), Is.True, "explicit engines must be the ones the tool class holds");
        Assert.That(Holds(tools, navigation), Is.True);
        Assert.That(Holds(tools, readNav), Is.True);
    }

    [Test]
    public void TestRunEngine_ManagerOnly()
    {
        var engine = TestEngines.TestRunEngine(_wm);
        Assert.That(engine, Is.TypeOf<TestRunEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void WholeFileWriteTools_AllSixArgumentsExplicit()
    {
        var diff = TestEngines.DiffEngine();
        var validation = TestEngines.ValidationEngine(_wm, diff, NullLogger<ValidationEngine>.Instance);
        var navigation = TestEngines.SymbolNavigationEngine(_wm, NullLogger<SymbolNavigationEngine>.Instance);
        var workspaceTools = TestTools.WorkspaceTools(_wm);
        var logger = NullLogger<WholeFileWriteTools>.Instance;

        var tools = TestTools.WholeFileWriteTools(_wm, workspaceTools, validation, diff, logger, navigation);

        Assert.That(tools, Is.TypeOf<WholeFileWriteTools>());
        Assert.That(Holds(tools, _wm), Is.True);
        Assert.That(Holds(tools, workspaceTools), Is.True, "the explicit WorkspaceTools must be the one the tool class holds");
        Assert.That(Holds(tools, validation), Is.True, "explicit engines must be the ones the tool class holds");
        Assert.That(Holds(tools, diff), Is.True);
        Assert.That(Holds(tools, logger), Is.True);
        Assert.That(Holds(tools, navigation), Is.True);
    }

    [Test]
    public void BasicRefactoringEngine_ManagerLoggerAndConfig()
    {
        var logger = NullLogger<BasicRefactoringEngine>.Instance;
        var config = TestEngines.SentinelConfiguration();
        var engine = TestEngines.BasicRefactoringEngine(_wm, logger, config);
        Assert.That(engine, Is.TypeOf<BasicRefactoringEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
        Assert.That(Holds(engine, logger), Is.True, "the (manager, logger, config) constructor must be chosen");
        Assert.That(Holds(engine, config), Is.True, "the explicit configuration must be the one the engine holds");
    }

    [Test]
    public void MemberRefactoringEngine_ManagerNavigationAndValidation()
    {
        var navigation = TestEngines.SymbolNavigationEngine(_wm);
        var validation = TestEngines.ValidationEngine(_wm);
        var engine = TestEngines.MemberRefactoringEngine(_wm, navigation, validation);
        Assert.That(engine, Is.TypeOf<MemberRefactoringEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
        Assert.That(Holds(engine, navigation), Is.True, "the explicit navigation engine must be the one the engine holds");
        Assert.That(Holds(engine, validation), Is.True, "the explicit validation engine must be the one the engine holds");
    }

    [Test]
    public void MemberRefactoringEngine_ManagerNavigationValidationAndConfig()
    {
        var navigation = TestEngines.SymbolNavigationEngine(_wm);
        var validation = TestEngines.ValidationEngine(_wm);
        var config = TestEngines.SentinelConfiguration();
        var engine = TestEngines.MemberRefactoringEngine(_wm, navigation, validation, config);
        Assert.That(engine, Is.TypeOf<MemberRefactoringEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
        Assert.That(Holds(engine, navigation), Is.True);
        Assert.That(Holds(engine, validation), Is.True);
        Assert.That(Holds(engine, config), Is.True, "the explicit configuration must be the one the engine holds");
    }

    [Test]
    public void MsToolAugmentEngine_ManagerOnly()
    {
        var engine = TestEngines.MsToolAugmentEngine(_wm);
        Assert.That(engine, Is.TypeOf<MsToolAugmentEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void DiscoveryEngine_ManagerOnly()
    {
        var engine = TestEngines.DiscoveryEngine(_wm);
        Assert.That(engine, Is.TypeOf<DiscoveryEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void WorkspaceHealthMiscImpl_ManagerOnly()
    {
        var impl = TestTools.WorkspaceHealthMiscImpl(_wm);
        Assert.That(impl, Is.TypeOf<WorkspaceHealthMiscImpl>());
        Assert.That(Holds(impl, _wm), Is.True);
    }

    [Test]
    public void WorkspaceHealthMiscImpl_ManagerAndConfig()
    {
        var config = TestEngines.SentinelConfiguration();
        var impl = TestTools.WorkspaceHealthMiscImpl(_wm, config);
        Assert.That(impl, Is.TypeOf<WorkspaceHealthMiscImpl>());
        Assert.That(Holds(impl, _wm), Is.True);
        Assert.That(Holds(impl, config), Is.True, "the explicit configuration must be the one the impl holds");
    }

    [Test]
    public void RefactoringStructuralImpl_AllSevenArgumentsExplicit()
    {
        var config = TestEngines.SentinelConfiguration();
        var diff = TestEngines.DiffEngine();
        var validation = TestEngines.ValidationEngine(_wm, diff, NullLogger<ValidationEngine>.Instance);
        var navigation = TestEngines.SymbolNavigationEngine(_wm, NullLogger<SymbolNavigationEngine>.Instance);
        var refactoring = TestEngines.BasicRefactoringEngine(_wm, NullLogger<BasicRefactoringEngine>.Instance, config);
        var member = TestEngines.MemberRefactoringEngine(_wm, navigation, validation, config);
        var structural = TestEngines.StructuralRefinementEngine(_wm, config);
        var logger = NullLogger.Instance;

        var impl = TestTools.RefactoringStructuralImpl(refactoring, member, structural, navigation, _wm, validation, logger);

        Assert.That(impl, Is.TypeOf<RefactoringStructuralImpl>());
        Assert.That(Holds(impl, _wm), Is.True);
        Assert.That(Holds(impl, refactoring), Is.True, "explicit engines must be the ones the impl holds");
        Assert.That(Holds(impl, member), Is.True);
        Assert.That(Holds(impl, structural), Is.True);
        Assert.That(Holds(impl, navigation), Is.True);
        Assert.That(Holds(impl, validation), Is.True);
        Assert.That(Holds(impl, logger), Is.True);
    }

    [Test]
    public void RefactoringStructuralTools_Impl()
    {
        var config = TestEngines.SentinelConfiguration();
        var validation = TestEngines.ValidationEngine(_wm);
        var navigation = TestEngines.SymbolNavigationEngine(_wm);
        var impl = TestTools.RefactoringStructuralImpl(
            TestEngines.BasicRefactoringEngine(_wm, NullLogger<BasicRefactoringEngine>.Instance, config),
            TestEngines.MemberRefactoringEngine(_wm, navigation, validation, config),
            TestEngines.StructuralRefinementEngine(_wm, config), navigation, _wm, validation, NullLogger.Instance);

        var tools = TestTools.RefactoringStructuralTools(impl);

        Assert.That(tools, Is.TypeOf<RefactoringStructuralTools>());
        Assert.That(Holds(tools, impl), Is.True, "the explicit impl must be the one the tool class holds");
    }

    [Test]
    public void TestCategoryTaggingTools_Impl()
    {
        var impl = TestTools.TestCategoryTaggingImpl(
            TestEngines.TestCategoryTaggingEngine(_wm),
            TestEngines.TestCategoryApplyEngine(_wm, TestEngines.ValidationEngine(_wm)),
            _wm,
            NullLogger.Instance);

        var tools = TestTools.TestCategoryTaggingTools(impl);

        Assert.That(tools, Is.TypeOf<TestCategoryTaggingTools>());
        Assert.That(Holds(tools, impl), Is.True, "the explicit impl must be the one the tool class holds");
    }

    [Test]
    public void TestCategoryTaggingEngine_ManagerOnly()
    {
        var engine = TestEngines.TestCategoryTaggingEngine(_wm);
        Assert.That(engine, Is.TypeOf<TestCategoryTaggingEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
    }

    [Test]
    public void TestCategoryApplyEngine_ManagerAndValidation()
    {
        var validation = TestEngines.ValidationEngine(_wm);
        var engine = TestEngines.TestCategoryApplyEngine(_wm, validation);
        Assert.That(engine, Is.TypeOf<TestCategoryApplyEngine>());
        Assert.That(Holds(engine, _wm), Is.True);
        Assert.That(Holds(engine, validation), Is.True, "the explicit validation engine must be the one the engine holds");
    }

    [Test]
    public void TestCategoryTaggingImpl_FourArgumentsExplicit()
    {
        var planner = TestEngines.TestCategoryTaggingEngine(_wm);
        var applier = TestEngines.TestCategoryApplyEngine(_wm, TestEngines.ValidationEngine(_wm));
        var logger = NullLogger.Instance;

        var impl = TestTools.TestCategoryTaggingImpl(planner, applier, _wm, logger);

        Assert.That(impl, Is.TypeOf<TestCategoryTaggingImpl>());
        Assert.That(Holds(impl, planner), Is.True, "explicit engines must be the ones the impl holds");
        Assert.That(Holds(impl, applier), Is.True);
        Assert.That(Holds(impl, _wm), Is.True);
        Assert.That(Holds(impl, logger), Is.True);
    }

    [Test]
    public void SymbolRelationshipImpl_FourArgumentsExplicit()
    {
        var discovery = TestEngines.DiscoveryEngine(_wm);
        var navigation = TestEngines.SymbolNavigationEngine(_wm, NullLogger<SymbolNavigationEngine>.Instance);
        var logger = NullLogger<SymbolRelationshipImpl>.Instance;

        var impl = TestTools.SymbolRelationshipImpl(discovery, navigation, _wm, logger);

        Assert.That(impl, Is.TypeOf<SymbolRelationshipImpl>());
        Assert.That(Holds(impl, discovery), Is.True, "explicit engines must be the ones the impl holds");
        Assert.That(Holds(impl, navigation), Is.True);
        Assert.That(Holds(impl, _wm), Is.True);
        Assert.That(Holds(impl, logger), Is.True);
    }

    [Test]
    public void RefactoringStructuralImpl_ManagerOnly_EnginesShareTheCallersManager()
    {
        var impl = TestTools.RefactoringStructuralImpl(_wm);
        Assert.That(impl, Is.TypeOf<RefactoringStructuralImpl>());
        Assert.That(Holds(impl, _wm), Is.True);
        Assert.That(Holds(FieldOfType<ValidationEngine>(impl), _wm), Is.True);
        Assert.That(Holds(FieldOfType<SymbolNavigationEngine>(impl), _wm), Is.True);
    }

    [Test]
    public void RefactoringStructuralTools_ManagerOnly_HoldsAnImplOverTheCallersManager()
    {
        var tools = TestTools.RefactoringStructuralTools(_wm);
        Assert.That(tools, Is.TypeOf<RefactoringStructuralTools>());
        Assert.That(Holds(FieldOfType<RefactoringStructuralImpl>(tools), _wm), Is.True);
    }

    [Test]
    public void RefactoringExtractionDocsImpl_ManagerOnly()
    {
        var impl = TestTools.RefactoringExtractionDocsImpl(_wm);
        Assert.That(impl, Is.TypeOf<RefactoringExtractionDocsImpl>());
        Assert.That(Holds(impl, _wm), Is.True);
        Assert.That(Holds(FieldOfType<ValidationEngine>(impl), _wm), Is.True);
    }

    [Test]
    public void RefactoringSignatureImpl_ManagerOnly()
    {
        var impl = TestTools.RefactoringSignatureImpl(_wm);
        Assert.That(impl, Is.TypeOf<RefactoringSignatureImpl>());
        Assert.That(Holds(impl, _wm), Is.True);
        Assert.That(Holds(FieldOfType<ValidationEngine>(impl), _wm), Is.True);
    }

    [Test]
    public void WholeFileWriteTools_ManagerAndWorkspaceTools()
    {
        var workspaceTools = TestTools.WorkspaceTools(_wm);
        var tools = TestTools.WholeFileWriteTools(_wm, workspaceTools);
        Assert.That(tools, Is.TypeOf<WholeFileWriteTools>());
        Assert.That(Holds(tools, _wm), Is.True);
        Assert.That(Holds(tools, workspaceTools), Is.True, "the explicit WorkspaceTools must be the one the tool class holds");
        Assert.That(Holds(FieldOfType<ValidationEngine>(tools), _wm), Is.True);
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
