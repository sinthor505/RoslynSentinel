namespace RoslynSentinel.Tests;

public abstract class WorkingDirectoryLeakDetector
{
    private HashSet<string> _before = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records the .cs and .csproj files present in the working directory before the fixture's tests run.
    /// </summary>
    [OneTimeSetUp]
    public void SnapshotWorkingDirectory()
    {
        _before = new HashSet<string>(
            ListSourceFileNames(System.IO.Directory.GetCurrentDirectory()),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fails if a test left a new .cs or .csproj file in the working directory. Does not delete the files.
    /// </summary>
    [OneTimeTearDown]
    public void AssertNoSourceFilesLeaked()
    {
        var leaked = FindNewSourceFiles(_before, System.IO.Directory.GetCurrentDirectory());
        if (leaked.Count > 0)
        {
            Assert.Fail(
                "A test wrote a source file with a bare relative path into the working directory: "
                + string.Join(", ", leaked)
                + ". Use a unique temp dir and PathOf instead (see WorkingDirectoryLeakGuard).");
        }
    }

    /// <summary>
    /// Returns the top-level .cs and .csproj file names in <paramref name="directory"/> that are not in <paramref name="before"/>.
    /// </summary>
    public static List<string> FindNewSourceFiles(IReadOnlySet<string> before, string directory)
    {
        var result = new List<string>();
        foreach (var name in ListSourceFileNames(directory))
        {
            if (!before.Contains(name))
            {
                result.Add(name);
            }
        }
        return result;
    }

    private static List<string> ListSourceFileNames(string directory)
    {
        var names = new List<string>();
        foreach (var path in System.IO.Directory.GetFiles(directory, "*", System.IO.SearchOption.TopDirectoryOnly))
        {
            var extension = System.IO.Path.GetExtension(path);
            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(System.IO.Path.GetFileName(path));
            }
        }
        return names;
    }
}
