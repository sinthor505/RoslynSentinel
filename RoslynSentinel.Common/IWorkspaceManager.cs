using Microsoft.CodeAnalysis;
namespace RoslynSentinel.Common;

/// <summary>
/// Full workspace contract: solution access, mutation, health reporting, and breaker/session
/// bookkeeping. Prefer a narrower interface (ISolutionProvider, etc.) where possible.
/// Implementations: <see cref="PersistentWorkspaceManager"/> (production, real MSBuild-backed
/// solution) and <c>RoslynSentinel.Tests.Fakes.FakeWorkspaceManager</c> (test double, throws
/// NotImplementedException on anything not explicitly stubbed). For tests that need a real
/// on-disk solution rather than the fake's in-memory/SetTestSolution path, see
/// <c>RoslynSentinel.Tests.TestSolutionFixture</c>, which loads via
/// <see cref="PersistentWorkspaceManager"/>.
/// </summary>
public interface IWorkspaceManager :
    ISolutionProvider, ICircuitBreaker, IWorkspaceHealthReporter,
    IWorkspaceMutator, IRateLimiter, ISymbolResolver
{
    /// <summary>Unique identifier for this workspace manager instance's session.</summary>
    Guid SessionId { get; }
    /// <summary>Releases workspace resources. DI container owns lifetime; no production caller as of this writing.</summary>
    void Dispose();
    /// <summary>Checks whether sessionId matches this instance's session. No production caller as of this writing.</summary>
    [Obsolete("No production caller. Reserved for external consumers; do not add new usages.")]
    bool IsCurrentSession(string sessionId);
    /// <summary>Resolves a symbol by its documentation-comment ID. Superseded by ResolveFromWireAsync; no production caller as of this writing.</summary>
    [Obsolete("Superseded by ResolveFromWireAsync. No production caller; do not add new usages.")]
    Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default);
    /// <summary>Resolves a symbol by handle. Superseded by ResolveFromWireAsync; no production caller as of this writing.</summary>
    [Obsolete("Superseded by ResolveFromWireAsync. No production caller; do not add new usages.")]
    Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken);
    /// <summary>Test-only seam: injects a solution directly, bypassing LoadSolutionAsync.</summary>
    void SetTestSolution(Solution solution);
    /// <summary>Associates a symbol handle with an agent for later lookup. No production caller as of this writing.</summary>
    [Obsolete("No production caller. Reserved for external consumers; do not add new usages.")]
    void TrackSymbol(string agentHandle, SymbolHandle handle);
}
