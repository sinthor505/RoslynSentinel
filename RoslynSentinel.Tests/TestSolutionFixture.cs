namespace RoslynSentinel.Tests;

/// <summary>
/// Real-disk fixture for tests that need <see cref="PersistentWorkspaceManager.LoadSolutionAsync(string, System.Threading.CancellationToken)"/>
/// to succeed against an actual MSBuild-openable solution (FakeWorkspaceManager only covers the
/// pure in-memory path via SetTestSolution). Copies the repo's Samples/ContosoOrders scenario
/// project into a fresh temp directory per instance so tests can load/watch/write real files
/// without touching the checked-in sample or colliding with other tests. Dispose() deletes the copy.
/// </summary>
public sealed class TestSolutionFixture : IDisposable
{
    private static readonly string[] SourceExtensions = [".sln", ".csproj", ".cs", ".md"];

    public string SolutionPath { get; }

    public string SolutionDirectory { get; }

    public TestSolutionFixture()
    {
        var sampleRoot = Path.Combine(FindRepoRoot(), "Samples", "ContosoOrders");
        if (!Directory.Exists(sampleRoot))
        {
            throw new DirectoryNotFoundException(
                $"TestSolutionFixture could not find the ContosoOrders sample at '{sampleRoot}'.");
        }

        SolutionDirectory = Path.Combine(Path.GetTempPath(), "RoslynSentinelTests_" + Guid.NewGuid());
        Directory.CreateDirectory(SolutionDirectory);

        CopySourceFiles(sampleRoot, SolutionDirectory);

        SolutionPath = Path.Combine(SolutionDirectory, "ContosoOrders.sln");
        if (!File.Exists(SolutionPath))
        {
            throw new FileNotFoundException(
                $"TestSolutionFixture copy did not produce a .sln at '{SolutionPath}'.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(SolutionDirectory))
        {
            Directory.Delete(SolutionDirectory, recursive: true);
        }
    }

    private static void CopySourceFiles(string sourceRoot, string destinationRoot)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (!SourceExtensions.Contains(Path.GetExtension(sourceFile), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is ".vs" or "bin" or "obj"))
            {
                continue;
            }

            var destinationFile = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"TestSolutionFixture could not locate the repo root (RoslynSentinel.slnx) walking up from '{AppContext.BaseDirectory}'.");
    }
}
