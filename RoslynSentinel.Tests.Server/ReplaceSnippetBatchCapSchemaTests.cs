// Guards the emitted ReplaceSnippetTool batchEdits description (plan step 34, R8b): the batch cap sentence
// is appended to the existing ToolParams.SnippetEdits text, never replacing it.
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ReplaceSnippetBatchCapSchemaTests
{
    private static JsonObject BuildSchema()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var method = typeof(WorkspaceTools).GetMethod(nameof(WorkspaceTools.ReplaceSnippetTool))!;
        var tool = McpToolSchemaPatcher.CreateSentinelTool(method, typeof(WorkspaceTools), services);
        return (JsonObject)JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
    }

    [Test]
    public void BatchEditsDescription_ContainsMaxEditsPerBatchAndEditsPerCallWording()
    {
        var description = BuildSchema()["properties"]!["batchEdits"]!["description"]!.GetValue<string>();

        Assert.That(description, Does.Contain(ReplaceSnippetOptions.MaxEditsPerBatch.ToString()));
        Assert.That(description, Does.Contain("edits per call"));
    }

    [Test]
    public void BatchEditsDescription_KeepsOriginalText_AndAppendsCapSentence()
    {
        var description = BuildSchema()["properties"]!["batchEdits"]!["description"]!.GetValue<string>();

        Assert.That(description, Does.StartWith(ToolParams.SnippetEdits));
        Assert.That(description, Does.EndWith($" Limit: {ReplaceSnippetOptions.MaxEditsPerBatch} edits per call; split larger batches."));
    }

    [Test]
    public void BatchEditsDescription_CapSentenceAppearsExactlyOnce()
    {
        var description = BuildSchema()["properties"]!["batchEdits"]!["description"]!.GetValue<string>();

        var sentence = $"Limit: {ReplaceSnippetOptions.MaxEditsPerBatch} edits per call; split larger batches.";
        Assert.That(description.IndexOf(sentence, StringComparison.Ordinal), Is.EqualTo(description.LastIndexOf(sentence, StringComparison.Ordinal)));
    }
}
