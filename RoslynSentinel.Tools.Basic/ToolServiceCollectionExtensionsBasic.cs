using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Tools.Basic;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Tool-layer DI registration for the Basic flavor. Lives in Tools.Basic (namespace
/// <c>Microsoft.Extensions.DependencyInjection</c> so callers need no extra using) so a project that cannot
/// reference a Server project -- chiefly test projects -- can register and resolve the real tool graph.
/// Everything here is <c>TryAdd*</c>: a registration the host made earlier or later wins, whichever order the
/// calls come in. Mode gating (<c>activeToolClasses</c>) and MCP exposure (<c>WithSentinelTools</c>) are host
/// concepts and stay in the Server project.
/// </summary>
public static class ToolServiceCollectionExtensionsBasic
{
    /// <summary>
    /// Registers the dependencies of the Basic tool classes: the <c>*Impl</c> helpers (shared between several
    /// classes, hence unconditional), the plain <see cref="ILogger"/> they take, and a default
    /// <see cref="WriteToolAdviceHelper"/> that treats every tool as exposed. A host replaces the helper with a
    /// gated one (plain <c>AddSingleton</c> after this call). Requires <c>AddLogging</c> (an
    /// <see cref="ILoggerFactory"/>) and the engines (<c>AddRoslynSentinelEnginesBasic</c>).
    /// </summary>
    public static IServiceCollection AddRoslynSentinelToolImplsBasic(this IServiceCollection services)
    {
        // The *Tools/*Impl split classes take a plain (non-generic) ILogger, not ILogger<T>; nothing else
        // (including AddLogging) registers it, so without this every one of them throws at first resolution.
        services.TryAddSingleton(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("RoslynSentinel"));

        // The real ctor takes activeToolClasses / allowedToolNames and is not DI-constructible.
        services.TryAddSingleton(WriteToolAdviceHelper.WithAllToolsExposed());

        services.TryAddSingleton<WorkspaceReadNavigationImpl>();
        services.TryAddSingleton<SymbolNavigationImpl>();
        services.TryAddSingleton<SymbolRelationshipImpl>();
        services.TryAddSingleton<WorkspaceFileEditImpl>();
        services.TryAddSingleton<WorkspaceBuildTestImpl>();
        services.TryAddSingleton<WorkspaceProjectManagementImpl>();
        services.TryAddSingleton<WorkspaceHealthMiscImpl>();
        services.TryAddSingleton<TestCategoryTaggingImpl>();
        services.TryAddSingleton<QualifyCrefsImpl>();
        services.TryAddSingleton<RefactoringSignatureImpl>();
        services.TryAddSingleton<RefactoringStructuralImpl>();
        services.TryAddSingleton<RefactoringExtractionDocsImpl>();

        return services;
    }

    /// <summary>
    /// Registers every <c>[McpServerToolType]</c> class in Tools.Basic (one <c>TryAddSingleton</c> each), with
    /// no mode gating. A host registers only its active classes itself so a mode still validates only what it
    /// serves; a test calls this to get all of them. A new Tools.Basic tool class must be added here --
    /// <c>ToolClassRegistrationTests</c> (Tests.Tools.Basic) fails loudly when one is missing.
    /// </summary>
    public static IServiceCollection AddRoslynSentinelToolClassesBasic(this IServiceCollection services)
    {
        services.TryAddSingleton<AdminTools>();
        services.TryAddSingleton<DeclarationTools>();
        services.TryAddSingleton<DocumentationTools>();
        services.TryAddSingleton<GitRemoteTools>();
        services.TryAddSingleton<GitTools>();
        services.TryAddSingleton<NamedArgumentsTools>();
        services.TryAddSingleton<ParameterEditTools>();
        services.TryAddSingleton<QualifyCrefsTools>();
        services.TryAddSingleton<RefactoringExtractionDocsTools>();
        services.TryAddSingleton<RefactoringSignatureTools>();
        services.TryAddSingleton<RefactoringStructuralTools>();
        services.TryAddSingleton<SemanticFindReplaceTools>();
        services.TryAddSingleton<SymbolNavigationTools>();
        services.TryAddSingleton<SymbolRelationshipTools>();
        services.TryAddSingleton<TestCategoryTaggingTools>();
        services.TryAddSingleton<ToolsetControlTools>();
        services.TryAddSingleton<TypedMemberTools>();
        services.TryAddSingleton<WholeFileWriteTools>();
        services.TryAddSingleton<WorkspaceBuildTestTools>();
        services.TryAddSingleton<WorkspaceFileEditTools>();
        services.TryAddSingleton<WorkspaceHealthMiscTools>();
        services.TryAddSingleton<WorkspaceProjectManagementTools>();
        services.TryAddSingleton<WorkspaceReadNavigationTools>();
        services.TryAddSingleton<WorkspaceTools>();

        return services;
    }
}
