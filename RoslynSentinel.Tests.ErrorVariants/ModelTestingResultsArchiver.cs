using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// Copies a finished run's directory (transcripts, variants.csv) into
/// <c>&lt;scratch&gt;\ModelTestingResults\&lt;host-suffix&gt;\&lt;relative-path-under-model-eval&gt;\</c> tree, so
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

            // Deviation: the outcome is always written as Unfinished (null) because this runs from the test body's
            // finally block, before the test's result is final. The file still names the test that wrote the folder.
            var result = TestContext.CurrentContext.Result;
            RunDirectoryArchive.WriteTestResult(runDirectory, null, TestContext.CurrentContext.Test.FullName, result.Message, result.StackTrace);

            RunDirectoryArchive.CopyToResults(runDirectory, ScratchRoot.ResultsRoot(), EvalHostTable.DeriveSuffix(RoslynSentinel.Common.LlmOptions.BaseUrl));
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"ModelTestingResultsArchiver: failed to archive '{runDirectory}': {ex.Message}");
        }
    }
}
