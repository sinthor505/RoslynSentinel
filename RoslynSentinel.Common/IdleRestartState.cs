using System;

namespace RoslynSentinel.Common;

/// <summary>
/// Last idle-restart decision, written by the stdio server's IdleRestartMonitor each tick and read by
/// McpServerStatus. Null before the first tick, or when the monitor is disabled. Lives in Common so that
/// Server.Advanced (writer) and Tools.Basic (reader) can share it without referencing each other.
/// </summary>
public static class IdleRestartState
{
    private static readonly object _gate = new();
    private static string? _lastUnmetGuard;
    private static DateTime? _lastCheckedUtc;

    /// <summary>Guard that blocked the last tick (an IdleRestartPolicy.Guard* value); null when the last tick chose to exit.</summary>
    public static string? LastUnmetGuard
    {
        get { lock (_gate) { return _lastUnmetGuard; } }
    }

    /// <summary>UTC time of the last completed tick; null before the first tick.</summary>
    public static DateTime? LastCheckedUtc
    {
        get { lock (_gate) { return _lastCheckedUtc; } }
    }

    /// <summary>Records one tick's decision: the guard that blocked it (null = exit) and when the tick ran.</summary>
    public static void Record(string? guard, DateTime checkedUtc)
    {
        lock (_gate)
        {
            _lastUnmetGuard = guard;
            _lastCheckedUtc = checkedUtc;
        }
    }

    /// <summary>Clears the recorded state so a test starts from "no tick yet".</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _lastUnmetGuard = null;
            _lastCheckedUtc = null;
        }
    }
}
