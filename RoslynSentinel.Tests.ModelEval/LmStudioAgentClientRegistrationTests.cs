using Microsoft.Extensions.DependencyInjection;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Guards the ModelEval fixtures' shared <c>AddLmStudioAgentClient</c> registration without needing
/// an LM Studio server. Lives in the parent namespace on purpose: <c>ModelEvalAssemblySetUp</c>
/// (namespace <c>...ModelEval.LiveModel</c>) only applies to the live-model fixtures beneath it, so
/// this test is never skipped by the unset <c>ROSLYNSENTINEL_LLM_MODEL</c> check.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LmStudioAgentClientRegistrationTests
{
    [TearDown]
    public void RestoreLlmOptions()
    {
        // Re-reads args (none) and ROSLYNSENTINEL_LLM_* env vars, undoing the test-only model.
        LlmOptions.Configure([]);
    }

    [Test]
    public void AddLmStudioAgentClient_ResolvesClient_WithModelFromLlmOptions()
    {
        LlmOptions.Configure(["--llm-model", "registration-test-model"]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLmStudioAgentClient(client => client.BaseAddress = new Uri("http://127.0.0.1:1/v1/"));

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<LmStudioAgentClient>();

        Assert.That(client, Is.Not.Null);
    }
}
