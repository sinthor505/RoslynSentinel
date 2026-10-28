// Construction factories for the Engines.Advanced classes that the Advanced test projects build by hand
// (docs/current/plans/plan_tests_support_projects_factory_rollout.md, Step 5). Same design as TestEngines in
// RoslynSentinel.Tests.Support.Basic: one static NON-GENERIC method per constructor shape, named after the type, so
// SemanticFindReplace(operation: replaceConstruction) can rewrite `new X(...)` to `TestEnginesAdvanced.X(...)`.
//
// Every factory is ActivatorUtilities.CreateInstance<T>(provider, <explicit args>): the arguments a call site passes are
// explicit (matched by runtime type) and every other constructor parameter resolves from a FRESH container built per call
// from the production registration methods, so a new constructor parameter is absorbed here once and no state is shared
// between tests. The container is not disposed (see TestEngines for why that is safe in a test process).
//
// The class is named TestEnginesAdvanced (not TestEngines) so a project that references both Support projects has no
// ambiguous type; it lives in namespace RoslynSentinel.Tests.Battery.Advanced.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

public static class TestEnginesAdvanced
{
    /// <summary>
    /// Fresh container for one factory call: logging, the Advanced engine graph (includes the Basic engines), the Basic and
    /// Advanced tool-layer registrations (ToolGraph, FailureRouter). When a workspace manager is passed it replaces the
    /// registered one for IWorkspaceManager / ISolutionProvider / IWorkspaceReader, so every engine the container resolves
    /// for the parameters the call site did NOT pass shares the caller's manager rather than a second PersistentWorkspaceManager.
    /// Shared with TestToolsAdvanced (same assembly).
    /// </summary>
    internal static ServiceProvider CreateProvider(IWorkspaceManager? workspaceManager = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRoslynSentinelEnginesAdvanced();
        services.AddRoslynSentinelToolImplsBasic();
        services.AddRoslynSentinelToolImplsAdvanced();
        if (workspaceManager != null)
        {
            // Plain AddSingleton after the flat registration: the last registration wins.
            services.AddSingleton(workspaceManager);
            services.AddSingleton<ISolutionProvider>(workspaceManager);
            if (workspaceManager is IWorkspaceReader reader)
            {
                services.AddSingleton(reader);
            }
        }

        return services.BuildServiceProvider();
    }

    public static StructuralRefactoringEngine StructuralRefactoringEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<StructuralRefactoringEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static LogicSimplificationEngine LogicSimplificationEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<LogicSimplificationEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static AntiPatternEngine AntiPatternEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<AntiPatternEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static AntiPatternEngine AntiPatternEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<AntiPatternEngine>(CreateProvider(workspaceManager), workspaceManager, config);

    public static AsyncOptimizationEngine AsyncOptimizationEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<AsyncOptimizationEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static AsyncAnalysisEngine AsyncAnalysisEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<AsyncAnalysisEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static PerformanceEngine PerformanceEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<PerformanceEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static SecurityEngine SecurityEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<SecurityEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static CodeGenerationEngine CodeGenerationEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<CodeGenerationEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static AdvancedRefactoringEngine AdvancedRefactoringEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<AdvancedRefactoringEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static AdvancedRefactoringEngine AdvancedRefactoringEngine(IWorkspaceManager workspaceManager, ILogger<AdvancedRefactoringEngine> logger, SentinelConfiguration config) =>
    ActivatorUtilities.CreateInstance<AdvancedRefactoringEngine>(CreateProvider(workspaceManager), workspaceManager, logger, config);

    public static AdvancedRefactoringEngine AdvancedRefactoringEngine(IWorkspaceManager workspaceManager, ILogger<AdvancedRefactoringEngine> logger) =>
        ActivatorUtilities.CreateInstance<AdvancedRefactoringEngine>(CreateProvider(workspaceManager), workspaceManager, logger);

    public static ApiGenerationEngine ApiGenerationEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<ApiGenerationEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static ControlFlowEngine ControlFlowEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<ControlFlowEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static DocumentationEngine DocumentationEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<DocumentationEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static MappingEngine MappingEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<MappingEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static DependencyInjectionEngine DependencyInjectionEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<DependencyInjectionEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static TestingEngine TestingEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<TestingEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static MetricsEngine MetricsEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<MetricsEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static DeadCodeEngine DeadCodeEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<DeadCodeEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static DeadCodeEngine DeadCodeEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<DeadCodeEngine>(CreateProvider(workspaceManager), workspaceManager, config);

    public static IDEStyleEngine IDEStyleEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<IDEStyleEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static SecurityAndSafetyEngine SecurityAndSafetyEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<SecurityAndSafetyEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static CodeStyleAnalysisEngine CodeStyleAnalysisEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<CodeStyleAnalysisEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static StackOverflowEngine StackOverflowEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<StackOverflowEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static SyntaxModernizationEngine SyntaxModernizationEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<SyntaxModernizationEngine>(CreateProvider(workspaceManager), workspaceManager, config);

    public static CodeStyleEngine CodeStyleEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<CodeStyleEngine>(CreateProvider(workspaceManager), workspaceManager, config);

    public static CodeHealingEngine CodeHealingEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<CodeHealingEngine>(CreateProvider(workspaceManager), workspaceManager, config);

    public static ResourceSafetyEngine ResourceSafetyEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<ResourceSafetyEngine>(CreateProvider(workspaceManager), workspaceManager, config);

    // Collapses the same 7-file inline chain (SolutionStructureEngine, config, PerformanceEngine, AntiPatternEngine, ...):
    // every other constructor parameter resolves from the container, which shares the caller's workspace manager.
    public static HealthOrchestrationEngine HealthOrchestrationEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<HealthOrchestrationEngine>(CreateProvider(workspaceManager));

    // The only constructor: all six arguments explicit (the shape the tests use), so the test's own engines and ledger are held.
    public static AsyncBatchEngine AsyncBatchEngine(
        IWorkspaceManager workspaceManager, AsyncOptimizationEngine asyncOptimizationEngine, ValidationEngine validationEngine,
        AntiPatternEngine antiPatternEngine, MigrationLedger ledger, ILogger<AsyncBatchEngine> logger) =>
        ActivatorUtilities.CreateInstance<AsyncBatchEngine>(
            CreateProvider(workspaceManager), workspaceManager, asyncOptimizationEngine, validationEngine, antiPatternEngine, ledger, logger);

    // The shape every existing call site uses (all five constructor arguments explicit, so the test's own engines are held).
    public static HealthOrchestrationEngine HealthOrchestrationEngine(
        IWorkspaceManager workspaceManager, SolutionStructureEngine solutionStructureEngine, SentinelConfiguration config,
        PerformanceEngine performanceEngine, AntiPatternEngine antiPatternEngine) =>
        ActivatorUtilities.CreateInstance<HealthOrchestrationEngine>(
            CreateProvider(workspaceManager), workspaceManager, solutionStructureEngine, config, performanceEngine, antiPatternEngine);
}
