#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Support;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
[Category("DiscoveryEngine")] // sentinel:auto-category
[Category("SearchResult")] // sentinel:auto-category
public class SemanticSearchEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private DiscoveryEngine _engine;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.DiscoveryEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task FindMethodsByReturnType_MatchingMethods_ReturnsCorrectResults()
    {
        SetSource(@"
public class InventoryService
{
    public Task<List<Product>> GetAllAsync() => null!;
    public Task<Product> GetByIdAsync(int id) => null!;
    public string GetName() => ""name"";
}");
        var results = await Basic.TestEngines.DiscoveryEngine(_workspaceManager).FindMethodsByReturnTypeAsync("Task");
        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Count, Is.EqualTo(2), "Should find exactly 2 Task-returning methods");
        Assert.That(results.All(r => r.MemberName is "GetAllAsync" or "GetByIdAsync"), Is.True);
    }

    [Test]
    public async Task FindMethodsByReturnType_NoMatch_ReturnsEmpty()
    {
        SetSource(@"
public class OrderService
{
    public int GetCount() => 0;
    public string GetName() => ""name"";
}");
        var results = await Basic.TestEngines.DiscoveryEngine(_workspaceManager).FindMethodsByReturnTypeAsync("XmlDocument");
        Assert.That(results, Is.Empty, "No methods return XmlDocument - should yield empty list");
    }

    [Test]
    public async Task FindTypesByAttribute_WithMatchingType_ReturnsResult()
    {
        SetSource(@"
public class ApiControllerAttribute : System.Attribute { }

[ApiController]
public class ProductsController { }

public class RegularClass { }
");
        var results = await Basic.TestEngines.DiscoveryEngine(_workspaceManager).FindTypesByAttributeAsync("ApiController");
        Assert.That(results.Count, Is.EqualTo(1), "Only one class has ApiController attribute");
        Assert.That(results[0].MemberName, Is.EqualTo("ProductsController"));
    }

    [Test]
    public async Task FindTypesByAttribute_NoMatchingAttribute_ReturnsEmpty()
    {
        SetSource(@"
public class PlainDto { public int Id { get; set; } }
");
        var results = await Basic.TestEngines.DiscoveryEngine(_workspaceManager).FindTypesByAttributeAsync("Obsolete");
        Assert.That(results, Is.Empty, "No types have Obsolete attribute");
    }

    [Test]
    public async Task FindTypesByAttribute_NamespacedAttribute_BareNameQuery_FindsDecoratedType()
    {
        SetSource(@"
using System;
namespace Ns
{
    public class MyAttrAttribute : Attribute { }
}
namespace App
{
    using Ns;
    [MyAttr]
    public class Widget { }
    public class Plain { }
}
");
        var results = await Basic.TestEngines.DiscoveryEngine(_workspaceManager).FindTypesByAttributeAsync("MyAttr");
        Assert.That(results.Count, Is.EqualTo(1), "Bare-name query must resolve the namespaced attribute and find exactly one decorated type");
        Assert.That(results[0].MemberName, Is.EqualTo("Widget"));
    }

    [Test]
    public async Task FindTypesByAttribute_NamespacedAttribute_QualifiedQuery_FindsTypeAndUsage()
    {
        SetSource(@"
using System;
namespace Ns
{
    public class MyAttrAttribute : Attribute { }
}
namespace App
{
    using Ns;
    [MyAttr]
    public class Widget { }
}
");
        var engine = Basic.TestEngines.DiscoveryEngine(_workspaceManager);
        var types = await engine.FindTypesByAttributeAsync("Ns.MyAttrAttribute");
        Assert.That(types.Count, Is.EqualTo(1), "Qualified query must find the type decorated with [MyAttr] written through the using directive");
        Assert.That(types[0].MemberName, Is.EqualTo("Widget"));

        var usages = await engine.FindAttributeUsagesAsync("Ns.MyAttrAttribute");
        Assert.That(usages.Count, Is.EqualTo(1), "Qualified query must match the [MyAttr] source usage by its last segment");
        Assert.That(usages[0].TargetName, Is.EqualTo("Widget"));
    }

    [Test]
    public async Task FindTypesByAttribute_NamespacedAttribute_CrefOnlyMention_IsNotCounted()
    {
        SetSource(@"
using System;
namespace Ns
{
    public class MyAttrAttribute : Attribute { }
}
namespace App
{
    /// <summary>See <see cref=""Ns.MyAttrAttribute""/> for details.</summary>
    public class DocOnly { }
}
");
        var engine = Basic.TestEngines.DiscoveryEngine(_workspaceManager);
        var types = await engine.FindTypesByAttributeAsync("Ns.MyAttrAttribute");
        Assert.That(types, Is.Empty, "A cref in an XML doc comment is not an attribute application and must not be reported as a typesWithAttribute hit");

        var usages = await engine.FindAttributeUsagesAsync("Ns.MyAttrAttribute");
        Assert.That(usages, Is.Empty, "A cref in an XML doc comment is not an AttributeSyntax and must not be reported as an attributeUsages hit");
    }
}
