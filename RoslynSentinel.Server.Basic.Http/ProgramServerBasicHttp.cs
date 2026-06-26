// ProgramServerBasicHttp.cs v1
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server.Basic.Http;

public static class ProgramHttpHostBasic
{
    // All modes available in the Basic variant.
    private static readonly HashSet<string> AllModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation",
        };

    public static async Task Main(string[] args)
    {
        // ── Arg parsing ──────────────────────────────────────────────────────
        ServerStartupHelpers.ParseArgs(args, AllModes, out var modeArg, out var activeModes, out var solutionPath);
        var port = ServerStartupHelpers.ParsePort(args, defaultPort: 5100);

        if (ServerStartupHelpers.HandleListTools(args, activeModes))
        {
            return;
        }

        // ── Logging (file + console — stdout is safe for HTTP transport) ─────
        var logPath = ServerStartupHelpers.ConfigureHttpLogging();
        ServerStartupHelpers.AttachCrashHandlers();

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

        builder.Services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = builder.Services.AddMcpServer().WithHttpTransport();
        mcpBuilder.AddRoslynSentinelToolsBasic(builder.Services, activeModes);

        var app = builder.Build();
        app.MapMcp("/mcp");

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("RoslynSentinel.HttpHost.Basic");

        app.Services.WarmupAndAutoLoadBasic(solutionPath, logger);
        SentinelConsoleMode.WriteStartupDump(app.Services, AppDomain.CurrentDomain.BaseDirectory, modeArg);
        SentinelConsoleMode.WriteMethodInventory(AppDomain.CurrentDomain.BaseDirectory, modeArg);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "RoslynSentinel Basic HTTP Host starting. Port={Port} | Modes={Modes} | Log={Log}",
                port, string.Join(", ", activeModes), logPath);
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
