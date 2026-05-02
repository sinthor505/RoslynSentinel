using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using RoslynSentinel.Server;
using System.Reflection;

namespace RoslynSentinel.Tests;

[TestFixture]
public class DependencyInjectionTests
{
    private IServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();

        // 1. Mock Logger
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // 2. Register all engines (Copying logic from Program.cs)
        services.AddSingleton<SentinelConfiguration>(); // <--- Added missing dependency
        services.AddSingleton<PersistentWorkspaceManager>();
        services.AddSingleton<DiffEngine>();
        services.AddSingleton<ValidationEngine>();
        services.AddSingleton<ImpactAnalyzer>();
        services.AddSingleton<RefactoringEngine>();
        services.AddSingleton<MetricsEngine>();
        services.AddSingleton<CodeHealingEngine>();
        services.AddSingleton<AnalysisEngine>();
        services.AddSingleton<PerformanceEngine>();
        services.AddSingleton<SecurityEngine>();
        services.AddSingleton<TestingEngine>();
        services.AddSingleton<CodeGenerationEngine>();
        services.AddSingleton<ModernizationEngine>();
        services.AddSingleton<DependencyInjectionEngine>();
        services.AddSingleton<ThreadSafetyEngine>();
        services.AddSingleton<ArchitecturalEngine>();
        services.AddSingleton<AdvancedRefactoringEngine>();
        services.AddSingleton<DocumentationEngine>();
        services.AddSingleton<SecurityAndSafetyEngine>();
        services.AddSingleton<ApiIntegrationEngine>();
        services.AddSingleton<InventoryEngine>();
        services.AddSingleton<AsyncOptimizationEngine>();
        services.AddSingleton<InstrumentationEngine>();
        services.AddSingleton<AdvancedTypeEngine>();
        services.AddSingleton<ModernLoggingEngine>();
        services.AddSingleton<CodeFlowEngine>();
        services.AddSingleton<StructuralRefinementEngine>();
        services.AddSingleton<LogicOptimizationEngine>();
        services.AddSingleton<SemanticSearchEngine>();
        services.AddSingleton<ModernizationUpgradeEngine>();
        services.AddSingleton<AsyncSafetyEngine>();
        services.AddSingleton<ProjectStructureEngine>();
        services.AddSingleton<DeadCodeEngine>();
        services.AddSingleton<SyntaxUpgradeEngine>();
        services.AddSingleton<RefinementEngine>();
        services.AddSingleton<DiagnosticEngine>();
        services.AddSingleton<SolutionManagementEngine>();
        services.AddSingleton<MappingEngine>();
        services.AddSingleton<IDEStyleEngine>();
        services.AddSingleton<StandardRefactoringEngine>();
        services.AddSingleton<ImmutabilityEngine>();
        services.AddSingleton<CodeStyleEngine>();
        services.AddSingleton<DependencyEngine>();
        services.AddSingleton<AdvancedLogicEngine>();
        services.AddSingleton<AdvancedStructuralEngine>();
        services.AddSingleton<SemanticRefactoringLibrary>();
        services.AddSingleton<GranularRefactoringEngine>();
        services.AddSingleton<ApiAutomationEngine>();
        services.AddSingleton<ControlFlowEngine>();
        services.AddSingleton<HealthOrchestrationEngine>();
        services.AddSingleton<SymbolNavigationEngine>();
        services.AddSingleton<AntiPatternEngine>();
        services.AddSingleton<DiscoveryEngine>();
        services.AddSingleton<MsToolAugmentEngine>();

        // 3. Register all tool classes
        services.AddSingleton<SentinelWorkspaceTools>();
        services.AddSingleton<SentinelIntelligenceTools>();
        services.AddSingleton<SentinelRefactoringTools>();
        services.AddSingleton<SentinelModernizationTools>();
        services.AddSingleton<SentinelQualityTools>();
        services.AddSingleton<SentinelGenerationTools>();
        services.AddSingleton<SentinelAugmentTools>();

        _serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    [Test]
    public void AllMcpTools_ShouldBeResolvable()
    {
        // Act & Assert
        var workspaceTools = _serviceProvider.GetService<SentinelWorkspaceTools>();
        Assert.That(workspaceTools, Is.Not.Null);

        var refactoringTools = _serviceProvider.GetService<SentinelRefactoringTools>();
        Assert.That(refactoringTools, Is.Not.Null, "Failed to resolve SentinelRefactoringTools. Check constructor dependencies.");
    }

    [Test]
    public void DynamicDiscovery_AllClassesWithToolAttribute_ShouldBeResolvable()
    {
        var assembly = typeof(SentinelWorkspaceTools).Assembly;
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null);

        foreach (var type in toolTypes)
        {
            var instance = _serviceProvider.GetService(type);
            Assert.That(instance, Is.Not.Null, $"Dynamically discovered tool {type.Name} is not registered in the DI container.");
        }
    }
}
