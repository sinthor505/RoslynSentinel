namespace RoslynSentinel.Common;

/// <summary>Kind of file write recorded in the edit attribution ledger.</summary>
public enum EditAttributionOp { Write, Create, Delete }

/// <summary>
/// One recorded write. HashAfter is null for a delete. AgentId is null when the call carried no identity.
/// </summary>
public sealed record EditAttribution(long Seq, string? AgentId, string? AgentType, string? Tool, string? Reason, DateTime UtcTime, EditAttributionOp Op, string? HashAfter);

/// <summary>
/// Bounded per-file history of writes, in memory only, not persisted, one instance per PersistentWorkspaceManager.
/// This is the edit attribution ledger. It is distinct from the scoped operation ledger (IScopedOperationLedger),
/// which tracks multi-step edits.
/// </summary>
public class EditAttributionLedger
{
    /// <summary>Maximum entries kept per file; the oldest entry is dropped first.</summary>
    public const int MaxEntriesPerFile = 8;

    /// <summary>Maximum length of a stored reason.</summary>
    public const int MaxReasonLength = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<EditAttribution>> _byPath = new(PathComparison.Comparer);
    private long _seq;

    /// <summary>Sequence number of the most recent append in this edit attribution ledger, or 0 when none.</summary>
    public long CurrentSeq => Interlocked.Read(ref _seq);

    /// <summary>
    /// Records one write in the edit attribution ledger. This is a test seam, so a test can override it to make recording throw.
    /// </summary>
    public virtual EditAttribution Append(string path, ToolCallContext? context, EditAttributionOp op, string? hashAfter)
    {
        lock (_gate)
        {
            var entry = new EditAttribution(
                Interlocked.Increment(ref _seq),
                context?.AgentId,
                context?.AgentType,
                context?.ToolName,
                SanitizeReason(context?.Reason),
                DateTime.UtcNow,
                op,
                hashAfter);
            if (!_byPath.TryGetValue(path, out var list))
            {
                list = new List<EditAttribution>();
                _byPath[path] = list;
            }

            list.Add(entry);
            if (list.Count > MaxEntriesPerFile)
            {
                list.RemoveAt(0);
            }

            return entry;
        }
    }

    /// <summary>Last recorded write for the path in this edit attribution ledger, or null when the path is unknown.</summary>
    public EditAttribution? GetLatest(string path)
    {
        lock (_gate)
        {
            if (!_byPath.TryGetValue(path, out var list) || list.Count == 0)
            {
                return null;
            }

            return list[list.Count - 1];
        }
    }

    /// <summary>Copy of the recorded writes for the path, oldest first; empty when the path is unknown.</summary>
    public IReadOnlyList<EditAttribution> GetHistory(string path)
    {
        lock (_gate)
        {
            return _byPath.TryGetValue(path, out var list) ? list.ToArray() : Array.Empty<EditAttribution>();
        }
    }

    private static string? SanitizeReason(string? reason)
    {
        if (reason is null)
        {
            return null;
        }

        var chars = reason.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
            else if (chars[i] > 126)
            {
                chars[i] = '?';
            }
        }

        var text = new string(chars);
        return text.Length > MaxReasonLength ? text.Substring(0, MaxReasonLength) : text;
    }
}

/// <summary>Exposes the server's edit attribution ledger.</summary>
public interface IEditAttributionLedgerProvider
{
    /// <summary>The single live instance, owned by the workspace manager.</summary>
    EditAttributionLedger EditAttributions { get; }
}
