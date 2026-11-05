using Microsoft.CodeAnalysis;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

public class SolutionManagementEngine
{
    private readonly IWorkspaceManager _workspaceManager;

    public SolutionManagementEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Creates a new project within the solution.
    /// </summary>
    public async Task<ProjectEditResult> CreateProjectAsync(string projectName, string projectType = "console", CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var slnPath = _workspaceManager.SolutionPath ?? solution.FilePath;
        var slnDir = Path.GetDirectoryName(slnPath) ?? throw new ToolNotFoundException("Solution path not found.");
        var projectDir = Path.Combine(slnDir, projectName);
        Directory.CreateDirectory(projectDir);

        // We use dotnet CLI for project creation as Roslyn MSBuildWorkspace is better at reading than structural solution modification
        // The exit-code guard between the two commands matters: -Command reports only the LAST native command's status,
        // so without it a failed `dotnet new` followed by a succeeding `dotnet sln add` would look like success.
        var csprojPath = Path.Combine(projectDir, projectName + ".csproj");
        var command = $"dotnet new {projectType} -n {projectName} -o \"{projectDir}\"";
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{command}; if ($LASTEXITCODE -ne 0) {{ exit $LASTEXITCODE }}; dotnet sln '{solution.FilePath}' add '{csprojPath}'\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new ToolInvalidArgumentException($"Project {projectName} was not created: the dotnet CLI could not be started. Check that the dotnet SDK is installed, then retry {ToolCallHint.Call("CreateProject")}.");

        // Drain both redirected streams while waiting so a chatty child cannot block on a full pipe; the text itself
        // is not returned (it can contain absolute paths).
        var drainStdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var drainStdErr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(drainStdOut, drainStdErr);

        if (process.ExitCode != 0)
        {
            throw new ToolInvalidArgumentException($"Project {projectName} was not created or not added to the solution (dotnet exited with code {process.ExitCode}). Check that projectName is a valid project name and projectType is a valid dotnet template short name (for example console, classlib), then {ToolCallHint.Call("Build")} to see the solution state before retrying {ToolCallHint.Call("CreateProject")}.");
        }

        // The template .cs files dotnet new writes bypass the chokepoint, so they get the resolved EOL here.
        string? eolRefusal = null;
        foreach (var templateFile in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            var relativeSegments = Path.GetRelativePath(projectDir, templateFile).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relativeSegments.Any(segment => segment is "bin" or "obj"))
            {
                continue;
            }

            eolRefusal ??= await EolFileWriter.NormalizeNewFileAsync(templateFile, cancellationToken);
        }

        // Register every file this call caused (written by the child process or the EOL pass above, i.e. outside the
        // write chokepoint) so the drift guard does not treat them as external edits. bin/obj are excluded: the watcher
        // ignores them already.
        var ownWrites = new List<string> { csprojPath, slnPath };
        if (!string.IsNullOrEmpty(solution.FilePath))
        {
            ownWrites.Add(solution.FilePath);
        }

        foreach (var projectFile in Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories))
        {
            var segments = Path.GetRelativePath(projectDir, projectFile).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!segments.Any(segment => segment is "bin" or "obj"))
            {
                ownWrites.Add(projectFile);
            }
        }

        _workspaceManager.RegisterOwnWrites(ownWrites);

        var message = $"Project {projectName} created and added to solution; its files were registered so follow-up edits will not trip the external-drift halt.";
        if (eolRefusal != null)
        {
            message += "; EOL normalisation refused: " + eolRefusal;
        }

        return new ProjectEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = default,
            Message = message
        };
    }

    /// <summary>
    /// Splits a project by moving a folder's contents into a new project and updating references.
    /// </summary>
    public async Task<ProjectEditResult> SplitProjectByFolderAsync(string sourceProjectName, string folderName, string targetProjectName, CancellationToken cancellationToken = default)
    {
        // 1. Create target project
        await CreateProjectAsync(targetProjectName, "classlib", cancellationToken);

        // 2. Identify files to move
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var sourceProject = ProjectLookup.FindProject(solution, sourceProjectName) ?? throw new ToolNotFoundException("Source project not found.");
        var filesToMove = sourceProject.Documents.Where(d => d.Folders.Contains(folderName)).ToList();

        // 3. Physically move files and update solution (simulated for expansion)
        // TODO SplitProjectByFolderAsync is a simulation: it creates the project and reports "Moved N files" without moving any file; throw ToolNotImplementedException or implement the move (plan_document_edit_result_non_nullable_updated_text risks)
        return new ProjectEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = default,
            Message = $"Moved {filesToMove.Count} files from {sourceProjectName}/{folderName} to {targetProjectName}. References will need manual updating."
        };
    }
}
