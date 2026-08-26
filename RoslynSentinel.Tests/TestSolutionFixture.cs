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

    /// <summary>
    /// Writes a new file directly to disk (relative to <see cref="SolutionDirectory"/>), then, by
    /// default, reloads the solution and acknowledges the resulting external-change entry. Bypasses
    /// <c>ApplyProposedChangesAsync</c> on purpose — for tests that need a file present before a
    /// tool call without going through the normal propose/apply path (e.g. seeding a fixture's
    /// starting state, or reproducing a scenario the workspace didn't itself write). Pass
    /// <paramref name="reloadSolution"/> = false to defer the reload/drift-clear — e.g. when the
    /// caller has more out-of-band writes to make first and will reload/clear once at the end.
    /// </summary>
    public async Task AddFileToSolution(IWorkspaceManager workspaceManager, string relativePath, string content, bool reloadSolution = true, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(SolutionDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);

        if (reloadSolution)
        {
            await workspaceManager.LoadSolutionAsync(SolutionPath, cancellationToken);
            workspaceManager.ClearExternalFileChanges();
        }
    }

    /// <summary>
    /// Overwrites an existing file directly on disk (relative to <see cref="SolutionDirectory"/>),
    /// then, by default, reloads the solution and acknowledges the resulting external-change entry.
    /// Same bypass-the-apply-path rationale as <see cref="AddFileToSolution"/>, including the
    /// <paramref name="reloadSolution"/> = false escape hatch for batching multiple writes.
    /// </summary>
    public async Task ModifyFileInSolution(IWorkspaceManager workspaceManager, string relativePath, string content, bool reloadSolution = true, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(SolutionDirectory, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"ModifyFileInSolution: '{relativePath}' does not exist under '{SolutionDirectory}'. Use AddFileToSolution for a new file.", fullPath);
        }

        await File.WriteAllTextAsync(fullPath, content, cancellationToken);

        if (reloadSolution)
        {
            await workspaceManager.LoadSolutionAsync(SolutionPath, cancellationToken);
            workspaceManager.ClearExternalFileChanges();
        }
    }

    /// <summary>
    /// Deletes an existing file directly from disk (relative to <see cref="SolutionDirectory"/>),
    /// then, by default, reloads the solution and acknowledges the resulting external-change entry.
    /// Same bypass-the-apply-path rationale as <see cref="AddFileToSolution"/>, including the
    /// <paramref name="reloadSolution"/> = false escape hatch for batching multiple writes.
    /// </summary>
    public async Task DeleteFileFromSolution(IWorkspaceManager workspaceManager, string relativePath, bool reloadSolution = true, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(SolutionDirectory, relativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"DeleteFileFromSolution: '{relativePath}' does not exist under '{SolutionDirectory}'.", fullPath);
        }

        File.Delete(fullPath);

        if (reloadSolution)
        {
            await workspaceManager.LoadSolutionAsync(SolutionPath, cancellationToken);
            workspaceManager.ClearExternalFileChanges();
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
