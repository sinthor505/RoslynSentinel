using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Builds battery tool classes the same way BatteryTwentyFourTests [SetUp] does, for in-memory fixtures.
// Each call builds its engines fresh; DiffEngine must be built before any engine that takes it.
internal static class BatteryToolFactory
{
    internal static AdvancedRefactoringTools CreateAdvancedRefactoringTools(IWorkspaceManager workspaceManager)
    {
        return new AdvancedRefactoringTools(workspaceManager);
    }

    internal static GenerationTools CreateGenerationTools(IWorkspaceManager workspaceManager)
    {
        return new GenerationTools(workspaceManager, NullLogger<GenerationTools>.Instance);
    }

    internal static RefactoringExtractionDocsTools CreateRefactoringExtractionDocsTools(IWorkspaceManager workspaceManager)
    {
        var (_, symbolNavigationEngine, validationEngine, refactoringEngine, _) = BuildEngines(workspaceManager);
        var msToolAugmentEngine = new MsToolAugmentEngine(workspaceManager);
        var impl = new RefactoringExtractionDocsImpl(refactoringEngine, msToolAugmentEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringExtractionDocsImpl>.Instance);
        return new RefactoringExtractionDocsTools(impl);
    }

    internal static TypedMemberTools CreateTypedMemberTools(IWorkspaceManager workspaceManager)
    {
        var (config, symbolNavigationEngine, validationEngine, refactoringEngine, memberRefactoringEngine) = BuildEngines(workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var impl = new RefactoringStructuralImpl(refactoringEngine, memberRefactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringStructuralImpl>.Instance);
        return new TypedMemberTools(impl);
    }

    internal static RefactoringStructuralTools CreateRefactoringStructuralTools(IWorkspaceManager workspaceManager)
    {
        // Mirrors the BatteryTwentyFourTests [SetUp] wiring: DiffEngine is built before ValidationEngine, and the
        // structural impl takes the refactoring, member, structural-refinement and navigation engines.
        var (config, symbolNavigationEngine, validationEngine, refactoringEngine, memberRefactoringEngine) = BuildEngines(workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(workspaceManager, config);
        var impl = new RefactoringStructuralImpl(refactoringEngine, memberRefactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringStructuralImpl>.Instance);
        return new RefactoringStructuralTools(impl);
    }

    internal static RefactoringSignatureTools CreateRefactoringSignatureTools(IWorkspaceManager workspaceManager)
    {
        var (config, symbolNavigationEngine, validationEngine, refactoringEngine, memberRefactoringEngine) = BuildEngines(workspaceManager);
        var impl = new RefactoringSignatureImpl(refactoringEngine, memberRefactoringEngine, workspaceManager, validationEngine, symbolNavigationEngine, NullLogger<RefactoringSignatureImpl>.Instance);
        return new RefactoringSignatureTools(impl);
    }

    internal static ModernizationTools CreateModernizationTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var modernizationEngine = new SyntaxModernizationEngine(workspaceManager, config);
        var syntaxUpgradeEngine = new SyntaxUpgradeEngine(workspaceManager, config);
        var logicOptimizationEngine = new LogicSimplificationEngine(workspaceManager);
        var codeStyleEngine = new CodeStyleEngine(workspaceManager, config);
        var codeHealingEngine = new CodeHealingEngine(workspaceManager, config);
        var advancedLogicEngine = new LogicSimplificationEngine(workspaceManager);
        var ideStyleEngine = new IDEStyleEngine(workspaceManager);
        var asyncOptimizationEngine = new AsyncOptimizationEngine(workspaceManager);
        return new ModernizationTools(modernizationEngine, syntaxUpgradeEngine, logicOptimizationEngine, codeStyleEngine, codeHealingEngine, advancedLogicEngine, ideStyleEngine, asyncOptimizationEngine, workspaceManager, config, NullLogger<ModernizationTools>.Instance);
    }

    internal static QualityTools CreateQualityTools(IWorkspaceManager workspaceManager)
    {
        var testingEngine = new TestingEngine(workspaceManager);
        var controlFlowEngine = new ControlFlowEngine(workspaceManager);
        var diagnosticEngine = new DiagnosticEngine(workspaceManager);
        return new QualityTools(testingEngine, controlFlowEngine, new AntiPatternEngine(workspaceManager), new ThreadSafetyEngine(workspaceManager), diagnosticEngine, new CodeStyleAnalysisEngine(workspaceManager), new StackOverflowEngine(workspaceManager), new MsToolAugmentEngine(workspaceManager), workspaceManager, NullLogger<QualityTools>.Instance);
    }

    internal static IntelligenceTools CreateIntelligenceTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var solutionStructureEngine = new SolutionStructureEngine(workspaceManager, config);
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var discoveryEngine = new DiscoveryEngine(workspaceManager, symbolNavigationEngine);
        var impactAnalyzer = new ImpactAnalyzer(workspaceManager, NullLogger<ImpactAnalyzer>.Instance);
        var healthOrchestrationEngine = new HealthOrchestrationEngine(workspaceManager, solutionStructureEngine, config, new PerformanceEngine(workspaceManager), new AntiPatternEngine(workspaceManager, config));
        return new IntelligenceTools(impactAnalyzer, new MetricsEngine(workspaceManager), new InventoryEngine(workspaceManager), new DeadCodeEngine(workspaceManager, config), new DocumentationEngine(workspaceManager), new DependencyEngine(workspaceManager), solutionStructureEngine, new AsyncAnalysisEngine(workspaceManager), healthOrchestrationEngine, symbolNavigationEngine, new DependencyInjectionEngine(workspaceManager), discoveryEngine, new ProjectConsistencyEngine(workspaceManager), workspaceManager, new AntiPatternEngine(workspaceManager), config: config, logger: NullLogger<IntelligenceTools>.Instance);
    }

