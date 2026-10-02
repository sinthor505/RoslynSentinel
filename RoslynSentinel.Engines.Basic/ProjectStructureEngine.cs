using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;
public class SolutionStructureEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;
    public SolutionStructureEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<DocumentEditResult> FixMismatchedNamespacesAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var project = document.Project;
        var defaultNamespace = project.DefaultNamespace ?? project.Name;
        var projectDir = Path.GetDirectoryName(project.FilePath);
        var fileDir = Path.GetDirectoryName(filePath);
        if (projectDir == null || fileDir == null || !fileDir.StartsWith(projectDir))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// File is outside the project directory."
            };
        }

        var relativePath = fileDir.Substring(projectDir.Length).Trim(Path.DirectorySeparatorChar);
        var expectedNamespace = string.IsNullOrEmpty(relativePath) ? defaultNamespace : $"{defaultNamespace}.{relativePath.Replace(Path.DirectorySeparatorChar, '.')}";
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var nsNode = root?.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (nsNode != null && nsNode.Name.ToString() != expectedNamespace)
        {
            var newNsName = SyntaxFactory.ParseName(expectedNamespace).WithTriviaFrom(nsNode.Name);
            var newNsNode = nsNode.WithName(newNsName);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                Message = "// Namespace updated.",
                UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, nsNode, newNsNode, cancellationToken)
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.TargetNotFound,
            FilePath = filePath,
            Message = "// Namespace is already correct."
        };
    }

    public async Task<DocumentEditResult> PreviewMoveFileToNamespaceFolderAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
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
        var nsNode = root?.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (nsNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Namespace not found."
            };
        }

        var ns = nsNode.Name.ToString();
        var project = document.Project;
        var projectDir = Path.GetDirectoryName(project.FilePath);
        var defaultNamespace = project.DefaultNamespace ?? project.Name;
        if (projectDir == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Project directory not found."
            };
        }

        // Strip the default namespace from the beginning of the file's namespace
        // to get the relative folder path (project-relative, not solution-relative)
        var relativeFolderNamespace = ns;
        if (ns.StartsWith(defaultNamespace))
        {
            relativeFolderNamespace = ns.Substring(defaultNamespace.Length).TrimStart('.');
        }

        var relativePath = relativeFolderNamespace.Replace('.', Path.DirectorySeparatorChar);
        var expectedDir = relativePath.Length > 0 ? Path.Combine(projectDir, relativePath) : projectDir;
        var expectedPath = Path.Combine(expectedDir, Path.GetFileName(filePath));
        if (filePath != expectedPath)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                Message = $"MOVE_REQUIRED: {filePath} -> {expectedPath}"};
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotMove,
            FilePath = filePath,
            Message = "// File is already in the correct folder."
        };
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

    public async Task<List<string>> FindStructuralSmellsAsync(StructuralSmellType typeFilter = StructuralSmellType.All, string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<string>();
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Solution-wide scan: skip test and benchmark projects -> TimeProvider can't be injected
            // into test fixtures, and the findings are noise rather than actionable guidance.
            projects = projects.Where(p => !p.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) && !p.Name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase));
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
                if (root == null)
                {
                    continue;
                }

                // Skip Roslyn source-generator output -> file names are always mismatched and contain generated types
                bool isGeneratedFile = (document.FilePath ?? document.Name).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
                var types = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t is ClassDeclarationSyntax || t is InterfaceDeclarationSyntax || t is RecordDeclarationSyntax || t is StructDeclarationSyntax || t is EnumDeclarationSyntax).ToList();
                if (!isGeneratedFile && (typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.MultiType) && _config.IsFeatureEnabled("MultiTypeFile") && types.Count > 1)
                {
                    results.Add($"[MULTI_TYPE] File '{document.Name}' in project '{project.Name}' contains {types.Count} type declarations.");
                }

                if (!isGeneratedFile && (typeFilter == StructuralSmellType.All || typeFilter == StructuralSmellType.NameMismatch) && _config.IsFeatureEnabled("NameMismatch") && types.Count > 0)
                {
                    // AppHost projects intentionally use Aspire resource-name constants whose file
                    // names don't correspond to class names -> skip to avoid hundreds of false positives.
                    bool isAppHostProject = project.Name.EndsWith(".AppHost", StringComparison.OrdinalIgnoreCase) || project.Name.Contains(".AppHost.", StringComparison.OrdinalIgnoreCase);
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
                        var stringLiterals = root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression));
                        foreach (var literal in stringLiterals)
                        {
                            var value = literal.Token.ValueText;
                            if (string.IsNullOrWhiteSpace(value) || value.Contains(' '))
                            {
                                continue;
                            }

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
                        var throwStmt = ifStmt.Statement is ThrowStatementSyntax t ? t : ifStmt.Statement is BlockSyntax b && b.Statements.Count == 1 && b.Statements[0] is ThrowStatementSyntax t2 ? t2 : null;
                        if (throwStmt != null && throwStmt.Expression is ObjectCreationExpressionSyntax oce)
                        {
                            var type = oce.Type.ToString();
                            bool isLegacy = false;
                            if (type == "ArgumentNullException")
                            {
                                isLegacy = true;
                            }

                            if (type == "ArgumentOutOfRangeException")
                            {
                                isLegacy = true;
                            }

                            if (type == "ObjectDisposedException")
                            {
                                isLegacy = true;
                            }

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

                    var arrayCreations = root.DescendantNodes().OfType<ArrayCreationExpressionSyntax>().Where(a => a.Initializer?.Expressions.Count == 0);
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

                    var semaphores = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => i.Expression.ToString().EndsWith(".Wait") || i.Expression.ToString().EndsWith(".WaitAsync"));
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
                    // Report DateTime.Now/UtcNow/Today in all non-static, non-generated classes.
                    // Static classes genuinely cannot inject TimeProvider, so they're skipped.
                    // Infrastructure-layer classes (repositories, workers, exporters, etc.) are
                    // flagged at LOW severity -> injection is possible but these classes rarely have
                    // date-driven business logic where controlling the clock in a test matters.
                    // Business-logic classes (services, controllers, handlers) are flagged at HIGH
                    // severity because mocking "now" is a real and common test requirement there.
                    static bool IsInfrastructureClass(string name)
                    {
                        string[] infraSuffixes = ["Repository", "Worker", "Job", "Exporter", "Importer", "Processor", "Builder", "Factory", "Mapper", "Converter", "Hub", "Middleware", "Helper", "Extensions", "Serializer", "Deserializer", "Formatter", "Parser", "Writer", "Reader", "Client", "Interceptor", "Decorator"];
                        return infraSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                    }

                    var containingClasses = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => !c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && !isGeneratedFile);
                    foreach (var cls in containingClasses)
                    {
                        bool isInfra = IsInfrastructureClass(cls.Identifier.Text);
                        var timeCalls = cls.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Where(m => m.Expression.ToString() == "DateTime" && m.Name.Identifier.Text is "Now" or "UtcNow" or "Today");
                        foreach (var call in timeCalls)
                        {
                            var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                            if (isInfra)
                            {
                                results.Add($"[TIME_ABSTRACTION:LOW] Direct DateTime.{call.Name.Identifier.Text} in '{document.Name}' (Line {line}). TimeProvider injection is possible but typically low-value - infrastructure classes rarely have date-driven logic that needs to be controlled in tests.");
                            }
                            else
                            {
                                results.Add($"[TIME_ABSTRACTION] Direct DateTime.{call.Name.Identifier.Text} in '{document.Name}' (Line {line}). Consider injecting TimeProvider for better testability.");
                            }
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

    /// <summary>
    /// Extracts all public API members (methods, properties, constructors) from a project or file.
    /// Save the returned list as a JSON baseline to compare against later.
    /// </summary>
    public async Task<List<PublicApiMember>> GetPublicApiSurfaceAsync(string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var results = new List<PublicApiMember>();
        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolNotFoundException($"Project '{projectName}' not found in the solution.");
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        foreach (var doc in documents)
        {
            if (doc == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var docPath = doc.FilePath ?? doc.Name;
            foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (!IsPublicOrProtected(typeDecl.Modifiers))
                {
                    continue;
                }

                var typeName = typeDecl.Identifier.Text;
                var typeLine = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                results.Add(new PublicApiMember("Type", typeName, BuildTypeSignature(typeDecl), docPath, typeLine));
                // TypeDeclarationSyntax covers class/struct/interface/record -> all have Members.
                // EnumDeclarationSyntax is a BaseTypeDeclarationSyntax but has no named callable members.
                if (typeDecl is not TypeDeclarationSyntax typeWithMembers)
                {
                    continue;
                }

                foreach (var member in typeWithMembers.Members)
                {
                    if (!IsPublicOrProtected(GetMemberModifiers(member)))
                    {
                        continue;
                    }

                    var(kind, sig) = GetMemberSignature(member, typeName);
                    if (sig == null)
                    {
                        continue;
                    }

                    var memberLine = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(new PublicApiMember(kind, typeName, sig, docPath, memberLine));
                }
            }
        }

        return results.OrderBy(m => m.ContainingType).ThenBy(m => m.Signature).ToList();
    }

    /// <summary>
    /// Compares the current API surface against a previously captured baseline.
    /// Provide the baseline as a list of PublicApiMember records.
    /// Returns a list of detected breaking changes.
    /// </summary>
    public async Task<List<BreakingChange>> DetectBreakingChangesAsync(List<PublicApiMember> baseline, string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var current = await GetPublicApiSurfaceAsync(projectName, filePath, cancellationToken);
        var changes = new List<BreakingChange>();
        // Index current by signature for fast lookup
        var currentBySignature = current.ToDictionary(m => $"{m.ContainingType}|{m.Signature}", m => m);
        var currentTypes = current.Where(m => m.Kind == "Type").Select(m => m.ContainingType).ToHashSet(StringComparer.Ordinal);
        foreach (var baselineMember in baseline)
        {
            var key = $"{baselineMember.ContainingType}|{baselineMember.Signature}";
            if (currentBySignature.ContainsKey(key))
            {
                continue; // Unchanged - good
            }

            // Member was removed or renamed. Check if the type itself still exists.
            if (baselineMember.Kind == "Type")
            {
                if (!currentTypes.Contains(baselineMember.ContainingType))
                {
                    changes.Add(new BreakingChange("TypeRemoved", $"Public type '{baselineMember.ContainingType}' was removed. All consumers will fail to compile.", baselineMember.Signature, baselineMember.FilePath, baselineMember.Line));
                }
                else
                {
                    changes.Add(new BreakingChange("TypeSignatureChanged", $"Type '{baselineMember.ContainingType}' signature changed from '{baselineMember.Signature}'.", baselineMember.Signature, baselineMember.FilePath, baselineMember.Line));
                }
            }
            else
            {
                if (!currentTypes.Contains(baselineMember.ContainingType))
                {
                    // Type was removed -> type-level change already reported
                    continue;
                }

                changes.Add(new BreakingChange("MemberRemovedOrRenamed", $"{baselineMember.Kind} '{baselineMember.Signature}' in '{baselineMember.ContainingType}' was removed or its signature changed. Callers will fail to compile.", $"{baselineMember.ContainingType}.{baselineMember.Signature}", baselineMember.FilePath, baselineMember.Line));
            }
        }

        // Also flag newly internal/private members (accessibility reduction is breaking)
        var baselineSignatures = baseline.Select(m => $"{m.ContainingType}|{m.Signature}").ToHashSet();
        // (Access reduction can't be detected from signature alone without semantic model -> covered by summary message)
        return changes.OrderBy(c => c.ChangeKind).ThenBy(c => c.AffectedMember).ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static bool IsPublicOrProtected(SyntaxTokenList modifiers) => modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword));
    private static SyntaxTokenList GetMemberModifiers(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Modifiers,
        PropertyDeclarationSyntax p => p.Modifiers,
        ConstructorDeclarationSyntax c => c.Modifiers,
        FieldDeclarationSyntax f => f.Modifiers,
        EventDeclarationSyntax e => e.Modifiers,
        EventFieldDeclarationSyntax ef => ef.Modifiers,
        _ => default
    };
    private static string BuildTypeSignature(BaseTypeDeclarationSyntax typeDecl)
    {
        var keyword = typeDecl switch
        {
            ClassDeclarationSyntax => "class",
            InterfaceDeclarationSyntax => "interface",
            StructDeclarationSyntax => "struct",
            RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
            EnumDeclarationSyntax => "enum",
            _ => "type"
        };
        var bases = typeDecl.BaseList?.Types.Count > 0 ? " : " + string.Join(", ", typeDecl.BaseList.Types.Select(t => t.ToString())) : "";
        return $"{keyword} {typeDecl.Identifier.Text}{bases}";
    }

    private static (string Kind, string? Signature) GetMemberSignature(MemberDeclarationSyntax member, string typeName) => member switch
    {
        MethodDeclarationSyntax m => ("Method", $"{m.ReturnType} {m.Identifier.Text}{m.TypeParameterList}{m.ParameterList}"),
        ConstructorDeclarationSyntax c => ("Constructor", $"{typeName}{c.ParameterList}"),
        PropertyDeclarationSyntax p => ("Property", $"{p.Type} {p.Identifier.Text} {{ {(p.AccessorList?.Accessors.Any(a => a.Keyword.IsKind(SyntaxKind.GetKeyword)) == true ? "get; " : "")}{(p.AccessorList?.Accessors.Any(a => a.Keyword.IsKind(SyntaxKind.SetKeyword) || a.Keyword.IsKind(SyntaxKind.InitKeyword)) == true ? "set; " : "")}}}"),
        FieldDeclarationSyntax f => ("Field", $"{f.Declaration.Type} {string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text))}"),
        EventDeclarationSyntax e => ("Event", $"event {e.Type} {e.Identifier.Text}"),
        EventFieldDeclarationSyntax ef => ("Event", $"event {ef.Declaration.Type} {string.Join(", ", ef.Declaration.Variables.Select(v => v.Identifier.Text))}"),
        _ => ("Member", null)};
    /// <summary>
    /// Converts a class into a .NET BackgroundService.
    /// </summary>
    public async Task<DocumentEditResult> ConvertToBackgroundServiceAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax ?? throw new InvalidOperationException("Could not parse syntax root.");
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className) ?? throw new InvalidOperationException("Class not found.");
        // 1. Add using
        if (!root.Usings.Any(u => u.Name?.ToString() == "Microsoft.Extensions.Hosting"))
        {
            root = root.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Microsoft.Extensions.Hosting")));
            // Re-find class node after root modification (stale reference otherwise)
            classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className) ?? throw new InvalidOperationException("Class not found after root modification.");
        }

        // 2. Change base class
        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("BackgroundService"));
        var newClass = classNode.WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType)));
        // 3. Add ExecuteAsync override
        var executeAsync = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName("Task"), "ExecuteAsync").AddModifiers(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword), SyntaxFactory.Token(SyntaxKind.AsyncKeyword)).AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("stoppingToken")).WithType(SyntaxFactory.ParseTypeName("CancellationToken"))).WithBody(SyntaxFactory.Block(SyntaxFactory.WhileStatement(SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("stoppingToken"), SyntaxFactory.IdentifierName("IsCancellationRequested"))), SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression("await Task.Delay(1000, stoppingToken)"))))));
        newClass = newClass.AddMembers(executeAsync);
        var newRoot = root.ReplaceNode(classNode, newClass);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
    }

    public async Task<List<CircularDependencyChain>> FindCircularDependenciesAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name == projectName);
        }

        // Pass 1: Collect all named types defined in the solution
        var allSymbols = new Dictionary<string, (INamedTypeSymbol Symbol, string? FilePath)>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);
                foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken)is not INamedTypeSymbol symbol)
                    {
                        continue;
                    }

                    if (symbol.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    if (symbol.IsGenericType && !symbol.IsDefinition)
                    {
                        continue;
                    }

                    var typeKey = symbol.ToDisplayString();
                    if (!allSymbols.ContainsKey(typeKey))
                    {
                        var filePath = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;
                        allSymbols[typeKey] = (symbol, filePath);
                    }
                }
            }
        }

        // Pass 2: Build dependency graph restricted to solution types
        var graph = new Dictionary<string, HashSet<string>>();
        foreach (var key in allSymbols.Keys)
        {
            graph[key] = new HashSet<string>();
        }

        foreach (var(key, (symbol, _))in allSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var dep in GetReferencedNamedTypes(symbol))
            {
                var depKey = dep.ToDisplayString();
                if (allSymbols.ContainsKey(depKey) && depKey != key)
                {
                    graph[key].Add(depKey);
                }
            }
        }

        // Pass 3: Find SCCs via Tarjan's algorithm
        var sccs = ComputeTarjanSCCs(graph);
        // Pass 4: Extract representative cycle from each SCC with size > 1
        var results = new List<CircularDependencyChain>();
        var seen = new HashSet<string>();
        foreach (var scc in sccs.Where(s => s.Count > 1))
        {
            var cycle = FindRepresentativeCycle(scc, graph);
            if (cycle == null || cycle.Count < 3)
            {
                continue;
            }

            // Canonicalize: rotate so the lexicographically smallest name is first
            var nodes = cycle.Take(cycle.Count - 1).ToList();
            var minIdx = 0;
            for (var i = 1; i < nodes.Count; i++)
            {
                if (string.Compare(nodes[i], nodes[minIdx], StringComparison.Ordinal) < 0)
                {
                    minIdx = i;
                }
            }

            var rotated = new List<string>();
            for (var i = 0; i < nodes.Count; i++)
            {
                rotated.Add(nodes[(i + minIdx) % nodes.Count]);
            }

            rotated.Add(rotated[0]);
            var canonicalKey = string.Join("->", rotated);
            if (!seen.Add(canonicalKey))
            {
                continue;
            }

            var filePaths = rotated.Select(t => allSymbols.TryGetValue(t, out var info) ? info.FilePath : null).ToList();
            var cycleType = rotated.Count == 3 ? "Direct" : "Transitive";
            results.Add(new CircularDependencyChain(rotated, cycleType, filePaths));
        }

        return results;
    }

    private static IEnumerable<INamedTypeSymbol> GetReferencedNamedTypes(INamedTypeSymbol type)
    {
        if (type.BaseType != null && type.BaseType.SpecialType == SpecialType.None)
        {
            foreach (var t in UnwrapNamedTypes(type.BaseType))
            {
                yield return t;
            }
        }

        foreach (var iface in type.Interfaces)
        {
            foreach (var t in UnwrapNamedTypes(iface))
            {
                yield return t;
            }
        }

        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field when field.AssociatedSymbol == null:
                    foreach (var t in UnwrapNamedTypes(field.Type))
                    {
                        yield return t;
                    }

                    break;
                case IPropertySymbol prop:
                    foreach (var t in UnwrapNamedTypes(prop.Type))
                    {
                        yield return t;
                    }

                    break;
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary || method.MethodKind == MethodKind.Constructor:
                    foreach (var t in UnwrapNamedTypes(method.ReturnType))
                    {
                        yield return t;
                    }

                    foreach (var param in method.Parameters)
                    {
                        foreach (var t in UnwrapNamedTypes(param.Type))
                        {
                            yield return t;
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> UnwrapNamedTypes(ITypeSymbol typeSymbol)
    {
        switch (typeSymbol)
        {
            case INamedTypeSymbol named:
                yield return named.OriginalDefinition;
                foreach (var arg in named.TypeArguments)
                {
                    foreach (var t in UnwrapNamedTypes(arg))
                    {
                        yield return t;
                    }
                }

                break;
            case IArrayTypeSymbol array:
                foreach (var t in UnwrapNamedTypes(array.ElementType))
                {
                    yield return t;
                }

                break;
        }
    }

    private static List<List<string>> ComputeTarjanSCCs(Dictionary<string, HashSet<string>> graph)
    {
        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var indices = new Dictionary<string, int>();
        var lowLinks = new Dictionary<string, int>();
        var sccs = new List<List<string>>();
        void StrongConnect(string v)
        {
            indices[v] = lowLinks[v] = index++;
            stack.Push(v);
            onStack.Add(v);
            foreach (var w in graph[v])
            {
                if (!indices.TryGetValue(w, out int value))
                {
                    StrongConnect(w);
                    lowLinks[v] = Math.Min(lowLinks[v], lowLinks[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowLinks[v] = Math.Min(lowLinks[v], value);
                }
            }

            if (lowLinks[v] == indices[v])
            {
                var scc = new List<string>();
                string w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    scc.Add(w);
                }
                while (w != v);
                sccs.Add(scc);
            }
        }

        foreach (var v in graph.Keys)
        {
            if (!indices.ContainsKey(v))
            {
                StrongConnect(v);
            }
        }

        return sccs;
    }

    private static List<string>? FindRepresentativeCycle(List<string> scc, Dictionary<string, HashSet<string>> graph)
    {
        var sccSet = new HashSet<string>(scc);
        List<string>? shortest = null;
        foreach (var start in scc)
        {
            var cycle = FindCycleFromStart(start, sccSet, graph);
            if (cycle != null && (shortest == null || cycle.Count < shortest.Count))
            {
                shortest = cycle;
                if (shortest.Count == 3)
                {
                    break;
                }
            }
        }

        return shortest;
    }

    private static List<string>? FindCycleFromStart(string start, HashSet<string> sccSet, Dictionary<string, HashSet<string>> graph)
    {
        var path = new List<string>
        {
            start
        };
        var pathSet = new HashSet<string>
        {
            start
        };
        bool Dfs(string current)
        {
            if (!graph.TryGetValue(current, out var neighbors))
            {
                return false;
            }

            foreach (var next in neighbors)
            {
                if (!sccSet.Contains(next))
                {
                    continue;
                }

                if (next == start)
                {
                    path.Add(start);
                    return true;
                }

                if (pathSet.Contains(next))
                {
                    continue;
                }

                path.Add(next);
                pathSet.Add(next);
                if (Dfs(next))
                {
                    return true;
                }

                path.RemoveAt(path.Count - 1);
                pathSet.Remove(next);
            }

            return false;
        }

        return Dfs(start) ? path : null;
    }

    // ── Layer Architecture Enforcement ───────────────────────────────────────
    public record LayerViolation(string ViolationType, string Description, string SourceLayer, string ForbiddenDependency, FilePathWrapper FilePath, int Line);
    // Standard layered architecture rule set (namespace segment -> layer rank, lower = higher-level)
    private static readonly Dictionary<string, int> LayerRank = new(StringComparer.OrdinalIgnoreCase)
    {
        {
            "Controllers",
            0
        },
        {
            "Endpoints",
            0
        },
        {
            "Hubs",
            0
        },
        {
            "Workers",
            1
        },
        {
            "Services",
            2
        },
        {
            "Managers",
            2
        },
        {
            "Handlers",
            2
        },
        {
            "Queries",
            3
        },
        {
            "Commands",
            3
        },
        {
            "Domain",
            4
        },
        {
            "Models",
            4
        },
        {
            "Entities",
            4
        },
        {
            "Data",
            5
        },
        {
            "Repositories",
            5
        },
        {
            "Migrations",
            6
        },
    };
    // Rules: a layer at rank R should NOT directly reference a layer at rank > R+1 (skip-a-layer)
    // and Controllers should never reference Data/Repositories directly.
    private static readonly Dictionary<string, HashSet<string>> ForbiddenDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Controllers"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Endpoints"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Hubs"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Workers"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Repositories",
            "Migrations"
        },
        ["Services"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Migrations"
        },
        ["Managers"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Migrations"
        },
        ["Domain"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Models"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
        ["Entities"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "Data",
            "Repositories",
            "Migrations"
        },
    };
    /// <summary>
    /// Detects namespace-level layer violations (e.g. Controllers referencing Repositories directly,
    /// domain models importing data-layer types). Operates on using directives -> no compilation needed.
    /// </summary>
    public async Task<List<LayerViolation>> DetectLayerViolationsAsync(string? projectName = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var violations = new List<LayerViolation>();
        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        foreach (var doc in documents)
        {
            if (doc == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var docPath = doc.FilePath ?? doc.Name;
            // Determine which layer this file belongs to from its own namespace
            var ownNamespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
            var ownLayer = GetLayerSegment(ownNamespace);
            if (ownLayer == null)
            {
                continue;
            }

            if (!ForbiddenDependencies.TryGetValue(ownLayer, out var forbidden))
            {
                continue;
            }

            // Scan all using directives in this file
            var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>();
            foreach (var usingDirective in usings)
            {
                var importedNs = usingDirective.Name?.ToString() ?? "";
                var importedLayer = GetLayerSegment(importedNs);
                if (importedLayer == null)
                {
                    continue;
                }

                if (!forbidden.Contains(importedLayer))
                {
                    continue;
                }

                var line = usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                violations.Add(new LayerViolation("LayerBypass", $"'{ownLayer}' layer directly references '{importedLayer}' layer ({importedNs}). " + $"Route through an intermediate service/repository interface instead.", ownLayer, importedNs, docPath, line));
            }
        }

        return violations.OrderBy(v => v.FilePath).ThenBy(v => v.Line).ToList();
    }

    private static string? GetLayerSegment(string namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName))
        {
            return null;
        }

        var segments = namespaceName.Split('.');
        return segments.FirstOrDefault(s => LayerRank.ContainsKey(s));
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

    /// <summary>
    /// Detects circular type dependencies at the class level by analysing constructor parameters.
    /// Unlike FindCircularDependenciesAsync (which checks project references), this method finds
    /// cycles in composition: ClassA's ctor takes ClassB, ClassB's ctor takes ClassA.
    /// Such cycles cause runtime DI failures without a compile error.
    /// </summary>
    public async Task<List<string>> FindCircularTypeReferencesAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, true, cancellationToken);
        // Build map: simpleName -> set of constructor-parameter simple names
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (target.SemanticModel == null)
            {
                continue;
            }

            foreach (var classDecl in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var className = classDecl.Identifier.Text;
                if (!deps.TryGetValue(className, out HashSet<string>? value))
                {
                    value = new HashSet<string>(StringComparer.Ordinal);
                    deps[className] = value;
                }

                var ctors = classDecl.Members.OfType<ConstructorDeclarationSyntax>();
                foreach (var ctor in ctors)
                {
                    foreach (var param in ctor.ParameterList.Parameters)
                    {
                        if (param.Type == null)
                        {
                            continue;
                        }

                        var typeSymbol = target.SemanticModel.GetTypeInfo(param.Type, cancellationToken).Type;
                        if (typeSymbol == null || typeSymbol.TypeKind == TypeKind.Error)
                        {
                            continue;
                        }

                        // Only track types that are declared in this solution (user types)
                        if (typeSymbol.DeclaringSyntaxReferences.Length == 0)
                        {
                            continue;
                        }

                        value.Add(typeSymbol.Name);
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

    private static void DetectTypeCycle(string current, Dictionary<string, HashSet<string>> deps, HashSet<string> visited, List<string> path, List<string> results, HashSet<string> reportedCycles)
    {
        if (!deps.TryGetValue(current, out HashSet<string>? value))
        {
            return;
        }

        if (path.Contains(current))
        {
            // Found a cycle -> normalise the cycle key so A->B->A and B->A->B produce one report
            var cycleStart = path.IndexOf(current);
            var cycle = path.Skip(cycleStart).Concat(new[] { current }).ToList();
            var key = string.Join("->", cycle.OrderBy(x => x));
            if (reportedCycles.Add(key))
            {
                results.Add($"Circular type dependency: {string.Join(" -> ", cycle)}");
            }

            return;
        }

        if (visited.Contains(current))
        {
            return;
        }

        visited.Add(current);
        path.Add(current);
        foreach (var dep in value)
        {
            DetectTypeCycle(dep, deps, visited, path, results, reportedCycles);
        }

        path.RemoveAt(path.Count - 1);
    }

    // ── Namespace / path mismatch detection ───────────────────────────────────
    public async Task<NamespacePathMismatchReport> FindNamespacePathMismatchesAsync(Solution solution, string? projectName, CancellationToken cancellationToken = default)
    {
        var errors = new List<NamespacePathMismatch>();
        var warnings = new List<NamespacePathMismatch>();
        int totalFiles = 0;
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        // Build a cross-project lookup: fully-qualified-type-name -> list of file paths
        // Used to detect duplicate type names across mismatched paths (ErrorData severity).
        var typeToFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // First pass: collect (document, declaredNamespace, expectedNamespace) for all relevant docs.
        var findings = new List<(Document Doc, string? DeclaredNs, string ExpectedNs, List<string> TypeNames, string ProjectName)>();
        foreach (var project in projects)
        {
            var rootNamespace = GetRootNamespace(project);
            var projectRoot = project.FilePath is not null ? Path.GetDirectoryName(project.FilePath) : null;
            foreach (var doc in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filePath = doc.FilePath;
                if (filePath is null)
                {
                    continue;
                }

                // Skip generated files.
                var fileName = Path.GetFileName(filePath);
                if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                totalFiles++;
                var syntaxRoot = await doc.GetSyntaxRootAsync(cancellationToken);
                if (syntaxRoot is null)
                {
                    continue;
                }

                // Extract all namespace declarations (both block and file-scoped).
                var nsDecls = syntaxRoot.DescendantNodes().Where(n => n is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax).ToList();
                // Extract top-level type names declared in this file (for duplicate detection).
                var typeNames = syntaxRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t.Parent is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax or CompilationUnitSyntax).Select(t => t.Identifier.Text).ToList();
                string? declaredNs;
                if (nsDecls.Count == 0)
                {
                    declaredNs = null; // global namespace
                }
                else if (nsDecls.Count == 1)
                {
                    declaredNs = nsDecls[0] switch
                    {
                        NamespaceDeclarationSyntax ns => ns.Name.ToString().Trim(),
                        FileScopedNamespaceDeclarationSyntax fns => fns.Name.ToString().Trim(),
                        _ => null
                    };
                }
                else
                {
                    // Multiple namespace declarations -> report as a warning, use first for path check.
                    declaredNs = nsDecls[0] switch
                    {
                        NamespaceDeclarationSyntax ns => ns.Name.ToString().Trim(),
                        FileScopedNamespaceDeclarationSyntax fns => fns.Name.ToString().Trim(),
                        _ => null
                    };
                    var expectedNsMulti = projectRoot is not null ? DeriveExpectedNamespace(filePath, projectRoot, rootNamespace) : rootNamespace;
                    warnings.Add(new NamespacePathMismatch { FilePath = filePath, ProjectName = project.Name, DeclaredNamespace = declaredNs ?? "", ExpectedNamespace = expectedNsMulti, Severity = "Warning", Reason = "MultipleNamespacesInFile", ConflictingFiles = [] });
                    continue;
                }

                var expectedNs = projectRoot is not null ? DeriveExpectedNamespace(filePath, projectRoot, rootNamespace) : rootNamespace;
                // Track type -> file for duplicate detection.
                foreach (var typeName in typeNames)
                {
                    var fqn = $"{declaredNs ?? ""}.{typeName}";
                    if (!typeToFiles.TryGetValue(fqn, out var list))
                    {
                        typeToFiles[fqn] = list = [];
                    }

                    list.Add(filePath);
                }

                findings.Add((doc, declaredNs, expectedNs, typeNames, project.Name));
            }
        }

        // Second pass: classify each finding.
        foreach (var(doc, declaredNs, expectedNs, typeNames, projName)in findings)
        {
            var filePath = doc.FilePath!;
            if (declaredNs is null)
            {
                // No namespace declaration -> global namespace is unexpected when a root namespace exists.
                if (!string.IsNullOrEmpty(expectedNs))
                {
                    warnings.Add(new NamespacePathMismatch { FilePath = filePath, ProjectName = projName, DeclaredNamespace = "", ExpectedNamespace = expectedNs, Severity = "Warning", Reason = "GlobalNamespace", ConflictingFiles = [] });
                }

                continue;
            }

            if (string.Equals(declaredNs, expectedNs, StringComparison.OrdinalIgnoreCase))
            {
                continue; // Clean - matches.
            }

            // Mismatch detected. Check for duplicate type names at the conflicting path.
            var conflicting = new List<string>();
            foreach (var typeName in typeNames)
            {
                var fqn = $"{expectedNs}.{typeName}";
                if (typeToFiles.TryGetValue(fqn, out var otherFiles))
                {
                    conflicting.AddRange(otherFiles.Where(f => !f.Equals(filePath, StringComparison.OrdinalIgnoreCase)));
                }
            }

            if (conflicting.Count > 0)
            {
                errors.Add(new NamespacePathMismatch { FilePath = filePath, ProjectName = projName, DeclaredNamespace = declaredNs, ExpectedNamespace = expectedNs, Severity = "Error", Reason = "DuplicateTypeAtMismatchedPath", ConflictingFiles = conflicting.Distinct().ToList() });
            }
            else
            {
                warnings.Add(new NamespacePathMismatch { FilePath = filePath, ProjectName = projName, DeclaredNamespace = declaredNs, ExpectedNamespace = expectedNs, Severity = "Warning", Reason = "NamespaceFolderMismatch", ConflictingFiles = [] });
            }
        }

        // Partial-class-across-namespaces detection.
        // Find partial type names declared in more than one namespace.
        var partialTypeToNamespaces = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var(doc, declaredNs, _, typeNames, _)in findings)
        {
            if (declaredNs is null)
            {
                continue;
            }

            var syntaxRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            if (syntaxRoot is null)
            {
                continue;
            }

            var partials = syntaxRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))).Select(t => t.Identifier.Text);
            foreach (var name in partials)
            {
                if (!partialTypeToNamespaces.TryGetValue(name, out var nss))
                {
                    partialTypeToNamespaces[name] = nss = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                nss.Add(declaredNs);
            }
        }

        foreach (var(typeName, namespaces)in partialTypeToNamespaces)
        {
            if (namespaces.Count > 1)
            {
                // Find the files that contain this partial type.
                var involvedFiles = findings.Where(f => f.TypeNames.Contains(typeName, StringComparer.OrdinalIgnoreCase)).Select(f => f.Doc.FilePath!).ToList();
                // Only report once per type (use first file).
                if (involvedFiles.Count > 0)
                {
                    var first = involvedFiles[0];
                    // Avoid double-reporting a file already in errors/warnings.
                    if (!errors.Any(e => e.FilePath == first) && !warnings.Any(w => w.FilePath == first))
                    {
                        warnings.Add(new NamespacePathMismatch { FilePath = first, ProjectName = findings.First(f => f.Doc.FilePath == first).ProjectName, DeclaredNamespace = string.Join(", ", namespaces), ExpectedNamespace = "", Severity = "Warning", Reason = "PartialClassAcrossNamespaces", ConflictingFiles = involvedFiles.Skip(1).ToList() });
                    }
                }
            }
        }

        int mismatchCount = errors.Count + warnings.Count;
        string summary = mismatchCount == 0 ? $"Clean: all {totalFiles} file(s) have matching namespaces and folder paths." : $"{mismatchCount} mismatch(es) found in {totalFiles} file(s): " + $"{errors.Count} error(s), {warnings.Count} warning(s).";
        return new NamespacePathMismatchReport
        {
            Errors = errors,
            Warnings = warnings,
            TotalFiles = totalFiles,
            MismatchCount = mismatchCount,
            IsClean = mismatchCount == 0,
            Summary = summary
        };
    }

    /// <summary>
    /// Derives the expected namespace for a file based on its path relative to the project root.
    /// </summary>
    private static string DeriveExpectedNamespace(FilePathWrapper filePath, string projectRoot, string rootNamespace)
    {
        // Get the directory containing the file, relative to the project root.
        var fileDir = Path.GetDirectoryName(filePath) ?? "";
        var relativeDir = Path.GetRelativePath(projectRoot, fileDir);
        if (relativeDir == ".")
        {
            return rootNamespace;
        }

        // Convert directory separators to namespace separators, strip leading dots.
        var nsPart = relativeDir.Replace('\\', '.').Replace('/', '.').Trim('.');
        return string.IsNullOrEmpty(nsPart) ? rootNamespace : $"{rootNamespace}.{nsPart}";
    }

    /// <summary>
    /// Reads the <c><RootNamespace></c> MSBuild property from the project file,
    /// falling back to the project name (the .csproj stem).
    /// </summary>
    private static string GetRootNamespace(Project project)
    {
        // Try to read <RootNamespace> from the .csproj XML directly -> works for both
        // SDK-style and legacy project formats, and doesn't require a compilation.
        if (project.FilePath is not null)
        {
            try
            {
                var xdoc = System.Xml.Linq.XDocument.Load(project.FilePath);
                var ns = xdoc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "RootNamespace", StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrWhiteSpace(ns))
                {
                    return ns.Trim();
                }
            }
            catch
            { /* best effort - fall through to project name */
            }
        }

        return project.Name;
    }
}