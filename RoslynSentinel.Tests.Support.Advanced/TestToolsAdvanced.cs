// Construction factories for the Tools.Advanced classes whose constructors chain many engines (the inline
// `new QualityTools(new TestingEngine(wm), new ControlFlowEngine(wm), ...)` blocks and helper methods in the Advanced
// test projects). Each takes only the workspace manager: every engine, logger and ledger parameter resolves from a fresh
// container (TestEnginesAdvanced.CreateProvider) that shares the caller's manager. See TestEnginesAdvanced for the design.
//
// Rewriting the inline chains onto these is a hand edit (a call site passes 8-9 engine arguments, which does not match the
// one-argument overload), so replaceConstruction will not rewrite them; they exist to collapse helper bodies.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Battery.Advanced;

public static class TestToolsAdvanced
{
    // AdvancedRefactoringTools and GenerationTools have a convenience (wm) constructor beside the all-engines one, so the
    // manager is passed explicitly (the shape existing call sites use); the rest have a single constructor.
    public static AdvancedRefactoringTools AdvancedRefactoringTools(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<AdvancedRefactoringTools>(TestEnginesAdvanced.CreateProvider(workspaceManager), workspaceManager);

    public static GenerationTools GenerationTools(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<GenerationTools>(TestEnginesAdvanced.CreateProvider(workspaceManager), workspaceManager);

    public static GenerationTools GenerationTools(IWorkspaceManager workspaceManager, ILogger<GenerationTools> logger) =>
        ActivatorUtilities.CreateInstance<GenerationTools>(TestEnginesAdvanced.CreateProvider(workspaceManager), workspaceManager, logger);

    public static QualityTools QualityTools(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<QualityTools>(TestEnginesAdvanced.CreateProvider(workspaceManager));

    public static ModernizationTools ModernizationTools(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<ModernizationTools>(TestEnginesAdvanced.CreateProvider(workspaceManager));

    public static ScanTools ScanTools(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<ScanTools>(TestEnginesAdvanced.CreateProvider(workspaceManager));

    public static IntelligenceTools IntelligenceTools(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<IntelligenceTools>(TestEnginesAdvanced.CreateProvider(workspaceManager));

    public static AsyncifyTools AsyncifyTools(IWorkspaceManager workspaceManager) =>
        ActivatorUtilities.CreateInstance<AsyncifyTools>(TestEnginesAdvanced.CreateProvider(workspaceManager));
}
