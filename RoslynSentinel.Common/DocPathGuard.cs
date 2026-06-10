namespace RoslynSentinel.Common;

/// <summary>
/// Defense-in-depth filename validation for scoped documentation tools.
/// Enforces path containment, extension allowlist, and Windows-specific safety checks.
/// All documentation tools that accept a filename parameter MUST call
/// <see cref="ResolveSafe"/> before performing any file I/O.
/// </summary>
public static class DocPathGuard
{
    private static readonly string[] AllowedExtensions = [".md", ".yaml", ".yml", ".json", ".txt"];

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    /// <summary>All documentation file extensions the guard permits.</summary>
    public static IReadOnlyList<string> AllowedDocExtensions => AllowedExtensions;

    /// <summary>
    /// Validates and resolves a bare filename within the given docs subdirectory root.
    /// </summary>
    /// <param name="docsSubdirRoot">
    /// Absolute path to the docs subdirectory (e.g. <c>&lt;solutionRoot&gt;/docs/plans/</c>).
    /// </param>
    /// <param name="filename">
    /// Caller-supplied filename. Must be a bare filename — any path component (slash, backslash,
    /// drive letter) causes immediate rejection.
    /// </param>
    /// <returns>
    /// <c>(true, absolutePath, "")</c> on success.
    /// <c>(false, "", errorMessage)</c> on any validation failure.
    /// </returns>
    public static (bool Ok, string FullPath, string Error) ResolveSafe(
        string docsSubdirRoot,
        string filename)
    {
        // 1. Strip to bare filename — discard all directory components.
        //    If the result differs from the input, path components were present.
        string bareName = Path.GetFileName(filename);
        if (bareName != filename)
            return (false, "", "Filename only — path components are not allowed.");

        if (string.IsNullOrWhiteSpace(bareName))
            return (false, "", "Empty filename.");

        // 2. Reject alternate data streams and invalid characters (Windows-specific).
        //    "notes.md:hidden.cs" is a valid NTFS ADS name but Path.GetFileName keeps the colon.
        if (bareName.Contains(':'))
            return (false, "", "Invalid character ':' in filename (alternate data stream).");

        if (bareName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return (false, "", "Invalid characters in filename.");

        // 3. Reject Windows reserved device names (CON, NUL, COM1, LPT1, etc.).
        //    Check the stem only so "CON.md" is caught as well as "CON".
        string nameNoExt = Path.GetFileNameWithoutExtension(bareName).ToUpperInvariant();
        if (ReservedNames.Contains(nameNoExt))
            return (false, "", $"'{nameNoExt}' is a reserved Windows filename.");

        // 4. Extension ALLOWLIST — closed set, matched exactly via Path.GetExtension.
        //    NEVER use substring matching (e.g. Contains(".cs")) — it over-matches and is gameable.
        string ext = Path.GetExtension(bareName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return (false, "", $"Extension '{ext}' not permitted. Allowed: {string.Join(", ", AllowedExtensions)}");

        // 5. Resolve and confirm containment (defense-in-depth backstop).
        //    GetFullPath resolves any remaining traversal sequences so the StartsWith check
        //    operates on the canonical absolute path.
        string fullPath = Path.GetFullPath(Path.Combine(docsSubdirRoot, bareName));
        string rootFull = Path.GetFullPath(docsSubdirRoot);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            rootFull += Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return (false, "", "Resolved path escapes the documentation root.");

        return (true, fullPath, "");
    }
}
