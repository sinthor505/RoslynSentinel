namespace RoslynSentinel.Common;

/// <summary>
/// The one agent-facing explanation for "this path is not a document of the loaded solution", shared by
/// <see cref="DocumentLookupResult.Describe"/> and the ReadFile / GetMethodSource / GetFileOutline not-found errors.
/// Before this, three copies of the text existed and all of them looked only at the loaded documents, so a file that
/// is on disk but outside the solution (or a directory, or a project file) got the misleading "does not exist anywhere".
/// <see cref="Build"/> probes the disk for a rooted path and picks the first matching branch. The overload taking
/// <see cref="FileCandidate"/>s is the one the lookup uses, fed by <see cref="DocumentLookup.FindFileCandidates"/>:
/// 1) a directory; 2) a project/solution file; 3) a file that exists on disk but is outside the loaded solution (with
/// the nearest LoadSolution target); 4) a loaded file with the same name exists; 5) nothing matches at all.
/// </summary>
public static class FileNotFoundMessage
{
    private static readonly string[] ProjectOrSolutionExtensions = [".csproj", ".vbproj", ".fsproj", ".sln", ".slnx"];

    /// <summary>
    /// Builds the message. <paramref name="requestedPath"/> is the path as the caller resolved it;
    /// <paramref name="candidatePaths"/> are the loaded documents sharing its file name (see
    /// <see cref="DocumentLookup.FindClosestPaths"/>); <paramref name="searchedProjectCount"/> is how many projects were searched.
    /// <paramref name="isCallable"/> says whether a tool name can be called on this server (null = every tool can);
    /// a tool is named in the text only when it can be called. A non-rooted path skips the disk checks.
    /// </summary>
    [Obsolete("Use the overload taking file candidates")]
    public static string Build(
        string requestedPath, IReadOnlyList<string> candidatePaths, int searchedProjectCount, Func<string, bool>? isCallable = null,
        IReadOnlyList<(string TypeName, string FilePath)>? declaringTypeFiles = null)
    {
        var diskText = TryDiskBranch(requestedPath, candidatePaths, isCallable);
        if (diskText is not null)
        {
            return diskText;
        }

        var list = string.Join("\n", candidatePaths.Select(c => $"  - {c}"));
        var declaring = DeclaringTypeSentence(declaringTypeFiles);
        if (candidatePaths.Count > 0)
        {
            return declaring + $"File '{requestedPath}' was not found in the loaded solution (searched {searchedProjectCount} project(s); path comparison ignores case, so casing is not the cause). " +
                   "A file with the same name exists at a different path. Retry with the exact path of the file you meant:\n" + list;
        }

        var notFound = declaring + $"File '{requestedPath}' was not found anywhere in the loaded solution (searched {searchedProjectCount} project(s), no file-name match; path comparison ignores case, so casing is not the cause). ";
        return notFound + NoMatchFactsAndOptions(requestedPath, isCallable);
    }

    /// <summary>
    /// Builds the message from the file candidates of <c>DocumentLookup.FindFileCandidates</c>. The disk branches are the
    /// same as the string-list overload. Candidates sharing the requested file name are named first and flagged; the rest
    /// are offered as "did you mean", each with a ReadFile call when ReadFile can be called. With no candidates it says so
    /// and names the next call.
    /// <paramref name="movedToPath"/> / <paramref name="movedByTool"/> (from <see cref="FileMoveMemory"/>, set only when the server itself moved the
    /// missing file earlier in this session and the new path is still a loaded document): after the disk branches, the text names the new
    /// path and the tool and returns, instead of the "did you mean" / "it may have been renamed" wording. Both null leaves every other
    /// branch's text exactly as it was.
    /// </summary>
    public static string Build(
        string requestedPath, IReadOnlyList<FileCandidate> candidates, int searchedProjectCount, Func<string, bool>? isCallable = null,
        IReadOnlyList<(string TypeName, string FilePath)>? declaringTypeFiles = null,
        string? movedToPath = null, string? movedByTool = null)
    {
        // TODO optional per-error-code operator policy suffix (idea_error_policy_pointer)
        return BuildCore(requestedPath, candidates, searchedProjectCount, isCallable, declaringTypeFiles, movedToPath, movedByTool);
    }

    private static string BuildCore(
        string requestedPath, IReadOnlyList<FileCandidate> candidates, int searchedProjectCount, Func<string, bool>? isCallable = null,
        IReadOnlyList<(string TypeName, string FilePath)>? declaringTypeFiles = null,
        string? movedToPath = null, string? movedByTool = null)
    {
        var sameNamePaths = candidates.Where(c => c.SameFileName).Select(c => c.Path).ToList();
        var diskText = TryDiskBranch(requestedPath, sameNamePaths, isCallable);
        if (diskText is not null)
        {
            return diskText;
        }

        if (movedToPath is not null && movedByTool is not null)
        {
            // Eval run 20261112-151531-550 T8: RenameSymbol moved the file, the model read the old path and the hedged text below cost it two more calls.
            var movedText = $"File '{requestedPath}' was moved to '{movedToPath}' by an earlier {movedByTool} call in this session" +
                            (movedByTool == "RenameSymbol" ? " (a rename of the type it declares)" : string.Empty) + $". The file is now at '{movedToPath}'.\n  - {movedToPath}";
            return Callable(isCallable, "ReadFile")
                ? movedText + $" -> {ToolCallHint.Call("ReadFile", ("filePath", movedToPath))}"
                : movedText;
        }

        var declaring = DeclaringTypeSentence(declaringTypeFiles);
        var intro = declaring + $"File '{requestedPath}' was not found in the loaded solution (searched {searchedProjectCount} project(s) by exact path, bare file name and similar file name; path comparison ignores case, so casing is not the cause). ";
        if (candidates.Count > 0)
        {
            var text = intro;
            if (sameNamePaths.Count > 0)
            {
                text += "A file with the same name exists at a different path. ";
            }

            text += "Did you mean one of these?";
            foreach (var candidate in candidates)
            {
                text += $"\n  - {candidate.Path}";
                if (Callable(isCallable, "ReadFile"))
                {
                    text += $" -> {ToolCallHint.Call("ReadFile", ("filePath", candidate.Path))}";
                }
            }

            return text + "\nRetry with the exact path of the file you meant. If none of these is the file you meant, it does not exist in this solution.";
        }

        var notFound = intro + "No file in the solution has a similar name. ";
        return notFound + NoMatchFactsAndOptions(requestedPath, isCallable);
    }

