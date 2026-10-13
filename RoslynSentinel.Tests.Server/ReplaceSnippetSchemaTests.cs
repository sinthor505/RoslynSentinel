// Guards the emitted ReplaceSnippet schema (blocker: replacesnippet_schema_invites_anchor_only_edits, slice 2):
// each batchEdits item lists oldContent as required, and lineBefore/lineAfter describe oldContent anchors
// rather than the nonexistent contextSnippet.
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ReplaceSnippetSchemaTests
{
    private const string ExpectedAnchorText =
        "Single line adjacent to oldContent; only needed when oldContent matches more than once.";

    private static JsonObject BuildSchema()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var method = typeof(WorkspaceTools).GetMethod(nameof(WorkspaceTools.ReplaceSnippet))!;
        var tool = McpToolSchemaPatcher.CreateSentinelTool(method, typeof(WorkspaceTools), services);
        return (JsonObject)JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
    }

    private static JsonObject ResolveItemSchema(JsonObject schema)
    {
        var items = (JsonObject)schema["properties"]!["batchEdits"]!["items"]!;
        if (items["$ref"] is JsonValue refValue && refValue.TryGetValue(out string? refPath) && refPath is not null)
        {
            return (JsonObject)schema["$defs"]![refPath["#/$defs/".Length..]]!;
        }

        return items;
    }

    [Test]
    public void BatchEditsItem_ListsOldContentAsRequired()
    {
        var item = ResolveItemSchema(BuildSchema());

        var required = ((JsonArray)item["required"]!).Select(n => n!.GetValue<string>()).ToList();
        Assert.That(required, Does.Contain("oldContent"));
    }

    [Test]
    public void TopLevelOldContent_StaysOptional()
    {
        var schema = BuildSchema();

        var required = ((JsonArray?)schema["required"])?.Select(n => n!.GetValue<string>()).ToList() ?? [];
        Assert.That(required, Does.Not.Contain("oldContent"));
        Assert.That(required, Does.Not.Contain("batchEdits"));
    }

    [Test]
    public void LineBeforeAndAfter_DescribeOldContentAnchors_TopLevelAndInBatchItems()
    {
        var schema = BuildSchema();
        var item = ResolveItemSchema(schema);

        foreach (var props in new[] { (JsonObject)schema["properties"]!, (JsonObject)item["properties"]! })
        {
            Assert.That(props["lineBefore"]!["description"]!.GetValue<string>(), Is.EqualTo(ExpectedAnchorText));
            Assert.That(props["lineAfter"]!["description"]!.GetValue<string>(), Is.EqualTo(ExpectedAnchorText));
        }
    }
}
