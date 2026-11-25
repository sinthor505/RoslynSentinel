using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The checks MoveFile's copyAndObsolete handling runs before it writes anything: the copy gets the destination's folder-derived namespace
/// (<see cref="FolderNamespace.Expected"/>), every top-level type of the old file is copied and later marked [Obsolete]. Each refusal carries
/// a <see cref="ToolErrorCode"/> and a message of facts and options (the parameter names are MoveFile's: destinationPath, namespaceHandling).
/// </summary>
public static class CopyAndObsoleteChecks
{
    private const int MaxListed = 10;

    /// <summary>
    /// Reads <paramref name="source"/> in <paramref name="solution"/> and returns its namespace, the folder-derived namespace of <paramref name="destination"/>
    /// and its top-level types, or a refusal. Refusals, in the order they are tested: either path is not a .cs file (InvalidArgument); the source is not a
    /// document of the solution (NotFound or Ambiguous); the destination is in no loaded project (InvalidArgument); the file has no top-level type or declares
    /// types in more than one namespace (TargetIneligible); a top-level type is partial with parts in other files (TargetIneligible); the new namespace equals the
    /// old one (InvalidArgument). Nothing is written.
    /// </summary>
    public static async Task<CopyAndObsoleteCheckResult> CheckAsync(Solution solution, FilePathWrapper source, FilePathWrapper destination, CancellationToken cancellationToken = default)
    {
        foreach (var path in new[] { source, destination })
        {
            if (!string.Equals(Path.GetExtension(path.Absolute), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                return CopyAndObsoleteCheckResult.Refused(ToolErrorCode.InvalidArgument,
                    $"copyAndObsolete: '{path.Absolute}' is not a .cs file; copying a type and marking the old one [Obsolete] applies to C# files only. Options: use namespaceHandling keep to move the file as it is.");
            }
        }

        var lookup = DocumentLookup.TryGetDocument(solution, source);
        if (!lookup.TryGetDocument(out var document))
        {
            return new CopyAndObsoleteCheckResult(null, null, [], lookup.ToResultError());
        }

        var destinationProject = SolutionProjectLocator.FindContainingProject(solution, destination.Absolute);
        var newNamespace = destinationProject is null ? null : FolderNamespace.Expected(destinationProject, destination);
        if (newNamespace is null)
        {
            var folders = solution.Projects
                .Select(project => project.FilePath is { } projectPath ? Path.GetDirectoryName(projectPath) : null)
                .OfType<string>()
                .Distinct(PathComparison.Comparer)
                .ToList();
            return CopyAndObsoleteCheckResult.Refused(ToolErrorCode.InvalidArgument,
                $"copyAndObsolete: the destination '{destination.Absolute}' is inside no loaded project, so the namespace its folder gives cannot be computed. Searched {folders.Count} project folder(s): {string.Join("; ", folders.Take(MaxListed))}{(folders.Count > MaxListed ? "; ..." : string.Empty)}. Options: pass a {MoveFileParameterNames.DestinationPath} under one of those folders, or use namespaceHandling keep, which does not compute a namespace.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
        {
            return CopyAndObsoleteCheckResult.Refused(ToolErrorCode.PreconditionNotMet,
                $"copyAndObsolete: '{source.Absolute}' has no syntax tree or semantic model in the loaded solution, so its types could not be read. Options: use namespaceHandling keep to move the file as it is.");
        }

        // Top-level types only: the walk enters the compilation unit and namespaces but never a type, so nested types are not listed.
        var declarations = root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .ToList();
        var types = declarations.Select(declaration => new TopLevelType(KindOf(declaration), NameOf(declaration), HasStaticFields(declaration))).ToList();
        if (declarations.Count == 0)
        {
            return CopyAndObsoleteCheckResult.Refused(null, newNamespace, types, ToolErrorCode.TargetIneligible,
                $"copyAndObsolete: '{source.Absolute}' declares no top-level type (class, struct, interface, enum, record or delegate), so there is no type to copy and mark [Obsolete]. Options: use namespaceHandling keep to move the file as it is.");
        }

        var namespaces = declarations.Select(DeclaredNamespace).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (namespaces.Count > 1)
        {
            return CopyAndObsoleteCheckResult.Refused(null, newNamespace, types, ToolErrorCode.TargetIneligible,
                $"copyAndObsolete: '{source.Absolute}' declares top-level types in {namespaces.Count} namespaces ({string.Join(", ", namespaces.Select(Describe))}), but the copy gets one namespace, '{newNamespace}'. Options: split the file so each file holds one namespace, or use namespaceHandling keep to move the file as it is.");
        }

        var oldNamespace = namespaces[0];
        var partialParts = new List<string>();
        foreach (var declaration in declarations)
        {
            if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol)
            {
                continue;
            }

            var otherFiles = symbol.DeclaringSyntaxReferences
                .Select(reference => reference.SyntaxTree.FilePath)
                .Where(filePath => !string.IsNullOrEmpty(filePath) && !PathComparison.Comparer.Equals(filePath, document.FilePath ?? string.Empty))
                .Distinct(PathComparison.Comparer)
                .ToList();
            if (otherFiles.Count > 0)
            {
                partialParts.Add($"'{symbol.Name}' (other parts in {string.Join(", ", otherFiles)})");
            }
        }

        if (partialParts.Count > 0)
        {
            return CopyAndObsoleteCheckResult.Refused(oldNamespace, newNamespace, types, ToolErrorCode.TargetIneligible,
                $"copyAndObsolete: {(partialParts.Count == 1 ? "the type" : "the types")} {string.Join("; ", partialParts)} in '{source.Absolute}' {(partialParts.Count == 1 ? "is" : "are")} partial with parts outside this file, so a copy of the file would hold only some of the parts. Options: merge the parts into this file first, or use namespaceHandling keep, which moves this file's part unchanged.");
        }

        if (string.Equals(oldNamespace, newNamespace, StringComparison.Ordinal))
        {
            return CopyAndObsoleteCheckResult.Refused(oldNamespace, newNamespace, types, ToolErrorCode.InvalidArgument,
                $"copyAndObsolete: the destination '{destination.Absolute}' (project '{destinationProject!.Name}') gives the namespace {Describe(newNamespace)}, which is the namespace {Describe(oldNamespace)} that '{source.Absolute}' already declares, so the copy and the original would declare the same type names in one namespace. Options: pass a {MoveFileParameterNames.DestinationPath} in a folder whose namespace differs, or use namespaceHandling keep to move the file without a copy.");
        }

        return new CopyAndObsoleteCheckResult(oldNamespace, newNamespace, types, null);
    }

    // The namespace a declaration sits in, from its enclosing namespace declarations (outermost first); the empty string for the global namespace.
    private static string DeclaredNamespace(SyntaxNode declaration) => string.Join(".", declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString()));

    private static string Describe(string ns) => ns.Length == 0 ? "the global namespace" : $"'{ns}'";

    private static string NameOf(SyntaxNode declaration) => declaration switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string KindOf(SyntaxNode declaration) => declaration.Kind() switch
    {
        SyntaxKind.ClassDeclaration => "class",
        SyntaxKind.StructDeclaration => "struct",
        SyntaxKind.InterfaceDeclaration => "interface",
        SyntaxKind.EnumDeclaration => "enum",
        SyntaxKind.RecordDeclaration => "record",
        SyntaxKind.RecordStructDeclaration => "record struct",
        SyntaxKind.DelegateDeclaration => "delegate",
        var other => other.ToString(),
    };

    // Counts static state that is copied with the type: a static non-const field, a static field-like event, or a static auto-property (no body on the property or its accessors). Const fields and static properties with a body do not count. Nested types count because they are copied with the type.
    private static bool HasStaticFields(SyntaxNode declaration) => declaration.DescendantNodes().Any(node => node switch
    {
        FieldDeclarationSyntax field => field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword),
        EventFieldDeclarationSyntax evt => evt.Modifiers.Any(SyntaxKind.StaticKeyword),
        PropertyDeclarationSyntax property => property.Modifiers.Any(SyntaxKind.StaticKeyword)
            && property.ExpressionBody == null
            && property.AccessorList != null
            && property.AccessorList.Accessors.All(accessor => accessor.Body == null && accessor.ExpressionBody == null),
        _ => false,
    });
}
