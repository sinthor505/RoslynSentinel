using RoslynSentinel.Server;

using Serilog;
using Serilog.Extensions.Logging;

namespace RoslynSentinel.HttpHost
{
    public partial class ProgramHttpHost
    {
        private static async Task Main(string[] args)
        {
            // ── Argument Parsing ─────────────────────────────────────────────────────────

            var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.Ordinal))?.Replace("--mode=", "", StringComparison.Ordinal) ?? "all";
            var solutionPath = args.FirstOrDefault(a => a.StartsWith("--solution=", StringComparison.Ordinal))?.Replace("--solution=", "", StringComparison.Ordinal);
            var portArg = args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.Ordinal))?.Replace("--port=", "", StringComparison.Ordinal);
            var port = int.TryParse(portArg, out var parsedPort) ? parsedPort : 5100;

            // Toolset aliases (mirrors RoslynSentinel.Server)
            var resolvedModeArg = modeArg.Equals("Toolset1", StringComparison.OrdinalIgnoreCase)
                ? "Workspace,Quality,Intelligence,Refactor"
                : modeArg;

            var activeModes = resolvedModeArg.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? new HashSet<string> { "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation" }
                : resolvedModeArg.Split(',').Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // ── Fast-exit: --list-tools ───────────────────────────────────────────────────

            if (args.Contains("--list-tools"))
            {
                var outputPath = args.FirstOrDefault(a => a.StartsWith("--output=", StringComparison.Ordinal))
                                    ?.Replace("--output=", "");
                SentinelConsoleMode.ListTools(activeModes, outputPath);
                return;
            }

            // ── Logging ───────────────────────────────────────────────────────────────────

            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "http-host.log");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Log.Fatal(ex, "UNHANDLED EXCEPTION (IsTerminating={IsTerminating}): {Message}",
                    e.IsTerminating, ex?.Message ?? e.ExceptionObject?.ToString());
                Log.CloseAndFlush();
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Warning(e.Exception, "Unobserved task exception (suppressed): {Message}", e.Exception.Message);
                e.SetObserved();
            };

            // ── Build WebApplication ──────────────────────────────────────────────────────

            var builder = WebApplication.CreateBuilder(args);

            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(Log.Logger));
            builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

            builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(port));

            // Register all Roslyn engine singletons
            builder.Services.AddRoslynSentinelEngines();

            // Register MCP with HTTP transport (Streamable HTTP)
            var mcpBuilder = builder.Services.AddMcpServer()
                .WithHttpTransport();

            // Register tool classes (mode-conditional) and the error filter
            mcpBuilder.AddRoslynSentinelTools(builder.Services, activeModes);

            // ── Run ───────────────────────────────────────────────────────────────────────

            var app = builder.Build();

            app.MapMcp("/mcp");

            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RoslynSentinel.HttpHost");

            // Pre-warm MSBuildLocator (~5-8 s) and optionally auto-load solution
            app.Services.WarmupAndAutoLoad(solutionPath, logger);

            // Startup tool dump (internal diagnostic — not an MCP tool)
            // SentinelConsoleMode.WriteStartupDump(app.Services, AppDomain.CurrentDomain.BaseDirectory, modeArg);
            // SentinelConsoleMode.WriteMethodInventory(AppDomain.CurrentDomain.BaseDirectory, modeArg);

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "RoslynSentinel HTTP Host starting. Port={Port} | Modes={Modes} | Log={Log}",
                    port, string.Join(", ", activeModes), logPath);

            Console.WriteLine($"[RoslynSentinel.HttpHost] Listening on http://0.0.0.0:{port}/mcp | PID={Environment.ProcessId}");
            Console.WriteLine($"[RoslynSentinel.HttpHost] Legacy SSE endpoint: http://0.0.0.0:{port}/sse");
            Console.WriteLine($"[RoslynSentinel.HttpHost] Log: {logPath}");

            try
            {
                await app.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
    }
}