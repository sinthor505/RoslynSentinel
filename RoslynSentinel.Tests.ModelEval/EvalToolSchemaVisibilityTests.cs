using System.Text.Json;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Checks the validateOnApply contract through worlds built by <see cref="ModelEvalMcpEnvironment.CreateAsync"/>: the default
/// world hides validateOnApply from the listed schemas and refuses validateOnApply:false; fullToolSchema: true offers it.
/// Not a live-model test: CreateAsync needs no LM Studio and the agent client is never called.
/// </summary>
// SchemaOptions, HiddenSchemaParams and the ToolArgumentValidator caches are process-static and every world resets them,
// so these tests must not run in parallel with other world-building fixtures.
[TestFixture]
[NonParallelizable]
public class EvalToolSchemaVisibilityTests
{
    private const string ReplaceSnippetName = "ReplaceSnippet";
    private const string ApplyDiffName = "ApplyDiff";
    private const string ValidateOnApply = "validateOnApply";
    private const string RefusalMarker = "Nothing was executed";
    private const string SnippetFile = "RoslynSentinel.Tools.Basic/WorkspaceTools.cs";

    [Test]
    public async Task Default_ValidateOnApplyIsHiddenFromTheListedSchemas()
    {
        await using var world = await CreateWorldAsync(fullToolSchema: false);
        var parameters = ParameterNamesByTool(await ListToolsAsync(world));

        // ApplyDiff is checked only when the default eval surface lists it (Batch A found it absent from the Workspace/Refactor surface).
        var checkedTools = new List<string> { ReplaceSnippetName };
        var applyDiffListed = parameters.ContainsKey(ApplyDiffName);
        TestContext.Out.WriteLine($"Default eval surface lists ApplyDiff: {applyDiffListed}");
        if (applyDiffListed)
        {
            checkedTools.Add(ApplyDiffName);
        }

        Assert.That(parameters.ContainsKey(ReplaceSnippetName), Is.True, "the default surface must list ReplaceSnippet");
        Assert.That(parameters[ReplaceSnippetName], Does.Contain("filePath"), "ReplaceSnippet schema has no properties, so the check below would be vacuous");

        Assert.Multiple(() =>
        {
            foreach (var tool in checkedTools)
            {
                Assert.That(parameters[tool].Contains(ValidateOnApply), Is.False, $"{tool} still lists validateOnApply in the default world");
            }
        });
    }

    [Test]
    public async Task Default_OtherBoilerplateParametersStayListed()
    {
        string[] boilerplate = ["returnDiff", "lineBefore", "lineAfter"];

        Dictionary<string, HashSet<string>> fullParameters;
        {
            await using var full = await CreateWorldAsync(fullToolSchema: true);
            fullParameters = ParameterNamesByTool(await ListToolsAsync(full));
        }

        Dictionary<string, HashSet<string>> defaultParameters;
        {
            await using var defaults = await CreateWorldAsync(fullToolSchema: false);
            defaultParameters = ParameterNamesByTool(await ListToolsAsync(defaults));
        }

        // Only the boilerplate parameters the full world actually lists are expected in the default world.
        var expected = fullParameters
            .SelectMany(tool => boilerplate.Where(tool.Value.Contains).Select(parameter => (Tool: tool.Key, Parameter: parameter)))
            .ToList();
        TestContext.Out.WriteLine($"Full world lists {expected.Count} boilerplate parameters: {string.Join(", ", expected.Select(e => $"{e.Tool}.{e.Parameter}"))}");

        Assert.That(expected, Is.Not.Empty, "the full world lists no boilerplate parameter, so this test would pass vacuously");

        var missing = expected
            .Where(e => !defaultParameters.TryGetValue(e.Tool, out var names) || !names.Contains(e.Parameter))
            .Select(e => $"{e.Tool}.{e.Parameter}")
            .ToList();
        Assert.That(missing, Is.Empty, "boilerplate parameters listed by the full world but missing from the default world");
    }

    [Test]
    public async Task Default_ValidateOnApplyFalse_IsRefusedThroughTheClient()
    {
        await using var world = await CreateWorldAsync(fullToolSchema: false);

        var text = await CallValidateOnApplyFalseAsync(world);
        TestContext.Out.WriteLine($"Default world reply: {text}");

        Assert.That(text, Does.Contain(RefusalMarker));
    }

