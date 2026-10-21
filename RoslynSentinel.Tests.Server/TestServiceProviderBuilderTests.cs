using Microsoft.Extensions.DependencyInjection;

namespace RoslynSentinel.Tests.Server;

public class TestServiceProviderBuilderTests
{
    [Test]
    public void Build_ResolvesFullBasicEngineGraph_WithoutThrowing()
    {
        // ValidateOnBuild=true means BuildServiceProvider itself throws AggregateException if any
        // registered service's dependency graph doesn't resolve - this is the same check that
        // caught the IWorkspaceReader gap at real server startup (see
        // docs/current/blockers/resolved/blocking_error_iworkspacereader_never_registered_server_wont_start.md).
        IServiceProvider? provider = null;
        Assert.DoesNotThrow(() => provider = TestServiceProviderBuilder.Build());

        // Spot-check one engine that depends on IWorkspaceReader specifically.
        //var engine = provider!.GetRequiredService<BasicRefactoringEngine>();

        // Confirm that IWorkspaceReader is a singleton: two resolutions return the same instance.
        var reader = provider!.GetRequiredService<IWorkspaceReader>();
        Assert.That(provider!.GetRequiredService<IWorkspaceReader>(), Is.SameAs(reader));
    }

    [Test]
    public void Build_ResolvesFullBasicEngineGraph_WithCustomConfiguration()
    {
        // ValidateOnBuild=true means BuildServiceProvider itself throws AggregateException if any
        // registered service's dependency graph doesn't resolve - this is the same check that
        // caught the IWorkspaceReader gap at real server startup (see
        // docs/current/blockers/resolved/blocking_error_iworkspacereader_never_registered_server_wont_start.md).
        var customDummy = new object();
        IServiceProvider? provider = null;
        Assert.DoesNotThrow(() => provider = TestServiceProviderBuilder.Build(services =>
        {
            // Add a dummy singleton to confirm that custom configuration is applied.
            services.AddSingleton(customDummy);
        }));
        // Confirm that the custom singleton is the registered instance.
        var dummy = provider!.GetRequiredService<object>();
        Assert.That(dummy, Is.SameAs(customDummy));
    }
}
