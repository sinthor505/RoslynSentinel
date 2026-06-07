using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ApiAutomationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ApiAutomationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Scans a Web API controller and generates a typed HttpClient for it.
    /// </summary>
    public async Task<DocumentEditResult> GenerateHttpClientForControllerAsync(FilePath filePath, string controllerName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var controller = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == controllerName);
        if (controller == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var sb = new System.Text.StringBuilder();
        var clientName = controllerName.Replace("Controller", "") + "Client";

        sb.AppendLine("using System.Net.Http.Json;");
        sb.AppendLine();
        sb.AppendLine($"public class {clientName}");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly HttpClient _httpClient;");
        sb.AppendLine($"    public {clientName}(HttpClient httpClient) => _httpClient = httpClient;");
        sb.AppendLine();

        var methods = controller.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)));

        foreach (var method in methods)
        {
            var returnType = ExtractClientReturnType(method.ReturnType.ToString());

            sb.AppendLine($"    public async {returnType} {method.Identifier.Text}Async()");
            sb.AppendLine("    {");
            sb.AppendLine($"        // Logic for calling {method.Identifier.Text}...");
            sb.AppendLine("        return default;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = sb.ToString(),
            FilePath = filePath
        };
    }

    private static string ExtractClientReturnType(string rawReturn)
    {
        // Task<ActionResult<X>> → Task<X>  (handles nested generics like Dictionary<string,int>)
        if (rawReturn.StartsWith("Task<ActionResult<") && rawReturn.EndsWith(">>"))
        {
            return string.Concat("Task<", rawReturn.AsSpan(18, rawReturn.Length - 20), ">");
        }

        // Task<ActionResult> / Task<IActionResult> → Task
        if (rawReturn is "Task<ActionResult>" or "Task<IActionResult>")
        {
            return "Task";
        }

        // ActionResult<X> → Task<X>
        if (rawReturn.StartsWith("ActionResult<") && rawReturn.EndsWith(">"))
        {
            return string.Concat("Task<", rawReturn.AsSpan(13, rawReturn.Length - 14), ">");
        }

        // ActionResult / IActionResult / void → Task
        if (rawReturn is "ActionResult" or "IActionResult" or "void")
        {
            return "Task";
        }

        // Already Task<X> or Task → keep as-is
        if (rawReturn.StartsWith("Task"))
        {
            return rawReturn;
        }

        // Wrap synchronous return types
        return $"Task<{rawReturn}>";
    }
}
