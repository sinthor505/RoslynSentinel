using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Copies a finished run's directory (transcripts, agent.log) into
/// <c>&lt;scratch&gt;\ModelTestingResults\&lt;host-suffix&gt;\&lt;relative-path-under-model-eval&gt;\</c> tree, so
/// every run is archived regardless of how the test was launched -> previously this only happened
/// when a run went through <c>roslynsentinel-modeleval.ps1</c>'s own post-run copy step
/// (Copy-NewRunDirectories), so a run launched directly via `dotnet test`/the VS Code test
/// explorer left its transcript stranded under the scratch build's own bin/obj output, never
/// reaching the durable archive a later pattern-analysis pass would read.
/// </summary>
// TODO move with ModelTestingResultsArchiverTests and RewriteBodyArchivedOutcome* to Tests.Harness: needs this type public (about 15 live fixtures use it) and an [assembly: RewriteBodyArchivedOutcome] line in both assemblies (TODO.md "remaining model-free Tests.ModelEval fixtures")
internal static class ModelTestingResultsArchiver
{
    /// <summary>
    /// Call from a model-eval test fixture's TearDown with its own <c>_runDirectory</c> (the
    /// leaf directory <see cref="ModelAgentRunner.RunAsync"/> wrote the transcript
    /// into). No-ops quietly (logging via <see cref="TestContext"/> rather than throwing) if the
    /// directory doesn't exist -> e.g. SetUp's Assert.Ignore fired before any run happened -> since
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

            // TestContext.CurrentContext.Result is already populated by the time TearDown runs (NUnit
            // fills in Outcome/Message/StackTrace before TearDown, even on failure) but nothing was ever
            // capturing it -> the archived run directory had the agent's own transcript/log but not the
            // test framework's verdict on it, so a failing run couldn't be told apart from a passing one,
            // and the actual failed-assertion text/stack trace was only ever visible in the live test
            // runner's own output, never preserved alongside the transcript that explains it.
            var result = TestContext.CurrentContext.Result;
            var outcome = result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Inconclusive
                ? null
                : result.Outcome.ToString();
            RunDirectoryArchive.WriteTestResult(runDirectory, outcome, TestContext.CurrentContext.Test.FullName, result.Message, result.StackTrace);

            var destination = RunDirectoryArchive.CopyToResults(runDirectory, ScratchRoot.ResultsRoot(), EvalHostTable.DeriveSuffix(RoslynSentinel.Common.LlmOptions.BaseUrl));
            if (outcome is null)
            {
                // Body-called: test-result.txt says Unfinished now; RewriteBodyArchivedOutcomeAttribute rewrites it (run directory and archived copy) when the test ends.
                PendingArchivedOutcomes.Register(TestContext.CurrentContext.Test.ID, runDirectory, destination);
            }
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"ModelTestingResultsArchiver: failed to archive '{runDirectory}': {ex.Message}");
        }
    }
}
