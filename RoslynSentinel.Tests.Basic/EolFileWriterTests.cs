using System.Linq;
using System.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[NonParallelizable]
public class EolFileWriterTests
{
    private EolMode _originalMode;
    private string _dir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _originalMode = EolPolicyOptions.Mode;
        _dir = Path.Combine(Path.GetTempPath(), "eolwriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        // EolPolicyOptions is static: restore the mode the process started with, not just Auto.
        EolPolicyOptions.Configure(_originalMode switch
        {
            EolMode.Crlf => new[] { "--eol=crlf" },
            EolMode.Lf => new[] { "--eol=lf" },
            _ => System.Array.Empty<string>(),
        });
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Test]
    public async Task WriteAllTextResolvedAsync_UnderCrlf_NewLfContent_IsWrittenAsUniformCrlf()
    {
        EolPolicyOptions.Configure(new[] { "--eol=crlf" });
        var path = Path.Combine(_dir, "new.cs");

        var refusal = await EolFileWriter.WriteAllTextResolvedAsync(path, "a\nb\n", preImage: null);

        Assert.That(refusal, Is.Null);
        AssertUniformCrlf(File.ReadAllBytes(path));
    }

    [Test]
    public async Task WriteAllTextResolvedAsync_UnderLf_CrlfContent_IsWrittenWithoutCr()
    {
        EolPolicyOptions.Configure(new[] { "--eol=lf" });
        var path = Path.Combine(_dir, "new.cs");

        var refusal = await EolFileWriter.WriteAllTextResolvedAsync(path, "a\r\nb\r\n", preImage: null);

        Assert.That(refusal, Is.Null);
        Assert.That(File.ReadAllBytes(path), Does.Not.Contain((byte)'\r'));
        Assert.That(File.ReadAllText(path), Is.EqualTo("a\nb\n"));
    }

    [Test]
    public async Task WriteAllTextResolvedAsync_ExistingBomFile_KeepsBom()
    {
        EolPolicyOptions.Configure(new[] { "--eol=crlf" });
        var path = Path.Combine(_dir, "bom.cs");
        File.WriteAllText(path, "a\nb\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var preImage = File.ReadAllText(path);

        var refusal = await EolFileWriter.WriteAllTextResolvedAsync(path, "x\ny\n", preImage);

        Assert.That(refusal, Is.Null);
        var bytes = File.ReadAllBytes(path);
        Assert.That(bytes[..3], Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        AssertUniformCrlf(bytes);
    }

    [Test]
    public async Task NormalizeNewFileAsync_UnderCrlf_RewritesLfFileAsUniformCrlf()
    {
        EolPolicyOptions.Configure(new[] { "--eol=crlf" });
        var path = Path.Combine(_dir, "created.cs");
        File.WriteAllText(path, "a\nb\n");

        var refusal = await EolFileWriter.NormalizeNewFileAsync(path);

        Assert.That(refusal, Is.Null);
        AssertUniformCrlf(File.ReadAllBytes(path));
    }

    [Test]
    public async Task NormalizeNewFileAsync_AlreadyCrlf_LeavesBytesUnchanged()
    {
        EolPolicyOptions.Configure(new[] { "--eol=crlf" });
        var path = Path.Combine(_dir, "created.cs");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("a\r\nb\r\n"));
        var before = File.ReadAllBytes(path);

        var refusal = await EolFileWriter.NormalizeNewFileAsync(path);

        Assert.That(refusal, Is.Null);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    private static void AssertUniformCrlf(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var crlfCount = text.Split("\r\n").Length - 1;
        var lfCount = text.Count(c => c == '\n');
        Assert.That(crlfCount, Is.GreaterThan(0));
        Assert.That(lfCount, Is.EqualTo(crlfCount), "every LF must be part of a CRLF");
        Assert.That(text.Replace("\r\n", string.Empty).Contains('\r'), Is.False, "no bare CR");
    }
}
