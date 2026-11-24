// Coverage for the --hide-params / ROSLYNSENTINEL_HIDE_PARAMS option (plan_modeleval_workflow_contract_phase1 Step 19):
// an explicit list of tool parameters hidden from the top level of tools/list input schemas, composed with the Lean
// profile. The server still accepts a hidden name at call time, and validateOnApply:false is
// refused only where the emitted schema hides the parameter. Same in-process MCP host shape as SchemaLeanProfileTests.cs;
// no solution load is needed. Schema tests run for ReplaceSnippet and ApplyDiff (the WholeFileWrite mode).
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common.SnippetMatching;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions (HiddenParameters)
public class SchemaHiddenParametersTests
{
    private const string HideParamsEnvVar = "ROSLYNSENTINEL_HIDE_PARAMS";

    // WholeFileWrite registers WholeFileWriteTools (ApplyDiff). Outside claude-lean it is a plain static class, not a toolset.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace", "Refactor", "WholeFileWrite" };

    private static readonly string[] LeanHidden = ["autoStage", "returnDiff", "validateOnApply", "lineBefore", "lineAfter"];

    [SetUp]
    public void SetUp() => ResetStatics();

    [TearDown]
    public void TearDown() => ResetStatics();

    private static void ResetStatics()
    {
        // The profile and explicit hidden list are process-static, so reset them between tests.
        SchemaOptions.Configure([]);
        SchemaOptions.ConfigureHiddenParameters([]);
    }

    private static async Task<T> WithClientAsync<T>(string[] startupArgs, Func<McpClient, Task<T>> action)
    {
        SchemaOptions.Configure(startupArgs);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        using var host = hostBuilder.Build();
        var hostRun = host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var result = await action(client);
        await host.StopAsync();
        await hostRun;
        return result;
    }

    private static Task<List<(string Name, JsonElement InputSchema)>> ListInputSchemasAsync(params string[] startupArgs) =>
        WithClientAsync(startupArgs, async client =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);

