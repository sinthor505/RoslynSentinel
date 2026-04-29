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
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode == null || semanticModel == null) return "Method not found.";
        
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken);
        if (methodSymbol == null) return "Symbol not found.";

        var sb = new StringBuilder();
        await BuildCallTree(methodSymbol, 0, depth, sb, new HashSet<ISymbol>(SymbolEqualityComparer.Default), cancellationToken);
        return sb.ToString();
    }

    private async Task BuildCallTree(IMethodSymbol symbol, int currentDepth, int maxDepth, StringBuilder sb, HashSet<ISymbol> visited, CancellationToken ct)
    {
        if (currentDepth > maxDepth || visited.Contains(symbol)) return;
        visited.Add(symbol);
        sb.AppendLine($"{new string(' ', currentDepth * 2)}- {symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
    }

    public async Task<string> GenerateEqualityOverridesAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        return root?.ToFullString() ?? "";
    }

    public async Task<List<string>> DetectMemoryLeaksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MemoryLeaks")) return new List<string>();
        return new List<string>();
    }

    public async Task<List<string>> AnalyzeSemaphoreUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("SemaphoreLeaks")) return new List<string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();
        var targets = await GetTargetDocumentsAsync(solution, null, filePath, false, cancellationToken);

        foreach (var target in targets)
        {
            var semaphoreWaits = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.ToString().Contains(".WaitAsync("));

            foreach (var wait in semaphoreWaits)
            {
                var method = wait.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (method != null && !method.ToString().Contains(".Release()"))
                {
                    results.Add($"Potential Semaphore leak in method '{method.Identifier.Text}' in {target.Document.Name}");
                }
            }
        }
        return results;
    }

    public async Task<List<string>> FindPossibleInfiniteLoopsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return new List<string>();
    }

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
            var invocations = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var symbol = target.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                if (symbol != null && symbol.ReturnType.Name == "Task" && invocation.Parent is not AwaitExpressionSyntax)
                {
                     results.Add($"Potential mismatched await in {target.Document.Name} at line {invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1}. Call to '{symbol.Name}' is not awaited.");
                }
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
                .SelectMany(n => n.GetTypeMembers()).Where(t => t.TypeKind == TypeKind.Interface);

            foreach (var @interface in interfaces)
            {
                var implementations = await SymbolFinder.FindImplementationsAsync(@interface, solution, cancellationToken: cancellationToken);
                if (!implementations.Any()) results.Add($"Interface '{@interface.Name}' has no implementations.");
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
