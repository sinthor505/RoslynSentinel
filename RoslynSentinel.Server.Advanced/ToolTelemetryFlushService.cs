using Microsoft.Extensions.Hosting;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Hosted service that drives <see cref="ToolTelemetryFlusher"/>: every <see cref="DefaultInterval"/> it flushes if the
/// counters changed, and <see cref="StopAsync"/> does one final bounded flush so a graceful stop loses nothing.
/// Registered through <c>AddRoslynSentinelToolsBasic</c>, so both flavors and both transports (stdio Generic Host, HTTP
/// WebApplication) get it. When the flusher is disabled (no explicit --working-dir) the loop exits at once: no timer.
/// StopServer ends in <c>Environment.Exit(0)</c> (AdminTools.ScheduleProcessExit, 1000 ms after the response is sent).
/// The host's ConsoleLifetime ProcessExit handler is meant to run <see cref="StopAsync"/> on that path, but a stopped
/// instance was observed with its last telemetry write at the 60 s tick and no final flush, so StopAsync is not relied
/// on there. When the flusher is enabled, <see cref="StartAsync"/> subscribes to <c>AppDomain.ProcessExit</c> (through
/// <see cref="IProcessExitSource"/>) and the handler runs the same bounded flush synchronously; it never throws.
/// The subscription is dropped when <see cref="StopAsync"/> begins and on <see cref="Dispose"/>. Both paths may flush:
/// that is safe because <see cref="ToolTelemetryFlusher.FlushIfDirtyAsync"/> serializes writers and skips a write
/// when nothing was recorded since the last successful one, so the second path is a no-op.
/// Public rather than internal because Server.Advanced has no InternalsVisibleTo for its tests.
/// </summary>
public sealed class ToolTelemetryFlushService : BackgroundService
{
    /// <summary>Flush period when the telemetry changed.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound for the final flush, so a stuck disk cannot hang host shutdown.</summary>
    public static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(3);

    private readonly ToolTelemetryFlusher _flusher;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private readonly IProcessExitSource _processExit;
    private readonly Func<CancellationToken, Task> _flush;
    private int _subscribed;

    /// <param name="flusher">The writer.</param>
    /// <param name="interval">Tick period; defaults to <see cref="DefaultInterval"/> (tests pass a short one).</param>
    /// <param name="clock">Timer clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public ToolTelemetryFlushService(
        ToolTelemetryFlusher flusher,
        TimeSpan? interval = null,
        TimeProvider? clock = null,
        IProcessExitSource? processExit = null,
        Func<CancellationToken, Task>? flush = null)
    {
        _flusher = flusher;
        _interval = interval ?? DefaultInterval;
        _clock = clock ?? TimeProvider.System;
        _processExit = processExit ?? new AppDomainProcessExitSource();
        _flush = flush ?? (async ct => await flusher.FlushIfDirtyAsync(ct).ConfigureAwait(false));
    }

    public override void Dispose()
    {
        Unsubscribe();
        base.Dispose();
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
        {
            _processExit.ProcessExit -= OnProcessExit;
        }
    }

    // ProcessExit handlers cannot await, so this blocks on the same bounded flush the host-stop path runs. Task.Run keeps
    // the flush off any ambient sync context; Wait adds a hard cap in case a write ignores cancellation. Never throws: an
    // exception escaping a ProcessExit handler would abort the other handlers (including the host's own).
    private void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            Task.Run(() => FlushOnShutdownAsync()).Wait(ShutdownFlushTimeout + TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Swallowed by design; see above.
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Only an enabled flusher has anything to lose on exit; a disabled one never subscribes.
        if (_flusher.IsEnabled && Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            _processExit.ProcessExit += OnProcessExit;
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_flusher.IsEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                // Never throws; a failed write stays dirty and is retried on the next tick.
                await _flusher.FlushIfDirtyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Host is stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Unsubscribe first: this path is flushing, so the ProcessExit handler has nothing left to add.
        Unsubscribe();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await FlushOnShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Final flush bounded by <see cref="ShutdownFlushTimeout"/> and by <paramref name="cancellationToken"/>; never throws.</summary>
    public async Task FlushOnShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ShutdownFlushTimeout);
            await _flush(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The flusher already swallows and logs write failures; this guards the seam. Shutdown must never fail on telemetry.
        }
    }

    /// <summary>Production source: forwards to <c>AppDomain.CurrentDomain.ProcessExit</c>.</summary>
    private sealed class AppDomainProcessExitSource : IProcessExitSource
    {
        public event EventHandler? ProcessExit
        {
            add => AppDomain.CurrentDomain.ProcessExit += value;
            remove => AppDomain.CurrentDomain.ProcessExit -= value;
        }
    }
}
