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
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/v0/models", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            ChatRequests++;
            return Task.FromResult(respond(request));
        }
    }
}
