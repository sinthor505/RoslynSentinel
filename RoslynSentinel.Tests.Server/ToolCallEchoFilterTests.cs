using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the outermost call-tool filter that stamps a <c>toolCall</c> echo (call id, tool
/// name, truncated arguments as sent) onto every response - see
/// docs/current/plans/plan_tool_call_echo.md. Runs against a real in-process
/// <see cref="McpClient"/>/<see cref="ModelContextProtocol.Server.McpServer"/> pair (the same pattern
/// as <see cref="LargeResultOffloadFilterTests"/>) because the point is that the stamp survives every
/// other filter in the chain, including the ones that replace the whole response body.
/// <para>
/// <see cref="NonParallelizableAttribute"/>: some tests flip the process-wide
/// <see cref="ToolCallEchoOptions.Enabled"/> flag to capture an un-stamped baseline, and
/// <see cref="TearDown"/> restores it.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class ToolCallEchoFilterTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    // Enough distinct callers of one target that FindReferences(kind: callers) exceeds the
    // 15 KB generic offload threshold on its own (same recipe as LargeResultOffloadFilterTests).
    private const int OffloadCallerCount = 400;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();

        var callers = Enumerable.Range(0, OffloadCallerCount)
            .Select(i => $"    public void EchoOffloadCaller{i}() {{ EchoOffloadTarget(); }}");
        var source = "namespace ContosoOrders.Core;" + Environment.NewLine +
                     "public class ToolCallEchoProbe" + Environment.NewLine +
                     "{" + Environment.NewLine +
                     "    public void EchoLone() { }" + Environment.NewLine +
                     "    public void EchoLoneCaller() { EchoLone(); }" + Environment.NewLine +
                     "    public void EchoOffloadTarget() { }" + Environment.NewLine +
                     string.Join(Environment.NewLine, callers) + Environment.NewLine +
                     "}" + Environment.NewLine;

        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = _fixture.SolutionPath })
        {
            await _fixture.AddFileToSolution(
                seedWorkspaceManager,
                Path.Combine("ContosoOrders.Core", "ToolCallEchoProbe.cs"),
                source,
                reloadSolution: false);
        }

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);
        // A tool that returns a plain string (not a JSON envelope), to exercise the wrap path.
        mcpBuilder.WithTools<CaseCollisionProbeTool>();

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);

        ToolCallEchoOptions.Configure(["--echo-tool-args=true"]);

        var loadResult = await _client.CallToolAsync(
            "LoadSolution",
            new Dictionary<string, object?> { ["reason"] = "test message", ["solutionPath"] = _fixture.SolutionPath }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load - cannot exercise the filter without a loaded solution.");
    }

    [TearDown]
    public async Task TearDown()
    {
        // Static state: always leave the echo on for whatever test runs next.
        ToolCallEchoOptions.Configure(["--echo-tool-args=true"]);

        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
    }

    private async Task<(CallToolResult Result, string Text)> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(
            tool,
            args!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return (result, text);
    }

    private static Dictionary<string, object?> FindEchoLoneArgs() => new()
    {
        ["reason"] = "test message",
        ["symbolName"] = "EchoLone",
        ["kind"] = "callers",
    };

    [Test]
    public async Task SuccessfulEnvelopeResponse_ToolCallIsFirstProperty_AndRestOfBodyIsUnchanged()
    {
        var (stamped, stampedText) = await CallAsync("FindReferences", FindEchoLoneArgs());
        Assert.That(stamped.IsError, Is.Not.True, $"The lookup itself must succeed. Got: {stampedText}");

        var stampedBody = JsonNode.Parse(stampedText)!.AsObject();
        Assert.That(stampedBody.First().Key, Is.EqualTo("toolCall"), "toolCall must be the first property of the body.");

        var toolCall = stampedBody["toolCall"]!.AsObject();
        Assert.That(toolCall["name"]!.GetValue<string>(), Is.EqualTo("FindReferences"));
        Assert.That(toolCall["toolCallId"]!.GetValue<string>(), Does.Match("^[0-9a-f]{12}$"));
        var echoedArgs = toolCall["arguments"]!.AsObject();
        Assert.That(echoedArgs["symbolName"]!.GetValue<string>(), Is.EqualTo("EchoLone"));
        Assert.That(echoedArgs["kind"]!.GetValue<string>(), Is.EqualTo("callers"));
        Assert.That(echoedArgs["reason"]!.GetValue<string>(), Is.EqualTo("test message"));

        // Baseline: the very same call with the echo switched off must carry no toolCall at all,
        // and must equal the stamped body once toolCall is removed (nothing else may change).
        ToolCallEchoOptions.Configure(["--echo-tool-args=false"]);
        var (_, plainText) = await CallAsync("FindReferences", FindEchoLoneArgs());
        var plainBody = JsonNode.Parse(plainText)!.AsObject();
        Assert.That(plainBody.ContainsKey("toolCall"), Is.False, "A disabled echo must be a pure pass-through.");

        stampedBody.Remove("toolCall");
        Assert.That(JsonNode.DeepEquals(stampedBody, plainBody), Is.True,
            $"Stamping must leave every other property unchanged.{Environment.NewLine}stamped: {stampedBody.ToJsonString()}{Environment.NewLine}plain:   {plainBody.ToJsonString()}");
    }

    [Test]
    public async Task ArgumentValidationRejection_IsWrapped_MessageVerbatim_IsErrorKept_AndEchoShowsPreNormalizationName()
    {
        // "filepath" (case-only mismatch, silently repaired by the validation filter) plus an
        // unknown parameter, which the validator then rejects. The echo must show "filepath"
        // as sent, not the repaired "filePath".
        var args = FindEchoLoneArgs();
        args["filepath"] = "c:\\somewhere\\Foo.cs";
        args["bogusParam"] = "x";

        var (result, text) = await CallAsync("FindReferences", args);
        Assert.That(result.IsError, Is.True, $"The rejection must still be reported as an error. Got: {text}");

        var body = JsonNode.Parse(text)!.AsObject();
        Assert.That(body.Select(p => p.Key), Is.EqualTo(new[] { "toolCall", "message" }),
            "A plain-text response must be wrapped as {toolCall, message}.");
        var echoedArgs = body["toolCall"]!["arguments"]!.AsObject();
        Assert.That(echoedArgs.ContainsKey("filepath"), Is.True, "The echo must show the argument name as sent.");
        Assert.That(echoedArgs.ContainsKey("filePath"), Is.False, "The echo must be taken before case normalization.");
        Assert.That(echoedArgs.ContainsKey("bogusParam"), Is.True);

        // The message must be the original rejection text, byte for byte.
        var message = body["message"]!.GetValue<string>();
        Assert.That(message, Does.Contain("bogusParam"));
        ToolCallEchoOptions.Configure(["--echo-tool-args=false"]);
        var (plainResult, plainText) = await CallAsync("FindReferences", args);
        Assert.That(plainResult.IsError, Is.True);
        Assert.That(message, Is.EqualTo(plainText), "The wrapped message must equal the un-stamped response text verbatim.");
    }

    [Test]
    public async Task CaseNormalizedCall_EchoShowsParameterNameAsSent()
    {
        var args = FindEchoLoneArgs();
        args["filepath"] = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "ToolCallEchoProbe.cs");

        var (result, text) = await CallAsync("FindReferences", args);
        Assert.That(result.IsError, Is.Not.True, $"A case-only mismatch is repaired and the call succeeds. Got: {text}");

        var echoedArgs = JsonNode.Parse(text)!["toolCall"]!["arguments"]!.AsObject();
        Assert.That(echoedArgs.ContainsKey("filepath"), Is.True, "The echo must show 'filepath' as sent.");
        Assert.That(echoedArgs.ContainsKey("filePath"), Is.False, "The echo must not show the repaired spelling.");
    }

    [Test]
    public async Task OffloadedLargeResult_StubCarriesToolCall()
    {
        var (result, text) = await CallAsync("FindReferences", new Dictionary<string, object?>
        {
            ["reason"] = "test message",
            ["symbolName"] = "EchoOffloadTarget",
            ["kind"] = "callers",
        });
        Assert.That(result.IsError, Is.Not.True);

        var stub = JsonNode.Parse(text)!.AsObject();
        Assert.That(stub["offloaded"]!.GetValue<bool>(), Is.True, "Precondition: the response must have been offloaded.");
        Assert.That(stub.First().Key, Is.EqualTo("toolCall"));
        Assert.That(stub["toolCall"]!["name"]!.GetValue<string>(), Is.EqualTo("FindReferences"));
        Assert.That(stub["toolCall"]!["arguments"]!["symbolName"]!.GetValue<string>(), Is.EqualTo("EchoOffloadTarget"));
    }

    [Test]
    public async Task OffloadedLargeResult_StubRelaysListSummary_AndStatusMessageHasNoFilePaths()
    {
        var (result, text) = await CallAsync("FindReferences", new Dictionary<string, object?>
        {
            ["reason"] = "test message",
            ["symbolName"] = "EchoOffloadTarget",
            ["kind"] = "callers",
        });
        Assert.That(result.IsError, Is.Not.True);

        var stub = JsonNode.Parse(text)!.AsObject();
        Assert.That(stub["offloaded"]!.GetValue<bool>(), Is.True, "Precondition: the response must have been offloaded.");

        // The raw offload backstop must carry the per-file breakdown, since the statusMessage no
        // longer repeats the file paths.
        var listSummary = stub["listSummary"]?.AsObject();
        Assert.That(listSummary, Is.Not.Null, "The stub must relay listSummary.");
        Assert.That(listSummary!["fileCount"]!.GetValue<int>(), Is.GreaterThan(0));
        var byFile = listSummary["byFile"]!.AsArray();
        Assert.That(byFile, Is.Not.Empty);
        Assert.That(byFile[0]!["filePath"]!.GetValue<string>(), Does.Contain("ToolCallEchoProbe.cs"));

        var statusMessage = stub["statusMessage"]!.GetValue<string>();
        Assert.That(statusMessage, Does.Match(@"^\d+ callers? across \d+ files?\.$"));
        Assert.That(statusMessage, Does.Not.Contain("ToolCallEchoProbe.cs"));

        // The pointer's own message embeds quotes around the resultId; it must not be HTML-escaped.
        Assert.That(text, Does.Not.Contain("\\u00"));
    }

    [Test]
    public async Task PlainTextToolResponse_IsWrapped_WithOriginalTextAsMessage()
    {
        // The probe tool declares no "reason" parameter, so none is sent.
        var (result, text) = await CallAsync("CaseCollisionProbe", new Dictionary<string, object?>
        {
            ["id"] = "a",
            ["Id"] = "b",
        });
        Assert.That(result.IsError, Is.Not.True, $"Got: {text}");

        var body = JsonNode.Parse(text)!.AsObject();
        Assert.That(body.Select(p => p.Key), Is.EqualTo(new[] { "toolCall", "message" }));
        Assert.That(body["message"]!.GetValue<string>(), Is.EqualTo("id=a Id=b"));
        Assert.That(body["toolCall"]!["name"]!.GetValue<string>(), Is.EqualTo("CaseCollisionProbe"));
    }
}

