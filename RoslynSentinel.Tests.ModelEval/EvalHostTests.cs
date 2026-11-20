using System;
using System.IO;
using System.Linq;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="EvalHostTable"/>: eval-hosts.json validation, suffix and backend lookup against the six pinned hosts. No live model.
/// </summary>
[TestFixture]
public class EvalHostTableTests
{
    private const string SixHostsJson = """
        {
          "hosts": [
            { "alias": "112u", "machine": "112", "baseUrl": "http://192.168.1.112:8888/v1", "backend": "llama-server", "suffix": "http___192_168_1_112_8888_v1", "default": true },
            { "alias": "112lms", "machine": "112", "baseUrl": "http://192.168.1.112:1234/v1", "backend": "lmstudio", "suffix": "112", "default": false },
            { "alias": "113u", "machine": "113", "baseUrl": "http://192.168.1.113:8888/v1", "backend": "llama-server", "suffix": "http___192_168_1_113_8888_v1", "default": true },
            { "alias": "113lms", "machine": "113", "baseUrl": "http://192.168.1.113:1234/v1", "backend": "lmstudio", "suffix": "113", "default": false },
            { "alias": "85u", "machine": "85", "baseUrl": "http://192.168.10.85:8888/v1", "backend": "llama-server", "suffix": "http___192_168_10_85_8888_v1", "default": true },
            { "alias": "85lms", "machine": "85", "baseUrl": "http://192.168.10.85:1234/v1", "backend": "lmstudio", "suffix": "85", "default": false }
          ]
        }
        """;

    [Test]
    public void Load_SixHostTable_ReturnsEveryEntryWithItsDefaultFlag()
    {
        var table = SixHosts();

        Assert.That(table.Count, Is.EqualTo(6));
        Assert.That(table.Single(h => h.Alias == "112u").IsDefault, Is.EqualTo(true));
        Assert.That(table.Single(h => h.Alias == "112lms").IsDefault, Is.EqualTo(false));
    }

    [Test]
    public void Load_MissingPath_ReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-eval-hosts-" + Guid.NewGuid().ToString("N") + ".json");

