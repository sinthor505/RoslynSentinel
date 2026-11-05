using Microsoft.CodeAnalysis;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

public class SolutionManagementEngine
{
    private readonly IWorkspaceManager _workspaceManager;

    public SolutionManagementEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }
}