    // The facts and options that follow a "no file matches" statement: CreateFile creates the file (an option, named only when callable),
    // a file read earlier may have been renamed or moved (a fact; the server records only its own moves, see movedToPath above), and
    // ListSolutionItems lists every file (an option, named only when callable). Hints are built only when rendered.
    // TODO remember server-side file renames (old path -> new path) and name the new path here instead of hedging.
    private static string NoMatchFactsAndOptions(string requestedPath, Func<string, bool>? isCallable)
    {
        var text = string.Empty;
        if (Callable(isCallable, "CreateFile"))
        {
            text += $"{ToolCallHint.Call("CreateFile", ("filePath", requestedPath))} creates a new file at this path. ";
        }

        text += "A file read earlier under this name may since have been renamed or moved.";
        if (Callable(isCallable, "ListSolutionItems"))
        {
            text += $" {ToolCallHint.Call("ListSolutionItems", ("kind", "all"))} lists every file in the solution. A file not in that list does not exist in this solution.";
        }

        return text;
    }

    // Probes the disk for a rooted path and returns the text of the first branch that applies (directory, project or solution
    // file, file outside the loaded solution), or null when none does. sameNamePaths feeds the outside-solution text.
    private static string? TryDiskBranch(string requestedPath, IReadOnlyList<string> sameNamePaths, Func<string, bool>? isCallable)
    {
        if (!IsRooted(requestedPath))
        {
            return null;
        }

        if (SafeDirectoryExists(requestedPath))
        {
            var directoryText = $"'{requestedPath}' is a directory, not a file. Pass the path of a source file inside it.";
            return Callable(isCallable, "ListSolutionItems")
                ? directoryText + $" To list the files of the loaded solution call {ToolCallHint.Call("ListSolutionItems", ("kind", "all"))}."
                : directoryText;
        }

        if (SafeFileExists(requestedPath))
        {
            if (ProjectOrSolutionExtensions.Contains(Path.GetExtension(requestedPath), PathComparison.Comparer))
            {
                return $"'{requestedPath}' is a project/solution file, not a source document; this tool reads source files (.cs) of the loaded solution.";
            }

            return OutsideSolutionText(requestedPath, sameNamePaths, isCallable);
        }

        return null;
    }

    // A type that was renamed, or a file that was moved, leaves the old file name behind: when the missing file's stem is a
    // top-level type declared in the solution, say which file declares it, before anything else. Empty when none does.
    private static string DeclaringTypeSentence(IReadOnlyList<(string TypeName, string FilePath)>? declaringTypeFiles)
    {
        if (declaringTypeFiles is null || declaringTypeFiles.Count == 0)
        {
            return string.Empty;
        }

        var typeName = declaringTypeFiles[0].TypeName;
        if (declaringTypeFiles.Count == 1)
        {
            return $"Type '{typeName}' is declared in {declaringTypeFiles[0].FilePath}. ";
        }

        return $"Type '{typeName}' is declared in {declaringTypeFiles.Count} files: {string.Join("; ", declaringTypeFiles.Select(d => d.FilePath))}. ";
    }

    private static string OutsideSolutionText(string requestedPath, IReadOnlyList<string> candidatePaths, Func<string, bool>? isCallable)
    {
        var text = $"'{requestedPath}' exists on disk but is outside the loaded solution (it is not part of any loaded project).";
        var target = FindNearestLoadTarget(requestedPath);
        if (target is null)
        {
            text += " No .sln, .slnx or .csproj was found in its directory or above it, so there is nothing to load for it.";
        }
        else if (Callable(isCallable, "LoadSolution"))
        {
            text += $" To work on it, load the project that contains it with {ToolCallHint.Call("LoadSolution", ("solutionPath", target))} (this replaces the currently loaded solution).";
        }

        if (candidatePaths.Count > 0)
        {
            text += " A file with the same name exists in the loaded solution; if you meant that one, retry with its exact path:\n" +
                    string.Join("\n", candidatePaths.Select(c => $"  - {c}"));
        }

        return text;
    }

    /// <summary>
    /// Walks up from the file's directory: at each level the alphabetically first .sln/.slnx wins, else the first .csproj.
    /// Null when nothing is found up to the root or the disk cannot be read.
    /// </summary>
    private static string? FindNearestLoadTarget(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(directory))
            {
                var files = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList()
                    : [];
                var solution = files.FirstOrDefault(f => HasExtension(f, ".sln") || HasExtension(f, ".slnx"));
                if (solution is not null)
                {
                    return solution;
                }

                var project = files.FirstOrDefault(f => HasExtension(f, ".csproj"));
                if (project is not null)
                {
                    return project;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unreadable disk state is treated as "nothing found".
        }

        return null;
    }

    private static bool HasExtension(string path, string extension) => string.Equals(Path.GetExtension(path), extension, PathComparison.Comparison);

    private static bool Callable(Func<string, bool>? isCallable, string toolName) => isCallable is null || isCallable(toolName);

    private static bool IsRooted(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
