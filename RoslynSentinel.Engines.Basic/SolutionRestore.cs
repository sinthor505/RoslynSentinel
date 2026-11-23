using System.ComponentModel;
using System.Diagnostics;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// What <see cref="SolutionRestore.RestoreAsync"/> observed. <paramref name="Started"/> is false when dotnet could not be
/// launched (then ExitCode is null). <paramref name="TimedOut"/> means the process tree was terminated at the timeout.
/// <paramref name="OutputTail"/> is the last lines of stdout+stderr, capped, never an exception text.
/// </summary>
public sealed record SolutionRestoreResult(
    bool Succeeded,
    bool Started,
    bool TimedOut,
    int? ExitCode,
    string OutputTail,
    TimeSpan Duration);

/// <summary>
/// Runs <c>dotnet restore &lt;solution&gt;</c> into the projects' own obj/ folders (so the workspace load finds each
/// project.assets.json), as opposed to Build, which restores into a throwaway artifacts path and reloads nothing.
/// Never throws for a launch failure or non-zero exit; only caller cancellation propagates.
/// </summary>
public static class SolutionRestore
{
    private const int TailMaxLines = 12;
    private const int TailMaxChars = 1200;

    // Env vars the MSBuildLocator pins in this process; the child must resolve its own toolset (same list as BuildEngine).
    private static readonly string[] MsBuildPinnedVariables =
    [
        "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly",
        "DOTNET_HOST_PATH", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
    ];

    public static async Task<SolutionRestoreResult> RestoreAsync(string solutionPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var fullPath = Path.GetFullPath(solutionPath);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(fullPath);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("quiet");
        foreach (var variable in MsBuildPinnedVariables)
        {
            startInfo.EnvironmentVariables.Remove(variable);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process = null;
        }

        if (process is null)
        {
            return new SolutionRestoreResult(false, false, false, null, "", stopwatch.Elapsed);
        }

        using (process)
        {
            // Close stdin at once: this runs inside a stdio MCP server whose stdin is the JSON-RPC pipe, which a child must not inherit.
            process.StandardInput.Close();
            var capture = await ChildProcessCapture.RunAsync(process, timeout, cancellationToken);
            var tail = Tail($"{capture.StdOut}\n{capture.StdErr}");
            var succeeded = !capture.TimedOut && capture.ExitCode == 0;
            return new SolutionRestoreResult(succeeded, true, capture.TimedOut, capture.ExitCode, tail, stopwatch.Elapsed);
        }
    }

    internal static string Tail(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r', ' ')).Where(l => l.Length > 0).TakeLast(TailMaxLines);
        var joined = string.Join(" | ", lines);
        return joined.Length <= TailMaxChars ? joined : joined[^TailMaxChars..];
    }
}
