using Microsoft.CodeAnalysis;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The folder-derived namespace convention: a root namespace plus the folder path of a file under its project directory,
/// one segment per folder. The one implementation behind <see cref="SolutionStructureEngine"/>'s namespace/path check,
/// MoveFile's kept-namespace fact and the MoveFile copyAndObsolete checks.
/// </summary>
public static class FolderNamespace
{
    /// <summary>
    /// The namespace the folder convention gives <paramref name="file"/> in <paramref name="project"/>: the project's default
    /// namespace (the project name when it has none) plus the file's folder path under the project directory.
    /// Null when the project has no file path, so there is no project directory to measure the folder against.
    /// </summary>
    // Root choice: Project.DefaultNamespace (the evaluated RootNamespace). SolutionStructureEngine keeps its own csproj-XML root
    // (GetRootNamespace) and shares only FromFolder; the two agree unless a props file or an expression sets RootNamespace.
    public static string? Expected(Project project, FilePathWrapper file)
    {
        if (project.FilePath is not { } projectPath || Path.GetDirectoryName(projectPath) is not { } projectDirectory)
        {
            return null;
        }

        var root = string.IsNullOrEmpty(project.DefaultNamespace) ? project.Name : project.DefaultNamespace;
        return FromFolder(root, projectDirectory, file);
    }

    /// <summary>
    /// <paramref name="rootNamespace"/> plus the folder of <paramref name="file"/> relative to <paramref name="projectDirectory"/>,
    /// with each folder separator turned into a namespace separator; just <paramref name="rootNamespace"/> for a file directly in the project directory.
    /// </summary>
    public static string FromFolder(string rootNamespace, string projectDirectory, FilePathWrapper file)
    {
        var fileDirectory = Path.GetDirectoryName(file.Absolute) ?? projectDirectory;
        var relativeDirectory = Path.GetRelativePath(projectDirectory, fileDirectory);
        if (relativeDirectory == ".")
        {
            return rootNamespace;
        }

        var suffix = relativeDirectory.Replace('\\', '.').Replace('/', '.').Trim('.');
        return suffix.Length == 0 ? rootNamespace : rootNamespace + "." + suffix;
    }
}
