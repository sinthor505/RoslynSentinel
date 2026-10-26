namespace RoslynSentinel.Tools.Basic;

internal static class ExistingTextReader
{
    /// <summary>
    /// The text a tool should treat as a file's "existing" content: the committed workspace document's text when
    /// <paramref name="path"/> is exactly a document of the loaded solution, otherwise the file on disk (null if it
    /// does not exist). An unloaded or still-loading solution also reads from disk and never throws.
    /// Exact path match only (Roslyn's case-insensitive comparer), deliberately not DocumentLookup's bare-file-name
    /// rule: a new file directly under the solution root must not pick up another file's text.
    /// Writes are still refused by the chokepoint (PersistentWorkspaceManager.ApplyProposedChangesAsync) when the
    /// workspace is stale or the target drifted on disk, so the workspace text is a safe baseline for the
    /// size guard and EOL detection.
    /// </summary>
    internal static async Task<string?> ReadAsync(IWorkspaceManager workspaceManager, FilePathWrapper path, CancellationToken cancellationToken)
    {
        try
        {
            var solution = await workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var documentId = solution.GetDocumentIdsWithFilePath(path.Absolute).FirstOrDefault();
            var document = documentId is null ? null : solution.GetDocument(documentId);
            if (document is not null)
            {
                return (await document.GetTextAsync(cancellationToken)).ToString();
            }
        }
        catch (Exception ex) when (ex is SolutionNotLoadedException or SolutionLoadTimeoutException)
        {
            // No loaded solution: the disk is the only source.
        }

        return await FileIoHelper.ReadAllTextIfExistsAsync(path, cancellationToken);
    }
}
