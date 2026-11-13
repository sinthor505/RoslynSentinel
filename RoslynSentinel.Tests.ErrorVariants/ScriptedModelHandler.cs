// DTO-EXEMPT: test double (HttpMessageHandler), not a data type.
// Deliberate copy of the private nested ScriptedHandler in RoslynSentinel.Tests.ModelEval/ToolResultTrailingBlockRunnerTests.cs
// because that project is not referenceable from here. TODO unify if ModelEval's test doubles ever move to a shared project.

using System.Net;
using System.Text;
using System.Text.Json;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// Scripted LM Studio stand-in: serves the queued responses in order (the last one repeats) and records every POST body.
/// The loaded-model status check (GET) is answered without being recorded or consuming a response.
/// </summary>
internal sealed class ScriptedModelHandler(params string[] responses) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responses);

    public List<string> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The client's loaded-model status check is a GET sent before the first POST: answer it without recording it
        // or consuming a scripted response. Only POSTs are model requests.
        if (request.Method == HttpMethod.Get)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
            };
        }

        Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        var next = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(next, Encoding.UTF8, "text/event-stream"),
        };
    }

    /// <summary>One response.completed event whose output is a single function_call (a fresh call id each time).</summary>
    internal static string FunctionCallSse(string toolName, string argumentsJson)
    {
        var payload = JsonSerializer.Serialize(new
        {
            response = new
            {
                output = new[]
                {
                    new
                    {
                        type = "function_call",
                        call_id = "call_" + Guid.NewGuid().ToString("N")[..8],
                        name = toolName,
                        arguments = argumentsJson,
                    },
                },
            },
        });

        return $"event: response.completed\ndata: {payload}\n\n";
    }

    /// <summary>One response.completed event carrying a plain assistant message and no tool call.</summary>
    internal static string FinalMessageSse(string text)
    {
        var payload = JsonSerializer.Serialize(new
        {
            response = new
            {
                output = new[]
                {
                    new
                    {
                        type = "message",
                        content = new[] { new { type = "output_text", text } },
                    },
                },
            },
        });

        return $"event: response.completed\ndata: {payload}\n\n";
    }

    /// <summary>The output of the single function_call_output item in a recorded request body: the tool result the model read.</summary>
    internal static string ToolOutput(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("input")
            .EnumerateArray()
            .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output")
            .Single()
            .GetProperty("output")
            .GetString()!;
    }
}
