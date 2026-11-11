using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// The input_tokens / output_tokens split on <see cref="AgentChatMessage"/>: parsed from the Responses API usage object,
/// omitted from transcript JSON when absent, and an old transcript without the fields still loads. No live model.
/// </summary>
[TestFixture]
public class TokenUsageSplitTests
{
    private const string OldTranscriptJson =
        """{"SystemPrompt":"s","UserPrompt":"u","Tools":[],"Turns":[{"TurnNumber":1,"ModelMessage":{"Role":"assistant","Content":"x","ToolCalls":[],"TotalTokens":5},"ModelLatency":"00:00:01","StartedAt":"2026-11-11T10:00:00+00:00","CompletedAt":"2026-11-11T10:00:01+00:00","ToolCalls":[]}]}""";

    private const string SplitTranscriptJson =
        """{"SystemPrompt":"s","UserPrompt":"u","Tools":[],"Turns":[{"TurnNumber":1,"ModelMessage":{"Role":"assistant","Content":"x","ToolCalls":[],"TotalTokens":5,"InputTokens":10,"OutputTokens":4},"ModelLatency":"00:00:01","StartedAt":"2026-11-11T10:00:00+00:00","CompletedAt":"2026-11-11T10:00:01+00:00","ToolCalls":[]}]}""";

    [Test]
    public async Task Client_ParsesInputAndOutputTokens_FromResponseUsage()
    {
        var client = NewClient(CompletedEvent("""{"input_tokens":1200,"output_tokens":345,"total_tokens":1545}"""));

        var reply = await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256);

        Assert.That(reply.InputTokens, Is.EqualTo(1200));
        Assert.That(reply.OutputTokens, Is.EqualTo(345));
        Assert.That(reply.TotalTokens, Is.EqualTo(1545));
    }

    [Test]
    public async Task Client_UsageWithoutSplitFields_LeavesInputAndOutputNull()
    {
        var client = NewClient(CompletedEvent("""{"total_tokens":77}"""));

        var reply = await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256);

        Assert.That(reply.TotalTokens, Is.EqualTo(77));
        Assert.That(reply.InputTokens, Is.Null);
        Assert.That(reply.OutputTokens, Is.Null);
    }

    [Test]
    public void Transcript_WithoutTokenSplit_OmitsTheFieldsFromJson()
    {
        var withoutSplit = new AgentChatMessage { Role = "assistant", Content = "x", TotalTokens = 5 };

        var json = JsonSerializer.Serialize(withoutSplit);

        Assert.That(json, Does.Not.Contain("InputTokens"));
        Assert.That(json, Does.Not.Contain("OutputTokens"));

        var withSplit = new AgentChatMessage { Role = "assistant", Content = "x", TotalTokens = 5, InputTokens = 3, OutputTokens = 2 };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(withSplit));

        Assert.That(doc.RootElement.GetProperty("InputTokens").GetInt32(), Is.EqualTo(3));
        Assert.That(doc.RootElement.GetProperty("OutputTokens").GetInt32(), Is.EqualTo(2));
    }

    [Test]
    public void TranscriptResumeLoad_OldTranscriptJsonWithoutTokenSplit_StillLoads()
    {
        var source = LoadFromTempFile(OldTranscriptJson);

        Assert.That(source.Turns, Has.Count.EqualTo(1));
        Assert.That(source.Turns[0].ModelMessage.InputTokens, Is.Null);
        Assert.That(source.Turns[0].ModelMessage.OutputTokens, Is.Null);

        var withSplit = LoadFromTempFile(SplitTranscriptJson);

        Assert.That(withSplit.Turns, Has.Count.EqualTo(1));
        Assert.That(withSplit.Turns[0].ModelMessage.InputTokens, Is.EqualTo(10));
        Assert.That(withSplit.Turns[0].ModelMessage.OutputTokens, Is.EqualTo(4));
    }

    private static ResumeSource LoadFromTempFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"token-split-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            return TranscriptResume.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CompletedEvent(string usageJson) =>
        "event: response.completed\ndata: {\"response\":{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"hi\"}]}],\"usage\":"
        + usageJson + "}}\n\n";

    private static LmStudioAgentClient NewClient(string sse)
    {
        var httpClient = new HttpClient(new ScriptedHandler(sse))
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        return new LmStudioAgentClient(httpClient, "test-model", NullLogger<LmStudioAgentClient>.Instance);
    }

    /// <summary>Answers every chat request with the same SSE body; the models endpoint is 404 so the status probe stays quiet.</summary>
    private sealed class ScriptedHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/v0/models", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        }
    }
}
