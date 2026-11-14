using System.IO.Pipelines;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Surface-aware parameter hints: the missing-required-parameter error names a discovery tool only when
/// that tool is callable on the active surface. A null or always-true predicate is the full surface and
/// must reproduce the HEAD text exactly.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SurfaceAwareHintsArgumentValidatorTests
{
    private const string ProbeName = "HintSurfaceProbe";

    private IHost _host = null!;
    private McpClient _client = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

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
        mcpBuilder.WithTools<HintSurfaceProbeTool>();

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

        // The probe has required parameters, so this call supplies all of them; it primes
        // HintSurfaceProbeTool.LastServer with the live server instance.
        await _client.CallToolAsync(
            ProbeName,
            new Dictionary<string, object?>
            {
                ["solutionPath"] = "C:\\repos\\MyApp\\MyApp.slnx",
                ["filePath"] = "src/Orders/OrderService.cs",
                ["typeName"] = "OrderService",
            },
            cancellationToken: TestContext.CurrentContext.CancellationToken);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        HintSurfaceProbeTool.LastServer = null;
        ToolArgumentValidator.ClearSchemaCacheForTests();
    }

    private static Dictionary<string, JsonElement> ArgsOmitting(string omitted)
    {
        var values = new (string Name, string Value)[]
        {
            ("solutionPath", "C:\\repos\\MyApp\\MyApp.slnx"),
            ("filePath", "src/Orders/OrderService.cs"),
            ("typeName", "OrderService"),
        };
        var args = new Dictionary<string, JsonElement>();
        foreach (var (name, value) in values)
        {
            if (name != omitted)
            {
                args[name] = JsonSerializer.SerializeToElement(value);
            }
        }
        return args;
    }

    private static string ExpectedMessage(string omitted, string hint) =>
        $"Missing required parameter '{omitted}' for tool '{ProbeName}'. Pass {omitted}: {hint} Nothing was executed.";

    // HEAD text of the ParameterHints entries, copied from the source literals unchanged.
    private static string GoldenSolutionPathHint() =>
        $"the absolute path to a .slnx/.sln/.csproj file, e.g. \"C:\\\\repos\\\\MyApp\\\\MyApp.slnx\". Call {ToolCallHint.Call("ListWorkspaceSolutions")} to discover the solutions available on this host.";

    private static string GoldenFilePathHint() =>
        $"a repo-relative or absolute path to a file in the loaded solution, e.g. \"src/Orders/OrderService.cs\". Call {ToolCallHint.Call("ListAll", ("kind", "all"))} or {ToolCallHint.Call("ListSolutionItems", ("kind", "all"))} to list the files in the solution.";

    private static string GoldenTypeNameHint() =>
        $"the name of a type in the loaded solution, e.g. \"OrderService\". Call {ToolCallHint.Call("ListAll", ("kind", "class"))} or {ToolCallHint.Call("Search", ("mode", "symbol"))} to find the exact name.";

    [Test]
    public void FullSurface_SolutionPathHint_MatchesHeadText()
    {
        var error = ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting("solutionPath"));

        Assert.That(error, Is.EqualTo(ExpectedMessage("solutionPath", GoldenSolutionPathHint())),
            "With no surface filter the solutionPath hint must be the HEAD text.");
    }

    [Test]
    public void FullSurface_FilePathHint_MatchesHeadText()
    {
        var error = ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting("filePath"));

        Assert.That(error, Is.EqualTo(ExpectedMessage("filePath", GoldenFilePathHint())),
            "With no surface filter the filePath hint must be the HEAD text.");
    }

    [Test]
    public void FullSurface_TypeNameHint_MatchesHeadText()
    {
        var error = ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting("typeName"));

        Assert.That(error, Is.EqualTo(ExpectedMessage("typeName", GoldenTypeNameHint())),
            "With no surface filter the typeName hint must be the HEAD text.");
    }

    [Test]
    public void AllowAllPredicate_EachHint_MatchesHeadText()
    {
        Func<string, bool> allowAll = _ => true;

        Assert.That(ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting("solutionPath"), allowAll),
            Is.EqualTo(ExpectedMessage("solutionPath", GoldenSolutionPathHint())),
            "A predicate that reports every tool callable must reproduce the solutionPath HEAD text.");
        Assert.That(ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting("filePath"), allowAll),
            Is.EqualTo(ExpectedMessage("filePath", GoldenFilePathHint())),
            "A predicate that reports every tool callable must reproduce the filePath HEAD text.");
        Assert.That(ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting("typeName"), allowAll),
            Is.EqualTo(ExpectedMessage("typeName", GoldenTypeNameHint())),
            "A predicate that reports every tool callable must reproduce the typeName HEAD text.");
    }

    [Test]
    public void LeanSurface_HintsNameOnlyCallableTools()
    {
        var leanSurface = new HashSet<string>(StringComparer.Ordinal)
        {
            "ReadFile", "GetFileOutline", "Search", "ListSolutionItems", "GetLargeResult", "ReplaceSnippet",
            "Member", "RenameSymbol", "CreateFile", "DeleteFile", "Build", "RunTest", "GetMethodSource",
            "UndoLastApply", "ChangeSignature", "ChangeAccessibility", "ModifyModifier", "UsingDirective",
        };
        Func<string, bool> isLeanCallable = leanSurface.Contains;

        foreach (var omitted in new[] { "solutionPath", "filePath", "typeName" })
        {
            var error = ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting(omitted), isLeanCallable);

            Assert.That(error, Is.Not.Null, $"Omitting '{omitted}' must be rejected.");
            foreach (Match call in Regex.Matches(error!, @"\b([A-Z][A-Za-z]+)\(reason:"))
            {
                Assert.That(leanSurface.Contains(call.Groups[1].Value), Is.True,
                    $"The hint for '{omitted}' renders {call.Groups[1].Value}, which is not on the lean surface. Got: {error}");
            }
            Assert.That(error, Does.Not.Contain("ListWorkspaceSolutions"),
                $"A lean surface must not name ListWorkspaceSolutions. Got: {error}");
            Assert.That(error, Does.Not.Contain("ListAll"),
                $"A lean surface must not name ListAll. Got: {error}");
        }

        var solutionPathError = ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting("solutionPath"), isLeanCallable);
        Assert.That(solutionPathError, Does.Not.Contain(" Call "),
            $"With ListWorkspaceSolutions not callable the solutionPath hint must drop its call sentence. Got: {solutionPathError}");
        Assert.That(solutionPathError, Does.Contain("MyApp.slnx\". Nothing was executed."),
            $"The solutionPath hint must end with its example sentence. Got: {solutionPathError}");

        var filePathError = ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting("filePath"), isLeanCallable);
        Assert.That(filePathError, Does.Contain("ListSolutionItems"),
            $"With only ListSolutionItems callable the filePath hint must name it. Got: {filePathError}");

        var typeNameError = ToolArgumentValidator.Validate(HintSurfaceProbeTool.LastServer, ProbeName, ArgsOmitting("typeName"), isLeanCallable);
        Assert.That(typeNameError, Does.Contain("Search"),
            $"With only Search callable the typeName hint must name it. Got: {typeNameError}");
    }
}

/// <summary>
/// Test-only tool with three required string parameters, one per discovery hint. The live
/// <see cref="McpServer"/> is captured on each invocation so direct validator calls read the real tool collection.
/// </summary>
[McpServerToolType]
public class HintSurfaceProbeTool
{
    /// <summary>The McpServer that last dispatched this tool (test-only capture).</summary>
    public static McpServer? LastServer { get; set; }

    [McpServerTool(Name = "HintSurfaceProbe")]
    [System.ComponentModel.Description("Test-only tool with three required string parameters, for surface-aware hint tests.")]
    public string HintSurfaceProbe(
        McpServer server,
        [System.ComponentModel.Description("Test-only required solution path.")] string solutionPath,
        [System.ComponentModel.Description("Test-only required file path.")] string filePath,
        [System.ComponentModel.Description("Test-only required type name.")] string typeName)
    {
        LastServer = server;
        return "ok";
    }
}
