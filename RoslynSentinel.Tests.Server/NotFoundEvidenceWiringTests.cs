using System.Reflection;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Wiring coverage for the not-found evidence note: a real operation blob on disk, the caller context the call filter
/// sets, and the note reaching both the marshaller (returned errors) and the failure handler (thrown errors).
/// </summary>
[TestFixture]
public class NotFoundEvidenceWiringTests
{
    private static readonly MethodInfo AnyMethod = typeof(NotFoundEvidenceWiringTests).GetMethod(nameof(AnyMethodTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    private string _root = null!;

    private static void AnyMethodTarget()
    {
    }

    [SetUp]
    public async Task SetUp()
    {
        ToolCallContext.Set(null);
        _root = Path.Combine(Path.GetTempPath(), "rs-notfound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        ToolCallContext.Set(new ToolCallContext("b7", "implementer", "RenameSymbol", "drop Bar", 0) { SessionId = "s1" });
        await OperationBlobWriter.WriteAsync(
            "RenameSymbol",
            Guid.NewGuid().ToString("N")[..8],
            [new OperationItemRecord { FilePath = Path.Combine(_root, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = "class Foo { void Bar() { } }" }],
            _root);
        ToolCallContext.Set(null);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void SetCaller(string? agentId = "main") =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "GetMethodSource", null, 0)
        {
            SessionId = "s1",
            TargetFilePath = "Foo.cs",
            MissingTerm = "Bar",
            SolutionRoot = _root,
        });

    private static JsonObject BodyOf(object? marshalled) => JsonNode.Parse((string)marshalled!)!.AsObject();

    private static string TextOf(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Test]
    public async Task Marshaller_ReturnedNotFound_CarriesNote()
    {
        SetCaller();
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance);
        var result = new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError("MethodNotFound", "missing") };

        object? marshalled = await marshaller.MarshalAsync(
            "GetMethodSource", AnyMethod, result, typeof(object), McpJsonUtilities.DefaultOptions, TestContext.CurrentContext.CancellationToken);

        JsonObject body = BodyOf(marshalled);
        Assert.That(body["findings"]![0]!["message"]!.GetValue<string>(), Does.Contain("`b7`"));
        Assert.That(body["errorData"]!["errorCode"]!.GetValue<string>(), Is.EqualTo("MethodNotFound"));
        Assert.That(body["errorData"]!.AsObject().ContainsKey("findings"), Is.False);
    }

    [Test]
    public async Task Marshaller_OtherError_Unchanged()
    {
        SetCaller();
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance);
        var result = new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError("InvalidArgument", "bad") };

        object? marshalled = await marshaller.MarshalAsync(
            "GetMethodSource", AnyMethod, result, typeof(object), McpJsonUtilities.DefaultOptions, TestContext.CurrentContext.CancellationToken);

        Assert.That(BodyOf(marshalled)["findings"]!.AsArray(), Is.Empty);
    }

    [Test]
    public async Task Marshaller_NotFoundWithoutAgentId_Unchanged()
    {
        SetCaller(agentId: null);
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance);
        var result = new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError("MethodNotFound", "missing") };

        object? marshalled = await marshaller.MarshalAsync(
            "GetMethodSource", AnyMethod, result, typeof(object), McpJsonUtilities.DefaultOptions, TestContext.CurrentContext.CancellationToken);

        Assert.That(BodyOf(marshalled)["findings"]!.AsArray(), Is.Empty);
    }

    [Test]
    public async Task Marshaller_Success_Unchanged()
    {
        SetCaller();
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance);
        var result = new SentinelCallToolResult<object> { IsError = false, SuccessData = "ok" };

        object? marshalled = await marshaller.MarshalAsync(
            "GetMethodSource", AnyMethod, result, typeof(object), McpJsonUtilities.DefaultOptions, TestContext.CurrentContext.CancellationToken);

        Assert.That(BodyOf(marshalled)["findings"]!.AsArray(), Is.Empty);
    }

    [Test]
    public void FailureHandler_ThrownNotFound_CarriesNote()
    {
        SetCaller();

        CallToolResult result = CallToolFailureHandler.ToFailureResult(new ToolNotFoundException("no match"), "ReplaceSnippet");

        JsonObject body = JsonNode.Parse(TextOf(result))!.AsObject();
        Assert.That(body["findings"]![0]!["message"]!.GetValue<string>(), Does.Contain("`b7`"));
        Assert.That(body["errorData"]!["errorCode"]!.GetValue<string>(), Is.EqualTo("NotFound"));
    }

    [Test]
    public void FailureHandler_OtherError_Unchanged()
    {
        SetCaller();

        CallToolResult result = CallToolFailureHandler.ToFailureResult(new InvalidOperationException("boom"), "ReplaceSnippet");

        JsonObject body = JsonNode.Parse(TextOf(result))!.AsObject();
        Assert.That(body["findings"]!.AsArray(), Is.Empty);
    }
}
