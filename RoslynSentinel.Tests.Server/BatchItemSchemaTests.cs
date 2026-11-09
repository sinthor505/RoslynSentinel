// Guards the emitted batch item schemas (BatchItemFieldAttribute via McpToolSchemaPatcher.ApplyBatchItemFields):
// each batch item lists the fields its item type requires, and every required field carries a description.
// Reads the same emitted schema the client receives, as ReplaceSnippetSchemaTests does.
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class BatchItemSchemaTests
{
    private static (Type ToolClass, string MethodName) ToolFor(string toolName) => toolName switch
    {
        "ReplaceSnippet" => (typeof(WorkspaceTools), nameof(WorkspaceTools.ReplaceSnippetTool)),
        "ModifyModifier" => (typeof(RefactoringStructuralTools), nameof(RefactoringStructuralTools.ModifyModifierTool)),
        "ModifyAttribute" => (typeof(RefactoringStructuralTools), nameof(RefactoringStructuralTools.ModifyAttributeTool)),
        "ModifyBaseType" => (typeof(RefactoringStructuralTools), nameof(RefactoringStructuralTools.ModifyBaseTypeTool)),
        "Declaration" => (typeof(DeclarationTools), nameof(DeclarationTools.DeclarationTool)),
        _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName, null),
    };

    private static JsonObject BuildSchema(string toolName)
    {
        var (toolClass, methodName) = ToolFor(toolName);
        var services = new ServiceCollection().BuildServiceProvider();
        var method = toolClass.GetMethod(methodName)!;
        var tool = McpToolSchemaPatcher.CreateSentinelTool(method, toolClass, services);
        return (JsonObject)JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
    }

    private static JsonObject ResolveItemSchema(JsonObject schema, string parameterName)
    {
        var items = (JsonObject)schema["properties"]![parameterName]!["items"]!;
        if (items["$ref"] is JsonValue refValue && refValue.TryGetValue(out string? refPath) && refPath is not null)
        {
            return (JsonObject)schema["$defs"]![refPath["#/$defs/".Length..]]!;
        }

        return items;
    }

    [TestCase("ReplaceSnippet", "batchEdits", "filePath,oldContent,newContent")]
    [TestCase("ModifyModifier", "batchEdits", "filePath,targetName,modifier,action")]
    [TestCase("ModifyAttribute", "batchEdits", "filePath,targetName,action")]
    [TestCase("ModifyBaseType", "batchEdits", "filePath,typeName,baseTypeName,action")]
    [TestCase("Declaration", "modifierBatchEdits", "filePath,targetName,modifier,action")]
    [TestCase("Declaration", "attributeBatchEdits", "filePath,targetName,action")]
    [TestCase("Declaration", "baseTypeBatchEdits", "filePath,typeName,baseTypeName,action")]
    public void BatchItem_ListsRequiredFields_AndDescribesEachOfThem(string toolName, string parameterName, string requiredCsv)
    {
        var item = ResolveItemSchema(BuildSchema(toolName), parameterName);

        var required = ((JsonArray)item["required"]!).Select(n => n!.GetValue<string>()).ToList();
        var properties = (JsonObject)item["properties"]!;
        foreach (var field in requiredCsv.Split(','))
        {
            Assert.That(required, Does.Contain(field), $"{toolName}.{parameterName} must list {field} as required");
            var description = properties[field]?["description"]?.GetValue<string>();
            Assert.That(description, Is.Not.Null.And.Not.Empty, $"{toolName}.{parameterName}.{field} needs a description");
        }
    }
}
