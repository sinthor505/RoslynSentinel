namespace RoslynSentinel.Tests.Basic;

using RoslynSentinel.Common;

public class EolWritePolicyTests
{
    [TestCase(EolMode.Crlf)]
    [TestCase(EolMode.Auto)]
    public void Crlf_NullPreImage_LfContent_ConvertsToCrlfOnly(EolMode mode)
    {
        var ok = EolWritePolicy.TryComputeContentToWrite(mode, "f.cs", null, "a\nb\n", null, false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("a\r\nb\r\n"));
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void Lf_CrlfOnlyPreImage_CrlfContent_ConvertsToLf()
    {
        var ok = EolWritePolicy.TryComputeContentToWrite(EolMode.Lf, "f.cs", "a\r\nb\r\n", "x\r\ny\r\n", null, false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("x\ny\n"));
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void Auto_CrlfOnlyPreImage_LfNewContent_ConvertsToCrlf()
    {
        var ok = EolWritePolicy.TryComputeContentToWrite(EolMode.Auto, "f.cs", "a\r\nb\r\n", "x\ny\n", null, false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("x\r\ny\r\n"));
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void Auto_LfOnlyPreImage_CrlfNewContent_ConvertsToLf()
    {
        var ok = EolWritePolicy.TryComputeContentToWrite(EolMode.Auto, "f.cs", "a\nb\n", "x\r\ny\r\n", null, false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("x\ny\n"));
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void Auto_MixedPreImage_EditorConfigLf_ConvertsWholeFileToLf()
    {
        var ok = EolWritePolicy.TryComputeContentToWrite(EolMode.Auto, "f.cs", "a\r\nb\nc\r\n", "x\r\ny\r\nz\n", "lf", false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("x\ny\nz\n"));
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void Auto_MixedPreImage_EditorConfigCrlf_ConvertsWholeFileToCrlf()
    {
        var ok = EolWritePolicy.TryComputeContentToWrite(EolMode.Auto, "f.cs", "a\r\nb\nc\r\n", "x\ny\nz\n", "crlf", false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("x\r\ny\r\nz\r\n"));
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void Auto_MixedPreImage_NoEditorConfig_UsesEnvironmentNewLine()
    {
        const string newContent = "x\ny\r\nz\n";
        var ok = EolWritePolicy.TryComputeContentToWrite(EolMode.Auto, "f.cs", "a\r\nb\nc\r\n", newContent, null, false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo(EolUtilities.NormalizeEol(newContent, Environment.NewLine)));
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void Auto_NewFile_EditorConfigCrlf_LfContent_ConvertsToCrlf()
    {
        var ok = EolWritePolicy.TryComputeContentToWrite(EolMode.Auto, "f.cs", null, "x\ny\n", "crlf", false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("x\r\ny\r\n"));
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void ExactRestore_MixedPreImageText_ReturnedByteForByte([Values(EolMode.Crlf, EolMode.Lf, EolMode.Auto)] EolMode mode)
    {
        const string mixed = "a\r\nb\nc\r\n";
        var ok = EolWritePolicy.TryComputeContentToWrite(mode, "f.cs", "p\nq\n", mixed, "lf", true, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo(mixed));
        Assert.That(refusal, Is.Null);
    }

    [TestCase(EolMode.Crlf)]
    [TestCase(EolMode.Auto)]
    public void NoLineBreaks_ReturnedUnchanged(EolMode mode)
    {
        var ok = EolWritePolicy.TryComputeContentToWrite(mode, "f.cs", null, "single line", null, false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("single line"));
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public void Crlf_CrOnlyNewContent_ConvergesToCrlf()
    {
        var ok = EolWritePolicy.TryComputeContentToWrite(EolMode.Crlf, "f.cs", null, "a\rb\rc", null, false, out var content, out var refusal);

        Assert.That(ok, Is.True);
        Assert.That(content, Is.EqualTo("a\r\nb\r\nc"));
        Assert.That(refusal, Is.Null);
    }
}
