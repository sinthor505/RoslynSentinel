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
