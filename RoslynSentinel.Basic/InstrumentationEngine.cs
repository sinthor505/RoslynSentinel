using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynSentinel.Basic;

public class InstrumentationEngine
{
    private readonly ISolutionProvider _workspaceManager;

    public InstrumentationEngine(ISolutionProvider workspaceManager)
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
    /// Wraps a method's body in a try/catch/finally block.
    /// </summary>
    public async Task<DocumentEditResult> AddTryCatchToMethodAsync(FilePath filePath, string methodName, string exceptionType = "Exception", bool addFinally = false, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null || methodNode.Body == null)
        {
            throw new InvalidOperationException($"Method or body not found: {methodName}");
        }

        var catchBlock = SyntaxFactory.CatchClause(
            SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName(exceptionType), SyntaxFactory.Identifier("ex")),
            null,
            SyntaxFactory.Block(
                SyntaxFactory.ThrowStatement() // Rethrow the caught exception
            ));

        var tryStatement = SyntaxFactory.TryStatement(
            methodNode.Body,
            SyntaxFactory.SingletonList(catchBlock),
            addFinally ? SyntaxFactory.FinallyClause(SyntaxFactory.Block()) : null
        );

        var newMethodNode = methodNode.WithBody(SyntaxFactory.Block(tryStatement));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Try/catch added to method.",
            UpdatedText = await ReplaceNodeFormattedAsync(document, root!, methodNode, newMethodNode, cancellationToken)
        };
    }

    /// <summary>
    /// Wraps all public methods in a class in try/catch blocks.
    /// </summary>
    public async Task<DocumentEditResult> AddTryCatchToClassAsync(FilePath filePath, string className, string exceptionType = "Exception", CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = (root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className)) ?? throw new InvalidOperationException($"Class not found: {className}");
        var publicMethods = classNode.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && m.Body != null)
            .ToList();

        var annotation = new SyntaxAnnotation();
        var newRoot = root!.ReplaceNodes(publicMethods, (oldMethod, newMethod) =>
        {
            var catchBlock = SyntaxFactory.CatchClause(
                SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName(exceptionType), SyntaxFactory.Identifier("ex")),
                null,
                SyntaxFactory.Block(SyntaxFactory.ThrowStatement())
            );

            var tryStatement = SyntaxFactory.TryStatement(
                newMethod.Body!,
                SyntaxFactory.SingletonList(catchBlock),
                null
            );

            return newMethod.WithBody(SyntaxFactory.Block(tryStatement)).WithAdditionalAnnotations(annotation);
        });

        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Try/catch added to class methods.",
            UpdatedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString()
        };
    }

    /// <summary>
    /// Adds Stopwatch diagnostics (prefix start, postfix stop and log) to a method.
    /// </summary>
    public async Task<DocumentEditResult> AddStopwatchDiagnosticsAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax ?? throw new InvalidOperationException($"Could not parse syntax root for file: {filePath}");
        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null || methodNode.Body == null)
        {
            throw new InvalidOperationException($"Method or body not found: {methodName} in file: {filePath}");
        }

        // Shared annotation for every node introduced below (the added using directive, if any, and
        // the rewritten method) — lets the final Formatter.FormatAsync pass reformat just these
        // edited spans instead of reflowing the whole file.
        var annotation = new SyntaxAnnotation();

        if (!root.Usings.Any(u => u.Name?.ToString() == "System.Diagnostics"))
        {
            root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Diagnostics")).WithAdditionalAnnotations(annotation));
        }

        // Re-find method after root change
        methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        var swStart = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator("sw")
                .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("Stopwatch"), SyntaxFactory.IdentifierName("StartNew"))))))));

        var swLog = SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression($"Console.WriteLine($\"[{methodName}] completed in {{sw.ElapsedMilliseconds}}ms\")"));

        // If method returns void, just append. If it returns a value, we have to capture the return, log, then return.
        // Using a try/finally block is the most robust way to inject post-fix logic regardless of return statements.

        var finallyClause = SyntaxFactory.FinallyClause(SyntaxFactory.Block(swLog));
        var tryStatement = SyntaxFactory.TryStatement(methodNode!.Body!, SyntaxFactory.List<CatchClauseSyntax>(), finallyClause);

        var newBody = SyntaxFactory.Block(swStart, tryStatement);
        var newMethodNode = methodNode.WithBody(newBody).WithAdditionalAnnotations(annotation);

        var newRoot = root.ReplaceNode(methodNode, newMethodNode);
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Stopwatch diagnostics added to method.",
            UpdatedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString()
        };
    }
}
