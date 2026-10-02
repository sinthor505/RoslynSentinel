using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Advanced;
public class DeadCodeEngine
{
    private readonly SentinelConfiguration _config;
    private readonly IWorkspaceManager _workspaceManager;
    public DeadCodeEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config = null)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<List<DeadCodeReport>> FindUnusedPrivateMembersAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return new List<DeadCodeReport>();
        }

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new List<DeadCodeReport>();
        }

        var reports = new List<DeadCodeReport>();
        var privateMembers = classNode.Members.Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PrivateKeyword)) && m is MethodDeclarationSyntax or PropertyDeclarationSyntax);
        foreach (var member in privateMembers)
        {
            ISymbol? symbol = member switch
            {
                MethodDeclarationSyntax meth => semanticModel.GetDeclaredSymbol(meth, cancellationToken),
                PropertyDeclarationSyntax prop => semanticModel.GetDeclaredSymbol(prop, cancellationToken),
                _ => null
            };
            if (symbol == null)
            {
                continue;
            }

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
                reports.Add(new DeadCodeReport(filePath, memberName, lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1, "UnusedPrivateMember"));
            }
        }

        return reports;
    }

    public async Task<List<DeadCodeReport>> DetectUnusedPrivateFieldsAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new List<DeadCodeReport>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return new List<DeadCodeReport>();
        }

        var reports = new List<DeadCodeReport>();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        foreach (var classNode in classes)
        {
            var fields = classNode.Members.OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)));
            foreach (var field in fields)
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken);
                    if (symbol == null)
                    {
                        continue;
                    }

                    var usages = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                    if (!usages.Any(u => u.Locations.Any()))
                    {
                        var lineSpan = variable.GetLocation().GetLineSpan();
                        reports.Add(new DeadCodeReport(filePath, variable.Identifier.Text, lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1, "UnusedPrivateField"));
                    }
                }
            }
        }

        return reports;
    }

    public async Task<List<DeadCodeReport>> DetectUnusedLocalVariablesAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new List<DeadCodeReport>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return new List<DeadCodeReport>();
        }

        var reports = new List<DeadCodeReport>();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
        foreach (var method in methods)
        {
            if (method.Body == null)
            {
                continue;
            }

            var dataFlow = semanticModel.AnalyzeDataFlow(method.Body);
            if (dataFlow == null)
            {
                continue;
            }

            var declaredVars = method.Body.DescendantNodes().OfType<VariableDeclaratorSyntax>();
            foreach (var variable in declaredVars)
            {
                var symbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken);
                if (symbol == null)
                {
                    continue;
                }

                if (!dataFlow.ReadInside.Contains(symbol))
                {
                    var lineSpan = variable.GetLocation().GetLineSpan();
                    reports.Add(new DeadCodeReport(filePath, variable.Identifier.Text, lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1, "UnusedLocalVariable"));
                }
            }
        }

        return reports;
    }

    public async Task<List<DeadCodeReport>> FindUnusedConstructorsAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new List<DeadCodeReport>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return new List<DeadCodeReport>();
        }

        var reports = new List<DeadCodeReport>();
        foreach (var classNode in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var constructors = classNode.Members.OfType<ConstructorDeclarationSyntax>().ToList();
            // Skip single-constructor classes -> likely registered in DI, reference count is misleading
            if (constructors.Count < 2)
            {
                continue;
            }

            foreach (var ctor in constructors)
            {
                var symbol = semanticModel.GetDeclaredSymbol(ctor, cancellationToken);
                if (symbol == null)
                {
                    continue;
                }

                var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                if (!references.Any(r => r.Locations.Any()))
                {
                    var lineSpan = ctor.GetLocation().GetLineSpan();
                    reports.Add(new DeadCodeReport(filePath, $"{classNode.Identifier.Text}()", lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1, "UnusedConstructorOverload"));
                }
            }
        }

        return reports;
    }

    public async Task<List<DeadCodeReport>> CheckForUnusedEventSubscriptionsAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new List<DeadCodeReport>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new List<DeadCodeReport>();
        }

        var reports = new List<DeadCodeReport>();
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return reports;
        }

        // Build set of unsubscribed event+handler pairs from all -= assignments
        var removeKeys = new HashSet<string>(root.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)).Select(a => $"{a.Left}|{a.Right}"));
        // Report += subscriptions that have no matching -=
        // Only flag actual event subscriptions (not string +=, numeric +=, etc.)
        foreach (var add in root.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)))
        {
            var leftSymbol = semanticModel.GetSymbolInfo(add.Left, cancellationToken).Symbol;
            if (leftSymbol is not IEventSymbol)
            {
                continue;
            }

            var key = $"{add.Left}|{add.Right}";
            if (!removeKeys.Contains(key))
            {
                var lineSpan = add.GetLocation().GetLineSpan();
                reports.Add(new DeadCodeReport(filePath, add.Left.ToString(), lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1, "EventSubscriptionWithoutUnsubscription"));
            }
        }

        return reports;
    }

    private async Task<IEnumerable<(Document Document, SyntaxNode Root, SemanticModel? SemanticModel)>> GetTargetDocumentsAsync(Solution solution, string? projectName, string? filePath, bool includeSemantic = false, CancellationToken cancellationToken = default)
    {
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        var documentList = new List<(Document, SyntaxNode, SemanticModel? )>();
        foreach (var project in projects)
        {
            var docs = project.Documents.AsEnumerable();
            if (!string.IsNullOrEmpty(filePath))
            {
                docs = docs.Where(d => d.Name == filePath || d.FilePath == filePath || (d.FilePath != null && d.FilePath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase)));
            }

            foreach (var doc in docs)
            {
                var root = await doc.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                {
                    continue;
                }

                var model = includeSemantic ? await doc.GetSemanticModelAsync(cancellationToken) : null;
                documentList.Add((doc, root, model));
            }
        }

        return documentList;
    }

    public async Task<List<string>> FindUninstantiatedTypesAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("UninstantiatedTypes"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var instantiatedTypes = new HashSet<string>();
        var declaredTypes = new List<(string Name, string Document)>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, true, cancellationToken);
        foreach (var target in targets)
        {
            if (target.SemanticModel == null)
            {
                continue;
            }

            var objectCreations = target.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
            foreach (var creation in objectCreations)
            {
                var symbol = target.SemanticModel.GetSymbolInfo(creation, cancellationToken).Symbol?.ContainingType;
                if (symbol != null)
                {
                    instantiatedTypes.Add(symbol.ToDisplayString());
                }
            }

            var typeDecls = target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            foreach (var decl in typeDecls)
            {
                var symbol = target.SemanticModel.GetDeclaredSymbol(decl, cancellationToken);
                if (symbol != null)
                {
                    declaredTypes.Add((symbol.ToDisplayString(), target.Document.Name));
                }
            }
        }

        return declaredTypes.Where(t => !instantiatedTypes.Contains(t.Name)).Select(t => $"Type '{t.Name}' in {t.Document} is never instantiated.").ToList();
    }

    public async Task<List<string>> FindInternalClassesThatCouldBePrivateAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, true, cancellationToken);
        foreach (var target in targets)
        {
            if (target.SemanticModel == null)
            {
                continue;
            }

            var internalClasses = target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)));
            foreach (var @class in internalClasses)
            {
                var symbol = target.SemanticModel.GetDeclaredSymbol(@class, cancellationToken);
                if (symbol != null)
                {
                    var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                    var uniqueFiles = refs.SelectMany(r => r.Locations).Select(l => l.Document.FilePath).Distinct().Count();
                    if (uniqueFiles <= 1)
                    {
                        results.Add($"Internal class '{@class.Identifier.Text}' in {target.Document.Name} is only used in one file and could be made private.");
                    }
                }
            }
        }

        return results;
    }

    public async Task<List<string>> FindUnusedInterfacesAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("UnusedInterfaces"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            var interfaces = compilation.GlobalNamespace.GetNamespaceMembers().SelectMany(n => n.GetTypeMembers()).Where(t => t.TypeKind == TypeKind.Interface && t.DeclaringSyntaxReferences.Length > 0);
            foreach (var @interface in interfaces)
            {
                var implementations = await SymbolFinder.FindImplementationsAsync(@interface, solution, cancellationToken: cancellationToken);
                if (!implementations.Any())
                {
                    results.Add($"Interface '{@interface.Name}' has no implementations.");
                }
            }
        }

        return results;
    }
}