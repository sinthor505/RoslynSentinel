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
    /// Returns the requested <see cref="Microsoft.CodeAnalysis.Solution"/> snapshot. Escape hatch
    /// for callers that need the actual Solution object (e.g. SymbolFinder-based searches across the
    /// whole solution) rather than a single document's text. Throws
    /// <see cref="SolutionNotLoadedException"/> if no solution is loaded.
    /// </summary>
    Task<Microsoft.CodeAnalysis.Solution> GetSolutionAsync(ReadSource source, CancellationToken cancellationToken);


    // Added by AddMember (expected - used for diagnostics)

    /// <summary>
    /// Returns the requested project's <see cref="Microsoft.CodeAnalysis.Compilation"/>, from a
    /// per-project cache when a valid entry exists for <paramref name="source"/>. Throws
    /// <see cref="SolutionNotLoadedException"/> if no solution is loaded, or
    /// <see cref="ArgumentException"/> if <paramref name="projectId"/> does not identify a project
    /// in the current solution.
    /// </summary>
    Task<Microsoft.CodeAnalysis.Compilation> GetCompilationAsync(Microsoft.CodeAnalysis.ProjectId projectId, ReadSource source, CancellationToken cancellationToken);
}
