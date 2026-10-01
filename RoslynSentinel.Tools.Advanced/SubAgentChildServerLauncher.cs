using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;

using RoslynSentinel.Common.GitWorktree;

namespace RoslynSentinel.Tools.Advanced;

/// <summary>
/// Launches the child RoslynSentinel server that a SubAgent/SubAgentEval call's model drives: a fresh
/// git worktree of the repo, a private build of that worktree's <c>Server.Advanced</c>, a stdio
/// <see cref="McpClient"/> connected to it, and an explicit <c>LoadSolution</c>. Shared by both tools,
/// which need the identical sequence. Modelled on PlanStepRunner's <c>RunStepAsync</c>.
/// </summary>
/// <remarks>
/// Never writes to stdout: this runs inside a stdio MCP server, where stdout carries the JSON-RPC
/// stream. Progress goes to <paramref name="logger"/>.
/// </remarks>
public sealed class SubAgentChildServerLauncher(ILogger logger)
{
    /// <summary>
    /// Tool classes the child server never exposes, on every launch regardless of which tool spawned
    /// it. This is the recursion guard: a model running inside a sub-agent call cannot see either
    /// tool, so nesting depth is capped at one by construction rather than checked at runtime.
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludedToolClasses = ["SubAgentTools", "SubAgentEvalTools"];

    /// <summary>
    /// Mode giving the dispatched model the same tool surface Claude Code itself drives with.
    /// <c>--mode</c> (not <c>--include-tools</c>) because "Claude" is a mode name, and
    /// <c>--include-tools</c> takes tool-class names.
    /// </summary>
    public const string ChildToolMode = "Claude";

    private const int MaxBuildDetailChars = 4000;

    /// <summary>The command-line arguments the child server is launched with.</summary>
    public static IReadOnlyList<string> BuildServerArguments(string worktreePath, string model, string logDirectory, string runId) =>
    [
        "--base-repo-dir=" + worktreePath,
        "--mode=" + ChildToolMode,
        // --exclude-tools always wins over --mode, so this holds even if a mode ever lists them.
        "--exclude-tools=" + string.Join(",", ExcludedToolClasses),
        // --testing puts ProjectDoc on docs/testing/: the worktree is a copy of this very repo, so
        // runner-specific docs and production docs share basenames (see PlanStepRunner/Program.cs).
        "--testing",
        "--llm-model=" + model,
        "--log-dir=" + logDirectory,
        "--run-id=" + runId,
        "--step-id=" + SubAgentWorktreeStep.FolderName,
    ];

