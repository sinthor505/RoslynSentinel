using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Advanced;

public class DocumentationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public DocumentationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<DocumentEditResult> GenerateXmlDocumentationStubsAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var normalizedPath = Path.GetFullPath(filePath);
        var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found in solution: {normalizedPath}");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
                     && !m.GetLeadingTrivia().Any(t =>
                            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                            t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)));

        // newRoot
        var newRoot = root.ReplaceNodes(methods, (oldMethod, newMethod) =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("/// <summary>");
            sb.AppendLine($"/// TODO: Add description for {newMethod.Identifier.Text}.");
            sb.AppendLine("/// </summary>");

            foreach (var param in newMethod.ParameterList.Parameters)
            {
                sb.AppendLine($"/// <param name=\"{param.Identifier.Text}\">TODO: Describe {param.Identifier.Text}.</param>");
            }

            if (newMethod.ReturnType.ToString() != "void" && newMethod.ReturnType.ToString() != "Task")
            {
                sb.AppendLine("/// <returns>TODO: Describe return value.</returns>");
            }

            var xmlTrivia = SyntaxFactory.ParseLeadingTrivia(sb.ToString());
            return newMethod.WithLeadingTrivia(newMethod.GetLeadingTrivia().AddRange(xmlTrivia));
        });

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> DocumentPocoFieldsAsync(FilePath filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Class not found."
            };
        }

        if (!root.Usings.Any(u => u.Name?.ToString() == "System.ComponentModel"))
        {
            root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.ComponentModel")));
            classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        }

        var properties = classNode!.Members.OfType<PropertyDeclarationSyntax>().ToList();
        var newClassNode = classNode.ReplaceNodes(properties, (oldProp, newProp) =>
        {
            var attr = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.ParseName("Description"))
                    .WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal($"Gets or sets the {newProp.Identifier.Text}."))))))));
            return newProp.AddAttributeLists(attr);
        });

        var newRoot = root.ReplaceNode(classNode, newClassNode);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }
}
