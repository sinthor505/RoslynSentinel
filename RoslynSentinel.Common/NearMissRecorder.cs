using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

/// <summary>
/// EVALUATION plumbing for <see cref="NearMissOptions"/>: records every near-miss substitution and every ambiguous near-miss hint of a tool
/// call as a <see cref="NearMissSubstitution"/>. Each record is written to the server log and appended to the current call's
/// <see cref="CallScope"/>; the call-tool filter (<c>NearMissRecordFilter</c>, Server.Advanced) copies the scope's records into the result's
/// <c>_meta.nearMissSubstitutions</c>. Nothing is logged or recorded unless <see cref="IsActive"/>, so with both options at their defaults the
/// server behaves as it did before this class existed.
/// </summary>
public static class NearMissRecorder
{
    /// <summary>The key under the result's <c>_meta</c> object that holds the records.</summary>
    public const string MetaKey = "nearMissSubstitutions";

    // The scope object is created by the filter BEFORE it calls next, and the tool body inherits the reference. An AsyncLocal set inside the
    // child flow would not be visible to the filter after next returns (see AddAgentIdentityFilter), so the filter reads the shared object instead.
    private static readonly AsyncLocal<CallScope?> Current = new();

    /// <summary>True when <see cref="NearMissOptions.AutoCorrect"/> is on or <see cref="NearMissOptions.NameDisplay"/> is <see cref="NearMissNameDisplay.Hide"/>.</summary>
    public static bool IsActive => NearMissOptions.AutoCorrect || NearMissOptions.NameDisplay == NearMissNameDisplay.Hide;

    /// <summary>
    /// Opens the record scope of one tool call in the current async flow. Call before invoking the tool and dispose when the call returns;
    /// <paramref name="logger"/> receives the log line of each record.
    /// </summary>
    public static CallScope Begin(ILogger? logger = null)
    {
        var scope = new CallScope(logger, Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Logs <paramref name="substitution"/> and appends it to the current call's scope. Does nothing unless <see cref="IsActive"/>. Without a
    /// scope (a call outside the filter) it is still logged, through <see cref="Debug"/>, the fallback the other filters use.
    /// </summary>
    public static void Record(NearMissSubstitution substitution)
    {
        ArgumentNullException.ThrowIfNull(substitution);
        if (!IsActive)
        {
            return;
        }

        var scope = Current.Value;
        const string template = "Near-miss {Outcome} in {Tool} at {FilePath}:{Line} ({DiagnosticId}): '{From}' -> '{To}', {CandidateCount} candidate(s), name display {DisplayMode}";
        var outcome = substitution.Applied ? "substitution applied" : "hint given";
        if (scope?.Logger is { } logger)
        {
            logger.LogInformation(template, outcome, substitution.Tool, substitution.FilePath, substitution.Line, substitution.DiagnosticId,
                substitution.From, substitution.To, substitution.CandidateCount, substitution.DisplayMode);
        }
        else
        {
            Debug.WriteLine($"Near-miss {outcome} in {substitution.Tool} at {substitution.FilePath}:{substitution.Line} ({substitution.DiagnosticId}): '{substitution.From}' -> '{substitution.To}', {substitution.CandidateCount} candidate(s), name display {substitution.DisplayMode}");
        }

        scope?.Add(substitution);
    }

    /// <summary>The records of one tool call. Thread-safe: a tool may record from parallel work inside the call.</summary>
    public sealed class CallScope : IDisposable
    {
        private readonly List<NearMissSubstitution> _records = [];
        private readonly CallScope? _previous;

        internal CallScope(ILogger? logger, CallScope? previous)
        {
            Logger = logger;
            _previous = previous;
        }

        internal ILogger? Logger { get; }

        /// <summary>A snapshot of the records made so far, in the order they were made.</summary>
        public IReadOnlyList<NearMissSubstitution> Records
        {
            get
            {
                lock (_records)
                {
                    return [.. _records];
                }
            }
        }

        internal void Add(NearMissSubstitution substitution)
        {
            lock (_records)
            {
                _records.Add(substitution);
            }
        }

        /// <summary>Restores the scope that was current when this one began.</summary>
        public void Dispose() => Current.Value = _previous;
    }
}
