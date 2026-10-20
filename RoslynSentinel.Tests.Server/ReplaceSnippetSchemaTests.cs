// Guards the emitted ReplaceSnippetTool schema (blocker: replacesnippet_schema_invites_anchor_only_edits, slice 2):
// each batchEdits item lists oldContent as required, and lineBefore/lineAfter describe oldContent anchors
// rather than the nonexistent contextSnippet.
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ReplaceSnippetSchemaTests
{
    private const string ExpectedAnchorText =
        "Optional extra filter, applied after matchLine/withinMember: the single line just before the first line (lineBefore) or just after the last line (lineAfter) of oldContent.";

    private static JsonObject BuildSchema()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var method = typeof(WorkspaceTools).GetMethod(nameof(WorkspaceTools.ReplaceSnippetTool))!;
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
            Assert.That(props["lineBefore"]!["description"]!.GetValue<string>(), Is.EqualTo(ToolParams.ReplaceSnippetAnchorLine));
            Assert.That(props["lineAfter"]!["description"]!.GetValue<string>(), Is.EqualTo(ToolParams.ReplaceSnippetAnchorLine));
        }
    }

    [Test]
    public void WithinMemberAndMatchLine_AreEmittedAsOptionalProperties_TopLevelAndInBatchItems()
    {
        var schema = BuildSchema();
        var item = ResolveItemSchema(schema);

        foreach (var node in new[] { schema, item })
        {
            var props = (JsonObject)node["properties"]!;
            Assert.That(props.ContainsKey("withinMember"), Is.True, "withinMember must be emitted");
            Assert.That(props.ContainsKey("matchLine"), Is.True, "matchLine must be emitted");
            if (node == schema)
            {
                // Batch item properties come from SnippetEdit's XML-free POCO shape and carry no description.
                Assert.That(props["withinMember"]!["description"]!.GetValue<string>(), Is.EqualTo(ToolParams.ReplaceSnippetWithinMember));
                Assert.That(props["matchLine"]!["description"]!.GetValue<string>(), Is.EqualTo(ToolParams.ReplaceSnippetMatchLine));
            }

            var required = ((JsonArray?)node["required"])?.Select(n => n!.GetValue<string>()).ToList() ?? [];
            Assert.That(required, Does.Not.Contain("withinMember"));
            Assert.That(required, Does.Not.Contain("matchLine"));
        }
    }
}
