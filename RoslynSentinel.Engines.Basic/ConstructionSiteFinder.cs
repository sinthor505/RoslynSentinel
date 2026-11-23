using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Finds the places that construct a type without calling its constructor in the source: reflective construction
/// (ActivatorUtilities, Activator.CreateInstance with arguments) and dependency-injection registration
/// (IServiceCollection extension calls such as AddSingleton&lt;T&gt;()). Both are matched by symbol; a constructor change or a
/// who-uses query does not see them as call sites because they bind to no constructor. Lines are 1-based.
/// </summary>
public static class ConstructionSiteFinder
{
    private const string DiNamespace = "Microsoft.Extensions.DependencyInjection";

    /// <summary>
    /// Calls of Microsoft.Extensions.DependencyInjection.ActivatorUtilities (CreateInstance, CreateFactory, GetServiceOrCreateInstance, generic or not) and of
    /// System.Activator.CreateInstance(Type, params object[] args) that name <paramref name="type"/> as a type argument or a typeof argument, matched by symbol.
    /// </summary>
    public static Task<List<SkippedCallSite>> FindReflectiveConstructionSitesAsync(Solution solution, INamedTypeSymbol type, CancellationToken cancellationToken)
        => ScanInvocationsAsync(solution, IsReflectiveConstructionName, (model, invocation, method) =>
        {
            var api = method.ContainingType;
            var isActivatorUtilities = api.Name == "ActivatorUtilities" && api.ContainingNamespace.ToDisplayString() == DiNamespace;
            var isActivatorWithArgs = api.Name == "Activator" && api.ContainingNamespace.ToDisplayString() == "System" && method.Name == "CreateInstance"
                && method.Parameters.Any(p => p.Name == "args");
            if (!isActivatorUtilities && !isActivatorWithArgs)
            {
                return null;
            }

            var namesType = method.TypeArguments.Any(t => IsSameType(t, type))
                || invocation.ArgumentList.DescendantNodes().OfType<TypeOfExpressionSyntax>()
                    .Any(typeOf => IsSameType(model.GetTypeInfo(typeOf.Type, cancellationToken).Type, type));
            return namesType
                ? $"{api.Name}.{method.Name} names '{type.Name}'; its arguments are not rewritten and are resolved at runtime"
                : null;
        }, cancellationToken);

    /// <summary>
    /// Invocations of the IServiceCollection registration extensions (AddSingleton/AddScoped/AddTransient, TryAdd*, AddKeyed*, TryAddEnumerable; any overload) that name
    /// <paramref name="type"/> as a type argument (in any position, so AddSingleton&lt;IFoo, Foo&gt;() lists both IFoo and Foo) or as a direct typeof argument, or that pass a
    /// ServiceDescriptor factory call naming it. A type that only appears inside a factory lambda is not a registration of that type and is not listed.
    /// The reason text starts with "DI registration: ".
    /// </summary>
    public static Task<List<SkippedCallSite>> FindDiRegistrationSitesAsync(Solution solution, INamedTypeSymbol type, CancellationToken cancellationToken)
        => ScanInvocationsAsync(solution, IsDiRegistrationName, (model, invocation, method) =>
        {
            if (!IsServiceCollectionExtension(method))
            {
                return null;
            }

            var namesType = method.TypeArguments.Any(t => IsSameType(t, type))
                || invocation.ArgumentList.Arguments.Any(argument => argument.Expression switch
                {
                    TypeOfExpressionSyntax typeOf => IsSameType(model.GetTypeInfo(typeOf.Type, cancellationToken).Type, type),
                    InvocationExpressionSyntax inner when model.GetSymbolInfo(inner, cancellationToken).Symbol is IMethodSymbol { ContainingType.Name: "ServiceDescriptor" } descriptor =>
                        descriptor.TypeArguments.Any(t => IsSameType(t, type))
                        || inner.ArgumentList.Arguments.Any(a => a.Expression is TypeOfExpressionSyntax innerTypeOf && IsSameType(model.GetTypeInfo(innerTypeOf.Type, cancellationToken).Type, type)),
                    _ => false
                });
            return namesType
                ? $"{DiRegistrationPrefix}{method.Name} names '{type.Name}'; the container constructs it at runtime, so no constructor call is visible here"
                : null;
        }, cancellationToken);

    /// <summary>Start of every reason text returned by <see cref="FindDiRegistrationSitesAsync"/>.</summary>
    public const string DiRegistrationPrefix = "DI registration: ";

    private static bool IsReflectiveConstructionName(string name) => name is "CreateInstance" or "CreateFactory" or "GetServiceOrCreateInstance";

    private static bool IsDiRegistrationName(string name) => name is "AddSingleton" or "AddScoped" or "AddTransient"
        or "TryAddSingleton" or "TryAddScoped" or "TryAddTransient" or "TryAddEnumerable"
        or "AddKeyedSingleton" or "AddKeyedScoped" or "AddKeyedTransient"
        or "TryAddKeyedSingleton" or "TryAddKeyedScoped" or "TryAddKeyedTransient" or "Add";

