using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Slice 8 coverage: a model sends a declared-object parameter (MoveMember's fieldMap,
/// Dictionary&lt;string,string&gt;?) as a JSON STRING. The SDK binder then throws a JsonException
/// and the caller sees a failure instead of the mapping.
/// <see cref="ToolArgumentValidator.CoerceStringToStructured"/> (not yet implemented; the tests that
/// call it are guarded by SLICE8_FIX) is meant to repair that before Validate and the binder run.
/// <para>
/// The probe tool below declares its parameters with the same CLR types and nullability as MoveMember's
/// fieldMap, so the schema and binder behaviour are identical without pulling the Advanced engines
/// into this fixture. The probe captures the live McpServer so direct calls to the validator read the
/// real tool collection rather than a null server.
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
public class CoerceStringToStructuredTests
{
    private const string ProbeName = "FieldMapStringProbe";

    private IHost _host = null!;
    private McpClient _client = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    private static string JoinText(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [SetUp]
    public async Task SetUp()
    {
        ToolArgumentValidator.ClearSchemaCacheForTests();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);
        mcpBuilder.WithTools<FieldMapStringProbeTool>();

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

        // A no-argument call primes FieldMapStringProbeTool.LastServer with the live server instance.
        await _client.CallToolAsync(
            ProbeName,
            new Dictionary<string, object?>(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        FieldMapStringProbeTool.LastServer = null;
        ToolArgumentValidator.ClearSchemaCacheForTests();
    }

    [Test]
    public async Task FieldMapSentAsJsonString_IsAcceptedAndBoundAsObject()
    {
        // REPRO (slice 8, step A): fieldMap as a JSON string. Before the fix the binder throws a
        // JsonException and the caller gets a failure; after the fix this must succeed with the mapping
        // delivered as a real dictionary.
        var result = await _client.CallToolAsync(
            ProbeName,
            new Dictionary<string, object?>
            {
                ["fieldMap"] = "{\"_a\":\"_b\"}",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = JoinText(result);
        Assert.That(result.IsError, Is.Not.True,
            $"A string holding a JSON object for a declared-object parameter must be coerced and the call must succeed. Got: {text}");
        Assert.That(text, Does.Contain("_a=_b"),
            $"The tool must run with fieldMap bound as a dictionary holding _a -> _b. Got: {text}");
    }

    private static Dictionary<string, JsonElement> StringArgs(string name, string value) =>
        new() { [name] = JsonSerializer.SerializeToElement(value) };

    [Test]
    public void FieldMapJsonString_IsCoercedToObject_AndOneNoteIsReturned()
    {
        var args = StringArgs("fieldMap", "{\"_a\":\"_b\"}");

        var notes = ToolArgumentValidator.CoerceStringToStructured(FieldMapStringProbeTool.LastServer, ProbeName, args);

        Assert.That(notes, Is.Not.Null.And.Count.EqualTo(1),
            "Coercing a JSON-object string must return exactly one note.");
        Assert.That(args["fieldMap"].ValueKind, Is.EqualTo(JsonValueKind.Object),
            "The argument must now be a JSON object.");
        Assert.That(args["fieldMap"].GetProperty("_a").GetString(), Is.EqualTo("_b"),
            "The coerced object must carry _a -> _b.");
        Assert.That(ToolArgumentValidator.Validate(FieldMapStringProbeTool.LastServer, ProbeName, args), Is.Null,
            "After coercion Validate must accept the call.");
    }

    [Test]
    public void FieldMapNonJsonString_IsNotCoerced_AndValidateExplainsExpectedObject()
    {
        var args = StringArgs("fieldMap", "hello");

        var notes = ToolArgumentValidator.CoerceStringToStructured(FieldMapStringProbeTool.LastServer, ProbeName, args);

        Assert.That(notes is null || notes.Count == 0, Is.True,
            "A string that is not JSON must not be coerced and must produce no note.");
        Assert.That(args["fieldMap"].ValueKind, Is.EqualTo(JsonValueKind.String),
            "The argument must be left as the original string.");

        var error = ToolArgumentValidator.Validate(FieldMapStringProbeTool.LastServer, ProbeName, args);
        Assert.That(error, Is.Not.Null, "A non-JSON string for an object parameter must be rejected.");
        Assert.That(error, Does.Contain("'fieldMap'"), $"The rejection must name 'fieldMap'. Got: {error}");
        Assert.That(error, Does.Contain(ProbeName), $"The rejection must name the tool. Got: {error}");
        Assert.That(error, Does.Contain("object"), $"The rejection must say an object is expected. Got: {error}");
        Assert.That(error, Does.Not.Contain("JsonException"), $"No exception type may leak. Got: {error}");
        Assert.That(error, Does.Not.Contain("   at "), $"No stack trace may leak. Got: {error}");
        Assert.That(error, Does.Not.Match(@"[A-Za-z]:\\"), $"No drive path may leak. Got: {error}");
    }

    [Test]
    public void RealObjectAndRealArray_AreLeftUntouched_WithNoNotes()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["fieldMap"] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["_a"] = "_b" }),
            ["names"] = JsonSerializer.SerializeToElement(new[] { "x", "y" }),
        };

        var notes = ToolArgumentValidator.CoerceStringToStructured(FieldMapStringProbeTool.LastServer, ProbeName, args);

        Assert.That(notes is null || notes.Count == 0, Is.True,
            "Values that already have the declared structure must produce no note.");
        Assert.That(args["fieldMap"].ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(args["names"].ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(ToolArgumentValidator.Validate(FieldMapStringProbeTool.LastServer, ProbeName, args), Is.Null);
    }

    [Test]
    public void StringDeclaredParameter_GivenJsonLookingText_IsNotCoerced()
    {
        var args = StringArgs("filePath", "{\"a\":1}");

        var notes = ToolArgumentValidator.CoerceStringToStructured(FieldMapStringProbeTool.LastServer, ProbeName, args);

        Assert.That(notes is null || notes.Count == 0, Is.True,
            "A string-declared parameter must never be coerced, even when its text looks like JSON.");
        Assert.That(args["filePath"].ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(args["filePath"].GetString(), Is.EqualTo("{\"a\":1}"));
    }
}

/// <summary>
/// Test-only tool whose parameter shapes mirror MoveMember's: <c>Dictionary&lt;string, string&gt;? fieldMap</c>
/// for the object case, a string-declared <c>filePath</c> and an array <c>names</c> for the controls.
/// The live <see cref="McpServer"/> is captured on each invocation so direct validator calls read the
/// real tool collection.
/// </summary>
[McpServerToolType]
public class FieldMapStringProbeTool
{
    /// <summary>The McpServer that last dispatched this tool (test-only capture).</summary>
    public static McpServer? LastServer { get; set; }

    [McpServerTool(Name = "FieldMapStringProbe")]
    [System.ComponentModel.Description("Test-only tool with Dictionary, string and array parameters, for fieldMap string-coercion tests.")]
    public string FieldMapStringProbe(
        McpServer server,
        [System.ComponentModel.Description("Test-only string-declared path parameter.")] string? filePath = null,
        [System.ComponentModel.Description("Test-only mapping parameter.")] Dictionary<string, string>? fieldMap = null,
        [System.ComponentModel.Description("Test-only array parameter.")] string[]? names = null)
    {
        LastServer = server;
        var rendered = fieldMap is null
            ? "null"
            : string.Join(",", fieldMap.Select(kv => kv.Key + "=" + kv.Value));
        return $"fieldMap={rendered}";
    }
}
