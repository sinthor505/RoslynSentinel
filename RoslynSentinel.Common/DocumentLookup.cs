using System.Diagnostics.CodeAnalysis;

using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

/// <summary>
/// The one path comparer for file-path identity in this server: ordinal, ignoring case, on every
/// OS. It matches <see cref="FilePathWrapper"/> (whose Equals/GetHashCode/CompareTo already ignore
/// case) and Roslyn's own document map (which ignores case on every OS), so a collection or
/// comparison keyed by a path string agrees with both. Use this instead of a default-comparer
/// <c>Dictionary&lt;string,...&gt;</c>/<c>HashSet&lt;string&gt;</c> or a bare <c>string ==</c> whenever the
/// string is a file path. Disk I/O should keep passing the path through unchanged and let the OS
/// decide. See docs/current/blockers/blocking_error_path_lookup_case_sensitive_drive_letter_replacesnippet_file_not_found.md.
/// </summary>
public static class PathComparison
{
    /// <summary>Comparer for path-keyed collections and LINQ operators (Distinct, GroupBy, ToDictionary, ...).</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Comparison for <c>string.Equals/StartsWith/EndsWith/Contains</c> on paths.</summary>
    public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;
}

/// <summary>Outcome of a path-to-<see cref="Document"/> lookup.</summary>
public enum DocumentLookupStatus
{
    /// <summary>Exactly one document (or one file shared by several projects) was resolved.</summary>
    Found,

    /// <summary>No document matched. <see cref="DocumentLookupResult.CandidatePaths"/> holds the closest real paths, if any.</summary>
    NotFound,

    /// <summary>A bare file name matched more than one distinct file. <see cref="DocumentLookupResult.CandidatePaths"/> lists them.</summary>
    Ambiguous,
}

/// <summary>
/// Result of <see cref="DocumentLookup.TryGetDocument"/>. A result type rather than a nullable
/// <see cref="Microsoft.CodeAnalysis.Document"/> because the two failure shapes (not found, ambiguous) each
/// carry the paths a caller needs to recover, and the lookup is the only place that knows them.
/// Tool-layer callers use <see cref="ToResultError"/>; engine-layer callers use
/// <see cref="GetDocumentOrThrow"/>, which throws the matching <see cref="ToolException"/> subclass so
/// <see cref="ToolErrorMapper"/> reports the right error code.
/// </summary>
public sealed record DocumentLookupResult(
    DocumentLookupStatus Status,
    string RequestedPath,
    Document? Document,
    IReadOnlyList<string> CandidatePaths,
    int SearchedProjectCount)
{
    /// <summary>True when <see cref="Document"/> is populated.</summary>
    public bool IsFound => Status == DocumentLookupStatus.Found && Document is not null;

    /// <summary>Returns the document when found.</summary>
    public bool TryGetDocument([NotNullWhen(true)] out Document? document)
    {
        document = IsFound ? Document : null;
        return document is not null;
    }

    /// <summary>
    /// Returns the document, or throws <see cref="ToolNotFoundException"/> /
    /// <see cref="ToolAmbiguousMatchException"/> carrying <see cref="Describe"/> as its message.
    /// </summary>
    public Document GetDocumentOrThrow()
    {
        if (TryGetDocument(out var document))
        {
            return document;
        }

        if (Status == DocumentLookupStatus.Ambiguous)
        {
            throw new ToolAmbiguousMatchException(Describe());
        }

        throw new ToolNotFoundException(Describe());
    }

    /// <summary>
    /// Agent-facing explanation of a failed lookup: names the closest real paths (not found) or the
    /// candidates (ambiguous), and says casing is not the cause. Empty string when found.
    /// </summary>
    public string Describe()
    {
        if (Status == DocumentLookupStatus.Found)
        {
            return string.Empty;
        }

        var list = string.Join("\n", CandidatePaths.Select(c => $"  - {c}"));
        if (Status == DocumentLookupStatus.Ambiguous)
        {
            return $"'{RequestedPath}' matches {CandidatePaths.Count} different files in the solution, so a bare file name is ambiguous. " +
                   "You MUST retry with the full path of the one you meant:\n" + list;
        }

        if (CandidatePaths.Count > 0)
        {
            return $"File '{RequestedPath}' was not found in the loaded solution (searched {SearchedProjectCount} project(s); path comparison ignores case, so casing is not the cause). " +
                   "A file with the same name exists at a different path. You MUST retry with the exact path of the file you meant:\n" + list;
        }

        return $"File '{RequestedPath}' was not found anywhere in the loaded solution (searched {SearchedProjectCount} project(s), no file-name match; path comparison ignores case, so casing is not the cause). " +
               "You MUST call ListSolutionItems(kind: all) next to see every file actually in the solution before trying another path.";
    }

    /// <summary>
    /// Builds the structured tool error for a failed lookup: <see cref="ToolErrorCode.NotFound"/> or
    /// <see cref="ToolErrorCode.Ambiguous"/>, with <see cref="Describe"/> as the message. Only call
    /// when <see cref="IsFound"/> is false.
    /// </summary>
    public ResultError ToResultError()
        => new(Status == DocumentLookupStatus.Ambiguous ? ToolErrorCode.Ambiguous : ToolErrorCode.NotFound, Describe());
}

