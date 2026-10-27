using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

/// <summary>Document-based access to <see cref="IWorkspaceMutator.ResolveEol"/> for engines.</summary>
public static class WorkspaceMutatorEolExtensions
{
    /// <summary>
    /// Returns the line terminator the write chokepoint will use for <paramref name="document"/>: <see cref="IWorkspaceMutator.ResolveEol"/>
    /// with the document's file path and its current text as the pre-image. Engines pass the result to the eol overloads of
    /// <see cref="RoslynFormattingHelper"/> instead of detecting the dominant line ending of the document.
    /// </summary>
    public static async Task<string> ResolveEolAsync(this IWorkspaceMutator workspace, Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(document);

        var text = await document.GetTextAsync(cancellationToken);
        return workspace.ResolveEol(document.FilePath ?? string.Empty, text.ToString());
    }
}
