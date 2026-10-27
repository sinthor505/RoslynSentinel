namespace RoslynSentinel.Common;

/// <summary>
/// Where this server process writes its per-process output (logs, crash.log, tool_list*.json) and where it
/// looks for the stopped-by-script marker. Resolved once from <c>--working-dir=&lt;path&gt;</c> (or
/// <c>--working-dir &lt;path&gt;</c>); when absent the working directory is the binary directory, never the
/// process CWD, which is exactly the behaviour before the arg existed.
/// <para>
/// <see cref="Resolve"/> is pure: it touches neither the filesystem nor process state, so tests use it
/// directly. <see cref="Current"/> is the process-wide instance built lazily from the real command line, so
/// no host needs an explicit configure call. Directories are created lazily by the code that writes into
/// them, not here.
/// </para>
/// </summary>
public sealed class ServerInstancePaths
{
    private const string WorkingDirFlag = "--working-dir";
    private const string StopMarkerFileName = "stopped-by-script.marker";
    private const string CrashLogFileName = "crash.log";

    private static readonly Lazy<ServerInstancePaths> CurrentLazy = new(
        () => Resolve(Environment.GetCommandLineArgs(), AppContext.BaseDirectory));

    private ServerInstancePaths(string workingDir, bool isExplicit, string stopMarkerPath)
    {
        WorkingDir = workingDir;
        IsExplicit = isExplicit;
        StopMarkerPath = stopMarkerPath;
    }

    /// <summary>The process-wide instance, built on first use from the real command line and base directory.</summary>
    public static ServerInstancePaths Current => CurrentLazy.Value;

    /// <summary>Full path of the working directory (explicit <c>--working-dir</c>, else the binary directory).</summary>
    public string WorkingDir { get; }

    /// <summary>True iff <c>--working-dir</c> was supplied with a non-empty value.</summary>
    public bool IsExplicit { get; }

    /// <summary>Default log directory: <c>WorkingDir\logs</c> (the <c>--log-dir</c> arg still overrides it).</summary>
    public string LogsDir => Path.Combine(WorkingDir, "logs");

    /// <summary>Default crash log file: inside <see cref="LogsDir"/>.</summary>
    public string CrashLogPath => Path.Combine(LogsDir, CrashLogFileName);

    /// <summary>
    /// The stopped-by-script marker file. With an explicit working dir it lives in that dir; otherwise it is
    /// <c>&lt;binaryDir&gt;\..\stopped-by-script.marker</c>, which the control script's handshake depends on.
    /// </summary>
    public string StopMarkerPath { get; }

    /// <summary>Resolves the paths from <paramref name="args"/> and the directory the server binary runs from.</summary>
    public static ServerInstancePaths Resolve(string[] args, string binaryDirectory)
    {
        var requested = GetArgValue(args, WorkingDirFlag);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            // GetFullPath resolves a relative value against the process CWD.
            var explicitDir = Path.GetFullPath(requested);
            return new ServerInstancePaths(explicitDir, true, Path.Combine(explicitDir, StopMarkerFileName));
        }

        var binaryDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binaryDirectory));
        var legacyMarker = Path.GetFullPath(Path.Combine(binaryDir, "..", StopMarkerFileName));
        return new ServerInstancePaths(binaryDir, false, legacyMarker);
    }

    // Mirrors ServerStartupHelpers.GetArgValue (Common cannot reference Server.Advanced): accepts
    // "--flag=value" and "--flag value"; null when absent or the two-token form lacks its value.
    private static string? GetArgValue(string[] args, string flag)
    {
        var inlinePrefix = flag + "=";
        foreach (var arg in args)
        {
            if (arg.StartsWith(inlinePrefix, StringComparison.Ordinal))
            {
                return arg[inlinePrefix.Length..];
            }
        }

        var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
