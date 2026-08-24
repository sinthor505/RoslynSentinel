using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

public static class SolutionProjectLocator
{
    // Longest-prefix match: returns the project whose .csproj directory is the deepest
    // ancestor of filePath, or null if no project contains it.
    public static Project? FindContainingProject(Solution solution, string filePath)
    {
        Project? best = null;
        int bestLen = -1;

        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null)
            {
                continue;
            }

            var projectDir = Path.GetDirectoryName(project.FilePath);
            if (projectDir == null)
            {
                continue;
            }

            if (filePath.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase) &&
                projectDir.Length > bestLen)
            {
                best = project;
                bestLen = projectDir.Length;
            }
        }

        return best;
    }
}