    /// <param name="solutionPath">Absolute path of the loaded solution in the live session. Its git repo is what gets branched.</param>
    /// <param name="runId">From <see cref="SubAgentRunNaming.NewRunId"/>.</param>
    /// <param name="baseRef">Ref the worktree branches from, e.g. "HEAD".</param>
    /// <param name="model">LM Studio model name the dispatched model runs as.</param>
    /// <exception cref="SubAgentLaunchException">Any launch step failed; everything created so far has been cleaned up.</exception>
    public async Task<SubAgentChildServer> LaunchAsync(
        string solutionPath, string runId, string baseRef, string model, CancellationToken cancellationToken)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath)
            ?? throw new SubAgentLaunchException(ToolErrorCode.InvalidArgument, "The loaded solution path has no directory.");

        var repoRoot = await ResolveRepoRootAsync(solutionDirectory, cancellationToken);
        var solutionRelativePath = Path.GetRelativePath(repoRoot, solutionPath);

        var runDirectory = SubAgentRunNaming.RunDirectory(repoRoot, runId);
        var step = SubAgentWorktreeStep.Instance;
        var git = new GitWorktreeManager(
            repoRoot,
            new SharedBranchStrategy(SubAgentRunNaming.BranchName(runId), baseRef),
            runDirectory,
            message => logger.LogInformation("SubAgent {RunId}: {Message}", runId, message));

        string worktreePath;
        try
        {
            git.EnsureBranchExists(step);
            worktreePath = git.CreateWorktree(step);
        }
        catch (InvalidOperationException ex)
        {
            // GitWorktreeManager's messages are already written for a human/agent reader.
            throw new SubAgentLaunchException(
                ToolErrorCode.Exception,
                $"Could not create the isolated worktree for this call (base ref '{baseRef}'): {FirstLine(ex.Message)}",
                ex.Message, ex);
        }

        McpClient? client = null;
        try
        {
            var stepDirectory = Path.Combine(runDirectory, SubAgentWorktreeStep.FolderName);
            var logDirectory = Path.Combine(stepDirectory, "Logs");
            // The build output lives beside the worktree, not inside it, so it never shows up as a
            // file the model touched in GetDirtyPaths.
            var buildDirectory = Path.Combine(stepDirectory, "Build");
            Directory.CreateDirectory(logDirectory);
            Directory.CreateDirectory(buildDirectory);

            var serverProject = Path.Combine(
                worktreePath, "RoslynSentinel.Server.Advanced", "RoslynSentinel.Server.Advanced.csproj");
            if (!File.Exists(serverProject))
            {
                throw new SubAgentLaunchException(
                    ToolErrorCode.NotFound,
                    "The worktree does not contain RoslynSentinel.Server.Advanced\\RoslynSentinel.Server.Advanced.csproj. " +
                    "SubAgent tools launch a child RoslynSentinel server built from the loaded repo, so the loaded solution " +
                    "must be the RoslynSentinel repo itself.");
            }

            await BuildServerAsync(serverProject, buildDirectory, cancellationToken);

            var serverExe = Path.Combine(buildDirectory, "RoslynSentinel.Server.Advanced.exe");
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "RoslynSentinel.Server.Advanced",
                Command = serverExe,
                Arguments = BuildServerArguments(worktreePath, model, logDirectory, runId).ToList(),
                WorkingDirectory = worktreePath,
            });

            client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

            // Explicit LoadSolution, never --solution: the server's own auto-load is fire-and-forget,
            // so it cannot be trusted to have finished before the first tool call.
            var loadResult = await client.CallToolAsync(
                "LoadSolution",
                new Dictionary<string, object?>
                {
                    ["reason"] = "SubAgent: loading the worktree solution before running the model.",
                    ["solutionPath"] = Path.Combine(worktreePath, solutionRelativePath),
                },
                progress: null, options: null, cancellationToken: cancellationToken);
            if (!ChildToolResult.IsSuccess(loadResult, out var loadError))
            {
                throw new SubAgentLaunchException(
                    ToolErrorCode.SolutionNotLoaded,
                    "The child server started but could not load the worktree's solution.",
                    loadError);
            }

            return new SubAgentChildServer(client, worktreePath, runDirectory, logDirectory, runId, step, git, logger);
        }
        catch (Exception)
        {
            // Failed after the worktree existed: tear down what was created so a failed launch
            // leaves no worktree or branch behind, then let the original failure propagate.
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch (Exception disposeEx)
                {
                    logger.LogWarning(disposeEx, "SubAgent {RunId}: disposing the child MCP client after a failed launch also failed.", runId);
                }
            }

            await SubAgentChildServer.RemoveWorktreeAndBranchAsync(git, worktreePath, step, logger, runId);
            throw;
        }
    }

    /// <summary>Resolves the git top-level directory containing <paramref name="directory"/>.</summary>
    private static async Task<string> ResolveRepoRootAsync(string directory, CancellationToken cancellationToken)
    {
        (int ExitCode, string StdOut, string StdErr) result;
        try
        {
            result = await RunProcessAsync("git", ["rev-parse", "--show-toplevel"], directory, cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SubAgentLaunchException(
                ToolErrorCode.Exception, "Could not run git to locate the repository for the loaded solution.", ex.Message, ex);
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new SubAgentLaunchException(
                ToolErrorCode.InvalidArgument,
                "The loaded solution is not inside a git repository, so there is nothing to create an isolated worktree from.",
                result.StdErr.Trim());
        }

        return Path.GetFullPath(result.StdOut.Trim());
    }

    /// <summary>
    /// Builds the worktree's own server into an isolated output directory. Never build.ps1: it kills
    /// every RoslynSentinel process system-wide, which would take down the live session (see
    /// PlanStepRunner's DotnetProcess).
    /// </summary>
    private static async Task BuildServerAsync(string projectPath, string outputDirectory, CancellationToken cancellationToken)
    {
        (int ExitCode, string StdOut, string StdErr) result;
        try
        {
            result = await RunProcessAsync(
                "dotnet",
                ["build", projectPath, "-c", "Debug", "-o", outputDirectory, "--nologo", "-v", "quiet"],
                Path.GetDirectoryName(projectPath)!,
                cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SubAgentLaunchException(
                ToolErrorCode.BuildFailed, "Could not run 'dotnet build' for the worktree's server.", ex.Message, ex);
        }

        if (result.ExitCode != 0)
        {
            throw new SubAgentLaunchException(
                ToolErrorCode.BuildFailed,
                $"Building the worktree's RoslynSentinel server failed (exit code {result.ExitCode}). " +
                "The base ref may not compile; try a different baseBranch.",
                Tail($"{result.StdOut}\n{result.StdErr}".Trim(), MaxBuildDetailChars));
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the cancellation and the kill.
            }

            throw;
        }

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).Trim();
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "..." + text[^maxChars..];
}

