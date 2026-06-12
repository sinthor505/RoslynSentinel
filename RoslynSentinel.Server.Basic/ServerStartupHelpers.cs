// ServerStartupHelpers.cs v1
using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Server.Basic;

using Serilog;
using Serilog.Extensions.Logging;

namespace RoslynSentinel.Server;

/// <summary>
/// Shared startup utilities used by all four server entry points
/// (Basic/Advanced × stdio/HTTP). Centralises argument parsing, Serilog
/// configuration, crash handlers, DI logger registration, and
/// ValidateOnBuild so each Program file is a minimal shell.
/// </summary>
public static class ServerStartupHelpers
{
    // ── Toolset aliases ──────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> ToolsetAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Toolset1: Async-migration / ongoing refactoring work.
            //   Includes: Workspace + Quality + Intelligence + Refactor (+ Augment).
            //   Excludes: Modernize (language-upgrade tools), Generation (scaffolding tools).
            ["Toolset1"] = "Workspace,Quality,Intelligence,Refactor",
        };

    // ── Argument parsing ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses the standard RoslynSentinel command-line arguments.
    /// </summary>
    /// <param name="args">Raw command-line args.</param>
    /// <param name="allModes">Full mode set for this variant (Basic vs Advanced).</param>
    /// <param name="modeArg">The raw --mode value (or "all").</param>
    /// <param name="activeModes">Resolved, expanded set of active modes.</param>
    /// <param name="solutionPath">Value of --solution=, or null.</param>
    public static void ParseArgs(
        string[] args,
        HashSet<string> allModes,
        out string modeArg,
        out HashSet<string> activeModes,
        out string? solutionPath)
    {
        modeArg = args.FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.Ordinal))
                      ?.Replace("--mode=", "", StringComparison.Ordinal)
                  ?? "all";

        solutionPath = args.FirstOrDefault(a => a.StartsWith("--solution=", StringComparison.Ordinal))
                          ?.Replace("--solution=", "", StringComparison.Ordinal);

        var resolvedModeArg = ToolsetAliases.TryGetValue(modeArg, out var alias) ? alias : modeArg;

        activeModes = resolvedModeArg.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? allModes
            : resolvedModeArg.Split(',').Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Parses --port=N; returns <paramref name="defaultPort"/> if absent or unparseable.</summary>
    public static int ParsePort(string[] args, int defaultPort = 5100)
    {
        var portArg = args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.Ordinal))
                         ?.Replace("--port=", "", StringComparison.Ordinal);
        return int.TryParse(portArg, out var parsed) ? parsed : defaultPort;
    }

    // ── Fast-exit: --list-tools ───────────────────────────────────────────────

    /// <summary>
    /// If --list-tools is present, writes the tool list and returns true.
    /// The caller should return immediately when this returns true.
    /// </summary>
    public static bool HandleListTools(string[] args, HashSet<string> activeModes)
    {
        if (!args.Contains("--list-tools"))
        {
            return false;
        }

        var outputPath = args.FirstOrDefault(a => a.StartsWith("--output=", StringComparison.Ordinal))
                            ?.Replace("--output=", "", StringComparison.Ordinal);
        SentinelConsoleMode.ListTools(activeModes, outputPath);
        return true;
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures Serilog for stdio servers: file-only, Information level.
    /// Stdout must stay clean for the MCP stdio transport.
    /// </summary>
    public static string ConfigureStdioLogging(string logFileName = "server.log")
    {
        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", logFileName);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();
        return logPath;
    }

    /// <summary>
    /// Configures Serilog for HTTP servers: file + console, Verbose level.
    /// Console output is safe because stdout is not the MCP transport.
    /// </summary>
    public static string ConfigureHttpLogging(string logFileName = "http-host.log")
    {
        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", logFileName);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return logPath;
    }

    // ── Crash handlers ────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches AppDomain and TaskScheduler unhandled-exception handlers.
    /// Logs to Serilog and writes a crash.log file in the logs directory.
    /// Safe to call for both stdio and HTTP hosts; does not write to stdout.
    /// </summary>
    public static void AttachCrashHandlers()
    {
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
            e.SetObserved();
        };
    }

    // ── DI logger registration ────────────────────────────────────────────────

    /// <summary>
    /// Registers Serilog as the ILoggerFactory / ILogger&lt;T&gt; provider in DI.
    /// Call after <see cref="ConfigureStdioLogging"/> or <see cref="ConfigureHttpLogging"/>.
    /// </summary>
    public static void RegisterSerilogLoggerFactory(IServiceCollection services)
    {
        services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(Log.Logger));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    }

    // ── ValidateOnBuild ───────────────────────────────────────────────────────

    /// <summary>
    /// Enables eager DI graph validation at Build() time so missing or
    /// mis-registered services surface as a hard startup failure instead of
    /// a runtime exception on first tool invocation.
    /// </summary>
    public static void EnableValidateOnBuild(HostApplicationBuilder builder)
    {
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));
    }

    // ── Debug smoke-resolve ───────────────────────────────────────────────────

    /// <summary>
    /// DEBUG only: force-constructs every tool type in <paramref name="toolTypes"/>
    /// so constructor-body throws surface here, not on first tool call.
    /// No-op in Release builds.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void SmokeResolveToolTypes(IServiceProvider services, IEnumerable<Type> toolTypes)
    {
        foreach (var toolType in toolTypes)
        {
            if (services.GetService(toolType) is null)
            {
                throw new InvalidOperationException($"Tool type not resolvable: {toolType.Name}");
            }
        }
    }

    // ── Startup log line ─────────────────────────────────────────────────────

    /// <summary>
    /// Logs the standard "server starting" message and Debug.WriteLine stamp.
    /// </summary>
    public static void LogStartup<TProgram>(
        ILogger<TProgram> logger,
        string logPath,
        HashSet<string> activeModes,
        string modeArg)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Roslyn Sentinel MCP Server starting. Modes: {Modes} (from --mode={ModeArg})",
                string.Join(", ", activeModes), modeArg);
        }

        Debug.WriteLine($"[RoslynSentinel] PID={Environment.ProcessId} | Log={logPath}");
        Debug.WriteLine($"IsInputRedirected: {Console.IsInputRedirected}");
        Debug.WriteLine($"IsOutputRedirected: {Console.IsOutputRedirected}");
    }
}
