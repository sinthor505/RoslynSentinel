using Microsoft.Extensions.DependencyInjection;

using RoslynSentinel.Server.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

public static class TestServiceProviderBuilder
{
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Builds a service provider using the real production registration
    /// (<see cref="ServiceRegistrationExtensionsBasic.AddRoslynSentinelEnginesBasic"/>) with
    /// <see cref="FakeWorkspaceManager"/> substituted for <see cref="PersistentWorkspaceManager"/>
    /// on every interface it implements. Use this instead of hand-constructing individual engines
    /// so tests exercise the same DI graph production does -- a missing forward (e.g. the
    /// IWorkspaceReader gap fixed 2026-09-25) shows up here via ValidateOnBuild instead of only at
    /// real server startup.
    /// </summary>
    public static IServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        // AddRoslynSentinelEnginesBasic() registers the concrete PersistentWorkspaceManager
        // singleton, which requires ILogger<IWorkspaceManager>. Production only ever runs
        // this registration inside a full host, where AddLogging() (or equivalent) has
        // already supplied the open-generic ILogger<T> -- a bare ServiceCollection needs it
        // added explicitly or ValidateOnBuild fails at first resolution.
        services.AddLogging();
        services.AddRoslynSentinelEnginesBasic();

        var fake = new FakeWorkspaceManager();
        services.AddSingleton(fake);
        services.AddSingleton<IWorkspaceManager>(fake);
        services.AddSingleton<ISolutionProvider>(fake);
        services.AddSingleton<IManualCircuitBreaker>(fake);
        services.AddSingleton<IAutomaticCircuitBreaker>(fake);
        services.AddSingleton<IUnrecoverableBreaker>(fake);
        services.AddSingleton<IWorkspaceHealthReporter>(fake);
        services.AddSingleton<IWorkspaceMutator>(fake);
        services.AddSingleton<IRateLimiter>(fake);
        services.AddSingleton<ISymbolResolver>(fake);
        services.AddSingleton<IScopedOperationLedger>(fake);
        services.AddSingleton<IWorkspaceReader>(fake);

        configure?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }


    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Builds a service provider using the real production registration with the real
    /// <see cref="PersistentWorkspaceManager"/> (no fake substitution). Use for tests that need
    /// actual on-disk solution loading, MSBuild, or file-watcher behavior -- pair with
    /// <see cref="TestSolutionFixture"/> rather than calling this directly in most cases.
    /// </summary>
    public static IServiceProvider BuildWithRealWorkspace(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        // See the comment in Build() above: a bare ServiceCollection needs AddLogging()
        // for PersistentWorkspaceManager's ILogger<IWorkspaceManager> dependency to resolve.
        services.AddLogging();
        services.AddRoslynSentinelEnginesBasic();

        configure?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
