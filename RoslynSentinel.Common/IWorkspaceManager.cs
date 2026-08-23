using Microsoft.CodeAnalysis;
namespace RoslynSentinel.Common;

/// <summary>Full workspace contract: solution access, mutation, health reporting, and breaker/session bookkeeping. Prefer a narrower interface (ISolutionProvider, etc.) where possible.</summary>
public interface IWorkspaceManager :
    ISolutionProvider, ICircuitBreaker, IWorkspaceHealthReporter,
    IWorkspaceMutator, IRateLimiter, ISymbolResolver
{
    /// <summary>Unique identifier for this workspace manager instance's session. No production caller as of this writing.</summary>
    Guid SessionId { get; }
    /// <summary>Releases workspace resources. DI container owns lifetime; no production caller as of this writing.</summary>
    void Dispose();
    /// <summary>Checks whether sessionId matches this instance's session. No production caller as of this writing.</summary>
    bool IsCurrentSession(string sessionId);
    /// <summary>Resolves a symbol by its documentation-comment ID. Superseded by ResolveFromWireAsync; no production caller as of this writing.</summary>
    Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default);
    /// <summary>Resolves a symbol by handle. Superseded by ResolveFromWireAsync; no production caller as of this writing.</summary>
    Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken);
    /// <summary>Test-only seam: injects a solution directly, bypassing LoadSolutionAsync.</summary>
    void SetTestSolution(Solution solution);
    /// <summary>Associates a symbol handle with an agent for later lookup. No production caller as of this writing.</summary>
    void TrackSymbol(string agentHandle, SymbolHandle handle);
}
