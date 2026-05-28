using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ModernLoggingEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ModernLoggingEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Converts a standard logger call (e.g. _logger.LogInformation("Msg {Param}", p)) into a source-generated [LoggerMessage] method.
    /// </summary>
    public async Task<string> ConvertToSourceGeneratedLoggingAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            throw new InvalidOperationException("Class not found.");
        }

        // Identify logging calls
        var invocations = classNode.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                          ma.Name.Identifier.Text.StartsWith("Log") &&
                          (ma.Name.Identifier.Text == "LogInformation" || ma.Name.Identifier.Text == "LogError" || ma.Name.Identifier.Text == "LogWarning"))
            .ToList();

        if (invocations.Count == 0)
        {
            return root!.ToFullString();
        }

        int eventId = 1;
        var generatedMethods = new List<MethodDeclarationSyntax>();
        var replaceMap = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var inv in invocations)
        {
            if (inv.Expression is MemberAccessExpressionSyntax ma)
            {
                var levelStr = ma.Name.Identifier.Text.Substring(3); // e.g. "Information"
                var args = inv.ArgumentList.Arguments;
                if (args.Count == 0)
                {
                    continue;
                }

                // Simple heuristic: arg0 is message, subsequent args are params.
                var messageArg = args[0].Expression as LiteralExpressionSyntax;
                if (messageArg == null || !messageArg.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }

                var methodName = $"Log{levelStr}Event{eventId}";

                // Build LoggerMessage attribute
                var attrArgs = SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(eventId))),
                    SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression($"LogLevel.{levelStr}")),
                    SyntaxFactory.AttributeArgument(messageArg)
                }));

                var attrList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Attribute(SyntaxFactory.ParseName("LoggerMessage")).WithArgumentList(attrArgs)));

                // Build partial method parameters (ILogger + whatever args were passed)
                var parameters = new List<ParameterSyntax>
                {
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("logger")).WithType(SyntaxFactory.ParseTypeName("ILogger"))
                };

                for (int i = 1; i < args.Count; i++)
                {
                    parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier($"p{i}")).WithType(SyntaxFactory.ParseTypeName("object")));
                }

                var genMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), methodName)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.PartialKeyword))
                    .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
                    .AddAttributeLists(attrList)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                generatedMethods.Add(genMethod);

                // Build replacement invocation
                var newArgs = new List<ArgumentSyntax> { SyntaxFactory.Argument(ma.Expression) }; // pass the logger instance
                newArgs.AddRange(args.Skip(1)); // pass the rest of the args

                var newInv = SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(methodName))
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));

                replaceMap[inv] = newInv;
                eventId++;
            }
        }

        // Replace invocations first on the original classNode (before any AddModifiers mutation
        // that would create new green-node identities and make the replaceMap keys stale).
        var newClassNode = replaceMap.Count > 0
            ? classNode.ReplaceNodes(replaceMap.Keys, (oldNode, _) => replaceMap[oldNode])
            : classNode;

        // Now safe to AddModifiers (re-found tree is already correct after ReplaceNodes)
        if (!newClassNode.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            newClassNode = newClassNode.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword));
        }

        newClassNode = newClassNode.AddMembers(generatedMethods.ToArray());

        var newRoot = root!.ReplaceNode(classNode, newClassNode);

        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
