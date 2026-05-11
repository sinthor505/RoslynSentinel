using System.Text.RegularExpressions;
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

        // 2. Hardcoded secrets embedded in string VALUES (not just variable names)
        //    Covers connection strings, JWT tokens, API keys the name-pattern check misses.
        foreach (var lit in root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            var val = lit.Token.ValueText;
            if (val.Length < 8) continue;

            string? reason = null;

            // Connection string fragments with embedded passwords
            if (System.Text.RegularExpressions.Regex.IsMatch(val,
                    @"(?i)(password|pwd)\s*=\s*[^;]{3,}") &&
                !val.Contains("@", StringComparison.Ordinal))
                reason = "connection string with embedded password";

            // JWT token (three base64url segments separated by dots)
            else if (val.StartsWith("eyJ", StringComparison.Ordinal) &&
                     val.Count(c => c == '.') >= 2 && val.Length > 50)
                reason = "JWT token";

            // GitHub/OpenAI/Stripe/Anthropic API key formats
            else if (System.Text.RegularExpressions.Regex.IsMatch(val,
                         @"^(sk-live-|sk-test-|sk-ant-|ghp_|gho_|ghs_|glpat-)"))
                reason = "API key";

            // Generic high-entropy bearer token (>30 chars, no spaces, mixed case+digits)
            else if (val.Length > 30 && !val.Contains(' ') &&
                     val.Any(char.IsUpper) && val.Any(char.IsLower) && val.Any(char.IsDigit) &&
                     lit.Ancestors().OfType<VariableDeclaratorSyntax>()
                         .Any(v => SecretNamePatterns.Any(p => v.Identifier.Text.ToLowerInvariant().Contains(p))))
                reason = "high-entropy secret value";

            if (reason != null)
            {
                var loc = lit.GetLocation().GetLineSpan().StartLinePosition;
                reports.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                    "HardcodedSecretValue",
                    $"String literal appears to contain a hardcoded {reason}. Use configuration, environment variables, or a secret store."));
            }
        }

        // 3. Secrets in code comments (single-line and block)
        var triviaSecretPattern = new System.Text.RegularExpressions.Regex(
            @"(?i)(password|secret|apikey|api_key|token|bearer|sk-live-|sk-test-|eyJ[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        foreach (var token in root.DescendantTokens())
        {
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                if (trivia.Kind() is not SyntaxKind.SingleLineCommentTrivia
                    and not SyntaxKind.MultiLineCommentTrivia
                    and not SyntaxKind.SingleLineDocumentationCommentTrivia) continue;

                var commentText = trivia.ToString();
                if (triviaSecretPattern.IsMatch(commentText))
                {
                    var loc = trivia.GetLocation().GetLineSpan().StartLinePosition;
                    reports.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                        "SecretInComment",
                        "Code comment appears to contain a secret or credential. Remove or move to a secure store."));
                }
            }
        }

        // 5. Weak hash algorithms: MD5 or SHA1 usage
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

        // 6. new Random() in a context whose name suggests security use
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
        // Covers ADO.NET, Dapper, EF Core raw SQL, and similar ORMs
        var sqlMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ExecuteNonQuery", "ExecuteNonQueryAsync", "ExecuteReader", "ExecuteReaderAsync",
            "ExecuteScalar", "ExecuteScalarAsync", "ExecuteAsync", "Execute",
            "FromSqlRaw", "SqlQuery", "ExecuteSql", "ExecuteSqlRaw",
            "Query", "QueryAsync", "QuerySingle", "QuerySingleAsync",
            "QueryFirst", "QueryFirstAsync", "QueryMultiple", "QueryMultipleAsync",
            "ExecuteQuery", "ExecuteReader"
        };

        // Collect local string variables assigned an interpolated string — these may be passed to SQL methods
        var interpolatedLocals = new HashSet<string>();
        foreach (var localDecl in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (var variable in localDecl.Declaration.Variables)
            {
                if (variable.Initializer?.Value is InterpolatedStringExpressionSyntax interp &&
                    HasNonConstInterpolation(interp, semanticModel, cancellationToken))
                    interpolatedLocals.Add(variable.Identifier.Text);
            }
        }

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
                    // Variable previously assigned an interpolated string
                    IdentifierNameSyntax id => interpolatedLocals.Contains(id.Identifier.Text),
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

    // ── ReDoS Detection ──────────────────────────────────────────────────────

    // Patterns that signal catastrophic backtracking potential in a regex string.
    // Each pattern is a regex-on-the-regex that matches the dangerous sub-expression.
    private static readonly Regex[] ReDoSSignals =
    [
        new(@"\([^)]*[+*]\)[+*]",         RegexOptions.None, TimeSpan.FromMilliseconds(200)), // (X+)+ or (X*)*
        new(@"\([^)]*[+*][^)]*\|[^)]*\)[+*]", RegexOptions.None, TimeSpan.FromMilliseconds(200)), // (a+|b)+
        new(@"\((\w+\|)+\w+\)[+*]",       RegexOptions.None, TimeSpan.FromMilliseconds(200)), // (a|b|c)+
        new(@"\.[+*]\.[+*]",              RegexOptions.None, TimeSpan.FromMilliseconds(200)), // .*.*
    ];

    /// <summary>
    /// Detects Regex patterns with nested quantifiers or alternation that can cause
    /// catastrophic backtracking on adversarial input (ReDoS).
    /// Checks new Regex(pattern) and Regex.IsMatch/Match/Replace invocations where
    /// the pattern string literal contains known dangerous constructs.
    /// </summary>
    public async Task<List<SecurityIssueReport>> FindReDoSPatternsAsync(
        string filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return [];

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return [];

        var issues = new List<SecurityIssueReport>();

        // Collect all string literals that are used as Regex patterns
        var patternLiterals = new List<(string Pattern, int Line, int Col)>();

        // new Regex("pattern")
        foreach (var oc in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (!oc.Type.ToString().Contains("Regex")) continue;
            var firstArg = oc.ArgumentList?.Arguments.FirstOrDefault();
            if (firstArg?.Expression is LiteralExpressionSyntax lit &&
                lit.IsKind(SyntaxKind.StringLiteralExpression))
            {
                var loc = lit.GetLocation().GetLineSpan().StartLinePosition;
                patternLiterals.Add((lit.Token.ValueText, loc.Line + 1, loc.Character + 1));
            }
        }

        // Regex.IsMatch / Match / Matches / Replace / Split
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            if (ma.Expression.ToString() != "Regex") continue;
            if (ma.Name.Identifier.Text is not ("IsMatch" or "Match" or "Matches" or "Replace" or "Split")) continue;

            // Pattern is usually the second argument: Regex.IsMatch(input, pattern)
            var patternArg = inv.ArgumentList.Arguments.ElementAtOrDefault(1);
            if (patternArg?.Expression is LiteralExpressionSyntax patLit &&
                patLit.IsKind(SyntaxKind.StringLiteralExpression))
            {
                var loc = patLit.GetLocation().GetLineSpan().StartLinePosition;
                patternLiterals.Add((patLit.Token.ValueText, loc.Line + 1, loc.Character + 1));
            }
        }

        foreach (var (pattern, line, col) in patternLiterals)
        {
            foreach (var signal in ReDoSSignals)
            {
                bool matched = false;
                try { matched = signal.IsMatch(pattern); }
                catch (RegexMatchTimeoutException) { /* treat as safe */ }
                if (!matched) continue;

                issues.Add(new SecurityIssueReport(filePath, line, col,
                    "ReDoSVulnerablePattern",
                    $"Regex pattern contains nested quantifiers or alternation that can cause " +
                    $"catastrophic backtracking on adversarial input (ReDoS): '{Truncate(pattern, 60)}'. " +
                    "Use atomic groups, possessive quantifiers, or rewrite to avoid ambiguity."));
                break; // one report per pattern
            }
        }

        return issues;
    }

    private static string Truncate(string s, int max = 80) =>
        s.Length <= max ? s : s[..max] + "…";

    // ── UnvalidatedRegexSource ────────────────────────────────────────────────
    // Constructing a Regex from a non-literal (user input, config, database) enables
    // Regex injection: an attacker can supply a catastrophically backtracking pattern.

    private static readonly HashSet<string> RegexFactoryMethods = new(StringComparer.Ordinal)
    {
        "IsMatch", "Match", "Matches", "Replace", "Split"
    };

    /// <summary>
    /// Detects Regex construction or static method calls where the pattern argument is not a
    /// compile-time string literal. Non-literal patterns may originate from user input, enabling
    /// Regex injection and ReDoS attacks.
    /// </summary>
    public async Task<List<SecurityIssueReport>> FindUnvalidatedRegexSourceAsync(
        string filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var issues = new List<SecurityIssueReport>();

        var docIds = solution.GetDocumentIdsWithFilePath(filePath);
        foreach (var docId in docIds)
        {
            var doc = solution.GetDocument(docId);
            if (doc == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;
            var fp = doc.FilePath ?? doc.Name;

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                int patternIndex = -1;

                if (invocation.Expression is MemberAccessExpressionSyntax ma)
                {
                    var receiver = ma.Expression.ToString();
                    var name = ma.Name.Identifier.Text;

                    // Regex.IsMatch(input, pattern) — pattern is arg[1]
                    if (receiver == "Regex" && RegexFactoryMethods.Contains(name))
                        patternIndex = 1;
                }
                else if (invocation.Parent is ObjectCreationExpressionSyntax ctor &&
                         ctor.Type.ToString() is "Regex" or "System.Text.RegularExpressions.Regex")
                {
                    // new Regex(pattern) — pattern is arg[0]
                    patternIndex = 0;
                }

                if (patternIndex < 0) continue;

                // Check new Regex(...) separately since it's an ObjectCreationExpressionSyntax
                var args = invocation.ArgumentList.Arguments;
                if (patternIndex >= args.Count) continue;
                var patternExpr = args[patternIndex].Expression;

                // Safe: compile-time string literals or string literal concatenation
                if (patternExpr is LiteralExpressionSyntax) continue;
                if (IsStringLiteralChain(patternExpr)) continue;

                var loc = invocation.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new SecurityIssueReport(fp, loc.Line + 1, loc.Character + 1,
                    "UnvalidatedRegexSource",
                    $"Regex pattern is not a compile-time literal (argument: '{Truncate(patternExpr.ToString())}')." +
                    " A non-literal pattern may originate from user input, enabling Regex injection and ReDoS. " +
                    "Validate or allowlist the pattern, or use a static compiled Regex."));
            }

            // Also catch: new Regex(nonLiteralPattern)
            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (creation.Type.ToString() is not ("Regex" or "System.Text.RegularExpressions.Regex")) continue;
                var args = creation.ArgumentList?.Arguments;
                if (args == null || args.Value.Count == 0) continue;
                var patternExpr = args.Value[0].Expression;

                if (patternExpr is LiteralExpressionSyntax) continue;
                if (IsStringLiteralChain(patternExpr)) continue;

                var loc = creation.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new SecurityIssueReport(fp, loc.Line + 1, loc.Character + 1,
                    "UnvalidatedRegexSource",
                    $"new Regex() pattern is not a compile-time literal (argument: '{Truncate(patternExpr.ToString())}')." +
                    " A non-literal pattern from user input enables Regex injection and ReDoS. " +
                    "Validate or allowlist the pattern, or use a static compiled Regex."));
            }
        }
        return issues;
    }

    private static bool IsStringLiteralChain(ExpressionSyntax expr) =>
        expr switch
        {
            LiteralExpressionSyntax => true,
            BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.AddExpression)
                => IsStringLiteralChain(bin.Left) && IsStringLiteralChain(bin.Right),
            _ => false
        };

    // ── RegexNewInLoop ────────────────────────────────────────────────────────
    // Constructing a new Regex inside a loop recompiles the pattern on every iteration.
    // If the pattern also comes from user input this is simultaneously a ReDoS vector
    // and a CPU-waste issue. Static or cached compiled Regex should be used instead.

    private static readonly SyntaxKind[] LoopKinds =
    [
        SyntaxKind.ForStatement, SyntaxKind.ForEachStatement,
        SyntaxKind.WhileStatement, SyntaxKind.DoStatement
    ];

    /// <summary>
    /// Detects new Regex() object creation expressions that appear inside loop bodies.
    /// Repeated compilation is both a performance problem and, when the pattern originates
    /// from user input, a ReDoS amplification vector.
    /// </summary>
    public async Task<List<SecurityIssueReport>> FindRegexNewInLoopAsync(
        string filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var issues = new List<SecurityIssueReport>();

        var docIds = solution.GetDocumentIdsWithFilePath(filePath);
        foreach (var docId in docIds)
        {
            var doc = solution.GetDocument(docId);
            if (doc == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;
            var fp = doc.FilePath ?? doc.Name;

            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (creation.Type.ToString() is not ("Regex" or "System.Text.RegularExpressions.Regex")) continue;

                // Must have a loop ancestor
                bool inLoop = creation.Ancestors().Any(a => LoopKinds.Contains(a.Kind()));
                if (!inLoop) continue;

                // Determine if the pattern is a literal (performance-only issue) or a variable (also security)
                var patternArg = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                bool isLiteral = patternArg is LiteralExpressionSyntax || IsStringLiteralChain(patternArg!);
                var severity = isLiteral ? "performance" : "performance + security (unvalidated pattern)";

                var loc = creation.GetLocation().GetLineSpan().StartLinePosition;
                issues.Add(new SecurityIssueReport(fp, loc.Line + 1, loc.Character + 1,
                    "RegexNewInLoop",
                    $"new Regex() inside a loop recompiles the pattern on every iteration ({severity}). " +
                    "Hoist to a static readonly field, use RegexOptions.Compiled, or call the static Regex.IsMatch overload."));
            }
        }
        return issues;
    }

    // ── JSON Usage Anti-Patterns ──────────────────────────────────────────────

    /// <summary>
    /// Detects common JSON anti-patterns:
    ///   1. JsonDocument.Parse() without a using block — leaks pooled memory.
    ///   2. JsonElement.GetProperty() without null/kind check — throws on missing keys.
    ///   3. Deserializing to dynamic or object — loses type safety.
    ///   4. JsonSerializer.Deserialize without null check on result — can silently return null.
    /// </summary>
    public async Task<List<SecurityIssueReport>> DetectJsonAntiPatternsAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return [];

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return [];

        var issues = new List<SecurityIssueReport>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;
            var receiver = ma.Expression.ToString();
            var methodName = ma.Name.Identifier.Text;

            // 1. JsonDocument.Parse() must be in a using block or using var
            if (receiver == "JsonDocument" && methodName == "Parse")
            {
                bool inUsing = invocation.Ancestors().Any(a =>
                    a is UsingStatementSyntax ||
                    (a is LocalDeclarationStatementSyntax lds && lds.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)));

                // Also allow: var doc = JsonDocument.Parse(...) where the variable is disposed via .Dispose() later
                bool hasExplicitDispose = false;
                if (!inUsing)
                {
                    var enclosingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    if (enclosingMethod != null)
                    {
                        // Find the variable name this Parse() is assigned to
                        var varDeclarator = invocation.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
                        if (varDeclarator != null)
                        {
                            var varName = varDeclarator.Identifier.Text;
                            hasExplicitDispose = enclosingMethod.DescendantNodes()
                                .OfType<InvocationExpressionSyntax>()
                                .Any(inv => inv.Expression is MemberAccessExpressionSyntax dma &&
                                            dma.Name.Identifier.Text == "Dispose" &&
                                            dma.Expression.ToString() == varName);
                        }
                    }
                }

                if (!inUsing && !hasExplicitDispose)
                {
                    var loc = invocation.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                        "JsonDocumentNotDisposed",
                        "JsonDocument.Parse() returns an IDisposable that uses pooled memory. " +
                        "Wrap it in 'using var doc = JsonDocument.Parse(...)' to return memory to the pool."));
                }
            }

            // 2. JsonElement.GetProperty() — should prefer TryGetProperty to avoid KeyNotFoundException
            if (methodName == "GetProperty")
            {
                var enclosing = invocation.Ancestors()
                    .OfType<TryStatementSyntax>()
                    .Any(ts => ts.Catches.Any(c =>
                        c.Declaration?.Type.ToString() is "KeyNotFoundException" or "JsonException" or "Exception" or "InvalidOperationException"));

                if (!enclosing)
                {
                    var loc = invocation.GetLocation().GetLineSpan().StartLinePosition;
                    issues.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                        "JsonUnsafeGetProperty",
                        $"'{ma.Expression}.GetProperty(...)' throws KeyNotFoundException if the property is absent. " +
                        "Use TryGetProperty(..., out JsonElement value) or wrap in try/catch."));
                }
            }

            // 3. JsonSerializer.Deserialize<dynamic> or Deserialize<object> — untyped
            if ((receiver == "JsonSerializer" || receiver == "JsonConvert") &&
                methodName == "Deserialize")
            {
                var genericArgs = (ma.Name as GenericNameSyntax)?.TypeArgumentList.Arguments;
                if (genericArgs != null)
                {
                    foreach (var arg in genericArgs)
                    {
                        var argText = arg.ToString();
                        if (argText is "dynamic" or "object")
                        {
                            var loc = invocation.GetLocation().GetLineSpan().StartLinePosition;
                            issues.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                                "JsonDeserializeToUntypedTarget",
                                $"Deserializing to '{argText}' loses compile-time type safety. " +
                                "Deserialize to a strongly-typed class or record instead."));
                        }
                    }
                }
            }

            // 4. Deserialize result used without null check
            // JsonSerializer.Deserialize<T>() can return null even for non-nullable T when the JSON is "null".
            if ((receiver == "JsonSerializer" || receiver == "JsonConvert") &&
                methodName is "Deserialize" or "DeserializeObject")
            {
                var parent = invocation.Parent;
                bool assignedToVar = parent is EqualsValueClauseSyntax;
                if (assignedToVar)
                {
                    var varDecl = parent?.Parent as VariableDeclaratorSyntax;
                    if (varDecl != null)
                    {
                        var varName = varDecl.Identifier.Text;
                        var enclosingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        if (enclosingMethod != null)
                        {
                            // Check that the variable has at least one null guard
                            bool hasNullGuard = enclosingMethod.DescendantNodes()
                                .OfType<IfStatementSyntax>()
                                .Any(ifStmt =>
                                {
                                    var cond = ifStmt.Condition.ToString();
                                    return cond.Contains(varName) &&
                                           (cond.Contains("null") || cond.Contains("is null") || cond.Contains("!= null"));
                                });

                            if (!hasNullGuard)
                            {
                                var loc = invocation.GetLocation().GetLineSpan().StartLinePosition;
                                issues.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1,
                                    "JsonDeserializeNullUnChecked",
                                    $"'{receiver}.{methodName}(...)' result stored in '{varName}' is never null-checked. " +
                                    "Deserialize can return null when the JSON payload is 'null'. " +
                                    "Add a null guard or use the null-forgiving operator only if you control the payload."));
                            }
                        }
                    }
                }
            }
        }

        return issues;
    }
}
