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
        """{"SystemPrompt":"s","UserPrompt":"u","Tools":[],"Turns":[{"TurnNumber":1,"ModelMessage":{"Role":"assistant","Content":"x","ToolCalls":[],"TotalTokens":5,"InputTokens":10,"OutputTokens":4,"CachedInputTokens":6,"ReasoningTokens":2},"ModelLatency":"00:00:01","StartedAt":"2026-11-11T10:00:00+00:00","CompletedAt":"2026-11-11T10:00:01+00:00","ToolCalls":[]}]}""";

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
    public async Task Client_ParsesCachedAndReasoningTokens_FromResponseUsageDetails()
    {
        var client = NewClient(CompletedEvent("""{"input_tokens":1200,"input_tokens_details":{"cached_tokens":1000},"output_tokens":345,"output_tokens_details":{"reasoning_tokens":300},"total_tokens":1545}"""));

        var reply = await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256);

        Assert.That(reply.CachedInputTokens, Is.EqualTo(1000));
        Assert.That(reply.ReasoningTokens, Is.EqualTo(300));
        Assert.That(reply.InputTokens, Is.EqualTo(1200));
        Assert.That(reply.OutputTokens, Is.EqualTo(345));
    }

    [Test]
    public async Task Client_UsageWithoutDetails_LeavesCachedAndReasoningNull()
    {
        var client = NewClient(CompletedEvent("""{"input_tokens":10,"output_tokens":4,"total_tokens":14}"""));

        var reply = await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256);

        Assert.That(reply.CachedInputTokens, Is.Null);
        Assert.That(reply.ReasoningTokens, Is.Null);
    }

    [Test]
    public void Transcript_WithoutTokenSplit_OmitsTheFieldsFromJson()
    {
        var withoutSplit = new AgentChatMessage { Role = "assistant", Content = "x", TotalTokens = 5 };

        var json = JsonSerializer.Serialize(withoutSplit);

        Assert.That(json, Does.Not.Contain("InputTokens"));
        Assert.That(json, Does.Not.Contain("OutputTokens"));
        Assert.That(json, Does.Not.Contain("CachedInputTokens"));
        Assert.That(json, Does.Not.Contain("ReasoningTokens"));

        var withSplit = new AgentChatMessage
        {
            Role = "assistant", Content = "x", TotalTokens = 5, InputTokens = 3, OutputTokens = 2,
            CachedInputTokens = 1, ReasoningTokens = 1,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(withSplit));

        Assert.That(doc.RootElement.GetProperty("InputTokens").GetInt32(), Is.EqualTo(3));
        Assert.That(doc.RootElement.GetProperty("OutputTokens").GetInt32(), Is.EqualTo(2));
        Assert.That(doc.RootElement.GetProperty("CachedInputTokens").GetInt32(), Is.EqualTo(1));
        Assert.That(doc.RootElement.GetProperty("ReasoningTokens").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public void TranscriptResumeLoad_OldTranscriptJsonWithoutTokenSplit_StillLoads()
    {
        var source = LoadFromTempFile(OldTranscriptJson);

        Assert.That(source.Turns, Has.Count.EqualTo(1));
        Assert.That(source.Turns[0].ModelMessage.InputTokens, Is.Null);
        Assert.That(source.Turns[0].ModelMessage.OutputTokens, Is.Null);
        Assert.That(source.Turns[0].ModelMessage.CachedInputTokens, Is.Null);
        Assert.That(source.Turns[0].ModelMessage.ReasoningTokens, Is.Null);

        var withSplit = LoadFromTempFile(SplitTranscriptJson);

        Assert.That(withSplit.Turns, Has.Count.EqualTo(1));
        Assert.That(withSplit.Turns[0].ModelMessage.InputTokens, Is.EqualTo(10));
        Assert.That(withSplit.Turns[0].ModelMessage.OutputTokens, Is.EqualTo(4));
        Assert.That(withSplit.Turns[0].ModelMessage.CachedInputTokens, Is.EqualTo(6));
        Assert.That(withSplit.Turns[0].ModelMessage.ReasoningTokens, Is.EqualTo(2));
    }

    [Test]
    public async Task Client_MeasuresFirstTokenAndGenerationTime_WhenStreamHasGenerationEvent()
    {
        var completed = CompletedEvent("""{"input_tokens":10,"output_tokens":40,"total_tokens":50}""");
        var client = NewClient(new StreamHandler(() => new TwoPartDelayedStream(
            "event: response.output_item.added\ndata: {}\n\n", TimeSpan.FromMilliseconds(200), completed)));

        var reply = await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256);

        Assert.That(reply.TimeToFirstTokenSeconds, Is.Not.Null);
        Assert.That(reply.TimeToFirstTokenSeconds, Is.GreaterThanOrEqualTo(0.0));

        // Lower bound only: the delay is real time, so machine load can only make the window longer.
        Assert.That(reply.GenerationSeconds, Is.GreaterThanOrEqualTo(0.15));
        Assert.That(reply.TokensPerSecond, Is.GreaterThan(0.0));
        Assert.That(reply.TokensPerSecond, Is.LessThanOrEqualTo(40 / 0.15));
    }

    [Test]
    public async Task Client_StreamWithoutGenerationEvent_LeavesTimingNull()
    {
        var client = NewClient(CompletedEvent("""{"input_tokens":10,"output_tokens":4,"total_tokens":14}"""));

        var reply = await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256);

        Assert.That(reply.TimeToFirstTokenSeconds, Is.Null);
        Assert.That(reply.GenerationSeconds, Is.Null);
        Assert.That(reply.TokensPerSecond, Is.Null);
    }

    [Test]
    public void Transcript_WithoutTiming_OmitsTheFieldsFromJson()
    {
        var withoutTiming = new AgentChatMessage { Role = "assistant", Content = "x", TotalTokens = 5 };

        var json = JsonSerializer.Serialize(withoutTiming);

        Assert.That(json, Does.Not.Contain("TimeToFirstTokenSeconds"));
        Assert.That(json, Does.Not.Contain("GenerationSeconds"));
        Assert.That(json, Does.Not.Contain("TokensPerSecond"));

        var withTiming = new AgentChatMessage
        {
            Role = "assistant", Content = "x", TimeToFirstTokenSeconds = 1.5, GenerationSeconds = 2.0, TokensPerSecond = 20.0,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(withTiming));

        Assert.That(doc.RootElement.GetProperty("TimeToFirstTokenSeconds").GetDouble(), Is.EqualTo(1.5));
        Assert.That(doc.RootElement.GetProperty("GenerationSeconds").GetDouble(), Is.EqualTo(2.0));
        Assert.That(doc.RootElement.GetProperty("TokensPerSecond").GetDouble(), Is.EqualTo(20.0));
    }

    [Test]
    public void TranscriptResumeLoad_OldTranscriptJsonWithoutTiming_StillLoads()
    {
        var source = LoadFromTempFile(OldTranscriptJson);

        Assert.That(source.Turns, Has.Count.EqualTo(1));
        Assert.That(source.Turns[0].ModelMessage.TimeToFirstTokenSeconds, Is.Null);
        Assert.That(source.Turns[0].ModelMessage.GenerationSeconds, Is.Null);
        Assert.That(source.Turns[0].ModelMessage.TokensPerSecond, Is.Null);
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

    private static LmStudioAgentClient NewClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        return new LmStudioAgentClient(httpClient, "test-model", NullLogger<LmStudioAgentClient>.Instance);
    }

    /// <summary>Answers every chat request with a fresh stream from the factory; the models endpoint is 404.</summary>
    private sealed class StreamHandler(Func<Stream> streamFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/v0/models", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(streamFactory()),
            });
        }
    }

    /// <summary>Serves the first text, waits, then serves the second, so the client sees a real gap between the first generation event and response.completed.</summary>
    private sealed class TwoPartDelayedStream(string first, TimeSpan delay, string second) : Stream
    {
        private readonly Queue<byte[]> _parts = new([Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second)]);
        private byte[]? _current;
        private int _offset;
        private bool _delayed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset >= _current.Length)
            {
                if (_parts.Count == 0)
                {
                    return 0;
                }

                if (_current is not null && !_delayed)
                {
                    _delayed = true;
                    await Task.Delay(delay, cancellationToken);
                }

                _current = _parts.Dequeue();
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
