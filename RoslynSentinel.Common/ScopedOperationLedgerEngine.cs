namespace RoslynSentinel.Common;

public class ScopedOperationLedgerEngine : IScopedOperationLedger
{
    private readonly object _lock = new();
    private string? _openOperationName;
    private List<LedgerEntryBase>? _entries;

    public bool TryOpen(string operationName, IReadOnlyList<LedgerEntryBase> entries, out string? rejectionReason, string? openingChangeId = null)
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
            _openingChangeId = openingChangeId;
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

            // Undoing the changeId that opened the ledger invalidates the whole thing: the
            // operation these entries were tracking no longer exists, so every entry (including
            // ones already individually fixed) goes back to unresolved rather than just the one
            // matching this changeId - there is nothing left for a per-entry fix to have resolved
            // against. This is the only place "invalidated" has a concrete meaning in this engine:
            // it reuses IsFixed=false (re-trips IsBlocked) rather than a separate entry state.
            var isOpeningChangeId = changeId == _openingChangeId;
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (isOpeningChangeId || entry.ChangeId == changeId)
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
            _openingChangeId = null;
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


    // Added by AddMember (expected - used for diagnostics)
    private string? _openingChangeId;
}