/// <summary>What <see cref="DocumentScope"/> selects.</summary>
public enum DocumentScopeKind
{
    /// <summary>One file, by path.</summary>
    File,

    /// <summary>Every document of one project.</summary>
    Project,

    /// <summary>Every document in the solution.</summary>
    Solution,
}

/// <summary>
/// Selects the documents <see cref="IWorkspaceReader.GetDocumentsAsync"/> returns: one file, one
/// project or the whole solution. Built through the factory methods so an invalid combination
/// (a project scope with no project key) is unrepresentable.
/// </summary>
public sealed class DocumentScope
{
    public DocumentScopeKind Kind
    {
        get;
    }
    public FilePathWrapper File
    {
        get;
    }
    public ProjectId? ProjectId
    {
        get;
    }
    public string? ProjectName
    {
        get;
    }

    private DocumentScope(DocumentScopeKind kind, FilePathWrapper file, ProjectId? projectId, string? projectName)
    {
        Kind = kind;
        File = file;
        ProjectId = projectId;
        ProjectName = projectName;
    }

    /// <summary>Every document in the solution.</summary>
    public static DocumentScope WholeSolution { get; } = new(DocumentScopeKind.Solution, default, null, null);

    /// <summary>The single document at <paramref name="path"/>, resolved by <see cref="DocumentLookup.TryGetDocument"/>.</summary>
    public static DocumentScope ForFile(FilePathWrapper path) => new(DocumentScopeKind.File, path, null, null);

    /// <summary>Every document of the project with this id (the exact key; use when a <see cref="Project"/> is already in hand).</summary>
    public static DocumentScope ForProject(ProjectId projectId) => new(DocumentScopeKind.Project, default, projectId, null);

    /// <summary>Every document of the project with this name (case-insensitive; the key a tool parameter carries).</summary>
    public static DocumentScope ForProject(string projectName) => new(DocumentScopeKind.Project, default, null, projectName);
}

/// <summary>
/// The one implementation of path-to-<see cref="Document"/> lookup. <see cref="IWorkspaceReader"/>
/// implementations run it on the snapshot the caller's <see cref="ReadSource"/> selects; it stays
/// public for callers that hold a forked or speculative <see cref="Solution"/> the reader cannot
/// hand out. Built on <see cref="Solution.GetDocumentIdsWithFilePath"/>, so it inherits Roslyn's
/// OrdinalIgnoreCase path comparer by construction -- never a hand-rolled <c>d.FilePath == x</c>,
/// whose case sensitivity depends on whether <c>x</c> is statically a string or a
/// <see cref="FilePathWrapper"/>. See docs/current/design_read_chokepoint.md step 5.
/// </summary>
public static class DocumentLookup
{
    private const int MaxClosestPaths = 5;
    private const int MaxAmbiguousPaths = 10;
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// Resolves <paramref name="path"/> to a document. Rules, in order:
    /// 1) exact path, ignoring case (a file shared by several projects, e.g. a multi-targeted one, is one file and
    /// resolves to its first document);
    /// 2) a bare file name (no directory part), but only if exactly one distinct file in the solution has that name --
    /// two or more is <see cref="DocumentLookupStatus.Ambiguous"/>, never first-wins;
    /// 3) otherwise <see cref="DocumentLookupStatus.NotFound"/> with the closest real paths (same file name).
    /// A path directly under the solution root whose file does not exist is indistinguishable from a bare name
    /// once <see cref="_workspaceManager.ResolveFromWire"/> has resolved it, so it takes rule 2 as well.
    /// </summary>
    public static DocumentLookupResult TryGetDocument(Solution solution, FilePathWrapper path)
    {
        var requested = path.Absolute;
        var projectCount = solution.Projects.Count();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return new DocumentLookupResult(DocumentLookupStatus.NotFound, requested, null, [], projectCount);
        }

