using Microsoft.Extensions.DependencyInjection.Extensions;

using RoslynSentinel.Engines.Basic;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Engine-layer DI registration for the Basic flavor. Lives in the Engines.Basic project (namespace
/// <c>Microsoft.Extensions.DependencyInjection</c> so callers need no extra using) so any project that
/// references Engines.Basic -- including test projects that cannot reference a Server project -- can
/// register the real engine graph.
/// </summary>
public static class EngineServiceCollectionExtensionsBasic
{
    /// <summary>
    /// Registers all Roslyn analysis engine singletons into the DI container.
    /// </summary>
    /// <remarks>
    /// Engines that only the Advanced tool classes need are registered by
    /// <c>AddRoslynSentinelEnginesAdvanced</c> (Engines.Advanced), which calls this method first.
    /// Don't also register one of those here -- that would register the same engine type in both flavors.
    /// </remarks>
    public static IServiceCollection AddRoslynSentinelEnginesBasic(this IServiceCollection services)
    {
        services.AddSingleton<SentinelConfiguration>();
        // Defaults to Production. TryAdd (not Add) so a host that called
        // AddRoslynSentinelHostOptions first keeps its own value -> registering unconditionally
        // here would make the last-wins order depend on which extension the host called last.
        services.TryAddSingleton(new SentinelHostOptions());
        // TryAdd only so a caller that already registered StoppedByScriptMarker (e.g., ServerStdio.cs
        // or ServerHttp.cs from a real host) is not overwritten. Test fixtures without that context
        // get this default stub.
        services.AddSingleton<BreakingChangeEngine>();
        services.AddSingleton<BuildEngine>();
        services.AddSingleton<DependencyEngine>();
        services.AddSingleton<DiagnosticEngine>();
        services.AddSingleton<DiffEngine>();
        services.AddSingleton<DiscoveryEngine>();
        services.AddSingleton<ImpactAnalyzer>();
        services.AddSingleton<InventoryEngine>();
        services.AddSingleton<ISolutionProvider>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
        services.AddSingleton<IWorkspaceManager>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
        services.AddSingleton<IWorkspaceReader>(sp => sp.GetRequiredService<PersistentWorkspaceManager>());
        services.AddSingleton<MsToolAugmentEngine>();
        services.AddSingleton<PersistentWorkspaceManager>();
        services.AddSingleton<ProjectConsistencyEngine>();
        services.AddSingleton<SolutionStructureEngine>();
        services.AddSingleton<BasicRefactoringEngine>();
        services.AddSingleton<SemanticRefactoringEngine>();
        services.AddSingleton<SemanticReplaceEngine>();
        services.AddSingleton<NamedArgumentsEngine>();
        services.AddSingleton<TestCategoryTaggingEngine>();
        services.AddSingleton<TestCategoryApplyEngine>();
        services.AddSingleton<SolutionManagementEngine>();
        services.AddSingleton<MemberRefactoringEngine>();
        services.AddSingleton<StructuralRefinementEngine>();
        services.AddSingleton<SymbolNavigationEngine>();
        services.AddSingleton<SyntaxUpgradeEngine>();
        services.AddSingleton<TestRunEngine>();
        services.AddSingleton<TestInstanceLauncher>();
        services.AddSingleton<ThreadSafetyEngine>();
        services.AddSingleton<ValidationEngine>();
        services.TryAddSingleton(new StoppedByScriptMarker(WasFound: false, Details: null));

        return services;
    }
}
