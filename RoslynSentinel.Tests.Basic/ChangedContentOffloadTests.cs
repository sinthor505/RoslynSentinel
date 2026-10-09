using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class ChangedContentOffloadTests
{
    [Test]
    public async Task StoreChangedContentAsync_WritesRawLargeResultFile_AndReturnsId()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var changes = new Dictionary<FilePathWrapper, string>
            {
                { new FilePathWrapper("path/to/file1.cs", validated: true), "content1" },
                { new FilePathWrapper("path/to/file2.cs", validated: true), "content2" }
            };

            var resultId = await ValidateAndApplyHelper.StoreChangedContentAsync(
                changes,
                tempDir,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.That(resultId, Is.Not.Null);
            Assert.That(resultId, Is.Not.Empty);

            var largeResultsDir = Path.Combine(tempDir, ".roslynsentinel", "largeresults");
            Assert.That(Directory.Exists(largeResultsDir), Is.True, "Large results directory should exist");

            var files = Directory.GetFiles(largeResultsDir, "largeresult_*.json");
            Assert.That(files.Length, Is.EqualTo(1), "Should create exactly one large result file");

            var jsonContent = await File.ReadAllTextAsync(files[0]);
            
            Assert.That(jsonContent, Does.Contain("\"Type\""));
            Assert.That(jsonContent, Does.Contain("\"Raw\""));
            Assert.That(jsonContent, Does.Contain("\"Data\""));
            Assert.That(jsonContent, Does.Contain("content1"));
            Assert.That(jsonContent, Does.Contain("content2"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task StoreChangedContentAsync_NoSolutionRoot_ReturnsNull()
    {
        var changes = new Dictionary<FilePathWrapper, string>
        {
            { new FilePathWrapper("path/to/file1.cs", validated: true), "content1" }
        };

        var resultId = await ValidateAndApplyHelper.StoreChangedContentAsync(
            changes,
            null,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.That(resultId, Is.Null);
    }

    [Test]
    public async Task StoreChangedContentAsync_EmptyChanges_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        try
        {
            var changes = new Dictionary<FilePathWrapper, string>();

            var resultId = await ValidateAndApplyHelper.StoreChangedContentAsync(
                changes,
                tempDir,
                NullLogger.Instance,
                CancellationToken.None);

            Assert.That(resultId, Is.Null);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