/// <summary>
/// Unit coverage for <see cref="ToolCallEcho.CreateEcho"/>'s shape-based truncation rules. No
/// server or workspace involved.
/// </summary>
[TestFixture]
public class ToolCallEchoTruncationTests
{
    private static Dictionary<string, JsonElement> Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static JsonObject EchoArgs(string json) =>
        ToolCallEcho.CreateEcho("0123456789ab", "SomeTool", Args(json))["arguments"]!.AsObject();

    [Test]
    public void CreateEcho_BuildsIdNameAndArguments()
    {
        var echo = ToolCallEcho.CreateEcho("0123456789ab", "SomeTool", Args("{\"a\":1}"));
        Assert.That(echo.Select(p => p.Key), Is.EqualTo(new[] { "toolCallId", "name", "arguments" }));
        Assert.That(echo["toolCallId"]!.GetValue<string>(), Is.EqualTo("0123456789ab"));
        Assert.That(echo["name"]!.GetValue<string>(), Is.EqualTo("SomeTool"));
    }

    [Test]
    public void CreateEcho_NullArguments_YieldsEmptyArgumentsObject()
    {
        var echo = ToolCallEcho.CreateEcho("0123456789ab", "SomeTool", null);
        Assert.That(echo["arguments"]!.AsObject(), Is.Empty);
    }

