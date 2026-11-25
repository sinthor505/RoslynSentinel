using System.Diagnostics;
using System.Globalization;

namespace RoslynSentinel.Common;

/// <summary>
/// Gives a caller (the MoveFile edge gate) access to the in-process edge-gate lease of a workspace manager. Implemented by
/// <see cref="PersistentWorkspaceManager"/> only; deliberately NOT a member of <see cref="IWorkspaceManager"/> or
/// <see cref="ISolutionProvider"/>, so no fake has to implement it. Callers reach it with an <c>as</c> check.
/// </summary>
public interface IEdgeGateLeaseSource
{
    /// <summary>The lease of this manager (one per manager instance).</summary>
    EdgeGateLease Lease { get; }
}

/// <summary>
/// What a writer observed while waiting for another flow's edge-gate lease: the time it waited, the holding tool (null when
/// the holder had no <see cref="ToolCallContext"/>) and whether it gave up at <see cref="EdgeGateLease.MaxWait"/> with the
/// lease still held. In-process only; the wire part is the <see cref="Finding"/> built by <see cref="ToFinding"/>.
/// </summary>
public readonly record struct EdgeGateWaitResult(TimeSpan Waited, string? HolderToolName, bool TimedOut)
{
    /// <summary>Source name of the finding.</summary>
    public const string FindingSource = "EdgeGateLease";

    /// <summary>Builds the facts-only finding for this wait.</summary>
    public Finding ToFinding()
    {
        var holder = HolderToolName is null ? "another tool call" : $"tool '{HolderToolName}'";
        var seconds = Waited.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        return TimedOut
            ? new Finding(FindingSource, $"This write waited {seconds} s for the edge-gate lease held by {holder} and proceeded at the wait limit while the lease was still held.", FindingSeverity.Caution)
            : new Finding(FindingSource, $"This write waited {seconds} s for the edge-gate lease held by {holder}, which was then released.", FindingSeverity.Info);
    }

    /// <summary>Returns <paramref name="result"/> with this wait's finding appended to <see cref="ApplyChangesResult.Findings"/>.</summary>
    public ApplyChangesResult AppendFindingTo(ApplyChangesResult result)
    {
        var findings = result.Findings is null ? new List<Finding>(1) : new List<Finding>(result.Findings);
        findings.Add(ToFinding());
        return result with { Findings = findings };
    }
}

/// <summary>
/// In-process lease that lets one flow (the MoveFile edge gate: compile against an augmented solution, then apply) keep other
/// writers out of the write chokepoint for the duration of its gate-and-apply, so the solution the gate compiled against is
/// still the committed one when the apply happens.
/// <para>It is a plain in-process object, not a lock on the workspace: it never takes the manager's solution lock or the
/// cross-process write lock (rule (c) of <c>CrossProcessWriteLock</c>; the solution lock is not re-entrant), and nothing here
/// is held across a compile by those locks.</para>
/// <para>Two roles. An <b>acquirer</b> (<see cref="AcquireAsync"/>) WAITS for the current holder to release, bounded by its
/// cancellation token and <see cref="AcquireMaxWait"/> (it throws <see cref="TimeoutException"/> at the bound, it never
/// proceeds without the lease), so two concurrent edge-gate calls serialize. A <b>writer</b> (the chokepoint, through
/// <see cref="WaitForOtherHolderAsync"/>) waits at most <see cref="MaxWait"/> for another holder and then proceeds anyway,
/// reporting what happened so the chokepoint can surface a finding: a lease must never be able to wedge ordinary writes.</para>
/// <para><b>Holder identity is the ambient flow.</b> It is <see cref="ToolCallContext.Current"/> (compared by reference) when
/// set, else a fallback token held in an <see cref="AsyncLocal{T}"/> owned by this class. This deliberately deviates from
/// "keyed by tool call id": <see cref="ToolCallContext.ToolCallId"/> is null when no echo filter runs, and
/// <see cref="ToolCallContext.Current"/> itself is null in tests and in-process hosts. An <see cref="AsyncLocal{T}"/> set inside
/// an awaited async method does not flow back to the caller, so <see cref="AcquireAsync"/> is NOT async: it resolves (and, with
/// no context, creates) the token synchronously in the caller's execution context before it returns the task. Every later
/// await in that caller, and every task it starts, then sees the same identity, so the holder's own apply passes through
/// <see cref="WaitForOtherHolderAsync"/>. A flow with no token never matches a holder, so it waits.</para>
/// </summary>
public sealed class EdgeGateLease
{
    /// <summary>Default for <see cref="MaxWait"/> (writer wait bound); the order of <c>SolutionLoadWait.DefaultTimeout</c>, not measured.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(30);

