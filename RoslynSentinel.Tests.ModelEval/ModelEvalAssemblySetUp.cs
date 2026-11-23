using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

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
/// <para>
/// Sampler source for every fixture in this assembly: only temperature and top-p are sent per request
/// (<c>LlmOptions.Temperature</c>/<c>TopP</c>; no test sets them itself). Top-k, min-p and the penalties come
/// from LM Studio's preset, or its defaults after a fresh model load, because /v1/responses ignores them if
/// sent. Only one model fits per host.
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
        // The in-process eval server never runs ServerStdio/ServerHttp, so read the experimental server flags'
        // env vars here (ROSLYNSENTINEL_LIFT_RENAMES, ROSLYNSENTINEL_LOOP_LATCH); unset leaves them off.
        LiftRenamesOptions.Configure([]);
        // Reads ROSLYNSENTINEL_NOTFOUND_INVENTORY (on by default; 0 or false turns the symbol member inventory off; the file-miss folder list is always on; the stamp below carries +inventory while it is on).
        NotFoundInventoryOptions.Configure([]);
        LoopLatchOptions.Configure([]);
        // Reads ROSLYNSENTINEL_NEAR_MISS_AUTOCORRECT / _NAME_DISPLAY (eval-only options; unset = off / show). Must run
        // before the in-process server is built: the _meta filter is registered only when NearMissRecorder.IsActive.
        NearMissOptions.Configure([]);
        // Reads ROSLYNSENTINEL_ERROR_POLICY_MODE / _FILE; unset leaves the policy Off. For Pointer/Inline with no file,
        // the harness defaults the file to ErrorPolicyFile.OutputPath (deviation from production startup, harness convenience only).
        ErrorPolicyOptions.Configure(ErrorPolicyFile.ResolveConfigureArgs(
            Environment.GetEnvironmentVariable("ROSLYNSENTINEL_ERROR_POLICY_MODE"),
            Environment.GetEnvironmentVariable("ROSLYNSENTINEL_ERROR_POLICY_FILE")));
        // RepoRootLocator.TryFind() with no argument consults ROSLYNSENTINEL_REPO_ROOT; TryFind(startDirectory) ignores it.
        ModelAgentRunner.ProcessStamp = EvalRunStamp.BuildProcessStamp(EvalRunStamp.ReadWorkingTreeStamp(RepoRootLocator.TryFind() ?? AppContext.BaseDirectory));
        // Reads ROSLYNSENTINEL_MODELEVAL_CONTRACT / _CONTRACT_ENDINGS (unset = legacy contract, system endings). Runs before the
        // Assert.Ignore model check below so a bad value fails loudly rather than being skipped.
        ModelAgentRunner.ContractStamp = EvalContractOptions.BuildContractStamp();

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