        Assert.That(EvalHostTable.Load(missing), Is.Empty);
    }

    [Test]
    public void Load_MachineWithZeroDefaults_Throws()
    {
        var json = Hosts(Entry("112u", "112", "http://192.168.1.112:8888/v1", false));

        Assert.Throws<InvalidOperationException>(() => LoadJson(json));
    }

    [Test]
    public void Load_MachineWithTwoDefaults_Throws()
    {
        var json = Hosts(
            Entry("112u", "112", "http://192.168.1.112:8888/v1", true),
            Entry("112lms", "112", "http://192.168.1.112:1234/v1", true));

        Assert.Throws<InvalidOperationException>(() => LoadJson(json));
    }

    [Test]
    public void Load_AliasEqualToMachineKey_Throws()
    {
        var json = Hosts(
            Entry("112u", "112", "http://192.168.1.112:8888/v1", true),
            Entry("112", "85", "http://192.168.10.85:8888/v1", true));

        Assert.Throws<InvalidOperationException>(() => LoadJson(json));
    }

    [Test]
    public void Load_DuplicateAlias_Throws()
    {
        var json = Hosts(
            Entry("112u", "112", "http://192.168.1.112:8888/v1", true),
            Entry("112u", "85", "http://192.168.10.85:8888/v1", true));

        Assert.Throws<InvalidOperationException>(() => LoadJson(json));
    }

    [Test]
    public void Load_BaseUrlSharedByTwoEntries_Throws()
    {
        var json = Hosts(
            Entry("112u", "112", "http://192.168.1.112:8888/v1", true),
            Entry("112lms", "112", "http://192.168.1.112:8888/v1", false));

        Assert.Throws<InvalidOperationException>(() => LoadJson(json));
    }

    [TestCase("http://192.168.1.112:1234/v1", "112")]
    [TestCase("http://192.168.1.113:1234/v1", "113")]
    [TestCase("http://192.168.10.85:1234/v1", "85")]
    [TestCase("http://192.168.1.112:1234/v1/", "112")]
    [TestCase("HTTP://192.168.1.113:1234/V1", "113")]
    public void DeriveSuffix_PinnedLmStudioUrl_ReturnsPinnedSuffix(string baseUrl, string expected)
    {
        Assert.That(EvalHostTable.DeriveSuffix(SixHosts(), baseUrl), Is.EqualTo(expected));
    }

    [TestCase("http://192.168.1.112:8888/v1", "http___192_168_1_112_8888_v1")]
    [TestCase("http://192.168.10.85:8888/v1", "http___192_168_10_85_8888_v1")]
    [TestCase("http://localhost:1234/v1", "http___localhost_1234_v1")]
    public void DeriveSuffix_UnpinnedUrl_ReturnsSanitisedUrl(string baseUrl, string expected)
    {
        Assert.That(EvalHostTable.DeriveSuffix(SixHosts(), baseUrl), Is.EqualTo(expected));
    }

    [TestCase("http://192.168.1.112:1234/v1", "http___192_168_1_112_1234_v1")]
    [TestCase("://", "unknown-host")]
    public void DeriveSuffix_EmptyTable_SanitisesEveryUrl(string baseUrl, string expected)
    {
        Assert.That(EvalHostTable.DeriveSuffix(Array.Empty<EvalHost>(), baseUrl), Is.EqualTo(expected));
    }

    [TestCase("http://192.168.1.112:8888/v1", "llama-server")]
    [TestCase("http://192.168.1.113:8888/v1", "llama-server")]
    [TestCase("http://192.168.10.85:8888/v1", "llama-server")]
    [TestCase("http://192.168.1.112:1234/v1", "lmstudio")]
    [TestCase("http://10.0.0.9:9999/v1", "lmstudio")]
    public void BackendFor_SixHostTable_ReturnsEntryBackendOrLmStudio(string baseUrl, string expected)
    {
        Assert.That(EvalHostTable.BackendFor(SixHosts(), baseUrl), Is.EqualTo(expected));
    }

    [TestCase("http://192.168.1.112:8888/v1")]
    [TestCase("http://192.168.1.112:1234/v1")]
    [TestCase("http://unknown.example/v1")]
    public void BackendFor_EmptyTable_ReturnsLmStudio(string baseUrl)
    {
        Assert.That(EvalHostTable.BackendFor(Array.Empty<EvalHost>(), baseUrl), Is.EqualTo(EvalHostTable.BackendLmStudio));
    }

    private static IReadOnlyList<EvalHost> SixHosts() => LoadJson(SixHostsJson);

    private static string Entry(string alias, string machine, string baseUrl, bool isDefault) =>
        "{ \"alias\": \"" + alias + "\", \"machine\": \"" + machine + "\", \"baseUrl\": \"" + baseUrl
        + "\", \"backend\": \"llama-server\", \"suffix\": \"sfx-" + alias + "\", \"default\": "
        + (isDefault ? "true" : "false") + " }";

    private static string Hosts(params string[] entries) =>
        "{ \"hosts\": [ " + string.Join(", ", entries) + " ] }";

    private static IReadOnlyList<EvalHost> LoadJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "eval-hosts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, json);
            return EvalHostTable.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// <see cref="LlamaServerModels"/>: /v1/models parsing and id matching against a live-shaped body. No live model.
/// </summary>
[TestFixture]
public class LlamaServerModelsTests
{
    private const string LiveShapedBody = """
        {"object":"list","data":[
          {"id":"Qwen3.5-4B-MTP-GGUF","object":"model","loaded":true,"status":{"value":"loaded"},"context_length":70000},
          {"id":"unsloth/Ornith-1.0-9B-GGUF","object":"model","loaded":true,"context_length":70000},
          {"id":"Bonsai-27B-GGUF","object":"model","loaded":true,"context_length":74752},
          {"id":"Idle-Model-GGUF","object":"model","loaded":false,"status":{"value":"unloaded"}}
        ]}
        """;

