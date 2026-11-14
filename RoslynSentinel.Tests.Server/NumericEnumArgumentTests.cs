using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// A JSON number sent for a parameter whose schema declares a string enum (e.g. Member operation: 4)
/// used to pass validation, and the SDK's JsonStringEnumConverter bound it as an ordinal. The validator
/// now rejects it before dispatch with an error that names the parameter, the members and the number.
/// The probe tool mirrors that shape: a CLR enum with a JsonStringEnumConverter and a plain int parameter.
/// </summary>
[TestFixture]
[NonParallelizable]
public class NumericEnumArgumentTests
{
    private const string ProbeName = "EnumNumberProbe";

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
        mcpBuilder.WithTools<EnumNumberProbeTool>();

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

        // A no-argument call primes EnumNumberProbeTool.LastServer with the live server instance.
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
        EnumNumberProbeTool.LastServer = null;
        EnumNumberProbeTool.Invocations = 0;
        ToolArgumentValidator.ClearSchemaCacheForTests();
    }

    [Test]
    public void NumberForStringEnumParameter_IsRejectedBeforeDispatch_NamingParameterToolMembersAndNumber()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["operation"] = JsonSerializer.SerializeToElement(4),
        };

        var error = ToolArgumentValidator.Validate(EnumNumberProbeTool.LastServer, ProbeName, args);

        Assert.That(error, Is.Not.Null, "A JSON number for a string-enum parameter must be rejected.");
        Assert.That(error, Does.Contain("'operation'"), $"The rejection must name 'operation'. Got: {error}");
        Assert.That(error, Does.Contain(ProbeName), $"The rejection must name the tool. Got: {error}");
        Assert.That(error, Does.Contain("expects one of"), $"The rejection must list the accepted values. Got: {error}");
        Assert.That(error, Does.Contain("got number 4"), $"The rejection must echo the number. Got: {error}");
    }

    [Test]
    public void StringMemberForStringEnumParameter_IsAccepted()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["operation"] = JsonSerializer.SerializeToElement("Beta"),
        };

        Assert.That(ToolArgumentValidator.Validate(EnumNumberProbeTool.LastServer, ProbeName, args), Is.Null,
            "A string value that matches an enum member must still be accepted.");
    }

    [Test]
    public void NumberForNonEnumParameter_IsNotRejectedByTheEnumBranch()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["count"] = JsonSerializer.SerializeToElement(4),
        };

        Assert.That(ToolArgumentValidator.Validate(EnumNumberProbeTool.LastServer, ProbeName, args), Is.Null,
            "A JSON number for a plain integer parameter must be accepted.");
    }

    [Test]
    public async Task NumberForStringEnumParameter_EndToEnd_IsErrorAndProbeBodyDoesNotRun()
    {
        EnumNumberProbeTool.Invocations = 0;

        var result = await _client.CallToolAsync(
            ProbeName,
            new Dictionary<string, object?>
            {
                ["operation"] = 4,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = JoinText(result);
        Assert.That(result.IsError, Is.True,
            $"A JSON number for a string-enum parameter must come back as an error. Got: {text}");
        Assert.That(text, Does.Contain("expects one of"),
            $"The error must list the accepted values. Got: {text}");
        Assert.That(EnumNumberProbeTool.Invocations, Is.EqualTo(0),
            "The probe body must not run when the call is rejected before dispatch.");
    }
}

/// <summary>Test-only enum with string members, bound through JsonStringEnumConverter as a declared string enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnumNumberProbeMode
{
    Alpha,
    Beta,
    View,
}

/// <summary>
/// Test-only tool: <c>EnumNumberProbeMode operation</c> is a declared string enum, <c>int count</c> a plain
/// integer. The live <see cref="McpServer"/> is captured on each invocation so direct validator calls read
/// the real tool collection; <see cref="Invocations"/> counts body executions.
/// </summary>
[McpServerToolType]
public class EnumNumberProbeTool
{
    /// <summary>The McpServer that last dispatched this tool (test-only capture).</summary>
    public static McpServer? LastServer { get; set; }

    /// <summary>How many times the probe body ran (test-only).</summary>
    public static int Invocations { get; set; }

    [McpServerTool(Name = "EnumNumberProbe")]
    [System.ComponentModel.Description("Test-only tool with a string-enum and an integer parameter, for number-for-enum validation tests.")]
    public string EnumNumberProbe(
        McpServer server,
        [System.ComponentModel.Description("Test-only string enum parameter.")] EnumNumberProbeMode operation = EnumNumberProbeMode.Alpha,
        [System.ComponentModel.Description("Test-only integer parameter.")] int count = 0)
    {
        LastServer = server;
        Invocations++;
        return $"operation={operation} count={count}";
    }
}
