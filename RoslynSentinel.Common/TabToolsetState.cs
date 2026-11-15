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
        throw new NotImplementedException(); // TODO implement (Step 2)
    }

    /// <summary>
    /// Persists the enabled toolsets via a temp file and File.Move(overwrite: true). Never throws.
    /// </summary>
    public void Save(IReadOnlyList<ToolSetName> enabled)
    {
        throw new NotImplementedException(); // TODO implement (Step 2)
    }

    /// <summary>Deletes the tab's toolset file, if present.</summary>
    public void Clear()
    {
        throw new NotImplementedException(); // TODO implement (Step 2)
    }
}