    [Test]
    public void NewToolCallId_IsTwelveLowercaseHexChars()
    {
        Assert.That(ToolCallEcho.NewToolCallId(), Does.Match("^[0-9a-f]{12}$"));
    }

    [Test]
    public void LongSingleLineString_KeepsFirst260Chars_WithCharCountMarker()
    {
        var path = "c:\\" + new string('p', 297);
        Assert.That(path.Length, Is.EqualTo(300));

        var args = EchoArgs(JsonSerializer.Serialize(new { filePath = path }));

        Assert.That(args["filePath"]!.GetValue<string>(), Is.EqualTo(path[..260] + "...[+40 chars]"));
    }

    [Test]
    public void SingleLineString_AtOrUnder260Chars_IsVerbatim()
    {
        var path = new string('p', 260);
        var args = EchoArgs(JsonSerializer.Serialize(new { filePath = path }));
        Assert.That(args["filePath"]!.GetValue<string>(), Is.EqualTo(path));
    }

    [Test]
    public void MultiLineString_IsCutTo100Chars_WithCharAndLineCountMarker()
    {
        // 50 lines of 9 chars joined by '\n': 499 chars in total.
        var code = string.Join("\n", Enumerable.Repeat("abcdefghi", 50));
        Assert.That(code.Length, Is.EqualTo(499));

        var args = EchoArgs(JsonSerializer.Serialize(new { oldContent = code }));

        Assert.That(args["oldContent"]!.GetValue<string>(), Is.EqualTo(code[..100] + "...[+399 chars, 50 lines]"));
    }

