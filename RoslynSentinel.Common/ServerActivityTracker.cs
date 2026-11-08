using System;
using System.Threading;

namespace RoslynSentinel.Common;

/// <summary>
/// Tracks when the server last did work and how many tool calls are in flight. The idle-restart guard
/// reads both: a restart is only safe when nothing is in flight and nothing has happened for the idle period.
/// <para>
/// <see cref="Shared"/> is the process-wide instance. The tool-telemetry filter brackets every tool call with
/// <see cref="Enter"/>, and the write chokepoint (<c>ApplyProposedChangesAsync</c>) calls <see cref="Touch"/>, so a
/// long single call that keeps writing keeps resetting the idle clock. Tests construct their own instance with a
/// fake clock instead of using <see cref="Shared"/>.
/// </para>
/// </summary>
public sealed class ServerActivityTracker
{
    private readonly Func<DateTime> _utcNow;
    private long _lastActivityTicks;
    private int _inFlightCount;

    /// <summary>The process-wide tracker, driven by the tool-telemetry filter and the write chokepoint.</summary>
    public static ServerActivityTracker Shared { get; } = new();

    /// <summary>
    /// Creates a tracker. A new tracker counts as active at construction, so a freshly started server is not idle.
    /// </summary>
    /// <param name="utcNow">Clock returning UTC time. Defaults to <see cref="DateTime.UtcNow"/>.</param>
    public ServerActivityTracker(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _lastActivityTicks = _utcNow().Ticks;
    }

    /// <summary>The UTC time of the most recent recorded activity.</summary>
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    /// <summary>The number of tool calls currently in flight.</summary>
    public int InFlightCount => Volatile.Read(ref _inFlightCount);

    /// <summary>Records activity now, resetting the idle clock.</summary>
    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, _utcNow().Ticks);

    /// <summary>
    /// Counts a tool call as in flight until the returned scope is disposed. Entering and leaving both count as activity.
    /// </summary>
    /// <returns>A scope whose disposal ends the in-flight count. Disposing it more than once is harmless.</returns>
    public IDisposable Enter()
    {
        Interlocked.Increment(ref _inFlightCount);
        Touch();
        return new Scope(this);
    }

    private sealed class Scope(ServerActivityTracker owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            owner.Touch();
            Interlocked.Decrement(ref owner._inFlightCount);
        }
    }
}
