using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Advanced;
public class ArchitecturalEngine
{
    private readonly SentinelConfiguration _config;
    private readonly IWorkspaceManager _workspaceManager;
    public ArchitecturalEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config = null)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }
}