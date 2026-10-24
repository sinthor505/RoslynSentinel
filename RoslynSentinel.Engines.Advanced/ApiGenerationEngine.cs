using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Advanced;
public class ApiGenerationEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    public ApiGenerationEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Scans a Web API controller and generates a typed HttpClient for it.
    /// </summary>
    public async Task<DocumentEditResult> GenerateHttpClientForControllerAsync(FilePathWrapper filePath, string controllerName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var controller = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == controllerName);
        if (controller == null)
        {
            throw new ToolNotFoundException($"Controller class '{controllerName}' not found in '{filePath}'. Searched: class declarations in the file. Next: Search(mode: class, query: '{controllerName}') to locate it, or GetFileOutline on the file.", "class", controllerName);
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
        var methods = controller.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)));
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
        // Task<ActionResult<X>> -> Task<X>  (handles nested generics like Dictionary<string,int>)
        if (rawReturn.StartsWith("Task<ActionResult<") && rawReturn.EndsWith(">>"))
        {
            return string.Concat("Task<", rawReturn.AsSpan(18, rawReturn.Length - 20), ">");
        }

        // Task<ActionResult> / Task<IActionResult> -> Task
        if (rawReturn is "Task<ActionResult>" or "Task<IActionResult>")
        {
            return "Task";
        }

        // ActionResult<X> -> Task<X>
        if (rawReturn.StartsWith("ActionResult<") && rawReturn.EndsWith(">"))
        {
            return string.Concat("Task<", rawReturn.AsSpan(13, rawReturn.Length - 14), ">");
        }

        // ActionResult / IActionResult / void -> Task
        if (rawReturn is "ActionResult" or "IActionResult" or "void")
        {
            return "Task";
        }

        // Already Task<X> or Task -> keep as-is
        if (rawReturn.StartsWith("Task"))
        {
            return rawReturn;
        }

        // Wrap synchronous return types
        return $"Task<{rawReturn}>";
    }

    public async Task<DocumentEditResult> AddValidationToPocoAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            throw new ToolInvalidArgumentException($"Syntax root of '{filePath}' is unavailable or not a compilation unit. Next: confirm the file is a plain .cs source file with GetFileOutline.");
        }

        // First add the using directive if not present
        if (!root.Usings.Any(u => u.Name?.ToString() == "System.ComponentModel.DataAnnotations"))
        {
            root = AnnotatedInsertionHelper.AddUsingDirective(root, "System.ComponentModel.DataAnnotations");
        }

        // Now get the classNode from the updated root
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            throw new ToolNotFoundException($"Class '{className}' not found in '{filePath}'. Searched: class declarations in the file after adding the using directive. Next: Search(mode: class, query: '{className}') to confirm the name.", "class", className);
        }

        // Helper: check whether a property already carries an attribute (avoids duplicates)
        static bool HasAttribute(PropertyDeclarationSyntax p, string name) => p.AttributeLists.SelectMany(al => al.Attributes).Any(a =>
        {
            var n = a.Name.ToString();
            return n == name || n == name + "Attribute" || n.EndsWith("." + name) || n.EndsWith("." + name + "Attribute");
        });
        // Replace properties with annotated versions
        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>();
        var newClassNode = classNode.ReplaceNodes(properties, (oldProp, newProp) =>
        {
            var typeStr = newProp.Type.ToString();
            var attributes = new List<AttributeListSyntax>();
            // Add [Required] for string (skip if already present)
            if (typeStr == "string" && !HasAttribute(newProp, "Required"))
            {
                attributes.Add(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.ParseName("Required")))));
            }

            // Add [StringLength] for string (skip if already present)
            if (typeStr == "string" && !HasAttribute(newProp, "StringLength"))
            {
                attributes.Add(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.ParseName("StringLength")).WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(256)))))))));
            }

            // Add [Range] for numeric types (skip if already present)
            if ((typeStr == "int" || typeStr == "decimal" || typeStr == "double" || typeStr == "float") && !HasAttribute(newProp, "Range"))
            {
                attributes.Add(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.ParseName("Range")).WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))), SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(int.MaxValue))) }))))));
            }

            return AnnotatedInsertionHelper.AppendAttributeLists(newProp, attributes);
        });
        var newRoot = root.ReplaceNode(classNode, newClassNode);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = await RoslynFormattingHelper.FormatAnnotatedTextAsync(document, newRoot, cancellationToken),
            FilePath = filePath
        };
    }
}