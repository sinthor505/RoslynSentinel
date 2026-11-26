using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="ToolResultFormatOptions.FromEnvironment"/> reads <c>ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT</c>
/// (json, plain or xml, case-insensitive; default json) and fails fast on anything else. The environment reader is
/// injected, so no process environment is touched.
/// </summary>
[TestFixture]
public class ToolResultFormatOptionsTests
{
    private const string Variable = "ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT";

    private static Func<string, string?> Env(string? value) => name => name == Variable ? value : null;

    [Test]
    public void Unset_IsJson()
    {
        Assert.That(ToolResultFormatOptions.FromEnvironment(Env(null)), Is.EqualTo(ToolResultFormat.Json));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void BlankValue_IsTreatedAsUnset(string value)
    {
        Assert.That(ToolResultFormatOptions.FromEnvironment(Env(value)), Is.EqualTo(ToolResultFormat.Json));
    }

    [TestCase("json", ToolResultFormat.Json)]
    [TestCase("plain", ToolResultFormat.Plain)]
    [TestCase("xml", ToolResultFormat.Xml)]
    [TestCase("JSON", ToolResultFormat.Json)]
    [TestCase("Plain", ToolResultFormat.Plain)]
    [TestCase(" XML ", ToolResultFormat.Xml)]
    public void EachValue_Parses(string value, ToolResultFormat expected)
    {
        Assert.That(ToolResultFormatOptions.FromEnvironment(Env(value)), Is.EqualTo(expected));
    }

    [Test]
    public void UnknownValue_Throws_NamingVariableValueAndAllowedValues()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ToolResultFormatOptions.FromEnvironment(Env("html")));

        Assert.That(exception!.Message, Does.Contain(Variable));
        Assert.That(exception.Message, Does.Contain("html"));
        Assert.That(exception.Message, Does.Contain("json|plain|xml"));
    }

    [Test]
    public void Descriptor_IsLiveAgentLoopSettingWithJsonDefault()
    {
        Assert.That(ToolResultFormatOptions.FormatSetting.EnvName, Is.EqualTo(Variable));
        Assert.That(ToolResultFormatOptions.FormatSetting.Scope, Is.EqualTo(SettingScope.AgentLoop));
        Assert.That(ToolResultFormatOptions.FormatSetting.Kind, Is.EqualTo(SettingKind.Live));
        Assert.That(ToolResultFormatOptions.FormatSetting.DefaultValue, Is.EqualTo(ToolResultFormat.Json));
    }

    [Test]
    public void Factory_JsonRenderer_ReturnsTheInputExactly()
    {
        const string body = "{\"a\":\"x\\\"y\"}";

        Assert.That(ToolResultRenderers.For(ToolResultFormat.Json).Render(body), Is.SameAs(body));
    }

    [TestCase(ToolResultFormat.Plain)]
    [TestCase(ToolResultFormat.Xml)]
    public void Factory_TextRenderers_AreAvailable(ToolResultFormat format)
    {
        Assert.That(ToolResultRenderers.For(format), Is.Not.Null);
    }
}
