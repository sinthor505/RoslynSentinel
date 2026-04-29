using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

namespace RoslynSentinel.Server;

public record GenerationResult(string FilePath, string Content);

public class CodeGenerationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public CodeGenerationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Generates C# classes from a JSON string.
    /// </summary>
    public GenerationResult GenerateClassesFromJson(string json, string rootClassName, string @namespace)
    {
        using var document = JsonDocument.Parse(json);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {@namespace};");
        sb.AppendLine();

        ProcessElement(document.RootElement, rootClassName, sb);

        return new GenerationResult($"{rootClassName}.cs", sb.ToString());
    }

    private void ProcessElement(JsonElement element, string className, System.Text.StringBuilder sb)
    {
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var type = GetCSharpType(property.Value, property.Name);
                sb.AppendLine($"    [JsonPropertyName(\"{property.Name}\")]");
                sb.AppendLine($"    public {type} {Capitalize(property.Name)} {{ get; set; }}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // Process nested objects
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    ProcessElement(property.Value, Capitalize(property.Name), sb);
                }
                else if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0 && property.Value[0].ValueKind == JsonValueKind.Object)
                {
                    ProcessElement(property.Value[0], Capitalize(property.Name), sb);
                }
            }
        }
    }

    private string GetCSharpType(JsonElement element, string propertyName)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "double",
            JsonValueKind.True => "bool",
            JsonValueKind.False => "bool",
            JsonValueKind.Object => Capitalize(propertyName),
            JsonValueKind.Array => $"List<{GetCSharpType(element.EnumerateArray().FirstOrDefault(), propertyName)}>",
            _ => "object"
        };
    }

    private string Capitalize(string name) => char.ToUpper(name[0]) + name.Substring(1);

    /// <summary>
    /// Scans a project for configuration usage (e.g. config["Key"]) and generates a JSON config file.
    /// </summary>
    public async Task<string> GenerateDefaultConfigJsonAsync(string projectName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name == projectName);
        if (project == null) throw new Exception("Project not found.");

        var configKeys = new Dictionary<string, object>();

        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            // Look for config["Key"] or config.GetValue<T>("Key")
            var accessors = root.DescendantNodes().OfType<ElementAccessExpressionSyntax>();
            foreach (var access in accessors)
            {
                if (access.ArgumentList.Arguments.Count == 1 && 
                    access.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var key = literal.Token.ValueText;
                    if (!configKeys.ContainsKey(key)) configKeys[key] = "TODO: Set default value";
                }
            }
        }

        return JsonSerializer.Serialize(configKeys, new JsonSerializerOptions { WriteIndented = true });
    }
}
