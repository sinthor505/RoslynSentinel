using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

using ModelContextProtocol;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class AppliedChangeSummaryWireShapeTests
{
    // Wire text of the no-stage summary built by BuildNoStageSummary, captured from the current code. Raw literal: backslashes are JSON-escaped path text.
    private const string ExpectedJson = """{"ChangeId":null,"AffectedFiles":["C:\\tmp\\a.cs","C:\\tmp\\b.cs"],"Description":"wire shape","DryRun":false,"Diff":"wire diff","WorkspaceVersion":null,"Validated":false,"LineChanges":null,"Status":"not_written","ChangedContent":{"C:\\tmp\\a.cs":"class A { }","C:\\tmp\\b.cs":"class B { }"},"Note":"Nothing was written (autoStage=false). The proposed file contents are in changedContent; apply them yourself, or re-call with autoStage: true to have the server apply and validate the change."}""";

    // Published-schema node for changedContent, captured from the current code (the McpToolSchemaPatcher transform leaves it as an object schema).
    private const string ExpectedChangedContentSchema = """{"type":["object","null"],"additionalProperties":{"type":"string"}}""";

    [SetUp]
    [TearDown]
    public void ResetContext() => ToolCallContext.Set(null);

    // No-stage result (dryRun false, validated false, ChangeId null): the only shape where ChangedContent survives serialization.
    private static AppliedChangeSummary BuildNoStageSummary()
    {
        var a = new FilePathWrapper(@"C:\tmp\a.cs");
        var b = new FilePathWrapper(@"C:\tmp\b.cs");
        var changedContent = new DocumentChangeSet
        {
            { a, "class A { }" },
            { b, "class B { }" },
        };
        return AppliedChangeSummary.From(
            lineChanges: null,
            findings: null,
            affectedFiles: new List<FilePathWrapper> { a, b },
            description: "wire shape",
            dryRun: false,
            diff: "wire diff",
            changedContent: changedContent,
            validated: false);
    }

    // Guards the DocumentChangeSet retype of ChangedContent (plan_document_changeset_type Step 6d).
    [Test]
    public void ChangedContent_SerializedWireJson_IsPinned()
    {
        var summary = BuildNoStageSummary();
        Assert.That(summary.ChangedContent, Is.Not.Null, "precondition: a no-stage result keeps ChangedContent");

        var json = JsonSerializer.Serialize(summary, SharedJsonOptions.Compact);

        Assert.That(json, Is.EqualTo(ExpectedJson));
    }

    // Guards the DocumentChangeSet retype of ChangedContent (plan_document_changeset_type Step 6d).
    [Test]
    public void ChangedContent_PublishedSchemaNode_IsPinned()
    {
        // Same creation call as SentinelToolFactory's OutputSchema: default MCP serializer options plus the McpToolSchemaPatcher transform.
        var schemaElement = AIJsonUtilities.CreateJsonSchema(
            typeof(AppliedChangeSummary),
            serializerOptions: McpJsonUtilities.DefaultOptions,
            inferenceOptions: McpToolSchemaPatcher.SchemaCreateOptions);
        var schema = JsonNode.Parse(schemaElement.GetRawText())!;
        var node = schema["properties"]?["changedContent"];
        Assert.That(node, Is.Not.Null, "published schema has a changedContent property");

        Assert.That(node!.ToJsonString(SharedJsonOptions.Compact), Is.EqualTo(ExpectedChangedContentSchema));
    }
}
