// ServerStdio.cs v1
using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Extensions.Tasks;

namespace RoslynSentinel.Server.Advanced
{
    public class ServerStdio
    {
        // All modes available in the Advanced variant. Asyncify is Advanced-only.
        private static readonly HashSet<string> AllModes =
            new(StringComparer.OrdinalIgnoreCase)
            {
            "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation", "Asyncify",
            };

        // Advanced active tool types for the DEBUG smoke-resolve check.
        // Extend this list as new tool classes are activated in AddRoslynSentinelToolsAdvanced.
        // SentinelAugmentTools is deliberately excluded — it declares zero [McpServerTool] methods
        // and is only conditionally registered when --include-tools/--mode selects it, but this
        // check always tries to resolve every type listed here regardless of what was requested,
        // so listing a conditionally-registered type here would make any --include-tools selection
        // that omits it crash at startup.
        private static readonly Type[] ActiveToolTypes =
        [
            typeof(SentinelWorkspaceTools),
        typeof(SentinelDocumentationTools),
        typeof(SentinelSymbolTools),
        typeof(SentinelRefactoringTools),
    ];

        public static async Task Startup(string[] args)
        {
            // ── Arg parsing ──────────────────────────────────────────────────────
            ServerStartupHelpers.ParseArgs(args, AllModes, out var modeArg, out var activeModes, out var solutionPath, out var baseRepoDirectory, out var includeTools, out var excludeTools, out var operatingMode);
            LlmOptions.Configure(args);

            if (ServerStartupHelpers.HandleListTools(args, activeModes, includeTools, excludeTools))
            {
                return;
            }

            Debug.WriteLine($"--- BUILD STAMP: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ---");

            // ── Interactive pipe setup (must precede host build) ─────────────────
            var isInteractive = args.Contains("--interactive");
            System.IO.Pipelines.Pipe? c2sPipe = null;
            System.IO.Pipelines.Pipe? s2cPipe = null;
            Stream? interactiveServerInput = null;
            Stream? interactiveServerOutput = null;
            Stream? replWriteStream = null;
            Stream? replReadStream = null;
            if (isInteractive)
            {
                c2sPipe = new System.IO.Pipelines.Pipe();
                s2cPipe = new System.IO.Pipelines.Pipe();
                interactiveServerInput = c2sPipe.Reader.AsStream();
                interactiveServerOutput = s2cPipe.Writer.AsStream();
                replWriteStream = c2sPipe.Writer.AsStream();
                replReadStream = s2cPipe.Reader.AsStream();
            }

            // ── Logging (file-only — stdout must stay clean for stdio transport) ─
            var logPath = ServerStartupHelpers.ConfigureStdioLogging();
            ServerStartupHelpers.AttachCrashHandlers();

            // ── Host ─────────────────────────────────────────────────────────────
            var builder = Host.CreateApplicationBuilder(args);
            ServerStartupHelpers.EnableValidateOnBuild(builder);

            builder.Logging.ClearProviders();
            ServerStartupHelpers.RegisterSerilogLoggerFactory(builder.Services);

            try
            {
                builder.Services.AddRoslynSentinelHostOptions(operatingMode);
                builder.Services.AddRoslynSentinelEnginesAdvanced();

                var mcpBuilder = builder.Services.AddMcpServer();
                if (isInteractive)
                {
                    mcpBuilder.WithStreamServerTransport(interactiveServerInput!, interactiveServerOutput!);
                }
                else
                {
                    mcpBuilder.WithStdioServerTransport();
                }

                mcpBuilder.WithTasks(
                    new InMemoryMcpTaskStore(),
                    o => o.ExecutionModeSelector = RoslynSentinelTaskTools.SelectExecutionMode);

                mcpBuilder.AddRoslynSentinelToolsAdvanced(builder.Services, activeModes, includeTools, excludeTools);

                using var host = builder.Build();
                var logger = host.Services.GetRequiredService<ILogger<ServerStdio>>();

                ServerStartupHelpers.SmokeResolveToolTypes(host.Services, ActiveToolTypes);

                host.Services.WarmupAndAutoLoadAdvanced(solutionPath, logger, baseRepoDirectory);
                SentinelConsoleMode.WriteStartupDump(host.Services, AppDomain.CurrentDomain.BaseDirectory, modeArg);
                SentinelConsoleMode.WriteMethodInventory(AppDomain.CurrentDomain.BaseDirectory, modeArg);
                ServerStartupHelpers.LogStartup<ServerStdio>(logger, logPath, activeModes, modeArg, includeTools, excludeTools, operatingMode);

                try
                {
                    if (isInteractive)
                    {
                        using var lifetimeCts = new CancellationTokenSource();
                        var hostTask = host.RunAsync(lifetimeCts.Token);
                        await SentinelConsoleMode.RunReplAsync(
                            replWriteStream!, replReadStream!, activeModes, lifetimeCts, includeTools, excludeTools).ConfigureAwait(false);
                        await hostTask.ConfigureAwait(false);
                    }
                    else
                    {
                        await host.RunAsync().ConfigureAwait(false);
                    }

                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Host shut down cleanly.");
                    }
                }
                catch (Exception runEx)
                {
                    if (logger.IsEnabled(LogLevel.Critical))
                    {
                        logger.LogCritical(runEx, "Host.RunAsync terminated with exception: {Message}", runEx.Message);
                    }
                    Debug.WriteLine($"[RoslynSentinel] FATAL: {runEx.Message}");
                    Debug.WriteLine(runEx.StackTrace);
                    throw;
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Fatal(ex, "Roslyn Sentinel failed to start.");
                Debug.WriteLine($"FATAL STARTUP ERROR: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
                throw;
            }
            finally
            {
                Serilog.Log.CloseAndFlush();
            }
        }
    }
}