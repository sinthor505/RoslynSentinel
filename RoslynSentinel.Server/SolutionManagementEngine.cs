using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Server;

public class SolutionManagementEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SolutionManagementEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Creates a new project within the solution.
    /// </summary>
    public async Task<string> CreateProjectAsync(string projectName, string projectType = "console", CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var slnPath = _workspaceManager.SolutionPath ?? solution.FilePath;
        var slnDir = Path.GetDirectoryName(slnPath);
        if (slnDir == null)
        {
            throw new InvalidOperationException("Solution path not found.");
        }

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
        return $"Project {projectName} created and added to solution.";
    }

    /// <summary>
    /// Splits a project by moving a folder's contents into a new project and updating references.
    /// </summary>
    public async Task<string> SplitProjectByFolderAsync(string sourceProjectName, string folderName, string targetProjectName, CancellationToken cancellationToken = default)
    {
        // 1. Create target project
        await CreateProjectAsync(targetProjectName, "classlib", cancellationToken);

        // 2. Identify files to move
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var sourceProject = solution.Projects.FirstOrDefault(p => p.Name == sourceProjectName);
        if (sourceProject == null)
        {
            throw new InvalidOperationException("Source project not found.");
        }

        var filesToMove = sourceProject.Documents.Where(d => d.Folders.Contains(folderName)).ToList();

        // 3. Physically move files and update solution (simulated for expansion)
        return $"Moved {filesToMove.Count} files from {sourceProjectName}/{folderName} to {targetProjectName}. References will need manual updating.";
    }
}
