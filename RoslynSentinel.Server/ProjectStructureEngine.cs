using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ProjectStructureEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public ProjectStructureEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<string> FixMismatchedNamespacesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var project = document.Project;
        var defaultNamespace = project.DefaultNamespace ?? project.Name;
        
        var projectDir = Path.GetDirectoryName(project.FilePath);
        var fileDir = Path.GetDirectoryName(filePath);
        
        if (projectDir == null || fileDir == null || !fileDir.StartsWith(projectDir)) 
            return "";

        var relativePath = fileDir.Substring(projectDir.Length).Trim(Path.DirectorySeparatorChar);
        var expectedNamespace = string.IsNullOrEmpty(relativePath) 
            ? defaultNamespace 
            : $"{defaultNamespace}.{relativePath.Replace(Path.DirectorySeparatorChar, '.')}";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var nsNode = root?.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

        if (nsNode != null && nsNode.Name.ToString() != expectedNamespace)
        {
            var newNsName = SyntaxFactory.ParseName(expectedNamespace).WithTriviaFrom(nsNode.Name);
            var newNsNode = nsNode.WithName(newNsName);
            var newRoot = root!.ReplaceNode(nsNode, newNsNode);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        return root?.ToFullString() ?? "";
    }

    public async Task<string> MoveFileToNamespaceFolderAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var nsNode = root?.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (nsNode == null) return "";

        var ns = nsNode.Name.ToString();
        var project = document.Project;
        var projectDir = Path.GetDirectoryName(project.FilePath);
        var defaultNamespace = project.DefaultNamespace ?? project.Name;
        
        if (projectDir == null) return "";

        // Strip the default namespace from the beginning of the file's namespace
        // to get the relative folder path (project-relative, not solution-relative)
        var relativeFolderNamespace = ns;
        if (ns.StartsWith(defaultNamespace))
        {
            relativeFolderNamespace = ns.Substring(defaultNamespace.Length).TrimStart('.');
        }

        var relativePath = relativeFolderNamespace.Replace('.', Path.DirectorySeparatorChar);
        var expectedDir = relativePath.Length > 0 
            ? Path.Combine(projectDir, relativePath) 
            : projectDir;
        var expectedPath = Path.Combine(expectedDir, Path.GetFileName(filePath));

        if (filePath != expectedPath)
        {
            return $"MOVE_REQUIRED: {filePath} -> {expectedPath}";
        }

        return "File is already in the correct folder.";
    }

    public enum StructuralSmellType
    {
        All,
        MultiType,
        NameMismatch,
        NameofCandidate,
        LegacyGuardClause,
        Verbosity,
        ThreadSafety,
        TimeAbstraction,
        GenericException
    }

    public async Task<List<string>> FindStructuralSmellsAsync(
        StructuralSmellType typeFilter = StructuralSmellType.All,
        string? projectName = null,
        string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<string>();

        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var project in projects)
        {
            var documents = project.Documents;
            if (!string.IsNullOrEmpty(filePath))
            {
                documents = documents.Where(d => d.Name == filePath || d.FilePath == filePath || (d.FilePath != null && d.FilePath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase)));
            }

            foreach (var document in documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null) continue;

                // Skip Roslyn source-generator output — file names are always mismatched and contain generated types
                bool isGeneratedFile = (document.FilePath ?? document.Name).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);

                var types = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                    .Where(t => t is ClassDeclarationSyntax || t is InterfaceDeclarationSyntax || t is RecordDeclarationSyntax || t is StructDeclarationSyntax || t is EnumDeclarationSyntax)
                    .ToList();

                if (!isGeneratedFile && (typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.MultiType) && _config.IsFeatureEnabled("MultiTypeFile") && types.Count > 1)
                {
                    results.Add($"[MULTI_TYPE] File '{document.Name}' in project '{project.Name}' contains {types.Count} type declarations.");
                }

                if (!isGeneratedFile && (typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.NameMismatch) && _config.IsFeatureEnabled("NameMismatch") && types.Count > 0)
                {
                    // AppHost projects intentionally use Aspire resource-name constants whose file
                    // names don't correspond to class names — skip to avoid hundreds of false positives.
                    bool isAppHostProject = project.Name.EndsWith(".AppHost", StringComparison.OrdinalIgnoreCase)
                        || project.Name.Contains(".AppHost.", StringComparison.OrdinalIgnoreCase);

                    if (!isAppHostProject)
                    {
                        var primaryType = types[0].Identifier.Text;
                        var fileName = Path.GetFileNameWithoutExtension(document.FilePath ?? document.Name);
                        if (fileName != primaryType)
                        {
                            results.Add($"[NAME_MISMATCH] File '{document.Name}' in project '{project.Name}' does not match primary type '{primaryType}'.");
                        }
                    }
                }

                if ((typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.NameofCandidate) && _config.IsFeatureEnabled("UnboundNameof"))
                {
                    var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                    if (semanticModel != null)
                    {
                        var stringLiterals = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
                            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression));

                        foreach (var literal in stringLiterals)
                        {
                            var value = literal.Token.ValueText;
                            if (string.IsNullOrWhiteSpace(value) || value.Contains(' ')) continue;

                            var symbols = semanticModel.LookupSymbols(literal.SpanStart, name: value);
                            if (symbols.Any(s => s.Kind is SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property or SymbolKind.Field))
                            {
                                var line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                results.Add($"[NAMEOF_CANDIDATE] String literal \"{value}\" in '{document.Name}' (Line {line}) could be replaced with 'nameof({value})' for better type safety.");
                            }
                        }
                    }
                }

                if ((typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.LegacyGuardClause) && _config.IsFeatureEnabled("ModernGuardClauses"))
                {
                    var ifs = root.DescendantNodes().OfType<IfStatementSyntax>();
                    foreach (var ifStmt in ifs)
                    {
                        var throwStmt = ifStmt.Statement is ThrowStatementSyntax t ? t : 
                                        ifStmt.Statement is BlockSyntax b && b.Statements.Count == 1 && b.Statements[0] is ThrowStatementSyntax t2 ? t2 : null;

                        if (throwStmt != null && throwStmt.Expression is ObjectCreationExpressionSyntax oce)
                        {
                            var type = oce.Type.ToString();
                            bool isLegacy = false;
                            
                            if (type == "ArgumentNullException") isLegacy = true;
                            if (type == "ArgumentOutOfRangeException") isLegacy = true;
                            if (type == "ObjectDisposedException") isLegacy = true;

                            if (type == "ArgumentException" && ifStmt.Condition is InvocationExpressionSyntax ies)
                            {
                                var method = ies.Expression.ToString();
                                if (method is "string.IsNullOrEmpty" or "string.IsNullOrWhiteSpace" or "String.IsNullOrEmpty" or "String.IsNullOrWhiteSpace")
                                {
                                    isLegacy = true;
                                }
                            }

                            if (isLegacy)
                            {
                                var line = ifStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                results.Add($"[LEGACY_GUARD] Legacy if-throw guard clause in '{document.Name}' (Line {line}) could be modernized to '{type}.ThrowIf...' helpers.");
                            }
                        }
                    }
                }
                
                if (typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.Verbosity)
                {
                    // Target-typed new
                    if (_config.IsFeatureEnabled("RedundantTypeSpecification"))
                    {
                        var objCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
                        foreach (var oce in objCreations)
                        {
                            if (oce.Parent is VariableDeclarationSyntax vds && vds.Type.ToString() == oce.Type.ToString())
                            {
                                var line = oce.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                results.Add($"[VERBOSITY] Redundant type specification in '{document.Name}' (Line {line}). Use 'new()' for target-typed object creation.");
                            }
                        }
                    }

                    var arrayCreations = root.DescendantNodes().OfType<ArrayCreationExpressionSyntax>()
                        .Where(a => a.Initializer?.Expressions.Count == 0);
                    foreach (var ace in arrayCreations)
                    {
                         var line = ace.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                         results.Add($"[VERBOSITY] Empty array creation in '{document.Name}' (Line {line}). Use '[]' (collection expression) instead.");
                    }

                    var ifs = root.DescendantNodes().OfType<IfStatementSyntax>();
                    foreach (var ifStmt in ifs)
                    {
                        if (ifStmt.Condition is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.EqualsExpression) && be.Right.IsKind(SyntaxKind.NullLiteralExpression))
                        {
                            var assignment = ifStmt.Statement is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax asgn ? asgn : null;
                            if (assignment != null && assignment.Left.ToString() == be.Left.ToString())
                            {
                                var line = ifStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                                results.Add($"[VERBOSITY] Null-check assignment in '{document.Name}' (Line {line}). Use '??=' (null-coalescing assignment) instead.");
                            }
                        }
                    }
                }

                if ((typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.ThreadSafety) && _config.IsFeatureEnabled("ThreadSafety"))
                {
                    var locks = root.DescendantNodes().OfType<LockStatementSyntax>();
                    foreach (var lockStmt in locks)
                    {
                        var expr = lockStmt.Expression.ToString();
                        if (expr is "this" or "typeof")
                        {
                            var line = lockStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            results.Add($"[THREAD_SAFETY] Dangerous lock object '{expr}' in '{document.Name}' (Line {line}). Use a private readonly object or C# 13 'lock' keyword.");
                        }
                    }

                    var semaphores = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Where(i => i.Expression.ToString().EndsWith(".Wait") || i.Expression.ToString().EndsWith(".WaitAsync"));
                    foreach (var sem in semaphores)
                    {
                        var parentBlock = sem.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
                        if (parentBlock != null && !parentBlock.DescendantNodes().OfType<FinallyClauseSyntax>().Any(f => f.ToString().Contains(".Release()")))
                        {
                            var line = sem.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            results.Add($"[THREAD_SAFETY] Potential unsafe SemaphoreSlim usage in '{document.Name}' (Line {line}). 'Release()' is not called in a 'finally' block.");
                        }
                    }
                }

                if ((typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.TimeAbstraction) && _config.IsFeatureEnabled("TimeAbstraction"))
                {
                    // Only flag DateTime.Now/UtcNow/Today in non-static classes where injecting TimeProvider
                    // is both feasible and testable. Infrastructure-layer types are excluded because
                    // their DateTime usage (audit columns, scheduling, document timestamps) is not a
                    // unit-test concern where controlling the clock matters.
                    static bool IsInfrastructureSuffix(string name)
                    {
                        string[] excluded = [
                            "Helper", "Extensions", "Base", "Repository", "Worker", "Job",
                            "Exporter", "Importer", "Processor", "Builder", "Factory",
                            "Mapper", "Converter", "Hub", "Middleware", "Attribute",
                            "Serializer", "Deserializer", "Formatter", "Parser", "Writer",
                            "Reader", "Client", "Interceptor", "Decorator"
                        ];
                        return excluded.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                    }

                    var containingClasses = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                        .Where(c =>
                            !c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                            && !IsInfrastructureSuffix(c.Identifier.Text)
                            && !isGeneratedFile);

                    foreach (var cls in containingClasses)
                    {
                        var timeCalls = cls.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                            .Where(m => m.Expression.ToString() == "DateTime" && m.Name.Identifier.Text is "Now" or "UtcNow" or "Today");
                        foreach (var call in timeCalls)
                        {
                            var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            results.Add($"[TIME_ABSTRACTION] Direct DateTime.{call.Name.Identifier.Text} usage in '{document.Name}' (Line {line}). Consider injecting TimeProvider for better testability.");
                        }
                    }
                }

                if (typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.GenericException)
                {
                    var throws = root.DescendantNodes().OfType<ThrowStatementSyntax>();
                    foreach (var t in throws)
                    {
                        if (t.Expression is ObjectCreationExpressionSyntax oce && oce.Type.ToString() == "Exception")
                        {
                            var line = t.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            var message = oce.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString() ?? "No message";
                            results.Add($"[GENERIC_EXCEPTION] Generic 'throw new Exception({message})' in '{document.Name}' (Line {line}). Use a strongly-typed custom exception instead.");
                        }
                    }
                }
            }
        }
        return results;
    }
}
