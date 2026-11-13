using System.Text.Json;
using NUnit.Framework;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class TranscriptSamplingHeaderTests
{
    [Test]
    public void Load_KeepsModelKeyTemperatureAndTopP()
    {
        var written = new AgentTranscript
        {
            RunId = "run-1",
            ModelKey = "qwen/qwen3-coder-30b",
            Temperature = 0.1,
            TopP = 0.95,
        };

        var loaded = RoundTrip(written, out var json);

        Assert.That(loaded.ModelKey, Is.EqualTo("qwen/qwen3-coder-30b"));
        Assert.That(loaded.Temperature, Is.EqualTo(0.1));
        Assert.That(loaded.TopP, Is.EqualTo(0.95));
        Assert.That(json, Does.Contain("\"ModelKey\""));
        Assert.That(json, Does.Contain("\"Temperature\""));
        Assert.That(json, Does.Contain("\"TopP\""));
    }

    [Test]
    public void Load_TranscriptWithoutTheFieldsReadsNull()
    {
        // The three properties are omitted when null, so this is also the shape of a transcript written before they existed.
        var loaded = RoundTrip(new AgentTranscript { RunId = "run-2" }, out var json);

        Assert.That(json, Does.Not.Contain("ModelKey"));
        Assert.That(json, Does.Not.Contain("Temperature"));
        Assert.That(json, Does.Not.Contain("TopP"));
        Assert.That(loaded.ModelKey, Is.Null);
        Assert.That(loaded.Temperature, Is.Null);
        Assert.That(loaded.TopP, Is.Null);
    }

    [Test]
    public void Load_ModelKeyAloneLeavesSamplingNull()
    {
        var loaded = RoundTrip(new AgentTranscript { RunId = "run-3", ModelKey = "m" }, out _);

        Assert.That(loaded.ModelKey, Is.EqualTo("m"));
        Assert.That(loaded.Temperature, Is.Null);
        Assert.That(loaded.TopP, Is.Null);
    }

    private static AgentTranscript RoundTrip(AgentTranscript written, out string json)
    {
        // Same serializer call as ModelAgentRunner.WriteTranscriptAsync.
        json = JsonSerializer.Serialize(written, new JsonSerializerOptions { WriteIndented = true });
        var folder = Path.Combine(Path.GetTempPath(), "TranscriptSamplingHeaderTests_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "transcript.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, json);
            return SavedTranscript.Load(path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
