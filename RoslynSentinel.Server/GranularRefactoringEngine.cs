using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    public async Task<DocumentEditResult> RunMicroRefactoringAsync(FilePath filePath, string refactoringId, int line, CancellationToken cancellationToken = default)
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

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot?.NormalizeWhitespace().ToFullString() ?? root.NormalizeWhitespace().ToFullString()
        };
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

        if (target == null)
        {
            return root;
        }

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

        if (target == null)
        {
            return root;
        }

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

        if (target == null)
        {
            return root;
        }

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
        if (literal == null)
        {
            return root;
        }

        // Find enclosing class or struct to inject const field
        var enclosingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (enclosingType == null)
        {
            return root;
        }

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
        if (newType == null)
        {
            return newRoot;
        }

        var updatedType = newType.WithMembers(newType.Members.Insert(0, constDecl));
        return newRoot.ReplaceNode(newType, updatedType);
    }

    public async Task<DocumentEditResult> InlineFieldAsync(FilePath filePath, string fieldName, CancellationToken cancellationToken = default)
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

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var field = root?.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));

        // If field not found or has no initializer, return error
        if (field == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Field '{fieldName}' not found."
            };
        }

        if (field.Declaration.Variables[0].Initializer == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot inline field '{fieldName}' without initializer. Field must have a static initializer or initial assignment."
            };
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
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> InlineParameterAsync(FilePath filePath, string methodName, string parameterName, CancellationToken cancellationToken = default)
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

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null || semanticModel == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var parameter = method.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == parameterName);
        if (parameter == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Parameter '{parameterName}' not found in method '{methodName}'."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = root!.ToFullString()
        };
    }

    public async Task<DocumentEditResult> ConvertMethodToIndexerAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
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

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Method '{methodName}' not found in {Path.GetFileName(filePath)}."
            };
        }

        if (method.ParameterList.Parameters.Count != 1)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot convert '{methodName}' to an indexer — it must have exactly one parameter (has {method.ParameterList.Parameters.Count})."
            };
        }

        // C# does not support static indexers
        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot convert static method '{methodName}' to an indexer — C# does not support static indexers."
            };
        }

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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ERROR: Cannot convert '{methodName}' to an indexer — method has no body."
            };
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
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> IntroduceFieldAsync(FilePath filePath, string contextSnippet, string newFieldName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
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

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
        var token = root.FindToken(position);
        var expression = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Expression not found."
            };
        }

        var containingClass = expression.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (containingClass == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing class not found."
            };
        }

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

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    private static bool IsExpressionClassScopeSafe(ExpressionSyntax expression, SemanticModel? semanticModel, CancellationToken cancellationToken)
    {
        // Check all identifiers in the expression to see if they reference method parameters or local variables
        var identifiers = expression.DescendantNodes().OfType<IdentifierNameSyntax>();

        if (semanticModel == null)
        {
            // If no semantic model, be conservative and assume it's not safe
            return !identifiers.Any();
        }

        foreach (var identifier in identifiers)
        {
            try
            {
                var symbolInfo = semanticModel.GetSymbolInfo(identifier, cancellationToken);
                var symbol = symbolInfo.Symbol;

                // Check if this identifier refers to a method parameter or local variable
                if (symbol is IParameterSymbol or ILocalSymbol)
                {
                    return false;
                }
            }
            catch
            {
                // If we can't determine the symbol, be conservative
                return false;
            }
        }

        return true;
    }

    public async Task<DocumentEditResult> IntroduceParameterAsync(FilePath filePath, string contextSnippet, string newParamName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // NOTE: Single-file only — call sites in other files are not updated.
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

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = ContextHelper.TryFindSnippetPosition(sourceText, contextSnippet, out var paramSnippetError, lineBefore, lineAfter);
        if (position < 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Error: {paramSnippetError}"
            };
        }

        var token = root.FindToken(position);
        var expression = token.Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();
        if (expression == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Expression not found."
            };
        }

        var containingMethod = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing method not found."
            };
        }

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
        if (currentExpression == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Expression not found."
            };
        }

        var newRoot = trackedRoot.ReplaceNode(currentExpression, paramRef);
        var currentMethod = newRoot.GetCurrentNode(containingMethod);
        if (currentMethod == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing method not found."
            };
        }

        var updatedMethod = currentMethod.WithParameterList(currentMethod.ParameterList.AddParameters(newParameter));
        newRoot = newRoot.ReplaceNode(currentMethod, updatedMethod);

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> IntroduceVariableAsync(FilePath filePath, string contextSnippet, string newVariableName, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
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

        if (expression == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Expression not found."
            };
        }

        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing statement not found."
            };
        }

        if (containingStatement.Parent is not BlockSyntax block)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Containing block not found."
            };
        }

        // If the expression IS the entire initializer of an existing local var declaration, the
        // variable is already introduced — extracting it would produce `var x = x;` (a duplicate).
        if (containingStatement is LocalDeclarationStatementSyntax existingDecl &&
            existingDecl.Declaration.Variables.Count == 1 &&
            existingDecl.Declaration.Variables[0].Initializer?.Value?.IsEquivalentTo(expression) == true)
        {
            var existingName = existingDecl.Declaration.Variables[0].Identifier.Text;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// '{existingName}' is already a local variable — nothing to introduce."
            };
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
        if (idx < 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Statement not found in block."
            };
        }

        var newBlock = currentBlock.WithStatements(currentBlock.Statements.Insert(idx, varDecl));
        newRoot = newRoot.ReplaceNode(currentBlock, newBlock);

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> MoveTypeToOuterScopeAsync(FilePath filePath, string nestedTypeName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Error: File '{filePath}' not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Error: Failed to get syntax root."
            };
        }

        // Find the type
        var nestedType = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == nestedTypeName);

        if (nestedType == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Error: Type '{nestedTypeName}' not found."
            };
        }

        // Check if type is actually nested (parent is a type, not namespace/file scope)
        var parentType = nestedType.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (parentType == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Error: Type '{nestedTypeName}' is already at outer scope. Cannot move to outer scope."
            };
        }

        // Type is nested, move it out
        var newRoot = root.RemoveNode(nestedType, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        if (newRoot == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Error: Failed to remove nested type '{nestedTypeName}'."
            };
        }

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

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = newRoot?.NormalizeWhitespace().ToFullString() ?? root.ToFullString()
        };
    }

    public async Task<Dictionary<FilePath, string>> ExtractMembersToPartialAsync(FilePath filePath, string className, string[] memberNames, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new Dictionary<FilePath, string>();
        }

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

            if (usings.Count != 0)
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
            if (usings.Count != 0 && !string.IsNullOrEmpty(namespaceName))
            {
                formattedCode = formattedCode.Replace(";namespace", ";\n\nnamespace");
            }

            return new Dictionary<FilePath, string> { { Path.Combine(Path.GetDirectoryName(filePath)!, $"{className}.Partial.cs"), formattedCode } };
        }
        return new Dictionary<FilePath, string>();
    }

    public async Task<DocumentEditResult> IntroduceParameterObjectAsync(
        FilePath filePath,
        string methodName,
        string? newTypeName = null,
        string[]? parameterNames = null,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Could not parse file."
            };
        }

        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Method not found."
            };
        }

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
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// No parameters to group."
            };
        }

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
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.Modified,
                    FilePath = filePath,
                    Message = warning,
                    UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
                };
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

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    private class ParamToPropertyRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _map;
        public ParamToPropertyRewriter(Dictionary<string, string> map)
        {
            _map = map;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var name = node.Identifier.Text;
            if (_map.TryGetValue(name, out var replacement))
            {
                return SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(node);
            }

            return base.VisitIdentifierName(node);
        }
    }
}
