using System.Text.Json;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

[TestFixture]
public class ToolSignatureCatalogTests
{
    private const string ToolName = "demo_tool";

    [Test]
    public void FromSchemas_RequiredAndOptional_FlagsFollowRequiredArray()
    {
        var catalog = Build(ToolName, """{"type":"object","properties":{"path":{"type":"string"},"limit":{"type":"integer"}},"required":["path"]}""");

        Assert.That(catalog.TryGetParameters(ToolName, out var parameters), Is.True);
        Assert.That(parameters, Has.Count.EqualTo(2));
        Assert.That(parameters[0], Is.EqualTo(new ToolParameterSpec("path", true, "string", null)));
        Assert.That(parameters[1], Is.EqualTo(new ToolParameterSpec("limit", false, "integer", null)));
    }

    [Test]
    public void FromSchemas_PropertyOrder_IsPreserved()
    {
        var catalog = Build(ToolName, """{"properties":{"zeta":{"type":"string"},"alpha":{"type":"string"},"mid":{"type":"string"}}}""");

        Assert.That(catalog.TryGetParameters(ToolName, out var parameters), Is.True);
        Assert.That(parameters, Has.Count.EqualTo(3));
        Assert.That(parameters[0].Name, Is.EqualTo("zeta"));
        Assert.That(parameters[1].Name, Is.EqualTo("alpha"));
        Assert.That(parameters[2].Name, Is.EqualTo("mid"));
    }

    [Test]
    public void FromSchemas_NullableTypeArray_UsesFirstNonNullType()
    {
        var catalog = Build(ToolName, """{"properties":{"flag":{"type":["null","boolean"]}}}""");

        catalog.TryGetParameters(ToolName, out var parameters);
        Assert.That(parameters[0].JsonType, Is.EqualTo("boolean"));
    }

    [Test]
    public void FromSchemas_MissingType_DefaultsToString()
    {
        var catalog = Build(ToolName, """{"properties":{"x":{}}}""");

        catalog.TryGetParameters(ToolName, out var parameters);
        Assert.That(parameters[0].JsonType, Is.EqualTo("string"));
        Assert.That(parameters[0].EnumValues, Is.Null);
    }

    [Test]
    public void FromSchemas_DirectEnum_ReadsValues()
    {
        var catalog = Build(ToolName, """{"properties":{"mode":{"type":"string","enum":["fast","slow"]}}}""");

        catalog.TryGetParameters(ToolName, out var parameters);
        Assert.That(parameters[0].EnumValues, Is.EqualTo(new[] { "fast", "slow" }));
    }

    [Test]
    public void FromSchemas_EnumInsideAnyOf_ReadsValues()
    {
        var catalog = Build(ToolName, """{"properties":{"mode":{"anyOf":[{"type":"null"},{"type":"string","enum":["a","b"]}]}}}""");

        catalog.TryGetParameters(ToolName, out var parameters);
        Assert.That(parameters[0].EnumValues, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void FromSchemas_NoProperties_YieldsEmptyParameterList()
    {
        var catalog = Build(ToolName, """{"type":"object"}""");

        Assert.That(catalog.Contains(ToolName), Is.True);
        Assert.That(catalog.TryGetParameters(ToolName, out var parameters), Is.True);
        Assert.That(parameters, Is.Empty);
    }

    [Test]
    public void FromSchemas_NotAnObjectSchema_YieldsEmptyParameterList()
    {
        var catalog = Build(ToolName, """ "just a string" """);

        Assert.That(catalog.Contains(ToolName), Is.True);
        catalog.TryGetParameters(ToolName, out var parameters);
        Assert.That(parameters, Is.Empty);
    }

    [Test]
    public void FromSchemas_MalformedRequired_DoesNotThrow()
    {
        ToolSignatureCatalog? catalog = null;

        Assert.DoesNotThrow(() => catalog = Build(ToolName, """{"properties":{"path":{"type":"string"}},"required":"path"}"""));

        catalog!.TryGetParameters(ToolName, out var parameters);
        Assert.That(parameters[0].Required, Is.False);
    }

    [Test]
    public void Contains_UnknownTool_ReturnsFalseAndNoParameters()
    {
        var catalog = Build(ToolName, """{"properties":{}}""");

        Assert.That(catalog.Contains("other_tool"), Is.False);
        Assert.That(catalog.TryGetParameters("other_tool", out var parameters), Is.False);
        Assert.That(parameters, Is.Empty);
    }

    [Test]
    public void Contains_IsCaseSensitive()
    {
        var catalog = Build(ToolName, """{"properties":{}}""");

        Assert.That(catalog.Contains("DEMO_TOOL"), Is.False);
    }

    [Test]
    public void Empty_HasNoTools()
    {
        Assert.That(ToolSignatureCatalog.Empty.ToolNames, Is.Empty);
        Assert.That(ToolSignatureCatalog.Empty.Contains(ToolName), Is.False);
    }

    private static ToolSignatureCatalog Build(string toolName, string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return ToolSignatureCatalog.FromSchemas(new[] { KeyValuePair.Create(toolName, document.RootElement.Clone()) });
    }
}
