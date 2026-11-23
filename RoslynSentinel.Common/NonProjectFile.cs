using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

/// <summary>What <see cref="NonProjectFile.Classify"/> decided about a path.</summary>
public enum NonProjectFileStatus
{
    /// <summary>No file exists at the path.</summary>
    Missing,

    /// <summary>The file exists and a loaded project owns it as a document (the document lookup, not this helper, serves it).</summary>
    ProjectDocument,

    /// <summary>The file exists, no project owns it, but it lies outside the solution root.</summary>
    OutsideSolutionRoot,

    /// <summary>The file exists under the solution root, no project owns it and it is a C# source file (.cs/.csx): editing it needs a syntax tree and the compile gate.</summary>
    CodeFileOutsideProjects,

    /// <summary>The file exists under the solution root, no project owns it and its first bytes contain a NUL, so it is not plain text.</summary>
    NotPlainText,

    /// <summary>The file exists under the solution root, no project owns it and it is plain text: the text edit tools may edit it without a compile gate.</summary>
    EligibleText,
}

/// <summary>
/// The one decision about an existing file no loaded project owns, shared by the text edit tools (ReplaceSnippet, ReplaceRange)
/// and DeleteFile so they agree. Sits beside <see cref="DocumentLookup"/>, which answers the project-document half.
/// The rules: a file exists when File.Exists is true for the validated absolute path (<see cref="ExistsOnDisk"/>); it is
/// "non-project" when no document of the solution has that path; it is editable as text when it lies under the solution
/// root, is not a C# source file and is not binary. There is no compile gate for such a file: the write chokepoint's
/// validation skips a non-.cs path that no project contains.
/// </summary>
public static class NonProjectFile
{
    private const int BinarySniffBytes = 8000;

    private static readonly string[] CodeExtensions = [".cs", ".csx"];

    /// <summary>
    /// True when a file exists at the validated absolute <paramref name="path"/>. DeleteFile's file-exists decision: it accepts any existing
    /// file, including one outside the solution root (it never asked for a project document).
    /// </summary>
    public static bool ExistsOnDisk(FilePathWrapper path)
        => !string.IsNullOrWhiteSpace(path.Absolute) && File.Exists(path.Absolute);

    /// <summary>
    /// Classifies <paramref name="path"/> (already resolved with ResolveFromWire). <paramref name="solution"/> may be null (no project owns
    /// anything then); <paramref name="solutionRoot"/> is the directory of the loaded solution, null when none is known (every
    /// existing file then counts as outside the root).
    /// </summary>
    public static NonProjectFileStatus Classify(Solution? solution, string? solutionRoot, FilePathWrapper path)
    {
        if (!ExistsOnDisk(path))
        {
            return NonProjectFileStatus.Missing;
        }

        if (solution is not null && solution.GetDocumentIdsWithFilePath(path.Absolute).Length > 0)
        {
            return NonProjectFileStatus.ProjectDocument;
        }

        if (!IsUnderRoot(path.Absolute, solutionRoot))
        {
            return NonProjectFileStatus.OutsideSolutionRoot;
        }

        // TODO a .cs/.csx file that no project contains cannot be edited as text yet (it needs a syntax tree, member names and the compile gate); decide whether to offer a text-only path for it
        var extension = Path.GetExtension(path.Absolute);
        if (CodeExtensions.Any(code => string.Equals(code, extension, PathComparison.Comparison)))
        {
            return NonProjectFileStatus.CodeFileOutsideProjects;
        }

        return LooksBinary(path.Absolute) ? NonProjectFileStatus.NotPlainText : NonProjectFileStatus.EligibleText;
    }

    private static bool IsUnderRoot(string absolutePath, string? solutionRoot)
    {
        if (string.IsNullOrWhiteSpace(solutionRoot))
        {
            return false;
        }

        var root = Path.GetFullPath(solutionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(absolutePath).StartsWith(root, PathComparison.Comparison);
    }

    private static bool LooksBinary(string absolutePath)
    {
        try
        {
            using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[BinarySniffBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch (IOException)
        {
            // unreadable now: not binary evidence; the edit path reports the real read failure
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
