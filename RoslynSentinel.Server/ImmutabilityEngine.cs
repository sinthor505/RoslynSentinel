using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ImmutabilityEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ImmutabilityEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Converts a class to be immutable by making fields readonly and properties init-only.
    /// </summary>
    public async Task<string> MakeClassImmutableAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) return root?.ToFullString() ?? "";

        var newMembers = classNode.Members.Select(member =>
        {
            if (member is FieldDeclarationSyntax field)
            {
                // const fields cannot have readonly — skip them
                if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
                    return field;
                if (!field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
                    return field.AddModifiers(
                        SyntaxFactory.Token(
                            SyntaxFactory.TriviaList(),
                            SyntaxKind.ReadOnlyKeyword,
                            SyntaxFactory.TriviaList(SyntaxFactory.Space)));
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
        var newRoot = root!.ReplaceNode(classNode, newClass);
        return newRoot.ToFullString();
    }
}
