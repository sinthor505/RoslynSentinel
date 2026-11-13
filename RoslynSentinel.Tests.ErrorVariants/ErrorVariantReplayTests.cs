using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// Live front door of the error-variant replay (plan Step 9): replays saved transcripts against a loaded LM Studio model with the
/// error text swapped for each variant, and writes one variants.csv row per sample. Driven by VARIANT_* environment variables
/// (see <see cref="ErrorVariantReplayOptions"/>) plus the usual ROSLYNSENTINEL_LLM_* ones; run it through
/// scripts/roslynsentinel-errorvariants.ps1. The pipeline itself is tested without a model in <see cref="ErrorVariantReplayPipelineTests"/>.
/// It asserts nothing about what the model does: it fails only when every sample hit a harness error, so nothing was measured.
/// </summary>
[TestFixture]
// LlmOptions.Configure is global state, so fixtures that call it must not run in parallel with each other.
[NonParallelizable]
public class ErrorVariantReplayTests
{
    [Test]
    [Explicit("Requires a loaded model. Manually run using scripts/roslynsentinel-errorvariants.ps1")]
    public async Task ReplayVariants_SamplesAndClassifies()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - the replay needs a real LM Studio server with a loaded model and is skipped rather than failed when unconfigured.");
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ErrorVariantReplayOptions.SourcesVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ErrorVariantReplayOptions.FilesVariable)))
        {
            Assert.Ignore(
                $"{ErrorVariantReplayOptions.SourcesVariable} and {ErrorVariantReplayOptions.FilesVariable} are not both set - give the source transcripts and the variants to replay.");
        }

        var options = ErrorVariantReplayOptions.FromEnvironment();
        var runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        TestContext.Out.WriteLine(
            $"Replaying {options.Sources.Count} source spec(s) x {options.Variants.Count} variant spec(s) x {options.Samples} sample(s), " +
            $"{options.ContinueTurns} live turn(s) after the cut, model {LlmOptions.Model} at {LlmOptions.BaseUrl}.");

        IReadOnlyList<VariantSampleRow> rows = [];
        try
        {
            using var handler = new SocketsHttpHandler();
            rows = await ErrorVariantReplay.RunAsync(
                options, handler, runDirectory, TestContext.CurrentContext.CancellationToken,
                row => TestContext.Out.WriteLine($"{row.VariantId} {row.SourceSample} #{row.SampleIndex}: {row.ToCsvLine()}"));
        }
        finally
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(runDirectory);
        }

        Assert.That(rows, Is.Not.Empty, "The replay wrote no rows.");
        Assert.That(
            rows.Any(row => row.HarnessError.Length == 0), Is.True,
            $"Every sample hit a harness error, so nothing was measured. First error: {rows[0].HarnessError}. See {Path.Combine(runDirectory, "variants.csv")}.");
    }
}
