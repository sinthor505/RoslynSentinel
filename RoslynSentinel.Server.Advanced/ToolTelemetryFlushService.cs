using Microsoft.Extensions.Hosting;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Hosted service that drives <see cref="ToolTelemetryFlusher"/>: every <see cref="DefaultInterval"/> it flushes if the
/// counters changed, and <see cref="StopAsync"/> does one final bounded flush so a graceful stop loses nothing.
/// Registered through <c>AddRoslynSentinelToolsBasic</c>, so both flavors and both transports (stdio Generic Host, HTTP
/// WebApplication) get it. When the flusher is disabled (no explicit --working-dir) the loop exits at once: no timer.
/// StopServer ends in <c>Environment.Exit</c>, which runs the ConsoleLifetime ProcessExit handler: it calls
/// StopApplication and blocks until the host has stopped, so <see cref="StopAsync"/> runs on that path too.
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

    /// <param name="flusher">The writer.</param>
    /// <param name="interval">Tick period; defaults to <see cref="DefaultInterval"/> (tests pass a short one).</param>
    /// <param name="clock">Timer clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public ToolTelemetryFlushService(ToolTelemetryFlusher flusher, TimeSpan? interval = null, TimeProvider? clock = null)
    {
        _flusher = flusher;
        _interval = interval ?? DefaultInterval;
        _clock = clock ?? TimeProvider.System;
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
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await FlushOnShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Final flush bounded by <see cref="ShutdownFlushTimeout"/> and by <paramref name="cancellationToken"/>; never throws.</summary>
    public async Task FlushOnShutdownAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ShutdownFlushTimeout);
        await _flusher.FlushIfDirtyAsync(cts.Token).ConfigureAwait(false);
    }
}
