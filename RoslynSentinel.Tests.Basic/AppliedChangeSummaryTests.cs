using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class AppliedChangeSummaryTests
{
    [SetUp]
    [TearDown]
    public void ResetContext() => ToolCallContext.Set(null);

    // A real apply's ValidateAndApplyHelper detaches its written files on the call's context; the Note reads that to point at largeResult.
    private static void DetachChangedFilesOnTheCall()
    {
        var context = new ToolCallContext("agent-1", "implementer", "Member", "note test", 0);
        ToolCallContext.Set(context);
        context.Detach("changedFiles", new List<ChangedFile> { new("test.cs", "content") });
    }

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
            Validated: true);

        Assert.That(summary.ChangedContent, Is.Null);
    }

    [Test]
    public void DryRun_DropsChangedContent_KeepsLineChanges()
    {
        var filePath = new FilePathWrapper("test.cs");
        var content = new Dictionary<FilePathWrapper, string> { { filePath, "content" } };
        var lineChanges = new List<FileLineChange> { FileLineChange.Compute(filePath, "old", "content") };
        var summary = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: true,
            ChangedContent: content,
            Validated: true,
            LineChanges: lineChanges);

        Assert.That(summary.ChangedContent, Is.Null, "a dry run is compact by default: no whole-file content");
        Assert.That(summary.LineChanges, Is.EqualTo(lineChanges));
        Assert.That(summary.Status, Is.EqualTo("dry_run_ok"));
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
            Validated: false);

        Assert.That(summary.ChangedContent, Is.EqualTo(content));
    }

    [Test]
    public void RealApply_WithDetachedChangedFiles_NotePointsAtLargeResultAndTheChangedFilesPath()
    {
        DetachChangedFilesOnTheCall();
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc12345",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: null,
            Validated: true);

        // The complete call (resultId and path rendered) is asserted in Tests.Tools.Basic ConvertedToolHintTests (this project sees no tool schemas, so ToolCallHint renders the bare tool name).
        Assert.That(summary.Note, Does.Contain("GetLargeResult"));
        Assert.That(summary.Note, Does.Contain("largeResult.resultId"));
        Assert.That(summary.Note, Does.Contain("successData.changedFiles"));
    }

    [Test]
    public void RealApply_NothingDetached_NoteNamesReadFile()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc12345",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: null,
            Validated: true);

        Assert.That(summary.Note, Does.Contain("ReadFile"));
        Assert.That(summary.Note, Does.Not.Contain("GetLargeResult"));
    }

    [Test]
    public void RealApply_SerializedJsonOmitsChangedContent()
    {
        DetachChangedFilesOnTheCall();
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc12345",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: null,
            Validated: true);

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        using var doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.TryGetProperty("changedContent", out _), Is.False);
        Assert.That(doc.RootElement.TryGetProperty("ChangedContent", out _), Is.False);
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
                Validated: true);

            Assert.That(summary.ChangedContent, Is.EqualTo(content));
        }
        finally
        {
            ChangedContentOptions.InlineOnApply = originalValue;
        }
    }

    [Test]
    public void DryRun_SerializedJsonOmitsChangedContent_KeepsLineChanges()
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
            LineChanges: new List<FileLineChange> { FileLineChange.Compute(filePath, "old", "content") });

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.TryGetProperty("changedContent", out _), Is.False);
        Assert.That(doc.RootElement.TryGetProperty("lineChanges", out _), Is.True);
    }

    [Test]
    public void DryRun_WithDiff_SerializedJsonCarriesDiffNotChangedContent()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: true,
            Diff: "--- test.cs\n-old\n+content",
            ChangedContent: new Dictionary<FilePathWrapper, string> { { filePath, "content" } },
            Validated: true);

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.TryGetProperty("diff", out var diff) && diff.GetString()!.Contains("+content"), Is.True, "the diff is the preview");
        Assert.That(doc.RootElement.TryGetProperty("changedContent", out _), Is.False, "never both the diff and the whole file");
    }

    [Test]
    [NonParallelizable]
    public void DryRun_InlineOptionOn_KeepsChangedContent()
    {
        var originalValue = ChangedContentOptions.InlineOnApply;
        try
        {
            ChangedContentOptions.InlineOnApply = true;

            var filePath = new FilePathWrapper("test.cs");
            var content = new Dictionary<FilePathWrapper, string> { { filePath, "content" } };
            var summary = new AppliedChangeSummary(
                ChangeId: null,
                AffectedFiles: new List<FilePathWrapper> { filePath },
                Description: "test",
                DryRun: true,
                ChangedContent: content,
                Validated: true);

            Assert.That(summary.ChangedContent, Is.EqualTo(content));
            Assert.That(summary.Note, Does.Contain("changedContent"));
        }
        finally
        {
            ChangedContentOptions.InlineOnApply = originalValue;
        }
    }

    [Test]
    public void DryRun_WithoutDiff_NoteNamesReturnDiffAndApply()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: true,
            ChangedContent: new Dictionary<FilePathWrapper, string> { { filePath, "content" } },
            Validated: true);

        Assert.That(summary.Note, Does.Contain("returnDiff: true"));
        Assert.That(summary.Note, Does.Contain("lineChanges"));
        Assert.That(summary.Note, Does.Contain("Re-call with dryRun=false to apply."));
    }

    [Test]
    public void DryRun_WithDiff_NoteSaysDiffIsThePreview()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: true,
            Diff: "--- test.cs\n-old\n+content",
            Validated: true);

        Assert.That(summary.Note, Does.Contain("The diff is the preview."));
        Assert.That(summary.Note, Does.Not.Contain("returnDiff"));
        Assert.That(summary.Note, Does.Contain("Re-call with dryRun=false to apply."));
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
            Validated: false);

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.That(json, Does.Contain("changedContent"));
    }

    [Test]
    public void NoStage_StatusIsNotWrittenAndNoteNamesChangedContent()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: null,
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: new Dictionary<FilePathWrapper, string> { { filePath, "content" } },
            Validated: false);

        Assert.That(summary.Status, Is.EqualTo("not_written"));
        Assert.That(summary.Note, Does.Contain("autoStage"));
        Assert.That(summary.Note, Does.Contain("changedContent"));
    }

    [Test]
    public void RealApply_ValidatedFalse_ChangedContentNull_StaysApplied()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: null,
            Validated: false);

        Assert.That(summary.Status, Is.EqualTo("applied"));
    }

    [Test]
    public void RealApply_StatusStaysApplied()
    {
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: new Dictionary<FilePathWrapper, string> { { filePath, "content" } },
            Validated: true);

        Assert.That(summary.Status, Is.EqualTo("applied"));
    }

    [Test]
    public void NonValidatedSiteWithDetachedChangedFiles_NoteNamesGetLargeResult()
    {
        DetachChangedFilesOnTheCall();
        var filePath = new FilePathWrapper("test.cs");
        var summary = new AppliedChangeSummary(
            ChangeId: "abc",
            AffectedFiles: new List<FilePathWrapper> { filePath },
            Description: "test",
            DryRun: false,
            ChangedContent: null,
            Validated: false);

        // The complete call is asserted in Tests.Tools.Basic ConvertedToolHintTests (this project sees no tool schemas, so ToolCallHint renders the bare tool name).
        Assert.That(summary.Note, Does.Contain("GetLargeResult"));
        Assert.That(summary.Note, Does.Contain("successData.changedFiles"));
    }
}
