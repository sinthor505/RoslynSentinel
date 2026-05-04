using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ApiIntegrationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ApiIntegrationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<string> AddValidationToPocoAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null) throw new Exception("Could not parse syntax root.");

        // First add the using directive if not present
        if (!root.Usings.Any(u => u.Name.ToString() == "System.ComponentModel.DataAnnotations"))
        {
            root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.ComponentModel.DataAnnotations")));
        }

        // Now get the classNode from the updated root
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) throw new Exception("Class not found.");

        // Helper: check whether a property already carries an attribute (avoids duplicates)
        static bool HasAttribute(PropertyDeclarationSyntax p, string name) =>
            p.AttributeLists.SelectMany(al => al.Attributes)
             .Any(a =>
             {
                 var n = a.Name.ToString();
                 return n == name || n == name + "Attribute" ||
                        n.EndsWith("." + name) || n.EndsWith("." + name + "Attribute");
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
                attributes.Add(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(SyntaxFactory.ParseName("Required")))));
            }

            // Add [StringLength] for string (skip if already present)
            if (typeStr == "string" && !HasAttribute(newProp, "StringLength"))
            {
                attributes.Add(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(SyntaxFactory.ParseName("StringLength")).WithArgumentList(
                        SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(256)))))))));
            }

            // Add [Range] for numeric types (skip if already present)
            if ((typeStr == "int" || typeStr == "decimal" || typeStr == "double" || typeStr == "float")
                && !HasAttribute(newProp, "Range"))
            {
                attributes.Add(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(SyntaxFactory.ParseName("Range")).WithArgumentList(
                        SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(new[]
                        {
                            SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))),
                            SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(int.MaxValue)))
                        }))))));
            }

            return attributes.Count == 0
                ? newProp
                : newProp.WithAttributeLists(newProp.AttributeLists.AddRange(attributes));
        });

        var newRoot = root.ReplaceNode(classNode, newClassNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
