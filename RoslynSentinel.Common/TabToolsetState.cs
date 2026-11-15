using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

/// <summary>
/// The toolsets enabled for one tab, as persisted by <see cref="TabToolsetStateStore"/>.
/// </summary>
public sealed record TabToolsetState(IReadOnlyList<string> EnabledToolSets, string? SessionId, DateTime SavedUtc);

/// <summary>
/// Reads and writes the per-tab toolset file (<see cref="FileName"/>) inside the tab state directory.
/// </summary>
public sealed class TabToolsetStateStore
{
    /// <summary>File name of the persisted toolset state inside the tab state directory.</summary>
    public const string FileName = "toolsets.json";

    /// <summary>Shared serializer options for the toolset file (camelCase property names, indented).</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ILogger? _logger;

    /// <summary>Creates a store rooted at <paramref name="tabStateDir"/> for the given session id.</summary>
    public TabToolsetStateStore(string tabStateDir, string? sessionId, ILogger? logger = null)
    {
        TabStateDir = tabStateDir;
        SessionId = sessionId;
        _logger = logger;
    }

    /// <summary>Directory that holds the tab's toolset file.</summary>
    public string TabStateDir { get; }

    /// <summary>Session id used to seed the state when the tab's own file does not exist yet.</summary>
    public string? SessionId { get; }

    /// <summary>Toolsets currently configured for the tab.</summary>
    public IReadOnlyList<ToolSetName> ConfiguredToolSets { get; set; } = Array.Empty<ToolSetName>();

    /// <summary>True when the tab's toolsets were restored from persisted state at server startup.</summary>
    public bool RestoredAtStartup { get; set; }

    /// <summary>
    /// Loads the tab's own file, else seeds from the session id. Never throws; returns null on an IO or parse problem.
    /// </summary>
    public TabToolsetState? Load()
    {
        try
        {
            var ownPath = Path.Combine(TabStateDir, FileName);
            if (File.Exists(ownPath))
            {
                return System.Text.Json.JsonSerializer.Deserialize<TabToolsetState>(File.ReadAllText(ownPath), JsonOptions);
            }

            if (string.IsNullOrEmpty(SessionId))
            {
                return null;
            }

            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(TabStateDir)));
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return null;
            }

            var ownDir = Path.GetFullPath(TabStateDir);
            TabToolsetState? newest = null;
            string? newestPath = null;
            foreach (var dir in Directory.EnumerateDirectories(parent))
            {
                if (string.Equals(Path.GetFullPath(dir), ownDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var candidatePath = Path.Combine(dir, FileName);
                    if (!File.Exists(candidatePath))
                    {
                        continue;
                    }

                    var candidate = System.Text.Json.JsonSerializer.Deserialize<TabToolsetState>(File.ReadAllText(candidatePath), JsonOptions);
                    if (candidate is null || !string.Equals(candidate.SessionId, SessionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (newest is null || candidate.SavedUtc > newest.SavedUtc)
                    {
                        newest = candidate;
                        newestPath = candidatePath;
                    }
                }
                catch (Exception)
                {
                    // A sibling that fails to read or parse is skipped; it cannot seed this tab.
                }
            }

            if (newest is null || newestPath is null)
            {
                return null;
            }

            Directory.CreateDirectory(TabStateDir);
            File.Copy(newestPath, ownPath, overwrite: true);
            return newest;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load persisted tab toolset state from {TabStateDir}", TabStateDir);
            return null;
        }
    }

    /// <summary>
    /// Persists the enabled toolsets via a temp file and File.Move(overwrite: true). Never throws.
    /// </summary>
    public void Save(IReadOnlyList<ToolSetName> enabled)
    {
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(TabStateDir);
            var state = new TabToolsetState(enabled.Select(s => s.ToString()).ToArray(), SessionId, DateTime.UtcNow);
            tempPath = Path.Combine(TabStateDir, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, Path.Combine(TabStateDir, FileName), overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save tab toolset state to {TabStateDir}", TabStateDir);
        }
        finally
        {
            try
            {
                if (tempPath is not null && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not delete temp toolset file {TempPath}", tempPath);
            }
        }
    }

    /// <summary>Deletes the tab's toolset file, if present. Never throws.</summary>
    public void Clear()
    {
        try
        {
            File.Delete(Path.Combine(TabStateDir, FileName));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not clear tab toolset state in {TabStateDir}", TabStateDir);
        }
    }
}
