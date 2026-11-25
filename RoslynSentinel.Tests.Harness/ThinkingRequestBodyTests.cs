using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Wire shape of <see cref="LmStudioAgentClient.CompleteAsync"/> with a thinking setting: the request body carries
/// <c>reasoning.effort</c> only when thinking is passed, and the streamed reasoning deltas are counted on the returned
/// message. A recording handler stands in for LM Studio. No live model.
/// </summary>
[TestFixture]
public class ThinkingRequestBodyTests
{
    [Test]
    public async Task CompleteAsync_WithoutThinking_HasNoReasoningKey()
    {
        var handler = new ThinkingScriptedHandler(reasoningDeltas: 0);
        var client = NewClient(handler);

        await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256);

        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.That(body.RootElement.TryGetProperty("reasoning", out _), Is.False);
    }

    [Test]
    public async Task CompleteAsync_WithThinking_SendsEffort()
    {
        var handler = new ThinkingScriptedHandler(reasoningDeltas: 0);
        var client = NewClient(handler);
        var options = new ThinkingOptions(ThinkingMode.Triggered, ThinkingTriggers.Error, "medium", null);

        await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256,
            thinking: options);

        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.That(body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString(), Is.EqualTo(options.Effort));
    }

    [Test]
    public async Task CompleteAsync_CountsReasoningDeltas()
    {
        var withDeltas = await CompleteWithReasoningDeltasAsync(3);
        var withoutDeltas = await CompleteWithReasoningDeltasAsync(0);

        Assert.That(withDeltas.ReasoningDeltaCount, Is.EqualTo(3));
        Assert.That(withoutDeltas.ReasoningDeltaCount, Is.Null);
    }

    private static async Task<AgentChatMessage> CompleteWithReasoningDeltasAsync(int reasoningDeltas)
    {
        var handler = new ThinkingScriptedHandler(reasoningDeltas);
        var client = NewClient(handler);

        return await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256,
            thinking: new ThinkingOptions(ThinkingMode.Always, ThinkingTriggers.None, "low", null));
    }

    private static HttpResponseMessage SseResponse(params string[] events) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(events), Encoding.UTF8, "text/event-stream"),
        };

    private static string ReasoningDeltaEvent(int index) =>
        "event: response.reasoning_text.delta\ndata: " +
        JsonSerializer.Serialize(new { item_id = "rs_0", delta = "step " + index + " " }) + "\n\n";

    private static string CompletedMessageEvent(string text) =>
        "event: response.completed\n" +
        "data: {\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"" +
        text + "\"}]}]}}\n\n";

    private static LmStudioAgentClient NewClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        return new LmStudioAgentClient(httpClient, "test-model", NullLogger<LmStudioAgentClient>.Instance);
    }

    /// <summary>Records each chat request body and answers with N reasoning deltas, then a message and completion; the models endpoint is 404 and not recorded.</summary>
    private sealed class ThinkingScriptedHandler(int reasoningDeltas) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post ||
                !request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            var events = new List<string>();
            for (var i = 1; i <= reasoningDeltas; i++)
            {
                events.Add(ReasoningDeltaEvent(i));
            }

            events.Add(CompletedMessageEvent("done"));
            return SseResponse([.. events]);
        }
    }
}
