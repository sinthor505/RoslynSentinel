using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

/// <summary>A named set of production-file replacements (relative path to text) overlaid on a run directory to test model-written tests.</summary>
internal sealed record ScenarioMutant(string Name, IReadOnlyDictionary<string, string> Files);

/// <summary>The test counts for one mutant; Killed = failed tests, or a build break against the model's tests (said in Note).</summary>
internal sealed record MutantResult(string Name, int Passed, int Failed, bool Killed, string Note);

/// <summary>Mutation grading for scenarios where the model writes the tests.</summary>
internal static class MutationGrader
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "TestResults" };

    /// <summary>
    /// Runs each mutant on its own copy of <paramref name="solutionDirectory"/> (under the scratch root, outside the repo), one after
    /// another so only one dotnet test runs at a time. A mutant that does not compile against the tests counts as killed.
    /// </summary>
    public static async Task<IReadOnlyList<MutantResult>> RunAsync(
        string solutionDirectory,
        string testProjectRelativePath,
        string testFilter,
        IReadOnlyList<ScenarioMutant> mutants,
        CancellationToken cancellationToken)
    {
        var results = new List<MutantResult>();
        foreach (var mutant in mutants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = Path.Combine(ScratchRoot.Resolve(), "MutationGrader", Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectory(solutionDirectory, copy);
                foreach (var (relativePath, text) in mutant.Files)
                {
                    var target = Path.Combine(copy, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllTextAsync(target, ImplementationScenario.NormalizeToCrlf(text), cancellationToken);
                }

                var testProject = Path.Combine(copy, testProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    var run = await DotnetTestRunner.RunAsync(testProject, cancellationToken, testFilter);
                    results.Add(new MutantResult(mutant.Name, run.Passed, run.Failed, run.Failed > 0, string.Empty));
                }
                catch (InvalidOperationException)
                {
                    // DotnetTestRunner throws when no summary line exists: the mutant broke the build before any test ran.
                    results.Add(new MutantResult(mutant.Name, 0, 0, true, "mutant did not compile against the tests"));
                }
            }
            finally
            {
                DeleteQuietly(copy);
            }
        }

        return results;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (!SkippedFolders.Contains(name))
            {
                CopyDirectory(directory, Path.Combine(destination, name));
            }
        }
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file (a lingering test host) leaves a scratch copy behind; that is harmless and must not hide the mutant result.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: scratch leftover only.
        }
    }
}
