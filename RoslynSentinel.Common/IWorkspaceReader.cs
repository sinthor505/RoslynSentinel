namespace RoslynSentinel.Common;

public enum ReadSource
{
    /// <summary>
    /// Last state written to disk via <see cref="PersistentWorkspaceManager.ApplyProposedChangesAsync"/>
    /// (or loaded at solution-open time). The only value that exists/matters until staged writes
    /// are implemented.
    /// </summary>
    Committed,

    /// <summary>
    /// Committed state, plus any currently-staged uncommitted edits from this session, if any exist.
    /// Identical to <see cref="Committed"/> until staging exists. Once staging exists, this is what
    /// most mutating-tool call sites should use -- a tool building on a prior uncommitted edit needs
    /// to see it.
    /// </summary>
    IncludeStaged,
}

/// <summary>
/// Read chokepoint for workspace content: answers content questions directly (document text, the
/// solution itself) instead of handing out a raw <see cref="Microsoft.CodeAnalysis.Solution"/> for
/// callers to navigate independently. See docs/current/design_read_chokepoint.md.
///
/// This exists alongside <see cref="ISolutionProvider"/> rather than replacing it (yet): the two
/// serve different questions -- <see cref="ISolutionProvider"/> answers "what solution/metadata is
/// loaded", this answers "what does the content say, as of which state". <see cref="ReadSource"/>
/// is mandatory on every method here so callers say explicitly which state they want.
/// </summary>
public interface IWorkspaceReader
{
    /// <summary>
    /// Returns a document's current text, or null if no document at that path is tracked in the
    /// solution. <paramref name="source"/> is mandatory -- see <see cref="ReadSource"/>.
    /// </summary>
    Task<string?> GetDocumentTextAsync(FilePathWrapper path, ReadSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves <paramref name="path"/> to a <see cref="Microsoft.CodeAnalysis.Document"/> on the snapshot
    /// <paramref name="source"/> selects, using <see cref="DocumentLookup.TryGetDocument"/>: exact path ignoring
    /// case first, then a bare file name only if exactly one file has it, else not-found (naming the closest real
    /// paths) or ambiguous (naming the candidates). Never returns null and never throws for a failed lookup --
    /// check <see cref="DocumentLookupResult.IsFound"/>, then use <c>document.FilePath</c> as the canonical path
    /// and <c>document.Project.Solution</c> (not a second <see cref="GetSolutionAsync"/> call) for the same
    /// snapshot. Throws <see cref="SolutionNotLoadedException"/> if no solution is loaded.
    /// </summary>
    Task<DocumentLookupResult> GetDocumentAsync(FilePathWrapper path, ReadSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the documents selected by <paramref name="scope"/> (one file, one project or the whole solution) from
    /// the snapshot <paramref name="source"/> selects. A file or project scope that does not resolve throws
    /// <see cref="ToolNotFoundException"/> / <see cref="ToolAmbiguousMatchException"/>. Throws
    /// <see cref="SolutionNotLoadedException"/> if no solution is loaded.
    /// </summary>
    Task<IReadOnlyList<Microsoft.CodeAnalysis.Document>> GetDocumentsAsync(DocumentScope scope, ReadSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the requested <see cref="Microsoft.CodeAnalysis.Solution"/> snapshot. Escape hatch
    /// for callers that need the actual Solution object (e.g. SymbolFinder-based searches across the
    /// whole solution) rather than a single document's text. Throws
    /// <see cref="SolutionNotLoadedException"/> if no solution is loaded.
    /// </summary>
    Task<Microsoft.CodeAnalysis.Solution> GetSolutionAsync(ReadSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the requested project's <see cref="Microsoft.CodeAnalysis.Compilation"/>, from a
    /// per-project cache when a valid entry exists for <paramref name="source"/>. Throws
    /// <see cref="SolutionNotLoadedException"/> if no solution is loaded, or
    /// <see cref="ArgumentException"/> if <paramref name="projectId"/> does not identify a project
    /// in the current solution.
    /// </summary>
    Task<Microsoft.CodeAnalysis.Compilation> GetCompilationAsync(Microsoft.CodeAnalysis.ProjectId projectId, ReadSource source, CancellationToken cancellationToken);
}