    [Test]
    public void ShortMultiLineString_IsVerbatim()
    {
        var args = EchoArgs(JsonSerializer.Serialize(new { oldContent = "a\r\nb" }));
        Assert.That(args["oldContent"]!.GetValue<string>(), Is.EqualTo("a\r\nb"));
    }

    [Test]
    public void Array_IsCappedAtThreeElements_WithMoreMarker_AndElementsAreTruncated()
    {
        var longString = new string('x', 300);
        var args = EchoArgs(JsonSerializer.Serialize(new { items = new[] { longString, "b", "c", "d", "e" } }));

        var items = args["items"]!.AsArray();
        Assert.That(items, Has.Count.EqualTo(4));
        Assert.That(items[0]!.GetValue<string>(), Is.EqualTo(longString[..260] + "...[+40 chars]"));
        Assert.That(items[1]!.GetValue<string>(), Is.EqualTo("b"));
        Assert.That(items[2]!.GetValue<string>(), Is.EqualTo("c"));
        Assert.That(items[3]!.GetValue<string>(), Is.EqualTo("...[+2 more]"));
    }

    [Test]
    public void Array_AtOrUnderThreeElements_HasNoMoreMarker()
    {
        var args = EchoArgs("{\"items\":[1,2,3]}");
        Assert.That(args["items"]!.AsArray().Select(n => n!.GetValue<int>()), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void NestedObject_IsRecursedInto()
    {
        var longString = new string('y', 300);
        var args = EchoArgs(JsonSerializer.Serialize(new { edit = new { path = longString, line = 7 } }));

        var edit = args["edit"]!.AsObject();
        Assert.That(edit["path"]!.GetValue<string>(), Is.EqualTo(longString[..260] + "...[+40 chars]"));
        Assert.That(edit["line"]!.GetValue<int>(), Is.EqualTo(7));
    }

    [Test]
    public void NumbersBoolsAndNulls_AreVerbatim()
    {
        var args = EchoArgs("{\"n\":42,\"f\":1.5,\"t\":true,\"z\":null}");
        Assert.That(args["n"]!.GetValue<int>(), Is.EqualTo(42));
        Assert.That(args["f"]!.GetValue<double>(), Is.EqualTo(1.5));
        Assert.That(args["t"]!.GetValue<bool>(), Is.True);
        Assert.That(args.ContainsKey("z"), Is.True);
        Assert.That(args["z"], Is.Null);
    }

    [Test]
    public void OverallCap_ReplacesObjectAndArrayArguments_ButKeepsScalars()
    {
        // 20 properties of 150-char strings: each survives per-value truncation, but together the
        // serialized arguments are far over 2048 chars.
        var big = Enumerable.Range(0, 20).ToDictionary(i => "k" + i, _ => new string('z', 150));
        var json = JsonSerializer.Serialize(new { reason = "keep me", count = 5, payload = big });

        var args = EchoArgs(json);

        Assert.That(args["payload"]!.GetValue<string>(), Does.StartWith("...[omitted, ").And.EndsWith(" chars]"));
        Assert.That(args["reason"]!.GetValue<string>(), Is.EqualTo("keep me"));
        Assert.That(args["count"]!.GetValue<int>(), Is.EqualTo(5));
        Assert.That(args.ToJsonString().Length, Is.LessThanOrEqualTo(ToolCallEcho.MaxArgumentsChars));
    }

    [Test]
    public void SurrogatePair_IsNeverSplitAtTheCutPoint()
    {
        // 259 chars, then a surrogate pair straddling the 260-char cut: the high surrogate sits
        // at index 259 (the last kept char), the low surrogate at index 260.
        var value = new string('a', 259) + "\uD83D\uDE00";
        Assert.That(value.Length, Is.EqualTo(261));

        var args = EchoArgs(JsonSerializer.Serialize(new { name = value }));

        var echoed = args["name"]!.GetValue<string>();
        Assert.That(echoed, Is.EqualTo(new string('a', 259) + "...[+2 chars]"));
        Assert.That(echoed.Any(char.IsSurrogate), Is.False, "No lone surrogate may remain after truncation.");
    }
}

/// <summary>
/// Coverage for <see cref="ToolCallEchoOptions"/> parsing. <see cref="NonParallelizableAttribute"/>
/// because <see cref="ToolCallEchoOptions.Enabled"/> and the env var are process-wide.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ToolCallEchoOptionsTests
{
    private const string EnvVar = "ROSLYNSENTINEL_ECHO_TOOL_ARGS";
    private string? _originalEnv;

    [SetUp]
    public void SetUp()
    {
        _originalEnv = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, null);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(EnvVar, _originalEnv);
        ToolCallEchoOptions.Configure(["--echo-tool-args=true"]);
    }

    [Test]
    public void Configure_Absent_LeavesItEnabled()
    {
        ToolCallEchoOptions.Configure(["--echo-tool-args=false"]);
        ToolCallEchoOptions.Configure([]);
        Assert.That(ToolCallEchoOptions.Enabled, Is.True);
    }

    [TestCase("--echo-tool-args=false")]
    [TestCase("--echo-tool-args=FALSE")]
    [TestCase("--echo-tool-args=0")]
    public void Configure_InlineFalseOrZero_Disables(string arg)
    {
        ToolCallEchoOptions.Configure([arg]);
        Assert.That(ToolCallEchoOptions.Enabled, Is.False);
    }

    [Test]
    public void Configure_SpaceSeparatedForm_IsAccepted()
    {
        ToolCallEchoOptions.Configure(["--echo-tool-args", "0"]);
        Assert.That(ToolCallEchoOptions.Enabled, Is.False);
    }

    [TestCase("--echo-tool-args=true")]
    [TestCase("--echo-tool-args=1")]
    [TestCase("--echo-tool-args=banana")]
    public void Configure_AnythingElse_LeavesItEnabled(string arg)
    {
        ToolCallEchoOptions.Configure(["--echo-tool-args=false"]);
        ToolCallEchoOptions.Configure([arg]);
        Assert.That(ToolCallEchoOptions.Enabled, Is.True);
    }

    [Test]
    public void Configure_EnvVarFalse_Disables_WhenNoArgGiven()
    {
        Environment.SetEnvironmentVariable(EnvVar, "false");
        ToolCallEchoOptions.Configure([]);
        Assert.That(ToolCallEchoOptions.Enabled, Is.False);
    }

    [Test]
    public void Configure_ArgWinsOverEnvVar()
    {
        Environment.SetEnvironmentVariable(EnvVar, "false");
        ToolCallEchoOptions.Configure(["--echo-tool-args=true"]);
        Assert.That(ToolCallEchoOptions.Enabled, Is.True);
    }
}
/// <summary>
/// Unit coverage for <see cref="ToolCallEcho.Stamp"/>'s re-serialization. The default
/// <c>JsonNode.ToJsonString()</c> encoder HTML-escapes angle brackets, quotes, apostrophes and
/// ampersands into backslash-u00XX sequences, which is the transcription hazard
/// <see cref="SharedJsonOptions"/> exists to prevent; the stamp must not re-introduce it.
/// </summary>
[TestFixture]
public class ToolCallEchoStampTests
{
    // Body as the SDK's default serializer writes it: <, >, ', ", & and + all HTML-escaped.
    private const string HtmlEscapedBody =
        "{\"isSuccess\":true,\"successData\":{\"signature\":\"List\\u003C(int, string)\\u003E Run(string a)\"," +
        "\"note\":\"it\\u0027s \\u0022quoted\\u0022 \\u0026 a\\u002Bb\"}}";

