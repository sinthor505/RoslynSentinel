// Construction factories for the engine classes that Tests.Tools.Basic builds by hand (pilot:
// docs/current/plans/plan_test_construction_factories_pilot.md). One static NON-GENERIC method per class, named after it,
// so SemanticFindReplace(operation: replaceConstruction) can rewrite `new X(...)` to `TestEngines.X(...)`.
//
// Every factory is ActivatorUtilities.CreateInstance<T>(provider, <explicit args>): the arguments a call site passes are
// explicit (matched by runtime type) and every other constructor parameter resolves from a FRESH container built per call
// from the production registration methods, so a new constructor parameter is absorbed here once and no state is shared
// between tests. The container is not disposed: ActivatorUtilities does not track the instance it creates, and what the
// container itself resolves (default engines, loggers) holds nothing that needs disposing in a test process.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

public static class TestEngines
{
    /// <summary>
    /// Fresh container for one factory call. When a workspace manager is passed it replaces the registered one for
    /// IWorkspaceManager / ISolutionProvider / IWorkspaceReader, so every engine the container resolves for the
    /// parameters the call site did NOT pass shares the caller's manager rather than a second PersistentWorkspaceManager.
    /// </summary>
    internal static ServiceProvider CreateProvider(IWorkspaceManager? workspaceManager = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRoslynSentinelEnginesBasic();
        services.AddRoslynSentinelToolImplsBasic();
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

    public static PersistentWorkspaceManager PersistentWorkspaceManager(ILogger<IWorkspaceManager> logger) =>
        ActivatorUtilities.CreateInstance<PersistentWorkspaceManager>(CreateProvider(), logger);

    public static SentinelConfiguration SentinelConfiguration() =>
        ActivatorUtilities.CreateInstance<SentinelConfiguration>(CreateProvider());

    public static DiffEngine DiffEngine() =>
        ActivatorUtilities.CreateInstance<DiffEngine>(CreateProvider());

    public static SymbolNavigationEngine SymbolNavigationEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<SymbolNavigationEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static SymbolNavigationEngine SymbolNavigationEngine(IWorkspaceManager workspaceManager, ILogger<SymbolNavigationEngine> logger) =>
        ActivatorUtilities.CreateInstance<SymbolNavigationEngine>(CreateProvider(workspaceManager), workspaceManager, logger);

    public static ValidationEngine ValidationEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<ValidationEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static ValidationEngine ValidationEngine(IWorkspaceManager workspaceManager, DiffEngine diffEngine, ILogger<ValidationEngine> logger) =>
        ActivatorUtilities.CreateInstance<ValidationEngine>(CreateProvider(workspaceManager), workspaceManager, diffEngine, logger);

    public static StructuralRefinementEngine StructuralRefinementEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<StructuralRefinementEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static StructuralRefinementEngine StructuralRefinementEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<StructuralRefinementEngine>(CreateProvider(workspaceManager), workspaceManager, config);

    public static DiagnosticEngine DiagnosticEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<DiagnosticEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static BuildEngine BuildEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<BuildEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static BuildEngine BuildEngine(IWorkspaceManager workspaceManager, DiagnosticEngine diagnosticEngine) =>
        ActivatorUtilities.CreateInstance<BuildEngine>(CreateProvider(workspaceManager), workspaceManager, diagnosticEngine);

    public static SolutionManagementEngine SolutionManagementEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<SolutionManagementEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static DependencyEngine DependencyEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<DependencyEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static ProjectConsistencyEngine ProjectConsistencyEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<ProjectConsistencyEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static TestRunEngine TestRunEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<TestRunEngine>(CreateProvider(workspaceManager), workspaceManager);

    public static BasicRefactoringEngine BasicRefactoringEngine(IWorkspaceManager workspaceManager, ILogger<BasicRefactoringEngine> logger, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<BasicRefactoringEngine>(CreateProvider(workspaceManager), workspaceManager, logger, config);

    public static MemberRefactoringEngine MemberRefactoringEngine(IWorkspaceManager workspaceManager, SymbolNavigationEngine symbolNavigationEngine, ValidationEngine validationEngine) =>
        ActivatorUtilities.CreateInstance<MemberRefactoringEngine>(CreateProvider(workspaceManager), workspaceManager, symbolNavigationEngine, validationEngine);

    public static MemberRefactoringEngine MemberRefactoringEngine(IWorkspaceManager workspaceManager, SymbolNavigationEngine symbolNavigationEngine, ValidationEngine validationEngine, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<MemberRefactoringEngine>(CreateProvider(workspaceManager), workspaceManager, symbolNavigationEngine, validationEngine, config);

    public static MsToolAugmentEngine MsToolAugmentEngine(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<MsToolAugmentEngine>(CreateProvider(workspaceManager), workspaceManager);
}
