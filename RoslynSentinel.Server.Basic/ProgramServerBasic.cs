// ProgramServerBasic.cs v1
using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server.Basic;

public class ProgramServerBasic
{
    // All modes available in the Basic variant. Asyncify is Advanced-only.
    private static readonly HashSet<string> AllModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation",
        };

    // Basic active tool types for the DEBUG smoke-resolve check.
    private static readonly Type[] ActiveToolTypes =
    [
        typeof(SentinelWorkspaceTools),
        typeof(DocumentationTools),
        typeof(SentinelSymbolTools),
        typeof(SentinelRefactoringTools),
        typeof(SentinelAugmentTools),
    ];

    public static async Task Main(string[] args)
    {
        // ── Arg parsing ──────────────────────────────────────────────────────
        ServerStartupHelpers.ParseArgs(args, AllModes, out var modeArg, out var activeModes, out var solutionPath);

        if (ServerStartupHelpers.HandleListTools(args, activeModes))
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
            builder.Services.AddRoslynSentinelEnginesBasic();

            var mcpBuilder = builder.Services.AddMcpServer();
            if (isInteractive)
            {
                mcpBuilder.WithStreamServerTransport(interactiveServerInput!, interactiveServerOutput!);
            }
            else
            {
                mcpBuilder.WithStdioServerTransport();
            }

            mcpBuilder.AddRoslynSentinelToolsBasic(builder.Services, activeModes);

            using var host = builder.Build();
            var logger = host.Services.GetRequiredService<ILogger<ProgramServerBasic>>();

            ServerStartupHelpers.SmokeResolveToolTypes(host.Services, ActiveToolTypes);

            host.Services.WarmupAndAutoLoadBasic(solutionPath, logger);
            SentinelConsoleMode.WriteStartupDump(host.Services, AppDomain.CurrentDomain.BaseDirectory, modeArg);
            SentinelConsoleMode.WriteMethodInventory(AppDomain.CurrentDomain.BaseDirectory, modeArg);
            ServerStartupHelpers.LogStartup<ProgramServerBasic>(logger, logPath, activeModes, modeArg);

            try
            {
                if (isInteractive)
                {
                    using var lifetimeCts = new CancellationTokenSource();
                    var hostTask = host.RunAsync(lifetimeCts.Token);
                    await SentinelConsoleMode.RunReplAsync(
                        replWriteStream!, replReadStream!, activeModes, lifetimeCts).ConfigureAwait(false);
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