            // Clone so the elements outlive the client/host disposal.
            return tools.Select(t => (t.Name, t.ProtocolTool.InputSchema.Clone())).ToList();
        });

    // Same host as ListInputSchemasAsync, but also returns the host's ToolSurfaceController snapshot. The controller's
    // first read forces the snapshot build, so it is read after the client has listed tools and before the host is disposed.
    private static async Task<(List<(string Name, JsonElement InputSchema)> Schemas, RoslynSentinel.Common.ToolSurfaceSnapshot Snapshot)> ListInputSchemasWithSnapshotAsync(params string[] startupArgs)
    {
        SchemaOptions.Configure(startupArgs);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        using var host = hostBuilder.Build();
        var hostRun = host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);

        // Clone so the elements outlive the client/host disposal.
        var schemas = tools.Select(t => (t.Name, t.ProtocolTool.InputSchema.Clone())).ToList();
        var snapshot = host.Services.GetRequiredService<RoslynSentinel.Common.ToolSurfaceController>().Current;
        await host.StopAsync();
        await hostRun;
        return (schemas, snapshot);
    }

    private static List<JsonElement> SchemasFor(List<(string Name, JsonElement InputSchema)> schemas, string toolName)
    {
        var found = schemas.Where(s => s.Name == toolName).Select(s => s.InputSchema).ToList();
        Assert.That(found, Is.Not.Empty, $"{toolName} should be listed in modes {string.Join("/", ActiveModes)}.");
        return found;
    }

    private static HashSet<string> TopLevelProperties(JsonElement schema) =>
        schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> RequiredNames(JsonElement schema) =>
        schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

    private static string CallText(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    // Calls ReplaceSnippet with action validate (no solution is loaded, so the tool may report its own error; the
    // assertions only look at whether the validator refused or rejected a parameter).
    private static Task<string> CallReplaceSnippetAsync(string[] startupArgs, string reason, Dictionary<string, object?> extra) =>
        WithClientAsync(startupArgs, async client =>
        {
            var arguments = new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["action"] = "validate",
            };
            foreach (var pair in extra)
            {
                arguments[pair.Key] = pair.Value;
            }

            var result = await client.CallToolAsync(
                "ReplaceSnippet",
                arguments,
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            return CallText(result);
        });

    // ---- schema shape: --hide-params=validateOnApply against the Full schema ----

    [TestCase("ReplaceSnippet")]
    [TestCase("ApplyDiff")]
    public async Task HideParams_ValidateOnApply_TopLevelSchemaLacksExactlyThatName(string toolName)
    {
        var full = await ListInputSchemasAsync();
        var hidden = await ListInputSchemasAsync("--hide-params=validateOnApply");

        var fullProperties = TopLevelProperties(SchemasFor(full, toolName).Single());
        var hiddenProperties = TopLevelProperties(SchemasFor(hidden, toolName).Single());

        Assert.That(fullProperties, Does.Contain("validateOnApply"), "Full keeps validateOnApply on the top level.");
        Assert.That(hiddenProperties, Is.EquivalentTo(fullProperties.Where(p => p != "validateOnApply")),
            $"{toolName} hidden schema should equal the Full schema minus validateOnApply, leaving the other boilerplate names where Full has them.");
    }

    [TestCase("ReplaceSnippet")]
    [TestCase("ApplyDiff")]
    public async Task HideParams_ValidateOnApply_RequiredNoLongerListsIt(string toolName)
    {
        var full = await ListInputSchemasAsync();
        var hidden = await ListInputSchemasAsync("--hide-params=validateOnApply");

        var fullRequired = RequiredNames(SchemasFor(full, toolName).Single());
        var hiddenRequired = RequiredNames(SchemasFor(hidden, toolName).Single());

        Assert.That(hiddenRequired, Does.Not.Contain("validateOnApply"));
        Assert.That(hiddenRequired, Is.EquivalentTo(fullRequired.Where(n => n != "validateOnApply")));
    }

    [Test]
    public async Task HideParams_DoesNotTouchNestedBatchEditItemSchemas()
    {
        var full = await ListInputSchemasAsync();
        var hidden = await ListInputSchemasAsync("--hide-params=validateOnApply");

        var fullBatchEdits = SchemasFor(full, "ReplaceSnippet").Single().GetProperty("properties").GetProperty("batchEdits").GetRawText();
        var hiddenBatchEdits = SchemasFor(hidden, "ReplaceSnippet").Single().GetProperty("properties").GetProperty("batchEdits").GetRawText();

        Assert.That(hiddenBatchEdits, Is.EqualTo(fullBatchEdits), "--hide-params strips only top-level names; batchEdits items are unchanged.");
    }

    [Test]
    public async Task HideParams_RegistersExactlyValidateOnApply()
    {
        var (_, snapshot) = await ListInputSchemasWithSnapshotAsync("--hide-params=validateOnApply");

        var hidden = snapshot.Find("ReplaceSnippet")!.HiddenParameters;
        Assert.That(hidden.Contains("validateOnApply"), Is.True);
        Assert.That(hidden.Contains("returnDiff"), Is.False, "returnDiff was not named, so it is not hidden.");
    }

    // ---- call-time refusal and acceptance ----

    [Test]
    public async Task HideParams_CallPassingValidateOnApplyFalse_IsRefused()
    {
        var text = await CallReplaceSnippetAsync(
            ["--hide-params=validateOnApply"],
            "Verify validateOnApply false is refused when hidden",
            new Dictionary<string, object?> { ["validateOnApply"] = false });

        Assert.That(text, Does.Contain("validateOnApply"), text);
        Assert.That(text, Does.Contain("Nothing was executed"), text);
        Assert.That(text, Does.Not.Contain("MUST"), text);
        Assert.That(text, Does.Not.Contain("do not"), text);
    }

    [Test]
    public async Task HideParams_CallPassingValidateOnApplyTrue_IsNotRefused()
    {
        var text = await CallReplaceSnippetAsync(
            ["--hide-params=validateOnApply"],
            "Verify hidden validateOnApply true is accepted",
            new Dictionary<string, object?> { ["validateOnApply"] = true });

        Assert.That(text, Does.Not.Contain("is not available for tool"), text);
    }

    [Test]
    public async Task HideParams_CallPassingStillVisibleReturnDiff_IsAccepted()
    {
        var text = await CallReplaceSnippetAsync(
            ["--hide-params=validateOnApply"],
            "Verify still-visible returnDiff is accepted",
            new Dictionary<string, object?> { ["returnDiff"] = true });

        Assert.That(text, Is.Not.Empty);
        Assert.That(text, Does.Not.Contain("Unknown parameter"), text);
    }

    [Test]
    public async Task HideParams_ToolScoped_HidesOnlyOnThatTool()
    {
        var full = await ListInputSchemasAsync();
        var scoped = await ListInputSchemasAsync("--hide-params=ReplaceSnippet.validateOnApply");

        var replaceFull = TopLevelProperties(SchemasFor(full, "ReplaceSnippet").Single());
        var replaceScoped = TopLevelProperties(SchemasFor(scoped, "ReplaceSnippet").Single());
        var applyDiffScoped = TopLevelProperties(SchemasFor(scoped, "ApplyDiff").Single());

        Assert.That(replaceFull, Does.Contain("validateOnApply"), "Full keeps validateOnApply on ReplaceSnippet.");
        Assert.That(replaceScoped, Does.Not.Contain("validateOnApply"),
            "ReplaceSnippet.validateOnApply is hidden, so ReplaceSnippet's top level must not list it.");
        Assert.That(applyDiffScoped, Does.Contain("validateOnApply"),
            "A Tool.param entry is scoped to that tool, so ApplyDiff must still list validateOnApply.");
    }

    [Test]
    public async Task HideParams_ToolScoped_HiddenArgumentStillAccepted()
    {
        var text = await CallReplaceSnippetAsync(
            ["--hide-params=ReplaceSnippet.validateOnApply"],
            "Verify tool-scoped hidden validateOnApply true is accepted",
            new Dictionary<string, object?> { ["validateOnApply"] = true });

        Assert.That(text, Does.Not.Contain("Unknown parameter"), text);
        Assert.That(text, Does.Not.Contain("is not available for tool"), text);
    }

    [Test]
    public async Task NoHideParams_ValidateOnApplyFalse_IsNotRefused()
    {
        var text = await CallReplaceSnippetAsync(
            [],
            "Verify validateOnApply false stays accepted with nothing hidden",
            new Dictionary<string, object?> { ["validateOnApply"] = false });

        Assert.That(SchemaOptions.HiddenParameters, Is.Empty);
        Assert.That(text, Does.Not.Contain("is not available for tool"), text);
    }

    // ---- composition with the Lean profile ----

    [Test]
    public async Task LeanProfileWithHideParams_HidesTheUnion_WithoutDuplicates()
    {
        var full = await ListInputSchemasAsync();
        var union = await ListInputSchemasAsync("--schema-profile=lean", "--hide-params=validateOnApply,dryRun");

        Assert.That(
            SchemaOptions.EffectiveHiddenParameters(),
            Is.EqualTo(new[] { "autoStage", "returnDiff", "validateOnApply", "lineBefore", "lineAfter", "dryRun" }),
            "The Lean list comes first, then the explicit names not already in it; validateOnApply appears once.");

        var expected = TopLevelProperties(SchemasFor(full, "ReplaceSnippet").Single());
        expected.ExceptWith(LeanHidden);
        expected.Remove("dryRun");

        Assert.That(TopLevelProperties(SchemasFor(union, "ReplaceSnippet").Single()), Is.EquivalentTo(expected));
    }

    // ---- SchemaOptions parsing and Configure ----

    [TestCase(null, "")]
    [TestCase("", "")]
    [TestCase("   ", "")]
    [TestCase("validateOnApply", "validateOnApply")]
    [TestCase("validateOnApply,returnDiff", "returnDiff|validateOnApply")]
    [TestCase(" a ; b ,, a ", "a|b")]
    [TestCase("unknownName", "unknownName")]
    public void ParseHiddenParameters_NullEmptyCommaSemicolonWhitespaceAndDuplicates(string? raw, string expected)
    {
        var names = SchemaOptions.ParseHiddenParameters(raw);

        Assert.That(string.Join("|", names.OrderBy(n => n, StringComparer.Ordinal)), Is.EqualTo(expected));
    }

    [Test]
    public void Configure_HideParamsFlag_SetsTheSet()
    {
        SchemaOptions.Configure(["--hide-params=validateOnApply,returnDiff"]);

        Assert.That(SchemaOptions.HiddenParameters, Is.EquivalentTo(new[] { "validateOnApply", "returnDiff" }));
        Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Full), "--hide-params does not select a profile.");
    }

    [Test]
    public void Configure_Absent_LeavesNoneHidden()
    {
        var previous = Environment.GetEnvironmentVariable(HideParamsEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(HideParamsEnvVar, null);

            SchemaOptions.Configure([]);

            Assert.That(SchemaOptions.HiddenParameters, Is.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HideParamsEnvVar, previous);
        }
    }

    [Test]
    public void Configure_EnvironmentVariable_IsUsedWhenNoFlag_AndFlagWins()
    {
        var previous = Environment.GetEnvironmentVariable(HideParamsEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(HideParamsEnvVar, "validateOnApply");

            SchemaOptions.Configure([]);
            Assert.That(SchemaOptions.HiddenParameters, Is.EquivalentTo(new[] { "validateOnApply" }));

            SchemaOptions.Configure(["--hide-params=returnDiff"]);
            Assert.That(SchemaOptions.HiddenParameters, Is.EquivalentTo(new[] { "returnDiff" }), "The flag takes precedence over the environment variable.");
            Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Full));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HideParamsEnvVar, previous);
        }
    }
}
