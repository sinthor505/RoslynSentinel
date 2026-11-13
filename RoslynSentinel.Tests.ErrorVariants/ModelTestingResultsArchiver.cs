using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

// Deliberate copy of RoslynSentinel.Tests.ModelEval/ModelTestingResultsArchiver.cs: this project may not reference ModelEval,
// and the archiver is a test-project helper, not library code, so it does not belong in Common.
// Differences from the original: namespace, and DeriveHostSuffix also maps 192.168.10.85 to "85"
// (the alias roslynsentinel-modeleval.ps1 has for that host) so runs on it land in ModelTestingResults\85\.
// TODO unify with Tests.ModelEval/ModelTestingResultsArchiver.cs once a shared home exists

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

            var repoRoot = FindRepoRoot();
            var suffix = DeriveHostSuffix(RoslynSentinel.Common.LlmOptions.BaseUrl);

            // runDirectory always looks like <work-dir>\model-eval\<...>\<timestamp>; keep everything after "model-eval\".
            var marker = Path.Combine("model-eval") + Path.DirectorySeparatorChar;
            var markerIndex = runDirectory.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            var relative = markerIndex >= 0
                ? runDirectory[(markerIndex + marker.Length)..]
                : Path.GetFileName(runDirectory);

            var destination = Path.Combine(repoRoot, "ModelTestingResults", suffix, relative);
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

    // Mirrors roslynsentinel-modeleval.ps1's $knownHosts alias table and its fallback
    // sanitize-the-URL-into-a-suffix logic, so an archive lands in the same ModelTestingResults\<suffix>\ tree.
    private static string DeriveHostSuffix(string baseUrl)
    {
        if (baseUrl.Contains("192.168.1.112", StringComparison.OrdinalIgnoreCase))
        {
            return "112";
        }

        if (baseUrl.Contains("192.168.1.113", StringComparison.OrdinalIgnoreCase))
        {
            return "113";
        }

        if (baseUrl.Contains("192.168.10.85", StringComparison.OrdinalIgnoreCase))
        {
            return "85";
        }

        var sanitized = new string(baseUrl.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "unknown-host" : sanitized;
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
