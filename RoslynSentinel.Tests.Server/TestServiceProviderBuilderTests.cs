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
        Assert.That(provider, Is.Not.Null);

        // Spot-check one engine that depends on IWorkspaceReader specifically.
        //var engine = provider!.GetRequiredService<BasicRefactoringEngine>();
        //Assert.That(engine, Is.Not.Null);

        // Confirm that IWorkspaceReader is registered.
        var reader = provider!.GetRequiredService<IWorkspaceReader>();
        Assert.That(reader, Is.Not.Null);
    }
}
