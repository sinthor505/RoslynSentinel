using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Reads the project file paths a solution names straight from the file on disk, so a caller can inspect them (for example
/// for restore state) before the workspace has loaded anything. Handles .sln (<c>Project("{...}") = "Name", "rel\path.csproj", "{...}"</c>),
/// .slnx (<c>&lt;Project Path="..." /&gt;</c> elements at any nesting) and a single project file. Never throws: a file that cannot
/// be read or parsed yields an empty list.
/// </summary>
public static class SolutionProjectPaths
{
    private static readonly string[] ProjectExtensions = [".csproj", ".vbproj", ".fsproj"];

    private static readonly Regex SlnProjectLine = new(
        "^\\s*Project\\(\"\\{[^}]*\\}\"\\)\\s*=\\s*\"[^\"]*\"\\s*,\\s*\"(?<path>[^\"]*)\"\\s*,",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Full paths of the existing project files that <paramref name="solutionOrProjectPath"/> names. A project file path returns
    /// itself; solution folders, non-project entries and missing files are skipped; results are distinct (case-insensitive).
    /// </summary>
    public static IReadOnlyList<string> Read(string solutionOrProjectPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(solutionOrProjectPath);
            var extension = Path.GetExtension(fullPath);
            if (IsProjectFile(fullPath))
            {
                return File.Exists(fullPath) ? [fullPath] : [];
            }

            if (!File.Exists(fullPath) || Path.GetDirectoryName(fullPath) is not { } solutionDir)
            {
                return [];
            }

            var relativePaths = extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                ? ReadSlnxPaths(fullPath)
                : extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ? ReadSlnPaths(fullPath) : [];

            return relativePaths
                .Where(IsProjectFile)
                .Select(relative => Path.GetFullPath(Path.Combine(solutionDir, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private static bool IsProjectFile(string path)
        => ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> ReadSlnPaths(string slnPath)
        => SlnProjectLine.Matches(File.ReadAllText(slnPath)).Select(m => m.Groups["path"].Value);

    private static IEnumerable<string> ReadSlnxPaths(string slnxPath)
        => XDocument.Load(slnxPath).Descendants()
            .Where(e => e.Name.LocalName == "Project")
            .Select(e => e.Attribute("Path")?.Value)
            .OfType<string>()
            .ToList();
}
