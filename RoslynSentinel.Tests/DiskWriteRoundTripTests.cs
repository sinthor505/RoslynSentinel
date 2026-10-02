using System.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

/// <summary>
/// Write-fidelity helper: pushes text through <see cref="FileIoHelper"/> (the chokepoint every .cs write goes through)
/// into a throwaway temp file and returns the raw bytes, with no workspace, MSBuild load or tool involved. Engine tests
/// run on <c>InMemoryWorkspace</c> and trust this layer; the byte-level guarantees live here.
/// </summary>
public static class DiskWriteRoundTrip
{
    public static async Task<byte[]> WriteAndReadBytesAsync(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "RoslynSentinelDiskRoundTrip", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "Sample.cs");
        try
        {
            await FileIoHelper.WriteAllTextAsync(new FilePathWrapper(path), content);
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class DiskWriteRoundTripTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [Test]
    public async Task Write_CrlfContent_IsPreservedByteForByteAsync()
    {
        const string content = "class A\r\n{\r\n}\r\n";

        var bytes = await DiskWriteRoundTrip.WriteAndReadBytesAsync(content);

        Assert.That(bytes, Is.EqualTo(Encoding.UTF8.GetBytes(content)));
    }

    [Test]
    public async Task Write_LfContent_IsNotConvertedToCrlfAsync()
    {
        const string content = "class A\n{\n}\n";

        var bytes = await DiskWriteRoundTrip.WriteAndReadBytesAsync(content);

        Assert.That(bytes, Is.EqualTo(Encoding.UTF8.GetBytes(content)));
        Assert.That(bytes, Does.Not.Contain((byte)'\r'));
    }

    [Test]
    public async Task Write_MixedLineEndings_AreNotNormalizedAsync()
    {
        const string content = "a\r\nb\nc\r\n";

        var bytes = await DiskWriteRoundTrip.WriteAndReadBytesAsync(content);

        Assert.That(bytes, Is.EqualTo(Encoding.UTF8.GetBytes(content)));
    }

    [Test]
    public async Task Write_ContentWithoutBom_DoesNotAddOneAsync()
    {
        var bytes = await DiskWriteRoundTrip.WriteAndReadBytesAsync("class A { }");

        Assert.That(bytes.Take(3), Is.Not.EqualTo(Utf8Bom));
    }

    [Test]
    public async Task Write_NonAsciiContent_RoundTripsAsUtf8Async()
    {
        const string content = "// café → ü\r\nclass A { }\r\n";

        var bytes = await DiskWriteRoundTrip.WriteAndReadBytesAsync(content);

        Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo(content));
    }
}
