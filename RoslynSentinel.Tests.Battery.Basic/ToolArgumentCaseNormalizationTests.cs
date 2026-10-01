using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Coverage for <see cref="ToolArgumentValidator.NormalizeParameterCase"/>,
/// added after a live transcript showed a model repeatedly passing "filepath" instead of the
/// declared "filePath" across several different tools - a case-only mismatch that
/// <c>ToolArgumentValidator.SuggestClosest</c> already recognised as a certain match in its
/// "Did you mean" text, but which previously still rejected the call and cost a full round-trip.
/// There is no reason for the dispatch path to be case-sensitive about a parameter NAME (as
/// opposed to its value) when the schema itself declares only one spelling.
/// <para>
/// Runs against a real in-process <see cref="McpClient"/>/<see cref="McpServer"/> pair (the same
/// pattern as <see cref="LargeResultOffloadFilterTests"/>) rather than calling
/// <c>NormalizeParameterCase</c> directly, because the thing actually worth proving is that the
/// rename reaches the SDK's own case-sensitive argument binder and the tool runs with the REAL
/// value under the renamed key - not merely that the validator's own bookkeeping looks right.
/// </para>
/// </summary>
[TestFixture]
public class ToolArgumentCaseNormalizationTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;
    private string _seededFilePath = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    // Only this test fixture's target: a single method for FindReferences to resolve, so a
    // successful lookup is unambiguous proof the renamed argument actually reached the tool.
    private const string TargetMethodName = "Target";

    /// <summary>
    /// A rejection is plain text wrapped by the tool-call echo filter as {toolCall, message}, and the
    /// wrapper's JSON escapes apostrophes ('), so assertions on the rejection wording read the
    /// <c>message</c> field instead of searching the raw response text.
    /// </summary>
    private static string RejectionMessage(string responseText) =>
        System.Text.Json.Nodes.JsonNode.Parse(responseText)?["message"]?.GetValue<string>() ?? responseText;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();

        var source = "namespace ContosoOrders.Core;" + Environment.NewLine +
                     "public class CaseNormalizationProbe" + Environment.NewLine +
                     "{" + Environment.NewLine +
                     $"    public void {TargetMethodName}() {{ }}" + Environment.NewLine +
                     $"    public void Caller() {{ {TargetMethodName}(); }}" + Environment.NewLine +
                     "}" + Environment.NewLine;

        var relativeFilePath = Path.Combine("ContosoOrders.Core", "CaseNormalizationProbe.cs");
        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = _fixture.SolutionPath })
        {
            await _fixture.AddFileToSolution(seedWorkspaceManager, relativeFilePath, source, reloadSolution: false);
        }

        _seededFilePath = Path.Combine(_fixture.SolutionDirectory, relativeFilePath);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);
        // Registered purely to give the schema-level-collision case a real tool to call - no
        // shipped RoslynSentinel tool declares two parameters that collide case-insensitively, so
        // that scenario can only be exercised against a purpose-built one.
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

        var loadResult = await _client.CallToolAsync(
            "LoadSolution",
            new Dictionary<string, object?> { ["reason"] = "test message", ["solutionPath"] = _fixture.SolutionPath }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load - cannot exercise normalization without a loaded solution.");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
    }

    [Test]
    public async Task CaseOnlyMismatch_IsRenamedToDeclaredSpelling_AndToolRunsOnTheRealValue()
    {
        // "filepath" instead of the declared "filePath" - the exact mistake from the live
        // transcript that prompted this fix. If normalization did not reach the SDK's binder, this
        // call would either be rejected as an unknown parameter (post-Validate) or silently ignored
        // and FindReferences would run with filePath unset, both distinguishable from the
        // assertions below.
        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbolName"] = TargetMethodName,
                ["kind"] = "callers",
                ["filepath"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(result.IsError, Is.Not.True,
            $"A case-only mismatch on 'filepath' must be normalized to 'filePath' and the call must succeed. Got: {text}");
        Assert.That(text, Does.Not.Contain("Unknown parameter"),
            "The renamed key must never reach Validate's unknown-parameter check.");
        Assert.That(text, Does.Contain("Caller"),
            $"FindReferences must have actually run with the real filePath value under the renamed key, not its default. Got: {text}");
    }

    [Test]
    public async Task CanonicalNameAlreadyPresent_LeavesBothKeysAlone_AndVariantIsReportedAsUnknown()
    {
        // Both the canonical "filePath" and a case-only variant "filepath" supplied in the same
        // call. Renaming the variant on top of the canonical key would silently discard whichever
        // value the caller intended by supplying two keys - so normalization must leave this call
        // exactly as if it had never run, and Validate must reject "filepath" as an ordinary
        // unknown parameter (not silently merge or overwrite either value).
        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbolName"] = TargetMethodName,
                ["kind"] = "callers",
                ["filePath"] = _seededFilePath,
                ["filepath"] = "some/other/path/that/does/not/exist.cs",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(result.IsError, Is.True,
            $"A duplicate case-only variant alongside the canonical key must be rejected, not silently merged. Got: {text}");
        Assert.That(RejectionMessage(text), Does.Contain("'filepath'"),
            $"The rejection must name 'filepath' as the unrecognised parameter. Got: {text}");
    }

    [Test]
    public async Task SchemaLevelCaseCollision_IsNeverNormalized_BothSpellingsStayDistinct()
    {
        // CaseCollisionProbeTool declares both "id" and "Id" as real, distinct parameters (see
        // CaseCollisionProbeTool below). Passing an unrelated case-only variant of either ("ID")
        // must NOT be silently routed to either one - TryGetSchemaParameters excludes colliding
        // declared names from its case-insensitive lookup entirely, so this must fall straight
        // through to Validate's ordinary unknown-parameter rejection instead of guessing which of
        // the two real parameters the caller meant.
        var result = await _client.CallToolAsync(
            "CaseCollisionProbe",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["id"] = "lower",
                ["Id"] = "upper",
                ["ID"] = "should not normalize to either declared parameter",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(result.IsError, Is.True,
            $"A case-only variant of two colliding declared parameters must be rejected, never silently normalized to either one. Got: {text}");
        Assert.That(RejectionMessage(text), Does.Contain("'ID'"),
            $"The rejection must name 'ID' as the unrecognised parameter. Got: {text}");
    }

    [Test]
    public async Task ExactDeclaredCase_StillWorks_NormalizationIsANoOpWhenNothingIsWrong()
    {
        // Baseline: a call that already uses the exact declared casing must be completely
        // unaffected by the normalization pass.
        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbolName"] = TargetMethodName,
                ["kind"] = "callers",
                ["filePath"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.That(result.IsError, Is.Not.True, $"An already-correct call must be unaffected. Got: {text}");
        Assert.That(text, Does.Contain("Caller"));
    }
}

/// <summary>
/// Test-only tool that declares two parameters colliding case-insensitively ("id" vs "Id"), so
/// <see cref="ToolArgumentCaseNormalizationTests.SchemaLevelCaseCollision_IsNeverNormalized_BothSpellingsStayDistinct"/>
/// has a real schema to exercise that scenario against. No shipped RoslynSentinel tool declares
/// colliding parameter names, so this cannot be tested against production tools.
/// </summary>
[McpServerToolType]
public class CaseCollisionProbeTool
{
    [McpServerTool(Name = "CaseCollisionProbe")]
    [System.ComponentModel.Description("Test-only tool with two case-colliding parameters, for case-normalization tests.")]
    public string CaseCollisionProbe(
        [System.ComponentModel.Description("Lowercase id parameter.")] string? id = null,
        [System.ComponentModel.Description("Uppercase-I Id parameter - a distinct parameter from 'id'.")] string? Id = null)
    {
        return $"id={id} Id={Id}";
    }
}
