using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Delegates to Battery.Basic.TestTools (Basic tool classes) and Battery.Advanced.TestToolsAdvanced (Advanced tool classes).
internal static class BatteryToolFactory
{
    internal static AdvancedRefactoringTools CreateAdvancedRefactoringTools(IWorkspaceManager workspaceManager)
    {
        return Battery.Advanced.TestToolsAdvanced.AdvancedRefactoringTools(workspaceManager);
    }

    internal static CoreRefactoringTools CreateCoreRefactoringTools(IWorkspaceManager workspaceManager)
    {
        return Battery.Basic.TestTools.CoreRefactoringTools(workspaceManager);
    }

    internal static GenerationTools CreateGenerationTools(IWorkspaceManager workspaceManager)
    {
        return Battery.Advanced.TestToolsAdvanced.GenerationTools(workspaceManager);
    }

    internal static RefactoringExtractionDocsTools CreateRefactoringExtractionDocsTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.RefactoringExtractionDocsTools(workspaceManager);

    internal static TypedMemberTools CreateTypedMemberTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.TypedMemberTools(workspaceManager);

    internal static RefactoringStructuralTools CreateRefactoringStructuralTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.RefactoringStructuralTools(workspaceManager);

    internal static RefactoringSignatureTools CreateRefactoringSignatureTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.RefactoringSignatureTools(workspaceManager);

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

    internal static SymbolRelationshipTools CreateSymbolRelationshipTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.SymbolRelationshipTools(workspaceManager);

    internal static SymbolNavigationTools CreateSymbolNavigationTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.SymbolNavigationTools(workspaceManager);

    internal static ScanTools CreateScanTools(IWorkspaceManager workspaceManager)
    {
        return Battery.Advanced.TestToolsAdvanced.ScanTools(workspaceManager);
    }
}
