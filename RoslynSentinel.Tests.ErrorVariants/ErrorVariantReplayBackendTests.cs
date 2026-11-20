using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// ErrorVariantReplay.CheckModelLoadedAsync against a llama-server model list (data[] with loaded fields), and the unchanged
/// LM Studio branch when the backend is lmstudio. A scripted host answers the GET; no model or server is involved.
/// </summary>
[TestFixture]
public class ErrorVariantReplayBackendTests
{
    private const string ModelsUrl = "http://host.test/v1/models";

    private static async Task<string?> CheckAsync(string modelsJson, string modelKey, string backend)
    {
        using var handler = new ScriptedModelHandler { ModelsJson = modelsJson };
        using var http = new HttpClient(handler, disposeHandler: false);
        return await ErrorVariantReplay.CheckModelLoadedAsync(http, ModelsUrl, modelKey, CancellationToken.None, backend);
    }

    [TestCase("unsloth/X", "X")]
    [TestCase("X", "unsloth/X")]
    public async Task LlamaServer_LoadedModel_ReturnsNull_WhateverTheOrgPrefix(string requested, string listedId)
    {
        var json = $$"""{"object":"list","data":[{"id":"{{listedId}}","loaded":true}]}""";

        Assert.That(await CheckAsync(json, requested, EvalHostTable.BackendLlamaServer), Is.Null);
    }

    [Test]
    public async Task LlamaServer_StatusFormLoaded_ReturnsNull()
    {
        const string json = """{"data":[{"id":"X","status":{"value":"loaded"}}]}""";

        Assert.That(await CheckAsync(json, "X", EvalHostTable.BackendLlamaServer), Is.Null);
    }

    [Test]
    public async Task LlamaServer_NoLoadedFields_ReturnsNull()
    {
        const string json = """{"data":[{"id":"X"},{"id":"Y"}]}""";

        Assert.That(await CheckAsync(json, "X", EvalHostTable.BackendLlamaServer), Is.Null);
    }

    [Test]
    public async Task LlamaServer_ListedButNotLoaded_ReturnsTheNotLoadedFact()
    {
        const string json = """{"data":[{"id":"X","loaded":false}]}""";

        Assert.That(
            await CheckAsync(json, "X", EvalHostTable.BackendLlamaServer),
            Is.EqualTo("model 'X' is known to the host but has no loaded instance."));
    }

    [Test]
    public async Task LlamaServer_NotListed_ReturnsTheNotListedFactWithCount()
    {
        const string json = """{"data":[{"id":"A","loaded":true},{"id":"B","loaded":false}]}""";

        Assert.That(
            await CheckAsync(json, "X", EvalHostTable.BackendLlamaServer),
            Is.EqualTo("model 'X' is not in the host's model list (2 model(s) listed)."));
    }

    [Test]
    public async Task LmStudio_LoadedModel_StillReturnsNull()
    {
        const string json = """{"models":[{"key":"m","loaded_instances":[{}]}]}""";

        Assert.That(await CheckAsync(json, "m", EvalHostTable.BackendLmStudio), Is.Null);
    }

    [Test]
    public async Task LmStudio_NotListed_StillReturnsTheNativeListFact()
    {
        const string json = """{"models":[{"key":"m","loaded_instances":[{}]}]}""";

        Assert.That(
            await CheckAsync(json, "other", EvalHostTable.BackendLmStudio),
            Is.EqualTo("model 'other' is not in the host's model list (1 model(s) listed)."));
    }
}
