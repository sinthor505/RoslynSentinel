using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record ThrowSiteInfo(
    string FilePath,
    int Line,
    int Column,
    string ExceptionType,
    string ContainingMethod,
    bool IsInCatch,
    string? MessageLiteral);

public record ObjectCreationSite(
    string FilePath,
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
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<ThrowSiteInfo>();

        foreach (var doc in GetDocuments(solution, filePath, projectName))
        {
            if (doc.FilePath == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            var path = doc.FilePath;

            foreach (var throwStmt in root.DescendantNodes().OfType<ThrowStatementSyntax>())
            {
                // Skip bare re-throws (expression is null)
                if (throwStmt.Expression == null) continue;

                var info = BuildThrowSiteInfo(throwStmt, throwStmt.Expression, path);
                if (info == null) continue;

                if (exceptionType != null && !info.ExceptionType.Contains(exceptionType, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(info);
            }

            foreach (var throwExpr in root.DescendantNodes().OfType<ThrowExpressionSyntax>())
            {
                var info = BuildThrowSiteInfo(throwExpr, throwExpr.Expression, path);
                if (info == null) continue;

                if (exceptionType != null && !info.ExceptionType.Contains(exceptionType, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(info);
            }
        }

        return results;
    }

    private static ThrowSiteInfo? BuildThrowSiteInfo(SyntaxNode throwNode, ExpressionSyntax? expression, string path)
    {
        if (expression == null) return null;

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
                            sb.Append(text.TextToken.ValueText);
                        else
                            break;
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
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<ObjectCreationSite>();

        foreach (var doc in GetDocuments(solution, filePath, projectName))
        {
            if (doc.FilePath == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            var path = doc.FilePath;

            foreach (var objCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var createdTypeName = objCreation.Type.ToString();
                var simpleName = createdTypeName.Split('.').Last().Split('<')[0];

                if (!simpleName.Contains(typeName, StringComparison.OrdinalIgnoreCase) &&
                    !createdTypeName.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                    continue;

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
                if (inferredType == null) continue;

                var simpleName = inferredType.Split('.').Last().Split('<')[0];
                if (!simpleName.Contains(typeName, StringComparison.OrdinalIgnoreCase) &&
                    !inferredType.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                    continue;

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
        if (project == null) return results;

        foreach (var doc in project.Documents)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) continue;

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
                    if (!isPublic && !isProtected) continue;

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
            return solution.GetDocumentIdsWithFilePath(filePath)
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
                return method.Identifier.Text;
            if (ancestor is ConstructorDeclarationSyntax ctor)
                return ctor.Identifier.Text;
            if (ancestor is PropertyDeclarationSyntax prop)
                return prop.Identifier.Text;
        }
        return "<unknown>";
    }

    private static string? ExtractXmlDocSummary(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));

        if (trivia == default) return null;

        var xmlDoc = trivia.ToString();
        var startIdx = xmlDoc.IndexOf("<summary>", StringComparison.Ordinal);
        var endIdx = xmlDoc.IndexOf("</summary>", StringComparison.Ordinal);

        if (startIdx < 0 || endIdx < 0) return null;

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
                return typeSyntax.ToString();
        }

        // x = new(...) — check AssignmentExpression left side
        if (node.Parent is AssignmentExpressionSyntax assignment)
            return assignment.Left.ToString();

        return null;
    }
}
