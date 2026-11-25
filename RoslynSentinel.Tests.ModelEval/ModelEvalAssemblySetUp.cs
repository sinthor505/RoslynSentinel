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
        // Transcripts of in-process eval hosts go to call-transcripts-tests, not the real-session folder.
        TestHostAmbientDefaults.Apply();
        // Applies every Server, AgentLoop and TestHarness setting from its env var (registry-driven; the in-process eval server never runs
        // ServerStdio/ServerHttp) and fails fast on an invalid value, so a bad env var fails the run instead of being ignored.
        // For ROSLYNSENTINEL_ERROR_POLICY_MODE / _FILE, Pointer/Inline with no file default the file to ErrorPolicyFile.OutputPath
        // (deviation from production startup, harness convenience only): the resolved args become flags, which beat env.
        var report = StartupSettings.ConfigureAll(
            ErrorPolicyFile.ResolveConfigureArgs(
                StartupSettings.EnvironmentReader(ErrorPolicyOptions.ModeSetting.EnvName!),
                StartupSettings.EnvironmentReader(ErrorPolicyOptions.FileSetting.EnvName!)),
            typeof(ModelEvalAssemblySetUp).Assembly,
            SettingScope.Server | SettingScope.AgentLoop | SettingScope.TestHarness);
        StartupSettings.ThrowIfInvalid(report);
        // RepoRootLocator.TryFind() with no argument consults ROSLYNSENTINEL_REPO_ROOT; TryFind(startDirectory) ignores it.
        ModelAgentRunner.ProcessStamp = EvalRunStamp.BuildProcessStamp(EvalRunStamp.ReadWorkingTreeStamp(RepoRootLocator.TryFind() ?? AppContext.BaseDirectory));
        // Reads ROSLYNSENTINEL_MODELEVAL_CONTRACT / _CONTRACT_ENDINGS (unset = legacy contract, system endings). Runs before the
        // Assert.Ignore model check below so a bad value fails loudly rather than being skipped.
        ModelAgentRunner.ContractStamp = EvalContractOptions.BuildContractStamp();
        // Reads the thinking-budget env vars (ThinkingOptions.FromEnvironment); unset = Off. Every ModelEval runner without an explicit thinking argument picks this up.
        ModelAgentRunner.ThinkingDefault = ThinkingOptions.FromEnvironment();

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
