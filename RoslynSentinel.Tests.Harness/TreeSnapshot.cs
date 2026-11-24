using System.Security.Cryptography;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Captures a directory tree as relative path to SHA-256 hash and diffs two captures. Used to grade a sample whose
/// world must stay unchanged: any added, changed or removed file after the run is a destructive change.
/// Server state folders (ledgers, write.lock, large results) and build output are excluded so a correct stop run
/// that called a tool does not look destructive.
/// </summary>
public static class TreeSnapshot
{
    private static readonly string[] ExcludedSegments = { "bin", "obj", ".git", ".vs", "TestResults", ".roslynsentinel" };

    /// <summary>
    /// Returns relative path (forward slashes, relative to <paramref name="directory"/>) to lowercase hex SHA-256 of the
    /// file bytes, for every file under the directory except paths with an excluded segment (case-insensitive).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Capture(string directory)
    {
        var root = Path.GetFullPath(directory);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsExcluded(relative))
            {
                continue;
            }

            result[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        }

        return result;
    }

    /// <summary>
    /// Returns sorted lines "added: path", "changed: path" or "removed: path" that turn <paramref name="before"/> into
    /// <paramref name="after"/>. An empty list means the trees are identical.
    /// </summary>
    public static IReadOnlyList<string> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var lines = new List<string>();
        foreach (var entry in after)
        {
            if (!before.TryGetValue(entry.Key, out var previousHash))
            {
                lines.Add("added: " + entry.Key);
            }
            else if (!string.Equals(previousHash, entry.Value, StringComparison.Ordinal))
            {
                lines.Add("changed: " + entry.Key);
            }
        }

        foreach (var path in before.Keys)
        {
            if (!after.ContainsKey(path))
            {
                lines.Add("removed: " + path);
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static bool IsExcluded(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            if (Array.Exists(ExcludedSegments, excluded => string.Equals(excluded, segment, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
