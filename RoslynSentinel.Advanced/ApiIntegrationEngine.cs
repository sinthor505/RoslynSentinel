using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Advanced;
public class ApiIntegrationEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    public ApiIntegrationEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }
}