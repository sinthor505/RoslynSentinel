using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("MemberRefactoringEngine")]
[Category("PreviewCallSite")]
public sealed class PreviewInstanceMoveOverloadRefusalTests
{
    private const string ClassASource = """
        namespace ContosoOrders.Core;

        public class PreviewMoveClassA
        {
            public void Foo(int a)
            {
            }

            public void Foo(string s)
            {
            }
        }
        """;

    private const string ClassBSource = """
        namespace ContosoOrders.Core;

        public class PreviewMoveClassB
        {
        }
        """;

    private const string CallerSource = """
        namespace ContosoOrders.Core;

        public class PreviewMoveCaller
        {
            public void Run(PreviewMoveClassA a)
            {
                a.Foo(1);
                a.Foo("x");
            }
        }
        """;

    private static MemberRefactoringEngine CreateEngine(InMemoryWorkspace workspace)
    {
        var diffEngine = TestEngines.DiffEngine();
        var validationEngine = TestEngines.ValidationEngine(workspace.Manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNav = TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        return TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, validationEngine);
    }

    [Test]
    public async Task BareOverloadedName_PreviewAndMoveRefuseWithSameMessage()
    {
        using var workspace = InMemoryWorkspace.Create(
            ("ContosoOrders.Core/PreviewMoveClassA.cs", ClassASource),
            ("ContosoOrders.Core/PreviewMoveClassB.cs", ClassBSource));
        var engine = CreateEngine(workspace);
        var path = workspace.PathOf("ContosoOrders.Core/PreviewMoveClassA.cs");

        var previewRefusal = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(() => engine.PreviewInstanceMoveCallSitesAsync(path, "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB"));
        var moveRefusal = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(() => engine.MoveMemberAsync(path, "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB"));

        Assert.That(previewRefusal!.Message, Is.EqualTo(moveRefusal!.Message), "The dry run must refuse with the same message as the real move.");
        Assert.That(previewRefusal.Message, Does.Contain("Foo(int)"));
        Assert.That(previewRefusal.Message, Does.Contain("Foo(string)"));
    }

    [Test]
    public async Task SignatureName_PreviewCoversOnlyThatOverloadsCallSites()
    {
        using var workspace = InMemoryWorkspace.Create(
            ("ContosoOrders.Core/PreviewMoveClassA.cs", ClassASource),
            ("ContosoOrders.Core/PreviewMoveClassB.cs", ClassBSource),
            ("ContosoOrders.Core/PreviewMoveCaller.cs", CallerSource));
        var engine = CreateEngine(workspace);

        var results = await engine.PreviewInstanceMoveCallSitesAsync(workspace.PathOf("ContosoOrders.Core/PreviewMoveClassA.cs"), "PreviewMoveClassA", ["Foo(int)"], "PreviewMoveClassB");

        // CallExpression is only the member access ("a.Foo") and cannot tell the overloads apart. Two sites would
        // mean the Foo(string) call was previewed too, so a count of one is the check.
        var sites = results.ToList();
        Assert.That(sites, Has.Count.EqualTo(1), "Only the Foo(int) call site should be previewed.");
        Assert.That(sites[0].CallExpression, Does.Contain("Foo"));
    }
}
