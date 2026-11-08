using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Server.Advanced
{
    /// <summary>
    /// Exits the stdio server once it has been idle for IdleRestartOptions.Minutes and the launcher has
    /// recorded a different, ready build for the dev repo's HEAD. VS Code respawns the server on the next
    /// tool call, which takes the launcher's fast path. The decision lives in IdleRestartPolicy.FirstUnmetGuard;
    /// this class only gathers its inputs each tick and schedules the exit.
    /// Runs only in the stdio server with --builds-root (the prod launcher); otherwise it logs once and stops.
    /// </summary>
    internal sealed class IdleRestartMonitor : BackgroundService
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

        private readonly IServiceProvider _services;
        private readonly ILogger<IdleRestartMonitor> _logger;

        public IdleRestartMonitor(IServiceProvider services, ILogger<IdleRestartMonitor> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!IdleRestartOptions.Enabled)
            {
                _logger.LogInformation(
                    "Idle restart disabled (minutes={Minutes}, buildsRoot={BuildsRoot}).",
                    IdleRestartOptions.Minutes, IdleRestartOptions.BuildsRoot);
                return;
            }

            // TODO MCP task-mode tool calls that keep running after the call returns are covered only by the build gate and by write-chokepoint touches; a long non-build, non-writing task could be cut off.
            try
            {
                using var timer = new PeriodicTimer(TickInterval);
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        if (CheckOnce())
                        {
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Idle restart check failed; will retry on the next tick.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown: end quietly.
            }
        }

        /// <summary>One tick: gathers the policy inputs, records the guard, and returns true after scheduling the exit.</summary>
        private bool CheckOnce()
        {
            var nowUtc = DateTime.UtcNow;
            var activity = ServerActivityTracker.Shared;
            // Fail safe: if the workspace manager cannot be resolved, never restart.
            var workspace = _services.GetService<IWorkspaceManager>();
            var latest = LatestBuildFile.Read(IdleRestartOptions.BuildsRoot);
            var devHeadSha = ServerBinaryStaleness.CurrentHeadSha();

            var request = new IdleRestartCheckRequest(
                Enabled: IdleRestartOptions.Enabled,
                NowUtc: nowUtc,
                LastActivityUtc: activity.LastActivityUtc,
                IdleThreshold: TimeSpan.FromMinutes(IdleRestartOptions.Minutes),
                InFlightCount: activity.InFlightCount,
                SessionHalted: workspace is null || workspace.IsSessionHalted(),
                OpenLedgerOperation: (workspace as IScopedOperationLedger)?.OpenOperationName,
                BuildGateBusy: BuildGate.Shared.IsBusy,
                Latest: latest,
                DevHeadSha: devHeadSha,
                OwnBuildSha: ServerBuildInfo.BuildSha);

            var guard = IdleRestartPolicy.FirstUnmetGuard(request);
            if (guard != IdleRestartState.LastUnmetGuard)
            {
                _logger.LogDebug("Idle restart guard: {Guard}", guard ?? "none (exit)");
            }

            IdleRestartState.Record(guard, nowUtc);

            if (guard is not null)
            {
                return false;
            }

            _logger.LogInformation(
                "Idle restart: idle for at least {Minutes} min, build {BuildSha8} is ready for HEAD {HeadSha8}; exiting so the next tool call respawns on it.",
                IdleRestartOptions.Minutes, Short(latest?.BuildSha8), Short(devHeadSha));
            ProcessExitScheduler.Schedule();
            return true;
        }

        private static string Short(string? sha) =>
            string.IsNullOrEmpty(sha) ? "unknown" : sha.Length <= 8 ? sha : sha[..8];
    }
}
