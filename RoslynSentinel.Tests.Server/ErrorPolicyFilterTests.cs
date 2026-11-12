using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the error-policy call-tool filter (<see cref="ErrorPolicyFilter"/>, registered by
/// <c>AddErrorPolicyFilter</c> when the mode is not Off). Part A drives the filter logic directly; Part B runs the
/// real filter pipeline in position through an in-process client/server pair with two test-only probe tools.
/// </summary>
// ErrorPolicyOptions and ToolCallContext are static process state, so this fixture cannot run in parallel.
[NonParallelizable]
[TestFixture]
public class ErrorPolicyFilterTests
{
    private const string FourSections =
        "Preamble ignored.\n" +
        "### FileNotFound.Lookup\nInline lookup guidance for files.\n" +
        "### FileNotFound.Edit\nInline edit guidance for files.\n" +
        "### SymbolNotFound.Lookup\nInline lookup guidance for symbols.\n" +
        "### SymbolNotFound.Edit\nInline edit guidance for symbols.\n";

    private static readonly Regex ToolCallIdPattern = new("\"toolCallId\":\"[^\"]*\"", RegexOptions.CultureInvariant);

    [TearDown]
    public void TearDown()
    {
        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=off" });
        ToolCallContext.Set(null);
    }

    // ---- Part A helpers -------------------------------------------------------------------------------------

    /// <summary>A failure result shaped like a tool's own error body; CallToolResult.IsError is left null.</summary>
    private static CallToolResult ErrorResult(string errorCode) => new()
    {
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(
                    new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(errorCode, "some message") },
                    McpJsonUtilities.DefaultOptions),
            },
        ],
    };

    private static Dictionary<string, JsonElement> Args(string? mode)
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (mode is not null)
        {
            arguments["mode"] = StringElement(mode);
        }

        return arguments;
    }

    private static JsonElement StringElement(string value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }

    /// <summary>Sets the ambient call context (with the missing-target kind when one is listed), then runs the filter over <paramref name="input"/>.</summary>
    private static async Task<CallToolResult> InvokeAsync(
        ErrorPolicyMode mode,
        ErrorPolicyDocument? document,
        string toolName,
        Dictionary<string, JsonElement> arguments,
        CallToolResult input,
        string? missingKind = null)
    {
        var ctx = new ToolCallContext(null, null, toolName, null, 0);
        ctx.RecordMissingTarget(missingKind);
        ToolCallContext.Set(ctx);

        return await ErrorPolicyFilter.InvokeAsync(mode, document, toolName, arguments, null, () => ValueTask.FromResult(input));
    }

    private static List<string> TextBlocks(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Select(b => b.Text).ToList();

    // ---- Part A: logic-level tests ----------------------------------------------------------------------------

    [TestCase(ErrorPolicyMode.Pointer, ErrorPolicyKey.FileNotFoundLookup, "ReadFile", "FileNotFound", null, null)]
    [TestCase(ErrorPolicyMode.Pointer, ErrorPolicyKey.FileNotFoundEdit, "ReplaceSnippet", ToolErrorCode.NotFound, "document", null)]
    [TestCase(ErrorPolicyMode.Pointer, ErrorPolicyKey.SymbolNotFoundLookup, "Search", ToolErrorCode.NotFound, "symbol", "symbol")]
    [TestCase(ErrorPolicyMode.Pointer, ErrorPolicyKey.SymbolNotFoundEdit, "RenameSymbol", ToolErrorCode.NotFound, "method", null)]
    [TestCase(ErrorPolicyMode.Inline, ErrorPolicyKey.FileNotFoundLookup, "ReadFile", "FileNotFound", null, null)]
    [TestCase(ErrorPolicyMode.Inline, ErrorPolicyKey.FileNotFoundEdit, "ReplaceSnippet", ToolErrorCode.NotFound, "document", null)]
    [TestCase(ErrorPolicyMode.Inline, ErrorPolicyKey.SymbolNotFoundLookup, "Search", ToolErrorCode.NotFound, "symbol", "symbol")]
    [TestCase(ErrorPolicyMode.Inline, ErrorPolicyKey.SymbolNotFoundEdit, "RenameSymbol", ToolErrorCode.NotFound, "method", null)]
    public async Task NotFoundError_ClassifiesToKey_AndAppendsOneTrailingBlock(
        ErrorPolicyMode mode,
        ErrorPolicyKey key,
        string toolName,
        string errorCode,
        string? missingKind,
        string? searchMode)
    {
        var arguments = Args(searchMode);
        var classified = ErrorPolicyClassifier.Classify(toolName, arguments, errorCode, missingKind);
        Assert.That(classified, Is.EqualTo(key), "The intent table and classifier must map this call to the expected key.");

        var document = mode == ErrorPolicyMode.Inline ? ErrorPolicyDocument.Parse(FourSections) : null;
        var input = ErrorResult(errorCode);
        var inputText = TextBlocks(input).Single();

        var result = await InvokeAsync(mode, document, toolName, arguments, input, missingKind);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Content, Has.Count.EqualTo(2));
        var texts = TextBlocks(result);
        Assert.That(texts[0], Is.EqualTo(inputText), "The first block must never be modified.");

        if (mode == ErrorPolicyMode.Pointer)
        {
            Assert.That(texts[1], Is.EqualTo(ErrorPolicyFilter.PointerText(key)));
            Assert.That(texts[1], Does.Contain("Error handling: " + ErrorPolicyKeys.ToWireName(key)));
        }
        else
        {
            Assert.That(texts[1], Is.EqualTo(document!.Entries[key]));
        }
    }

    [Test]
    public async Task OffMode_NotFoundError_IsUnchanged()
    {
        var input = ErrorResult("FileNotFound");

        var result = await InvokeAsync(ErrorPolicyMode.Off, null, "ReadFile", Args(null), input);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Content, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SuccessResult_IsUnchanged()
    {
        var input = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }], IsError = false };

        var result = await InvokeAsync(ErrorPolicyMode.Pointer, null, "ReadFile", Args(null), input);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Content, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task NonNotFoundError_IsUnchanged()
    {
        var input = ErrorResult(ToolErrorCode.InvalidArgument);

        var result = await InvokeAsync(ErrorPolicyMode.Pointer, null, "ReadFile", Args(null), input);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Content, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task UnclassifiedNotFound_IsUnchanged()
    {
        // FailureProbe is not in the intent table and carries no missing-target kind, so no key applies.
        var input = ErrorResult(ToolErrorCode.NotFound);

        var result = await InvokeAsync(ErrorPolicyMode.Pointer, null, "FailureProbe", Args(null), input);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Content, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task InlineMode_DocumentLacksTheKey_IsUnchanged()
    {
        // RenameSymbol with kind "method" classifies to SymbolNotFound.Edit, which this document does not contain.
        var document = ErrorPolicyDocument.Parse("### FileNotFound.Lookup\nOnly this section is present.\n");
        var input = ErrorResult(ToolErrorCode.NotFound);

        var result = await InvokeAsync(ErrorPolicyMode.Inline, document, "RenameSymbol", Args(null), input, "method");

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Content, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task NonJsonErrorBody_IsUnchanged()
    {
        var input = new CallToolResult { Content = [new TextContentBlock { Text = "boom" }], IsError = true };

        var result = await InvokeAsync(ErrorPolicyMode.Pointer, null, "ReadFile", Args(null), input);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Content, Has.Count.EqualTo(1));
    }

    // ---- Part B: real filter pipeline through an in-process client/server pair --------------------------------

    /// <summary>
    /// Starts an in-process server with the error-policy mode set through the startup switch, the two probe tools and
    /// no other tool classes (so the real ReadFile and RenameSymbol are not registered), runs <paramref name="body"/>
    /// against a connected client, then stops the host.
    /// </summary>
    private static async Task RunPipelineAsync(string mode, Func<McpClient, Task> body)
    {
        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=" + mode });

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        mcpBuilder.WithTools<ReadFileProbeTool>();
        mcpBuilder.WithTools<RenameSymbolProbeTool>();

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

        try
        {
            await body(client);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<CallToolResult> CallProbeAsync(string mode, string toolName)
    {
        CallToolResult? captured = null;
        await RunPipelineAsync(mode, async client =>
        {
            // A one-time per-process stamp (ReplaceSnippetLimits) rides on the first response, so a warm-up call
            // makes Off and Pointer comparable regardless of test order. The warm-up result is discarded.
            _ = await client.CallToolAsync(
                toolName,
                new Dictionary<string, object?>(),
                cancellationToken: TestContext.CurrentContext.CancellationToken);

            captured = await client.CallToolAsync(
                toolName,
                new Dictionary<string, object?>(),
                cancellationToken: TestContext.CurrentContext.CancellationToken);
        });

        return captured!;
    }

    private static string NormalizeCallIds(string text) =>
        ToolCallIdPattern.Replace(text, "\"toolCallId\":\"<normalized>\"");

    [Test]
    public async Task Pointer_RenameSymbolMissingSymbol_AppendsSymbolNotFoundEditPointer()
    {
        // The kind ("method") is recorded by ToolErrorMapper.ToResultError inside the call, when the probe throws and
        // the mapping runs; the outer ErrorPolicy filter reads the same ToolCallContext after the call returns.
        var result = await CallProbeAsync("pointer", "RenameSymbol");

        Assert.That(result.IsError, Is.True);
        var texts = TextBlocks(result);
        Assert.That(texts, Has.Count.EqualTo(2));
        Assert.That(texts[1], Is.EqualTo(ErrorPolicyFilter.PointerText(ErrorPolicyKey.SymbolNotFoundEdit)));
    }

    [Test]
    public async Task Pointer_ReadFileMissingFile_AppendsFileNotFoundLookupPointer()
    {
        var result = await CallProbeAsync("pointer", "ReadFile");

        Assert.That(result.IsError, Is.True);
        var texts = TextBlocks(result);
        Assert.That(texts, Has.Count.EqualTo(2));
        Assert.That(texts[1], Is.EqualTo(ErrorPolicyFilter.PointerText(ErrorPolicyKey.FileNotFoundLookup)));
    }

    [TestCase("ReadFile")]
    [TestCase("RenameSymbol")]
    public async Task OffMode_ResponseMatchesPointerModeFirstBlock(string toolName)
    {
        // Normalised fields: the echo's per-call toolCallId (random per call). The echo stamps nothing else per call.
        var off = await CallProbeAsync("off", toolName);
        var pointer = await CallProbeAsync("pointer", toolName);

        var offTexts = TextBlocks(off);
        Assert.That(offTexts, Has.Count.EqualTo(1), "Off mode must add no trailing block.");
        var pointerTexts = TextBlocks(pointer);
        Assert.That(pointerTexts, Has.Count.EqualTo(2));
        var normalizedOff = NormalizeCallIds(offTexts[0]);
        var normalizedPointer = NormalizeCallIds(pointerTexts[0]);
        Assert.That(normalizedOff, Is.EqualTo(normalizedPointer), $"off=[{normalizedOff}] pointer=[{normalizedPointer}]");
    }

    [Test]
    public void FilterRegistration_IsAbsentInOffMode_AndPresentInPointerMode()
    {
        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=off" });
        Assert.That(RoslynSentinelServiceExtensionsBasic.CallToolFilterNames, Does.Not.Contain("ErrorPolicy"));

        ErrorPolicyOptions.Configure(new[] { "--error-policy-mode=pointer" });
        Assert.That(RoslynSentinelServiceExtensionsBasic.CallToolFilterNames, Does.Contain("ErrorPolicy"));
    }
}

/// <summary>
/// Test-only probe named ReadFile. It returns a FileNotFound failure through the normal return path, the same
/// return type the real ReadFile tool uses, so the error-policy filter sees a structured not-found body.
/// </summary>
[McpServerToolType]
public class ReadFileProbeTool
{
    [McpServerTool(Name = "ReadFile")]
    [System.ComponentModel.Description("Test-only probe that always returns a FileNotFound failure.")]
    public Task<SentinelCallToolResult<object>> ReadFileProbe() => Task.FromResult(
        new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError("FileNotFound", "ReadFile failed: probe file is missing"),
        });
}

/// <summary>
/// Test-only probe named RenameSymbol. It throws a not-found with kind "method" so the exception is mapped inside the
/// call (ToolErrorMapper records the kind), in position behind the error-policy filter.
/// </summary>
[McpServerToolType]
public class RenameSymbolProbeTool
{
    [McpServerTool(Name = "RenameSymbol")]
    [System.ComponentModel.Description("Test-only probe that always throws a method not-found.")]
    public string RenameSymbolProbe() => throw new ToolNotFoundException("probe symbol missing", "method", "Foo");
}
