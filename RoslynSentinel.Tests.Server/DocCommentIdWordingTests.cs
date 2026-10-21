// Guards the schema wording that stops models constructing docCommentId values by hand:
// the sentence "From Search(mode: symbol), pass exactly as returned, do not construct." must reach
// the tool schema for every docCommentId parameter that takes a Search(mode: symbol) id.
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class DocCommentIdWordingTests
{
    private static JsonObject BuildSchema(Type declaringType, string methodName)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var method = declaringType.GetMethod(methodName)!;
        var tool = McpToolSchemaPatcher.CreateSentinelTool(method, declaringType, services);
        return (JsonObject)JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
    }

    private static string ParameterDescription(JsonObject schema, string parameterName)
    {
        return schema["properties"]![parameterName]!["description"]!.GetValue<string>();
    }

    [Test]
    public void DeleteUnusedSymbolFacade_DocCommentIdSaysDoNotConstruct_AndFilePathIsRequired()
    {
        var schema = BuildSchema(typeof(WorkspaceTools), nameof(WorkspaceTools.DeleteUnusedSymbolTool));

        Assert.That(ParameterDescription(schema, "docCommentId"), Does.Contain("do not construct"));
        var filePathDescription = ParameterDescription(schema, "filePath");
        Assert.That(filePathDescription, Is.Not.Empty);
        Assert.That(filePathDescription, Does.Contain("required"));
    }

    [Test]
    public void DeleteUnusedSymbolSplit_DocCommentIdSaysDoNotConstruct_AndFilePathIsRequired()
    {
        var schema = BuildSchema(typeof(WorkspaceProjectManagementTools), nameof(WorkspaceProjectManagementTools.DeleteUnusedSymbolTool));

        Assert.That(ParameterDescription(schema, "docCommentId"), Does.Contain("do not construct"));
        var filePathDescription = ParameterDescription(schema, "filePath");
        Assert.That(filePathDescription, Is.Not.Empty);
        Assert.That(filePathDescription, Does.Contain("required"));
    }

    [Test]
    public void NamedArgumentsTool_TargetSymbolSaysDoNotConstruct()
    {
        var schema = BuildSchema(typeof(NamedArgumentsTools), nameof(NamedArgumentsTools.NamedArgumentsTool));

        Assert.That(ParameterDescription(schema, "targetSymbol"), Does.Contain("do not construct"));
    }
}
