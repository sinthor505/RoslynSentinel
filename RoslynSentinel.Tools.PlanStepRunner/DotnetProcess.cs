using System.Diagnostics;

namespace RoslynSentinel.Tools.PlanStepRunner;

/// <summary>
/// Builds a single project directly via `dotnet build`, deliberately NOT build.ps1 — build.ps1
/// unconditionally kills every *RoslynSentinel* process system-wide by name (not by path) and
/// refreshes a shared bin-vscode\Advanced copy tied to the user's live VS Code MCP connection.
/// Running it from inside a worktree would risk killing that live session and any other
/// worktree's in-flight step, so each worktree instead gets its own isolated `dotnet build`
/// output directory it never shares with anything else.
/// </summary>
public static class DotnetProcess
{
    public static async Task BuildAsync(string projectPath, string outputDirectory)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputDirectory);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");

        using var process = Process.Start(psi)!;
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            throw new InvalidOperationException(
                $"dotnet build failed for {projectPath} (exit {process.ExitCode}):\n{stdOut}\n{stdErr}");
        }
    }
}
