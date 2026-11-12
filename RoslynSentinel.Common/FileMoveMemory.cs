namespace RoslynSentinel.Common;

/// <summary>
/// Remembers the files this server moved or renamed (old path -> new path, and the tool that did it), so a later
/// "file not found" for the OLD path can name the new one instead of hedging (eval run 20261112-151531-550 T8-T12: RenameSymbol
/// moved OrderPricingCalculator.cs, the model read the old path and needed two more calls to find the new one).
/// A chain A -> B -> C resolves to C. Paths are compared with <see cref="PathComparison"/>. Cleared when a solution is
/// (re)loaded, because the record describes the previous session's files.
/// DEVIATION from a DI singleton, on purpose: the MCP pipeline builds a fresh tool-class instance per call (ActivatorUtilities, not the
/// registered singleton), the Impl classes are also constructed by hand in tests and by the legacy facades, and the not-found
/// text is built in static helpers in Common, so a constructor-injected instance would be empty at the next call. Same reasoning
/// and shape as the other process-wide per-call memories (<c>RewrittenLineMemory.Shared</c>); tests use their own instance.
/// DTO-EXEMPT: no new data type, the lookup returns its result through out parameters.
/// </summary>
public sealed class FileMoveMemory
{
    // Chain-following guard; Record already keeps the map acyclic, so this only bounds a pathological chain.
    private const int MaxChainHops = 32;

    /// <summary>The process-wide instance used by the tools and the not-found messages.</summary>
    public static FileMoveMemory Shared { get; } = new();

    private readonly Dictionary<string, (string NewPath, string ToolName)> _moves = new(PathComparison.Comparer);
    private readonly object _gate = new();

    /// <summary>Records that <paramref name="toolName"/> moved the file at <paramref name="oldPath"/> to <paramref name="newPath"/> (both absolute).</summary>
    public void Record(string oldPath, string newPath, string toolName)
    {
        var oldKey = Normalize(oldPath);
        var newKey = Normalize(newPath);
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey) || PathComparison.Comparer.Equals(oldKey, newKey))
        {
            return;
        }

        lock (_gate)
        {
            // A file now lives at newKey, so an older "newKey moved away" row is stale; dropping it also keeps the map acyclic.
            _moves.Remove(newKey);
            _moves[oldKey] = (newKey, toolName);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> was moved: <paramref name="currentPath"/> is where its file lives now (the end of the chain)
    /// and <paramref name="toolName"/> is the tool of the first move, the call that took the file away from <paramref name="path"/>.
    /// </summary>
    public bool TryGetCurrentPath(string path, out string currentPath, out string toolName)
    {
        currentPath = string.Empty;
        toolName = string.Empty;
        var key = Normalize(path);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_moves.TryGetValue(key, out var first))
            {
                return false;
            }

            toolName = first.ToolName;
            currentPath = first.NewPath;
            for (var hops = 0; hops < MaxChainHops && _moves.TryGetValue(currentPath, out var next); hops++)
            {
                currentPath = next.NewPath;
            }

            return true;
        }
    }

    /// <summary>Forgets every recorded move (a solution was (re)loaded).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _moves.Clear();
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
