using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

public record AttributeUsageSite(
    string AttributeName,
    string TargetKind,
    string TargetName,
    string ContainingType,
    FilePath FilePath,
    int Line);
public record TodoCommentFinding(FilePath FilePath, int Line, string Kind, string Text);
public record RenameImpactPreview(string SymbolName, int TotalReferences, int FilesAffected, bool HasTestReferences, List<string> AffectedFiles);

public record ThrowSiteInfo(
    FilePath FilePath,
    int Line,
    int Column,
    string ExceptionType,
    string ContainingMethod,
    bool IsInCatch,
    string? MessageLiteral);

public record ObjectCreationSite(
    FilePath FilePath,
    int Line,
    int Column,
    string TypeName,
    string ContainingMethod,
    int ArgumentCount);

public record ApiSurfaceEntry(
    string TypeName,
    string MemberName,
    string Signature,
    string Kind,
    bool IsVirtual,
    bool IsAbstract,
    bool IsSealed,
    string? XmlDocSummary);

public class DiscoveryEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public DiscoveryEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Finds all throw sites (throw statements and throw expressions) across the solution,
    /// optionally filtered by exception type, file, or project.
    /// </summary>
    public async Task<List<ThrowSiteInfo>> FindAllThrowSitesAsync(
        string? exceptionType = null,
        string? filePath = null,
        string? projectName = null,
        bool sortByFrequency = false,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<ThrowSiteInfo>();

        foreach (var doc in GetDocuments(solution, filePath, projectName))
        {
            if (doc?.FilePath == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var path = doc.FilePath;

            foreach (var throwStmt in root.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                // Skip bare re-throws (expression is null)
                if (throwStmt.Expression == null)
                {
                    continue;
                }

                var info = BuildThrowSiteInfo(throwStmt, throwStmt.Expression, path);
                if (info == null)
                {
                    continue;
                }

                if (exceptionType != null && !info.ExceptionType.Contains(exceptionType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(info);
            }

            foreach (var throwExpr in root.DescendantNodes().OfType<ThrowExpressionSyntax>())
            {
                var info = BuildThrowSiteInfo(throwExpr, throwExpr.Expression, path);
                if (info == null)
                {
                    continue;
                }

                if (exceptionType != null && !info.ExceptionType.Contains(exceptionType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(info);
            }
        }

        if (sortByFrequency)
        {
            // Rank by exception type frequency (most-thrown type first), then file + line
            var freqMap = results
                .GroupBy(r => r.ExceptionType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            results = results
                .OrderByDescending(r => freqMap[r.ExceptionType])
                .ThenBy(r => r.ExceptionType)
                .ThenBy(r => r.FilePath)
                .ThenBy(r => r.Line)
                .ToList();
        }

        return results;
    }

    private static ThrowSiteInfo? BuildThrowSiteInfo(SyntaxNode throwNode, ExpressionSyntax? expression, string path)
    {
        if (expression == null)
        {
            return null;
        }

        string exType = "Exception";
        string? messageLiteral = null;

        if (expression is ObjectCreationExpressionSyntax objCreation)
        {
            exType = objCreation.Type.ToString();

            var firstArg = objCreation.ArgumentList?.Arguments.FirstOrDefault();
            if (firstArg != null)
            {
                if (firstArg.Expression is LiteralExpressionSyntax lit &&
                    lit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    messageLiteral = lit.Token.ValueText;
                }
                else if (firstArg.Expression is InterpolatedStringExpressionSyntax interp)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var content in interp.Contents)
                    {
                        if (content is InterpolatedStringTextSyntax text)
                        {
                            sb.Append(text.TextToken.ValueText);
                        }
                        else
                        {
                            break;
                        }
                    }
                    messageLiteral = sb.Length > 0 ? sb.ToString() : null;
                }
            }
        }
        else
        {
            exType = expression.ToString();
        }

        var isInCatch = throwNode.Ancestors().OfType<CatchClauseSyntax>().Any();
        var containingMethod = GetContainingMemberName(throwNode);
        var lineSpan = throwNode.GetLocation().GetLineSpan();

        return new ThrowSiteInfo(
            path,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            exType,
            containingMethod,
            isInCatch,
            messageLiteral);
    }

    /// <summary>
    /// Finds all object creation sites (new T(...) and new(...)) for a given type name,
    /// optionally filtered by file or project.
    /// </summary>
    public async Task<List<ObjectCreationSite>> FindObjectCreationSitesAsync(
        string typeName,
        string? filePath = null,
        string? projectName = null,
        bool sortByFrequency = false,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<ObjectCreationSite>();

        foreach (var doc in GetDocuments(solution, filePath, projectName))
        {
            if (doc?.FilePath == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var path = doc.FilePath;

            foreach (var objCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var createdTypeName = objCreation.Type.ToString();
                var simpleName = createdTypeName.Split('.').Last().Split('<')[0];

                if (!simpleName.Contains(typeName, StringComparison.OrdinalIgnoreCase) &&
                    !createdTypeName.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lineSpan = objCreation.GetLocation().GetLineSpan();
                results.Add(new ObjectCreationSite(
                    path,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    createdTypeName,
                    GetContainingMemberName(objCreation),
                    objCreation.ArgumentList?.Arguments.Count ?? 0));
            }

            foreach (var implicitCreation in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
            {
                var inferredType = InferImplicitCreationType(implicitCreation);
                if (inferredType == null)
                {
                    continue;
                }

                var simpleName = inferredType.Split('.').Last().Split('<')[0];
                if (!simpleName.Contains(typeName, StringComparison.OrdinalIgnoreCase) &&
                    !inferredType.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lineSpan = implicitCreation.GetLocation().GetLineSpan();
                results.Add(new ObjectCreationSite(
                    path,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    inferredType,
                    GetContainingMemberName(implicitCreation),
                    implicitCreation.ArgumentList?.Arguments.Count ?? 0));
            }
        }

        if (sortByFrequency)
        {
            // When searching across the whole solution, rank by how many times each
            // distinct resolved type name appears — most-instantiated type first
            var freqMap = results
                .GroupBy(r => r.TypeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            results = results
                .OrderByDescending(r => freqMap[r.TypeName])
                .ThenBy(r => r.TypeName)
                .ThenBy(r => r.FilePath)
                .ThenBy(r => r.Line)
                .ToList();
        }

        return results;
    }

    /// <summary>
    /// Returns the complete public API surface of a project: all public/protected types,
    /// methods, and properties with metadata and XML doc summaries.
    /// </summary>
    public async Task<List<ApiSurfaceEntry>> GetPublicApiSurfaceAsync(
        string projectName,
        bool includeMethods = true,
        bool includeProperties = true,
        bool includeTypes = true,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<ApiSurfaceEntry>();

        var project = solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
        if (project == null)
        {
            //return results;
            throw new ArgumentException($"Project '{projectName}' not found in solution.");
        }

        foreach (var doc in project.Documents)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                {
                    continue;
                }

                var typeName = typeDecl.Identifier.Text;
                var isAbstract = typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
                var isSealed = typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.SealedKeyword));
                var typeKind = typeDecl switch
                {
                    InterfaceDeclarationSyntax => "Interface",
                    RecordDeclarationSyntax => "Record",
                    StructDeclarationSyntax => "Struct",
                    _ => "Class"
                };

                if (includeTypes)
                {
                    results.Add(new ApiSurfaceEntry(
                        typeName,
                        typeName,
                        typeDecl.Identifier.Text + (typeDecl.TypeParameterList?.ToString() ?? ""),
                        typeKind,
                        false,
                        isAbstract,
                        isSealed,
                        ExtractXmlDocSummary(typeDecl)));
                }

                foreach (var member in typeDecl.Members)
                {
                    var modifiers = member switch
                    {
                        BaseMethodDeclarationSyntax m => m.Modifiers,
                        BasePropertyDeclarationSyntax p => p.Modifiers,
                        _ => SyntaxFactory.TokenList()
                    };

                    var isPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
                    var isProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
                    if (!isPublic && !isProtected)
                    {
                        continue;
                    }

                    var isVirtual = modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword));
                    var isMemberAbstract = modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
                    var isMemberSealed = modifiers.Any(m => m.IsKind(SyntaxKind.SealedKeyword));
                    var xmlDoc = ExtractXmlDocSummary(member);

                    if (member is MethodDeclarationSyntax method && includeMethods)
                    {
                        var returnType = method.ReturnType.ToString();
                        var name = method.Identifier.Text;
                        var typeParams = method.TypeParameterList?.ToString() ?? "";
                        var paramList = method.ParameterList.ToString();
                        var signature = $"{returnType} {name}{typeParams}{paramList}";

                        results.Add(new ApiSurfaceEntry(typeName, name, signature, "Method",
                            isVirtual, isMemberAbstract, isMemberSealed, xmlDoc));
                    }
                    else if (member is PropertyDeclarationSyntax prop && includeProperties)
                    {
                        var propType = prop.Type.ToString();
                        var name = prop.Identifier.Text;
                        var hasGet = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false;
                        var hasSet = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false;
                        var accessors = (hasGet && hasSet) ? "{ get; set; }" : hasGet ? "{ get; }" : hasSet ? "{ set; }" : "{}";
                        var signature = $"{propType} {name} {accessors}";

                        results.Add(new ApiSurfaceEntry(typeName, name, signature, "Property",
                            isVirtual, isMemberAbstract, isMemberSealed, xmlDoc));
                    }
                    else if (member is ConstructorDeclarationSyntax ctor && includeMethods)
                    {
                        var name = ctor.Identifier.Text;
                        var paramList = ctor.ParameterList.ToString();
                        var signature = $"{name}{paramList}";

                        results.Add(new ApiSurfaceEntry(typeName, name, signature, "Constructor",
                            false, false, false, xmlDoc));
                    }
                }
            }
        }

        return results;
    }

    private static IEnumerable<Document> GetDocuments(Solution solution, string? filePath, string? projectName)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            return solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath))
                .Select(solution.GetDocument)
                .Where(d => d != null)!;
        }

        return solution.Projects
            .Where(p => projectName == null || string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Documents);
    }

    private static string GetContainingMemberName(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is MethodDeclarationSyntax method)
            {
                return method.Identifier.Text;
            }

            if (ancestor is ConstructorDeclarationSyntax ctor)
            {
                return ctor.Identifier.Text;
            }

            if (ancestor is PropertyDeclarationSyntax prop)
            {
                return prop.Identifier.Text;
            }
        }
        return "<unknown>";
    }

    private static string? ExtractXmlDocSummary(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));

        if (trivia == default)
        {
            return null;
        }

        var xmlDoc = trivia.ToString();
        var startIdx = xmlDoc.IndexOf("<summary>", StringComparison.Ordinal);
        var endIdx = xmlDoc.IndexOf("</summary>", StringComparison.Ordinal);

        if (startIdx < 0 || endIdx < 0)
        {
            return null;
        }

        startIdx += "<summary>".Length;
        var raw = xmlDoc.Substring(startIdx, endIdx - startIdx);

        var lines = raw.Split('\n')
            .Select(l => l.TrimStart().TrimStart('/').Trim())
            .Where(l => l.Length > 0);

        return string.Join(" ", lines).Trim();
    }

    private static string? InferImplicitCreationType(ImplicitObjectCreationExpressionSyntax node)
    {
        // var x = new(...) — check EqualsValueClause → VariableDeclaration type
        if (node.Parent is EqualsValueClauseSyntax equals &&
            equals.Parent is VariableDeclaratorSyntax &&
            equals.Parent?.Parent is VariableDeclarationSyntax varDecl)
        {
            var typeSyntax = varDecl.Type;
            if (typeSyntax is not IdentifierNameSyntax { Identifier.Text: "var" })
            {
                return typeSyntax.ToString();
            }
        }

        // x = new(...) — check AssignmentExpression left side
        if (node.Parent is AssignmentExpressionSyntax assignment)
        {
            return assignment.Left.ToString();
        }

        return null;
    }

    public async Task<BestInsertionResult> FindBestInsertionPointAsync(
        FilePath filePath, string containerName, string memberKind, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            throw new InvalidOperationException("Could not get syntax root.");
        }

        var container = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == containerName);
        if (container == null)
        {
            throw new InvalidOperationException($"Type '{containerName}' not found.");
        }

        // Standard C# ordering: fields(0) → constructors(1) → destructors(2) → properties(3) → events(4) → methods(5) → nested(6)
        static int MemberOrder(MemberDeclarationSyntax m) => m switch
        {
            FieldDeclarationSyntax => 0,
            ConstructorDeclarationSyntax => 1,
            DestructorDeclarationSyntax => 2,
            PropertyDeclarationSyntax => 3,
            EventDeclarationSyntax => 4,
            EventFieldDeclarationSyntax => 4,
            MethodDeclarationSyntax => 5,
            TypeDeclarationSyntax => 6,
            _ => 7
        };

        int requestedOrder = memberKind.ToLowerInvariant() switch
        {
            "field" => 0,
            "constructor" => 1,
            "destructor" => 2,
            "property" => 3,
            "event" => 4,
            "method" => 5,
            "nestedtype" => 6,
            _ => throw new InvalidOperationException($"Unknown memberKind '{memberKind}'. Use: field, constructor, destructor, property, event, method, nestedtype")
        };

        var members = container.Members.ToList();
        if (members.Count == 0)
        {
            // Empty type: insert after the opening brace
            var openBrace = container.OpenBraceToken;
            var lineSpan = openBrace.GetLocation().GetLineSpan();
            return new BestInsertionResult(filePath, containerName, memberKind, lineSpan.StartLinePosition.Line + 2, "Empty type — inserting after opening brace");
        }

        // Find last member of same kind
        var sameKindMembers = members.Where(m => MemberOrder(m) == requestedOrder).ToList();
        if (sameKindMembers.Count != 0)
        {
            var last = sameKindMembers.Last();
            var lineSpan = last.GetLocation().GetLineSpan();
            return new BestInsertionResult(filePath, containerName, memberKind, lineSpan.EndLinePosition.Line + 2, $"After last {memberKind}");
        }

        // Find first member of a higher kind
        var higherKindMember = members.FirstOrDefault(m => MemberOrder(m) > requestedOrder);
        if (higherKindMember != null)
        {
            var lineSpan = higherKindMember.GetLocation().GetLineSpan();
            return new BestInsertionResult(filePath, containerName, memberKind, lineSpan.StartLinePosition.Line + 1, $"Before first {higherKindMember.GetType().Name}");
        }

        // All existing members are of a lower kind; insert at the end
        var lastMember = members.Last();
        var lastLineSpan = lastMember.GetLocation().GetLineSpan();
        return new BestInsertionResult(filePath, containerName, memberKind, lastLineSpan.EndLinePosition.Line + 2, "After last member");
    }

    private static readonly string[] TodoKeywords = ["BUG", "FIXME", "HACK", "TODO", "REVIEW", "NOTE"];
    private static readonly Dictionary<string, int> TodoSeverity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BUG"] = 0,
        ["FIXME"] = 1,
        ["HACK"] = 2,
        ["TODO"] = 3,
        ["REVIEW"] = 4,
        ["NOTE"] = 5
    };

    private static bool ContainsKeywordWithWordBoundary(string text, string keyword)
    {
        // Use regex to match whole words only (word boundary check)
        // This prevents "BUG" from matching in "DEBUGGING"
        var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(keyword)}\b";
        return System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public async Task<List<TodoCommentFinding>> FindTodoFixmeCommentsAsync(
        string? filePath = null, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<TodoCommentFinding>();

        foreach (var doc in GetDocuments(solution, filePath, projectName))
        {
            if (doc?.FilePath == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
            {
                foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
                {
                    string? commentText = null;
                    if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    {
                        commentText = trivia.ToFullString();
                    }

                    if (commentText == null)
                    {
                        continue;
                    }

                    var lineSpan = trivia.GetLocation().GetLineSpan();
                    var lineNum = lineSpan.StartLinePosition.Line + 1;

                    foreach (var keyword in TodoKeywords)
                    {
                        if (ContainsKeywordWithWordBoundary(commentText, keyword))
                        {
                            results.Add(new TodoCommentFinding(doc.FilePath, lineNum, keyword.ToUpperInvariant(), commentText.Trim()));
                            break; // Only report one keyword per comment
                        }
                    }
                }
            }
        }

        return results
            .OrderBy(r => TodoSeverity.TryGetValue(r.Kind, out var sev) ? sev : 99)
            .ThenBy(r => r.FilePath)
            .ThenBy(r => r.Line)
            .ToList();
    }

    public async Task<RenameImpactPreview> PreviewRenameImpactAsync(
        FilePath filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var sourceText = await document.GetTextAsync(ct);
        int position;
        if (contextSnippet != null)
        {
            position = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
        }
        else
        {
            // Find first occurrence of symbolName
            position = ContextHelper.FindSnippetPosition(sourceText.ToString(), symbolName);
        }

        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel == null)
        {
            throw new InvalidOperationException("Could not get semantic model.");
        }

        var symbol = semanticModel.GetSymbolInfo(root!.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0)), ct).Symbol
                     ?? semanticModel.GetDeclaredSymbol(root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0)), ct);

        if (symbol == null)
        {
            throw new InvalidOperationException($"Symbol '{symbolName}' not found at the provided location.");
        }

        var references = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(symbol, solution, ct);
        var locations = references.SelectMany(r => r.Locations).ToList();

        var affectedFiles = locations
            .Select(l => l.Location.SourceTree?.FilePath ?? "")
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool hasTestRefs = affectedFiles.Any(f =>
            f.Contains(".Tests", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("Test.", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("Tests.", StringComparison.OrdinalIgnoreCase));

        return new RenameImpactPreview(
            symbolName,
            locations.Count,
            affectedFiles.Count,
            hasTestRefs,
            affectedFiles);
    }

    /// <summary>
    /// Finds all usages of a named attribute across the solution, optionally scoped to a
    /// project or file. Matches both "Foo" and "FooAttribute" spelling variants.
    /// </summary>
    public async Task<List<AttributeUsageSite>> FindAttributeUsagesAsync(
        string attributeName,
        string? projectName = null,
        string? filePath = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        // Normalise: strip leading [ / trailing ] if user typed e.g. "[Authorize]"
        var name = attributeName.Trim('[', ']');

        // Build both canonical forms: "Authorize" and "AuthorizeAttribute"
        var bare = name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^9]
            : name;
        var full = bare + "Attribute";

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var proj = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = proj?.Documents.Cast<Document?>() ?? [];
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        var results = new List<AttributeUsageSite>();

        foreach (var doc in documents)
        {
            if (doc == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var docPath = doc.FilePath ?? doc.Name;

            foreach (var attrList in root.DescendantNodes().OfType<AttributeListSyntax>())
            {
                foreach (var attr in attrList.Attributes)
                {
                    var attrText = attr.Name.ToString().Split('.').Last();
                    if (!string.Equals(attrText, bare, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(attrText, full, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // What is the attribute applied to?
                    var parent = attrList.Parent;
                    var (kind, targetName, containingType) = parent switch
                    {
                        MethodDeclarationSyntax m => ("Method", m.Identifier.Text,
                            m.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? ""),
                        PropertyDeclarationSyntax p => ("Property", p.Identifier.Text,
                            p.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? ""),
                        FieldDeclarationSyntax f => ("Field",
                            f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "",
                            f.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? ""),
                        ClassDeclarationSyntax c => ("Class", c.Identifier.Text, ""),
                        InterfaceDeclarationSyntax i => ("Interface", i.Identifier.Text, ""),
                        RecordDeclarationSyntax r => ("Record", r.Identifier.Text, ""),
                        StructDeclarationSyntax s => ("Struct", s.Identifier.Text, ""),
                        EnumDeclarationSyntax e => ("Enum", e.Identifier.Text, ""),
                        ParameterSyntax param => ("Parameter", param.Identifier.Text,
                            param.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? ""),
                        ConstructorDeclarationSyntax ctor => ("Constructor", ctor.Identifier.Text,
                            ctor.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? ""),
                        _ => ("Unknown", parent?.GetType().Name ?? "", "")
                    };

                    var line = attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(new AttributeUsageSite(bare, kind, targetName, containingType, docPath, line));
                }
            }
        }

        return results
            .OrderBy(r => r.FilePath)
            .ThenBy(r => r.Line)
            .ToList();
    }
}
