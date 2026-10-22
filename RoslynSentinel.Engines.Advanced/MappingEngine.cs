using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Engines.Advanced;

public class MappingEngine
{
    private readonly IWorkspaceReader _workspaceManager;

    public MappingEngine(IWorkspaceReader workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>Resolves a type by fully-qualified metadata name first, then by simple name; null when neither finds it.</summary>
    private static INamedTypeSymbol? ResolveType(Compilation? compilation, string typeName, CancellationToken cancellationToken) =>
        compilation?.GetTypeByMetadataName(typeName)
        ?? compilation?.GetSymbolsWithName(n => n == typeName || n == typeName.Split('.').Last(), SymbolFilter.Type, cancellationToken)
            .OfType<INamedTypeSymbol>().FirstOrDefault();

    /// <summary>
    /// Generates a mapping method between two types based on property names.
    /// </summary>
    public async Task<DocumentEditResult> GenerateMappingAsync(
        FilePathWrapper filePath,
        string fromType,
        string toType,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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

        var compilation = await document.Project.GetCompilationAsync(cancellationToken);

        // Try fully-qualified name first, then fall back to simple name search
        var fromSymbol = ResolveType(compilation, fromType, cancellationToken);
        var toSymbol = ResolveType(compilation, toType, cancellationToken);

        if (fromSymbol == null || toSymbol == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Could not resolve symbols for '{fromType}' or '{toType}'.\n" +
                          $"// Tip: pass the simple class name (e.g. 'CreateRecipeCommand') or the fully-qualified metadata name\n" +
                          $"// (e.g. 'MyApp.Commands.CreateRecipeCommand').  Ensure the types are in the loaded solution."
            };
        }

        var fromProps = fromSymbol.GetMembers().OfType<IPropertySymbol>().ToList();
        var toProps = toSymbol.GetMembers().OfType<IPropertySymbol>().ToList();

        var assignments = new List<StatementSyntax>();
        foreach (var toProp in toProps)
        {
            var match = fromProps.FirstOrDefault(p => p.Name == toProp.Name && p.Type.Equals(toProp.Type, SymbolEqualityComparer.Default));
            if (match != null)
            {
                assignments.Add(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("dest"), SyntaxFactory.IdentifierName(toProp.Name)),
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("source"), SyntaxFactory.IdentifierName(match.Name)))));
            }
        }

        var mappingMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName("void"), $"Map{fromSymbol.Name}To{toSymbol.Name}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("source")).WithType(SyntaxFactory.ParseTypeName(fromSymbol.Name)),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("dest")).WithType(SyntaxFactory.ParseTypeName(toSymbol.Name)))
            .WithBody(SyntaxFactory.Block(assignments));

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = $"// Mapping method generated for {fromSymbol.Name} to {toSymbol.Name}",
            UpdatedText = await RoslynFormattingHelper.FormatNewSnippetAsync(document, mappingMethod, cancellationToken)
        };
    }

    /// <summary>
    /// Generates the mapping from <paramref name="fromType"/> to <paramref name="toType"/> as the full text of a NEW file
    /// (never an edit of an existing one): a <c>static class {From}To{To}Mapper</c> holding the method
    /// <see cref="GenerateMappingAsync"/> produces, in the source type's namespace (file-scoped when the source file uses
    /// one) and in the same folder as the source type's file. <see cref="EngineResultBase.FilePath"/> is the new file's path
    /// and <see cref="EngineResultBase.UpdatedText"/> its whole text. The class is public only when both types are public
    /// (otherwise the public mapping method would expose a less accessible type). Refuses with
    /// <see cref="ToolErrorCode.InvalidArgument"/> when a document or file already exists at that path.
    /// </summary>
    public async Task<DocumentEditResult> GenerateMappingFileAsync(
        FilePathWrapper filePath,
        string fromType,
        string toType,
        CancellationToken cancellationToken = default)
    {
        var snippetResult = await GenerateMappingAsync(filePath, fromType, toType, cancellationToken);
        if (snippetResult.Outcome != EditOutcome.Modified || string.IsNullOrEmpty(snippetResult.UpdatedText))
        {
            return snippetResult;
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        var compilation = document == null ? null : await document.Project.GetCompilationAsync(cancellationToken);
        var fromSymbol = ResolveType(compilation, fromType, cancellationToken);
        var toSymbol = ResolveType(compilation, toType, cancellationToken);
        if (document == null || fromSymbol == null || toSymbol == null)
        {
            return DocumentEditResult.TargetNotFound(filePath);
        }

        var sourceTree = fromSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
        string sourcePath = sourceTree?.FilePath ?? document.FilePath ?? (string)filePath;
        var directory = Path.GetDirectoryName(sourcePath);
        var className = $"{fromSymbol.Name}To{toSymbol.Name}Mapper";
        var fileName = className + ".cs";
        FilePathWrapper newPath = string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
        if (solution.GetDocumentIdsWithFilePath(newPath).Any() || File.Exists(newPath))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = newPath,
                ErrorCode = ToolErrorCode.InvalidArgument,
                Message = $"GenerateMapping: '{newPath}' already exists. GenerateMapping never overwrites a file - delete or rename the existing file, or add the mapping method to it yourself."
            };
        }

        var sourceNamespace = fromSymbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;
        var sourceRoot = sourceTree == null ? null : await sourceTree.GetRootAsync(cancellationToken);
        var fileScoped = sourceRoot?.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Any() == true;

        // Only the two mapped types are named in the method, so their namespaces are the only usings it can need.
        var usings = new[] { fromSymbol, toSymbol }
            .Select(s => s.ContainingNamespace)
            .Where(n => !n.IsGlobalNamespace)
            .Select(n => n.ToDisplayString())
            .Where(n => n != sourceNamespace)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var method = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(snippetResult.UpdatedText)!;
        var classAccessibility = fromSymbol.DeclaredAccessibility == Accessibility.Public && toSymbol.DeclaredAccessibility == Accessibility.Public
            ? SyntaxKind.PublicKeyword
            : SyntaxKind.InternalKeyword;
        MemberDeclarationSyntax topLevel = SyntaxFactory.ClassDeclaration(className)
            .AddModifiers(SyntaxFactory.Token(classAccessibility), SyntaxFactory.Token(SyntaxKind.StaticKeyword))
            .AddMembers(method);
        if (sourceNamespace != null)
        {
            var namespaceName = SyntaxFactory.ParseName(sourceNamespace);
            topLevel = fileScoped
                ? SyntaxFactory.FileScopedNamespaceDeclaration(namespaceName).AddMembers(topLevel)
                : SyntaxFactory.NamespaceDeclaration(namespaceName).AddMembers(topLevel);
        }

        var body = await RoslynFormattingHelper.FormatNewSnippetAsync(document, topLevel, cancellationToken);
        var eol = EolUtilities.DetectDominantEol((await document.GetTextAsync(cancellationToken)).ToString());
        var header = usings.Count == 0 ? string.Empty : string.Concat(usings.Select(u => $"using {u};{eol}")) + eol;

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = newPath,
            Message = $"// Mapping class {className} generated for {fromSymbol.Name} to {toSymbol.Name} in a new file",
            UpdatedText = header + body + eol
        };
    }

    /// <summary>
    /// Inverts the direction of all assignments in a selected block of code.
    /// </summary>
    public async Task<DocumentEditResult> InvertAssignmentsAsync(FilePathWrapper filePath, int startLine, int endLine, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(sourceText.Lines[startLine - 1].Start, sourceText.Lines[endLine - 1].End);

        var nodes = root?.DescendantNodes(span).OfType<AssignmentExpressionSyntax>().ToList();
        if (nodes == null || nodes.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// No assignment expressions found in the specified range: {startLine}-{endLine}",
                UpdatedText = root?.ToFullString() ?? string.Empty
            };
        }

        var annotation = Formatter.Annotation;
        var newRoot = root!.ReplaceNodes(nodes, (oldNode, newNode) =>
            SwapAssignmentOperands(oldNode, newNode, annotation));

        var formattedDoc = await RoslynSentinel.Common.RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, newRoot, cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = $"// Assignment expressions inverted in the specified range: {startLine}-{endLine}",
            UpdatedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString()
        };
    }

    /// <summary>
    /// Inverts all assignment expressions within a code snippet (identified via contextSnippet, lineBefore/lineAfter).
    /// Uses SnippetLookup.ResolveOrThrow to locate the snippet, then inverts assignments near that position.
    /// </summary>
    public async Task<DocumentEditResult> InvertAssignmentsAsync(FilePathWrapper filePath, string contextSnippet, string? lineBefore, string? lineAfter, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Cannot get syntax root or source text."
            };
        }

        try
        {
            // Find the snippet position
            var snippetPos = SnippetLookup.ResolveOrThrow(sourceText, contextSnippet, lineBefore, lineAfter).Start;

            // Find assignment expressions near/at the snippet position
            var nodes = root.DescendantNodes()
                .Where(n => n is AssignmentExpressionSyntax &&
                           (n.Span.Contains(snippetPos) ||
                            (n.Span.Start <= snippetPos && n.Span.End >= snippetPos)))
                .OfType<AssignmentExpressionSyntax>()
                .ToList();

            if (nodes.Count == 0)
            {
                // Fallback: try to find assignments on the same line as the snippet
                var linePos = sourceText.Lines.GetLinePosition(snippetPos);
                var lineNumber = linePos.Line;
                var line = sourceText.Lines[lineNumber];
                var span = line.Span;

                nodes = root.DescendantNodes(span)
                    .OfType<AssignmentExpressionSyntax>()
                    .ToList();
            }

            if (nodes.Count == 0)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// No assignment expressions found at the snippet location",
                    UpdatedText = root.ToFullString() ?? string.Empty
                };
            }

            var annotation = Formatter.Annotation;
            var newRoot = root.ReplaceNodes(nodes, (oldNode, newNode) =>
                SwapAssignmentOperands(oldNode, newNode, annotation));

            var formattedDoc = await RoslynSentinel.Common.RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, newRoot, cancellationToken);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                Message = $"// Assignment expressions inverted in the snippet",
                UpdatedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString()
            };
        }
        catch (ToolException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ContextSnippet error: {ex.Message}",
                ErrorCode = ex.ErrorCode,
                UpdatedText = root.ToFullString() ?? string.Empty
            };
        }
    }

    /// <summary>
    /// Swaps the operands of an assignment while keeping each operand's leading trivia (comments, blank lines, indentation) in its original position.
    /// </summary>
    private static AssignmentExpressionSyntax SwapAssignmentOperands(AssignmentExpressionSyntax oldNode, AssignmentExpressionSyntax newNode, SyntaxAnnotation annotation)
    {
        var newLeft = oldNode.Right.WithLeadingTrivia(oldNode.Left.GetLeadingTrivia());
        var newRight = oldNode.Left.WithLeadingTrivia(oldNode.Right.GetLeadingTrivia());
        return newNode.WithLeft(newLeft).WithRight(newRight).WithAdditionalAnnotations(annotation);
    }
}
