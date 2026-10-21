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
}
