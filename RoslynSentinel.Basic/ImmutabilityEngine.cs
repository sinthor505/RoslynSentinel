using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynSentinel.Basic;

public class ImmutabilityEngine
{
    private readonly ISolutionProvider _workspaceManager;

    public ImmutabilityEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Replaces <paramref name = "oldNode"/> with <paramref name = "newNode"/> and formats only the
    /// replaced node (via a tracking annotation), instead of the whole file. Prevents write-back
    /// paths from silently reformatting unrelated code and shifting line numbers below the edit.
    /// </summary>
    private static async Task<string> ReplaceNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken = default)
    {
        var annotation = new SyntaxAnnotation();
        var annotatedNewNode = newNode.WithAdditionalAnnotations(annotation);
        var newRoot = root.ReplaceNode(oldNode, annotatedNewNode);
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        return (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>
    /// Converts a class to be immutable by making fields readonly and properties init-only.
    /// </summary>
    public async Task<DocumentEditResult> MakeClassImmutableAsync(FilePath filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Class not found.",
                UpdatedText = root?.ToFullString() ?? string.Empty
            };
        }

        var newMembers = classNode.Members.Select(member =>
        {
            if (member is FieldDeclarationSyntax field)
            {
                // const fields cannot have readonly — skip them
                if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
                {
                    return field;
                }

                if (!field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
                {
                    return field.AddModifiers(
                        SyntaxFactory.Token(
                            SyntaxFactory.TriviaList(),
                            SyntaxKind.ReadOnlyKeyword,
                            SyntaxFactory.TriviaList(SyntaxFactory.Space)));
                }
            }
            else if (member is PropertyDeclarationSyntax prop)
            {
                var setter = prop.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
                if (setter != null)
                {
                    var initOnly = setter.WithKeyword(SyntaxFactory.Token(SyntaxKind.InitKeyword));
                    return prop.WithAccessorList(prop.AccessorList!.WithAccessors(prop.AccessorList.Accessors.Replace(setter, initOnly)));
                }
            }
            return member;
        });

        var newClass = classNode.WithMembers(SyntaxFactory.List(newMembers));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Class made immutable.",
            UpdatedText = await ReplaceNodeFormattedAsync(document, root!, classNode, newClass, cancellationToken)
        };
    }
}
