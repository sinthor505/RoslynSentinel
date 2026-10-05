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
    public static async Task<byte[]> WriteAndReadBytesAsync(string content, Encoding? encoding = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "RoslynSentinelDiskRoundTrip", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "Sample.cs");
        try
        {
            await FileIoHelper.WriteAllTextAsync(new FilePathWrapper(path), content, encoding);
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

    [Test]
    public async Task Write_BomEncodingWithBomContent_PreservesBomBytesAsync()
    {
        const string content = "class A { }\r\n";
        var bomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        var bytes = await DiskWriteRoundTrip.WriteAndReadBytesAsync(content, bomEncoding);

        // Verify the first three bytes are the UTF-8 BOM.
        Assert.That(bytes.Take(3), Is.EqualTo(Utf8Bom));
        // Verify the rest of the content is correct.
        Assert.That(Encoding.UTF8.GetString(bytes[3..]), Is.EqualTo(content));
    }

    [Test]
    public async Task Write_NoBomEncodingWithContent_DoesNotAddBomAsync()
    {
        const string content = "class A { }\r\n";
        var noBomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var bytes = await DiskWriteRoundTrip.WriteAndReadBytesAsync(content, noBomEncoding);

        // Verify no BOM is present.
        Assert.That(bytes.Take(3), Is.Not.EqualTo(Utf8Bom));
        // Verify content is correct.
        Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo(content));
    }

    [Test]
    public async Task Write_CrlfFileEditedWithLf_PreservesOriginalCrlfAsync()
    {
        const string originalContent = "class A\r\n{\r\n    void M() { }\r\n}\r\n";
        var originalBytes = Encoding.UTF8.GetBytes(originalContent);

        var dir = Path.Combine(Path.GetTempPath(), "RoslynSentinelEolRoundTrip", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "Sample.cs");
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(path, originalBytes);

            var existingContent = await File.ReadAllTextAsync(path);
            var detectedEol = EolUtilities.DetectDominantEol(existingContent);
            Assert.That(detectedEol, Is.EqualTo("\r\n"), "Original should be CRLF");

            const string newContent = "class A\n{\n    void M() { }\n    void N() { }\n}\n";
            var normalizedContent = EolUtilities.NormalizeEol(newContent, detectedEol);
            var fileWrapper = new FilePathWrapper(path);
            await FileIoHelper.WriteAllTextAsync(fileWrapper, normalizedContent);

            var writtenBytes = await File.ReadAllBytesAsync(path);
            var writtenText = Encoding.UTF8.GetString(writtenBytes);
            var finalEol = EolUtilities.DetectDominantEol(writtenText);
            Assert.That(finalEol, Is.EqualTo("\r\n"), "Written content should be CRLF");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Test]
    public async Task Write_LfFileEditedWithCrlf_PreservesOriginalLfAsync()
    {
        const string originalContent = "class A\n{\n    void M() { }\n}\n";
        var originalBytes = Encoding.UTF8.GetBytes(originalContent);

        var dir = Path.Combine(Path.GetTempPath(), "RoslynSentinelEolRoundTrip", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "Sample.cs");
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(path, originalBytes);

            var existingContent = await File.ReadAllTextAsync(path);
            var detectedEol = EolUtilities.DetectDominantEol(existingContent);
            Assert.That(detectedEol, Is.EqualTo("\n"), "Original should be LF");

            const string newContent = "class A\r\n{\r\n    void M() { }\r\n    void N() { }\r\n}\r\n";
            var normalizedContent = EolUtilities.NormalizeEol(newContent, detectedEol);
            var fileWrapper = new FilePathWrapper(path);
            await FileIoHelper.WriteAllTextAsync(fileWrapper, normalizedContent);

            var writtenBytes = await File.ReadAllBytesAsync(path);
            var writtenText = Encoding.UTF8.GetString(writtenBytes);
            var finalEol = EolUtilities.DetectDominantEol(writtenText);
            Assert.That(finalEol, Is.EqualTo("\n"), "Written content should be LF");
            Assert.That(writtenText.Contains("\r\n"), Is.False, "Should not have CRLF");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Test]
    public async Task ApplyProposedChanges_CrlfDominantFile_NormalizesSplicedLfToCrlfOnDiskAsync()
    {
        // On disk: CRLF-dominant with one stray bare LF (mixed, so EolChangeGuard does not refuse an edit that
        // keeps CRLF dominant). The proposed edit splices in a new line terminated by a bare LF, as an agent
        // typing LF would. The tracked dominant EOL (captured at LoadSolutionAsync) must be applied on write.
        const string original = "class Sample\r\n{\r\n    void Method() { }\r\n\r\n    void Other() { }\n}\r\n";
        const string proposed = "class Sample\r\n{\r\n    // Added comment\n    void Method() { }\r\n\r\n    void Other() { }\n}\r\n";

        var (result, diskBytes, tracked) = await ApplyToTrackedFileAsync("CrlfDominant.cs", Encoding.UTF8.GetBytes(original), proposed);

        Assert.That(tracked, Is.True, "Fixture file must be a loaded document, otherwise EOL tracking was never captured.");
        Assert.That(!result.IsError, Is.True, result.Summary);
        Assert.That(Encoding.UTF8.GetString(diskBytes), Does.Contain("// Added comment"), "Content should be updated");
        for (var i = 0; i < diskBytes.Length; i++)
        {
            if (diskBytes[i] == 0x0A)
            {
                Assert.That(i > 0 && diskBytes[i - 1] == 0x0D, Is.True, $"Bare LF at byte {i}; every LF must be preceded by CR in a CRLF-dominant file.");
            }
        }

        var expected = Encoding.UTF8.GetBytes(proposed.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.That(diskBytes, Is.EqualTo(expected));
    }

    [Test]
    public async Task ApplyProposedChanges_LfDominantFile_NormalizesSplicedCrlfToLfOnDiskAsync()
    {
        // On disk: LF-dominant with one stray CRLF (mixed). The edit splices in a CRLF-terminated line; the
        // tracked dominant EOL (LF) must be applied on write, so no 0x0D remains on disk.
        const string original = "class Sample\n{\n    void Method() { }\n\n    void Other() { }\r\n}\n";
        const string proposed = "class Sample\n{\n    // Added comment\r\n    void Method() { }\n\n    void Other() { }\r\n}\n";

        var (result, diskBytes, tracked) = await ApplyToTrackedFileAsync("LfDominant.cs", Encoding.UTF8.GetBytes(original), proposed);

        Assert.That(tracked, Is.True, "Fixture file must be a loaded document, otherwise EOL tracking was never captured.");
        Assert.That(!result.IsError, Is.True, result.Summary);
        Assert.That(Encoding.UTF8.GetString(diskBytes), Does.Contain("// Added comment"), "Content should be updated");
        Assert.That(diskBytes, Does.Not.Contain((byte)0x0D), "No CR may remain in an LF-dominant file.");

        var expected = Encoding.UTF8.GetBytes(proposed.Replace("\r\n", "\n"));
        Assert.That(diskBytes, Is.EqualTo(expected));
    }

    [Test]
    public async Task ApplyProposedChanges_SingleStyleCrlfFileProposedWithLf_IsRefusedByEolGuardAndLeavesDiskUntouchedAsync()
    {
        // Documents the layering: for a single-style .cs file, EolChangeGuard refuses a style change before the
        // write loop's EOL normalization is ever reached, so that normalization only matters for mixed-EOL files.
        const string original = "class Sample\r\n{\r\n    void Method() { }\r\n}\r\n";
        const string proposed = "class Sample\n{\n    // Added comment\n    void Method() { }\n}\n";
        var originalBytes = Encoding.UTF8.GetBytes(original);

        var (result, diskBytes, tracked) = await ApplyToTrackedFileAsync("CrlfSingleStyle.cs", originalBytes, proposed);

        Assert.That(tracked, Is.True);
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.EolChangeRefused));
        Assert.That(diskBytes, Is.EqualTo(originalBytes), "A refused change must not touch the file.");
    }

    [Test]
    public async Task ApplyProposedChanges_BomFile_PreservesBomOnDiskAsync()
    {
        // Proposed content is a plain string (no BOM) and ReadAllText strips the BOM on load, so only the
        // tracked BOM flag can make the write emit EF BB BF again.
        const string original = "class Sample\r\n{\r\n    void Method() { }\r\n}\r\n";
        const string proposed = "class Sample\r\n{\r\n    // Added comment\r\n    void Method() { }\r\n}\r\n";
        var originalBytes = Utf8Bom.Concat(Encoding.UTF8.GetBytes(original)).ToArray();

        var (result, diskBytes, tracked) = await ApplyToTrackedFileAsync("WithBom.cs", originalBytes, proposed);

        Assert.That(tracked, Is.True, "Fixture file must be a loaded document, otherwise BOM tracking was never captured.");
        Assert.That(!result.IsError, Is.True, result.Summary);
        Assert.That(diskBytes.Take(3), Is.EqualTo(Utf8Bom), "UTF-8 BOM must survive the edit.");
        Assert.That(diskBytes, Is.EqualTo(Utf8Bom.Concat(Encoding.UTF8.GetBytes(proposed)).ToArray()), "Exactly one BOM followed by the updated content.");
    }

    [Test]
    public async Task ApplyProposedChanges_NoBomFile_DoesNotGainBomAsync()
    {
        const string original = "class Sample\r\n{\r\n    void Method() { }\r\n}\r\n";
        const string proposed = "class Sample\r\n{\r\n    // Added comment\r\n    void Method() { }\r\n}\r\n";

        var (result, diskBytes, tracked) = await ApplyToTrackedFileAsync("NoBom.cs", Encoding.UTF8.GetBytes(original), proposed);

        Assert.That(tracked, Is.True);
        Assert.That(!result.IsError, Is.True, result.Summary);
        Assert.That(diskBytes.Take(3), Is.Not.EqualTo(Utf8Bom));
        Assert.That(diskBytes, Is.EqualTo(Encoding.UTF8.GetBytes(proposed)));
    }

    [Test]
    public async Task ApplyProposedChanges_NewFileNoTracking_CreatesWithoutThrowAsync()
    {
        // Test that a new file (not previously tracked) can be created through ApplyProposedChangesAsync.
        var fixture = new TestSolutionFixture();
        try
        {
            var workspaceManager = new PersistentWorkspaceManager(new Microsoft.Extensions.Logging.Abstractions.NullLogger<IWorkspaceManager>());
            await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

            const string newContent = "namespace NewNamespace;\n\npublic class NewClass\n{\n}\n";
            var filePath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core/NewFile.cs");

            var fpw = new FilePathWrapper(filePath);
            var changes = new Dictionary<FilePathWrapper, string> { [fpw] = newContent };

            var result = await workspaceManager.ApplyProposedChangesAsync(changes);
            Assert.That(!result.IsError, Is.True, result.Summary);

            // Verify file exists and content is correct.
            Assert.That(File.Exists(filePath), Is.True, "New file should exist on disk");
            var written = await File.ReadAllTextAsync(filePath);
            Assert.That(written, Is.EqualTo(newContent));

            workspaceManager.Dispose();
        }
        finally
        {
            fixture.Dispose();
        }
    }

    /// <summary>
    /// Writes <paramref name="initialDiskBytes"/> into the copied sample project BEFORE loading the solution (BOM and
    /// EOL tracking are captured at <c>LoadSolutionAsync</c>), loads it, applies one proposed change through the
    /// chokepoint, and returns the result, the raw bytes now on disk, and whether the file was a loaded document.
    /// </summary>
    private static async Task<(ApplyChangesResult Result, byte[] DiskBytes, bool Tracked)> ApplyToTrackedFileAsync(
        string fileName, byte[] initialDiskBytes, string proposedContent)
    {
        var fixture = new TestSolutionFixture();
        try
        {
            var filePath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", fileName);
            await File.WriteAllBytesAsync(filePath, initialDiskBytes);

            var workspaceManager = new PersistentWorkspaceManager(new Microsoft.Extensions.Logging.Abstractions.NullLogger<IWorkspaceManager>());
            try
            {
                await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
                var tracked = workspaceManager.CurrentSolution!.Projects
                    .SelectMany(p => p.Documents)
                    .Any(d => string.Equals(Path.GetFileName(d.FilePath), fileName, StringComparison.OrdinalIgnoreCase));

                var changes = new Dictionary<FilePathWrapper, string> { [new FilePathWrapper(filePath)] = proposedContent };
                var result = await workspaceManager.ApplyProposedChangesAsync(changes);
                return (result, await File.ReadAllBytesAsync(filePath), tracked);
            }
            finally
            {
                workspaceManager.Dispose();
            }
        }
        finally
        {
            fixture.Dispose();
        }
    }
}
