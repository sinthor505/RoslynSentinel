// Coverage for proposal_reduce_tool_schema_token_cost.md Step 2 (slice 2a): the "lean" schema profile
// hides autoStage/returnDiff/validateOnApply/lineBefore/lineAfter from the top level of tools/list
// input schemas, while the server still accepts those names at call time (HiddenSchemaParams) and the
// ambiguous-target errors still name lineBefore/lineAfter. Same in-process MCP host shape as
// SchemaDataTagOptionTests.cs; no solution load is needed.
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[NonParallelizable] // mutates the process-wide SchemaOptions, HiddenSchemaParams and the validator schema cache
public class SchemaLeanProfileTests
{
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace", "Refactor" };

    private static readonly string[] LeanHidden = ["autoStage", "returnDiff", "validateOnApply", "lineBefore", "lineAfter"];

    [SetUp]
    public void SetUp() => ResetStatics();

    [TearDown]
    public void TearDown() => ResetStatics();

    private static void ResetStatics()
    {
        // All three are process-static: the profile, the hidden-name registry, and the validator's
        // per-tool schema cache (which would otherwise keep validating against a stale schema).
        SchemaOptions.Configure([]);
        HiddenSchemaParams.Clear();
        ToolArgumentValidator.ClearSchemaCacheForTests();
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
        _ = host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var result = await action(client);
        await host.StopAsync();
        return result;
    }

    private static Task<List<(string Name, JsonElement InputSchema)>> ListInputSchemasAsync(params string[] startupArgs) =>
        WithClientAsync(startupArgs, async client =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);

