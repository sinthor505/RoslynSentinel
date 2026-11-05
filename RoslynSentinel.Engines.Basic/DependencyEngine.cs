using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Engines.Basic;

public class DependencyEngine
{
    private readonly IWorkspaceManager _workspaceManager;

    public DependencyEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }
}
