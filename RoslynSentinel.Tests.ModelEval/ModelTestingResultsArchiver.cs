namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Copies a finished run's directory (transcripts, agent.log) into the repo's
/// <c>ModelTestingResults\&lt;host-suffix&gt;\&lt;relative-path-under-model-eval&gt;\</c> tree, so
/// every run is archived regardless of how the test was launched — previously this only happened
/// when a run went through <c>roslynsentinel-modeleval.ps1</c>'s own post-run copy step
/// (Copy-NewRunDirectories), so a run launched directly via `dotnet test`/the VS Code test
/// explorer left its transcript stranded under the scratch build's own bin/obj output, never
/// reaching the durable archive a later pattern-analysis pass would read.
/// </summary>
internal static class ModelTestingResultsArchiver
{
    /// <summary>
    /// Call from a model-eval test fixture's TearDown with its own <c>_runDirectory</c> (the
    /// leaf directory <see cref="AgentLoop.ModelAgentRunner.RunAsync"/> wrote the transcript
    /// into). No-ops quietly (logging via <see cref="TestContext"/> rather than throwing) if the
    /// directory doesn't exist — e.g. SetUp's Assert.Ignore fired before any run happened — since
    /// a failed archive copy should never mask or replace the test's own pass/fail outcome.
    /// </summary>
    public static void ArchiveRunDirectory(string runDirectory)
    {
        try
        {
            if (!Directory.Exists(runDirectory))
            {
                return;
            }

            var repoRoot = FindRepoRoot();
            var suffix = DeriveHostSuffix(RoslynSentinel.Common.LlmOptions.BaseUrl);

            // _runDirectory always looks like <work-dir>\model-eval\<...>\<timestamp>; keep
            // everything from "model-eval"'s next segment onward so SizeThreshold's extra
            // "n<size>" nesting level is preserved exactly like the .ps1 script's own
            // Copy-NewRunDirectories does, without hardcoding either shape here.
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

    // Mirrors roslynsentinel-modeleval.ps1's $knownHosts alias table + its fallback
    // sanitize-the-URL-into-a-suffix logic, so a test-driven archive lands in the exact same
    // ModelTestingResults\<suffix>\ tree the script has always used — existing runs (e.g.
    // ModelTestingResults\113\...) and newly-archived ones merge into one history per host.
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
