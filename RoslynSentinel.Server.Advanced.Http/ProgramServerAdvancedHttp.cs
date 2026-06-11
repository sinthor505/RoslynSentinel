using System.Diagnostics;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Server.Basic;

using Serilog;
using Serilog.Extensions.Logging;

namespace RoslynSentinel.Server.Advanced;

public partial class ProgramServerAdvancedHttp
{
    private static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseDefaultServiceProvider((context, options) =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        // --- Command Line Argument Parsing ---
        var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode="))?.Replace("--mode=", "") ?? "all";
        var solutionPath = args.FirstOrDefault(a => a.StartsWith("--solution="))?.Replace("--solution=", "");

        // ── Named toolset aliases ────────────────────────────────────────────────────
        // Toolset1: Async-migration / ongoing refactoring work.
        //   Includes: Workspace + Quality + Intelligence + Refactor (+ Augment).
        //   Excludes: Modernize (language-upgrade tools), Generation (scaffolding tools).
        //   Use --mode=Toolset1 in mcp.json args to load only these ~254 tools instead of
        //   all 294, keeping the tool list focused on async migration and code analysis.
        var resolvedModeArg = modeArg.Equals("Toolset1", StringComparison.OrdinalIgnoreCase)
            ? "Workspace,Quality,Intelligence,Refactor"
            : modeArg;

        var activeModes = resolvedModeArg.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? new HashSet<string> { "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation", "Asyncify" }
            : resolvedModeArg.Split(',').Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // --- Fast-exit commands (no host build needed) ---
        if (args.Contains("--list-tools"))
        {
            var outputPath = args.FirstOrDefault(a => a.StartsWith("--output=", StringComparison.Ordinal))?.Replace("--output=", "");
            SentinelConsoleMode.ListTools(activeModes, outputPath);
            return;
        }

        Debug.WriteLine($"--- BUILD STAMP: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ---");

        // --- Interactive-mode pipe setup (streams must exist before host build) ---
        var isInteractive = args.Contains("--interactive");
        System.IO.Pipelines.Pipe? _c2sPipe = null;
        System.IO.Pipelines.Pipe? _s2cPipe = null;
        Stream? _interactiveServerInput = null;
        Stream? _interactiveServerOutput = null;
        Stream? _replWriteStream = null;
        Stream? _replReadStream = null;
        if (isInteractive)
        {
            _c2sPipe = new System.IO.Pipelines.Pipe();
            _s2cPipe = new System.IO.Pipelines.Pipe();
            _interactiveServerInput = _c2sPipe.Reader.AsStream();
            _interactiveServerOutput = _s2cPipe.Writer.AsStream();
            _replWriteStream = _c2sPipe.Writer.AsStream();
            _replReadStream = _s2cPipe.Reader.AsStream();
        }

        // --- Configure Logging (Redirection to File ONLY) ---
        // Clear default providers (especially Console which breaks MCP Stdio)
        builder.Logging.ClearProviders();

        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "server.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        // Global unhandled exception handlers — log before dying
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "UNHANDLED EXCEPTION (IsTerminating={IsTerminating}): {Message}",
                e.IsTerminating, ex?.Message ?? e.ExceptionObject?.ToString());
            try
            {
                var crashPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "crash.log");
                File.AppendAllText(crashPath,
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CRASH (IsTerminating={e.IsTerminating})\n" +
                    (ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "unknown") + "\n");
            }
            catch { /* best effort */ }
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warning(e.Exception, "Unobserved task exception (suppressed): {Message}", e.Exception.Message);
            e.SetObserved(); // Prevent .NET from terminating on unobserved task exceptions
        };

        builder.Services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(Log.Logger));
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        try
        {
            // --- Register Infrastructure ---
            builder.Services.AddRoslynSentinelEnginesAdvanced();

            // --- Configure MCP Server Transport ---
            var mcpBuilder = builder.Services.AddMcpServer();
            if (isInteractive)
            {
                mcpBuilder.WithStreamServerTransport(_interactiveServerInput!, _interactiveServerOutput!);
            }
            else
            {
                mcpBuilder.WithStdioServerTransport();
            }

            // --- Tool Registration and Error Filter ---
            mcpBuilder.AddRoslynSentinelToolsAdvanced(builder.Services, activeModes);

            using var host = builder.Build();

            var logger = host.Services.GetRequiredService<ILogger<ProgramServerBasic>>();

#if DEBUG
            // Startup self-check: force-construct every registered tool class so a
            // ctor-body throw or missed registration surfaces here, not on first tool call.
            foreach (var toolType in new[]
            {
                typeof(SentinelWorkspaceTools),
                typeof(DocumentationTools),
                typeof(SentinelSymbolTools),
                typeof(SentinelRefactoringTools),
                typeof(SentinelAugmentTools),
            })
            {
                if (host.Services.GetService(toolType) is null)
                {
                    throw new InvalidOperationException($"Tool type not resolvable: {toolType.Name}");
                }
            }
#endif

            // --- Pre-Warm MSBuildLocator + Auto-Load Solution ---
            host.Services.WarmupAndAutoLoadAdvanced(solutionPath, logger);

            // --- Startup tool dump (internal diagnostic — not an MCP tool) ---
            SentinelConsoleMode.WriteStartupDump(host.Services, AppDomain.CurrentDomain.BaseDirectory, modeArg);
            SentinelConsoleMode.WriteMethodInventory(AppDomain.CurrentDomain.BaseDirectory, modeArg);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Roslyn Sentinel MCP Server starting. Modes: {Modes} (from --mode={ModeArg})",
                    string.Join(", ", activeModes), modeArg);
            }
            Debug.WriteLine($"[RoslynSentinel] PID={Environment.ProcessId} | Log={logPath}");

            // Temp testing output
            Debug.WriteLine($"IsInputRedirected: {Console.IsInputRedirected}");
            Debug.WriteLine($"IsOutputRedirected: {Console.IsOutputRedirected}");

            try
            {
                if (isInteractive)
                {
                    // Run the MCP server on pipe streams; drive it from the console REPL.
                    using var lifetimeCts = new CancellationTokenSource();
                    var hostTask = host.RunAsync(lifetimeCts.Token);
                    await SentinelConsoleMode.RunReplAsync(
                        _replWriteStream!, _replReadStream!, activeModes, lifetimeCts).ConfigureAwait(false);
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
            Log.Fatal(ex, "Roslyn Sentinel failed to start.");
            Debug.WriteLine($"FATAL STARTUP ERROR: {ex.Message}");
            Debug.WriteLine(ex.StackTrace);
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}