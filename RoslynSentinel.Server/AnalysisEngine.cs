using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Security.Cryptography;
using System.Text;

namespace RoslynSentinel.Server;

public record LargeTypeReport(string FilePath, string TypeName, int LineCount);
public record LargeMethodReport(string FilePath, string TypeName, string MethodName, int LineCount);
public record DuplicateMethodGroup(string Hash, List<MethodLocation> Locations);
public record MethodLocation(string FilePath, string TypeName, string MethodName);
public record InterfaceCandidateReport(string FilePath, string ClassName, List<string> PublicMethods);

public class AnalysisEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public AnalysisEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    private async Task<IEnumerable<(Document Document, SyntaxNode Root, SemanticModel? SemanticModel)>> GetTargetDocumentsAsync(
        Solution solution, string? projectName, string? filePath, bool includeSemantic = false, CancellationToken ct = default)
    {
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        var documentList = new List<(Document, SyntaxNode, SemanticModel?)>();
        foreach (var project in projects)
        {
            var docs = project.Documents.AsEnumerable();
            if (!string.IsNullOrEmpty(filePath))
            {
                docs = docs.Where(d => d.Name == filePath || d.FilePath == filePath || (d.FilePath != null && d.FilePath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase)));
            }

            foreach (var doc in docs)
            {
                var root = await doc.GetSyntaxRootAsync(ct);
                if (root == null) continue;
                var model = includeSemantic ? await doc.GetSemanticModelAsync(ct) : null;
                documentList.Add((doc, root, model));
            }
        }
        return documentList;
    }

    public async Task<List<LargeTypeReport>> FindLargeTypesAsync(int maxLines = 500, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("LargeTypes")) return new List<LargeTypeReport>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var reports = new List<LargeTypeReport>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);

        foreach (var target in targets)
        {
            var types = target.Root.DescendantNodes().OfType<TypeDeclarationSyntax>();
            foreach (var type in types)
            {
                var lines = type.GetLocation().GetLineSpan().EndLinePosition.Line - type.GetLocation().GetLineSpan().StartLinePosition.Line;
                if (lines > maxLines)
                {
                    reports.Add(new LargeTypeReport(target.Document.FilePath ?? target.Document.Name, type.Identifier.Text, lines));
                }
            }
        }
        return reports.OrderByDescending(r => r.LineCount).ToList();
    }

    public async Task<List<LargeMethodReport>> FindLargeMethodsAsync(int maxLines = 50, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("LargeMethods")) return new List<LargeMethodReport>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var reports = new List<LargeMethodReport>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);

        foreach (var target in targets)
        {
            var methods = target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                var lines = method.GetLocation().GetLineSpan().EndLinePosition.Line - method.GetLocation().GetLineSpan().StartLinePosition.Line;
                if (lines > maxLines)
                {
                    var typeName = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Global";
                    reports.Add(new LargeMethodReport(target.Document.FilePath ?? target.Document.Name, typeName, method.Identifier.Text, lines));
                }
            }
        }
        return reports.OrderByDescending(r => r.LineCount).ToList();
    }

    public async Task<List<DuplicateMethodGroup>> FindDuplicateMethodsAsync(int minStatements = 5, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("DuplicateMethods")) return new List<DuplicateMethodGroup>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var methodHashes = new Dictionary<string, List<MethodLocation>>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);

        foreach (var target in targets)
        {
            var methods = target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Body != null && m.Body.Statements.Count >= minStatements);

            foreach (var method in methods)
            {
                var hash = ComputeStructuralHash(method.Body!);
                if (!methodHashes.ContainsKey(hash))
                    methodHashes[hash] = new List<MethodLocation>();

                var typeName = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Global";
                methodHashes[hash].Add(new MethodLocation(target.Document.FilePath ?? target.Document.Name, typeName, method.Identifier.Text));
            }
        }

        return methodHashes.Where(kvp => kvp.Value.Count > 1)
            .Select(kvp => new DuplicateMethodGroup(kvp.Key, kvp.Value))
            .OrderByDescending(g => g.Locations.Count).ToList();
    }

    public async Task<List<InterfaceCandidateReport>> FindInterfaceExtractionCandidatesAsync(int minPublicMethods = 3, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("UnusedInterfaces")) return new List<InterfaceCandidateReport>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var candidates = new List<InterfaceCandidateReport>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);

        foreach (var target in targets)
        {
            var classes = target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) && c.BaseList == null);

            foreach (var classNode in classes)
            {
                var publicMethods = classNode.Members.OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)))
                    .Select(m => m.Identifier.Text).ToList();

                if (publicMethods.Count >= minPublicMethods)
                {
                    candidates.Add(new InterfaceCandidateReport(target.Document.FilePath ?? target.Document.Name, classNode.Identifier.Text, publicMethods));
                }
            }
        }
        return candidates;
    }

    public async Task<List<string>> DetectLongParameterListsAsync(int threshold = 5, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("LongParameterLists")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);

        foreach (var target in targets)
        {
            var methods = target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.ParameterList.Parameters.Count > threshold);

            foreach (var method in methods)
            {
                results.Add($"Method '{method.Identifier.Text}' in {target.Document.Name} has {method.ParameterList.Parameters.Count} parameters.");
            }
        }
        return results;
    }

    public async Task<List<string>> FindUninstantiatedTypesAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("UninstantiatedTypes")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var instantiatedTypes = new HashSet<string>();
        var declaredTypes = new List<(string Name, string Document)>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, true, cancellationToken);

        foreach (var target in targets)
        {
            if (target.SemanticModel == null) continue;

            var objectCreations = target.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
            foreach (var creation in objectCreations)
            {
                var symbol = target.SemanticModel.GetSymbolInfo(creation, cancellationToken).Symbol?.ContainingType;
                if (symbol != null) instantiatedTypes.Add(symbol.ToDisplayString());
            }

            var typeDecls = target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            foreach (var decl in typeDecls)
            {
                var symbol = target.SemanticModel.GetDeclaredSymbol(decl, cancellationToken);
                if (symbol != null) declaredTypes.Add((symbol.ToDisplayString(), target.Document.Name));
            }
        }

        return declaredTypes.Where(t => !instantiatedTypes.Contains(t.Name))
            .Select(t => $"Type '{t.Name}' in {t.Document} is never instantiated.").ToList();
    }

    public async Task<List<string>> DetectUnreachableCodeAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return new List<string>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null) return new List<string>();

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);

        if (method == null) return new List<string>();

        var diagnostics = semanticModel.GetDiagnostics(method.Span, cancellationToken);
        return diagnostics
            .Where(d => d.Id == "CS0162")
            .Select(d => $"Unreachable code: {d.GetMessage()}")
            .ToList();
    }

    public async Task<List<string>> FindCircularDependenciesAsync(CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var projects = solution.Projects.ToDictionary(p => p.Id);

        foreach (var project in solution.Projects)
        {
            var visited = new HashSet<ProjectId>();
            var path = new List<ProjectId>();
            if (HasCycle(project.Id, visited, path, projects))
            {
                var cycleNames = string.Join(" -> ", path.Select(id => projects[id].Name));
                results.Add($"Circular dependency: {cycleNames} -> {project.Name}");
            }
        }
        return results.Distinct().ToList();
    }

    private bool HasCycle(ProjectId current, HashSet<ProjectId> visited, List<ProjectId> path, Dictionary<ProjectId, Project> projects)
    {
        if (path.Contains(current)) return true;
        if (visited.Contains(current)) return false;

        visited.Add(current);
        path.Add(current);

        foreach (var reference in projects[current].ProjectReferences)
        {
            if (HasCycle(reference.ProjectId, visited, path, projects)) return true;
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    public async Task<string> GenerateCallTreeAsync(string filePath, string methodName, int depth = 3, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "File not found.";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        
        // Parse methodName which may be in format "ClassName.MethodName" or just "MethodName"
        string? className = null;
        string actualMethodName = methodName;
        
        if (methodName.Contains("."))
        {
            var parts = methodName.Split('.');
            if (parts.Length == 2)
            {
                className = parts[0];
                actualMethodName = parts[1];
            }
        }
        
        // Find the method(s) matching the criteria
        IEnumerable<MethodDeclarationSyntax> candidateMethods = 
            root?.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.Text == actualMethodName) ?? Enumerable.Empty<MethodDeclarationSyntax>();
        
        // If className was provided, filter to implementations in that class
        if (className != null)
        {
            candidateMethods = candidateMethods.Where(m => 
            {
                var classNode = m.Parent;
                while (classNode != null && classNode is not ClassDeclarationSyntax && classNode is not StructDeclarationSyntax)
                    classNode = classNode.Parent;
                
                if (classNode is ClassDeclarationSyntax classDecl)
                    return classDecl.Identifier.Text == className;
                if (classNode is StructDeclarationSyntax structDecl)
                    return structDecl.Identifier.Text == className;
                return false;
            });
        }
        
        var methodNode = candidateMethods.FirstOrDefault();
        if (methodNode == null || semanticModel == null) return "Method not found.";
        
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken);
        if (methodSymbol == null) return "Symbol not found.";

        var sb = new StringBuilder();
        await BuildCallTree(methodSymbol, 0, depth, sb, new HashSet<ISymbol>(SymbolEqualityComparer.Default), cancellationToken);
        return sb.ToString();
    }

    private async Task BuildCallTree(IMethodSymbol symbol, int currentDepth, int maxDepth, StringBuilder sb, HashSet<ISymbol> visited, CancellationToken ct)
    {
        if (currentDepth > maxDepth || !visited.Add(symbol)) return;

        var indent = new string(' ', currentDepth * 2);
        sb.AppendLine($"{indent}- {symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var node = await syntaxRef.GetSyntaxAsync(ct);
            var document = solution.GetDocument(node.SyntaxTree);
            if (document == null) continue;

            var model = await document.GetSemanticModelAsync(ct);
            if (model == null) continue;

            foreach (var inv in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var info = model.GetSymbolInfo(inv, ct);
                if (info.Symbol is IMethodSymbol callee
                    && callee.ContainingAssembly?.Name == symbol.ContainingAssembly?.Name)
                {
                    await BuildCallTree(callee, currentDepth + 1, maxDepth, sb, visited, ct);
                }
            }
        }
    }

    public async Task<string> GenerateEqualityOverridesAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) throw new Exception($"File '{filePath}' not found in solution.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (root == null || classNode == null) throw new Exception($"Class '{className}' not found.");

        // Gather fields with their types (private instance fields only — skip const, static, backing fields)
        var fieldsWithTypes = classNode.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword) || m.IsKind(SyntaxKind.StaticKeyword)))
            .SelectMany(f => f.Declaration.Variables.Select(v =>
                (Name: v.Identifier.Text, Type: f.Declaration.Type.ToString())))
            .ToList();

        // Prefer auto-properties when available — they represent the class's semantic identity
        var propertyFields = classNode.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => !p.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) &&
                        p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true)
            .Select(p => (Name: p.Identifier.Text, Type: p.Type.ToString()))
            .ToList();

        if (propertyFields.Count > 0)
            fieldsWithTypes = propertyFields;

        if (fieldsWithTypes.Count == 0) throw new Exception($"Class '{className}' has no fields or properties to generate equality from.");

        var fieldNames = fieldsWithTypes.Select(f => f.Name).ToList();

        // Build: obj is ClassName other
        ExpressionSyntax body = SyntaxFactory.IsPatternExpression(
            SyntaxFactory.IdentifierName("obj"),
            SyntaxFactory.DeclarationPattern(
                SyntaxFactory.IdentifierName(className),
                SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("other"))));

        // Chain: && field == other.field  (or SequenceEqual for collection types)
        foreach (var (name, typeName) in fieldsWithTypes)
        {
            ExpressionSyntax equality;
            if (IsCollectionType(typeName))
            {
                // Use Enumerable.SequenceEqual for collection types (reference equality is wrong)
                equality = SyntaxFactory.ParseExpression(
                    $"Enumerable.SequenceEqual({name} ?? Enumerable.Empty<{GetElementType(typeName)}>(), other.{name} ?? Enumerable.Empty<{GetElementType(typeName)}>())");
            }
            else
            {
                equality = SyntaxFactory.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    SyntaxFactory.IdentifierName(name),
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("other"),
                        SyntaxFactory.IdentifierName(name)));
            }
            body = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
                body.WithTrailingTrivia(SyntaxFactory.Space),
                equality);
        }

        var equalsMethod = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                "Equals")
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space))
            .AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("obj"))
                    .WithType(SyntaxFactory.NullableType(
                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)))))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(body))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        // Build GetHashCode: HashCode.Combine(f1, f2, ...) — handles up to 8 args natively
        ExpressionSyntax hashBody;
        if (fieldNames.Count <= 8)
        {
            hashBody = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("HashCode"),
                        SyntaxFactory.IdentifierName("Combine")))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(
                        fieldNames.Select(n => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(n))))));
        }
        else
        {
            // For >8 fields use a HashCode builder
            // Emit: { var hc = new HashCode(); hc.Add(f1); ...; return hc.ToHashCode(); }
            var statements = new List<StatementSyntax>
            {
                SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .AddVariables(SyntaxFactory.VariableDeclarator("hc")
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName("HashCode"))
                                    .WithArgumentList(SyntaxFactory.ArgumentList())))))
            };
            foreach (var name in fieldNames)
            {
                statements.Add(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("hc"), SyntaxFactory.IdentifierName("Add")))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(SyntaxFactory.IdentifierName(name)))))));
            }
            statements.Add(SyntaxFactory.ReturnStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("hc"), SyntaxFactory.IdentifierName("ToHashCode")))));

            var getHashBlockBody = SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                    "GetHashCode")
                .AddModifiers(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                .WithBody(SyntaxFactory.Block(statements));

            var newClassForBlock = classNode.AddMembers(equalsMethod, getHashBlockBody);
            var updatedRootBlock = root.ReplaceNode(classNode, newClassForBlock);
            return updatedRootBlock.NormalizeWhitespace().ToFullString();
        }

        var getHashMethod = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                "GetHashCode")
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(hashBody))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        var newClass = classNode.AddMembers(equalsMethod, getHashMethod);
        var updatedRoot = root.ReplaceNode(classNode, newClass);
        return updatedRoot.NormalizeWhitespace().ToFullString();
    }

    private static bool IsCollectionType(string typeName)
    {
        var t = typeName.Trim().TrimEnd('?');
        return t.EndsWith("[]")
            || t.StartsWith("List<")
            || t.StartsWith("IList<")
            || t.StartsWith("IEnumerable<")
            || t.StartsWith("ICollection<")
            || t.StartsWith("IReadOnlyList<")
            || t.StartsWith("IReadOnlyCollection<")
            || t.StartsWith("HashSet<")
            || t.StartsWith("SortedSet<")
            || t.StartsWith("Collection<");
    }

    private static string GetElementType(string typeName)
    {
        var t = typeName.Trim().TrimEnd('?');
        if (t.EndsWith("[]")) return t[..^2];
        var open = t.IndexOf('<');
        var close = t.LastIndexOf('>');
        if (open >= 0 && close > open) return t[(open + 1)..close];
        return "object";
    }

    public async Task<List<string>> DetectMemoryLeaksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MemoryLeaks")) return new List<string>();

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var targets = await GetTargetDocumentsAsync(solution, null, filePath, false, cancellationToken);
        var results = new List<string>();

        foreach (var target in targets)
        {
            foreach (var classNode in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                bool implementsDisposable = classNode.BaseList?.Types
                    .Any(t => t.ToString().Contains("IDisposable")) ?? false;

                // Find subscriptions to external events (left side is a non-this member access)
                var subscriptions = classNode.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                        && a.Left is MemberAccessExpressionSyntax ma
                        && ma.Expression is not ThisExpressionSyntax)
                    .ToList();

                if (!subscriptions.Any()) continue;

                // Find the unsubscriptions
                var unsubscribeKeys = new HashSet<string>(
                    classNode.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                        .Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression))
                        .Select(a => $"{a.Left}|{a.Right}"));

                foreach (var sub in subscriptions)
                {
                    bool hasUnsubscribe = unsubscribeKeys.Contains($"{sub.Left}|{sub.Right}");
                    if (!implementsDisposable || !hasUnsubscribe)
                    {
                        var lineSpan = sub.GetLocation().GetLineSpan();
                        string reason = !implementsDisposable
                            ? "class does not implement IDisposable"
                            : "Dispose does not unsubscribe";
                        results.Add(
                            $"{target.Document.FilePath ?? target.Document.Name}:{lineSpan.StartLinePosition.Line + 1} " +
                            $"- Class '{classNode.Identifier.Text}' subscribes to '{sub.Left}' but {reason}.");
                    }

                    // Extra: lambda handler captures 'this' or an outer variable — the publisher
                    // holds a reference to the subscriber even without IDisposable.
                    bool handlerIsLambda = sub.Right is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax;
                    if (!handlerIsLambda) continue;

                    bool capturesThis = sub.Right.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any();
                    // Also flag if the lambda accesses a member through 'this' (implicit or explicit)
                    bool capturesMember = !capturesThis && sub.Right.DescendantNodesAndSelf()
                        .OfType<MemberAccessExpressionSyntax>()
                        .Any(ma => ma.Expression is ThisExpressionSyntax);
                    if (capturesThis || capturesMember)
                    {
                        var lineSpan2 = sub.GetLocation().GetLineSpan();
                        results.Add(
                            $"{target.Document.FilePath ?? target.Document.Name}:{lineSpan2.StartLinePosition.Line + 1} " +
                            $"- Class '{classNode.Identifier.Text}' subscribes to '{sub.Left}' with a lambda that captures " +
                            $"'this' — the publisher will keep this instance alive until unsubscribed.");
                    }
                }
            }
        }

        return results;
    }

    public async Task<List<string>> AnalyzeSemaphoreUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("SemaphoreLeaks")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, null, filePath, true, cancellationToken);

        foreach (var target in targets)
        {
            var semaphoreWaits = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.ToString().Contains(".WaitAsync("));

            foreach (var wait in semaphoreWaits)
            {
                // Semantic verification: confirm the receiver is actually SemaphoreSlim, not some
                // other class that happens to have a WaitAsync() method.
                if (target.SemanticModel != null &&
                    wait.Expression is MemberAccessExpressionSyntax waitMa)
                {
                    var receiverType = target.SemanticModel.GetTypeInfo(waitMa.Expression, cancellationToken).Type;
                    if (receiverType != null && !SemanticTypeHelper.IsSemaphoreSlim(receiverType)) continue;
                }

                var method = wait.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var containingType = wait.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (method == null) continue;

                // If this method already contains its own Release call, it handles the semaphore correctly.
                // Use ".Release" (without parens) to catch both Release() and Release(n).
                bool methodHandlesOwnRelease = method.ToString().Contains(".Release(");
                if (methodHandlesOwnRelease) continue;

                // Check if ANY other member of the class contains a release call.
                // Covers methods, constructors, properties, and finalizers — catches helper-method patterns.
                bool classHasReleaseElsewhere = containingType?.Members
                    .Where(m => !ReferenceEquals(m, method))
                    .Any(m => m.ToString().Contains(".Release(")) == true;

                if (!classHasReleaseElsewhere && target.SemanticModel != null && containingType != null)
                {
                    // Second pass: follow 1-level-deep method calls made from class members.
                    // Catches the pattern where Release is delegated to a helper in another class:
                    // e.g. this.Return() calls _helper.ReleaseSlot(_sem) which calls _sem.Release().
                    classHasReleaseElsewhere = containingType.Members
                        .Where(m => !ReferenceEquals(m, method))
                        .SelectMany(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        .Any(inv =>
                        {
                            var calledMethod = target.SemanticModel.GetSymbolInfo(inv, cancellationToken).Symbol as IMethodSymbol;
                            if (calledMethod == null) return false;
                            var syntaxRef = calledMethod.DeclaringSyntaxReferences.FirstOrDefault();
                            return syntaxRef?.GetSyntax(cancellationToken).ToString().Contains(".Release(") == true;
                        });
                }

                if (classHasReleaseElsewhere)
                {
                    // Pool pattern — semaphore lifetime spans method boundaries intentionally.
                    // Report as advisory so callers know to verify the release path is always reachable.
                    results.Add($"Advisory (pool pattern): '{method.Identifier.Text}' in {target.Document.Name} acquires a semaphore slot; Release() is in another method of the same class. Verify the release path is always reachable (e.g., via try/finally or a paired return method).");
                }
                else
                {
                    // Genuine leak — WaitAsync is called but no Release() exists anywhere in the class.
                    results.Add($"Semaphore leak in '{method.Identifier.Text}' in {target.Document.Name}: WaitAsync() is called but no Release() was found in this class — pool slots will be permanently lost on exceptions.");
                }
            }
        }
        return results;
    }

    public async Task<List<string>> FindPossibleInfiniteLoopsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var targets = await GetTargetDocumentsAsync(solution, null, filePath, false, cancellationToken);
        var results = new List<string>();

        foreach (var target in targets)
        {
            // while (true) { ... }
            foreach (var loop in target.Root.DescendantNodes().OfType<WhileStatementSyntax>()
                .Where(w => w.Condition is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.TrueLiteralExpression)))
            {
                if (!HasExitStatement(loop.Statement))
                {
                    var method = loop.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var line = loop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(
                        $"{target.Document.FilePath ?? target.Document.Name}:{line} " +
                        $"- Potential infinite loop (while(true)) in '{method?.Identifier.Text ?? "<unknown>"}' — no break/return/throw found.");
                }
            }

            // for (;;) { ... } — ForStatement with no condition
            foreach (var loop in target.Root.DescendantNodes().OfType<ForStatementSyntax>()
                .Where(f => f.Condition == null))
            {
                if (!HasExitStatement(loop.Statement))
                {
                    var method = loop.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var line = loop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(
                        $"{target.Document.FilePath ?? target.Document.Name}:{line} " +
                        $"- Potential infinite loop (for(;;)) in '{method?.Identifier.Text ?? "<unknown>"}' — no break/return/throw found.");
                }
            }
        }

        return results;
    }

    private static bool HasExitStatement(StatementSyntax body) =>
        body.DescendantNodes().OfType<BreakStatementSyntax>().Any()
        || body.DescendantNodes().OfType<ReturnStatementSyntax>().Any()
        || body.DescendantNodes().OfType<ThrowStatementSyntax>().Any()
        || body.DescendantNodes().OfType<GotoStatementSyntax>().Any();

    public async Task<List<string>> FindInternalClassesThatCouldBePrivateAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, true, cancellationToken);

        foreach (var target in targets)
        {
            if (target.SemanticModel == null) continue;
            var internalClasses = target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)));

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

    public async Task<List<string>> DetectMismatchedAwaitAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MismatchedAwait")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, true, cancellationToken);

        foreach (var target in targets)
        {
            if (target.SemanticModel == null) continue;

            // Pre-collect all variables in this document that feed Task.WhenAll/WhenAny
            var whenAllVars = new HashSet<string>();
            foreach (var whenAllInv in target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var invName = whenAllInv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => null
                };
                if (invName is "WhenAll" or "WhenAny")
                {
                    foreach (var arg in whenAllInv.ArgumentList.Arguments)
                    {
                        if (arg.Expression is IdentifierNameSyntax argId)
                            whenAllVars.Add(argId.Identifier.Text);
                    }
                }
            }

            // Pre-collect variables that are awaited anywhere in the document.
            // var t = DoAsync(); ... await t; — t should not be flagged at the assignment site.
            var awaitedLocalVars = new HashSet<string>();
            foreach (var awaitExpr in target.Root.DescendantNodes().OfType<AwaitExpressionSyntax>())
            {
                if (awaitExpr.Expression is IdentifierNameSyntax awaitedId)
                    awaitedLocalVars.Add(awaitedId.Identifier.Text);
            }

            var invocations = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var symbol = target.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                ITypeSymbol? returnType;
                string invocationName;

                if (symbol != null)
                {
                    returnType = symbol.ReturnType;
                    invocationName = symbol.Name;
                }
                else
                {
                    // Delegate invocations (e.g. Func<Task> factory = ...; factory()) return null from GetSymbolInfo.
                    // Fall back to the invocation expression's type to catch unawaited delegate calls.
                    returnType = target.SemanticModel.GetTypeInfo(invocation, cancellationToken).Type;
                    invocationName = invocation.Expression.ToString();
                }

                if (returnType == null || !SemanticTypeHelper.IsTaskOrValueTask(returnType)) continue;
                // Direct await
                if (invocation.Parent is AwaitExpressionSyntax) continue;
                // await expr! — null-forgiving wraps the invocation; parent is PostfixUnary, grandparent is Await
                if (invocation.Parent is PostfixUnaryExpressionSyntax pue &&
                    pue.IsKind(SyntaxKind.SuppressNullableWarningExpression) &&
                    pue.Parent is AwaitExpressionSyntax)
                    continue;

                // Skip: assigned to a discard (  _ = SomeAsync()  )
                if (invocation.Parent is AssignmentExpressionSyntax assign &&
                    assign.Left is IdentifierNameSyntax discardId &&
                    discardId.Identifier.Text == "_")
                    continue;

                // Skip: the invocation IS the return expression (expression-bodied or return statement).
                // e.g.  public Task<T> FooAsync() => Task.FromResult(x);
                //        return Task.FromResult(x);
                // These are not fire-and-forget — the Task is propagated to the caller.
                if (invocation.Parent is ArrowExpressionClauseSyntax) continue;
                if (invocation.Parent is ReturnStatementSyntax) continue;

                // Skip: the invocation IS the entire body of a lambda expression.
                // Covers Moq setup chains: .Setup(x => x.FooAsync(...)).ReturnsAsync(...)
                // and similar fluent API patterns where the Task is implicitly returned.
                if (invocation.Parent is SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax) continue;

                // Skip: the invocation is an argument to a new ValueTask<T>(...) constructor.
                // e.g.  ct => new ValueTask<T>(GetByIdFromDbAsync(id, ct))
                // The Task is propagated to the caller via the ValueTask wrapper.
                if (invocation.Parent is ArgumentSyntax &&
                    invocation.Parent.Parent?.Parent is ObjectCreationExpressionSyntax valueTaskCtor &&
                    valueTaskCtor.Type.ToString().Contains("ValueTask"))
                    continue;

                // Skip: assigned to a local variable that is later passed to Task.WhenAll/WhenAny
                if (invocation.Parent is EqualsValueClauseSyntax evc &&
                    evc.Parent is VariableDeclaratorSyntax vd &&
                    whenAllVars.Contains(vd.Identifier.Text))
                    continue;

                // Skip: direct argument to Task.WhenAll / Task.WhenAny / Task.WhenEach
                // e.g. await Task.WhenAll(RemoveAsync(id), UpdateAsync(id))
                if (invocation.Parent is ArgumentSyntax directArg &&
                    directArg.Parent?.Parent is InvocationExpressionSyntax composerInv &&
                    composerInv.Expression is MemberAccessExpressionSyntax composerMa &&
                    composerMa.Name.Identifier.Text is "WhenAll" or "WhenAny" or "WhenEach")
                    continue;

                // Skip: Task / ValueTask factory methods — synchronous, no actual async work
                // e.g. Task.FromResult(x), Task.FromException(ex), Task.FromCanceled(ct)
                if (invocation.Expression is MemberAccessExpressionSyntax factoryMa &&
                    (factoryMa.Expression.ToString() is "Task" or "ValueTask") &&
                    factoryMa.Name.Identifier.Text is "FromResult" or "FromException" or "FromCanceled")
                    continue;

                // Skip: await using — the await keyword is on the using declaration, not the invocation directly
                // e.g.  await using var conn = OpenConnectionAsync(ct);
                var ancestorLocalDecl = invocation.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
                if (ancestorLocalDecl?.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) == true)
                    continue;

                // Skip: the invocation is the receiver in a method chain consumed by the chain itself
                // e.g. MethodAsync().ContinueWith(...)  — the returned Task feeds the chain
                if (invocation.Parent is MemberAccessExpressionSyntax chainedMa &&
                    chainedMa.Parent is InvocationExpressionSyntax chainedCall &&
                    chainedMa.Name.Identifier.Text is "ContinueWith" or "Unwrap" or "ConfigureAwait" or "AsTask")
                    continue;

                // Skip: result stored in a local variable that is later awaited in this document
                // e.g. var t = DoAsync(); ... await t;
                if (invocation.Parent is EqualsValueClauseSyntax evc2 &&
                    evc2.Parent is VariableDeclaratorSyntax vd2 &&
                    awaitedLocalVars.Contains(vd2.Identifier.Text))
                    continue;

                // Skip: result stored in a field or property (deferred consumption).
                // Covers: this._task = X()  (MemberAccess) and  _task = X()  (Identifier, underscore convention).
                if (invocation.Parent is AssignmentExpressionSyntax fieldAssign &&
                    (fieldAssign.Left is MemberAccessExpressionSyntax ||
                     (fieldAssign.Left is IdentifierNameSyntax lhsId &&
                      lhsId.Identifier.Text.StartsWith("_", StringComparison.Ordinal))))
                    continue;

                // Skip: invocation is inside a ternary or null-coalescing expression
                // The Task value is used as a value in the expression, likely returned or stored.
                if (invocation.Ancestors().OfType<ConditionalExpressionSyntax>().Any())
                    continue;
                if (invocation.Parent is BinaryExpressionSyntax coalesceOp &&
                    coalesceOp.IsKind(SyntaxKind.CoalesceExpression))
                    continue;

                // Skip: invocation inside an anonymous method expression
                // e.g. Action fn = delegate { DoWorkAsync(); };
                if (invocation.Ancestors().OfType<AnonymousMethodExpressionSyntax>().Any())
                    continue;

                // Skip: invocation inside an object or collection initializer
                // e.g. new Foo { BackgroundTask = StartAsync() }
                if (invocation.Ancestors().OfType<InitializerExpressionSyntax>().Any())
                    continue;

                // Skip: argument to collection/task-composition methods that consume tasks
                // e.g. _tasks.Add(DoWorkAsync())  or  Task.Run(DoWorkAsync)
                if (invocation.Parent is ArgumentSyntax taskConsumerArg &&
                    taskConsumerArg.Parent?.Parent is InvocationExpressionSyntax taskConsumerInv &&
                    taskConsumerInv.Expression is MemberAccessExpressionSyntax taskConsumerMa &&
                    taskConsumerMa.Name.Identifier.Text is "Add" or "TryAdd" or "Append" or "Push" or
                        "Enqueue" or "Run" or "StartNew" or "Schedule" or "Post")
                    continue;

                results.Add($"Potential mismatched await in {target.Document.Name} at line {invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Call to '{invocationName}' is not awaited.");
            }
        }
        return results;
    }

    public async Task<List<string>> CheckForEmptyCatchBlocksAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("EmptyCatchBlocks")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);

        foreach (var target in targets)
        {
            var catchBlocks = target.Root.DescendantNodes().OfType<CatchClauseSyntax>().Where(c => c.Block.Statements.Count == 0);
            results.AddRange(catchBlocks.Select(c => $"Empty catch block in {target.Document.Name} at line {c.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Silent failures are risky."));
        }
        return results;
    }

    public async Task<List<string>> FindLargeSwitchStatementsAsync(int threshold = 10, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("LargeSwitchStatements")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);

        foreach (var target in targets)
        {
            var switches = target.Root.DescendantNodes().OfType<SwitchStatementSyntax>().Where(s => s.Sections.Count > threshold);
            foreach (var sw in switches)
            {
                results.Add($"Large switch statement in {target.Document.Name} has {sw.Sections.Count} cases. Consider refactoring.");
            }
        }
        return results;
    }

    public async Task<List<string>> CheckForRedundantCastAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("RedundantCasts")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, true, cancellationToken);

        foreach (var target in targets)
        {
            if (target.SemanticModel == null) continue;
            var casts = target.Root.DescendantNodes().OfType<CastExpressionSyntax>();

            foreach (var cast in casts)
            {
                var typeInfo = target.SemanticModel.GetTypeInfo(cast.Expression, cancellationToken);
                var castTypeInfo = target.SemanticModel.GetTypeInfo(cast, cancellationToken);
                
                if (typeInfo.Type != null && castTypeInfo.Type != null && SymbolEqualityComparer.Default.Equals(typeInfo.Type, castTypeInfo.Type))
                {
                    results.Add($"Redundant cast in {target.Document.Name} at line {cast.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Expression is already of type {castTypeInfo.Type.Name}.");
                }
            }
        }
        return results;
    }

    public async Task<List<string>> OptimizeResourceDisposalAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ResourceDisposal")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, true, cancellationToken);
        var disposalExclusions = new HashSet<string> { "HttpClient", "Task", "MemoryStream" };

        foreach (var target in targets)
        {
            if (target.SemanticModel == null) continue;
            var localDeclarations = target.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>();
            foreach (var decl in localDeclarations)
            {
                foreach (var variable in decl.Declaration.Variables)
                {
                    if (variable.Initializer == null) continue;
                    var typeInfo = target.SemanticModel.GetTypeInfo(variable.Initializer.Value, cancellationToken);
                    
                    if (typeInfo.Type != null && typeInfo.Type.AllInterfaces.Any(i => i.Name == "IDisposable") && !disposalExclusions.Contains(typeInfo.Type.Name))
                    {
                        var isDisposeHandled = decl.Ancestors().Any(a => a is UsingStatementSyntax || a is LocalDeclarationStatementSyntax l && l.UsingKeyword.IsKind(SyntaxKind.UsingKeyword));
                        if (!isDisposeHandled)
                        {
                            results.Add($"[DISPOSAL] Variable '{variable.Identifier.Text}' of type '{typeInfo.Type.Name}' is IDisposable but not within a 'using' in {target.Document.Name}.");
                        }
                    }
                }
            }
        }
        return results;
    }

    public async Task<List<string>> DetectReflectionUsageAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ReflectionUsage")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);

        foreach (var target in targets)
        {
            var memberAccesses = target.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>();
            foreach (var access in memberAccesses)
            {
                var name = access.Name.Identifier.Text;
                if (name is "GetProperty" or "GetMethod" or "GetField" or "GetCustomAttribute" or "Invoke")
                {
                    results.Add($"[REFLECTION] Potential reflection usage in {target.Document.Name} at line {access.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (.{name}). Reflection can impact performance and AOT compatibility.");
                }
            }
        }
        return results;
    }

    public async Task<List<string>> DetectInefficientStringComparisonsAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("InefficientStringComparison")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);

        foreach (var target in targets)
        {
            var comparisons = target.Root.DescendantNodes().OfType<BinaryExpressionSyntax>()
                .Where(b => b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression));

            foreach (var comp in comparisons)
            {
                if (comp.Left is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma && (ma.Name.Identifier.Text == "ToLower" || ma.Name.Identifier.Text == "ToUpper"))
                {
                    results.Add($"[PERF] Inefficient string comparison in {target.Document.Name} at line {comp.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Use string.Equals with StringComparison.OrdinalIgnoreCase instead.");
                }
            }
        }
        return results;
    }

    public async Task<List<string>> FindBoxingAllocationsAsync(string? filePath = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("BoxingAllocation")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, true, cancellationToken);

        foreach (var target in targets)
        {
            if (target.SemanticModel == null) continue;

            // 1. Check Assignments (e.g. object o; o = 1;)
            var assignments = target.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>();
            foreach (var assignment in assignments)
            {
                var leftType = target.SemanticModel.GetTypeInfo(assignment.Left, cancellationToken).Type;
                var rightType = target.SemanticModel.GetTypeInfo(assignment.Right, cancellationToken).Type;

                if (IsBoxing(leftType, rightType))
                {
                    results.Add($"[PERF] Boxing allocation in {target.Document.Name} at line {assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Value type '{rightType!.Name}' assigned to object.");
                }
            }

            // 2. Check Variable Declarations (e.g. object o = 1;)
            var declarators = target.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>();
            foreach (var declarator in declarators)
            {
                if (declarator.Initializer == null) continue;
                
                var symbol = target.SemanticModel.GetDeclaredSymbol(declarator, cancellationToken);
                ITypeSymbol? leftType = null;

                if (symbol is ILocalSymbol local) leftType = local.Type;
                else if (symbol is IFieldSymbol field) leftType = field.Type;
                
                if (leftType != null)
                {
                    var rightType = target.SemanticModel.GetTypeInfo(declarator.Initializer.Value, cancellationToken).Type;

                    if (IsBoxing(leftType, rightType))
                    {
                        results.Add($"[PERF] Boxing allocation in {target.Document.Name} at line {declarator.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Value type '{rightType!.Name}' assigned to object.");
                    }
                }
            }
        }
        return results;
    }

    private bool IsBoxing(ITypeSymbol? left, ITypeSymbol? right)
    {
        if (left == null || right == null) return false;
        return (left.SpecialType == SpecialType.System_Object || left.Name == "ValueType") && right.IsValueType;
    }

    public async Task<List<string>> FindPossibleDeadlocksAsync(string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("Deadlocks")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);

        foreach (var target in targets)
        {
            var lockStatements = target.Root.DescendantNodes().OfType<LockStatementSyntax>();
            foreach (var lockStmt in lockStatements)
            {
                if (lockStmt.DescendantNodes().OfType<LockStatementSyntax>().Any())
                {
                    results.Add($"Potential deadlock risk: Nested lock statement found at line {lockStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1} in {target.Document.Name}.");
                }
            }
        }
        return results;
    }

    public async Task<List<string>> FindUnusedInterfacesAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("UnusedInterfaces")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName)) projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            var interfaces = compilation.GlobalNamespace.GetNamespaceMembers()
                .SelectMany(n => n.GetTypeMembers()).Where(t => t.TypeKind == TypeKind.Interface
                    && t.DeclaringSyntaxReferences.Length > 0);

            foreach (var @interface in interfaces)
            {
                var implementations = await SymbolFinder.FindImplementationsAsync(@interface, solution, cancellationToken: cancellationToken);
                if (!implementations.Any()) results.Add($"Interface '{@interface.Name}' has no implementations.");
            }
        }
        return results;
    }

    /// <summary>
    /// Detects circular type dependencies at the class level by analysing constructor parameters.
    /// Unlike FindCircularDependenciesAsync (which checks project references), this method finds
    /// cycles in composition: ClassA's ctor takes ClassB, ClassB's ctor takes ClassA.
    /// Such cycles cause runtime DI failures without a compile error.
    /// </summary>
    public async Task<List<string>> FindCircularTypeReferencesAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, true, cancellationToken);

        // Build map: simpleName → set of constructor-parameter simple names
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var target in targets)
        {
            if (target.SemanticModel == null) continue;
            foreach (var classDecl in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var className = classDecl.Identifier.Text;
                if (!deps.ContainsKey(className)) deps[className] = new HashSet<string>(StringComparer.Ordinal);

                var ctors = classDecl.Members.OfType<ConstructorDeclarationSyntax>();
                foreach (var ctor in ctors)
                {
                    foreach (var param in ctor.ParameterList.Parameters)
                    {
                        if (param.Type == null) continue;
                        var typeSymbol = target.SemanticModel.GetTypeInfo(param.Type, cancellationToken).Type;
                        if (typeSymbol == null || typeSymbol.TypeKind == TypeKind.Error) continue;
                        // Only track types that are declared in this solution (user types)
                        if (typeSymbol.DeclaringSyntaxReferences.Length == 0) continue;
                        deps[className].Add(typeSymbol.Name);
                    }
                }
            }
        }

        var results = new List<string>();
        var reportedCycles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var start in deps.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var path = new List<string>();
            DetectTypeCycle(start, deps, visited, path, results, reportedCycles);
        }

        return results;
    }

    /// <summary>
    /// Detects classes that both implement IDisposable AND declare a finalizer (~Destructor).
    /// Unless the finalizer guards with a disposed flag, this risks double-freeing managed
    /// resources when the GC calls the finalizer after Dispose() was already called.
    /// </summary>
    public async Task<List<string>> FindFinalizerOnDisposableAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        var results = new List<string>();

        foreach (var target in targets)
        {
            foreach (var classDecl in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                bool implementsDisposable = classDecl.BaseList?.Types
                    .Any(t => t.ToString().Contains("IDisposable")) ?? false;
                if (!implementsDisposable) continue;

                var finalizer = classDecl.Members.OfType<DestructorDeclarationSyntax>().FirstOrDefault();
                if (finalizer == null) continue;

                // Check if the finalizer guards with a disposed flag (correct IDisposable pattern).
                // Use identifier-level checks only — "disposed" as a bare word also appears in
                // comments like "/* no disposed guard */" and would produce false negatives.
                var finalizerText = finalizer.ToString();
                bool hasDisposedGuard = finalizerText.Contains("_disposed") ||
                                        finalizerText.Contains("IsDisposed");

                if (!hasDisposedGuard)
                {
                    var loc = finalizer.GetLocation().GetLineSpan().StartLinePosition;
                    results.Add(
                        $"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " +
                        $"- Class '{classDecl.Identifier.Text}' implements IDisposable and declares a finalizer " +
                        $"without a disposed-flag guard. The GC may call the finalizer after Dispose(), " +
                        $"causing double-free. Add: if (_disposed) return; at the top of the finalizer.");
                }
            }
        }
        return results;
    }

    private static readonly HashSet<string> UnboundedCollectionTypes = new(StringComparer.Ordinal)
    {
        "Dictionary", "List", "HashSet", "SortedDictionary", "SortedSet",
        "ConcurrentDictionary", "ConcurrentBag", "ConcurrentQueue",
        "Queue", "Stack", "LinkedList"
    };

    /// <summary>
    /// Detects static fields that hold unbounded collections (Dictionary, List, etc.) with
    /// no size cap, Clear(), or expiry — a memory exhaustion DoS vector when populated from
    /// user-controlled data. Flags when: static field is a known collection type AND the class
    /// adds to it (.Add / .TryAdd) but never calls .Clear() or checks Count against a limit.
    /// </summary>
    public async Task<List<string>> FindUnboundedStaticCollectionsAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        var results = new List<string>();

        foreach (var target in targets)
        {
            foreach (var classDecl in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                // Find static fields of known collection types
                var staticCollectionFields = classDecl.Members.OfType<FieldDeclarationSyntax>()
                    .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                    .SelectMany(f => f.Declaration.Variables.Select(v => new
                    {
                        Name = v.Identifier.Text,
                        TypeName = f.Declaration.Type.ToString().Split('<')[0].Split('.')[^1]
                    }))
                    .Where(x => UnboundedCollectionTypes.Contains(x.TypeName))
                    .ToList();

                if (staticCollectionFields.Count == 0) continue;

                var classText = classDecl.ToString();
                foreach (var field in staticCollectionFields)
                {
                    // Must be populated via Add/TryAdd anywhere in the class
                    bool isPopulated = classDecl.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Any(inv => inv.Expression is MemberAccessExpressionSyntax ma &&
                                    ma.Expression.ToString().Contains(field.Name) &&
                                    ma.Name.Identifier.Text is "Add" or "TryAdd" or "TryGetOrAdd" or "Enqueue" or "Push");
                    if (!isPopulated) continue;

                    // Flag if there's no Clear(), no Count check, and no capacity/max constant
                    bool hasClear = classText.Contains(field.Name + ".Clear()");
                    bool hasCountCheck = classDecl.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                        .Any(ma => ma.Expression.ToString().Contains(field.Name) &&
                                   ma.Name.Identifier.Text == "Count");
                    bool hasMaxConstant = classDecl.Members.OfType<FieldDeclarationSyntax>()
                        .Any(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)) &&
                                  (f.Declaration.Variables.Any(v => v.Identifier.Text.ToLower().Contains("max") ||
                                                                     v.Identifier.Text.ToLower().Contains("limit"))));

                    if (!hasClear && !hasCountCheck && !hasMaxConstant)
                    {
                        var loc = classDecl.GetLocation().GetLineSpan().StartLinePosition;
                        results.Add(
                            $"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " +
                            $"- Static field '{field.Name}' ({field.TypeName}) in '{classDecl.Identifier.Text}' " +
                            $"is populated without a size cap or Clear() call. " +
                            $"This can cause unbounded memory growth (DoS) on user-controlled input.");
                    }
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Detects directly recursive methods — methods that call themselves on every code path
    /// without a depth parameter or an early-return base case that does NOT itself recurse.
    /// Unbounded recursion causes StackOverflowException on deep or adversarial input.
    /// </summary>
    public async Task<List<string>> FindUnboundedRecursionAsync(
        string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);
        var results = new List<string>();

        foreach (var target in targets)
        {
            foreach (var method in target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (method.Body == null && method.ExpressionBody == null) continue;
                var methodName = method.Identifier.Text;

                // Find self-recursive calls
                SyntaxNode body = (SyntaxNode?)method.Body ?? method.ExpressionBody!;
                var selfCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(inv =>
                    {
                        var name = inv.Expression switch
                        {
                            IdentifierNameSyntax id => id.Identifier.Text,
                            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                            _ => null
                        };
                        return name == methodName;
                    }).ToList();

                if (selfCalls.Count == 0) continue;

                // Look for a depth parameter (int depth, int level, int maxDepth, etc.)
                bool hasDepthParam = method.ParameterList.Parameters
                    .Any(p => p.Identifier.Text.ToLower() is "depth" or "level" or "maxdepth" or
                              "currentdepth" or "recursionlevel" or "limit");

                // Look for an early-return guard (if ... return without self-call)
                bool hasBaseCase = false;
                if (method.Body != null)
                {
                    foreach (var ifStmt in method.Body.DescendantNodes().OfType<IfStatementSyntax>())
                    {
                        // The if-statement's then/else contains a return/throw but no recursive call
                        var ifBody = ifStmt.Statement.ToString() + (ifStmt.Else?.Statement.ToString() ?? "");
                        bool hasSelfCall = selfCalls.Any(sc => ifBody.Contains(methodName + "("));
                        bool hasReturn = ifStmt.Statement.DescendantNodesAndSelf()
                            .Any(n => n is ReturnStatementSyntax or ThrowStatementSyntax);
                        if (hasReturn && !hasSelfCall)
                        {
                            hasBaseCase = true;
                            break;
                        }
                    }
                }

                if (!hasDepthParam && !hasBaseCase)
                {
                    var loc = method.GetLocation().GetLineSpan().StartLinePosition;
                    results.Add(
                        $"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " +
                        $"- Method '{methodName}' recurses without a depth guard or base case. " +
                        $"Deep or adversarial input will cause StackOverflowException. " +
                        $"Add a depth parameter or a non-recursive early-return guard.");
                }
            }
        }
        return results;
    }

    private static void DetectTypeCycle(
        string current,
        Dictionary<string, HashSet<string>> deps,
        HashSet<string> visited,
        List<string> path,
        List<string> results,
        HashSet<string> reportedCycles)
    {
        if (!deps.ContainsKey(current)) return;
        if (path.Contains(current))
        {
            // Found a cycle — normalise the cycle key so A→B→A and B→A→B produce one report
            var cycleStart = path.IndexOf(current);
            var cycle = path.Skip(cycleStart).Concat(new[] { current }).ToList();
            var key = string.Join("→", cycle.OrderBy(x => x));
            if (reportedCycles.Add(key))
                results.Add($"Circular type dependency: {string.Join(" → ", cycle)}");
            return;
        }
        if (visited.Contains(current)) return;
        visited.Add(current);
        path.Add(current);
        foreach (var dep in deps[current])
            DetectTypeCycle(dep, deps, visited, path, results, reportedCycles);
        path.RemoveAt(path.Count - 1);
    }

    /// <summary>
    /// Detects generic type parameters that are used in the method body in ways that imply
    /// a missing constraint — for example, null-comparing T without "where T : class",
    /// or calling new T() without "where T : new()".  These are not compile errors but are
    /// design gaps that can surprise callers and lead to confusing runtime exceptions.
    /// </summary>
    public async Task<List<string>> FindMissingGenericConstraintsAsync(
        string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var targets = await GetTargetDocumentsAsync(solution, projectName, filePath, false, cancellationToken);
        var results = new List<string>();

        foreach (var target in targets)
        {
            foreach (var method in target.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!method.TypeParameterList?.Parameters.Any() == true) continue;
                if (method.Body == null && method.ExpressionBody == null) continue;

                foreach (var typeParam in method.TypeParameterList!.Parameters)
                {
                    var tName = typeParam.Identifier.Text;

                    // Find existing constraints declared for this type parameter
                    var constraintClause = method.ConstraintClauses
                        .FirstOrDefault(cc => cc.Name.Identifier.Text == tName);
                    var existingConstraints = constraintClause?.Constraints
                        .Select(c => c.ToString())
                        .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();

                    SyntaxNode bodyNode = (SyntaxNode?)method.Body ?? method.ExpressionBody!;

                    // Collect parameter names whose declared type is exactly this type parameter.
                    // These are the expressions whose null comparison would be meaningless for value types.
                    var paramNamesOfT = method.ParameterList.Parameters
                        .Where(p => p.Type?.ToString() == tName)
                        .Select(p => p.Identifier.Text)
                        .ToHashSet(StringComparer.Ordinal);

                    // Check: body uses 'new T()' but no 'new()' constraint
                    bool usesNew = bodyNode.DescendantNodes()
                        .OfType<ObjectCreationExpressionSyntax>()
                        .Any(oc => oc.Type.ToString() == tName);
                    if (usesNew && !existingConstraints.Contains("new()"))
                    {
                        var loc = method.GetLocation().GetLineSpan().StartLinePosition;
                        results.Add(
                            $"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " +
                            $"- Method '{method.Identifier.Text}': type parameter '{tName}' is instantiated " +
                            $"with 'new {tName}()' but is missing 'where {tName} : new()' constraint.");
                    }

                    if (paramNamesOfT.Count == 0) continue; // no parameters typed as T — skip null checks

                    // Check: a parameter of type T is compared to null, but no 'class' constraint exists.
                    // Value types can never be null, so this comparison is always false for structs.
                    bool comparesToNull = bodyNode.DescendantNodes()
                        .OfType<BinaryExpressionSyntax>()
                        .Any(b =>
                            (b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression)) &&
                            ((b.Left is IdentifierNameSyntax li && paramNamesOfT.Contains(li.Identifier.Text) &&
                              b.Right is LiteralExpressionSyntax rn && rn.IsKind(SyntaxKind.NullLiteralExpression)) ||
                             (b.Right is IdentifierNameSyntax ri && paramNamesOfT.Contains(ri.Identifier.Text) &&
                              b.Left is LiteralExpressionSyntax ln && ln.IsKind(SyntaxKind.NullLiteralExpression))));

                    // Also catch 'x is null' / 'x is not null' patterns on T-typed params
                    bool isNullPattern = bodyNode.DescendantNodes()
                        .OfType<IsPatternExpressionSyntax>()
                        .Any(ip => ip.Expression is IdentifierNameSyntax id && paramNamesOfT.Contains(id.Identifier.Text));

                    bool hasClassConstraint = existingConstraints.Contains("class") ||
                                              existingConstraints.Contains("notnull") ||
                                              existingConstraints.Any(c => c.StartsWith("class"));

                    if ((comparesToNull || isNullPattern) && !hasClassConstraint)
                    {
                        var loc = method.GetLocation().GetLineSpan().StartLinePosition;
                        results.Add(
                            $"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " +
                            $"- Method '{method.Identifier.Text}': type parameter '{tName}' is compared to null " +
                            $"but is missing 'where {tName} : class' constraint — value types will never be null.");
                    }
                }
            }
        }
        return results;
    }

    private string ComputeStructuralHash(BlockSyntax body)
    {
        var kinds = body.DescendantNodes().Select(n => (int)n.Kind());
        var bytes = kinds.SelectMany(BitConverter.GetBytes).ToArray();
        return Convert.ToBase64String(SHA256.HashData(bytes));
    }
}
