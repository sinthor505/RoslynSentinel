using System.Text;
using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>One recorded touch of a method by a migration phase.</summary>
public class LedgerOperation
{
    public string Phase { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public int Run { get; set; }
}

/// <summary>Accumulated history for a single method across all migration runs.</summary>
public class LedgerEntry
{
    public string Key { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string MethodName { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>Total number of times any phase has touched this method. > 1 indicates re-entry.</summary>
    public int HitCount { get; set; }
    public List<LedgerOperation> Operations { get; set; } = new();
}

/// <summary>Filtered view returned by <c>GetMigrationLedger</c>.</summary>
public class LedgerSnapshot
{
    public int RunCount { get; set; }
    public int TotalEntries { get; set; }
    public int TotalOperations { get; set; }
    /// <summary>Number of methods touched more than once — the primary re-entry diagnostic.</summary>
    public int RepeatedMethods { get; set; }
    public List<LedgerEntry> Entries { get; set; } = new();
}

internal class LedgerData
{
    public int RunCount { get; set; }
    public List<LedgerEntry> Entries { get; set; } = new();
}

/// <summary>
/// Singleton service that records every method mutated by a migration phase and persists the
/// ledger to <c>.roslynsentinel/migration-ledger.json</c> under the solution root after each
/// Asyncify run. Survives server restarts — data accumulates across multiple sessions.
///
/// Phase tokens used by AsyncBatchEngine:
///   "Bridge"               — method converted to async-bridge pattern
///   "BridgeStaleSkip"      — bridge skipped because async overload already has CT
///   "Uplift"               — caller uplifted to async overload
///   "UpliftIdempotentSkip" — uplift skipped because file already contains the transformation
///   "CtPropagated"         — CancellationToken forwarded through a file
/// </summary>
public class MigrationLedger
{
    private readonly Dictionary<string, LedgerEntry> _entries = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private string? _solutionRoot;
    private bool _loaded;
    private int _runCount;
    private int _currentRun;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Increments the run counter and sets it as the current run for subsequent
    /// <see cref="Record"/> calls. Call once at the start of each <c>AsyncifyCore</c> invocation.
    /// </summary>
    public int BeginRun()
    {
        _currentRun = Interlocked.Increment(ref _runCount);
        return _currentRun;
    }

    /// <summary>
    /// Loads the ledger from disk for <paramref name="solutionRoot"/> if not already loaded.
    /// Safe to call on every <c>AsyncifyCore</c> entry — no-ops when root is unchanged.
    /// </summary>
    public async Task EnsureLoadedAsync(string? solutionRoot)
    {
        if (string.IsNullOrEmpty(solutionRoot)) return;
        if (_loaded && _solutionRoot == solutionRoot) return;

        if (_solutionRoot != null && _solutionRoot != solutionRoot)
        {
            // Different solution loaded — start fresh rather than mixing two codebases.
            lock (_lock)
            {
                _entries.Clear();
                _runCount = 0;
                _currentRun = 0;
            }
        }

        _solutionRoot = solutionRoot;
        await LoadFromDiskAsync();
        _loaded = true;
    }

    /// <summary>
    /// Records a phase touch for the given method. Uses the run number set by the most recent
    /// <see cref="BeginRun"/> call. Thread-safe.
    /// </summary>
    public void Record(string filePath, string methodName, string phase)
    {
        var key = $"{filePath}::{methodName}";
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new LedgerEntry
                {
                    Key = key,
                    FilePath = filePath,
                    MethodName = methodName,
                    FirstSeen = now,
                };
                _entries[key] = entry;
            }
            entry.LastSeen = now;
            entry.HitCount++;
            entry.Operations.Add(new LedgerOperation { Phase = phase, Timestamp = now, Run = _currentRun });
        }
    }

    /// <summary>
    /// Writes the ledger to disk. Called after each Asyncify run so progress is never lost
    /// to a server restart. Never throws — failures are silently swallowed.
    /// </summary>
    public async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(_solutionRoot)) return;
        await _saveLock.WaitAsync();
        try
        {
            LedgerData data;
            lock (_lock)
            {
                data = new LedgerData { RunCount = _runCount, Entries = _entries.Values.ToList() };
            }
            var path = LedgerFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(data, JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch { /* non-fatal — log callers handle observability */ }
        finally { _saveLock.Release(); }
    }

    /// <summary>Returns a filtered snapshot of the current in-memory ledger.</summary>
    /// <param name="phase">When set, only entries that have at least one operation for this phase are included.</param>
    /// <param name="repeatedOnly">When true, only entries touched more than once are included.</param>
    public LedgerSnapshot GetSnapshot(string? phase = null, bool repeatedOnly = false)
    {
        lock (_lock)
        {
            var entries = _entries.Values.AsEnumerable();
            if (phase != null)
                entries = entries.Where(e => e.Operations.Any(o => o.Phase == phase));
            if (repeatedOnly)
                entries = entries.Where(e => e.HitCount > 1);
            var list = entries.OrderByDescending(e => e.HitCount).ThenBy(e => e.MethodName).ToList();
            return new LedgerSnapshot
            {
                RunCount = _runCount,
                TotalEntries = _entries.Count,
                TotalOperations = _entries.Values.Sum(e => e.Operations.Count),
                RepeatedMethods = _entries.Values.Count(e => e.HitCount > 1),
                Entries = list,
            };
        }
    }

    /// <summary>Clears all in-memory entries and writes the empty state to disk.</summary>
    public async Task ResetAsync()
    {
        lock (_lock)
        {
            _entries.Clear();
            _runCount = 0;
            _currentRun = 0;
        }
        await SaveAsync();
    }

    private async Task LoadFromDiskAsync()
    {
        var path = LedgerFilePath();
        if (!File.Exists(path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var data = JsonSerializer.Deserialize<LedgerData>(json, JsonOpts);
            if (data == null) return;
            lock (_lock)
            {
                _runCount = data.RunCount;
                _currentRun = data.RunCount;
                _entries.Clear();
                foreach (var e in data.Entries)
                    _entries[e.Key] = e;
            }
        }
        catch { /* corrupt file — start fresh */ }
    }

    private string LedgerFilePath() =>
        Path.Combine(_solutionRoot!, ".roslynsentinel", "migration-ledger.json");
}
