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

        // Replace properties with annotated versions
        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>();
        var newClassNode = classNode.ReplaceNodes(properties, (oldProp, newProp) => 
        {
            var typeStr = newProp.Type.ToString();
            var attributes = new List<AttributeListSyntax>();

            // Add [Required] for string
            if (typeStr == "string")
            {
                attributes.Add(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(SyntaxFactory.ParseName("Required")))));
            }

            // Add [StringLength] for string
            if (typeStr == "string")
            {
                attributes.Add(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(SyntaxFactory.ParseName("StringLength")).WithArgumentList(
                        SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(256)))))))));
            }

            // Add [Range] for numeric types
            if (typeStr == "int" || typeStr == "decimal" || typeStr == "double" || typeStr == "float")
            {
                attributes.Add(SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(SyntaxFactory.ParseName("Range")).WithArgumentList(
                        SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(new[]
                        {
                            SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))),
                            SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(int.MaxValue)))
                        }))))));
            }

            return newProp.WithAttributeLists(newProp.AttributeLists.AddRange(attributes));
        });

        var newRoot = root.ReplaceNode(classNode, newClassNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