    /// <summary>Default for <see cref="AcquireMaxWait"/> (acquirer wait bound); generous because an acquirer cannot proceed without the lease.</summary>
    public static readonly TimeSpan DefaultAcquireMaxWait = TimeSpan.FromMinutes(2);

    // Fallback identity for a flow with no ToolCallContext. Static so one flow has one token across managers; created only by
    // AcquireAsync (see the class comment for why that must happen synchronously in the caller's context).
    private static readonly AsyncLocal<object?> FlowToken = new();

    private static readonly Task<EdgeGateWaitResult?> NoWait = Task.FromResult<EdgeGateWaitResult?>(null);

    private readonly Lock _sync = new();

    // Written under _sync, read without it by the writer fast path: a volatile read and no allocation when no lease is held.
    private Holder? _current;

    // Completed (and replaced) on every release; waiters re-check the lease state after it fires.
    private TaskCompletionSource _released = NewSignal();

    /// <summary>
    /// How long a writer waits for another flow's lease before proceeding anyway. Set like
    /// <see cref="PersistentWorkspaceManager.DefaultLoadTimeout"/> so tests can use a short value; zero or negative means
    /// do not wait.
    /// </summary>
    public TimeSpan MaxWait { get; set; } = DefaultMaxWait;

    /// <summary>How long <see cref="AcquireAsync"/> waits for the current holder before it throws <see cref="TimeoutException"/>.</summary>
    public TimeSpan AcquireMaxWait { get; set; } = DefaultAcquireMaxWait;

    /// <summary>True while some flow holds the lease.</summary>
    public bool IsHeld => Volatile.Read(ref _current) is not null;

    /// <summary>Tool name of the current holder; null when no lease is held or the holder had no <see cref="ToolCallContext"/>.</summary>
    public string? HolderToolName => Volatile.Read(ref _current)?.ToolName;

    /// <summary>
    /// Acquires the lease for the calling flow, waiting for the current holder to release (bounded by
    /// <paramref name="cancellationToken"/> and <see cref="AcquireMaxWait"/>). Dispose the returned holder to release.
    /// If the calling flow already holds the lease, returns a nested holder whose disposal does nothing, so the outer holder
    /// stays in charge and a nested call cannot deadlock on itself.
    /// </summary>
    /// <exception cref="TimeoutException">The holder did not release within <see cref="AcquireMaxWait"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting; nothing is held.</exception>
    public Task<Holder> AcquireAsync(CancellationToken cancellationToken)
    {
        // Not async on purpose: see the class comment (the fallback token must be set in the caller's execution context).
        var identity = ResolveIdentity(createFallback: true)!;
        return AcquireCoreAsync(identity, ToolCallContext.Current?.ToolName, cancellationToken);
    }

    /// <summary>
    /// Writer side. Completes at once with null when no lease is held or the calling flow is the holder; otherwise waits up to
    /// <see cref="MaxWait"/> for the release and then proceeds anyway. The result says how long it waited, who held the lease
    /// and whether it proceeded at the bound. Does not take any workspace lock.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
    public Task<EdgeGateWaitResult?> WaitForOtherHolderAsync(CancellationToken cancellationToken)
    {
        return Volatile.Read(ref _current) is null ? NoWait : WaitForOtherHolderSlowAsync(cancellationToken);
    }

