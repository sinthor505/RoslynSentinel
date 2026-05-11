using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace RoslynSentinel.Server;

public record DependencyReport(string TypeName, string Status, string RecommendedLifetime);

public record DiRegistration(
    string Lifetime,
    string ServiceType,
    string? ImplementationType,
    string FilePath,
    int Line,
    string CallSite);

public record UnregisteredServiceFinding(
    string ConsumerClass,
    string ConsumerFile,
    int Line,
    string MissingType,
    string ConstructorParam);

public class DependencyInjectionEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public DependencyInjectionEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Analyzes a class to find what it needs in its constructor and checks if those are likely registered.
    /// </summary>
    public async Task<List<DependencyReport>> AnalyzeDependenciesAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);

        if (classNode == null) throw new Exception("Class not found.");

        var reports = new List<DependencyReport>();
        var constructor = classNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();

        if (constructor != null)
        {
            foreach (var parameter in constructor.ParameterList.Parameters)
            {
                var typeInfo = semanticModel!.GetTypeInfo(parameter.Type!);
                var typeSymbol = typeInfo.Type;

                if (typeSymbol != null)
                {
                    // Logic: Interfaces are usually Scoped/Singleton, DALs are Scoped, etc.
                    string lifetime = "Scoped";
                    if (typeSymbol.Name.Contains("Client") || typeSymbol.Name.Contains("Factory")) lifetime = "Singleton";
                    if (typeSymbol.Name.Contains("Context")) lifetime = "Scoped";

                    reports.Add(new DependencyReport(typeSymbol.ToDisplayString(), "Detected in Constructor", lifetime));
                }
            }
        }

        return reports;
    }

    private static readonly HashSet<string> _lifetimeMethods = new(StringComparer.Ordinal)
    {
        "AddSingleton", "AddScoped", "AddTransient"
    };

    /// <summary>
    /// Scans the solution (or a specific project/file) for DI registrations using a pure syntax-based approach.
    /// </summary>
    public async Task<List<DiRegistration>> FindDiRegistrationsAsync(
        string? projectName = null,
        string? filePath = null,
        string? lifetimeFilter = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<DiRegistration>();

        IEnumerable<Document> documents = solution.Projects
            .Where(p => projectName == null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Documents);

        if (filePath != null)
            documents = documents.Where(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        foreach (var doc in documents)
        {
            if (doc.FilePath == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? methodName = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    GenericNameSyntax gn => gn.Identifier.Text,
                    _ => null
                };

                if (methodName == null || !_lifetimeMethods.Contains(methodName)) continue;

                var lifetime = methodName switch
                {
                    "AddSingleton" => "Singleton",
                    "AddScoped" => "Scoped",
                    "AddTransient" => "Transient",
                    _ => "Unknown"
                };

                if (lifetimeFilter != null && !lifetime.Equals(lifetimeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string serviceType = "Unknown";
                string? implType = null;

                // Generic form: AddSingleton<IFoo, Foo>() or AddSingleton<IFoo>()
                TypeArgumentListSyntax? typeArgs = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax { Name: GenericNameSyntax gn2 } => gn2.TypeArgumentList,
                    GenericNameSyntax gn3 => gn3.TypeArgumentList,
                    _ => null
                };

                if (typeArgs != null && typeArgs.Arguments.Count >= 1)
                {
                    serviceType = typeArgs.Arguments[0].ToString();
                    if (typeArgs.Arguments.Count >= 2)
                        implType = typeArgs.Arguments[1].ToString();
                }
                else
                {
                    // Non-generic form: AddSingleton(typeof(IFoo), typeof(Foo))
                    var args = invocation.ArgumentList.Arguments;
                    if (args.Count >= 1 && args[0].Expression is TypeOfExpressionSyntax t0)
                        serviceType = t0.Type.ToString();
                    if (args.Count >= 2 && args[1].Expression is TypeOfExpressionSyntax t1)
                        implType = t1.Type.ToString();
                }

                var lineSpan = root.SyntaxTree.GetLineSpan(invocation.Span);
                var callSite = invocation.ToString();
                if (callSite.Length > 200) callSite = callSite[..200] + "...";

                results.Add(new DiRegistration(
                    lifetime,
                    serviceType,
                    implType,
                    doc.FilePath,
                    lineSpan.StartLinePosition.Line + 1,
                    callSite));
            }
        }

        return results;
    }

    /// <summary>
    /// Injects a new dependency into a class constructor and adds the corresponding private field.
    /// </summary>
    public async Task<string> AddDependencyAsync(string filePath, string className, string dependencyType, string dependencyName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) throw new Exception("Class not found.");

        var fieldName = $"_{char.ToLower(dependencyName[0])}{dependencyName.Substring(1)}";
        
        // 1. Create the field
        var fieldDecl = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(dependencyType))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(fieldName))))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        // 2. Update/Create constructor
        var constructor = classNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        var newParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(dependencyName)).WithType(SyntaxFactory.ParseTypeName(dependencyType));
        var assignment = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(fieldName),
                SyntaxFactory.IdentifierName(dependencyName)));

        ClassDeclarationSyntax newClassNode;
        if (constructor != null)
        {
            var newConstructor = constructor.AddParameterListParameters(newParameter)
                .AddBodyStatements(assignment);
            newClassNode = classNode.ReplaceNode(constructor, newConstructor);
        }
        else
        {
            var newConstructor = SyntaxFactory.ConstructorDeclaration(className)
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(newParameter)))
                .WithBody(SyntaxFactory.Block(assignment));
            newClassNode = classNode.AddMembers(newConstructor);
        }

        newClassNode = newClassNode.InsertNodesBefore(newClassNode.Members.First(), new[] { fieldDecl });

        var newRoot = root!.ReplaceNode(classNode, newClassNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static readonly HashSet<string> _frameworkProvidedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Microsoft.Extensions.Logging
        "ILogger", "ILoggerFactory", "ILoggerProvider",
        // Microsoft.Extensions.Options
        "IOptions", "IOptionsMonitor", "IOptionsSnapshot", "IOptionsFactory",
        // Microsoft.Extensions.Configuration
        "IConfiguration", "IConfigurationRoot", "IConfigurationSection",
        // Microsoft.Extensions.Hosting
        "IHostEnvironment", "IWebHostEnvironment", "IHostApplicationLifetime",
        "IHostLifetime", "IApplicationLifetime",
        // Microsoft.Extensions.DependencyInjection
        "IServiceProvider", "IServiceScopeFactory", "IServiceScope",
        // Microsoft.Extensions.Http
        "IHttpClientFactory",
        // Microsoft.Extensions.Caching
        "IMemoryCache", "IDistributedCache", "IHybridCache",
        // Microsoft.AspNetCore
        "IHttpContextAccessor", "IHttpContextFactory", "IActionContextAccessor",
        // Aspire / cloud infra (registered via extension methods, not Add<T,U>)
        "IConnection", "IConnectionMultiplexer",
        // Feature flags (Aspire/OpenFeature pattern)
        "IFeatureManager", "IVariantFeatureManager",
    };

    private static readonly HashSet<string> _serviceNameSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Service", "Repository", "Manager", "Factory", "Provider",
        "Handler", "Validator", "Dispatcher"
    };

    private static bool LooksLikeInjectedService(string typeName)
    {
        var simpleName = typeName.Split('<')[0].Split('.').Last();
        if (simpleName.Length >= 2 && simpleName[0] == 'I' && char.IsUpper(simpleName[1]))
            return true;
        foreach (var suffix in _serviceNameSuffixes)
            if (simpleName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsFrameworkProvided(string typeName)
    {
        var simpleName = typeName.Split('<')[0].Split('.').Last();
        foreach (var framework in _frameworkProvidedTypes)
            if (simpleName.StartsWith(framework, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Scans all constructors in the solution (or a specific project) and reports
    /// injected service parameters whose types are not found in any DI registration.
    /// </summary>
    public async Task<List<UnregisteredServiceFinding>> FindServicesNotRegisteredAsync(
        string? projectName = null,
        CancellationToken ct = default)
    {
        var registrations = await FindDiRegistrationsAsync(projectName: projectName, ct: ct);
        var registeredTypes = new HashSet<string>(
            registrations.Select(r => r.ServiceType.Split('<')[0].Split('.').Last()),
            StringComparer.OrdinalIgnoreCase);

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<UnregisteredServiceFinding>();

        var documents = solution.Projects
            .Where(p => projectName == null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Documents);

        foreach (var doc in documents)
        {
            if (doc.FilePath == null) continue;

            // Skip test files
            if (doc.FilePath.Contains(".Tests.") ||
                doc.FilePath.Contains("Tests\\") ||
                doc.FilePath.Contains("Tests/"))
                continue;

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            {
                var containingClass = ctor.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                var className = containingClass?.Identifier.Text ?? "<unknown>";

                foreach (var param in ctor.ParameterList.Parameters)
                {
                    if (param.Type == null) continue;

                    var typeName = param.Type.ToString();
                    var simpleName = typeName.Split('<')[0].Split('.').Last();

                    if (!LooksLikeInjectedService(typeName)) continue;
                    if (IsFrameworkProvided(typeName)) continue;
                    if (registeredTypes.Contains(simpleName)) continue;

                    var lineSpan = param.GetLocation().GetLineSpan();
                    results.Add(new UnregisteredServiceFinding(
                        className,
                        doc.FilePath,
                        lineSpan.StartLinePosition.Line + 1,
                        typeName,
                        param.Identifier.Text));
                }
            }
        }

        return results;
    }

    // ── CaptiveDependency ─────────────────────────────────────────────────

    public record CaptiveDependencyFinding(
        string ConsumerClass,
        string ConsumerLifetime,
        string DependencyType,
        string DependencyLifetime,
        string FilePath,
        int Line
    );

    /// <summary>
    /// Detects captive dependency anti-pattern: a longer-lived service (Singleton) depending
    /// on a shorter-lived one (Scoped/Transient), which causes the dependency to be captured
    /// for the container's lifetime rather than its intended scope.
    /// </summary>
    public async Task<List<CaptiveDependencyFinding>> FindCaptiveDependenciesAsync(
        string? projectName = null,
        CancellationToken ct = default)
    {
        // Build a map: simple type name → lifetime (checking both service and impl type)
        var registrations = await FindDiRegistrationsAsync(projectName: projectName, ct: ct);

        // Also parse lambda factory registrations (task 13)
        var lifetimeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reg in registrations)
        {
            var svc = SimpleName(reg.ServiceType);
            var impl = reg.ImplementationType != null ? SimpleName(reg.ImplementationType) : null;
            if (!string.IsNullOrEmpty(svc)) lifetimeMap.TryAdd(svc, reg.Lifetime);
            if (!string.IsNullOrEmpty(impl)) lifetimeMap.TryAdd(impl, reg.Lifetime);
        }

        // Also scan for lambda factory registrations:
        // services.AddSingleton(sp => new Foo(sp.GetRequiredService<IBar>()))
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var docs = solution.Projects
            .Where(p => projectName == null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Documents);

        foreach (var doc in docs)
        {
            if (doc.FilePath == null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null) continue;

            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? methodName = inv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    _ => null
                };
                if (methodName == null || !_lifetimeMethods.Contains(methodName)) continue;

                string lifetime;
                if (methodName == "AddSingleton") lifetime = "Singleton";
                else if (methodName == "AddScoped") lifetime = "Scoped";
                else if (methodName == "AddTransient") lifetime = "Transient";
                else continue;

                // Look for lambda argument: sp => new Foo(...)
                var lambdaArg = inv.ArgumentList.Arguments
                    .Select(a => a.Expression)
                    .OfType<SimpleLambdaExpressionSyntax>()
                    .FirstOrDefault();
                if (lambdaArg?.Body is not ObjectCreationExpressionSyntax lambdaOc) continue;

                var implTypeName = lambdaOc.Type.ToString().Split('<')[0].Split('.').Last();
                lifetimeMap.TryAdd(implTypeName, lifetime);
            }
        }

        // Now find violations
        var findings = new List<CaptiveDependencyFinding>();

        foreach (var doc in docs)
        {
            if (doc.FilePath == null) continue;
            var root2 = await doc.GetSyntaxRootAsync(ct);
            if (root2 == null) continue;

            foreach (var classDecl in root2.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var className = classDecl.Identifier.Text;
                if (!lifetimeMap.TryGetValue(className, out var consumerLifetime)) continue;
                if (consumerLifetime != "Singleton") continue; // Only Singletons can trap shorter-lived deps

                var ctor = classDecl.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
                if (ctor == null) continue;

                foreach (var param in ctor.ParameterList.Parameters)
                {
                    if (param.Type == null) continue;
                    var depSimpleName = SimpleName(param.Type.ToString());

                    if (!lifetimeMap.TryGetValue(depSimpleName, out var depLifetime)) continue;
                    if (depLifetime is "Scoped" or "Transient")
                    {
                        var line = param.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        findings.Add(new CaptiveDependencyFinding(
                            className, consumerLifetime,
                            param.Type.ToString(), depLifetime,
                            doc.FilePath, line));
                    }
                }
            }
        }

        return findings;
    }

    private static string SimpleName(string typeName) =>
        typeName.Split('<')[0].Split('.').Last();
}