            // Clone so the elements outlive the client/host disposal.
            return tools.Select(t => (t.Name, t.ProtocolTool.InputSchema.Clone())).ToList();
        });

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

    // ---- schema shape ----

    [TestCase("ReplaceSnippet")]
    [TestCase("Member")]
    [TestCase("MethodSignature")]
    public async Task Lean_TopLevelProperties_LackTheFiveBoilerplateNames(string toolName)
    {
        var schemas = await ListInputSchemasAsync("--schema-profile=lean");

        Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Lean));
        foreach (var schema in SchemasFor(schemas, toolName))
        {
            var properties = TopLevelProperties(schema);
            var required = RequiredNames(schema);
            foreach (var hidden in LeanHidden)
            {
                Assert.That(properties, Does.Not.Contain(hidden), $"{toolName} top-level properties should not carry '{hidden}' under Lean.");
                Assert.That(required, Does.Not.Contain(hidden), $"{toolName} required[] should not carry '{hidden}' under Lean.");
            }
        }
    }

    [Test]
    public async Task Lean_LeavesDryRunContextSnippetAndReasonUntouched()
    {
        var schemas = await ListInputSchemasAsync("--schema-profile=lean");

        foreach (var schema in SchemasFor(schemas, "Member"))
        {
            var properties = TopLevelProperties(schema);
            Assert.That(properties, Does.Contain("dryRun"));
            Assert.That(properties, Does.Contain("contextSnippet"));
            Assert.That(properties, Does.Contain("reason"));
        }
    }

    [Test]
    public async Task Lean_DoesNotTouchNestedBatchEditItemSchemas()
    {
        var schemas = await ListInputSchemasAsync("--schema-profile=lean");

        foreach (var schema in SchemasFor(schemas, "ReplaceSnippet"))
        {
            var items = schema.GetProperty("properties").GetProperty("batchEdits").GetRawText();
            Assert.That(items, Does.Contain("lineBefore"), "batchEdits items keep their own lineBefore under Lean.");
            Assert.That(items, Does.Contain("lineAfter"), "batchEdits items keep their own lineAfter under Lean.");
        }
    }

    [Test]
    public async Task Full_ReplaceSnippetAndMember_KeepTheBoilerplateNames()
    {
        var schemas = await ListInputSchemasAsync("--schema-profile=full");

        Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Full));
        foreach (var schema in SchemasFor(schemas, "ReplaceSnippet"))
        {
            Assert.That(TopLevelProperties(schema), Is.SupersetOf(new[] { "returnDiff", "validateOnApply", "lineBefore", "lineAfter" }));
        }

        foreach (var schema in SchemasFor(schemas, "Member"))
        {
            Assert.That(TopLevelProperties(schema), Is.SupersetOf(new[] { "autoStage", "returnDiff", "lineBefore", "lineAfter" }));
        }

        foreach (var schema in SchemasFor(schemas, "MethodSignature"))
        {
            Assert.That(TopLevelProperties(schema).Intersect(LeanHidden), Is.Not.Empty, "Full keeps at least one boilerplate name on MethodSignature.");
        }
    }

    [Test]
    public async Task Default_WithoutFlag_IsFull()
    {
        var schemas = await ListInputSchemasAsync();

        Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Full));
        foreach (var schema in SchemasFor(schemas, "ReplaceSnippet"))
        {
            Assert.That(TopLevelProperties(schema), Does.Contain("returnDiff"));
        }
    }

    // ---- HiddenSchemaParams + call-time acceptance ----

    [Test]
    public async Task Full_RegistersNoHiddenParams()
    {
        await ListInputSchemasAsync("--schema-profile=full");

        foreach (var name in LeanHidden)
        {
            Assert.That(HiddenSchemaParams.IsHidden("ReplaceSnippet", name), Is.False, $"Full must not register '{name}'.");
            Assert.That(HiddenSchemaParams.IsHidden("Member", name), Is.False, $"Full must not register '{name}'.");
        }
    }

    [Test]
    public async Task Lean_RegistersExactlyTheStrippedNames()
    {
        await ListInputSchemasAsync("--schema-profile=lean");

        Assert.That(HiddenSchemaParams.IsHidden("ReplaceSnippet", "returnDiff"), Is.True);
        Assert.That(HiddenSchemaParams.IsHidden("replacesnippet", "validateOnApply"), Is.True, "Tool names match case-insensitively.");
        // ReplaceSnippet has no autoStage parameter, so it must not be registered for it.
        Assert.That(HiddenSchemaParams.IsHidden("ReplaceSnippet", "autoStage"), Is.False);
        Assert.That(HiddenSchemaParams.IsHidden("Member", "autoStage"), Is.True);
        // Never registers names that stay in the schema.
        Assert.That(HiddenSchemaParams.IsHidden("ReplaceSnippet", "dryRun"), Is.False);
        Assert.That(HiddenSchemaParams.IsHidden("ReplaceSnippet", "reason"), Is.False);
    }

    [Test]
    public async Task Lean_CallPassingHiddenReturnDiff_IsNotRejectedAsUnknown()
    {
        var text = await WithClientAsync(["--schema-profile=lean"], async client =>
        {
            var result = await client.CallToolAsync(
                "ReplaceSnippet",
                new Dictionary<string, object?>
                {
                    ["reason"] = "Verify hidden returnDiff is accepted",
                    ["action"] = "validate",
                    ["returnDiff"] = true,
                },
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            return CallText(result);
        });

        // Whatever the tool then does (no solution is loaded here, so it may well report an error), it
        // must not be the validator's "Unknown parameter" rejection.
        Assert.That(text, Does.Not.Contain("Unknown parameter"), text);
    }

    [Test]
    public async Task Lean_GenuinelyUnknownParam_IsStillRejected_AndHiddenNamesAreNotAdvertised()
    {
        var text = await WithClientAsync(["--schema-profile=lean"], async client =>
        {
            var result = await client.CallToolAsync(
                "ReplaceSnippet",
                new Dictionary<string, object?>
                {
                    ["reason"] = "Verify unknown params are still rejected",
                    ["action"] = "validate",
                    ["returnDiff"] = true,
                    ["bogusParam"] = 1,
                },
                cancellationToken: TestContext.CurrentContext.CancellationToken);
            Assert.That(result.IsError, Is.True);
            return CallText(result);
        });

        Assert.That(text, Does.Contain("Unknown parameter 'bogusParam'"), text);
        Assert.That(text, Does.Not.Contain("'returnDiff'"), "A hidden-but-accepted name is not an unknown parameter.");

        // The "This tool accepts only: ..." list is built from the emitted schema, so hidden names stay out of it.
        var acceptsOnly = text[text.IndexOf("accepts only:", StringComparison.Ordinal)..];
        foreach (var hidden in LeanHidden)
        {
            Assert.That(acceptsOnly, Does.Not.Contain(hidden), $"'{hidden}' must not be advertised in the accepts-only list.");
        }
    }

    // ---- SchemaOptions parsing ----

    [Test]
    public void Configure_Absent_LeavesProfileFull()
    {
        SchemaOptions.Configure([]);
        Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Full));
    }

    [TestCase("--schema-profile=lean")]
    [TestCase("--schema-profile=LEAN")]
    [TestCase("--schema-profile= lean ")]
    public void Configure_InlineLean_SelectsLean(string arg)
    {
        SchemaOptions.Configure([arg]);
        Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Lean));
    }

    [Test]
    public void Configure_SeparateValueForm_SelectsLean()
    {
        SchemaOptions.Configure(["--schema-profile", "lean"]);
        Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Lean));
    }

    [TestCase("--schema-profile=full")]
    [TestCase("--schema-profile=bogus")]
    [TestCase("--schema-profile=")]
    [TestCase("--schema-profile")]
    public void Configure_FullInvalidOrBare_KeepsFull_WithoutThrowing(string arg)
    {
        Assert.DoesNotThrow(() => SchemaOptions.Configure([arg]));
        Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Full));
    }

    [Test]
    public void Configure_BareProfileFlagFollowedByAnotherFlag_DoesNotConsumeItAsValue()
    {
        SchemaOptions.Configure(["--schema-profile", "--emit-datatags"]);
        Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Full));
        Assert.That(SchemaOptions.EmitDataTags, Is.True);
    }

    [Test]
    public void Configure_EnvironmentVariable_IsUsedWhenNoFlag_AndFlagWins()
    {
        const string envName = "ROSLYNSENTINEL_SCHEMA_PROFILE";
        var previous = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "lean");

            SchemaOptions.Configure([]);
            Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Lean));

            SchemaOptions.Configure(["--schema-profile=full"]);
            Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Full), "The flag takes precedence over the environment variable.");

            Environment.SetEnvironmentVariable(envName, "nonsense");
            SchemaOptions.Configure([]);
            Assert.That(SchemaOptions.Profile, Is.EqualTo(SchemaProfile.Full), "An invalid environment value keeps Full.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    // ---- ambiguous-target errors name the (hidden-in-Lean) disambiguation parameters ----

    [Test]
    public void AmbiguousSnippetError_NamesLineBeforeAndLineAfter()
    {
        var ex = Assert.Throws<ToolAmbiguousMatchException>(
            () => ContextHelper.FindSnippetPosition("int x = 1; int y = 1;", "int"));

        Assert.That(ex!.Message, Does.Contain("lineBefore"));
        Assert.That(ex.Message, Does.Contain("lineAfter"));
    }

    [Test]
    public void StillAmbiguousSnippetError_NamesLineBeforeAndLineAfter()
    {
        var ex = ContextErrorBuilder.Build(
            SnippetMatchOutcome.StillAmbiguous, "int", SourceText.From("int x = 1; int y = 1;"));

        Assert.That(ex.Message, Does.Contain("lineBefore"));
        Assert.That(ex.Message, Does.Contain("lineAfter"));
    }
}
