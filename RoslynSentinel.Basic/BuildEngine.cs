using System.Diagnostics;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Basic;

public class BuildEngine
{
    private readonly ISolutionProvider _workspaceManager;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly ILogger<BuildEngine> _logger;

    public BuildEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _diagnosticEngine = new DiagnosticEngine(workspaceManager);
        _logger = new NullLogger<BuildEngine>();
    }
    public BuildEngine(ISolutionProvider workspaceManager, ILogger<BuildEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _diagnosticEngine = new DiagnosticEngine(workspaceManager);
        _logger = logger;
    }
    public BuildEngine(ISolutionProvider workspaceManager, DiagnosticEngine diagnosticEngine)
    {
        _workspaceManager = workspaceManager;
        _diagnosticEngine = diagnosticEngine;
        _logger = new NullLogger<BuildEngine>();
    }
    public BuildEngine(ISolutionProvider workspaceManager, DiagnosticEngine diagnosticEngine, ILogger<BuildEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _diagnosticEngine = diagnosticEngine;
        _logger = logger;
    }

    public async Task<EngineResultWrapper<BuildResult>> RunQuickBuildAsync(
        ToolScope scope, string? scopeName, int maxDetails, CancellationToken cancellationToken = default)
    {
        var start = DateTime.UtcNow;
        DiagnosticSummary? summary;
        List<string> projectsCompiled;

        if (scope == ToolScope.file)
        {
            if (string.IsNullOrEmpty(scopeName))
            {
                return new EngineResultWrapper<BuildResult>(EngineOutcome.InvalidInput,
                    error: new EngineError("scopeName (filePath) is required when scope=file."));
            }
            var fileResult = await _diagnosticEngine.GetFileDiagnosticsAsync(scopeName, cancellationToken);
            if (!fileResult.TryGetData(out summary))
            {
                return new EngineResultWrapper<BuildResult>(fileResult.Outcome, error: fileResult.Error);
            }
            var solutionForFile = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var documentId = solutionForFile.GetDocumentIdsWithFilePath(scopeName).FirstOrDefault();
            var owningProject = documentId is null ? null : solutionForFile.GetProject(documentId.ProjectId);
            projectsCompiled = owningProject is null ? [] : [owningProject.Name];
        }
        else if (scope == ToolScope.project)
        {
            if (string.IsNullOrEmpty(scopeName))
            {
                return new EngineResultWrapper<BuildResult>(EngineOutcome.InvalidInput,
                    error: new EngineError("scopeName (projectName) is required when scope=project."));
            }
            var projectResult = await _diagnosticEngine.GetProjectDiagnosticsAsync(scopeName, cancellationToken);
            if (!projectResult.TryGetData(out summary))
            {
                return new EngineResultWrapper<BuildResult>(projectResult.Outcome, error: projectResult.Error);
            }
            var solutionForProject = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            projectsCompiled = solutionForProject.Projects.Any(p => p.Name == scopeName) ? [scopeName] : [];
        }
        else
        {
            var solutionResult = await _diagnosticEngine.GetSolutionDiagnosticsAsync(maxDetails, cancellationToken);
            if (!solutionResult.TryGetData(out summary))
            {
                return new EngineResultWrapper<BuildResult>(solutionResult.Outcome, error: solutionResult.Error);
            }
            var solutionForAll = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var compiledProjects = new List<string>();
            foreach (var project in solutionForAll.Projects)
            {
                if (await project.GetCompilationAsync(cancellationToken) is not null)
                {
                    compiledProjects.Add(project.Name);
                }
            }
            projectsCompiled = compiledProjects;
        }

        if (projectsCompiled.Count == 0)
        {
            return EngineResultWrapper<BuildResult>.Failure(
                EngineOutcome.InvalidInput,
                new EngineError(
                    EngineErrorCode.BuildNotRun,
                    $"Quick build compiled zero projects. Scope '{scope}' with scopeName '{scopeName}' resolved to nothing -- no compile verdict is available. Call ListAll(kind: \"all\") to see valid project/file names."));
        }

        var errors = summary!.Details.Where(d => d.Severity == "Error").ToList();
        var warnings = summary!.Details.Where(d => d.Severity == "Warning").ToList();
        const int SummaryTopN = 50;

        return new EngineResultWrapper<BuildResult>(EngineOutcome.Success, new BuildResult(
            Outcome: summary.Errors == 0 ? BuildOutcome.Succeeded : BuildOutcome.Failed,
            Level: BuildVerifyLevel.quickBuild,
            ProjectsCompiled: projectsCompiled,
            DiagnosticsComplete: true,
            ErrorCount: summary.Errors,
            WarningCount: summary.Warnings,
            Errors: errors,
            Warnings: warnings,
            ErrorSummary: errors.GroupBySeverity(SummaryTopN),
            WarningSummary: warnings.GroupBySeverity(SummaryTopN),
            StdoutTail: null,
            StderrTail: null,
            Duration: DateTime.UtcNow - start
        ));
    }

    private static readonly Regex DiagnosticLineRegex = new(
        @"^(?<path>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<id>[A-Za-z0-9]+):\s*(?<message>.+?)\s*\[.+\]$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    public async Task<EngineResultWrapper<BuildResult>> RunFullBuildAsync(CancellationToken cancellationToken = default, int maxDetails = 50)
    {
        var start = DateTime.UtcNow;
        var solutionPath = _workspaceManager.CurrentSolution?.FilePath ?? _workspaceManager.SolutionPath;
        if (string.IsNullOrEmpty(solutionPath))
        {
            return new EngineResultWrapper<BuildResult>(EngineOutcome.InvalidInput,
                error: new EngineError("No solution is loaded. Call LoadSolution before running a full build."));
        }

        var projectNames = _workspaceManager.CurrentSolution?.Projects.Select(p => p.Name).ToList() ?? [];
        if (projectNames.Count == 0)
        {
            return EngineResultWrapper<BuildResult>.Failure(
                EngineOutcome.InvalidInput,
                new EngineError(
                    EngineErrorCode.BuildNotRun,
                    "Full build compiled zero projects. The loaded solution contains no projects -- no compile verdict is available. Call LoadSolution with a populated .slnx/.sln, or ListAll(kind: \"all\") to inspect the current workspace."));
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(solutionPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("build");
        process.StartInfo.ArgumentList.Add(solutionPath);
        process.StartInfo.ArgumentList.Add("--nologo");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("quiet");

        // MSBuildLocator.RegisterDefaults() (see PersistentWorkspaceManager) pins this process's
        // environment to a specific MSBuild toolset. Strip the pin from the spawned "dotnet build"
        // child so it resolves its own toolset independently, rather than inheriting ours.
        process.StartInfo.EnvironmentVariables.Remove("MSBUILD_EXE_PATH");
        process.StartInfo.EnvironmentVariables.Remove("MSBuildExtensionsPath");
        process.StartInfo.EnvironmentVariables.Remove("MSBuildSDKsPath");
        process.StartInfo.EnvironmentVariables.Remove("MSBuildLoadMicrosoftTargetsReadOnly");
        process.StartInfo.EnvironmentVariables.Remove("DOTNET_HOST_PATH");
        process.StartInfo.EnvironmentVariables.Remove("DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR");
        process.StartInfo.EnvironmentVariables.Remove("DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR");

        process.Start();

        // Process.WaitForExitAsync alongside BeginOutputReadLine/BeginErrorReadLine can hang
        // indefinitely even after the child has exited: confirmed live via attached debugger that a
        // "dotnet build" child had fully exited (no such process remained) while WaitForExitAsync was
        // still stuck awaiting pipe EOF -> a grandchild (e.g. an MSBuild worker node) can inherit and
        // hold the redirected stdout/stderr handles open. Reading the streams directly with
        // ReadToEndAsync -> instead of the event-based BeginOutputReadLine/BeginErrorReadLine -> and
        // bounding the whole thing with a timeout that kills the process tree closes both the hang
        // and its blast radius.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
        var exitTask = process.WaitForExitAsync(linkedCts.Token);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, exitTask);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            return new EngineResultWrapper<BuildResult>(EngineOutcome.Failure,
                error: new EngineError("The full build timed out after 5 minutes and the build process was terminated."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw;
        }

        var stdoutText = stdoutTask.Result;
        var stderrText = stderrTask.Result;

        var errors = new List<DiagnosticInfo>();
        var warnings = new List<DiagnosticInfo>();
        foreach (Match m in DiagnosticLineRegex.Matches(stdoutText))
        {
            var severity = m.Groups["severity"].Value == "error" ? "Error" : "Warning";
            var lineNum = int.Parse(m.Groups["line"].Value);
            var colNum = int.Parse(m.Groups["col"].Value);
            var info = new DiagnosticInfo(
                m.Groups["id"].Value,
                severity,
                m.Groups["message"].Value,
                m.Groups["path"].Value,
                lineNum,
                colNum,
                lineNum,
                colNum
            );
            (severity == "Error" ? errors : warnings).Add(info);
        }

        string? detail = null;
        if (stderrText.Contains("MSB3027") || stdoutText.Contains("MSB3027") ||
            stderrText.Contains("MSB3021") || stdoutText.Contains("MSB3021"))
        {
            detail = "Build failed to copy the output file - it is likely locked by a running process (e.g. this MCP server or an IDE holding the binary). Close the process holding the file and retry.";
        }

        const int TailLines = 40;
        static string Tail(string text) => string.Join(Environment.NewLine, text.Split(Environment.NewLine).TakeLast(TailLines));

        const int SummaryTopN = 50;
        return new EngineResultWrapper<BuildResult>(EngineOutcome.Success, new BuildResult(
            Outcome: process.ExitCode == 0 ? BuildOutcome.Succeeded : BuildOutcome.Failed,
            Level: BuildVerifyLevel.fullBuild,
            ProjectsCompiled: projectNames,
            DiagnosticsComplete: true,
            ExitCode: process.ExitCode,
            ErrorCount: errors.Count,
            WarningCount: warnings.Count,
            // Capped by maxDetails, same as RunQuickBuildAsync -> the raw per-diagnostic lists
            // previously shipped uncapped regardless of the caller's maxDetails value (a confirmed
            // 2,160-error / ~880KB response, since Build's own maxDetails parameter was never
            // forwarded to this method at all). ErrorSummary/WarningSummary below remain the
            // uncapped grouped-by-Id view for spotting one cause behind many errors.
            Errors: errors.Take(maxDetails).ToList(),
            Warnings: warnings.Take(maxDetails).ToList(),
            ErrorSummary: errors.GroupBySeverity(SummaryTopN),
            WarningSummary: warnings.GroupBySeverity(SummaryTopN),
            StdoutTail: Tail(stdoutText),
            StderrTail: string.IsNullOrWhiteSpace(stderrText) ? null : Tail(stderrText),
            Duration: DateTime.UtcNow - start,
            Detail: detail
        ));
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
