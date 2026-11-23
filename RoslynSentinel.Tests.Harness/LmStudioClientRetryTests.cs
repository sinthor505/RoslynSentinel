using System.IO;
using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Retry behaviour of <see cref="LmStudioAgentClient.CompleteAsync"/>: a context-size overflow (HTTP 500 with
/// "Context size has been exceeded") is thrown as <see cref="ContextSizeExceededException"/> and never retried,
/// while other HTTP 500 errors and transient IO failures get exactly one retry. No live model.
/// </summary>
[TestFixture]
public class LmStudioClientRetryTests
{
    private const string ContextExceededBody = "{\"error\":\"Context size has been exceeded.\"}";

    [Test]
    public async Task CompleteAsync_ContextSizeExceeded_ThrowsWithoutRetry()
    {
        var handler = new CountingHandler(_ => ErrorResponse(ContextExceededBody));
        var client = NewClient(handler);

        await Assert.ThrowsAsync<ContextSizeExceededException>(() => client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256));

        Assert.That(handler.ChatRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task CompleteAsync_OtherHttp500_ThrowsInvalidOperationExceptionAfterOneRetry()
    {
        var handler = new CountingHandler(_ => ErrorResponse("boom"));
        var client = NewClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256));

        Assert.That(handler.ChatRequests, Is.EqualTo(2));
    }

    [Test]
    public async Task CompleteAsync_IOExceptionOnEveryRequest_ThrowsIOExceptionAfterOneRetry()
    {
        var handler = new CountingHandler(_ => throw new IOException("connection reset"));
        var client = NewClient(handler);

        await Assert.ThrowsAsync<IOException>(() => client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256));

        Assert.That(handler.ChatRequests, Is.EqualTo(2));
    }

    private const string PlaceholderArguments = "{\"reason\":\"...\",\"filePath\":\"...\"}";

    private const string RealArguments = "{\"filePath\":\"a.cs\"}";

    [Test]
    public async Task PlaceholderToolCall_RetriesOnce_ThenThrowsStreamRunaway()
    {
        var handler = new CountingHandler(_ => SseResponse(FunctionCallEvent("ReadFile", PlaceholderArguments)));
        var client = NewClient(handler);

        await Assert.ThrowsAsync<StreamRunawayException>(() => client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256));

        Assert.That(handler.ChatRequests, Is.EqualTo(2));
    }

    [Test]
    public async Task ThreeIdenticalToolCalls_ThenGoodResponse_ReturnsGoodResponse()
    {
        var attempt = 0;
        var handler = new CountingHandler(_ => ++attempt == 1
            ? SseResponse(
                FunctionCallEvent("ReadFile", RealArguments),
                FunctionCallEvent("ReadFile", RealArguments),
                FunctionCallEvent("ReadFile", RealArguments))
            : SseResponse(CompletedMessageEvent("done")));
        var client = NewClient(handler);

        var result = await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256);

        Assert.That(result.Content, Is.EqualTo("done"));
        Assert.That(handler.ChatRequests, Is.EqualTo(2));
    }

    [Test]
    public async Task TwoIdenticalToolCalls_AreNotARunaway()
    {
        var handler = new CountingHandler(_ => SseResponse(
            FunctionCallEvent("ReadFile", RealArguments),
            FunctionCallEvent("ReadFile", RealArguments),
            CompletedMessageEvent("done")));
        var client = NewClient(handler);

        var result = await client.CompleteAsync(
            [new AgentChatMessage { Role = "user", Content = "hi" }],
            [],
            256);

        Assert.That(result.Content, Is.EqualTo("done"));
        Assert.That(handler.ChatRequests, Is.EqualTo(1));
    }

    private static HttpResponseMessage SseResponse(params string[] events) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(events), Encoding.UTF8, "text/event-stream"),
        };

    private static string FunctionCallEvent(string name, string argumentsJson) =>
        "event: response.output_item.done\n" +
        "data: {\"item\":{\"type\":\"function_call\",\"call_id\":\"call\",\"name\":\"" + name +
        "\",\"arguments\":\"" + argumentsJson.Replace("\"", "\\\"") + "\"}}\n\n";

    private static string CompletedMessageEvent(string text) =>
        "event: response.completed\n" +
        "data: {\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"" +
        text + "\"}]}]}}\n\n";

    private static HttpResponseMessage ErrorResponse(string body) =>
        new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static LmStudioAgentClient NewClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/v1/"),
        };
        return new LmStudioAgentClient(httpClient, "test-model", NullLogger<LmStudioAgentClient>.Instance);
    }

    /// <summary>Counts chat requests and answers each with the factory's response; the models endpoint is 404 and not counted.</summary>
    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int ChatRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post ||
                !request.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            ChatRequests++;
            return Task.FromResult(respond(request));
        }
    }
}
