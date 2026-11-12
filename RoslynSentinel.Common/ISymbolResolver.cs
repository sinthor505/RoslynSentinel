namespace RoslynSentinel.Common;

/// <summary>Resolves symbol handles received over the wire (MCP tool arguments) to Roslyn symbols.</summary>
public interface ISymbolResolver
{
    /// <summary>
    /// Single integration point for all symbol-accepting tools: resolves the doc-comment ID to a symbol.
    /// <paramref name="retryCall"/> (optional, last so positional callers are unchanged) maps a not-found candidate's docCommentId to
    /// the calling tool's own call with only the id corrected; see <see cref="SymbolWireResolver.ResolveAsync"/>.
    /// </summary>
    Task<SymbolResolution> ResolveFromWireAsync(string projectName, string docCommentId, CancellationToken cancellationToken, Func<string, string?>? retryCall = null);
}
