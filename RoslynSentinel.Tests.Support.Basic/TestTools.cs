// Construction factories for the Tools.Basic classes that Tests.Tools.Basic builds by hand; the engine counterpart is
// TestEngines (see its header for the ActivatorUtilities + fresh-container design). The workspace manager is always a
// parameter, never created here, so several constructions in one test keep sharing the manager the test owns.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

public static class TestTools
{
    // TODO The other tool classes (DocumentationTools, ...) have no factory yet; add them when their slice is migrated.

    public static WorkspaceReadNavigationImpl WorkspaceReadNavigationImpl(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<WorkspaceReadNavigationImpl>(TestEngines.CreateProvider(workspaceManager), workspaceManager);

    public static WorkspaceReadNavigationImpl WorkspaceReadNavigationImpl(IWorkspaceManager workspaceManager, ILogger<WorkspaceReadNavigationImpl> logger) =>
        ActivatorUtilities.CreateInstance<WorkspaceReadNavigationImpl>(TestEngines.CreateProvider(workspaceManager), workspaceManager, logger);

    /// <summary>
    /// Short form: only the workspace manager is explicit; the engines and helpers the 15-parameter constructor needs
    /// resolve from the fresh container, which shares the caller's manager (see TestEngines.CreateProvider).
    /// </summary>
    public static WorkspaceTools WorkspaceTools(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<WorkspaceTools>(TestEngines.CreateProvider(workspaceManager), workspaceManager);

    /// <summary>
    /// The shape the BuildTools-style helpers use: all 15 constructor arguments explicit, so the test's own engines are
    /// the ones the tool class holds.
    /// </summary>
    public static WorkspaceTools WorkspaceTools(
        IWorkspaceManager workspaceManager, ValidationEngine validationEngine, DiffEngine diffEngine,
        DiagnosticEngine diagnosticEngine, SolutionManagementEngine solutionManagementEngine,
        StructuralRefinementEngine structuralRefinementEngine, DependencyEngine dependencyEngine,
        ProjectConsistencyEngine projectConsistencyEngine, SentinelConfiguration config, ILogger<WorkspaceTools> logger,
        BuildEngine buildEngine, SymbolNavigationEngine symbolNavigationEngine, TestRunEngine testRunEngine,
        WorkspaceReadNavigationImpl readNav, WriteToolAdviceHelper writeAdvice) =>
        ActivatorUtilities.CreateInstance<WorkspaceTools>(
            TestEngines.CreateProvider(workspaceManager),
            workspaceManager, validationEngine, diffEngine, diagnosticEngine, solutionManagementEngine,
            structuralRefinementEngine, dependencyEngine, projectConsistencyEngine, config, logger, buildEngine,
            symbolNavigationEngine, testRunEngine, readNav, writeAdvice);

    /// <summary>
    /// The only shape the tests use: all six constructor arguments explicit, so the test's own tool class and engines
    /// are the ones WholeFileWriteTools holds.
    /// </summary>
    public static WholeFileWriteTools WholeFileWriteTools(
        IWorkspaceManager workspaceManager, WorkspaceTools workspaceTools, ValidationEngine validationEngine,
        DiffEngine diffEngine, ILogger<WholeFileWriteTools> logger, SymbolNavigationEngine symbolNavigationEngine) =>
        ActivatorUtilities.CreateInstance<WholeFileWriteTools>(
            TestEngines.CreateProvider(workspaceManager),
            workspaceManager, workspaceTools, validationEngine, diffEngine, logger, symbolNavigationEngine);

    public static WorkspaceHealthMiscImpl WorkspaceHealthMiscImpl(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<WorkspaceHealthMiscImpl>(TestEngines.CreateProvider(workspaceManager), workspaceManager);

    public static WorkspaceHealthMiscImpl WorkspaceHealthMiscImpl(IWorkspaceManager workspaceManager, SentinelConfiguration config) =>
        ActivatorUtilities.CreateInstance<WorkspaceHealthMiscImpl>(TestEngines.CreateProvider(workspaceManager), workspaceManager, config);

    /// <summary>
    /// All seven constructor arguments explicit (the only shape the tests use); the manager is the fifth argument.
    /// </summary>
    public static RefactoringStructuralImpl RefactoringStructuralImpl(
        BasicRefactoringEngine refactoringEngine, MemberRefactoringEngine memberRefactoringEngine,
        StructuralRefinementEngine structuralRefinementEngine, SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager, ValidationEngine validationEngine, ILogger logger) =>
        ActivatorUtilities.CreateInstance<RefactoringStructuralImpl>(
            TestEngines.CreateProvider(workspaceManager),
            refactoringEngine, memberRefactoringEngine, structuralRefinementEngine, symbolNavigationEngine,
            workspaceManager, validationEngine, logger);

    public static RefactoringStructuralTools RefactoringStructuralTools(RefactoringStructuralImpl impl) =>
        ActivatorUtilities.CreateInstance<RefactoringStructuralTools>(TestEngines.CreateProvider(), impl);

    public static TestCategoryTaggingTools TestCategoryTaggingTools(TestCategoryTaggingImpl impl) =>
        ActivatorUtilities.CreateInstance<TestCategoryTaggingTools>(TestEngines.CreateProvider(), impl);
}
