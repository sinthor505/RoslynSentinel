using System.Diagnostics;
using System.Text.RegularExpressions;

using RoslynSentinel.Common;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Engines.Basic;

public class BuildEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly ILogger<BuildEngine> _logger;

    public BuildEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _diagnosticEngine = new DiagnosticEngine(workspaceManager);
        _logger = new NullLogger<BuildEngine>();
    }
    public BuildEngine(IWorkspaceManager workspaceManager, ILogger<BuildEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _diagnosticEngine = new DiagnosticEngine(workspaceManager);
        _logger = logger;
    }
    public BuildEngine(IWorkspaceManager workspaceManager, DiagnosticEngine diagnosticEngine)
    {
        _workspaceManager = workspaceManager;
        _diagnosticEngine = diagnosticEngine;
        _logger = new NullLogger<BuildEngine>();
    }
    public BuildEngine(IWorkspaceManager workspaceManager, DiagnosticEngine diagnosticEngine, ILogger<BuildEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _diagnosticEngine = diagnosticEngine;
        _logger = logger;
    }

    /// <summary>Quick (in-memory) build of the scope: diagnostics only, no dotnet build.</summary>
    /// <param name="isCallable">Tool-callability predicate for the active surface; null means every tool is callable (full surface). Selects the listing tool and LoadSolution named in zero-project messages.</param>
    public async Task<EngineResultWrapper<BuildResult>> RunQuickBuildAsync(
        ToolScope scope, string? scopeName, int maxDetails, CancellationToken cancellationToken = default, Func<string, bool>? isCallable = null)
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
            var solutionForFile = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
            var solutionForProject = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            projectsCompiled = solutionForProject.Projects.Any(p => p.Name == scopeName) ? [scopeName] : [];
        }
        else
        {
            var solutionResult = await _diagnosticEngine.GetSolutionDiagnosticsAsync(int.MaxValue, cancellationToken);
            if (!solutionResult.TryGetData(out summary))
            {
                return new EngineResultWrapper<BuildResult>(solutionResult.Outcome, error: solutionResult.Error);
            }
            var solutionForAll = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
                    $"Quick build compiled zero projects. Scope '{scope}' with scopeName '{scopeName}' resolved to nothing -- no compile verdict is available.{ListingSentence(ListingCall(isCallable), "to see valid project/file names.")}"));
        }

        // The engine returns every diagnostic: solution scope fetched the uncapped list (int.MaxValue) and
        // BuildResultProjector caps warnings by maxDetails; the result marshaller pages errors.
        // TODO maxDetails is no longer used by RunQuickBuildAsync/RunFullBuildAsync; drop the parameter and its callers' arguments.
        var errors = summary!.Details.Where(d => d.Severity == "Error").ToList();
        var warnings = summary.Details.Where(d => d.Severity == "Warning").ToList();
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
        @"^(?<path>.+?)(?:\((?<line>\d+),(?<col>\d+)\))?\s*:\s*(?<severity>error|warning)\s+(?<id>[A-Za-z0-9]+):\s*(?<message>.+?)\s*\[(?<project>[^\]]+?\.[A-Za-z]+proj)(?:::[^\]]*)?\]\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Parses MSBuild/dotnet build stdout into error and warning lists. Handles compiler lines
    /// (<c>path(line,col): error CS0000: msg [proj]</c>) and project-level lines with no
    /// position (<c>X.csproj : error NU1101: msg [X.csproj]</c>); a multi-target suffix
    /// (<c>[X.csproj::TargetFramework=net9.0]</c>) is accepted. Line/column default to 0 when absent;
    /// <see cref="DiagnosticInfo.Project"/> is the project file name without extension.
    /// </summary>
    public static (List<DiagnosticInfo> Errors, List<DiagnosticInfo> Warnings) ParseDiagnostics(string stdout)
    {
        var errors = new List<DiagnosticInfo>();
        var warnings = new List<DiagnosticInfo>();
        // Quiet verbosity prints each diagnostic inline and again in the "Build FAILED." summary,
        // so the same line is matched twice. Project is part of the key because the same file
        // can build in two projects. Path is lowered so the comparison is ordinal-ignore-case.
        var seen = new HashSet<(string Id, string Severity, string Path, int Line, int Col, string Message, string Project)>();
        foreach (Match m in DiagnosticLineRegex.Matches(stdout))
        {
            var severity = m.Groups["severity"].Value == "error" ? "Error" : "Warning";
            var lineNum = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : 0;
            var colNum = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 0;
            var project = ProjectNameFromPath(m.Groups["project"].Value);
            var id = m.Groups["id"].Value;
            var path = m.Groups["path"].Value;
            var message = m.Groups["message"].Value;
            if (!seen.Add((id, severity, path.ToLowerInvariant(), lineNum, colNum, message, project)))
            {
                continue;
            }
            var info = new DiagnosticInfo(
                id,
                severity,
                message,
                path,
                lineNum,
                colNum,
                lineNum,
                colNum,
                Project: project
            );
            (severity == "Error" ? errors : warnings).Add(info);
        }

        return (errors, warnings);
    }

    // Path.GetFileNameWithoutExtension treats only the host's separators; MSBuild output may carry either.
    private static string ProjectNameFromPath(string projectPath)
    {
        var fileName = projectPath[(projectPath.LastIndexOfAny(['\\', '/']) + 1)..];
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    /// <summary>Prefix of <see cref="BuildResult.Detail"/> for a throwaway build that hit no problem; the tool
    /// layer treats a detail that is only this note as clean (it drops the stdout/stderr tails).</summary>
    public const string ThrowawayDetailPrefix = "fullBuildThrowaway: output went to ";

    /// <summary>Names the listing tool for a zero-project message: ListAll when callable, else ListSolutionItems, else null.</summary>
    private static string? ListingCall(Func<string, bool>? isCallable)
    {
        if (isCallable is null || isCallable("ListAll"))
        {
            return ToolCallHint.Call("ListAll", ("kind", "all"));
        }

        return isCallable("ListSolutionItems") ? ToolCallHint.Call("ListSolutionItems", ("kind", "all")) : null;
    }

    /// <summary>Returns " Call {listing} {tail}", or an empty string when no listing tool is callable.</summary>
    private static string ListingSentence(string? listing, string tail) =>
        listing is null ? string.Empty : " Call " + listing + " " + tail;

    /// <summary>Zero-project full build message; names LoadSolution and the listing tool only when they are callable.</summary>
    private static string NoProjectsMessage(Func<string, bool>? isCallable)
    {
        const string Head = "Full build compiled zero projects. The loaded solution contains no projects -- no compile verdict is available.";
        var listing = ListingCall(isCallable);
        var loadSolutionCallable = isCallable is null || isCallable("LoadSolution");
        if (loadSolutionCallable && listing is not null)
        {
            return Head + " Call " + ToolCallHint.Call("LoadSolution") + " with a populated .slnx/.sln, or " + listing + " to inspect the current workspace.";
        }

        if (loadSolutionCallable)
        {
            return Head + " Call " + ToolCallHint.Call("LoadSolution") + " with a populated .slnx/.sln.";
        }

        if (listing is not null)
        {
            return Head + " Call " + listing + " to inspect the current workspace.";
        }

        return Head;
    }

    /// <summary>Runs `dotnet build` on the whole solution (scope=solution, default), only the scoped project and its project references (scope=project), or the file's containing project and its references (scope=file). <paramref name="level"/> = fullBuild writes the projects'
    /// own bin/obj (and keeps the competing-build probe); fullBuildThrowaway writes to
    /// .roslynsentinel/build/&lt;instance&gt;/ and skips the probe. <paramref name="useScratchDir"/> is obsolete:
    /// true is the same as level = fullBuildThrowaway. Both levels hold the server-wide <see cref="BuildGate"/>.</summary>
    /// <param name="isCallable">Tool-callability predicate for the active surface; null means every tool is callable (full surface). Selects the listing tool and LoadSolution named in zero-project messages.</param>
    public async Task<EngineResultWrapper<BuildResult>> RunFullBuildAsync(CancellationToken cancellationToken = default, int maxDetails = 50, bool useScratchDir = false,
        BuildVerifyLevel level = BuildVerifyLevel.fullBuild, ToolScope scope = ToolScope.solution, string? scopeName = null, Func<string, bool>? isCallable = null)
    {
        var throwaway = useScratchDir || level == BuildVerifyLevel.fullBuildThrowaway;
        var usedLevel = throwaway ? BuildVerifyLevel.fullBuildThrowaway : BuildVerifyLevel.fullBuild;
        var start = DateTime.UtcNow;
        var currentSolution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var solutionPath = currentSolution?.FilePath ?? _workspaceManager.SolutionPath;
        if (string.IsNullOrEmpty(solutionPath))
        {
            return new EngineResultWrapper<BuildResult>(EngineOutcome.InvalidInput,
                error: new EngineError(SolutionNotLoadedMessage.Build(_workspaceManager.LoadState) + " A full build needs a loaded solution."));
        }

        var projectNames = currentSolution?.Projects.Select(p => p.Name).ToList() ?? [];
        if (projectNames.Count == 0)
        {
            return EngineResultWrapper<BuildResult>.Failure(
                EngineOutcome.InvalidInput,
                new EngineError(
                    EngineErrorCode.BuildNotRun,
                    NoProjectsMessage(isCallable)));
        }

        string buildTargetPath = solutionPath;
        List<string> builtProjectNames = projectNames;
        List<string>? scopedProjectFilePaths = null;
        if (scope is ToolScope.project or ToolScope.file)
        {
            if (string.IsNullOrEmpty(scopeName))
            {
                return new EngineResultWrapper<BuildResult>(EngineOutcome.InvalidInput,
                    error: new EngineError(scope == ToolScope.project
                        ? "scopeName (projectName) is required when scope=project."
                        : "scopeName (filePath) is required when scope=file."));
            }

            Microsoft.CodeAnalysis.Project? scopedProject;
            if (scope == ToolScope.project)
            {
                scopedProject = ProjectLookup.FindProject(currentSolution!, scopeName, ignoreCase: true);
            }
            else
            {
                var documentId = currentSolution!.GetDocumentIdsWithFilePath(scopeName).FirstOrDefault();
                scopedProject = documentId is null ? null : currentSolution!.GetProject(documentId.ProjectId);
            }

            if (scopedProject is null || string.IsNullOrEmpty(scopedProject.FilePath))
            {
                return EngineResultWrapper<BuildResult>.Failure(
                    EngineOutcome.InvalidInput,
                    new EngineError(
                        EngineErrorCode.BuildNotRun,
                        $"Full build compiled zero projects. Scope '{scope}' with scopeName '{scopeName}' resolved to no project in the loaded solution -- no compile verdict is available.{ListingSentence(ListingCall(isCallable), "to see valid project/file names.")}"));
            }

            // Scoped build: the project plus every project it transitively references.
            buildTargetPath = scopedProject.FilePath;
            var scopedProjects = new List<Microsoft.CodeAnalysis.Project> { scopedProject };
            foreach (var dependencyId in currentSolution!.GetProjectDependencyGraph().GetProjectsThatThisProjectTransitivelyDependsOn(scopedProject.Id))
            {
                var dependency = currentSolution!.GetProject(dependencyId);
                if (dependency is not null)
                {
                    scopedProjects.Add(dependency);
                }
            }

            builtProjectNames = scopedProjects.Select(p => p.Name).ToList();
            scopedProjectFilePaths = scopedProjects.Select(p => p.FilePath).OfType<string>().ToList();
        }

        BuildGateLease gateLease;
        try
        {
            gateLease = await BuildGate.Shared.AcquireAsync($"Build ({usedLevel})", BuildGate.DefaultMaxWait, cancellationToken);
        }
        catch (BuildGateBusyException busy)
        {
            return EngineResultWrapper<BuildResult>.Failure(
                EngineOutcome.Failure, new EngineError(EngineErrorCode.BuildNotRun, busy.Message));
        }

        // Held until the method returns (every path below, including cancellation and the timeout kill).
        using var gate = gateLease;

        if (!throwaway)
        {
            // A throwaway build is isolated, so only a build writing to the repo's own obj/ and bin/ can collide.
            var targetPaths = scopedProjectFilePaths is null ? new List<string> { solutionPath } : new List<string>(scopedProjectFilePaths);
            if (scopedProjectFilePaths is null)
            {
                targetPaths.AddRange(currentSolution?.Projects.Select(p => p.FilePath).OfType<string>() ?? []);
            }
            var waitStart = DateTime.UtcNow;
            var competing = await BuildIsolation.WaitForNoCompetingBuildAsync(targetPaths, BuildIsolation.DefaultMaxWait, cancellationToken);
            if (competing.Count > 0)
            {
                return EngineResultWrapper<BuildResult>.Failure(
                    EngineOutcome.Failure,
                    new EngineError(EngineErrorCode.BuildNotRun, BuildIsolation.DescribeCompetingBuilds(competing, DateTime.UtcNow - waitStart)));
            }
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
        process.StartInfo.ArgumentList.Add(buildTargetPath);
        process.StartInfo.ArgumentList.Add("--nologo");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("quiet");
        // Without this, MSBuild's incremental "up-to-date" check can skip recompiling a project
        // entirely if its outputs are newer than its inputs by mtime (e.g. built moments earlier by
        // this same tool, an IDE, or CI) -- and a skipped project emits no diagnostics at all, not
        // zero diagnostics. That silently produced "0 Warning(s)" while DiagnosticsComplete stayed
        // true even though real CS0618/Obsolete warnings existed on disk. fullBuild's whole purpose
        // is an authoritative from-scratch compile, so always force one.
        process.StartInfo.ArgumentList.Add("--no-incremental");
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        string? artifactsPath = null;
        if (throwaway)
        {
            artifactsPath = BuildIsolation.ApplyArtifactsPath(process.StartInfo, solutionDirectory);
        }

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

        // A grandchild (e.g. an MSBuild worker node with /nodeReuse:true) can inherit and hold the redirected
        // stdout/stderr handles open after dotnet itself exited, so waiting for pipe EOF can last until the
        // timeout. ChildProcessCapture waits for process exit, then gives the reads a short drain grace.
        var capture = await ChildProcessCapture.RunAsync(process, TimeSpan.FromMinutes(5), cancellationToken);
        if (capture.TimedOut)
        {
            var exitState = capture.RootExited
                ? $"the build process had exited, exit code {capture.ExitCode}"
                : "the build process had not exited";
            return new EngineResultWrapper<BuildResult>(EngineOutcome.Failure,
                error: new EngineError($"The full build timed out after 5 minutes ({exitState}) and the process tree was terminated."));
        }

        var stdoutText = capture.StdOut;
        var stderrText = capture.StdErr;

        var (errors, warnings) = ParseDiagnostics(stdoutText);

        string? detail = null;
        if (stderrText.Contains("MSB3027") || stdoutText.Contains("MSB3027") ||
            stderrText.Contains("MSB3021") || stdoutText.Contains("MSB3021"))
        {
            detail = "Build failed to copy the output file - it is likely locked by a running process (e.g. this MCP server or an IDE holding the binary). Close the process holding the file and retry.";
        }

        if (capture.DrainTimedOut)
        {
            var drainNote = "The output streams were still held open by a child process after dotnet exited; the build output may be incomplete.";
            detail = detail is null ? drainNote : detail + " " + drainNote;
        }

        if (artifactsPath is not null)
        {
            var relativePath = Path.GetRelativePath(solutionDirectory, artifactsPath).Replace('\\', '/');
            var note = $"{ThrowawayDetailPrefix}{relativePath}/, not the projects' own bin/ and obj/ folders; the projects' bin/ was not updated (use level=fullBuild for that).";
            // Any problem text stays first so the tool layer can tell a clean throwaway build from a troubled one.
            detail = detail is null ? note : detail + " " + note;
        }

        const int TailLines = 40;
        static string Tail(string text) => string.Join(Environment.NewLine, text.Split(Environment.NewLine).TakeLast(TailLines));

        const int SummaryTopN = 50;
        return new EngineResultWrapper<BuildResult>(EngineOutcome.Success, new BuildResult(
            Outcome: capture.ExitCode == 0 ? BuildOutcome.Succeeded : BuildOutcome.Failed,
            Level: usedLevel,
            ProjectsCompiled: builtProjectNames,
            DiagnosticsComplete: true,
            ExitCode: capture.ExitCode!.Value,
            ErrorCount: errors.Count,
            WarningCount: warnings.Count,
            // Every diagnostic: BuildResultProjector caps warnings by maxDetails and the result marshaller
            // pages errors, so the engine no longer caps. ErrorSummary/WarningSummary below are the
            // grouped-by-Id view for spotting one cause behind many errors.
            Errors: errors,
            Warnings: warnings,
            ErrorSummary: errors.GroupBySeverity(SummaryTopN),
            WarningSummary: warnings.GroupBySeverity(SummaryTopN),
            StdoutTail: Tail(stdoutText),
            StderrTail: string.IsNullOrWhiteSpace(stderrText) ? null : Tail(stderrText),
            Duration: DateTime.UtcNow - start,
            Detail: detail
        ));
    }
}
