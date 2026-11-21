namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Archives a finished model-eval run directory into a results tree and writes its test-result.txt.
/// Pure file logic with no NUnit or TestContext, so any test host can call it.
/// </summary>
public static class RunDirectoryArchive
{
    /// <summary>Outcome written when a run directory is recorded before its test has finished.</summary>
    public const string UnfinishedOutcome = "Unfinished (overwritten when the test ends)";

    /// <summary>
    /// Copies <paramref name="runDirectory"/> to <c>resultsRoot\hostSuffix\relative</c>, where relative is
    /// everything after the "model-eval" segment, or the directory's own leaf name when that segment is absent.
    /// Returns null when the source does not exist or the destination already exists (nothing is copied);
    /// otherwise returns the destination path.
    /// </summary>
    public static string? CopyToResults(string runDirectory, string resultsRoot, string hostSuffix)
    {
        if (!Directory.Exists(runDirectory))
        {
            return null;
        }

        var marker = Path.Combine("model-eval") + Path.DirectorySeparatorChar;
        var markerIndex = runDirectory.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var relative = markerIndex >= 0
            ? runDirectory[(markerIndex + marker.Length)..]
            : Path.GetFileName(runDirectory);

        var destination = Path.Combine(resultsRoot, hostSuffix, relative);
        if (Directory.Exists(destination))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        CopyDirectoryRecursive(runDirectory, destination);
        return destination;
    }

    /// <summary>
    /// Writes <c>test-result.txt</c> into <paramref name="runDirectory"/> with the outcome, test name and,
    /// when non-empty, the message and stack trace sections.
    /// </summary>
    public static void WriteTestResult(string runDirectory, string? outcome, string testName, string? message, string? stackTrace)
    {
        var lines = new List<string>
        {
            $"Outcome: {outcome ?? UnfinishedOutcome}",
            $"Test: {testName}",
        };

        if (!string.IsNullOrEmpty(message))
        {
            lines.Add("");
            lines.Add("Message:");
            lines.Add(message);
        }

        if (!string.IsNullOrEmpty(stackTrace))
        {
            lines.Add("");
            lines.Add("StackTrace:");
            lines.Add(stackTrace);
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

        foreach (var directory in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
        }
    }
}