    [Test]
    public void Parse_LiveShapedBody_ReadsIdLoadedAndContextLength()
    {
        var models = LlamaServerModels.Parse(LiveShapedBody);

        Assert.That(models.Count, Is.EqualTo(4));
        Assert.That(models[0], Is.EqualTo(new LlamaServerModel("Qwen3.5-4B-MTP-GGUF", true, 70000)));
        Assert.That(models[1], Is.EqualTo(new LlamaServerModel("unsloth/Ornith-1.0-9B-GGUF", true, 70000)));
        Assert.That(models[2], Is.EqualTo(new LlamaServerModel("Bonsai-27B-GGUF", true, 74752)));
        Assert.That(models[3], Is.EqualTo(new LlamaServerModel("Idle-Model-GGUF", false, null)));
    }

    [Test]
    public void Parse_EntryWithoutLoadedOrStatus_LoadedIsNull()
    {
        var models = LlamaServerModels.Parse("{\"data\":[{\"id\":\"plain-GGUF\",\"context_length\":4096}]}");

        Assert.That(models.Single(), Is.EqualTo(new LlamaServerModel("plain-GGUF", null, 4096)));
    }

    [Test]
    public void Parse_StatusOnlyEntry_LoadedFromStatusValue()
    {
        var models = LlamaServerModels.Parse("{\"data\":[{\"id\":\"a\",\"status\":{\"value\":\"loaded\"}},{\"id\":\"b\",\"status\":{\"value\":\"unloaded\"}}]}");

        Assert.That(models[0].Loaded, Is.EqualTo(true));
        Assert.That(models[1].Loaded, Is.EqualTo(false));
    }

    [TestCase("{not json")]
    [TestCase("")]
    [TestCase(null)]
    public void Parse_InvalidOrEmptyBody_ReturnsEmpty(string? body)
    {
        Assert.That(LlamaServerModels.Parse(body), Is.Empty);
    }

    [TestCase("unsloth/Ornith-1.0-9B-GGUF")]
    [TestCase("Ornith-1.0-9B-GGUF")]
    public void FindLoaded_RequestedWithOrWithoutOrgPrefix_ReturnsLoadedEntry(string requested)
    {
        var models = LlamaServerModels.Parse(LiveShapedBody);

        Assert.That(LlamaServerModels.FindLoaded(models, requested)?.Id, Is.EqualTo("unsloth/Ornith-1.0-9B-GGUF"));
    }

    [Test]
    public void FindLoaded_UnloadedEntry_ReturnsNullButFindListedReturnsIt()
    {
        var models = LlamaServerModels.Parse(LiveShapedBody);

        Assert.That(LlamaServerModels.FindLoaded(models, "Idle-Model-GGUF"), Is.Null);
        Assert.That(LlamaServerModels.FindListed(models, "Idle-Model-GGUF")?.Id, Is.EqualTo("Idle-Model-GGUF"));
    }

    [TestCase("Qwen3.5-4B-MTP-GGUF")]
    [TestCase("unsloth/Qwen3.5-4B-MTP-GGUF")]
    public void FindListed_RequestedWithOrWithoutOrgPrefix_ReturnsEntry(string requested)
    {
        var models = LlamaServerModels.Parse(LiveShapedBody);

        Assert.That(LlamaServerModels.FindListed(models, requested)?.Id, Is.EqualTo("Qwen3.5-4B-MTP-GGUF"));
    }

    [Test]
    public void FindListed_UnknownModel_ReturnsNull()
    {
        var models = LlamaServerModels.Parse(LiveShapedBody);

        Assert.That(LlamaServerModels.FindListed(models, "Missing-GGUF"), Is.Null);
    }

    [TestCase("unsloth/Qwen3.5-4B-MTP-GGUF", "Qwen3.5-4B-MTP-GGUF", true)]
    [TestCase("qwen3.5-4b-mtp-gguf", "unsloth/Qwen3.5-4B-MTP-GGUF", true)]
    [TestCase("Qwen3.5-4B-GGUF", "Qwen3.5-4B-MTP-GGUF", false)]
    [TestCase("Qwen3.5-4B", "unsloth/Qwen3.5-4B-MTP-GGUF", false)]
    public void IdsMatch_ComparesTextAfterLastSlashIgnoringCase(string requested, string listed, bool expected)
    {
        Assert.That(LlamaServerModels.IdsMatch(requested, listed), Is.EqualTo(expected));
    }
}
