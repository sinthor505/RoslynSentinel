namespace RoslynSentinel.Common;

/// <summary>Shared whole-file observed-content recording for read tools.</summary>
public static class ObservedContentExtensions
{
    /// <summary>
    /// Records the whole-file hash of the committed text at <paramref name="path"/> with the workspace's observed-content ledger.
    /// Shared so InspectSymbol and other read tools can record the whole-file hash, not only ReadFile.
    /// </summary>
    public static async Task RecordWholeFileObservedAsync(this IWorkspaceManager workspaceManager, FilePathWrapper path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceManager);

        var solution = await workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.FindDocumentOrNull(solution, path);

        string? wholeText;
        if (document != null)
        {
            wholeText = (await document.GetTextAsync(cancellationToken)).ToString();
        }
        else
        {
            wholeText = await FileIoHelper.ReadAllTextIfExistsAsync(path, cancellationToken);
        }
        if (wholeText != null)
        {
            workspaceManager.RecordObserved(path, wholeText);
        }
    }
}
