namespace RoslynSentinel.Common;

/// <summary>Resolves symbol handles received over the wire (MCP tool arguments) to Roslyn symbols.</summary>
public interface ISymbolResolver
{
    /// <summary>Single integration point for all symbol-accepting tools: validates the session, then resolves the doc-comment ID to a symbol.</summary>
    Task<SymbolResolution> ResolveFromWireAsync(string sessionId, string projectName, string docCommentId, CancellationToken cancellationToken);
}