    private async Task<EdgeGateWaitResult?> WaitForOtherHolderSlowAsync(CancellationToken cancellationToken)
    {
        var identity = ResolveIdentity(createFallback: false);
        var stopwatch = Stopwatch.StartNew();
        var waited = false;
        string? holderToolName = null;
        while (true)
        {
            Task signal;
            lock (_sync)
            {
                var current = _current;
                if (current is null || (identity is not null && ReferenceEquals(current.Identity, identity)))
                {
                    return waited ? new EdgeGateWaitResult(stopwatch.Elapsed, holderToolName, TimedOut: false) : null;
                }

                holderToolName = current.ToolName;
                signal = _released.Task;
            }

            waited = true;
            var remaining = MaxWait - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new EdgeGateWaitResult(stopwatch.Elapsed, holderToolName, TimedOut: true);
            }

            try
            {
                await signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new EdgeGateWaitResult(stopwatch.Elapsed, holderToolName, TimedOut: true);
            }
        }
    }

    private async Task<Holder> AcquireCoreAsync(object identity, string? toolName, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            Task signal;
            string? holderToolName;
            lock (_sync)
            {
                var current = _current;
                if (current is null)
                {
                    var holder = new Holder(this, identity, toolName, owning: true);
                    Volatile.Write(ref _current, holder);
                    return holder;
                }

                if (ReferenceEquals(current.Identity, identity))
                {
                    return new Holder(this, identity, toolName, owning: false);
                }

                holderToolName = current.ToolName;
                signal = _released.Task;
            }

            var remaining = AcquireMaxWait - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw AcquireTimeout(holderToolName, stopwatch.Elapsed);
            }

            try
            {
                await signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw AcquireTimeout(holderToolName, stopwatch.Elapsed);
            }
        }
    }

    private static TimeoutException AcquireTimeout(string? holderToolName, TimeSpan waited)
    {
        var holder = holderToolName is null ? "another tool call" : $"tool '{holderToolName}'";
        return new TimeoutException($"The edge-gate lease is held by {holder}; waited {waited.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s without it being released.");
    }

    // The ambient flow identity: the tool call context when one is set, else the fallback token (created here only when asked).
    private static object? ResolveIdentity(bool createFallback)
    {
        var context = ToolCallContext.Current;
        if (context is not null)
        {
            return context;
        }

        var token = FlowToken.Value;
        if (token is null && createFallback)
        {
            token = new object();
            FlowToken.Value = token;
        }

        return token;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Release(Holder holder)
    {
        TaskCompletionSource signalled;
        lock (_sync)
        {
            if (!ReferenceEquals(_current, holder))
            {
                return;
            }

            Volatile.Write(ref _current, null);
            signalled = _released;
            _released = NewSignal();
        }

        // Completed outside the lock; continuations run asynchronously anyway.
        signalled.TrySetResult();
    }

    /// <summary>
    /// A held lease. <see cref="Dispose"/> releases it and wakes the waiters; it is idempotent and safe to call from a
    /// <c>finally</c>. A holder returned for a nested acquire by the owning flow (<see cref="Owning"/> false) releases nothing.
    /// </summary>
    public sealed class Holder : IDisposable
    {
        private readonly EdgeGateLease _owner;
        private int _disposed;

        internal Holder(EdgeGateLease owner, object identity, string? toolName, bool owning)
        {
            _owner = owner;
            Identity = identity;
            ToolName = toolName;
            Owning = owning;
        }

        /// <summary>The flow identity this holder was acquired under (the <see cref="ToolCallContext"/> or the fallback token).</summary>
        public object Identity { get; }

        /// <summary>Tool name of the acquiring flow's <see cref="ToolCallContext"/>; null when it had none.</summary>
        public string? ToolName { get; }

        /// <summary>False for the no-op holder given to a nested acquire by the flow that already holds the lease.</summary>
        public bool Owning { get; }

        /// <summary>Releases the lease exactly once; later calls do nothing.</summary>
        public void Dispose()
        {
            if (Owning && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(this);
            }
        }
    }
}
