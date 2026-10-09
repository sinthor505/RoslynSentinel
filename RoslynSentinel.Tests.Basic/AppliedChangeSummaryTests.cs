using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class AppliedChangeSummaryTests
{
    [Test]
    public void RealApply_DropsChangedContent()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc12345",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: new Dictionary<FilePathWrapper, string> { { filePath, "content" } },
            Validated: true,
            ChangedContentResultId: null);

        Assert.That(summary.ChangedContent, Is.Null);
    }

    [Test]
    public void DryRun_KeepsChangedContent()
    {
        var filePath = new FilePathWrapper("test.cs");
        var content = new Dictionary<FilePathWrapper, string> { { filePath, "content" } };
        var summary = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: true,
            ChangedContent: content,
            Validated: true,
            ChangedContentResultId: null);

        Assert.That(summary.ChangedContent, Is.EqualTo(content));
    }

    [Test]
    public void NoStage_KeepsChangedContent()
    {
        var filePath = new FilePathWrapper("test.cs");
        var content = new Dictionary<FilePathWrapper, string> { { filePath, "content" } };
        var summary = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: content,
            Validated: false,
            ChangedContentResultId: null);

        Assert.That(summary.ChangedContent, Is.EqualTo(content));
    }

    [Test]
    public void RealApply_WithResultId_NoteNamesGetLargeResultAndResultId()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc12345",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: null,
            Validated: true,
            ChangedContentResultId: "resultId123");

        Assert.That(summary.Note, Does.Contain("GetLargeResult"));
        Assert.That(summary.Note, Does.Contain("resultId123"));
    }

    [Test]
    public void RealApply_WithoutResultId_NoteNamesReadFile()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc12345",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: null,
            Validated: true,
            ChangedContentResultId: null);

        Assert.That(summary.Note, Does.Contain("ReadFile"));
        Assert.That(summary.Note, Does.Not.Contain("GetLargeResult"));
    }

    [Test]
    public void RealApply_SerializedJsonOmitsChangedContentAndCarriesResultId()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc12345",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: null,
            Validated: true,
            ChangedContentResultId: "resultId123");

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.TryGetProperty("changedContent", out _), Is.False);
        Assert.That(doc.RootElement.TryGetProperty("ChangedContent", out _), Is.False);
        Assert.That(json, Does.Contain("resultId123"));
        Assert.That(json, Does.Contain("changedContentResultId"));
    }

    [Test]
    [NonParallelizable]
    public void RealApply_InlineOptionOn_KeepsChangedContent()
    {
        var originalValue = ChangedContentOptions.InlineOnApply;
        try
        {
            ChangedContentOptions.InlineOnApply = true;

            var filePath = new FilePathWrapper("test.cs");
            var content = new Dictionary<FilePathWrapper, string> { { filePath, "content" } };
            var summary = new AppliedChangeSummary(
                ChangeId: "abc12345",
                AffectedFiles: new List<FilePathWrapper> { filePath },
                Description: "test",
                DryRun: false,
                ChangedContent: content,
                Validated: true,
                ChangedContentResultId: null);

            Assert.That(summary.ChangedContent, Is.EqualTo(content));
        }
        finally
        {
            ChangedContentOptions.InlineOnApply = originalValue;
        }
    }

    [Test]
    public void DryRun_SerializedJsonKeepsChangedContent()
    {
        var filePath = new FilePathWrapper("test.cs");
        var content = new Dictionary<FilePathWrapper, string> { { filePath, "content" } };
        var summary = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: true,
            ChangedContent: content,
            Validated: true,
            ChangedContentResultId: null);

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.That(json, Does.Contain("changedContent"));
    }

    [Test]
    public void NoStage_SerializedJsonKeepsChangedContent()
    {
        var filePath = new FilePathWrapper("test.cs");
        var content = new Dictionary<FilePathWrapper, string> { { filePath, "content" } };
        var summary = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: content,
            Validated: false,
            ChangedContentResultId: null);

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.That(json, Does.Contain("changedContent"));
    }
}
