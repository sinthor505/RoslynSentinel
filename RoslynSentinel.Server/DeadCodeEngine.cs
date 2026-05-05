using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Server;

public record DeadCodeReport(string FilePath, string SymbolName, int Line, int Column, string Type);

public class DeadCodeEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public DeadCodeEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<DeadCodeReport>> FindUnusedPrivateMembersAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null) return new List<DeadCodeReport>();

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) return new List<DeadCodeReport>();

        var reports = new List<DeadCodeReport>();

        var privateMembers = classNode.Members
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PrivateKeyword))
                && m is MethodDeclarationSyntax or PropertyDeclarationSyntax);

        foreach (var member in privateMembers)
        {
            ISymbol? symbol = member switch
            {
                MethodDeclarationSyntax meth => semanticModel.GetDeclaredSymbol(meth, cancellationToken),
                PropertyDeclarationSyntax prop => semanticModel.GetDeclaredSymbol(prop, cancellationToken),
                _ => null
            };
            if (symbol == null) continue;

            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            if (!references.Any(r => r.Locations.Any()))
            {
                string memberName = member switch
                {
                    MethodDeclarationSyntax meth => meth.Identifier.Text,
                    PropertyDeclarationSyntax prop => prop.Identifier.Text,
                    _ => "Unknown"
                };
                var lineSpan = member.GetLocation().GetLineSpan();
                reports.Add(new DeadCodeReport(filePath, memberName,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    "UnusedPrivateMember"));
            }
        }

        return reports;
    }

    public async Task<List<DeadCodeReport>> DetectUnusedPrivateFieldsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<DeadCodeReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null) return new List<DeadCodeReport>();

        var reports = new List<DeadCodeReport>();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classNode in classes)
        {
            var fields = classNode.Members.OfType<FieldDeclarationSyntax>()
                .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)));

            foreach (var field in fields)
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken);
                    if (symbol == null) continue;

                    var usages = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                    if (!usages.Any(u => u.Locations.Any()))
                    {
                        var lineSpan = variable.GetLocation().GetLineSpan();
                        reports.Add(new DeadCodeReport(
                            filePath,
                            variable.Identifier.Text,
                            lineSpan.StartLinePosition.Line + 1,
                            lineSpan.StartLinePosition.Character + 1,
                            "UnusedPrivateField"
                        ));
                    }
                }
            }
        }

        return reports;
    }

    public async Task<List<DeadCodeReport>> DetectUnusedLocalVariablesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<DeadCodeReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null) return new List<DeadCodeReport>();

        var reports = new List<DeadCodeReport>();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

        foreach (var method in methods)
        {
            if (method.Body == null) continue;

            var dataFlow = semanticModel.AnalyzeDataFlow(method.Body);
            if (dataFlow == null) continue;

            var declaredVars = method.Body.DescendantNodes().OfType<VariableDeclaratorSyntax>();
            foreach (var variable in declaredVars)
            {
                var symbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken);
                if (symbol == null) continue;

                if (!dataFlow.ReadInside.Contains(symbol))
                {
                    var lineSpan = variable.GetLocation().GetLineSpan();
                    reports.Add(new DeadCodeReport(
                        filePath,
                        variable.Identifier.Text,
                        lineSpan.StartLinePosition.Line + 1,
                        lineSpan.StartLinePosition.Character + 1,
                        "UnusedLocalVariable"
                    ));
                }
            }
        }

        return reports;
    }

    public async Task<List<DeadCodeReport>> FindUnusedConstructorsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<DeadCodeReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null) return new List<DeadCodeReport>();

        var reports = new List<DeadCodeReport>();

        foreach (var classNode in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var constructors = classNode.Members.OfType<ConstructorDeclarationSyntax>().ToList();
            // Skip single-constructor classes — likely registered in DI, reference count is misleading
            if (constructors.Count < 2) continue;

            foreach (var ctor in constructors)
            {
                var symbol = semanticModel.GetDeclaredSymbol(ctor, cancellationToken);
                if (symbol == null) continue;

                var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                if (!references.Any(r => r.Locations.Any()))
                {
                    var lineSpan = ctor.GetLocation().GetLineSpan();
                    reports.Add(new DeadCodeReport(
                        filePath,
                        $"{classNode.Identifier.Text}()",
                        lineSpan.StartLinePosition.Line + 1,
                        lineSpan.StartLinePosition.Character + 1,
                        "UnusedConstructorOverload"));
                }
            }
        }

        return reports;
    }

    public async Task<List<DeadCodeReport>> CheckForUnusedEventSubscriptionsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<DeadCodeReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<DeadCodeReport>();

        var reports = new List<DeadCodeReport>();

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null) return reports;

        // Build set of unsubscribed event+handler pairs from all -= assignments
        var removeKeys = new HashSet<string>(
            root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression))
                .Select(a => $"{a.Left}|{a.Right}"));

        // Report += subscriptions that have no matching -=
        // Only flag actual event subscriptions (not string +=, numeric +=, etc.)
        foreach (var add in root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)))
        {
            var leftSymbol = semanticModel.GetSymbolInfo(add.Left, cancellationToken).Symbol;
            if (leftSymbol is not IEventSymbol)
                continue;

            var key = $"{add.Left}|{add.Right}";
            if (!removeKeys.Contains(key))
            {
                var lineSpan = add.GetLocation().GetLineSpan();
                reports.Add(new DeadCodeReport(
                    filePath,
                    add.Left.ToString(),
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    "EventSubscriptionWithoutUnsubscription"));
            }
        }

        return reports;
    }
}
