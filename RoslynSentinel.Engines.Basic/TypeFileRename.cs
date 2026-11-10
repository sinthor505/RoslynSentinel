using Microsoft.CodeAnalysis;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The "move a type's file to a new name" part shared by SyncTypeAndFilename (file follows the type) and RenameSymbol
/// (a renamed top-level type takes its same-named file along). Pure logic: no MCP types, no writes; the caller feeds the
/// result through its own validate-and-apply step so the symbol rename and the file move land as one operation.
/// </summary>
public static class TypeFileRename
{
    /// <summary>
    /// The files to rename when <paramref name="symbol"/> (still the OLD symbol) is renamed to <paramref name="newName"/>.
    /// Only a top-level (non-nested) named type qualifies, and only declaration files whose name stem equals the old type
    /// name exactly (Order.cs for Order). Generic arity is ignored: Box&lt;T&gt; counts for Box.cs, and the new file is
    /// "newName" plus the same extension, with no arity suffix. For a partial type only the matching-stem file(s) are
    /// returned; members, nested types, namespaces and files with a different stem return nothing.
    /// A matching file that cannot be moved safely comes back with a <c>SkipReason</c> instead: the validation compile adds the
    /// moved file to the project whose folder contains the new path, so a file compiled by several projects (one path, several
    /// documents) or whose new path maps to no project, or to another one, would drop the type from a compilation and fail the
    /// whole rename. <paramref name="solution"/> is the one <paramref name="symbol"/> was bound in.
    /// </summary>
    public static List<(FilePathWrapper From, FilePathWrapper To, string? SkipReason)> FindMatchingFiles(Solution solution, ISymbol symbol, string newName, string? solutionRoot)
    {
        var moves = new List<(FilePathWrapper From, FilePathWrapper To, string? SkipReason)>();
        if (symbol is not INamedTypeSymbol { ContainingType: null } type)
        {
            return moves;
        }

        foreach (var location in type.Locations)
        {
            if (!location.IsInSource || string.IsNullOrEmpty(location.SourceTree?.FilePath))
            {
                continue;
            }

            var path = location.SourceTree!.FilePath;
            var directory = Path.GetDirectoryName(path);
            if (directory is null || !string.Equals(Path.GetFileNameWithoutExtension(path), type.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var from = new FilePathWrapper(path, solutionRoot);
            if (moves.Any(m => m.From == from))
            {
                continue;
            }

            var to = new FilePathWrapper(Path.Combine(directory, newName + Path.GetExtension(path)), solutionRoot);
            var documentIds = solution.GetDocumentIdsWithFilePath(from.Absolute);
            string? skipReason = null;
            if (documentIds.Length != 1)
            {
                skipReason = "the file is not a document of exactly one project (it is compiled by several projects or is not in the loaded solution)";
            }
            else if (SolutionProjectLocator.FindContainingProject(solution, to.Absolute)?.Id != documentIds[0].ProjectId)
            {
                skipReason = "the new path is not inside the folder of the project that compiles the file";
            }

            moves.Add((from, to, skipReason));
        }

        return moves;
    }

    /// <summary>True when a file already sits at <paramref name="target"/>; a move there would overwrite it, so callers refuse.</summary>
    public static bool TargetExists(FilePathWrapper target) => File.Exists(target.Absolute);

    /// <summary>
    /// Folds a file move into <paramref name="changes"/>: drops <paramref name="from"/>'s entry and adds
    /// <paramref name="content"/> at <paramref name="to"/>. Returns the paths the caller must pass as <c>deletePaths</c>
    /// (not <c>removePaths</c>) to the apply step: deletePaths goes through the tracked delete path that captures the old
    /// file's pre-image for UndoLastApply and removes the old Document from the workspace itself. Passing the old path in
    /// BOTH removePaths and deletePaths would ask the validation to RemoveDocument the same path twice against its own
    /// candidate solution (InvalidOperationException on the second), so removePaths is intentionally not returned.
    /// </summary>
    public static IReadOnlyCollection<FilePathWrapper> MoveInto(Dictionary<FilePathWrapper, string> changes, FilePathWrapper from, FilePathWrapper to, string content)
    {
        changes.Remove(from);
        changes[to] = content;
        return [from];
    }
}
