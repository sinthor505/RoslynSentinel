namespace RoslynSentinel.Common;

public class ScopedOperationLedgerEngine : IScopedOperationLedger
{
    private readonly object _lock = new();
    private string? _openOperationName;
    private List<LedgerEntryBase>? _entries;

    public bool TryOpen(string operationName, IReadOnlyList<LedgerEntryBase> entries, out string? rejectionReason)
    {
        lock (_lock)
        {
            if (_entries is not null)
            {
                rejectionReason = $"A ledger is already open for operation '{_openOperationName}' with {_entries.Count} entries. Resolve it before starting another blast-radius operation.";
                return false;
            }

            _openOperationName = operationName;
            _entries = [.. entries];
            rejectionReason = null;
            return true;
        }
    }

    public bool IsBlocked(FilePathWrapper filePath, out string? blockReason)
    {
        lock (_lock)
        {
            if (_entries is null)
            {
                blockReason = null;
                return false;
            }

            var normalized = filePath.ToString();
            var hasUnresolvedEntryForFile = _entries.Any(e => !e.IsFixed && string.Equals(e.FilePath, normalized, StringComparison.OrdinalIgnoreCase));
            if (hasUnresolvedEntryForFile)
            {
                blockReason = null;
                return false;
            }

            var anyUnresolved = _entries.Any(e => !e.IsFixed);
            if (!anyUnresolved)
            {
                blockReason = null;
                return false;
            }

            blockReason = $"A scoped ledger is open for operation '{_openOperationName}' with unresolved entries outside this file. Resolve the open ledger's entries before making unrelated changes.";
            return true;
        }
    }

    public void RecordFix(IReadOnlyList<string> entryIds, string changeId)
    {
        lock (_lock)
        {
            if (_entries is null)
            {
                return;
            }

            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entryIds.Contains(entry.EntryId))
                {
                    _entries[i] = entry switch
                    {
                        CallSiteLedgerEntry callSite => callSite with { IsFixed = true, ChangeId = changeId },
                        _ => entry
                    };
                }
            }
        }
    }

    public void RecordUndo(string changeId)
    {
        lock (_lock)
        {
            if (_entries is null)
            {
                return;
            }

            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.ChangeId == changeId)
                {
                    _entries[i] = entry switch
                    {
                        CallSiteLedgerEntry callSite => callSite with { IsFixed = false },
                        _ => entry
                    };
                }
            }
        }
    }

    public bool TryRelease()
    {
        lock (_lock)
        {
            if (_entries is null)
            {
                return false;
            }

            if (_entries.Any(e => !e.IsFixed))
            {
                return false;
            }

            _entries = null;
            _openOperationName = null;
            return true;
        }
    }

    public IReadOnlyList<LedgerEntryBase> GetOpenEntries()
    {
        lock (_lock)
        {
            return _entries is null ? [] : [.. _entries];
        }
    }
}