    [Test]
    public async Task OptIn_FullToolSchema_ListsValidateOnApply_AndAcceptsFalse()
    {
        await using var world = await CreateWorldAsync(fullToolSchema: true);
        var parameters = ParameterNamesByTool(await ListToolsAsync(world));

        Assert.That(parameters.ContainsKey(ReplaceSnippetName), Is.True, "the full surface must list ReplaceSnippet");
        Assert.That(parameters[ReplaceSnippetName].Contains(ValidateOnApply), Is.True, "fullToolSchema: true must list validateOnApply on ReplaceSnippet");

        // The call may still fail for other reasons (the sample file may not exist); only the refusal is ruled out.
        var text = await CallValidateOnApplyFalseAsync(world);
        TestContext.Out.WriteLine($"Full world reply: {text}");

        Assert.That(text, Does.Not.Contain(RefusalMarker));
    }

    [Test]
    public async Task OptIn_AfterADefaultWorld_IsNotRefused()
    {
        {
            await using var defaults = await CreateWorldAsync(fullToolSchema: false);
            Assert.That(await CallValidateOnApplyFalseAsync(defaults), Does.Contain(RefusalMarker), "the default world must refuse first");
        }

        await using var full = await CreateWorldAsync(fullToolSchema: true);
        var text = await CallValidateOnApplyFalseAsync(full);
        TestContext.Out.WriteLine($"Full world after a default world: {text}");

        Assert.That(text, Does.Not.Contain(RefusalMarker), "a full world built after a default world must not inherit the refusal");
    }

    [Test]
    public async Task Default_AfterAnOptInWorld_HidesAgain()
    {
        {
            await using var full = await CreateWorldAsync(fullToolSchema: true);
            var fullParameters = ParameterNamesByTool(await ListToolsAsync(full));
            Assert.That(fullParameters[ReplaceSnippetName].Contains(ValidateOnApply), Is.True, "the full world must offer validateOnApply first");
        }

        await using var defaults = await CreateWorldAsync(fullToolSchema: false);
        var parameters = ParameterNamesByTool(await ListToolsAsync(defaults));
        Assert.That(parameters[ReplaceSnippetName].Contains(ValidateOnApply), Is.False, "a default world built after a full world must hide validateOnApply again");

        var text = await CallValidateOnApplyFalseAsync(defaults);
        Assert.That(text, Does.Contain(RefusalMarker), "a default world built after a full world must refuse validateOnApply:false again");
    }

    [Test]
    public void NoRegisteredScenarioOptsIn()
    {
        var implementationScenarios = ImplementationScenarioRegistry.All;
        var definitions = ScenarioRegistry.All;

        Assert.Multiple(() =>
        {
            Assert.That(implementationScenarios, Is.Not.Empty, "no implementation scenarios found, so the check would be vacuous");
            Assert.That(definitions, Is.Not.Empty, "no scenario definitions found, so the check would be vacuous");
            foreach (var scenario in implementationScenarios)
            {
                Assert.That(scenario.OfferFullToolSchema, Is.False, $"implementation scenario {scenario.Id} opts into the full schema");
            }

            foreach (var definition in definitions)
            {
                Assert.That(definition.OfferFullToolSchema, Is.False, $"scenario {definition.Id} opts into the full schema");
            }
        });
    }

    private static string NewSampleDirectory() =>
        Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

    private static Task<ModelEvalMcpEnvironment> CreateWorldAsync(bool fullToolSchema) =>
        ModelEvalMcpEnvironment.CreateAsync(
            NewSampleDirectory(),
            restorePackages: false,
            beforeLoad: null,
            afterLoad: null,
            TestContext.CurrentContext.CancellationToken,
            fullToolSchema: fullToolSchema);

    private static async Task<IList<McpClientTool>> ListToolsAsync(ModelEvalMcpEnvironment environment) =>
        await environment.Client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);

    private static Dictionary<string, HashSet<string>> ParameterNamesByTool(IEnumerable<McpClientTool> tools)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    names.Add(property.Name);
                }
            }

            result[tool.Name] = names;
        }

        return result;
    }

    private static async Task<string> CallValidateOnApplyFalseAsync(ModelEvalMcpEnvironment environment)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["reason"] = "Checks that validateOnApply false is refused through the client",
            ["action"] = "validate",
            ["filePath"] = SnippetFile,
            ["oldContent"] = "placeholder",
            ["newContent"] = "replacement",
            [ValidateOnApply] = false,
        };

        var result = await environment.Client.CallToolAsync(
            ReplaceSnippetName,
            arguments,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }
}