    private static string StampedText(string text, string? echoedCode = null)
    {
        var arguments = echoedCode is null
            ? null
            : new Dictionary<string, JsonElement> { ["code"] = JsonDocument.Parse(JsonSerializer.Serialize(echoedCode)).RootElement.Clone() };
        var result = new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        ToolCallEcho.Stamp(result, ToolCallEcho.CreateEcho("0123456789ab", "SomeTool", arguments));
        return ((TextContentBlock)result.Content[0]).Text;
    }

    [Test]
    public void Stamp_JsonObjectBody_KeepsGenericsAndPunctuationLiteral_IsValidJson_AndToolCallIsFirst()
    {
        var text = StampedText(HtmlEscapedBody, echoedCode: "List<int> x = 'a' & b;");

        Assert.That(text, Does.Contain("List<(int, string)> Run(string a)"), "Generic syntax must stay literal.");
        Assert.That(text, Does.Contain("it's"), "Apostrophes must stay literal.");
        Assert.That(text, Does.Contain("& a+b"), "Ampersand and plus must stay literal.");
        Assert.That(text, Does.Contain("List<int> x = 'a' & b;"), "The echoed arguments must stay literal too.");
        Assert.That(text, Does.Not.Contain("\\u00"), "No HTML-safe \\u00XX escape may remain.");
        Assert.That(text, Does.Contain("\\\"quoted\\\""), "A quote is written as the two-char JSON escape, not as \\u0022.");
        Assert.That(text, Does.Not.Contain("\n"), "The stamped body must be compact (no indentation tokens).");

        var body = JsonNode.Parse(text)!.AsObject();
        Assert.That(body.First().Key, Is.EqualTo("toolCall"));
        Assert.That(body["successData"]!["signature"]!.GetValue<string>(), Is.EqualTo("List<(int, string)> Run(string a)"));
        Assert.That(body["successData"]!["note"]!.GetValue<string>(), Is.EqualTo("it's \"quoted\" & a+b"));
    }

    [Test]
    public void Stamp_PlainTextBody_IsWrappedWithLiteralCharacters()
    {
        var text = StampedText("List<(int, string)> failed: it's 'bad' & wrong");

        Assert.That(text, Does.Contain("List<(int, string)> failed: it's 'bad' & wrong"));
        Assert.That(text, Does.Not.Contain("\\u00"));
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.That(body.Select(p => p.Key), Is.EqualTo(new[] { "toolCall", "message" }));
        Assert.That(body["message"]!.GetValue<string>(), Is.EqualTo("List<(int, string)> failed: it's 'bad' & wrong"));
    }
}
