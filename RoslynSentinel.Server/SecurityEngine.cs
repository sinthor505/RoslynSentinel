using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record SecurityIssueReport(string FilePath, int Line, int Column, string IssueType, string Description);

public class SecurityEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SecurityEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    private static readonly string[] SecretNamePatterns =
    [
        "password", "passwd", "secret", "apikey", "api_key", "token",
        "connectionstring", "privatekey", "authtoken", "clientsecret",
        "accesskey", "credential", "passphrase", "apisecret"
    ];

    public async Task<List<SecurityIssueReport>> AnalyzeSecurityAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<SecurityIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SecurityIssueReport>();

        var reports = new List<SecurityIssueReport>();

        // 1. Hardcoded secrets: identifier name looks like a credential + assigned a non-trivial string literal
        var declarators = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => SecretNamePatterns.Any(p => v.Identifier.Text.ToLowerInvariant().Contains(p))
                && v.Initializer?.Value is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.StringLiteralExpression)
                && lit.Token.ValueText.Length > 3);

        foreach (var v in declarators)
        {
            var loc = v.GetLocation().GetLineSpan().StartLinePosition;
            reports.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                "HardcodedSecret",
                $"'{v.Identifier.Text}' appears to be a hardcoded secret. Use configuration, environment variables, or a secret store."));
        }

        // 2. Weak hash algorithms: MD5 or SHA1 usage
        var memberAccesses = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(ma => ma.Expression.ToString() is "MD5" or "SHA1"
                || ma.Expression.ToString().EndsWith(".MD5", StringComparison.Ordinal)
                || ma.Expression.ToString().EndsWith(".SHA1", StringComparison.Ordinal));

        foreach (var ma in memberAccesses)
        {
            var loc = ma.GetLocation().GetLineSpan().StartLinePosition;
            reports.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                "WeakHashAlgorithm",
                $"Weak hash algorithm ({ma.Expression}) detected. Use SHA256 or SHA512."));
        }

        // 3. new Random() in a context whose name suggests security use
        var randomCreations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(oc => oc.Type.ToString() == "Random");

        foreach (var r in randomCreations)
        {
            var ancestor = r.Ancestors()
                .FirstOrDefault(a => a is MethodDeclarationSyntax or VariableDeclaratorSyntax or PropertyDeclarationSyntax);
            string contextName = ancestor switch
            {
                MethodDeclarationSyntax m => m.Identifier.Text,
                VariableDeclaratorSyntax v => v.Identifier.Text,
                PropertyDeclarationSyntax p => p.Identifier.Text,
                _ => string.Empty
            };
            if (SecretNamePatterns.Any(p => contextName.ToLowerInvariant().Contains(p)))
            {
                var loc = r.GetLocation().GetLineSpan().StartLinePosition;
                reports.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "InsecureRandom",
                    $"'new Random()' in security-sensitive context '{contextName}'. Use 'RandomNumberGenerator' for cryptographic randomness."));
            }
        }

        return reports;
    }

    public async Task<List<SecurityIssueReport>> FindHardcodedPathsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<SecurityIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SecurityIssueReport>();

        var reports = new List<SecurityIssueReport>();
        var strings = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression));

        foreach (var str in strings)
        {
            var text = str.Token.ValueText;
            if (text.Contains(@":\") || text.Contains(@"/") || text.StartsWith(@"\\"))
            {
                 var loc = str.GetLocation().GetLineSpan().StartLinePosition;
                 reports.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1, "HardcodedPath", "Avoid hardcoding file system paths. Use configuration or environment variables."));
            }
        }
        return reports;
    }

    public async Task<List<SecurityIssueReport>> CheckForSqlInjectionAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<SecurityIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SecurityIssueReport>();

        // Get semantic model to check if interpolation expressions are compile-time constants
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        var reports = new List<SecurityIssueReport>();

        // Method names that take a SQL string as their first argument
        var sqlMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ExecuteNonQuery", "ExecuteNonQueryAsync", "ExecuteReader", "ExecuteReaderAsync",
            "ExecuteScalar", "ExecuteScalarAsync", "ExecuteAsync", "Execute",
            "FromSqlRaw", "SqlQuery", "ExecuteSql", "ExecuteSqlRaw",
            "Query", "QueryAsync", "ExecuteQuery"
        };

        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? methodName = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => null
            };
            if (methodName == null || !sqlMethods.Contains(methodName)) continue;

            foreach (var arg in inv.ArgumentList.Arguments)
            {
                bool suspect = arg.Expression switch
                {
                    InterpolatedStringExpressionSyntax s => HasNonConstInterpolation(s, semanticModel, cancellationToken),
                    BinaryExpressionSyntax b => IsDynamicStringConcat(b),
                    _ => false
                };

                if (suspect)
                {
                    var loc = inv.GetLocation().GetLineSpan().StartLinePosition;
                    reports.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                        "PossibleSqlInjection",
                        $"Possible SQL injection: '{methodName}' called with a dynamic/interpolated string. Use parameterized queries."));
                    break;
                }
            }
        }

        return reports;
    }

    /// <summary>Returns true only if any interpolation in the string is a non-constant (runtime) expression.</summary>
    private static bool HasNonConstInterpolation(InterpolatedStringExpressionSyntax s, SemanticModel? semanticModel, CancellationToken ct)
    {
        foreach (var interp in s.Contents.OfType<InterpolationSyntax>())
        {
            if (semanticModel == null) return true; // no model — assume dynamic
            var constVal = semanticModel.GetConstantValue(interp.Expression, ct);
            if (!constVal.HasValue) return true; // not a compile-time constant → suspect
        }
        return false; // all interpolations are constants — safe
    }

    private static bool IsDynamicStringConcat(BinaryExpressionSyntax bin)
    {
        if (!bin.IsKind(SyntaxKind.AddExpression)) return false;

        bool HasString(ExpressionSyntax e) =>
            e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            || e is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.AddExpression) && (HasString(b.Left) || HasString(b.Right));

        bool HasNonLiteral(ExpressionSyntax e) =>
            e is not LiteralExpressionSyntax
            && (e is not BinaryExpressionSyntax b || !b.IsKind(SyntaxKind.AddExpression)
                || HasNonLiteral(b.Left) || HasNonLiteral(b.Right));

        return HasString(bin) && HasNonLiteral(bin);
    }
}