    internal static SymbolRelationshipTools CreateSymbolRelationshipTools(IWorkspaceManager workspaceManager)
    {
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var discoveryEngine = new DiscoveryEngine(workspaceManager, symbolNavigationEngine);
        return new SymbolRelationshipTools(new SymbolRelationshipImpl(discoveryEngine, symbolNavigationEngine, workspaceManager, NullLogger<SymbolRelationshipImpl>.Instance));
    }

    internal static SymbolNavigationTools CreateSymbolNavigationTools(IWorkspaceManager workspaceManager)
    {
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var impactAnalyzer = new ImpactAnalyzer(workspaceManager, NullLogger<ImpactAnalyzer>.Instance);
        return new SymbolNavigationTools(new SymbolNavigationImpl(symbolNavigationEngine, impactAnalyzer, workspaceManager, NullLogger<SymbolNavigationImpl>.Instance));
    }

    internal static ScanTools CreateScanTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var asyncSafetyEngine = new AsyncAnalysisEngine(workspaceManager);
        var deadCodeEngine = new DeadCodeEngine(workspaceManager, config);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var solutionStructureEngine = new SolutionStructureEngine(workspaceManager, config);
        var dependencyInjectionEngine = new DependencyInjectionEngine(workspaceManager);
        var metricsEngine = new MetricsEngine(workspaceManager);
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var discoveryEngine = new DiscoveryEngine(workspaceManager, symbolNavigationEngine);
        return new ScanTools(new SecurityEngine(workspaceManager), new AntiPatternEngine(workspaceManager), asyncSafetyEngine, new ThreadSafetyEngine(workspaceManager), new ControlFlowEngine(workspaceManager), new PerformanceEngine(workspaceManager), deadCodeEngine, dependencyEngine, solutionStructureEngine, dependencyInjectionEngine, new ProjectConsistencyEngine(workspaceManager), metricsEngine, new CloneDetectionEngine(workspaceManager), discoveryEngine, new StackOverflowEngine(workspaceManager), new CodeStyleEngine(workspaceManager, config), new CodeStyleAnalysisEngine(workspaceManager), new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config), symbolNavigationEngine, new BreakingChangeEngine(workspaceManager), workspaceManager, new ResourceSafetyEngine(workspaceManager), NullLogger<ScanTools>.Instance);
    }

    private static (SentinelConfiguration Config, SymbolNavigationEngine Navigation, ValidationEngine Validation, BasicRefactoringEngine Refactoring, MemberRefactoringEngine Member) BuildEngines(IWorkspaceManager workspaceManager)
    {
        // DiffEngine is built before ValidationEngine, matching the BatteryTwentyFourTests [SetUp] wiring.
        var config = new SentinelConfiguration();
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var refactoringEngine = new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var memberRefactoringEngine = new MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine);
        return (config, symbolNavigationEngine, validationEngine, refactoringEngine, memberRefactoringEngine);
    }
}
