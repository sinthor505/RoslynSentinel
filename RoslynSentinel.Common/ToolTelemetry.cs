using System.Collections.Concurrent;

namespace RoslynSentinel.Common;

/// <summary>
/// Process-wide, thread-safe per-tool call counters: calls, successes, errors (plus a count per error code), total and
/// maximum duration, first and last call time. Updated by the single call-tool telemetry filter (Server.Advanced) via
/// <see cref="Record"/>; surfaced by McpServerStatus through <see cref="Snapshot"/>. Telemetry is diagnostic only, so
/// nothing here throws for odd input (null names, unbounded name or code cardinality are bucketed).
/// <para>
/// Each tool's counters sit behind that tool's own lock, so a snapshot of one tool is always internally consistent
/// (successes + errors = calls) while different tools never contend.
/// </para>
/// </summary>
public sealed class ToolTelemetry
{
    /// <summary>Environment variable that names this instance explicitly.</summary>
    public const string InstanceIdEnvVar = "ROSLYNSENTINEL_INSTANCE_ID";

    /// <summary>Instance id when neither the env var nor an explicit working directory names one.</summary>
    public const string DefaultInstanceId = "default";

    /// <summary>Error code recorded when a failed call carries none we can read.</summary>
    public const string UnknownErrorCode = "Unknown";

    /// <summary>Tool name recorded for a call with a null or blank name.</summary>
    public const string UnknownToolName = "(unknown)";

    /// <summary>Bucket for tool names / error codes past the caps below.</summary>
    public const string OverflowKey = "(other)";

    /// <summary>Distinct tool names kept; a client-chosen name beyond this is counted under <see cref="OverflowKey"/>.</summary>
    public const int MaxTools = 512;

    /// <summary>Distinct error codes kept per tool; further codes are counted under <see cref="OverflowKey"/>.</summary>
    public const int MaxErrorCodesPerTool = 64;

    /// <summary>Pending suggestions kept; past this the oldest is dropped and counted as ignored.</summary>
    public const int MaxPendingGuidance = 64;

    /// <summary>A pending suggestion older than this (by the injected clock) is expired as ignored.</summary>
    public static readonly TimeSpan GuidanceTimeout = TimeSpan.FromMinutes(2);

    /// <summary>A pending suggestion is expired as ignored once more than this many calls have started since it was issued.</summary>
    public const int GuidanceMaxCallDistance = 3;

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, ToolEntry> _tools = new(StringComparer.Ordinal);
    private long _version;

    // Guidance adherence state (all guarded by _guidanceGate): suggestions emitted by hints and not yet followed or expired,
    // oldest first, and the count of calls started so far. Counts are APPROXIMATE with concurrent subagents: stdio carries
    // no session id, so interleaved agents can consume each other's suggestions (and push each other's past the call-distance
    // limit).
    private readonly object _guidanceGate = new();
    private readonly List<PendingSuggestion> _pending = [];
    private long _callSequence;

    /// <param name="clock">Injectable time source (tests); defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="instanceId">Defaults to <see cref="ResolveInstanceId"/>.</param>
    /// <param name="buildSha">Defaults to <see cref="ServerBuildInfo.BuildSha"/>.</param>
    public ToolTelemetry(TimeProvider? clock = null, string? instanceId = null, string? buildSha = null)
    {
        _clock = clock ?? TimeProvider.System;
        InstanceId = string.IsNullOrWhiteSpace(instanceId) ? ResolveInstanceId() : instanceId;
        BuildSha = buildSha ?? ServerBuildInfo.BuildSha;
        ServerStartedUtc = _clock.GetUtcNow().UtcDateTime;
    }

    public string InstanceId { get; }

    public string? BuildSha { get; }

    public DateTime ServerStartedUtc { get; }

    /// <summary>True once at least one call has been recorded.</summary>
    public bool HasCalls => !_tools.IsEmpty;

