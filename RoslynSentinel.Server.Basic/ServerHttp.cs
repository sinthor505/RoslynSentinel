// ServerHttp.cs v1
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server.Basic;

public static class ServerHttp
{
    // All modes available in the Basic variant.
    private static readonly HashSet<string> AllModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation",
        };

    public static async Task Startup(string[] args)
    {
        // ── Arg parsing ──────────────────────────────────────────────────────
        ServerStartupHelpers.ParseArgs(args, AllModes, out var modeArg, out var activeModes, out var solutionPath, out var baseRepoDirectory, out var includeTools, out var excludeTools, out var operatingMode);
        var port = ServerStartupHelpers.ParsePort(args, defaultPort: 5100);

        if (ServerStartupHelpers.HandleListTools(args, activeModes, includeTools, excludeTools))
        {
            return;
        }

        // Before binding a port: a server with no tools can't serve anything, and saying so here
        // names the missing flag instead of accepting connections that expose nothing.
        if (ServerStartupHelpers.HandleNoActiveTools(
                modeArg, ToolClassRegistry.BasicModeToToolClasses, activeModes, includeTools, excludeTools))
        {
            return;
        }

        // ── Logging (file + console — stdout is safe for HTTP transport) ─────
        var logDirectory = ServerStartupHelpers.ParseLogDirectory(args);
        var runId = ServerStartupHelpers.ParseRunId(args);
        var stepId = ServerStartupHelpers.ParseStepId(args);
        var logPath = ServerStartupHelpers.ConfigureHttpLogging(logDirectory: logDirectory, runId: runId, stepId: stepId);
        ServerStartupHelpers.AttachCrashHandlers(logDirectory, runId, stepId);

        // ── Host ─────────────────────────────────────────────────────────────
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseDefaultServiceProvider((_, options) =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        builder.Logging.ClearProviders();
        ServerStartupHelpers.RegisterSerilogLoggerFactory(builder.Services);
        builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(port));

        builder.Services.AddRoslynSentinelHostOptions(operatingMode);
        builder.Services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = builder.Services.AddMcpServer().WithHttpTransport();
        mcpBuilder.AddRoslynSentinelToolsBasic(builder.Services, activeModes, includeTools, excludeTools);

        var app = builder.Build();
        app.MapMcp("/mcp");

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("RoslynSentinel.HttpHost.Basic");

        app.Services.WarmupAndAutoLoadBasic(solutionPath, logger, baseRepoDirectory);
        SentinelConsoleMode.WriteStartupDump(app.Services, AppDomain.CurrentDomain.BaseDirectory, modeArg);
        SentinelConsoleMode.WriteMethodInventory(AppDomain.CurrentDomain.BaseDirectory, modeArg);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "RoslynSentinel Basic HTTP Host starting. Port={Port} | Modes={Modes} | IncludeTools={IncludeTools} | ExcludeTools={ExcludeTools} | Log={Log}",
                port, string.Join(", ", activeModes),
                includeTools.Count > 0 ? string.Join(", ", includeTools) : "(none)",
                excludeTools.Count > 0 ? string.Join(", ", excludeTools) : "(none)",
                logPath);
        }

        Console.WriteLine($"[RoslynSentinel.Basic.HttpHost] Listening on http://0.0.0.0:{port}/mcp | PID={Environment.ProcessId}");
        Console.WriteLine($"[RoslynSentinel.Basic.HttpHost] Log: {logPath}");

        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }
}
