using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

public static class SolutionProjectLocator
{
    // Longest-prefix match: returns the project whose .csproj directory is the deepest
    // ancestor of filePath, or null if no project contains it. A directory matches only on a
    // path-separator boundary ('\' or '/'), so 'Foo' does not contain 'FooBar\x.cs'.
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

            var dirPrefix = projectDir.TrimEnd('\\', '/');
            var isInside = string.Equals(filePath, projectDir, StringComparison.OrdinalIgnoreCase) ||
                (filePath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase) &&
                 filePath.Length > dirPrefix.Length &&
                 (filePath[dirPrefix.Length] == '\\' || filePath[dirPrefix.Length] == '/'));

            if (isInside && projectDir.Length > bestLen)
            {
                best = project;
                bestLen = projectDir.Length;
            }
        }

        return best;
    }
}