        var exact = solution.GetDocumentIdsWithFilePath(requested)
            .Select(solution.GetDocument)
            .FirstOrDefault(d => d is not null);
        if (exact is not null)
        {
            return new DocumentLookupResult(DocumentLookupStatus.Found, requested, exact, [], projectCount);
        }

        var bareName = GetBareFileName(path);
        if (bareName is not null)
        {
            var byName = solution.Projects
                .SelectMany(p => p.Documents)
                .Where(d => PathComparison.Comparer.Equals(d.Name, bareName))
                .ToList();
            var distinctPaths = byName
                .Select(d => d.FilePath ?? d.Name)
                .Distinct(PathComparison.Comparer)
                .ToList();
            if (distinctPaths.Count == 1)
            {
                return new DocumentLookupResult(DocumentLookupStatus.Found, requested, byName[0], [], projectCount);
            }

            if (distinctPaths.Count > 1)
            {
                return new DocumentLookupResult(DocumentLookupStatus.Ambiguous, bareName, null, distinctPaths.Take(MaxAmbiguousPaths).ToList(), projectCount);
            }
        }

        return new DocumentLookupResult(DocumentLookupStatus.NotFound, requested, null, FindClosestPaths(solution, requested), projectCount);
    }

    /// <summary>
    /// The real paths of documents whose file name matches the file name of <paramref name="requestedPath"/>
    /// (ignoring case), at most <paramref name="max"/>. Shared by the not-found errors of the lookup and of ReadFile.
    /// </summary>
    public static IReadOnlyList<string> FindClosestPaths(Solution solution, string requestedPath, int max = MaxClosestPaths)
    {
        var requestedFileName = Path.GetFileName(requestedPath);
        if (string.IsNullOrEmpty(requestedFileName))
        {
            return [];
        }

        return solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => !string.IsNullOrEmpty(d.FilePath) && PathComparison.Comparer.Equals(Path.GetFileName(d.FilePath), requestedFileName))
            .Select(d => d.FilePath!)
            .Distinct(PathComparison.Comparer)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Selects documents by <paramref name="scope"/>. A file scope that does not resolve, or a project scope naming no
    /// project, throws <see cref="ToolNotFoundException"/> / <see cref="ToolAmbiguousMatchException"/> rather than
    /// returning an empty list, because an empty list would read as "nothing to analyze" instead of "the scope was wrong".
    /// </summary>
    public static IReadOnlyList<Document> GetDocuments(Solution solution, DocumentScope scope)
    {
        switch (scope.Kind)
        {
            case DocumentScopeKind.File:
                return [TryGetDocument(solution, scope.File).GetDocumentOrThrow()];

            case DocumentScopeKind.Project:
                var projects = scope.ProjectId is not null
                    ? solution.Projects.Where(p => p.Id == scope.ProjectId).ToList()
                    : solution.Projects.Where(p => string.Equals(p.Name, scope.ProjectName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (projects.Count == 0)
                {
                    var key = scope.ProjectId is not null ? scope.ProjectId.ToString() : scope.ProjectName;
                    throw new ToolNotFoundException(
                        $"Project '{key}' was not found in the loaded solution. Projects: {string.Join(", ", solution.Projects.Select(p => p.Name))}.");
                }

                return projects.SelectMany(p => p.Documents).ToList();

            default:
                return solution.Projects.SelectMany(p => p.Documents).ToList();
        }
    }

    // A bare file name is either a rootless wrapper with no directory part (the implicit string
    // conversion, or ResolveFromWire with no solution root), or a path ResolveFromWire resolved to a single
    // segment under the solution root (Relative has no separator).
    private static string? GetBareFileName(FilePathWrapper path)
    {
        var absolute = path.Absolute;
        if (!Path.IsPathRooted(absolute) && absolute.IndexOfAny(Separators) < 0)
        {
            return absolute;
        }

        var relative = path.Relative;
        if (!string.IsNullOrEmpty(relative) && relative.IndexOfAny(Separators) < 0 && relative != ".." && relative != ".")
        {
            return relative;
        }

        return null;
    }
}