    private static bool IsSameType(ITypeSymbol? candidate, INamedTypeSymbol type)
        => candidate != null && SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, type.OriginalDefinition);

    private static bool IsServiceCollectionExtension(IMethodSymbol method)
    {
        var api = method.ContainingType;
        if (api.ContainingNamespace.ToDisplayString() != DiNamespace)
        {
            return false;
        }

        if (api.Name.EndsWith("ServiceCollectionServiceExtensions", StringComparison.Ordinal)
            || api.Name.EndsWith("ServiceCollectionDescriptorExtensions", StringComparison.Ordinal)
            || api.Name.EndsWith("ServiceCollectionKeyedServiceExtensions", StringComparison.Ordinal))
        {
            return true;
        }

        return method.IsExtensionMethod && (method.ReducedFrom ?? method).Parameters.FirstOrDefault()?.Type.Name == "IServiceCollection";
    }

    // Shared scan: a cheap syntactic pre-filter on the invoked name, then the semantic model (built only for files that pass it) resolves each candidate to a method
    // and reasonFor returns the reason text when the call is a site, null otherwise.
    private static async Task<List<SkippedCallSite>> ScanInvocationsAsync(
        Solution solution,
        Func<string, bool> nameFilter,
        Func<SemanticModel, InvocationExpressionSyntax, IMethodSymbol, string?> reasonFor,
        CancellationToken cancellationToken)
    {
        var sites = new List<SkippedCallSite>();
        foreach (var document in solution.Projects.Where(project => project.Language == LanguageNames.CSharp).SelectMany(project => project.Documents))
        {
            if (document.FilePath == null || await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
            {
                continue;
            }

            var candidates = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.Expression switch
                {
                    MemberAccessExpressionSyntax access => nameFilter(access.Name.Identifier.ValueText),
                    SimpleNameSyntax name => nameFilter(name.Identifier.ValueText),
                    _ => false
                })
                .ToList();
            if (candidates.Count == 0 || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            foreach (var invocation in candidates)
            {
                if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
                {
                    continue;
                }

                if (reasonFor(model, invocation, method) is { } reason)
                {
                    var line = root.SyntaxTree.GetLineSpan(invocation.Span, cancellationToken: cancellationToken).StartLinePosition.Line + 1;
                    sites.Add(new SkippedCallSite(document.FilePath, line, reason));
                }
            }
        }

        return sites;
    }
}

/// <summary>
/// The construction sites of a constructor or a type as <see cref="Finding"/>s, one kind per label, for the who-uses and blast-radius tools. Findings are leads
/// to check, never hits: they are not counted among references or callers.
/// </summary>
public static class ConstructionSiteFindings
{
    private const int CapPerKind = 25;
    private const string ReflectiveLabel = "Reflective construction";
    private const string DiLabel = "DI registration";

    /// <summary>
    /// Empty unless <paramref name="symbol"/> is a constructor (its containing type is used) or a class/struct/record type. One Info finding per site, labelled
    /// "Reflective construction" or "DI registration", at most 25 per kind plus one finding stating the total when a kind is capped.
    /// <paramref name="isExcludedFile"/>, when given, drops every site whose absolute file path it accepts; the drop happens before the per-kind cap, so dropped sites never count toward it.
    /// </summary>
    public static async Task<List<Finding>> ForSymbolAsync(Solution solution, ISymbol symbol, string findingSource, CancellationToken cancellationToken, Func<string, bool>? isExcludedFile = null)
    {
        var findings = new List<Finding>();
        var type = symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => constructor.ContainingType,
            INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } named => named,
            _ => null
        };
        if (type == null)
        {
            return findings;
        }

        var reflectiveSites = await ConstructionSiteFinder.FindReflectiveConstructionSitesAsync(solution, type, cancellationToken);
        var diSites = await ConstructionSiteFinder.FindDiRegistrationSitesAsync(solution, type, cancellationToken);
        AddKind(findings, findingSource, ReflectiveLabel, reflectiveSites.Where(site => isExcludedFile?.Invoke(site.FilePath.Absolute) != true).ToList(), stripPrefix: null);
        AddKind(findings, findingSource, DiLabel, diSites.Where(site => isExcludedFile?.Invoke(site.FilePath.Absolute) != true).ToList(), ConstructionSiteFinder.DiRegistrationPrefix);
        return findings;
    }

    private static void AddKind(List<Finding> findings, string findingSource, string label, List<SkippedCallSite> sites, string? stripPrefix)
    {
        foreach (var site in sites.Take(CapPerKind))
        {
            var reason = stripPrefix != null && site.Reason.StartsWith(stripPrefix, StringComparison.Ordinal) ? site.Reason[stripPrefix.Length..] : site.Reason;
            findings.Add(new Finding(findingSource, $"{label}: {Path.GetFileName(site.FilePath.ToString())}:{site.LineNumber} {reason}", FindingSeverity.Info));
        }

        if (sites.Count > CapPerKind)
        {
            findings.Add(new Finding(findingSource, $"{label} listing capped at {CapPerKind}: {sites.Count} site(s) in total.", FindingSeverity.Info));
        }
    }
}
