using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

// Deliberate copy of Tests.ModelEval's archiver; the host suffix comes from RoslynSentinel.Common.AgentLoop.EvalHostTable (owns the rule, reads scripts/eval-hosts.json).

/// <summary>
/// Copies a finished run's directory (transcripts, variants.csv) into the repo's
/// <c>ModelTestingResults\&lt;host-suffix&gt;\&lt;relative-path-under-model-eval&gt;\</c> tree, so
/// every replay is archived however the test was launched (script, <c>dotnet test</c> or the test explorer).
/// </summary>
internal static class ModelTestingResultsArchiver
{
    /// <summary>
    /// Call from the replay test's finally block with the run directory (<c>&lt;work-dir&gt;\model-eval\&lt;test&gt;\&lt;timestamp&gt;</c>).
    /// No-ops quietly (logging via <see cref="TestContext"/> rather than throwing) if the directory doesn't exist,
    /// since a failed archive copy must never mask or replace the test's own pass/fail outcome.
    /// </summary>
    public static void ArchiveRunDirectory(string runDirectory)
    {
        try
        {
            if (!Directory.Exists(runDirectory))
            {
                return;
            }

            WriteTestResult(runDirectory);

            var resultsBase = ResolveResultsBase();
            var suffix = EvalHostTable.DeriveSuffix(RoslynSentinel.Common.LlmOptions.BaseUrl);

            // runDirectory always looks like <work-dir>\model-eval\<...>\<timestamp>; keep everything after "model-eval\".
            var marker = Path.Combine("model-eval") + Path.DirectorySeparatorChar;
            var markerIndex = runDirectory.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var relative = markerIndex >= 0
                ? runDirectory[(markerIndex + marker.Length)..]
                : Path.GetFileName(runDirectory);

            var destination = Path.Combine(resultsBase, suffix, relative);
            if (Directory.Exists(destination))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyDirectoryRecursive(runDirectory, destination);
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"ModelTestingResultsArchiver: failed to archive '{runDirectory}': {ex.Message}");
        }
    }

    // Deviation: the original runs from TearDown, where the test's Result is final. Here it is called from the test body's
    // finally block, so the outcome written is the one so far (usually Inconclusive); the file still names the test that wrote the folder.
    private static void WriteTestResult(string runDirectory)
    {
        var result = TestContext.CurrentContext.Result;
        var lines = new List<string>
        {
            $"Outcome: {result.Outcome}",
            $"Test: {TestContext.CurrentContext.Test.FullName}",
        };

        if (!string.IsNullOrEmpty(result.Message))
        {
            lines.Add("");
            lines.Add("Message:");
            lines.Add(result.Message);
        }

        if (!string.IsNullOrEmpty(result.StackTrace))
        {
            lines.Add("");
            lines.Add("StackTrace:");
            lines.Add(result.StackTrace);
        }

        File.WriteAllLines(Path.Combine(runDirectory, "test-result.txt"), lines);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    // ROSLYNSENTINEL_RESULTS_ROOT (absolute) redirects the results tree, e.g. for queue jobs that run from a throwaway checkout; unset or relative -> <repo root>\ModelTestingResults as before.
    private static string ResolveResultsBase()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("ROSLYNSENTINEL_RESULTS_ROOT");
        return !string.IsNullOrWhiteSpace(overrideRoot) && Path.IsPathRooted(overrideRoot)
            ? overrideRoot
            : Path.Combine(FindRepoRoot(), "ModelTestingResults");
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
            $"ModelTestingResultsArchiver could not locate the repo root (RoslynSentinel.slnx) walking up from '{AppContext.BaseDirectory}'.");
    }
}
