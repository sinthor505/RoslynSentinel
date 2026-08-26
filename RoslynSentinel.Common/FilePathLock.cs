using System.Collections.Concurrent;

namespace RoslynSentinel.Common;

/// <summary>
/// Per-path async lock keyed by normalized full path. Used to serialize writes to the same file
/// across concurrent callers and to let other components (e.g. the file watcher) cheaply check
/// whether a write to a given path is currently in flight before touching the file themselves.
/// </summary>
public static class FilePathLock
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class Entry : IDisposable
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public int RefCount;

        public void Dispose()
        {
            this.Sem.Dispose();
        }
    }

    private static readonly ConcurrentDictionary<string, Entry> Map = new(PathComparer);

    /// <summary>
    /// Returns true if the given path currently has an active lock held by another caller.
    /// Intended for diagnostic and debugging use only; do not use for synchronization decisions.
    /// </summary>
    /// <param name="filePath">The file path to check.</param>
    /// <returns>True if a lock is currently held; false if no lock exists or the path is free.</returns>
    public static bool IsLocked(FilePath filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath.ToString());

        string key = Normalize(filePath);
        return Map.TryGetValue(key, out Entry? entry) && entry.Sem.CurrentCount == 0;
    }

    public static Task<IDisposable> AcquireAsync(FilePath filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath.ToString());

        return AcquireAsync(filePath, Timeout.InfiniteTimeSpan, ct);
    }

    private static async Task<IDisposable> AcquireAsync(FilePath filePath, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath.ToString());

        string key = Normalize(filePath);
        Entry entry = Map.AddOrUpdate(key,
            static s => new Entry { RefCount = 1 },
            static (s, e) =>
            {
                _ = Interlocked.Increment(ref e.RefCount);
                return e;
            });
        bool locked = false;
        try
        {
            locked = await entry.Sem.WaitAsync(timeout, ct).ConfigureAwait(false);
            if (!locked)
            {
                throw new TimeoutException($"Timed out acquiring lock for '{key}'.");
            }

            return new Releaser(key, entry);
        }
        catch
        {
            if (!locked) // only undo refcount if we didn't acquire the semaphore
            {
                if (Interlocked.Decrement(ref entry.RefCount) is 0)
                {
                    KeyValuePair<string, Entry> pair = new KeyValuePair<string, Entry>(key, entry);
                    if (((ICollection<KeyValuePair<string, Entry>>)Map).Remove(pair))
                    {
                        entry.Sem.Dispose();
                    }
                }
            }

            throw;
        }
    }

    private sealed class Releaser : IDisposable
    {
        private int _disposed;
        private readonly string _key;
        private readonly Entry _entry;

        public Releaser(string key, Entry entry)
        {
            this._key = key;
            this._entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this._disposed, 1) != 0)
            {
                return;
            }

            _ = this._entry.Sem.Release();

            if (Interlocked.Decrement(ref this._entry.RefCount) is 0)
            {
                KeyValuePair<string, Entry> pair = new KeyValuePair<string, Entry>(this._key, this._entry);
                if (((ICollection<KeyValuePair<string, Entry>>)Map).Remove(pair))
                {
                    this._entry.Sem.Dispose();
                }
            }
        }
    }

    private static string Normalize(FilePath filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath.ToString());

        string full = Path.GetFullPath(filePath.ToString());
        return Path.TrimEndingDirectorySeparator(full);
    }
}