/// <summary>
/// A live child server plus everything needed to tear it down. Disposing kills the child process
/// (the stdio transport owns it), then force-removes the worktree and deletes its branch.
/// </summary>
public sealed class SubAgentChildServer : IAsyncDisposable
{
    private const int RemoveAttempts = 3;
    private static readonly TimeSpan RemoveRetryDelay = TimeSpan.FromMilliseconds(750);

    private readonly ILogger _logger;
    private readonly IWorktreeStep _step;
    private int _disposed;

    internal SubAgentChildServer(
        McpClient mcpClient, string worktreePath, string runDirectory, string logDirectory,
        string runId, IWorktreeStep step, GitWorktreeManager gitWorktreeManager, ILogger logger)
    {
        McpClient = mcpClient;
        WorktreePath = worktreePath;
        RunDirectory = runDirectory;
        LogDirectory = logDirectory;
        RunId = runId;
        GitWorktreeManager = gitWorktreeManager;
        _step = step;
        _logger = logger;
    }

    /// <summary>Client connected to the child server, with the worktree's solution already loaded.</summary>
    public McpClient McpClient { get; }

    /// <summary>The run's disposable git worktree, where the dispatched model's edits land.</summary>
    public string WorktreePath { get; }

    /// <summary>Folder owning this run's worktree, build output and logs. Logs outlive disposal.</summary>
    public string RunDirectory { get; }

    /// <summary>Where the run's transcript.json and agent.log go; survives the worktree's removal.</summary>
    public string LogDirectory { get; }

    public string RunId { get; }

    public GitWorktreeManager GitWorktreeManager { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // The client first: disposing it kills the child process, which otherwise holds file locks
        // inside the worktree that would make the removal below fail.
        try
        {
            await McpClient.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SubAgent {RunId}: disposing the child MCP client failed.", RunId);
        }

        await RemoveWorktreeAndBranchAsync(GitWorktreeManager, WorktreePath, _step, _logger, RunId);
    }

    /// <summary>
    /// Force-removes the worktree (retrying briefly while the just-killed child process releases its
    /// file locks) and deletes the run's branch. Logs, never throws: a Windows path-length failure
    /// removing deeply nested build output leaves a harmless leftover worktree, not a failed call.
    /// </summary>
    internal static async Task RemoveWorktreeAndBranchAsync(
        GitWorktreeManager git, string worktreePath, IWorktreeStep step, ILogger logger, string runId)
    {
        string? removeError = null;
        for (var attempt = 1; attempt <= RemoveAttempts; attempt++)
        {
            removeError = git.TryRemoveWorktree(worktreePath, force: true);
            if (removeError is null)
            {
                break;
            }

            if (attempt < RemoveAttempts)
            {
                await Task.Delay(RemoveRetryDelay);
            }
        }

        if (removeError is not null)
        {
            logger.LogWarning(
                "SubAgent {RunId}: could not remove worktree {WorktreePath}; leaving it in place. {Error}",
                runId, worktreePath, removeError);
            return;
        }

        var branchError = git.TryDeleteBranch(step);
        if (branchError is not null)
        {
            logger.LogWarning("SubAgent {RunId}: could not delete the run's branch. {Error}", runId, branchError);
        }
    }
}

/// <summary>Reads a RoslynSentinel tool result as seen by an <see cref="McpClient"/>.</summary>
public static class ChildToolResult
{
    /// <summary>
    /// The concatenated text content of <paramref name="result"/>, or empty when it has none.
    /// </summary>
    public static string Text(ModelContextProtocol.Protocol.CallToolResult result)
    {
        var builder = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is ModelContextProtocol.Protocol.TextContentBlock text)
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when the call succeeded. RoslynSentinel reports failure inside the JSON envelope
    /// (<c>isSuccess: false</c>), so the MCP-level <c>IsError</c> flag alone is not enough.
    /// <paramref name="failureText"/> is the offending text on failure, trimmed to a safe length.
    /// </summary>
    public static bool IsSuccess(ModelContextProtocol.Protocol.CallToolResult result, out string? failureText)
    {
        failureText = null;
        var text = Text(result);

        var envelopeSaysFailure = false;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("isSuccess", out var isSuccess)
                && isSuccess.ValueKind == JsonValueKind.False)
            {
                envelopeSaysFailure = true;
            }
        }
        catch (JsonException)
        {
            // Not an envelope (e.g. a transport-level error string); fall back to IsError alone.
        }

        if (result.IsError == true || envelopeSaysFailure)
        {
            failureText = text.Length <= 2000 ? text : text[..2000] + "...";
            return false;
        }

        return true;
    }
}
