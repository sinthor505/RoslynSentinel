namespace RoslynSentinel.Common;

/// <summary>
/// Backs the optional <c>repoPath</c> parameter of Build and RunTest: checks that a path is a git worktree of the
/// loaded solution's repository, maps the loaded solution's files onto that worktree by their path relative to the
/// repository root, and reads the worktree's HEAD commit. Filesystem checks only (no git process): a worktree's
/// <c>.git</c> entry is a directory (the main working tree) or a file holding a <c>gitdir:</c> pointer whose
/// <c>commondir</c> file names the git directory every worktree of one repository shares.
/// DTO-EXEMPT: static helper class, not a data carrier.
/// </summary>
public static class RepoWorktrees
{
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private const string Checked =
        "Checked in order: an absolute path; an existing directory; a .git entry at its top level (a worktree root, not a subdirectory); "
        + "the same git common directory as the loaded solution's repository.";

    /// <summary>
    /// Validates <paramref name="repoPath"/> as a git worktree of the repository that contains
    /// <paramref name="loadedSolutionDirectory"/>. On success <paramref name="worktreeRoot"/> is its full path; on
    /// failure <paramref name="error"/> says which check failed (it names only the supplied path).
    /// </summary>
    public static bool TryResolve(string? repoPath, string? loadedSolutionDirectory, out string worktreeRoot, out string error)
    {
        worktreeRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            error = $"repoPath is empty. {Checked} Omit repoPath to use the loaded solution.";
            return false;
        }

        if (!Path.IsPathFullyQualified(repoPath))
        {
            error = $"repoPath '{repoPath}' is not an absolute path. {Checked} Omit repoPath to use the loaded solution.";
            return false;
        }

        string root;
        try
        {
            root = TrimSeparators(Path.GetFullPath(repoPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"repoPath '{repoPath}' is not a valid path. {Checked} Omit repoPath to use the loaded solution.";
            return false;
        }

        if (!Directory.Exists(root))
        {
            error = $"repoPath '{repoPath}' is not an existing directory. {Checked} Omit repoPath to use the loaded solution.";
            return false;
        }

        var targetCommon = CommonGitDirectory(root);
        if (targetCommon is null)
        {
            error = $"repoPath '{repoPath}' has no .git entry at its top level, so it is not a git worktree root. {Checked} Omit repoPath to use the loaded solution.";
            return false;
        }

        var loadedRoot = FindWorkingTreeRoot(loadedSolutionDirectory);
        var loadedCommon = loadedRoot is null ? null : CommonGitDirectory(loadedRoot);
        if (loadedCommon is null)
        {
            error = $"repoPath '{repoPath}' was not compared: the loaded solution is not inside a git repository (or no solution is loaded), so there is no repository to match it against. Omit repoPath to use the loaded solution.";
            return false;
        }

        if (!string.Equals(targetCommon, loadedCommon, PathComparison))
        {
            error = $"repoPath '{repoPath}' is a git working tree of a different repository than the loaded solution's (their git common directories differ). {Checked} Omit repoPath to use the loaded solution.";
            return false;
        }

        worktreeRoot = root;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Maps <paramref name="loadedPath"/> (a file of the loaded solution) onto <paramref name="worktreeRoot"/> by its
    /// path relative to the loaded solution's repository root, and requires that file to exist there. False carries an
    /// error naming the repository-relative path (never the loaded tree's absolute path).
    /// </summary>
    public static bool TryMapExisting(string loadedPath, string worktreeRoot, out string mapped, out string error)
    {
        mapped = string.Empty;
        var candidate = MapToWorktree(loadedPath, worktreeRoot);
        if (candidate is null)
        {
            error = "The file to build is not inside the loaded solution's git repository, so it has no counterpart in the worktree. Omit repoPath to build the loaded solution.";
            return false;
        }

        if (!File.Exists(candidate))
        {
            var relative = Path.GetRelativePath(worktreeRoot, candidate).Replace('\\', '/');
            error = $"The worktree '{worktreeRoot}' has no '{relative}' (the loaded solution's file at the same repository-relative path); its checked-out commit may predate it. Check out a commit that has it, or omit repoPath.";
            return false;
        }

        mapped = candidate;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// <paramref name="loadedPath"/> re-rooted from the loaded solution's repository root onto
    /// <paramref name="worktreeRoot"/>; null when it is outside any git repository. Does not check existence.
    /// </summary>
    public static string? MapToWorktree(string loadedPath, string worktreeRoot)
    {
        var loadedRoot = FindWorkingTreeRoot(Path.GetDirectoryName(loadedPath));
        if (loadedRoot is null)
        {
            return null;
        }

        var relative = Path.GetRelativePath(loadedRoot, loadedPath);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        return Path.Combine(worktreeRoot, relative);
    }

    /// <summary>The full sha of the worktree's HEAD; null when git cannot answer (never throws).</summary>
    public static string? ReadHeadCommit(string worktreeRoot)
    {
        try
        {
            var sha = GitReaders.Default.GetHeadSha(worktreeRoot, shortForm: false, HeadTimeout)?.Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Walks up from <paramref name="startDirectory"/> to the first directory with a .git entry (directory or file); null when none.</summary>
    public static string? FindWorkingTreeRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        string? directory;
        try
        {
            directory = Path.GetFullPath(startDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        while (!string.IsNullOrEmpty(directory))
        {
            var entry = Path.Combine(directory, ".git");
            if (Directory.Exists(entry) || File.Exists(entry))
            {
                return TrimSeparators(directory);
            }

            var parent = Path.GetDirectoryName(directory);
            if (string.Equals(parent, directory, PathComparison))
            {
                break;
            }

            directory = parent;
        }

        return null;
    }

    // The git directory all worktrees of one repository share: the .git directory itself for a main working tree; for a
    // linked worktree, the gitdir its .git file points at, redirected through that directory's commondir file.
    private static string? CommonGitDirectory(string workingTreeRoot)
    {
        var entry = Path.Combine(workingTreeRoot, ".git");
        try
        {
            string gitDirectory;
            if (Directory.Exists(entry))
            {
                gitDirectory = entry;
            }
            else if (File.Exists(entry))
            {
                string? pointer = null;
                foreach (var line in File.ReadLines(entry))
                {
                    if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                    {
                        pointer = line["gitdir:".Length..].Trim();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(pointer))
                {
                    return null;
                }

                gitDirectory = Path.GetFullPath(pointer, workingTreeRoot);
            }
            else
            {
                return null;
            }

            var commonFile = Path.Combine(gitDirectory, "commondir");
            return File.Exists(commonFile)
                ? TrimSeparators(Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDirectory))
                : TrimSeparators(Path.GetFullPath(gitDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string TrimSeparators(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
