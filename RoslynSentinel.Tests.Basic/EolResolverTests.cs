using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

public class EolResolverTests
{
    [TearDown]
    public void ResetEolPolicy()
    {
        // EolPolicyOptions is static, so reset it after each test (as SchemaLeanProfileTests does) to keep it from leaking into other tests.
        EolPolicyOptions.Configure(System.Array.Empty<string>());
    }

    [TestCase(EolMode.Crlf, "\r\n")]
    [TestCase(EolMode.Lf, "\n")]
    public void Resolve_ExplicitMode_IgnoresPreImageAndEditorConfig(EolMode mode, string expected)
    {
        Assert.That(EolResolver.Resolve(mode, "a\nb\n", "lf"), Is.EqualTo(expected));
        Assert.That(EolResolver.Resolve(mode, "a\r\nb\r\n", "crlf"), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_Auto_CrlfOnlyPreImage_ReturnsCrlf()
    {
        Assert.That(EolResolver.Resolve(EolMode.Auto, "a\r\nb\r\n", "lf"), Is.EqualTo("\r\n"));
    }

    [Test]
    public void Resolve_Auto_LfOnlyPreImage_ReturnsLf()
    {
        Assert.That(EolResolver.Resolve(EolMode.Auto, "a\nb\n", "crlf"), Is.EqualTo("\n"));
    }

    [TestCase("lf", "\n")]
    [TestCase("crlf", "\r\n")]
    public void Resolve_Auto_MixedPreImage_FallsBackToEditorConfig(string editorConfig, string expected)
    {
        Assert.That(EolResolver.Resolve(EolMode.Auto, "a\r\nb\nc\r\n", editorConfig), Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_Auto_NullPreImageAndNoEditorConfig_ReturnsEnvironmentNewLine()
    {
        Assert.That(EolResolver.Resolve(EolMode.Auto, null, null), Is.EqualTo(Environment.NewLine));
    }

    [Test]
    public void Resolve_Auto_NoNewlinePreImage_UsesEditorConfigCrlf()
    {
        Assert.That(EolResolver.Resolve(EolMode.Auto, "single line", "crlf"), Is.EqualTo("\r\n"));
    }

    [TestCase("cr")]
    [TestCase("garbage")]
    [TestCase("")]
    [TestCase(null)]
    public void Resolve_Auto_IgnoresUnsupportedEditorConfigValue(string? editorConfig)
    {
        Assert.That(EolResolver.Resolve(EolMode.Auto, null, editorConfig), Is.EqualTo(Environment.NewLine));
    }

    [TestCase("crlf", EolMode.Crlf)]
    [TestCase("LF", EolMode.Lf)]
    [TestCase("auto", EolMode.Auto)]
    [TestCase("bogus", EolMode.Auto)]
    [TestCase(null, EolMode.Auto)]
    public void ParseMode_MapsKnownValuesAndFallsBackToAuto(string? raw, EolMode expected)
    {
        Assert.That(EolPolicyOptions.ParseMode(raw), Is.EqualTo(expected));
    }

    [Test]
    public void Configure_EolLfArgument_SetsModeLf()
    {
        EolPolicyOptions.Configure(new[] { "--eol=lf" });

        Assert.That(EolPolicyOptions.Mode, Is.EqualTo(EolMode.Lf));
    }

    [Test]
    public void Configure_NoArgument_ResetsModeToAuto()
    {
        EolPolicyOptions.Configure(new[] { "--eol=lf" });
        EolPolicyOptions.Configure(System.Array.Empty<string>());

        Assert.That(EolPolicyOptions.Mode, Is.EqualTo(EolMode.Auto));
    }
}
