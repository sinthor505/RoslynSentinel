// Resolve test for the Advanced construction factories (TestEnginesAdvanced / TestToolsAdvanced), same contract as
// TestFactoryResolveTests in Tests.Tools.Basic: every overload returns an instance of exactly the right type, the
// explicit arguments the caller passed are the ones the instance holds, and the manager the caller passed is the one
// the instance holds. A constructor parameter the container cannot supply (or an explicit argument no constructor takes
// any more) fails here by name.

using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("TestFactories")]
public class TestFactoryResolveAdvancedTests
{
    private PersistentWorkspaceManager _wm = null!;

    [OneTimeSetUp]
    public void CreateManager() => _wm = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

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

    private void AssertManagerOnly<T>(T instance) where T : class
    {
        Assert.That(instance, Is.TypeOf<T>());
        Assert.That(Holds(instance, _wm), Is.True, $"{typeof(T).Name} must hold the manager the caller passed");
    }

    private void AssertManagerAndConfig<T>(T instance, SentinelConfiguration config) where T : class
    {
        AssertManagerOnly(instance);
        Assert.That(Holds(instance, config), Is.True, $"{typeof(T).Name} must hold the explicit config the caller passed");
    }

    // ---- engines, manager only ----

    [Test]
    public void StructuralRefactoringEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.StructuralRefactoringEngine(_wm));

    [Test]
    public void LogicSimplificationEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.LogicSimplificationEngine(_wm));

    [Test]
    public void AntiPatternEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.AntiPatternEngine(_wm));

    [Test]
    public void AsyncOptimizationEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.AsyncOptimizationEngine(_wm));

    [Test]
    public void AsyncAnalysisEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.AsyncAnalysisEngine(_wm));

    [Test]
    public void PerformanceEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.PerformanceEngine(_wm));

    [Test]
    public void SecurityEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.SecurityEngine(_wm));

    [Test]
    public void CodeGenerationEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.CodeGenerationEngine(_wm));

    [Test]
    public void AdvancedRefactoringEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.AdvancedRefactoringEngine(_wm));

    [Test]
    public void ApiGenerationEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.ApiGenerationEngine(_wm));

    [Test]
    public void ControlFlowEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.ControlFlowEngine(_wm));

    [Test]
    public void DocumentationEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.DocumentationEngine(_wm));

    [Test]
    public void MappingEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.MappingEngine(_wm));

    [Test]
    public void DependencyInjectionEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.DependencyInjectionEngine(_wm));

    [Test]
    public void TestingEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.TestingEngine(_wm));

    [Test]
    public void MetricsEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.MetricsEngine(_wm));

    [Test]
    public void DeadCodeEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.DeadCodeEngine(_wm));

    [Test]
    public void IDEStyleEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.IDEStyleEngine(_wm));

    [Test]
    public void SecurityAndSafetyEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.SecurityAndSafetyEngine(_wm));

    [Test]
    public void CodeStyleAnalysisEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.CodeStyleAnalysisEngine(_wm));

    [Test]
    public void StackOverflowEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.StackOverflowEngine(_wm));

    [Test]
    public void HealthOrchestrationEngine_ManagerOnly() => AssertManagerOnly(TestEnginesAdvanced.HealthOrchestrationEngine(_wm));

    // ---- engines, other shapes ----

    [Test]
    public void AdvancedRefactoringEngine_ManagerAndLogger()
    {
        var logger = NullLogger<AdvancedRefactoringEngine>.Instance;
        var engine = TestEnginesAdvanced.AdvancedRefactoringEngine(_wm, logger);
        AssertManagerOnly(engine);
        Assert.That(Holds(engine, logger), Is.True, "the explicit logger must be the one the engine holds");
    }

    [Test]
    public void AntiPatternEngine_ManagerAndConfig()
    {
        var config = Basic.TestEngines.SentinelConfiguration();
        AssertManagerAndConfig(TestEnginesAdvanced.AntiPatternEngine(_wm, config), config);
    }

    [Test]
    public void DeadCodeEngine_ManagerAndConfig()
    {
        var config = Basic.TestEngines.SentinelConfiguration();
        AssertManagerAndConfig(TestEnginesAdvanced.DeadCodeEngine(_wm, config), config);
    }

    [Test]
    public void SyntaxModernizationEngine_ManagerAndConfig()
    {
        var config = Basic.TestEngines.SentinelConfiguration();
        AssertManagerAndConfig(TestEnginesAdvanced.SyntaxModernizationEngine(_wm, config), config);
    }

    [Test]
    public void CodeStyleEngine_ManagerAndConfig()
    {
        var config = Basic.TestEngines.SentinelConfiguration();
        AssertManagerAndConfig(TestEnginesAdvanced.CodeStyleEngine(_wm, config), config);
    }

    [Test]
    public void CodeHealingEngine_ManagerAndConfig()
    {
        var config = Basic.TestEngines.SentinelConfiguration();
        AssertManagerAndConfig(TestEnginesAdvanced.CodeHealingEngine(_wm, config), config);
    }

    [Test]
    public void ResourceSafetyEngine_ManagerAndConfig()
    {
        var config = Basic.TestEngines.SentinelConfiguration();
        AssertManagerAndConfig(TestEnginesAdvanced.ResourceSafetyEngine(_wm, config), config);
    }

    // ---- tools: each resolves its whole engine graph from the container, sharing the caller's manager ----

    [Test]
    public void AdvancedRefactoringTools_Manager() => AssertManagerOnly(TestToolsAdvanced.AdvancedRefactoringTools(_wm));

    [Test]
    public void GenerationTools_Manager() => AssertManagerOnly(TestToolsAdvanced.GenerationTools(_wm));

    [Test]
    public void QualityTools_Manager() => AssertManagerOnly(TestToolsAdvanced.QualityTools(_wm));

    [Test]
    public void ModernizationTools_Manager() => AssertManagerOnly(TestToolsAdvanced.ModernizationTools(_wm));

    [Test]
    public void ScanTools_Manager() => AssertManagerOnly(TestToolsAdvanced.ScanTools(_wm));

    [Test]
    public void IntelligenceTools_Manager() => AssertManagerOnly(TestToolsAdvanced.IntelligenceTools(_wm));

    [Test]
    public void AsyncifyTools_Manager() => AssertManagerOnly(TestToolsAdvanced.AsyncifyTools(_wm));

    [Test]
    public void EveryFactoryMethod_IsNonGenericAndNamedAfterItsReturnType()
    {
        // The naming rule is what lets replaceConstruction map `new X(...)` to a factory; guard it for future additions.
        foreach (var factoryClass in new[] { typeof(TestEnginesAdvanced), typeof(TestToolsAdvanced) })
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
