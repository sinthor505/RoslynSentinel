using System;
using System.Linq;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")]
public class ToolSignatureCatalogReflectionTests
{
    [Test]
    public void FromLoadedAssemblies_ContainsKnownTools_WithServerRequiredRules()
    {
        var catalog = ToolSignatureCatalogReflection.FromLoadedAssemblies();

        Assert.That(catalog.Contains("GetLargeResult"), Is.True);
        Assert.That(catalog.Contains("ReadFile"), Is.True);
        Assert.That(catalog.Contains("Search"), Is.True);
        Assert.That(catalog.Contains("LoadSolution"), Is.True);

        foreach (var tool in new[] { "GetLargeResult", "ReadFile", "Search", "LoadSolution" })
        {
            Assert.That(RequireParameter(catalog, tool, "reason").Required, Is.True, $"{tool}.reason should be required");
        }

        Assert.That(RequireParameter(catalog, "GetLargeResult", "resultId").Required, Is.False);
        Assert.That(RequireParameter(catalog, "Search", "mode").Required, Is.True);
    }

    [Test]
    public void FromLoadedAssemblies_IsNonEmpty()
    {
        Assert.That(ToolSignatureCatalogReflection.FromLoadedAssemblies().ToolNames.Count, Is.GreaterThan(20));
    }

    [Test]
    public void FromLoadedAssemblies_CalledTwice_GivesEqualToolNameSets()
    {
        var first = ToolSignatureCatalogReflection.FromLoadedAssemblies().ToolNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var second = ToolSignatureCatalogReflection.FromLoadedAssemblies().ToolNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.That(second, Is.EqualTo(first));
    }

    private static ToolParameterSpec RequireParameter(ToolSignatureCatalog catalog, string toolName, string parameterName)
    {
        Assert.That(catalog.TryGetParameters(toolName, out var parameters), Is.True, $"{toolName} is not in the catalog");
        return parameters.Single(p => p.Name == parameterName);
    }
}
