using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// Runs once before any test in this assembly. Each fixture's own <c>[SetUp]</c> builds a real
/// <c>LmStudioAgentClient</c> and only checks that <see cref="LlmOptions.Model"/> is non-empty -
/// none of them touch the network before starting a model-eval run, so a genuinely unreachable LM
/// Studio server surfaces only via the per-fixture <c>HttpClient</c>'s own multi-minute timeout
/// (<c>Math.Max(LlmOptions.TimeoutSeconds * 4, 600)</c> seconds) or the 120s
/// <see cref="LlmOptions.StreamIdleTimeoutSeconds"/> retry cycle, deep inside the first generation
/// call - indistinguishable from a server that is merely slow to respond.
/// <para>
/// This adds a fast, deliberately short-timeout reachability check, separate from those generation
/// timeouts, so an unreachable server fails in seconds with a clear reason instead of minutes with
/// an ambiguous one. All the fixtures in this assembly are also marked <c>[Explicit]</c> so they
/// never run as part of an ordinary full-suite pass - this preflight is a safety net for when they
/// ARE run explicitly without a live server up, not the primary gate.
/// </para>
/// </summary>
[SetUpFixture]
public class ModelEvalAssemblySetUp
{
    private static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(5);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        LlmOptions.Configure([]);

        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        using var probeClient = new HttpClient { Timeout = ReachabilityTimeout };
        var modelsUrl = LlmOptions.BaseUrl.TrimEnd('/') + "/models";

        try
        {
            using var response = await probeClient.GetAsync(modelsUrl, TestContext.CurrentContext.CancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Assert.Ignore(
                $"LLM server at {LlmOptions.BaseUrl} did not respond within {ReachabilityTimeout.TotalSeconds}s " +
                $"({ex.GetType().Name}: {ex.Message}) - is LM Studio running with a model loaded?");
        }
    }
}
