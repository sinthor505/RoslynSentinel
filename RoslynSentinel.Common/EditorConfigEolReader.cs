using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

public static class EditorConfigEolReader
{
    // Keyed by directory plus extension, not by file: [*.cs] style globs make the result depend on the file name.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Signature, string? Value)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns every existing .editorconfig from <paramref name="directory"/> up to the filesystem root, nearest first.</summary>
    private static List<string> FindEditorConfigPaths(string directory)
    {
        var paths = new List<string>();
        for (var dir = directory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, ".editorconfig");
            if (File.Exists(candidate))
            {
                paths.Add(candidate);
            }
        }

        return paths;
    }

    private static string BuildSignature(List<string> editorConfigPaths)
    {
        return string.Join(";", editorConfigPaths.Select(path => path + "|" + File.GetLastWriteTimeUtc(path).Ticks));
    }

    /// <summary>Parses the .editorconfig files and returns the effective end_of_line for the source path, or null.</summary>
    private static string? ReadEndOfLine(string fullPath, List<string> editorConfigPaths)
    {
        // AnalyzerConfigSet drops configs above a root = true file, so the full walk is safe to pass in.
        var configs = editorConfigPaths
            .Select(path => AnalyzerConfig.Parse(File.ReadAllText(path), path))
            .ToList();
        if (configs.Count == 0)
        {
            return null;
        }

        var options = AnalyzerConfigSet.Create(configs).GetOptionsForSourcePath(fullPath).AnalyzerOptions;
        if (!options.TryGetValue("end_of_line", out var value))
        {
            return null;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Returns the lowercased, trimmed <c>end_of_line</c> value that .editorconfig files apply to
    /// <paramref name="filePath"/>, or null when none applies or none sets it. Never throws.
    /// The file itself does not need to exist.
    /// </summary>
    public static string? Read(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var editorConfigPaths = FindEditorConfigPaths(directory);
            // Stat-only signature so that editing .editorconfig is picked up without a server restart.
            var signature = BuildSignature(editorConfigPaths);
            var cacheKey = directory + "|" + Path.GetExtension(fullPath).ToLowerInvariant();
            if (Cache.TryGetValue(cacheKey, out var cached) && cached.Signature == signature)
            {
                return cached.Value;
            }

            var value = ReadEndOfLine(fullPath, editorConfigPaths);
            Cache[cacheKey] = (signature, value);
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
