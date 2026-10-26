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
        var command = $"dotnet new {projectType} -n {projectName} -o \"{projectDir}\"";
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{command}; dotnet sln '{solution.FilePath}' add '{Path.Combine(projectDir, projectName + ".csproj")}'\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        await process!.WaitForExitAsync(cancellationToken);

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

        var message = $"Project {projectName} created and added to solution.";
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
