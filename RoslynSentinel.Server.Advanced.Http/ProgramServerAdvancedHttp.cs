// ProgramServerAdvancedHttp.cs v1
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server.Advanced.Http;

public class ProgramHttpHostAdvanced
{
    // All modes available in the Advanced variant. Asyncify is Advanced-only.
    private static readonly HashSet<string> AllModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation", "Asyncify",
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

        builder.Services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = builder.Services.AddMcpServer().WithHttpTransport();
        mcpBuilder.AddRoslynSentinelToolsAdvanced(builder.Services, activeModes);

        var app = builder.Build();
        app.MapMcp("/mcp");

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("RoslynSentinel.HttpHost.Advanced");

        app.Services.WarmupAndAutoLoadAdvanced(solutionPath, logger);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "RoslynSentinel Advanced HTTP Host starting. Port={Port} | Modes={Modes} | Log={Log}",
                port, string.Join(", ", activeModes), logPath);
        }

        Console.WriteLine($"[RoslynSentinel.Advanced.HttpHost] Listening on http://0.0.0.0:{port}/mcp | PID={Environment.ProcessId}");
        Console.WriteLine($"[RoslynSentinel.Advanced.HttpHost] Log: {logPath}");

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
