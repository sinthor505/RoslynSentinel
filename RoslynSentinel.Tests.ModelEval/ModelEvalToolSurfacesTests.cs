using ModelContextProtocol.Client;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Checks the tool list each <see cref="ModelEvalToolSurfaces"/> option produces through a world built by
/// <see cref="ModelEvalMcpEnvironment.CreateAsync"/>. Not a live-model test: CreateAsync needs no LM Studio and no
/// ROSLYNSENTINEL_LLM_MODEL (the agent client is only registered, never called).
/// </summary>
[TestFixture]
public class ModelEvalToolSurfacesTests
{
    [Test]
    public async Task Basic_OffersWorkspaceAndRefactorTools_NotWholeFileWriteOrAdvancedOnly()
    {
        var sampleDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        await using var environment = await ModelEvalMcpEnvironment.CreateAsync(
            sampleDirectory,
            restorePackages: false,
            beforeLoad: null,
            afterLoad: null,
            TestContext.CurrentContext.CancellationToken,
            configureToolSurface: ModelEvalToolSurfaces.Basic);

        var tools = await environment.Client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Tools.Advanced classes are registered only by AddRoslynSentinelToolsAdvanced, so none of its tools may appear.
        var advancedOnly = McpToolSchemaPatcher.DiscoverToolMethods([typeof(SubAgentTools).Assembly])
            .Select(t => t.ToolName)
            .Where(names.Contains)
            .ToList();

        TestContext.Out.WriteLine($"Basic surface offers {names.Count} tools: {string.Join(", ", names.Order(StringComparer.OrdinalIgnoreCase))}");

        Assert.Multiple(() =>
        {
            // "Workspace" mode (read/search/build/edit) and "Refactor" mode (member and modifier edits).
            Assert.That(names, Does.Contain("ReadFile"));
            Assert.That(names, Does.Contain("Search"));
            Assert.That(names, Does.Contain("Build"));
            Assert.That(names, Does.Contain("ReplaceSnippet"));
            Assert.That(names, Does.Contain("Member"));
            Assert.That(names, Does.Contain("ModifyModifier"));

            // The "WholeFileWrite" mode is not part of this surface, and no hide list is applied.
            Assert.That(names, Does.Not.Contain("WriteFile"));
            Assert.That(names, Does.Not.Contain("ApplyDiff"));
            Assert.That(advancedOnly, Is.Empty, "Tools.Advanced tools offered by the Basic surface");
        });
    }

    [Test]
    public async Task BorrowedFixture_IsUsedByTheEnvironmentAndNotDisposedWithIt()
    {
        var sampleDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        var fixture = new RoslynSentinel.Tests.TestSolutionFixture();
        try
        {
            var environment = await ModelEvalMcpEnvironment.CreateAsync(
                sampleDirectory,
                restorePackages: false,
                beforeLoad: null,
                afterLoad: null,
                TestContext.CurrentContext.CancellationToken,
                configureToolSurface: ModelEvalToolSurfaces.Basic,
                borrowedFixture: fixture);

            try
            {
                var tools = await environment.Client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
                Assert.Multiple(() =>
                {
                    Assert.That(environment.Fixture, Is.SameAs(fixture));
                    Assert.That(tools.Select(t => t.Name), Does.Contain("ReadFile"));
                });
            }
            finally
            {
                await environment.DisposeAsync();
            }

            Assert.That(Directory.Exists(fixture.SolutionDirectory), Is.True, "a borrowed fixture must not be disposed with the environment");
        }
        finally
        {
            fixture.Dispose();
        }
    }
}
