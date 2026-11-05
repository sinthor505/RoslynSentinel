using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

/// <summary>
/// Persists <see cref="ToolTelemetry.Snapshot"/> to <c>server-telemetry_&lt;instanceId&gt;.json</c> in the instance working
/// directory so the counters survive server death (the launch script sweeps stale instance folders). Active only when the
/// working directory was passed explicitly (<see cref="ServerInstancePaths.IsExplicit"/>); otherwise every method is a
/// cheap no-op, so an ordinary single-instance run writes nothing.
/// <para>
/// The file is the <c>toolTelemetry</c> wire object of McpServerStatus (same camelCase shape) plus two leading fields:
/// <c>schemaVersion</c> (1) and <c>writtenUtc</c>. It is written IN PLACE under <see cref="FilePathLock"/> via
/// <see cref="FileIoHelper"/> with no temp-then-rename (owner ruling), so a reader may observe a partially written file
/// during a write; readers must tolerate unreadable or partial JSON (the rollup script skips and reports such files).
/// </para>
/// <para>
/// <see cref="FlushIfDirtyAsync"/> is the unit of work: it writes only when <see cref="ToolTelemetry.Version"/> moved since
/// the last successful write, and never throws (failures are logged at warning level, once per distinct message, and the
/// next call retries). The timer and the shutdown hook live in Server.Advanced (ToolTelemetryFlushService).
/// </para>
/// </summary>
public sealed class ToolTelemetryFlusher
{
    /// <summary>File-name prefix; the full name is <see cref="FileNameFor"/>.</summary>
    public const string FileNamePrefix = "server-telemetry_";

    /// <summary>Value of the file's top-level <c>schemaVersion</c>.</summary>
    public const int SchemaVersion = 1;

    // Same wire conventions as the status result: camelCase, relaxed escaping, string enums. Compact: the file is small and
    // read by scripts. Dictionary keys (error codes) are left as-is, as on the wire.
    private static readonly JsonSerializerOptions WireJson = new(SharedJsonOptions.Compact)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const int MaxDistinctWarnings = 16;

    private readonly ToolTelemetry _telemetry;
    private readonly ILogger? _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private long _flushedVersion;

    /// <param name="telemetry">The counters to persist.</param>
    /// <param name="paths">Where to write; defaults to <see cref="ServerInstancePaths.Current"/>. Tests pass
    /// <c>ServerInstancePaths.Resolve(["--working-dir=" + dir], dir)</c> (explicit) or <c>Resolve([], dir)</c> (not explicit).</param>
    /// <param name="logger">Optional; warnings only.</param>
    /// <param name="clock">Source of <c>writtenUtc</c>; defaults to <see cref="TimeProvider.System"/>.</param>
    public ToolTelemetryFlusher(
        ToolTelemetry telemetry,
        ServerInstancePaths? paths = null,
        ILogger? logger = null,
        TimeProvider? clock = null)
    {
        _telemetry = telemetry;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        var resolved = paths ?? ServerInstancePaths.Current;
        IsEnabled = resolved.IsExplicit;
        FilePath = IsEnabled ? Path.Combine(resolved.WorkingDir, FileNameFor(telemetry.InstanceId)) : null;
    }

    /// <summary>True iff the working directory is explicit; when false nothing is ever written.</summary>
    public bool IsEnabled { get; }

    /// <summary>Full path of the telemetry file; null when not <see cref="IsEnabled"/>.</summary>
    public string? FilePath { get; }

    /// <summary><c>server-telemetry_&lt;instanceId&gt;.json</c> with invalid file-name characters stripped (blank becomes "default").</summary>
    public static string FileNameFor(string instanceId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string((instanceId ?? string.Empty).Where(c => Array.IndexOf(invalid, c) < 0).ToArray())
            .TrimEnd('.', ' ')
            .Trim();
        return FileNamePrefix + (safe.Length == 0 ? ToolTelemetry.DefaultInstanceId : safe) + ".json";
    }

    /// <summary>The file content: the snapshot's wire shape with <c>schemaVersion</c> and <c>writtenUtc</c> first.</summary>
    public static string Serialize(ToolTelemetrySnapshot snapshot, DateTime writtenUtc)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, WireJson)!.AsObject();
        node.Insert(0, "writtenUtc", JsonValue.Create(writtenUtc));
        node.Insert(0, "schemaVersion", JsonValue.Create(SchemaVersion));
        return node.ToJsonString(WireJson);
    }

    /// <summary>
    /// Writes the snapshot if enabled and anything was recorded since the last successful write. Returns true iff a file
    /// was written. Never throws: an I/O failure (or cancellation) returns false, leaving the telemetry dirty so the next
    /// call retries.
    /// </summary>
    public async Task<bool> FlushIfDirtyAsync(CancellationToken cancellationToken = default)
    {
        // Cheap check first so an idle tick costs one volatile read and one interlocked read.
        if (!IsEnabled || _telemetry.Version == Volatile.Read(ref _flushedVersion))
        {
            return false;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Version BEFORE the snapshot: a call recorded in between is flushed again next time, never lost.
                var version = _telemetry.Version;
                if (version == Volatile.Read(ref _flushedVersion))
                {
                    return false;
                }

                var json = Serialize(_telemetry.Snapshot(), _clock.GetUtcNow().UtcDateTime);
                await FileIoHelper.WriteAllTextAsync(new FilePathWrapper(FilePath!), json, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _flushedVersion, version);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            WarnOnce($"Tool telemetry flush to '{FilePath}' failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void WarnOnce(string message)
    {
        try
        {
            lock (_warned)
            {
                if (_warned.Count >= MaxDistinctWarnings || !_warned.Add(message))
                {
                    return;
                }
            }

            if (_logger is { } logger && logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning("{Message}", message);
            }
        }
        catch (Exception)
        {
            // A broken logger must not turn a diagnostic write into a failure.
        }
    }
}
