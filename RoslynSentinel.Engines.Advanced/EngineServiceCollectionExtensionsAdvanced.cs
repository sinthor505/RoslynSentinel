using RoslynSentinel.Engines.Advanced;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Engine-layer DI registration for the Advanced flavor. Lives in Engines.Advanced (namespace
/// <c>Microsoft.Extensions.DependencyInjection</c> so callers need no extra using) so a project that
/// references only Common / Engines.* can register the real engine graph.
/// </summary>
public static class EngineServiceCollectionExtensionsAdvanced
{
    /// <summary>
    /// Registers every engine Basic registers (via <c>AddRoslynSentinelEnginesBasic</c>, the shared base --
    /// Advanced does not maintain its own separate copy of that list) plus the additional engines only
    /// Advanced's tool classes need, and <see cref="MigrationLedger"/>. Does NOT register the LLM client
    /// (<c>AddRoslynSentinelLlmClient</c>, Server project, needs Microsoft.Extensions.Http) or the tool graph
    /// (<c>AddRoslynSentinelToolImplsAdvanced</c>, Tools.Advanced).
    /// </summary>
    public static IServiceCollection AddRoslynSentinelEnginesAdvanced(this IServiceCollection services)
    {
        services.AddRoslynSentinelEnginesBasic();

        // Advanced-only engines: not registered by Basic (either because Basic's tool classes
        // don't use them, or because they're deliberately gated to the fuller Advanced tool set).
        services.AddSingleton<AdvancedRefactoringEngine>();
        services.AddSingleton<StructuralRefactoringEngine>();
        services.AddSingleton<AntiPatternEngine>();
        services.AddSingleton<ApiGenerationEngine>();
        services.AddSingleton<AsyncBatchEngine>();
        services.AddSingleton<AsyncOptimizationEngine>();
        services.AddSingleton<AsyncAnalysisEngine>();
        services.AddSingleton<CloneDetectionEngine>();
        services.AddSingleton<CodeGenerationEngine>();
        services.AddSingleton<CodeHealingEngine>();
        services.AddSingleton<CodeStyleAnalysisEngine>();
        services.AddSingleton<CodeStyleEngine>();
        services.AddSingleton<CommentingEngine>();
        services.AddSingleton<ControlFlowEngine>();
        services.AddSingleton<DeadCodeEngine>();
        services.AddSingleton<DependencyInjectionEngine>();
        services.AddSingleton<DocumentationEngine>();
        services.AddSingleton<HealthOrchestrationEngine>();
        services.AddSingleton<IDEStyleEngine>();
        services.AddSingleton<InstrumentationEngine>();
        services.AddSingleton<LogicSimplificationEngine>();
        services.AddSingleton<MappingEngine>();
        services.AddSingleton<MetricsEngine>();
        services.AddSingleton<MigrationLedger>();
        services.AddSingleton<SyntaxModernizationEngine>();
        services.AddSingleton<OutParamRefactoringEngine>();
        services.AddSingleton<PathDrivenTestEngine>();
        services.AddSingleton<PerformanceEngine>();
        services.AddSingleton<ResourceSafetyEngine>();
        services.AddSingleton<ScopedOperationLedgerEngine>();
        services.AddSingleton<SecurityAndSafetyEngine>();
        services.AddSingleton<SecurityEngine>();
        services.AddSingleton<StackOverflowEngine>();
        services.AddSingleton<TestingEngine>();

        return services;
    }
}
