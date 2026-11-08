using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>
/// The launcher's shared build selection, read from <c>latest.json</c> in the builds root. <c>HeadSha</c> is the dev repo
/// HEAD the launcher was asked to build, <c>BuildSha8</c> the 8-character SHA of the build it selected, <c>Selection</c>
/// how it chose that build, <c>BuildFailed</c> whether the build of <c>HeadSha</c> failed, and <c>Utc</c> the write time.
/// </summary>
public sealed record LatestBuildRecord(string? HeadSha, string? BuildSha8, string? Selection, bool BuildFailed, string? Utc);

/// <summary>
/// Reads the launcher's <c>latest.json</c>. A missing, unreadable or malformed file reads as null; nothing here throws.
/// </summary>
public static class LatestBuildFile
{
    /// <summary>File the launcher writes in the builds root.</summary>
    public const string FileName = "latest.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses the file JSON. Returns null for empty, malformed or non-object input.</summary>
    internal static LatestBuildRecord? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LatestBuildRecord>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads and parses <c>latest.json</c> in <paramref name="buildsRoot"/>; null when the root is unset, the file is absent, unreadable or malformed.</summary>
    public static LatestBuildRecord? Read(string? buildsRoot)
    {
        if (string.IsNullOrWhiteSpace(buildsRoot))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(buildsRoot, FileName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
