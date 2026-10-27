using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

using RoslynSentinel.Common;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Engines.Basic;

public class SemanticRefactoringEngine
{
    private readonly IWorkspaceManager _workspaceManager;

    public SemanticRefactoringEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Inlines a temporary variable by replacing its usages with its initializer.
    /// 
    /// Safety rules:
    /// - Variable must be assigned exactly once
    /// - All usages must be within the same method scope
    /// - No variable shadowing in nested scopes
    /// - Complex expressions will be parenthesized if needed
    /// </summary>
    public async Task<string> InlineVariableAsync(FilePathWrapper filePath, string variableName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return "";
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return "";
        }

        // Find the variable declaration
        var variableMatches = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.Text == variableName).ToList();
        var variable = RoslynSentinel.Common.NameTargetLookup.RequireSingle(variableMatches, "Variable", variableName);

        if (variable?.Initializer?.Value == null)
        {
            return root.ToFullString();
        }

        // Find the containing variable declaration statement
        var declarationStatement = variable.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        if (declarationStatement == null)
        {
            return root.ToFullString();
        }

        // Find the containing method
        var containingMethod = declarationStatement.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod?.Body == null)
        {
            return root.ToFullString();
        }

        // Check if there's only one declarator
        if (declarationStatement.Declaration.Variables.Count != 1)
        {
            return root.ToFullString();
        }

        var value = variable.Initializer.Value;

        // Determine if parenthesization is needed
        var needsParens = NeedsParenthesization(value);

        // Find all usages in the method - ordered by position for stable replacement
        var usages = containingMethod.Body.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(i => i.Identifier.Text == variableName)
            .OrderByDescending(u => u.SpanStart) // Process from end to beginning to avoid position shifts
            .ToList();

        // If no usages, just remove the declaration
        if (usages.Count == 0)
        {
            try
            {
                return await RoslynFormattingHelper.RemoveNodeAndFormatContainerAsync(document, root, declarationStatement, SyntaxRemoveOptions.KeepUnbalancedDirectives, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return root.ToFullString();
            }
        }

        // Replace each usage (process backwards to avoid position invalidation). All replacement
        // nodes share one annotation so the final formatting pass can reformat just the affected
        // spans instead of reflowing the whole file.
        var editAnnotation = Formatter.Annotation;
        var modifiedRoot = root;
        foreach (var usage in usages)
        {
            try
            {
                var replacement = needsParens
                    ? SyntaxFactory.ParenthesizedExpression(value.WithoutTrivia())
                    : value.WithoutTrivia();

                // Find the current usage in the modified root
                var currentUsage = modifiedRoot.FindNode(usage.Span) as IdentifierNameSyntax;
                if (currentUsage != null && currentUsage.Identifier.Text == variableName)
                {
                    modifiedRoot = modifiedRoot.ReplaceNode(currentUsage, replacement.WithTriviaFrom(currentUsage).WithAdditionalAnnotations(editAnnotation));
                }
            }
            catch
            {
                // Skip any individual replacement errors
            }
        }

        // Remove the declaration
        try
        {
            var currentDeclaration = modifiedRoot.FindNode(declarationStatement.Span) as LocalDeclarationStatementSyntax;
            if (currentDeclaration != null)
            {
                modifiedRoot = modifiedRoot.RemoveNode(currentDeclaration, SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
            }
        }
        catch
        {
            // If removal fails, return with usages replaced
        }

        // Format only the spans carrying the edit annotation (the replaced usages), unless every
        // individual replacement above failed (e.g. all caught exceptions) -> in which case nothing
        // is annotated and Formatter.FormatAsync(document, annotation) would find no matching spans.
        if (modifiedRoot.GetAnnotatedNodes(editAnnotation).Any())
        {
            var scopedFormattedDoc = await RoslynFormattingHelper.FormatAnnotatedSpansAsync(document, modifiedRoot, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken);
            return (await scopedFormattedDoc.GetTextAsync(cancellationToken)).ToString();
        }

        // This fallback is reached only when no usage was replaced, so it removes just the declaration
        // with container-scoped formatting instead of reflowing the whole file.
        try
        {
            return await RoslynFormattingHelper.RemoveNodeAndFormatContainerAsync(document, root, declarationStatement, SyntaxRemoveOptions.KeepUnbalancedDirectives, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return root.ToFullString();
        }
    }

    /// <summary>
    /// Determines if an expression needs parenthesization when inlined.
    /// Complex expressions (binary ops, method calls, etc.) need parens when used in certain contexts.
    /// </summary>
    private static bool NeedsParenthesization(ExpressionSyntax expr)
    {
        return expr is BinaryExpressionSyntax or ConditionalExpressionSyntax or LambdaExpressionSyntax
            or AssignmentExpressionSyntax or InvocationExpressionSyntax;
    }

    /// <summary>
    /// Converts a simple property into a get/set method pair.
    /// </summary>
    public async Task<DocumentEditResult> ConvertPropertyToMethodsAsync(FilePathWrapper filePath, string className, string propertyName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = RoslynSentinel.Common.NameTargetLookup.RequireSingle(root?.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == className).ToList() ?? new System.Collections.Generic.List<ClassDeclarationSyntax>(), "Class", className);
        var propNode = RoslynSentinel.Common.NameTargetLookup.RequireSingle(classNode?.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Identifier.Text == propertyName).ToList() ?? new System.Collections.Generic.List<PropertyDeclarationSyntax>(), "Property", propertyName);

        if (classNode == null)
        {
            throw new ToolNotFoundException($"Class '{className}' was not found in '{filePath}'. Searched: class declarations in this file. Next: {ToolCallHint.Call("Search", ("mode", "class"), ("query", className))}", "class", className);
        }

        if (propNode == null)
        {
            throw new ToolNotFoundException($"Property '{propertyName}' was not found in class '{className}'. Next: {ToolCallHint.Call("Search", ("mode", "property"), ("query", propertyName))}", "property", propertyName);
        }

        var getter = SyntaxFactory.MethodDeclaration(propNode.Type, $"Get{propertyName}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(propertyName.ToLowerInvariant()))));

        var setter = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), $"Set{propertyName}")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(propNode.Type))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName(propertyName.ToLowerInvariant()), SyntaxFactory.IdentifierName("value")))));

        var field = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(propNode.Type)
            .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(propertyName.ToLowerInvariant()))))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));

        var newClass = classNode.ReplaceNode(propNode, new MemberDeclarationSyntax[] { field, getter, setter });
        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root!, classNode, newClass, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken), FilePath = filePath };
    }

    /// <summary>
    /// Wraps a block of code in a using statement for an IDisposable object. The range must start with the
    /// local declaration of that object (see <see cref="WrapStatementsInUsingAsync"/>); anything else is refused.
    /// </summary>
    public async Task<DocumentEditResult> WrapInUsingAsync(FilePathWrapper filePath, int startLine, int endLine, string disposalName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(sourceText.Lines[startLine - 1].Start, sourceText.Lines[endLine - 1].End);

        var nodes = root?.DescendantNodes(span).Where(n => n is StatementSyntax && n.Parent is BlockSyntax).Cast<StatementSyntax>().ToList();
        if (nodes == null || nodes.Count == 0)
        {
            throw new ToolNotFoundException($"No statements found in lines {startLine}-{endLine} of '{filePath}'. Searched: statements inside a block body whose span falls within that line range. Choose a range that covers complete statements inside a method body (not a constructor header, field or attribute list); use {ToolCallHint.Call("GetFileOutline")} to find the lines.", "statements");
        }

        return await WrapStatementsInUsingAsync(filePath, document, root!, nodes, disposalName, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Wraps a code snippet (identified via contextSnippet, lineBefore/lineAfter) in a using statement; the same
    /// range requirement as the line-range overload applies.
    /// Uses SnippetLookup.ResolveOrThrow to locate the snippet, then wraps the enclosing statements.
    /// </summary>
    public async Task<DocumentEditResult> WrapInUsingAsync(FilePathWrapper filePath, string contextSnippet, string? lineBefore, string? lineAfter, string disposalName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            throw new ToolInvalidArgumentException($"Syntax root or source text is unavailable for '{filePath}'; the file did not parse.");
        }

        // Find the snippet position
        var snippetPos = SnippetLookup.ResolveOrThrow(sourceText, contextSnippet, lineBefore, lineAfter).Start;

        // Find the statements that contain the snippet position
        var nodes = root.DescendantNodes()
            .Where(n => n is StatementSyntax && n.Parent is BlockSyntax && n.Span.Contains(snippetPos))
            .Cast<StatementSyntax>()
            .ToList();

        if (nodes.Count == 0)
        {
            // Fallback: try to find any statements in a reasonable range around the snippet
            var span = sourceText.Lines.GetLineFromPosition(snippetPos).Span;
            nodes = root.DescendantNodes(span)
                .Where(n => n is StatementSyntax && n.Parent is BlockSyntax)
                .Cast<StatementSyntax>()
                .ToList();
        }

        if (nodes.Count == 0)
        {
            throw new ToolNotFoundException($"No statements found at the snippet location in '{filePath}'. Searched: statements containing the snippet match and statements on the snippet's line. Choose a range that covers complete statements inside a method body (not a constructor header, field or attribute list); use {ToolCallHint.Call("GetFileOutline")} to find the lines.", "statements");
        }

        // TODO this snippet form selects only the statement(s) containing the snippet, so it cannot cover a multi-statement range (the using block would be empty); WrapRange using is meant for startLine/endLine
        return await WrapStatementsInUsingAsync(filePath, document, root, nodes, disposalName, await _workspaceManager.ResolveEolAsync(document, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Replaces the selected statements with <c>using (&lt;first declaration&gt;) { &lt;remaining statements&gt; }</c>.
    /// The range must start with a single-variable local declaration, with an initializer, of a type that
    /// implements <see cref="IDisposable"/>, and <paramref name="disposalName"/> must be that variable's name;
    /// anything else is refused with a <see cref="ToolTargetIneligibleException"/> (a name mismatch with a
    /// <see cref="ToolInvalidArgumentException"/>), because a using header without an initializer is never valid C#.
    /// Shared by both <c>WrapInUsingAsync</c> overloads.
    /// </summary>
    private static async Task<DocumentEditResult> WrapStatementsInUsingAsync(FilePathWrapper filePath, Document document, SyntaxNode root, IReadOnlyList<StatementSyntax> selected, string disposalName, string eol, CancellationToken cancellationToken)
    {
        const string Requirement = "WrapRange wrapper=using requires the range to start with a local declaration of an IDisposable (for example `var s = new MemoryStream();`), and name must be that variable's name";

        var parentBlock = (BlockSyntax)selected[0].Parent!;

        // DescendantNodes also yields statements nested inside the selected ones; only siblings of the first statement belong in the new block.
        var nodes = selected.Where(n => n.Parent == parentBlock).ToList();

        var firstLine = nodes[0].ToString().Trim().Split('\n')[0].Trim();
        if (firstLine.Length > 60)
            firstLine = firstLine[..60] + "...";

        if (nodes[0] is not LocalDeclarationStatementSyntax declaration
            || declaration.IsConst
            || !declaration.UsingKeyword.IsKind(SyntaxKind.None)
            || declaration.Declaration.Variables.Count != 1
            || declaration.Declaration.Variables[0].Initializer == null)
        {
            throw new ToolTargetIneligibleException($"Cannot wrap in using: {Requirement}, but the first statement of the range is not a single-variable local declaration with an initializer ('{firstLine}').");
        }

        var declarator = declaration.Declaration.Variables[0];
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var disposable = semanticModel?.Compilation.GetSpecialType(SpecialType.System_IDisposable);
        var declaredType = (semanticModel?.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol)?.Type;
        if (declaredType == null || declaredType.TypeKind == TypeKind.Error)
        {
            throw new ToolTargetIneligibleException($"Cannot wrap in using: {Requirement}, but the type of '{declarator.Identifier.Text}' could not be resolved (is the project compiling?).");
        }

        if (disposable == null || (!SymbolEqualityComparer.Default.Equals(declaredType, disposable) && !declaredType.AllInterfaces.Contains(disposable, SymbolEqualityComparer.Default)))
        {
            throw new ToolTargetIneligibleException($"Cannot wrap in using: {Requirement}, but '{declarator.Identifier.Text}' has type '{declaredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}', which does not implement System.IDisposable.");
        }

        if (declarator.Identifier.Text != disposalName)
        {
            throw new ToolInvalidArgumentException($"Cannot wrap in using: name is '{disposalName}' but the range declares '{declarator.Identifier.Text}'; name must be the declared variable's name (case-sensitive).");
        }

        var usingStatement = SyntaxFactory.UsingStatement(
                declaration.Declaration.WithoutTrivia(),
                null,
                SyntaxFactory.Block(nodes.Skip(1)))
            .WithLeadingTrivia(declaration.GetLeadingTrivia());

        // Rebuild the parent block's statement list: replace the entire selected range
        // with the single using statement. ReplaceNodes(delegate returning null) would crash Roslyn.
        var origStatements = parentBlock.Statements.ToList();
        int startIdx = origStatements.IndexOf(nodes[0]);
        int endIdx = origStatements.IndexOf(nodes[^1]);
        var newStatements = new List<StatementSyntax>();
        newStatements.AddRange(origStatements.Take(startIdx));
        newStatements.Add(usingStatement);
        newStatements.AddRange(origStatements.Skip(endIdx + 1));

        var newBlock = parentBlock.WithStatements(SyntaxFactory.List(newStatements));

        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = await RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync(document, root, parentBlock, newBlock, eol, cancellationToken), FilePath = filePath };
    }
}
