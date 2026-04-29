using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class InstrumentationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public InstrumentationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Wraps a method's body in a try/catch/finally block.
    /// </summary>
    public async Task<string> AddTryCatchToMethodAsync(string filePath, string methodName, string exceptionType = "Exception", bool addFinally = false, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null || methodNode.Body == null) throw new Exception("Method or body not found.");

        var catchBlock = SyntaxFactory.CatchClause(
            SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName(exceptionType), SyntaxFactory.Identifier("ex")),
            null,
            SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression("throw")) // Simple rethrow as default
            ));

        var tryStatement = SyntaxFactory.TryStatement(
            methodNode.Body,
            SyntaxFactory.SingletonList(catchBlock),
            addFinally ? SyntaxFactory.FinallyClause(SyntaxFactory.Block()) : null
        );

        var newMethodNode = methodNode.WithBody(SyntaxFactory.Block(tryStatement));
        var newRoot = root!.ReplaceNode(methodNode, newMethodNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Wraps all public methods in a class in try/catch blocks.
    /// </summary>
    public async Task<string> AddTryCatchToClassAsync(string filePath, string className, string exceptionType = "Exception", CancellationToken cancellationToken = default)
    {
         var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) throw new Exception("Class not found.");

        var publicMethods = classNode.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && m.Body != null)
            .ToList();

        var newRoot = root!.ReplaceNodes(publicMethods, (oldMethod, newMethod) => 
        {
            var catchBlock = SyntaxFactory.CatchClause(
                SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName(exceptionType), SyntaxFactory.Identifier("ex")),
                null,
                SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression("throw")))
            );

            var tryStatement = SyntaxFactory.TryStatement(
                newMethod.Body!,
                SyntaxFactory.SingletonList(catchBlock),
                null
            );

            return newMethod.WithBody(SyntaxFactory.Block(tryStatement));
        });

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// <summary>
    /// Adds Stopwatch diagnostics (prefix start, postfix stop and log) to a method.
    /// </summary>
    public async Task<string> AddStopwatchDiagnosticsAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null) throw new Exception("Could not parse syntax root.");

        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode == null || methodNode.Body == null) throw new Exception("Method or body not found.");

        if (!root.Usings.Any(u => u.Name.ToString() == "System.Diagnostics"))
        {
            root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Diagnostics")));
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
        var newMethodNode = methodNode.WithBody(newBody);
        
        var newRoot = root.ReplaceNode(methodNode, newMethodNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
