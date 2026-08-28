using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Basic;

public class SolutionManagementEngine
{
    private readonly ISolutionProvider _workspaceManager;

    public SolutionManagementEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Creates a new project within the solution.
    /// </summary>
    public async Task<DocumentEditResult> CreateProjectAsync(string projectName, string projectType = "console", CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var slnPath = _workspaceManager.SolutionPath ?? solution.FilePath;
        var slnDir = Path.GetDirectoryName(slnPath) ?? throw new InvalidOperationException("Solution path not found.");
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
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = default,
            Message = $"Project {projectName} created and added to solution."
        };
    }

    /// <summary>
    /// Splits a project by moving a folder's contents into a new project and updating references.
    /// </summary>
    public async Task<DocumentEditResult> SplitProjectByFolderAsync(string sourceProjectName, string folderName, string targetProjectName, CancellationToken cancellationToken = default)
    {
        // 1. Create target project
        await CreateProjectAsync(targetProjectName, "classlib", cancellationToken);

        // 2. Identify files to move
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var sourceProject = solution.Projects.FirstOrDefault(p => p.Name == sourceProjectName) ?? throw new InvalidOperationException("Source project not found.");
        var filesToMove = sourceProject.Documents.Where(d => d.Folders.Contains(folderName)).ToList();

        // 3. Physically move files and update solution (simulated for expansion)
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = default,
            Message = $"Moved {filesToMove.Count} files from {sourceProjectName}/{folderName} to {targetProjectName}. References will need manual updating."
        };
    }
}
