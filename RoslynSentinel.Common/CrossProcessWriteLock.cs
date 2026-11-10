using System.Globalization;
using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>
/// Cross-process write lock for the solution's write chokepoint, held as a FileShare.None lock file under
/// <c>&lt;solutionRoot&gt;/.roslynsentinel/write.lock</c>. Rules:
/// (a) callers take _solutionLock first, then this lock, never the reverse;
/// (b) the lock is a FileShare.None FileStream and NOT re-entrant: a second acquire from the same process fails
///     like another process's, so acquire only at entry points and never inside ApplyInMemoryDocumentUpdatesAsync;
/// (c) never hold it across compile validation or a full MSBuild reload;
/// (d) the holder metadata is a sidecar file (write.lock.holder.json) because the exclusive holder's lock file
///     cannot be read by a waiter.
/// </summary>
public static class CrossProcessWriteLock
{
    private const string LockFileName = "write.lock";
    private const string HolderFileName = "write.lock.holder.json";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Tries to take the lock, polling every 50 ms until <paramref name="wait"/> elapses. On timeout returns a
    /// result with no lease and the best-effort holder read from the sidecar (null when missing or unparsable).
    /// Throws OperationCanceledException only when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static async Task<WriteLockAcquireResult> AcquireAsync(string solutionRoot, WriteLockHolder holder, TimeSpan wait, CancellationToken ct)
    {
        var dir = Path.Combine(solutionRoot, ".roslynsentinel");
        Directory.CreateDirectory(dir);
        var lockPath = Path.Combine(dir, LockFileName);
        var holderPath = Path.Combine(dir, HolderFileName);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                WriteHolderSidecar(holderPath, holder);
                return new WriteLockAcquireResult(new WriteLockLease(stream, holderPath), null);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return new WriteLockAcquireResult(null, ReadHolderSidecar(holderPath));
                }
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    private static void WriteHolderSidecar(string holderPath, WriteLockHolder holder)
    {
        try
        {
            File.WriteAllText(holderPath, JsonSerializer.Serialize(holder));
        }
        catch (IOException)
        {
            // Metadata only: a missing sidecar just means waiters report the holder as unknown.
        }
        catch (UnauthorizedAccessException)
        {
            // Metadata only: a missing sidecar just means waiters report the holder as unknown.
        }
    }

    private static WriteLockHolder? ReadHolderSidecar(string holderPath)
    {
        try
        {
            return JsonSerializer.Deserialize<WriteLockHolder>(File.ReadAllText(holderPath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Formats a busy holder for a refusal message; null parts read as "unknown".</summary>
    internal static string FormatBusy(WriteLockHolder? busy)
    {
        if (busy is null)
        {
            return "another session (holder unknown) is writing; retry in a moment";
        }
        var agent = busy.AgentId ?? "unknown";
        var tool = busy.ToolName ?? "unknown";
        var since = busy.SinceUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return $"another session ({agent}, {tool}, since {since} UTC) is writing; retry in a moment";
    }
}

/// <summary>Who holds (or last held) the cross-process write lock. Any string may be null.</summary>
public sealed record WriteLockHolder(string? SessionId, string? AgentId, string? ToolName, string? Reason, DateTime SinceUtc);

/// <summary>Wait budgets for the cross-process write lock. Defaults only; startup wiring is a later slice.</summary>
public sealed record CrossProcessWriteLockOptions
{
    public TimeSpan ChokepointWait { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DebounceWait { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan FoldOnReadWait { get; init; } = TimeSpan.FromSeconds(1);

    // TODO wire startup arg per the LlmOptions pattern
}

/// <summary>
/// An acquired cross-process write lock. Dispose releases it: the sidecar is deleted first (best effort), then the
/// FileStream is disposed, so a waiter that acquires right after never has its fresh sidecar deleted by us.
/// </summary>
public sealed class WriteLockLease : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly string _sidecarPath;
    private int _disposed;

    internal WriteLockLease(FileStream stream, string sidecarPath)
    {
        _stream = stream;
        _sidecarPath = sidecarPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            File.Delete(_sidecarPath);
        }
        catch (IOException)
        {
            // Best effort: a stale sidecar is replaced by the next acquirer.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: a stale sidecar is replaced by the next acquirer.
        }
        await _stream.DisposeAsync();
    }
}

/// <summary>Outcome of <see cref="CrossProcessWriteLock.AcquireAsync"/>: a lease when acquired, else the busy holder.</summary>
public sealed record WriteLockAcquireResult(WriteLockLease? Lease, WriteLockHolder? BusyHolder)
{
    public bool Acquired => Lease is not null;

    /// <summary>Refusal text for a busy lock, naming the holder when known.</summary>
    public string BusyMessage() => CrossProcessWriteLock.FormatBusy(BusyHolder);
}
