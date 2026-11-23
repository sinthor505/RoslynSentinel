using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Deliberate deviation from the codebase pattern: this type is declared in RoslynSentinel.Tests.Tools.Advanced,
// not in this project's own namespace. Reason: zero-edit moves of 13 test files from RoslynSentinel.Tests.Tools.Advanced
// to this project keep their ~118 call sites compiling; the alternative is a 118-site sweep (plan decision 7).
// The class is internal in each assembly, so it does not clash with the BatteryToolFactory in Tests.Tools.Advanced
// (the two assemblies do not reference each other).
// TODO inline into TestTools calls and delete once MoveFile namespaceHandling update exists
internal static class BatteryToolFactory
{
    internal static CoreRefactoringTools CreateCoreRefactoringTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.CoreRefactoringTools(workspaceManager);

    internal static RefactoringExtractionDocsTools CreateRefactoringExtractionDocsTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.RefactoringExtractionDocsTools(workspaceManager);

    internal static TypedMemberTools CreateTypedMemberTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.TypedMemberTools(workspaceManager);

    internal static RefactoringStructuralTools CreateRefactoringStructuralTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.RefactoringStructuralTools(workspaceManager);

    internal static RefactoringSignatureTools CreateRefactoringSignatureTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.RefactoringSignatureTools(workspaceManager);

    internal static SymbolRelationshipTools CreateSymbolRelationshipTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.SymbolRelationshipTools(workspaceManager);

    internal static SymbolNavigationTools CreateSymbolNavigationTools(IWorkspaceManager workspaceManager) => Battery.Basic.TestTools.SymbolNavigationTools(workspaceManager);
}
