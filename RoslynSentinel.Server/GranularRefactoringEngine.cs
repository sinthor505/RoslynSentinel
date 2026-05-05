using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Server;

public class GranularRefactoringEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public GranularRefactoringEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Dispatches a named micro-refactoring against a specific line in a file.
    /// </summary>
    /// <param name="refactoringId">One of: type-to-var, remove-unused-local, add-braces, remove-braces, extract-constant</param>
    public async Task<string> RunMicroRefactoringAsync(string filePath, string refactoringId, int line, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        SyntaxNode? newRoot = refactoringId.ToLowerInvariant() switch
        {
            "type-to-var" => ApplyTypeToVar(root, line),
            "remove-unused-local" => ApplyRemoveLocalAtLine(root, line),
            "add-braces" => ApplyAddBraces(root, line),
            "remove-braces" => ApplyRemoveBraces(root, line),
            "extract-constant" => ApplyExtractConstant(root, line),
            _ => throw new ArgumentException(
                $"Unknown micro-refactoring '{refactoringId}'. " +
                "Known IDs: type-to-var, remove-unused-local, add-braces, remove-braces, extract-constant.")
        };

        return newRoot?.NormalizeWhitespace().ToFullString() ?? root.NormalizeWhitespace().ToFullString();
    }

    private static SyntaxNode ApplyTypeToVar(SyntaxNode root, int line)
    {
        var target = root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault(n =>
                n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line &&
                !n.IsConst &&
                !n.Declaration.Type.IsVar &&
                n.Declaration.Variables.Count == 1 &&
                n.Declaration.Variables[0].Initializer != null);

        if (target == null) return root;

        var varType = SyntaxFactory.IdentifierName("var")
            .WithLeadingTrivia(target.Declaration.Type.GetLeadingTrivia())
            .WithTrailingTrivia(target.Declaration.Type.GetTrailingTrivia());

        return root.ReplaceNode(target, target.WithDeclaration(target.Declaration.WithType(varType)));
    }

    private static SyntaxNode ApplyRemoveLocalAtLine(SyntaxNode root, int line)
    {
        var target = root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        return target == null ? root : (root.RemoveNode(target, SyntaxRemoveOptions.KeepNoTrivia) ?? root);
    }

    private static SyntaxNode ApplyAddBraces(SyntaxNode root, int line)
    {
        // Find an if/while/for at the target line that has a braceless body
        var target = root.DescendantNodes()
            .Where(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line)
            .OfType<StatementSyntax>()
            .FirstOrDefault(n => n is IfStatementSyntax || n is WhileStatementSyntax || n is ForStatementSyntax || n is ForEachStatementSyntax);

        if (target == null) return root;

        SyntaxNode replacement = target switch
        {
            IfStatementSyntax ifs when ifs.Statement is not BlockSyntax =>
                ifs.WithStatement(SyntaxFactory.Block(ifs.Statement)),
            WhileStatementSyntax ws when ws.Statement is not BlockSyntax =>
                ws.WithStatement(SyntaxFactory.Block(ws.Statement)),
            ForStatementSyntax fs when fs.Statement is not BlockSyntax =>
                fs.WithStatement(SyntaxFactory.Block(fs.Statement)),
            ForEachStatementSyntax fes when fes.Statement is not BlockSyntax =>
                fes.WithStatement(SyntaxFactory.Block(fes.Statement)),
            _ => target
        };

        return root.ReplaceNode(target, replacement);
    }

    private static SyntaxNode ApplyRemoveBraces(SyntaxNode root, int line)
    {
        // Find an if/while/for at the target line with a single-statement block body
        var target = root.DescendantNodes()
            .Where(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line)
            .OfType<StatementSyntax>()
            .FirstOrDefault(n => n is IfStatementSyntax || n is WhileStatementSyntax || n is ForStatementSyntax || n is ForEachStatementSyntax);

        if (target == null) return root;

        SyntaxNode replacement = target switch
        {
            IfStatementSyntax ifs when ifs.Statement is BlockSyntax blk && blk.Statements.Count == 1 =>
                ifs.WithStatement(blk.Statements[0]),
            WhileStatementSyntax ws when ws.Statement is BlockSyntax blk && blk.Statements.Count == 1 =>
                ws.WithStatement(blk.Statements[0]),
            ForStatementSyntax fs when fs.Statement is BlockSyntax blk && blk.Statements.Count == 1 =>
                fs.WithStatement(blk.Statements[0]),
            ForEachStatementSyntax fes when fes.Statement is BlockSyntax blk && blk.Statements.Count == 1 =>
                fes.WithStatement(blk.Statements[0]),
            _ => target
        };

        return root.ReplaceNode(target, replacement);
    }

    private static SyntaxNode ApplyExtractConstant(SyntaxNode root, int line)
    {
        var literal = root.DescendantNodes()
            .Where(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line)
            .OfType<LiteralExpressionSyntax>()
            .FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression) ||
                                 l.IsKind(SyntaxKind.NumericLiteralExpression));
        if (literal == null) return root;

        // Find enclosing class or struct to inject const field
        var enclosingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (enclosingType == null) return root;

        var typeName = literal.IsKind(SyntaxKind.StringLiteralExpression) ? "string" : "int";
        const string constName = "ExtractedConstant";

        var constDecl = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.ParseTypeName(typeName),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(constName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(literal)))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ConstKeyword)));

        var newRoot = root.ReplaceNode(literal, SyntaxFactory.IdentifierName(constName));
        var newType = newRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == enclosingType.Identifier.Text);
        if (newType == null) return newRoot;

        var updatedType = newType.WithMembers(newType.Members.Insert(0, constDecl));
        return newRoot.ReplaceNode(newType, updatedType);
    }

    public async Task<string> InlineFieldAsync(string filePath, string fieldName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var field = root?.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));

        // If field not found or has no initializer, return error
        if (field == null)
        {
            return $"// ERROR: Field '{fieldName}' not found.\n" + (root?.ToFullString() ?? "");
        }

        if (field.Declaration.Variables[0].Initializer == null)
        {
            return $"// ERROR: Cannot inline field '{fieldName}' without initializer. Field must have a static initializer or initial assignment.\n" + root!.ToFullString();
        }

        var value = field.Declaration.Variables[0].Initializer!.Value;
        var usages = root!.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == fieldName).ToList();
        
        var trackedRoot = root.TrackNodes(new SyntaxNode[] { field });
        var usagesInTracked = trackedRoot.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == fieldName).ToList();
        
        var newRoot = trackedRoot.ReplaceNodes(usagesInTracked, (old, _) => value.WithTriviaFrom(old));
        var newField = newRoot.GetCurrentNode(field);
        if (newField != null)
        {
            newRoot = newRoot.RemoveNode(newField, SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
        }
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> InlineParameterAsync(string filePath, string methodName, string parameterName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null || semanticModel == null) return root?.ToFullString() ?? "";

        var parameter = method.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == parameterName);
        if (parameter == null) return root!.ToFullString();

        return root!.ToFullString();
    }

    public async Task<string> ConvertMethodToIndexerAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null)
            return $"// ERROR: Method '{methodName}' not found in {Path.GetFileName(filePath)}.\n" + (root?.ToFullString() ?? "");

        if (method.ParameterList.Parameters.Count != 1)
            return $"// ERROR: Cannot convert '{methodName}' to an indexer — it must have exactly one parameter (has {method.ParameterList.Parameters.Count}).\n" + root!.ToFullString();

        // C# does not support static indexers
        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return $"// ERROR: Cannot convert static method '{methodName}' to an indexer — C# does not support static indexers.\n" + root!.ToFullString();

        // Build getter body from block body or expression body
        BlockSyntax getterBody;
        if (method.Body != null)
        {
            getterBody = method.Body;
        }
        else if (method.ExpressionBody != null)
        {
            getterBody = SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(method.ExpressionBody.Expression));
        }
        else
        {
            // Abstract/extern methods have no body — cannot create indexer
            return $"// ERROR: Cannot convert '{methodName}' to an indexer — method has no body.\n" + root!.ToFullString();
        }

        // Exclude modifiers that are invalid on indexers (static, abstract, extern, etc.)
        var validModifiers = method.Modifiers
            .Where(m => !m.IsKind(SyntaxKind.StaticKeyword) && !m.IsKind(SyntaxKind.AbstractKeyword) && !m.IsKind(SyntaxKind.ExternKeyword))
            .ToArray();

        var parameter = method.ParameterList.Parameters[0];
        var indexer = SyntaxFactory.IndexerDeclaration(method.ReturnType)
            .AddModifiers(validModifiers)
            .WithParameterList(SyntaxFactory.BracketedParameterList(SyntaxFactory.SingletonSeparatedList(parameter)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(getterBody))));

        var newRoot = root!.ReplaceNode(method, indexer);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> IntroduceFieldAsync(string filePath, string contextSnippet, string newFieldName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
        var token = root.FindToken(position);
        var expression = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null) return root.ToFullString();

        var containingClass = expression.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (containingClass == null) return root.ToFullString();

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax fieldType;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
            fieldType = typeInfo.Type != null
                ? SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString())
                : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }
        else
        {
            fieldType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }

        // Check if expression is safe to use as a class field initializer
        // (doesn't reference method parameters or local variables)
        var isInitializerSafe = IsExpressionClassScopeSafe(expression, semanticModel, cancellationToken);

        var variableDeclarator = isInitializerSafe
            ? SyntaxFactory.VariableDeclarator(newFieldName)
                .WithInitializer(SyntaxFactory.EqualsValueClause(expression.WithoutTrivia()))
            : SyntaxFactory.VariableDeclarator(newFieldName);

        var fieldDeclaration = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(fieldType)
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(variableDeclarator)))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

        var fieldRef = SyntaxFactory.IdentifierName(newFieldName).WithTriviaFrom(expression);

        var trackedRoot = root.TrackNodes(new SyntaxNode[] { expression, containingClass });
        var newRoot = trackedRoot.ReplaceNode(trackedRoot.GetCurrentNode(expression)!, fieldRef);
        var currentClass = newRoot.GetCurrentNode(containingClass)!;
        var newClass = currentClass.WithMembers(currentClass.Members.Insert(0, fieldDeclaration));
        newRoot = newRoot.ReplaceNode(currentClass, newClass);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static bool IsExpressionClassScopeSafe(ExpressionSyntax expression, SemanticModel? semanticModel, CancellationToken cancellationToken)
    {
        // Check all identifiers in the expression to see if they reference method parameters or local variables
        var identifiers = expression.DescendantNodes().OfType<IdentifierNameSyntax>();
        
        if (semanticModel == null)
            // If no semantic model, be conservative and assume it's not safe
            return identifiers.Count() == 0;

        foreach (var identifier in identifiers)
        {
            try
            {
                var symbolInfo = semanticModel.GetSymbolInfo(identifier, cancellationToken);
                var symbol = symbolInfo.Symbol;
                
                // Check if this identifier refers to a method parameter or local variable
                if (symbol is IParameterSymbol or ILocalSymbol)
                    return false;
            }
            catch
            {
                // If we can't determine the symbol, be conservative
                return false;
            }
        }

        return true;
    }

    public async Task<string> IntroduceParameterAsync(string filePath, string contextSnippet, string newParamName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // NOTE: Single-file only — call sites in other files are not updated.
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = ContextHelper.TryFindSnippetPosition(sourceText, contextSnippet, out var paramSnippetError, lineBefore, lineAfter);
        if (position < 0)
            return $"// Error: {paramSnippetError}";
        var token = root.FindToken(position);
        var expression = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null) return root.ToFullString();

        var containingMethod = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null) return root.ToFullString();
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax paramType;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
            paramType = typeInfo.Type != null
                ? SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString())
                : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }
        else
        {
            paramType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }

        var newParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(newParamName))
            .WithType(paramType.WithTrailingTrivia(SyntaxFactory.Space));
        var paramRef = SyntaxFactory.IdentifierName(newParamName).WithTriviaFrom(expression);

        var trackedRoot = root.TrackNodes(new SyntaxNode[] { expression, containingMethod });
        var currentExpression = trackedRoot.GetCurrentNode(expression);
        if (currentExpression == null) return root.ToFullString();
        var newRoot = trackedRoot.ReplaceNode(currentExpression, paramRef);
        var currentMethod = newRoot.GetCurrentNode(containingMethod);
        if (currentMethod == null) return root.ToFullString();
        var updatedMethod = currentMethod.WithParameterList(currentMethod.ParameterList.AddParameters(newParameter));
        newRoot = newRoot.ReplaceNode(currentMethod, updatedMethod);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> IntroduceVariableAsync(string filePath, string contextSnippet, string newVariableName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);

        // Primary: find an expression whose span starts at position and whose text matches
        // the snippet — handles compound expressions like "a + b".
        var trimmedSnippet = contextSnippet.Trim();
        var expression = root.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(e => e.SpanStart == position && e.ToString().Trim() == trimmedSnippet)
            .FirstOrDefault()
            // Fallback: walk from the token at the position up to the first expression.
            ?? root.FindToken(position).Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();

        if (expression == null) return root.ToFullString();

        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null) return root.ToFullString();
        if (containingStatement.Parent is not BlockSyntax block) return root.ToFullString();

        // If the expression IS the entire initializer of an existing local var declaration, the
        // variable is already introduced — extracting it would produce `var x = x;` (a duplicate).
        if (containingStatement is LocalDeclarationStatementSyntax existingDecl &&
            existingDecl.Declaration.Variables.Count == 1 &&
            existingDecl.Declaration.Variables[0].Initializer?.Value?.IsEquivalentTo(expression) == true)
        {
            var existingName = existingDecl.Declaration.Variables[0].Identifier.Text;
            return $"// '{existingName}' is already a local variable — nothing to introduce.";
        }

        var varDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(newVariableName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(expression.WithoutTrivia())))));

        // If the extracted expression is the sole content of a parenthesized expression,
        // replace the outer parens too — avoids spurious "(sum) * c" when extracting "a + b"
        // from "(a + b) * c". A bare identifier never needs parens (highest precedence).
        SyntaxNode nodeToReplace = expression;
        if (expression.Parent is ParenthesizedExpressionSyntax parenParent &&
            parenParent.Expression == expression)
        {
            nodeToReplace = parenParent;
        }

        var varRef = SyntaxFactory.IdentifierName(newVariableName).WithTriviaFrom(nodeToReplace);

        var trackedRoot = root.TrackNodes(new SyntaxNode[] { nodeToReplace, containingStatement, block });
        var newRoot = trackedRoot.ReplaceNode(trackedRoot.GetCurrentNode(nodeToReplace)!, varRef);
        var currentStatement = newRoot.GetCurrentNode(containingStatement)!;
        var currentBlock = newRoot.GetCurrentNode(block)!;
        var idx = currentBlock.Statements.IndexOf(currentStatement);
        if (idx < 0) return root.ToFullString();
        var newBlock = currentBlock.WithStatements(currentBlock.Statements.Insert(idx, varDecl));
        newRoot = newRoot.ReplaceNode(currentBlock, newBlock);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> MoveTypeToOuterScopeAsync(string filePath, string nestedTypeName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return $"// Error: File '{filePath}' not found.";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return $"// Error: Failed to get syntax root.";

        // Find the type
        var nestedType = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == nestedTypeName);
        
        if (nestedType == null) 
            return $"// Error: Type '{nestedTypeName}' not found.";

        // Check if type is actually nested (parent is a type, not namespace/file scope)
        var parentType = nestedType.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (parentType == null)
            return $"// Error: Type '{nestedTypeName}' is already at outer scope. Cannot move to outer scope.";

        // Type is nested, move it out
        var newRoot = root.RemoveNode(nestedType, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        if (newRoot == null) return root.ToFullString();

        // Find the namespace or file scope and add the type there
        var ns = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        var fileScopedNs = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();

        if (ns != null)
        {
            var newNs = ns.AddMembers(nestedType);
            newRoot = newRoot!.ReplaceNode(ns, newNs);
        }
        else if (fileScopedNs != null)
        {
            var newFileScopedNs = fileScopedNs.AddMembers(nestedType);
            newRoot = newRoot!.ReplaceNode(fileScopedNs, newFileScopedNs);
        }
        else
        {
            // Add at compilation unit level
            var compilationUnit = root as CompilationUnitSyntax;
            if (compilationUnit != null)
            {
                newRoot = compilationUnit.AddMembers(nestedType);
            }
        }

        return newRoot?.NormalizeWhitespace().ToFullString() ?? root.ToFullString();
    }

    public async Task<Dictionary<string, string>> ExtractMembersToPartialAsync(string filePath, string className, string[] memberNames, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new Dictionary<string, string>();

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);

        if (classNode != null)
        {
            var membersToMove = classNode.Members.Where(m => 
                (m is MethodDeclarationSyntax meth && memberNames.Contains(meth.Identifier.Text)) ||
                (m is PropertyDeclarationSyntax prop && memberNames.Contains(prop.Identifier.Text))).ToList();

            // Extract namespace
            var namespaceDeclOpt = root?.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
            var fileScopedNamespaceOpt = root?.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
            var namespaceName = namespaceDeclOpt?.Name.ToString() ?? fileScopedNamespaceOpt?.Name.ToString();

            // Extract usings
            var usings = root?.ChildNodes().OfType<UsingDirectiveSyntax>().ToList() ?? new List<UsingDirectiveSyntax>();

            // Build new partial class
            var newClassNode = SyntaxFactory.ClassDeclaration(className)
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                .WithMembers(SyntaxFactory.List(membersToMove));

            // Build new compilation unit with usings and namespace
            CompilationUnitSyntax newCompilationUnit = SyntaxFactory.CompilationUnit();

            if (usings.Any())
            {
                newCompilationUnit = newCompilationUnit.WithUsings(SyntaxFactory.List(usings));
            }

            if (!string.IsNullOrEmpty(namespaceName))
            {
                var namespaceDecl = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(namespaceName))
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newClassNode));
                newCompilationUnit = newCompilationUnit.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(namespaceDecl));
            }
            else
            {
                newCompilationUnit = newCompilationUnit.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newClassNode));
            }

            // Format with proper newlines
            var formattedCode = newCompilationUnit.NormalizeWhitespace().ToFullString();
            // Ensure proper spacing after usings before namespace
            if (usings.Any() && !string.IsNullOrEmpty(namespaceName))
            {
                formattedCode = formattedCode.Replace(";namespace", ";\n\nnamespace");
            }

            return new Dictionary<string, string> { { Path.Combine(Path.GetDirectoryName(filePath)!, $"{className}.Partial.cs"), formattedCode } };
        }
        return new Dictionary<string, string>();
    }

    public async Task<string> IntroduceParameterObjectAsync(
        string filePath,
        string methodName,
        string? newTypeName = null,
        string[]? parameterNames = null,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "// File not found.";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "// Could not parse file.";

        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null) return "// Method not found.";

        // Check if method implements an interface
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        IMethodSymbol? methodSymbol = null;
        if (semanticModel != null)
        {
            methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken);
        }

        var allParams = methodNode.ParameterList.Parameters.ToList();

        // Separate CancellationToken params from the rest
        var ctParams = allParams.Where(p => p.Type?.ToString().Contains("CancellationToken") == true).ToList();
        var candidateParams = allParams.Where(p => !ctParams.Contains(p)).ToList();

        // Determine which params to group
        List<ParameterSyntax> groupedParams;
        if (parameterNames != null && parameterNames.Length > 0)
        {
            var nameSet = new HashSet<string>(parameterNames, StringComparer.Ordinal);
            groupedParams = candidateParams.Where(p => nameSet.Contains(p.Identifier.Text)).ToList();
        }
        else
        {
            groupedParams = candidateParams;
        }

        if (groupedParams.Count == 0)
            return "// No parameters to group.";

        // Generate record name
        var recordName = newTypeName ?? (methodName + "Parameters");

        // Build record properties: capitalize first letter
        static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1);

        var recordParams = groupedParams.Select(p =>
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(Capitalize(p.Identifier.Text)))
                .WithType(p.Type!));

        var recordDecl = SyntaxFactory.RecordDeclaration(
                SyntaxFactory.Token(SyntaxKind.RecordKeyword),
                SyntaxFactory.Identifier(recordName))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(recordParams)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        // Build new parameter list: (RecordName request, [CT params])
        var requestParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("request"))
            .WithType(SyntaxFactory.ParseTypeName(recordName + " "));

        var remainingParams = candidateParams.Where(p => !groupedParams.Contains(p)).ToList();
        var newParams = new List<ParameterSyntax> { requestParam };
        newParams.AddRange(remainingParams);
        newParams.AddRange(ctParams);
        var newParamList = methodNode.ParameterList.WithParameters(SyntaxFactory.SeparatedList(newParams));

        // Build param->property rewrite map: paramName -> request.ParamName
        var rewriteMap = groupedParams.ToDictionary(
            p => p.Identifier.Text,
            p => $"request.{Capitalize(p.Identifier.Text)}");

        // Add TODO comment to method
        var todoComment = SyntaxFactory.Comment($"// TODO: Update call sites to use {recordName}\r\n        ");

        // Rewrite references in method body
        SyntaxNode? newBody = null;
        if (methodNode.Body != null)
        {
            var rewriter = new ParamToPropertyRewriter(rewriteMap);
            newBody = rewriter.Visit(methodNode.Body);
        }

        var newMethodNode = methodNode.WithParameterList(newParamList);
        if (newBody is BlockSyntax block)
        {
            var firstStmt = block.Statements.FirstOrDefault();
            StatementSyntax todoStmt = SyntaxFactory.EmptyStatement()
                .WithLeadingTrivia(SyntaxFactory.TriviaList(
                    SyntaxFactory.Comment($"// TODO: Update call sites to use {recordName}")))
                .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken));
            var newStmts = block.Statements.Insert(0, todoStmt);
            newMethodNode = newMethodNode.WithBody(block.WithStatements(newStmts));
        }

        // If method is on an interface, also update interface definition
        var classNode = methodNode.Parent as ClassDeclarationSyntax;
        var interfaceNode = methodNode.Parent as InterfaceDeclarationSyntax;
        
        SyntaxNode newRoot = root;
        
        if (interfaceNode != null && methodSymbol?.ExplicitInterfaceImplementations.Length == 0)
        {
            // Update interface method
            var newInterface = interfaceNode.ReplaceNode(methodNode, newMethodNode);
            newRoot = root.ReplaceNode(interfaceNode, newInterface);
        }
        else if (classNode != null && methodSymbol?.ExplicitInterfaceImplementations.Length == 0)
        {
            // Check if method implements an interface
            if (methodSymbol?.ContainingType?.Interfaces.Length > 0)
            {
                // Method implements interface — add warning but update the implementation
                newRoot = root.ReplaceNode(methodNode, newMethodNode);
                // Append warning comment
                var warning = $"// WARNING: This method implements an interface. Update the interface signature in the corresponding interface file.\n";
                return warning + newRoot.NormalizeWhitespace().ToFullString();
            }
            else
            {
                newRoot = root.ReplaceNode(methodNode, newMethodNode);
            }
        }
        else
        {
            newRoot = root.ReplaceNode(methodNode, newMethodNode);
        }

        // Append record declaration to end of file
        var compilationUnit = newRoot as CompilationUnitSyntax;
        if (compilationUnit != null)
        {
            var recordMember = SyntaxFactory.GlobalStatement(
                SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression("_placeholder_")));
            // Actually append as a namespace member or top-level
            // Find the namespace or use file-scoped namespace
            var nsNode = compilationUnit.Members.OfType<NamespaceDeclarationSyntax>().LastOrDefault();
            var fileScopeNs = compilationUnit.Members.OfType<FileScopedNamespaceDeclarationSyntax>().LastOrDefault();

            if (nsNode != null)
            {
                var newNs = nsNode.AddMembers(recordDecl.NormalizeWhitespace());
                newRoot = compilationUnit.ReplaceNode(nsNode, newNs);
            }
            else if (fileScopeNs != null)
            {
                var newNs = fileScopeNs.AddMembers(recordDecl.NormalizeWhitespace());
                newRoot = compilationUnit.ReplaceNode(fileScopeNs, newNs);
            }
            else
            {
                // Top-level: add after the last type declaration
                newRoot = compilationUnit.AddMembers(recordDecl.NormalizeWhitespace());
            }
        }

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private class ParamToPropertyRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _map;
        public ParamToPropertyRewriter(Dictionary<string, string> map) { _map = map; }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var name = node.Identifier.Text;
            if (_map.TryGetValue(name, out var replacement))
                return SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(node);
            return base.VisitIdentifierName(node);
        }
    }
}