    /// <summary>
    /// Monotonic change counter: bumped after every <see cref="Record"/>. Read it BEFORE <see cref="Snapshot"/>; a flusher that
    /// remembers the value it read then re-flushes whenever a later read is larger (a call recorded mid-snapshot is at worst
    /// flushed twice, never missed).
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Instance name: <see cref="InstanceIdEnvVar"/> if set; else the leaf folder of <see cref="ServerInstancePaths.WorkingDir"/>
    /// when that directory was passed explicitly (--working-dir); else <see cref="DefaultInstanceId"/>.
    /// </summary>
    public static string ResolveInstanceId()
    {
        var fromEnv = Environment.GetEnvironmentVariable(InstanceIdEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        try
        {
            var paths = ServerInstancePaths.Current;
            if (paths.IsExplicit)
            {
                var leaf = Path.GetFileName(paths.WorkingDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(leaf))
                {
                    return leaf;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ToolTelemetry instance id resolution failed: {ex}");
        }

        return DefaultInstanceId;
    }

    /// <summary>Records one finished call. <paramref name="errorCode"/> is used only when <paramref name="isError"/> is true.</summary>
    public void Record(string? toolName, bool isError, string? errorCode, TimeSpan duration)
    {
        var entry = EntryFor(toolName);
        entry.Record(_clock.GetUtcNow().UtcDateTime, isError, string.IsNullOrWhiteSpace(errorCode) ? UnknownErrorCode : errorCode, duration);
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Immutable copy of the counters: tools with calls (or guidance activity) only, most-called first then by name.
    /// First expires pending guidance suggestions older than <see cref="GuidanceTimeout"/> as ignored (once each: expiry
    /// removes the entry), so a quiet server does not hold ignored counts back.
    /// </summary>
    public ToolTelemetrySnapshot Snapshot()
    {
        SweepExpiredGuidance();
        var tools = _tools
            .Select(pair => pair.Value.ToStats(pair.Key))
            .Where(stats => stats.Calls > 0 || stats.GuidanceIssued > 0 || stats.GuidanceFollowed > 0 || stats.GuidanceIgnored > 0)
            .OrderByDescending(stats => stats.Calls)
            .ThenBy(stats => stats.ToolName, StringComparer.Ordinal)
            .ToList();
        return new ToolTelemetrySnapshot(InstanceId, ServerStartedUtc, BuildSha, tools);
    }

    // The counter entry for a tool name, bucketing a blank name and names past the cap exactly as Record always did.
    private ToolEntry EntryFor(string? toolName)
    {
        var key = string.IsNullOrWhiteSpace(toolName) ? UnknownToolName : toolName;
        if (_tools.Count >= MaxTools && !_tools.ContainsKey(key))
        {
            key = OverflowKey;
        }

        return _tools.GetOrAdd(key, static _ => new ToolEntry());
    }

    /// <summary>
    /// Pending suggestions not yet followed or expired (diagnostic and test seam). Does not sweep, so an expired-by-time
    /// entry still shows here until the next call start or <see cref="Snapshot"/>.
    /// </summary>
    public int PendingGuidanceCount
    {
        get
        {
            lock (_guidanceGate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Guidance step 1, at call START (before the tool runs): advances the call sequence, expires pending suggestions
    /// older than <see cref="GuidanceTimeout"/> or issued more than <see cref="GuidanceMaxCallDistance"/> calls ago
    /// (each counts <c>guidanceIgnored</c> for its ORIGINATING tool), then matches <paramref name="toolName"/>
    /// (case-insensitive) against the remaining suggestions, OLDEST FIRST, and consumes exactly ONE: one call can satisfy
    /// only one outstanding suggestion, so two hints naming the same tool still need two calls to both count as followed
    /// (otherwise a single incidental call would credit several earlier hints). A match counts <c>guidanceFollowed</c>
    /// for the originating tool and returns its name, which the caller passes to <see cref="RecordGuidanceFollowedSucceeded"/>
    /// if the call succeeds; otherwise returns null. A tool suggesting itself matches normally (a retry). Never throws.
    /// </summary>
    public string? BeginGuidanceCall(string? toolName)
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            lock (_guidanceGate)
            {
                _callSequence++;
                ExpirePendingLocked(now, byCallDistance: true);
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    return null;
                }

                var index = _pending.FindIndex(p => string.Equals(p.SuggestedTool, toolName, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    return null;
                }

                var originating = _pending[index].OriginatingTool;
                _pending.RemoveAt(index);
                CountGuidance(originating, GuidanceOutcome.Followed);
                return originating;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ToolTelemetry guidance begin failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Guidance step 2: the call matched by <see cref="BeginGuidanceCall"/> finished without error, so count
    /// <c>guidanceFollowedSucceeded</c> for <paramref name="originatingTool"/>. A null name is ignored. Never throws.
    /// </summary>
    public void RecordGuidanceFollowedSucceeded(string? originatingTool)
    {
        if (originatingTool is null)
        {
            return;
        }

        try
        {
            CountGuidance(originatingTool, GuidanceOutcome.FollowedSucceeded);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ToolTelemetry guidance success failed: {ex}");
        }
    }

    /// <summary>
    /// Guidance step 3, after the tool ran (also when it errored): every suggested tool of the call becomes a pending
    /// suggestion and counts <c>guidanceIssued</c> for <paramref name="originatingTool"/>. Identical tool names (case-insensitive)
    /// within one call are de-duplicated; blank names are skipped. Past <see cref="MaxPendingGuidance"/> the oldest pending
    /// suggestion is dropped and counted as ignored for its own originating tool. Never throws.
    /// </summary>
    public void IssueGuidance(string? originatingTool, IEnumerable<(string ToolName, string? Operation)> suggestions)
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var origin = string.IsNullOrWhiteSpace(originatingTool) ? UnknownToolName : originatingTool;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_guidanceGate)
            {
                foreach (var (toolName, operation) in suggestions)
                {
                    if (string.IsNullOrWhiteSpace(toolName) || !seen.Add(toolName))
                    {
                        continue;
                    }

                    _pending.Add(new PendingSuggestion(origin, toolName, operation, now, _callSequence));
                    CountGuidance(origin, GuidanceOutcome.Issued);
                    while (_pending.Count > MaxPendingGuidance)
                    {
                        var dropped = _pending[0];
                        _pending.RemoveAt(0);
                        CountGuidance(dropped.OriginatingTool, GuidanceOutcome.Ignored);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ToolTelemetry guidance issue failed: {ex}");
        }
    }

    private void CountGuidance(string originatingTool, GuidanceOutcome outcome)
    {
        EntryFor(originatingTool).AddGuidance(outcome);
        Interlocked.Increment(ref _version);
    }

    // Expires pending suggestions by age (always) and by call distance (when asked); each counts as ignored for its
    // originating tool. Caller holds _guidanceGate. Removal is what prevents a double count on a later sweep.
    private void ExpirePendingLocked(DateTime now, bool byCallDistance)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var pending = _pending[i];
            if (now - pending.IssuedUtc > GuidanceTimeout
                || (byCallDistance && _callSequence - pending.CallSequenceAtIssue > GuidanceMaxCallDistance))
            {
                _pending.RemoveAt(i);
                CountGuidance(pending.OriginatingTool, GuidanceOutcome.Ignored);
            }
        }
    }

    // Time-based expiry only: with no further calls a suggestion has not been ignored by call count yet, but once it is
    // older than the timeout its ignored count must not stay hidden in pending. Runs under the guidance gate.
    private void SweepExpiredGuidance()
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            lock (_guidanceGate)
            {
                ExpirePendingLocked(now, byCallDistance: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ToolTelemetry guidance sweep failed: {ex}");
        }
    }

    private enum GuidanceOutcome
    {
        Issued,
        Followed,
        FollowedSucceeded,
        Ignored,
    }

    /// <summary>One suggestion waiting to be followed or to expire (private bookkeeping, never serialized).</summary>
    private sealed class PendingSuggestion(string originatingTool, string suggestedTool, string? operation, DateTime issuedUtc, long callSequenceAtIssue)
    {
        public string OriginatingTool { get; } = originatingTool;

        public string SuggestedTool { get; } = suggestedTool;

        /// <summary>The operation/mode argument the hint supplied; stored for later analysis, not used for matching.</summary>
        public string? Operation { get; } = operation;

        public DateTime IssuedUtc { get; } = issuedUtc;

        public long CallSequenceAtIssue { get; } = callSequenceAtIssue;
    }

    private sealed class ToolEntry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _errorsByCode = new(StringComparer.Ordinal);
        private long _calls;
        private long _errors;
        private double _totalMs;
        private double _maxMs;
        private DateTime _first;
        private DateTime _last;

        // Guidance counters, all keyed on the ORIGINATING tool (the one whose hints suggested something).
        private long _guidanceIssued;
        private long _guidanceFollowed;
        private long _guidanceFollowedSucceeded;
        private long _guidanceIgnored;

        public void AddGuidance(GuidanceOutcome outcome)
        {
            lock (_gate)
            {
                switch (outcome)
                {
                    case GuidanceOutcome.Issued:
                        _guidanceIssued++;
                        break;
                    case GuidanceOutcome.Followed:
                        _guidanceFollowed++;
                        break;
                    case GuidanceOutcome.FollowedSucceeded:
                        _guidanceFollowedSucceeded++;
                        break;
                    case GuidanceOutcome.Ignored:
                        _guidanceIgnored++;
                        break;
                }
            }
        }

        public void Record(DateTime nowUtc, bool isError, string errorCode, TimeSpan duration)
        {
            var ms = Math.Max(0, duration.TotalMilliseconds);
            lock (_gate)
            {
                if (_calls == 0)
                {
                    _first = nowUtc;
                }

                _last = nowUtc;
                _calls++;
                _totalMs += ms;
                if (ms > _maxMs)
                {
                    _maxMs = ms;
                }

                if (isError)
                {
                    _errors++;
                    var code = _errorsByCode.ContainsKey(errorCode) || _errorsByCode.Count < MaxErrorCodesPerTool ? errorCode : OverflowKey;
                    _errorsByCode[code] = _errorsByCode.GetValueOrDefault(code) + 1;
                }
            }
        }

        public ToolCallStats ToStats(string toolName)
        {
            lock (_gate)
            {
                IReadOnlyDictionary<string, int>? byCode = _errorsByCode.Count == 0
                    ? null
                    : _errorsByCode
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                return new ToolCallStats(
                    ToolName: toolName,
                    Calls: _calls,
                    Successes: _calls - _errors,
                    Errors: _errors,
                    ErrorsByCode: byCode,
                    TotalDurationMs: (long)Math.Round(_totalMs),
                    MaxDurationMs: (long)Math.Round(_maxMs),
                    FirstCallUtc: _first,
                    LastCallUtc: _last,
                    GuidanceIssued: _guidanceIssued,
                    GuidanceFollowed: _guidanceFollowed,
                    GuidanceFollowedSucceeded: _guidanceFollowedSucceeded,
                    GuidanceIgnored: _guidanceIgnored);
            }
        }
    }
}
