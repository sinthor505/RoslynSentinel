// ServerStartupHelpers.cs v1
using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Extensions.Logging;

namespace RoslynSentinel.Server.Basic;

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
    /// <param name="solutionPath">Value of --solution=, or null. May be absolute or relative;
    /// relative paths are resolved via <see cref="PersistentWorkspaceManager"/>
    /// against the current directory, --base-repo-dir (if set), or the server's install directory.</param>
    /// <param name="baseRepoDirectory">Value of --base-repo-dir=, or null. Used to resolve relative --solution/LoadSolution paths.</param>
    /// <param name="includeTools">Parsed --include-tools value: individual tool-class names to
    /// activate in addition to whatever --mode resolves, e.g. "SentinelGitTools,SentinelScanTools".</param>
    /// <param name="excludeTools">Parsed --exclude-tools value: individual tool-class names to
    /// deactivate even if --mode or --include-tools would otherwise activate them. Always wins.</param>
    /// <param name="operatingMode"><see cref="OperatingMode.Testing"/> when --testing is present,
    /// otherwise <see cref="OperatingMode.Production"/>. Note this is deliberately a separate switch
    /// from --mode, which means tool activation.</param>
    public static void ParseArgs(
        string[] args,
        HashSet<string> allModes,
        out string modeArg,
        out HashSet<string> activeModes,
        out string? solutionPath,
        out string? baseRepoDirectory,
        out HashSet<string> includeTools,
        out HashSet<string> excludeTools,
        out OperatingMode operatingMode)
    {
        operatingMode = args.Contains("--testing") ? OperatingMode.Testing : OperatingMode.Production;

        // No --mode/--modes means no mode set is loaded (activeModes ends up empty) — a caller
        // must opt in via --mode=all, an explicit mode list, or --include-tools.
        modeArg = GetArgValue(args, "--mode") ?? GetArgValue(args, "--modes") ?? "";
        solutionPath = GetArgValue(args, "--solution");
        baseRepoDirectory = GetArgValue(args, "--base-repo-dir");

        var resolvedModeArg = ToolsetAliases.TryGetValue(modeArg, out var alias) ? alias : modeArg;

        var requestedModes = resolvedModeArg.Split(',')
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // "all" expands to the full allModes set; when combined with other entries (e.g.
        // "all,admin") those extras are unioned in rather than being treated as literal mode
        // names alongside a no-op "all" — otherwise "all" could only ever be used alone.
        activeModes = requestedModes.Contains("all")
            ? new HashSet<string>(allModes, StringComparer.OrdinalIgnoreCase)
            : requestedModes;
        if (requestedModes.Contains("all"))
        {
            foreach (var extra in requestedModes.Where(m => !m.Equals("all", StringComparison.OrdinalIgnoreCase)))
            {
                activeModes.Add(extra);
            }
        }

        includeTools = NormalizeToolClassNames(ParseNameList(GetArgValue(args, "--include-tools")));
        excludeTools = NormalizeToolClassNames(ParseNameList(GetArgValue(args, "--exclude-tools")));
    }

    private static HashSet<string> ParseNameList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : value.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every tool class carries a "Sentinel" prefix (e.g. SentinelWorkspaceTools,
    /// SentinelGitTools), so a shortened --include-tools/--exclude-tools name like "GitTools"
    /// can be resolved by prepending it unconditionally when it's not already present.
    /// </summary>
    private static HashSet<string> NormalizeToolClassNames(HashSet<string> names)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            result.Add(name.StartsWith("Sentinel", StringComparison.OrdinalIgnoreCase) ? name : "Sentinel" + name);
        }

        return result;
    }

    /// <summary>
    /// Expands <paramref name="activeModes"/> into individual tool-class names via
    /// <paramref name="modeToToolClasses"/>, unions in <paramref name="includeTools"/>, then
    /// removes anything in <paramref name="excludeTools"/> (exclude always wins, applied last).
    /// </summary>
    public static HashSet<string> ResolveActiveToolClasses(
        HashSet<string> activeModes,
        IReadOnlyDictionary<string, string[]> modeToToolClasses,
        HashSet<string> includeTools,
        HashSet<string> excludeTools)
    {
        var activeToolClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mode in activeModes)
        {
            if (modeToToolClasses.TryGetValue(mode, out var classes))
            {
                foreach (var className in classes)
                {
                    activeToolClasses.Add(className);
                }
            }
        }

        foreach (var className in includeTools)
        {
            activeToolClasses.Add(className);
        }

        foreach (var className in excludeTools)
        {
            activeToolClasses.Remove(className);
        }

        return activeToolClasses;
    }

    /// <summary>
    /// Reads a command-line flag's value, accepting both "--flag=value" and "--flag value" forms.
    /// Returns null if the flag is absent (or "--flag value" is missing its value).
    /// </summary>
    private static string? GetArgValue(string[] args, string flag)
    {
        var inlinePrefix = flag + "=";
        var inline = args.FirstOrDefault(a => a.StartsWith(inlinePrefix, StringComparison.Ordinal));
        if (inline is not null)
        {
            return inline[inlinePrefix.Length..];
        }

        var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Parses --port (either "--port=N" or "--port N"); returns <paramref name="defaultPort"/> if absent or unparseable.</summary>
    public static int ParsePort(string[] args, int defaultPort = 5100)
    {
        var portArg = GetArgValue(args, "--port");
        return int.TryParse(portArg, out var parsed) ? parsed : defaultPort;
    }

    /// <summary>Parses --transport (either "--transport=stdio|http" or "--transport stdio|http"); returns "stdio" if absent.</summary>
    public static string ParseTransport(string[] args) => GetArgValue(args, "--transport") ?? "stdio";

    // ── Fast-exit: --list-tools ───────────────────────────────────────────────

    /// <summary>
    /// If --list-tools is present, writes the tool list and returns true.
    /// The caller should return immediately when this returns true.
    /// </summary>
    public static bool HandleListTools(
        string[] args,
        HashSet<string> activeModes,
        HashSet<string>? includeTools = null,
        HashSet<string>? excludeTools = null)
    {
        if (!args.Contains("--list-tools"))
        {
            return false;
        }

        var outputPath = GetArgValue(args, "--output");
        SentinelConsoleMode.ListTools(activeModes, outputPath, includeTools, excludeTools);
        return true;
    }

    // ── No-tools guard ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns an operator-facing explanation when the resolved arguments would activate no tool
    /// classes at all, or null when at least one would be active. Call after
    /// <see cref="HandleListTools"/> and before building the host.
    /// </summary>
    /// <remarks>
    /// A server with no tools cannot do anything, but the failure used to surface from deep inside
    /// DI: launching with no <c>--mode</c> registered zero tool classes, then the DEBUG smoke
    /// check demanded <c>SentinelWorkspaceTools</c> unconditionally and threw
    /// "Tool type not resolvable: SentinelWorkspaceTools". That names a type the operator never
    /// mentioned and says nothing about the missing flag. Checked here instead, where the actual
    /// cause — no <c>--mode</c> and no <c>--include-tools</c> — is still known.
    ///
    /// Deliberately not defaulting to <c>--mode=all</c>: which tools are exposed changes agent
    /// behaviour measurably (see docs/current/project_wholefilewrite_gating_overnight_result_2026_09_08.md,
    /// where gating whole-file writes off scored 26/26 against a 47% baseline), so silently
    /// picking a tool surface for the caller would be the wrong kind of helpful. Fail, and say how
    /// to choose.
    /// </remarks>
    public static string? DescribeNoActiveToolsFailure(
        string modeArg,
        IReadOnlyDictionary<string, string[]> modeToToolClasses,
        HashSet<string> activeModes,
        HashSet<string> includeTools,
        HashSet<string> excludeTools)
    {
        if (ResolveActiveToolClasses(activeModes, modeToToolClasses, includeTools, excludeTools).Count > 0)
        {
            return null;
        }

        var availableModes = string.Join(", ", modeToToolClasses.Keys.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));

        // The three ways to reach zero are distinguished, because the fix differs for each and
        // the operator can't tell them apart from the outside.
        if (activeModes.Count == 0 && includeTools.Count == 0)
        {
            return "No tools would be active, so the server has nothing to serve: neither --mode nor " +
                   "--include-tools was supplied (both are absent or empty). Pass --mode=all for every " +
                   $"tool, --mode=<name>[,<name>] for a subset ({availableModes}), or " +
                   "--include-tools=<ToolClassName>[,<ToolClassName>] to activate individual classes. " +
                   "Use --list-tools to print the tool surface a given combination would expose.";
        }

        if (excludeTools.Count > 0)
        {
            return "No tools would be active: --exclude-tools removed every class that --mode/" +
                   $"--include-tools selected. --mode='{modeArg}', " +
                   $"--exclude-tools={string.Join(",", excludeTools.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))}. " +
                   "Exclude always wins and is applied last, so narrow it or widen --mode. " +
                   "Use --list-tools to check a combination before launching.";
        }

        return "No tools would be active: nothing supplied to --mode/--include-tools matched a known " +
               $"mode or tool class. --mode='{modeArg}' (known modes: {availableModes}). Tool-class " +
               "names are matched with an implied \"Sentinel\" prefix, so both 'GitTools' and " +
               "'SentinelGitTools' are accepted. Use --list-tools to see valid combinations.";
    }

    /// <summary>
    /// Checks the no-active-tools condition and, when it applies, reports it and returns true so
    /// the caller can return without building a host. Shared by all four entry points so the
    /// message and exit behaviour can't drift between transports.
    /// </summary>
    /// <remarks>
    /// Written to stderr, never stdout: under the stdio transport stdout carries the MCP protocol
    /// stream, and a plain-text diagnostic there would corrupt the first frame the client reads —
    /// turning a clear configuration error into a protocol parse failure. Stderr is safe on both
    /// transports, and this runs before Serilog is configured on some paths, so Console is used
    /// rather than a logger.
    /// </remarks>
    public static bool HandleNoActiveTools(
        string modeArg,
        IReadOnlyDictionary<string, string[]> modeToToolClasses,
        HashSet<string> activeModes,
        HashSet<string> includeTools,
        HashSet<string> excludeTools)
    {
        var failure = DescribeNoActiveToolsFailure(modeArg, modeToToolClasses, activeModes, includeTools, excludeTools);
        if (failure is null)
        {
            return false;
        }

        Console.Error.WriteLine(failure);
        Debug.WriteLine(failure);
        Environment.ExitCode = 2;
        return true;
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Output template shared by every Serilog file/console sink this server configures. RunId and
    /// StepId are enriched properties (see <see cref="ConfigureStdioLogging"/>/
    /// <see cref="ConfigureHttpLogging"/>/<see cref="AttachCrashHandlers"/>), and must be named here
    /// explicitly to appear in the written file — Serilog does not print enriched properties unless
    /// the template references them. Always present (defaulting to "-" when the caller supplied
    /// none) so the column position and grep pattern never change between a correlated and an
    /// uncorrelated run.
    /// </summary>
    private const string LogOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [Run={RunId} Step={StepId}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Configures Serilog for stdio servers: file-only, Information level.
    /// Stdout must stay clean for the MCP stdio transport.
    /// </summary>
    /// <param name="logFileName">Base log file name; a timestamp is inserted before the extension.</param>
    /// <param name="logDirectory">Directory to write the log into. Defaults to
    /// <c>AppDomain.CurrentDomain.BaseDirectory\logs</c> when null. Created if it doesn't exist.</param>
    /// <param name="runId">Value stamped onto every log line as "Run=". Defaults to "-" when null.</param>
    /// <param name="stepId">Value stamped onto every log line as "Step=". Defaults to "-" when null.</param>
    public static string ConfigureStdioLogging(
        string logFileName = "server.log", string? logDirectory = null, string? runId = null, string? stepId = null)
    {
        var logPath = Path.Combine(ResolveLogDirectory(logDirectory), TimestampedLogFileName(logFileName));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("RunId", runId ?? "-")
            .Enrich.WithProperty("StepId", stepId ?? "-")
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Infinite, outputTemplate: LogOutputTemplate)
            .CreateLogger();
        return logPath;
    }

    /// <summary>
    /// Configures Serilog for HTTP servers: file + console, Verbose level.
    /// Console output is safe because stdout is not the MCP transport.
    /// </summary>
    /// <param name="logFileName">Base log file name; a timestamp is inserted before the extension.</param>
    /// <param name="logDirectory">Directory to write the log into. Defaults to
    /// <c>AppDomain.CurrentDomain.BaseDirectory\logs</c> when null. Created if it doesn't exist.</param>
    /// <param name="runId">Value stamped onto every log line as "Run=". Defaults to "-" when null.</param>
    /// <param name="stepId">Value stamped onto every log line as "Step=". Defaults to "-" when null.</param>
    public static string ConfigureHttpLogging(
        string logFileName = "http-host.log", string? logDirectory = null, string? runId = null, string? stepId = null)
    {
        var logPath = Path.Combine(ResolveLogDirectory(logDirectory), TimestampedLogFileName(logFileName));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("RunId", runId ?? "-")
            .Enrich.WithProperty("StepId", stepId ?? "-")
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Infinite, outputTemplate: LogOutputTemplate)
            .WriteTo.Console(outputTemplate: LogOutputTemplate)
            .CreateLogger();
        return logPath;
    }

    /// <summary>
    /// Parses --log-dir (either "--log-dir=path" or "--log-dir path"); returns null if absent, in
    /// which case callers fall back to their own default (see <see cref="ResolveLogDirectory"/>).
    /// </summary>
    public static string? ParseLogDirectory(string[] args) => GetArgValue(args, "--log-dir");

    /// <summary>
    /// Parses --run-id (either "--run-id=value" or "--run-id value"); returns null if absent, in
    /// which case logging falls back to "-" (see <see cref="LogOutputTemplate"/>). Not required —
    /// a server launched without it (e.g. an interactive VS Code session) logs and runs exactly as
    /// before.
    /// </summary>
    public static string? ParseRunId(string[] args) => GetArgValue(args, "--run-id");

    /// <summary>
    /// Parses --step-id (either "--step-id=value" or "--step-id value"); returns null if absent,
    /// same fallback behaviour as <see cref="ParseRunId"/>.
    /// </summary>
    public static string? ParseStepId(string[] args) => GetArgValue(args, "--step-id");

    /// <summary>
    /// Resolves the effective log directory: <paramref name="logDirectory"/> when supplied
    /// (creating it if missing), otherwise the historical default of
    /// <c>AppDomain.CurrentDomain.BaseDirectory\logs</c>.
    /// </summary>
    private static string ResolveLogDirectory(string? logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        }

        Directory.CreateDirectory(logDirectory);
        return logDirectory;
    }

    /// <summary>
    /// Inserts a yyyyMMdd-HHmmss timestamp before the file extension so each server restart
    /// starts a fresh log file instead of appending to (or day-rolling into) a shared one.
    /// </summary>
    private static string TimestampedLogFileName(string logFileName)
    {
        var extension = Path.GetExtension(logFileName);
        var stem = Path.GetFileNameWithoutExtension(logFileName);
        return $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}";
    }

    // ── Crash handlers ────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches AppDomain and TaskScheduler unhandled-exception handlers.
    /// Logs to Serilog and writes a crash.log file in the logs directory.
    /// Safe to call for both stdio and HTTP hosts; does not write to stdout.
    /// </summary>
    /// <param name="logDirectory">Directory to write crash.log into. Defaults to
    /// <c>AppDomain.CurrentDomain.BaseDirectory\logs</c> when null, matching
    /// <see cref="ConfigureStdioLogging"/>/<see cref="ConfigureHttpLogging"/>.</param>
    /// <param name="runId">Value stamped onto each crash.log entry. Defaults to "-" when null.</param>
    /// <param name="stepId">Value stamped onto each crash.log entry. Defaults to "-" when null.</param>
    public static void AttachCrashHandlers(string? logDirectory = null, string? runId = null, string? stepId = null)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "UNHANDLED EXCEPTION (IsTerminating={IsTerminating}): {Message}",
                e.IsTerminating, ex?.Message ?? e.ExceptionObject?.ToString());
            try
            {
                var crashPath = Path.Combine(ResolveLogDirectory(logDirectory), "crash.log");
                File.AppendAllText(crashPath,
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CRASH (IsTerminating={e.IsTerminating}) [Run={runId ?? "-"} Step={stepId ?? "-"}]\n" +
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
    /// DEBUG only: force-constructs each tool type in <paramref name="toolTypes"/> that
    /// <paramref name="activeToolClasses"/> says was registered, so constructor-body throws
    /// surface here rather than on first tool call. No-op in Release builds.
    /// </summary>
    /// <remarks>
    /// Skipping types that were never registered is the point of taking
    /// <paramref name="activeToolClasses"/>. This check previously resolved its whole list
    /// unconditionally, which meant any narrower <c>--include-tools</c>/<c>--mode</c> selection
    /// crashed at startup complaining about a type the operator had deliberately not asked for —
    /// the caller's own comment above each ActiveToolTypes list predicted exactly this. Callers
    /// that reach zero active classes are caught earlier by
    /// <see cref="DescribeNoActiveToolsFailure"/>, so an empty set here is not treated as an error.
    ///
    /// The failure is reported via <see cref="ServiceProviderServiceExtensions.GetRequiredService"/>
    /// rather than a null check on GetService: the original discarded the real reason (the
    /// unresolvable *dependency*) and reported only the outer tool type, which sent diagnosis to
    /// the wrong place.
    /// </remarks>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void SmokeResolveToolTypes(
        IServiceProvider services,
        IEnumerable<Type> toolTypes,
        HashSet<string> activeToolClasses)
    {
        foreach (var toolType in toolTypes)
        {
            if (!activeToolClasses.Contains(toolType.Name))
            {
                continue;
            }

            try
            {
                services.GetRequiredService(toolType);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Tool class '{toolType.Name}' is active but could not be constructed. One of its " +
                    $"dependencies is not registered — see the inner exception for which. {ex.Message}",
                    ex);
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
        string modeArg,
        HashSet<string>? includeTools = null,
        HashSet<string>? excludeTools = null,
        OperatingMode operatingMode = OperatingMode.Production)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            // OperatingMode is logged explicitly (and names the doc root it selects) because a
            // testing-mode server reading production docs — or the reverse — is otherwise only
            // detectable by noticing that ProjectDoc returned the wrong file.
            logger.LogInformation(
                "Roslyn Sentinel MCP Server starting. Modes: {Modes} (from --mode={ModeArg}) | IncludeTools: {IncludeTools} | ExcludeTools: {ExcludeTools} | OperatingMode: {OperatingMode} (ProjectDoc root: {DocRoot})",
                string.Join(", ", activeModes), modeArg,
                includeTools is { Count: > 0 } ? string.Join(", ", includeTools) : "(none)",
                excludeTools is { Count: > 0 } ? string.Join(", ", excludeTools) : "(none)",
                operatingMode,
                operatingMode == OperatingMode.Testing ? "docs/testing/" : "docs/");
        }

        Debug.WriteLine($"[RoslynSentinel] PID={Environment.ProcessId} | Log={logPath}");
        Debug.WriteLine($"IsInputRedirected: {Console.IsInputRedirected}");
        Debug.WriteLine($"IsOutputRedirected: {Console.IsOutputRedirected}");
    }
}
