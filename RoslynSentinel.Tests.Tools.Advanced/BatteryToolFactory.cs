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
        return Battery.Advanced.TestToolsAdvanced.AdvancedRefactoringTools(workspaceManager);
    }

    internal static GenerationTools CreateGenerationTools(IWorkspaceManager workspaceManager)
    {
        return Battery.Advanced.TestToolsAdvanced.GenerationTools(workspaceManager);
    }

    internal static RefactoringExtractionDocsTools CreateRefactoringExtractionDocsTools(IWorkspaceManager workspaceManager)
    {
        var (_, symbolNavigationEngine, validationEngine, refactoringEngine, _) = BuildEngines(workspaceManager);
        var msToolAugmentEngine = Battery.Basic.TestEngines.MsToolAugmentEngine(workspaceManager);
        var impl = new RefactoringExtractionDocsImpl(refactoringEngine, msToolAugmentEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringExtractionDocsImpl>.Instance);
        return new RefactoringExtractionDocsTools(impl);
    }

    internal static TypedMemberTools CreateTypedMemberTools(IWorkspaceManager workspaceManager)
    {
        var (config, symbolNavigationEngine, validationEngine, refactoringEngine, memberRefactoringEngine) = BuildEngines(workspaceManager);
        var structuralRefinementEngine = Battery.Basic.TestEngines.StructuralRefinementEngine(workspaceManager, config);
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(refactoringEngine, memberRefactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringStructuralImpl>.Instance);
        return new TypedMemberTools(impl);
    }

    internal static RefactoringStructuralTools CreateRefactoringStructuralTools(IWorkspaceManager workspaceManager)
    {
        // Mirrors the BatteryTwentyFourTests [SetUp] wiring: DiffEngine is built before ValidationEngine, and the
        // structural impl takes the refactoring, member, structural-refinement and navigation engines.
        var (config, symbolNavigationEngine, validationEngine, refactoringEngine, memberRefactoringEngine) = BuildEngines(workspaceManager);
        var structuralRefinementEngine = Battery.Basic.TestEngines.StructuralRefinementEngine(workspaceManager, config);
        var impl = Battery.Basic.TestTools.RefactoringStructuralImpl(refactoringEngine, memberRefactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, NullLogger<RefactoringStructuralImpl>.Instance);
        return Battery.Basic.TestTools.RefactoringStructuralTools(impl);
    }

    internal static RefactoringSignatureTools CreateRefactoringSignatureTools(IWorkspaceManager workspaceManager)
    {
        var (config, symbolNavigationEngine, validationEngine, refactoringEngine, memberRefactoringEngine) = BuildEngines(workspaceManager);
        var impl = new RefactoringSignatureImpl(refactoringEngine, memberRefactoringEngine, workspaceManager, validationEngine, symbolNavigationEngine, NullLogger<RefactoringSignatureImpl>.Instance);
        return new RefactoringSignatureTools(impl);
    }

    internal static ModernizationTools CreateModernizationTools(IWorkspaceManager workspaceManager)
    {
        return Battery.Advanced.TestToolsAdvanced.ModernizationTools(workspaceManager);
    }

    internal static QualityTools CreateQualityTools(IWorkspaceManager workspaceManager)
    {
        return Battery.Advanced.TestToolsAdvanced.QualityTools(workspaceManager);
    }

    internal static IntelligenceTools CreateIntelligenceTools(IWorkspaceManager workspaceManager)
    {
        return Battery.Advanced.TestToolsAdvanced.IntelligenceTools(workspaceManager);
    }

    internal static SymbolRelationshipTools CreateSymbolRelationshipTools(IWorkspaceManager workspaceManager)
    {
        var symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var discoveryEngine = Battery.Basic.TestEngines.DiscoveryEngine(workspaceManager, symbolNavigationEngine);
        return new SymbolRelationshipTools(Battery.Basic.TestTools.SymbolRelationshipImpl(discoveryEngine, symbolNavigationEngine, workspaceManager, NullLogger<SymbolRelationshipImpl>.Instance));
    }

    internal static SymbolNavigationTools CreateSymbolNavigationTools(IWorkspaceManager workspaceManager)
    {
        var symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var impactAnalyzer = Battery.Basic.TestEngines.ImpactAnalyzer(workspaceManager, NullLogger<ImpactAnalyzer>.Instance);
        return new SymbolNavigationTools(new SymbolNavigationImpl(symbolNavigationEngine, impactAnalyzer, workspaceManager, NullLogger<SymbolNavigationImpl>.Instance));
    }

    internal static ScanTools CreateScanTools(IWorkspaceManager workspaceManager)
    {
        return Battery.Advanced.TestToolsAdvanced.ScanTools(workspaceManager);
    }

    private static (SentinelConfiguration Config, SymbolNavigationEngine Navigation, ValidationEngine Validation, BasicRefactoringEngine Refactoring, MemberRefactoringEngine Member) BuildEngines(IWorkspaceManager workspaceManager)
    {
        // DiffEngine is built before ValidationEngine, matching the BatteryTwentyFourTests [SetUp] wiring.
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        var symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var diffEngine = Battery.Basic.TestEngines.DiffEngine();
        var validationEngine = Battery.Basic.TestEngines.ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var memberRefactoringEngine = Battery.Basic.TestEngines.MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine);
        return (config, symbolNavigationEngine, validationEngine, refactoringEngine, memberRefactoringEngine);
    }
}
