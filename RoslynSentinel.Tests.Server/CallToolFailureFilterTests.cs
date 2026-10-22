using System.IO.Pipelines;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common.SnippetMatching;
using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the central call-tool exception filter (<c>AddWorkspaceNotLoadedFilter</c>): an exception
/// that escapes a tool becomes a structured failure body (<c>isError</c> + <c>errorData</c>) built by
/// <see cref="ToolErrorMapper"/> instead of plain text. The mapping lives in
/// <see cref="CallToolFailureHandler"/>, which the filter delegates to, so most cases drive that handler
/// directly (no host needed, and a cancelled request token is easy to supply); one case runs the real filter
/// pipeline in position through an in-process client/server pair.
/// </summary>
[TestFixture]
public class CallToolFailureFilterTests
{
    private static async Task<CallToolResult> RunAsync(Exception thrown, CancellationToken requestToken = default) =>
        await CallToolFailureHandler.InvokeAsync(
            () => ValueTask.FromException<CallToolResult>(thrown), "FailureProbe", requestToken);

    private static string TextOf(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static JsonObject BodyOf(CallToolResult result) => JsonNode.Parse(TextOf(result))!.AsObject();

    private static JsonObject ErrorDataOf(CallToolResult result) => BodyOf(result)["errorData"]!.AsObject();

    private static SnippetFailureDetail SnippetDetail() => new(
        Outcome: "NoMatch",
        RequestedTolerance: "Exact",
        RequestedCaseMode: "Sensitive",
        TiersProbed: ["Exact"],
        ClosestWindows: [],
        ClosestWindowTotalCount: 0,
        NearMatchTotalCount: 0,
        FilterTrail: [],
        CandidatesBeforeSelectors: 0,
        CandidatesAfterSelectors: 0,
        InvalidParameterName: null,
        InvalidParameterValue: null);

    [Test]
    public async Task ToolNotFoundException_WithSnippet_KeepsNotFoundCodeAndStructuredDetail()
    {
        var thrown = new ToolNotFoundException("snippet not found", "snippet", "Foo()", SnippetDetail());

        var result = await RunAsync(thrown);

        Assert.That(result.IsError, Is.True);
        var body = BodyOf(result);
        Assert.That(body["isError"]!.GetValue<bool>(), Is.True);
        var errorData = body["errorData"]!.AsObject();
        Assert.That(errorData["errorCode"]!.GetValue<string>(), Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(errorData["message"]!.GetValue<string>(), Does.Contain("FailureProbe").And.Contain("snippet not found"));
        var structuredDetail = errorData["structuredDetail"]!.AsArray();
        Assert.That(structuredDetail, Has.Count.EqualTo(1));
        Assert.That(structuredDetail[0]!["outcome"]!.GetValue<string>(), Is.EqualTo("NoMatch"));
    }

    [Test]
    public async Task ToolAmbiguousMatchException_WithCandidates_CarriesThemInStructuredDetail()
    {
        var candidates = new[]
        {
            new AmbiguousCandidate("A.cs", 10, "method", "void Run()", "A", "void Run()", null, true),
            new AmbiguousCandidate("B.cs", 20, "method", "void Run()", "B", "void Run()", null, true),
        };

        var result = await RunAsync(new ToolAmbiguousMatchException("two matches", candidates));

        Assert.That(result.IsError, Is.True);
        var errorData = ErrorDataOf(result);
        Assert.That(errorData["errorCode"]!.GetValue<string>(), Is.EqualTo(ToolErrorCode.Ambiguous));
        var structuredDetail = errorData["structuredDetail"]!.AsArray();
        Assert.That(structuredDetail, Has.Count.EqualTo(2));
        Assert.That(structuredDetail[0]!["filePath"]!.GetValue<string>(), Is.EqualTo("A.cs"));
        Assert.That(structuredDetail[1]!["line"]!.GetValue<int>(), Is.EqualTo(20));
    }

    [Test]
    public async Task UnexpectedException_BecomesExceptionCode_AndMessageNamesTheType()
    {
        var result = await RunAsync(new InvalidOperationException("boom"));

        Assert.That(result.IsError, Is.True);
        var errorData = ErrorDataOf(result);
        Assert.That(errorData["errorCode"]!.GetValue<string>(), Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(errorData["message"]!.GetValue<string>(), Does.Contain("InvalidOperationException").And.Contain("boom"));
    }

    [Test]
    public async Task SolutionNotLoadedException_KeepsCode_AndTheCallLoadSolutionSentence()
    {
        var result = await RunAsync(new SolutionNotLoadedException());

        Assert.That(result.IsError, Is.True);
        var errorData = ErrorDataOf(result);
        Assert.That(errorData["errorCode"]!.GetValue<string>(), Is.EqualTo(ToolErrorCode.SolutionNotLoaded));
        Assert.That(errorData["message"]!.GetValue<string>(), Does.Contain(SolutionNotLoadedMessage.Plain));
        Assert.That(TextOf(result), Does.Contain("Call LoadSolution"));
    }

    [Test]
    public async Task OperationCanceled_WithCancelledRequestToken_IsRethrownUnchanged()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var thrown = new OperationCanceledException(cts.Token);

        var caught = await Assert.ThrowsAsync<OperationCanceledException>(async () => await RunAsync(thrown, cts.Token));

        Assert.That(caught, Is.SameAs(thrown));
    }

    [Test]
    public async Task OperationCanceled_WithLiveRequestToken_BecomesCancelledError()
    {
        var result = await RunAsync(new OperationCanceledException("internal cancel"), CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(ErrorDataOf(result)["errorCode"]!.GetValue<string>(), Is.EqualTo(ToolErrorCode.Cancelled));
    }

    [Test]
    public async Task TimeoutException_BecomesTimedOutError()
    {
        var result = await RunAsync(new TimeoutException("too slow"));

        Assert.That(result.IsError, Is.True);
        Assert.That(ErrorDataOf(result)["errorCode"]!.GetValue<string>(), Is.EqualTo(ToolErrorCode.TimedOut));
    }

    [Test]
    public async Task SuccessfulCall_IsReturnedUntouched()
    {
        var expected = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };

        var result = await CallToolFailureHandler.InvokeAsync(() => ValueTask.FromResult(expected), "FailureProbe", CancellationToken.None);

        Assert.That(result, Is.SameAs(expected));
    }

    [Test]
    public async Task ThrowingTool_ThroughTheRealFilterPipeline_ReturnsStructuredFailure()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Workspace" });
        mcpBuilder.WithTools<FailureProbeTool>();

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        using var host = hostBuilder.Build();
        _ = host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);

        var result = await client.CallToolAsync(
            "FailureProbe",
            new Dictionary<string, object?>(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.True);
        var errorData = ErrorDataOf(result);
        Assert.That(errorData["errorCode"]!.GetValue<string>(), Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(errorData["message"]!.GetValue<string>(), Does.Contain("InvalidOperationException").And.Contain("probe failure"));

        await host.StopAsync();
    }
}
/// <summary>
/// Test-only tool that throws instead of returning a result, so the central exception filter is
/// exercised in position (every real tool catches its own exceptions).
/// </summary>
[McpServerToolType]
public class FailureProbeTool
{
    [McpServerTool(Name = "FailureProbe")]
    [System.ComponentModel.Description("Test-only probe that always throws an InvalidOperationException.")]
    public string FailureProbe() => throw new InvalidOperationException("probe failure");
}
