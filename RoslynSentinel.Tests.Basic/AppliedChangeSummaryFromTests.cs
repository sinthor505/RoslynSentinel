using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// The single build point for <see cref="AppliedChangeSummary"/>: <c>From(ApplyOutcome, ...)</c> copies the outcome's fields so no site
/// forgets LineChanges/Findings, <c>From(lineChanges, findings, ...)</c> is the preview-only form, and
/// <see cref="ValidateAndApplyHelper.ComputeLineChangesAsync"/> supplies the preview-only line counts.
/// </summary>
[TestFixture]
public class AppliedChangeSummaryFromTests
{
    private static readonly FilePathWrapper File = new("test.cs");

    private static List<FileLineChange> SampleLineChanges() => [FileLineChange.Compute(File, "old", "new")];

    private static List<Finding> SampleFindings() => [new Finding("Member", "something to know", FindingSeverity.Caution)];

    [Test]
    public void FromApplyOutcome_CopiesChangeIdDryRunDiffLineChangesAndFindings()
    {
        var lineChanges = SampleLineChanges();
        var findings = SampleFindings();
        var apply = new ApplyOutcome("abc12345", null, false, "the diff", LineChanges: lineChanges, Findings: findings);

        var summary = AppliedChangeSummary.From(apply, [File], "described");

        Assert.That(summary.ChangeId, Is.EqualTo("abc12345"));
        Assert.That(summary.DryRun, Is.False);
        Assert.That(summary.Diff, Is.EqualTo("the diff"));
        Assert.That(summary.LineChanges, Is.SameAs(lineChanges));
        Assert.That(summary.Findings, Is.SameAs(findings));
        Assert.That(summary.AffectedFiles, Is.EqualTo(new List<FilePathWrapper> { File }));
        Assert.That(summary.Description, Is.EqualTo("described"));
        Assert.That(summary.Validated, Is.True, "the apply went through the compile gate, so validated defaults to true");
        Assert.That(summary.WorkspaceVersion, Is.Null);
        Assert.That(summary.EditSpans, Is.Null);
    }

    [Test]
    public void FromApplyOutcome_DryRunWithLineChanges_KeepsLineChangesAndIsDryRunOk()
    {
        var lineChanges = SampleLineChanges();
        var apply = new ApplyOutcome(null, null, true, LineChanges: lineChanges);

        var summary = AppliedChangeSummary.From(apply, [File], "described", changedContent: new Dictionary<FilePathWrapper, string> { { File, "new" } });

        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.LineChanges, Is.Not.Null);
        Assert.That(summary.LineChanges, Is.SameAs(lineChanges));
        Assert.That(summary.Status, Is.EqualTo("dry_run_ok"));
        Assert.That(summary.ChangedContent, Is.Null, "a validated dry run is compact: lineChanges, not the whole file");
    }

    [Test]
    public void FromApplyOutcome_ChangedContentAndValidatedFalse_PassThrough()
    {
        var content = new Dictionary<FilePathWrapper, string> { { File, "new" } };
        var apply = new ApplyOutcome("abc", null, false);

        var summary = AppliedChangeSummary.From(apply, [File], "described", changedContent: content, validated: false);

        Assert.That(summary.ChangedContent, Is.SameAs(content), "validated: false keeps the echoed content");
        Assert.That(summary.Validated, Is.False);
    }

    [Test]
    public void FromApplyOutcome_ValidatedRealApply_DropsChangedContentCentrally()
    {
        var apply = new ApplyOutcome("abc", null, false);

        var summary = AppliedChangeSummary.From(apply, [File], "described", changedContent: new Dictionary<FilePathWrapper, string> { { File, "new" } });

        Assert.That(summary.ChangedContent, Is.Null);
        Assert.That(summary.Validated, Is.True);
        Assert.That(summary.Status, Is.EqualTo("applied"));
    }

    [Test]
    public void FromApplyOutcome_WorkspaceVersionAndEditSpans_PassThrough()
    {
        var spans = new List<AppliedEditSpan> { new("test.cs", 0, 3, "new") };
        var apply = new ApplyOutcome(null, null, true);

        var summary = AppliedChangeSummary.From(apply, [File], "described", workspaceVersion: 7, editSpans: spans);

        Assert.That(summary.WorkspaceVersion, Is.EqualTo(7));
        Assert.That(summary.EditSpans, Is.SameAs(spans));
    }

    [Test]
    public void FromPreview_HasNullChangeIdAndCopiesLineChangesFindingsDiffAndContent()
    {
        var lineChanges = SampleLineChanges();
        var findings = SampleFindings();
        var content = new Dictionary<FilePathWrapper, string> { { File, "new" } };

        var summary = AppliedChangeSummary.From(lineChanges, findings, [File], "described", diff: "the diff", changedContent: content);

        Assert.That(summary.ChangeId, Is.Null);
        Assert.That(summary.DryRun, Is.False);
        Assert.That(summary.Validated, Is.False);
        Assert.That(summary.Diff, Is.EqualTo("the diff"));
        Assert.That(summary.LineChanges, Is.SameAs(lineChanges));
        Assert.That(summary.Findings, Is.SameAs(findings));
        Assert.That(summary.ChangedContent, Is.SameAs(content));
        Assert.That(summary.Status, Is.EqualTo("not_written"));
    }

    [Test]
    public void FromPreview_DryRunAndValidatedPassThrough()
    {
        var summary = AppliedChangeSummary.From(null, null, [File], "described", dryRun: true, validated: true);

        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Validated, Is.True);
        Assert.That(summary.ChangeId, Is.Null);
        Assert.That(summary.Status, Is.EqualTo("dry_run_ok"));
    }

    [Test]
    public void FromPreview_NoOpWithNullLineChangesAndNoFiles_IsNoChanges()
    {
        var summary = AppliedChangeSummary.From(lineChanges: null, findings: null, affectedFiles: [], description: "nothing to do");

        Assert.That(summary.ChangeId, Is.Null);
        Assert.That(summary.LineChanges, Is.Null);
        Assert.That(summary.Findings, Is.Null);
        Assert.That(summary.AffectedFiles, Is.Empty);
        Assert.That(summary.Status, Is.EqualTo("no_changes"));
    }

    [Test]
    public async Task ComputeLineChangesAsync_ModifiedFileAndNewFile_ReportsCountsAgainstTheWorkspaceText()
    {
        using var workspace = InMemoryWorkspace.Create(("Existing.cs", "line1\r\nline2\r\n"));
        var existing = new FilePathWrapper(workspace.PathOf("Existing.cs"));
        var brand = new FilePathWrapper(workspace.PathOf("Brand.cs"));
        var changes = new Dictionary<FilePathWrapper, string>
        {
            { existing, "line1\r\nline2\r\nline3\r\n" },
            { brand, "a\r\nb\r\n" },
        };

        var lineChanges = await ValidateAndApplyHelper.ComputeLineChangesAsync(workspace.Manager, changes, CancellationToken.None);

        Assert.That(lineChanges, Has.Count.EqualTo(2));
        var existingChange = lineChanges.Single(c => c.FilePath == (string)existing);
        Assert.That(existingChange.LinesAdded, Is.EqualTo(1));
        Assert.That(existingChange.LinesRemoved, Is.EqualTo(0));
        var brandChange = lineChanges.Single(c => c.FilePath == (string)brand);
        Assert.That(brandChange.LinesAdded, Is.EqualTo(2), "a path that is neither a workspace document nor on disk is a new file: every line is added");
        Assert.That(brandChange.LinesRemoved, Is.EqualTo(0));
    }

    [Test]
    public async Task ComputeLineChangesAsync_NoChanges_ReturnsEmpty()
    {
        using var workspace = InMemoryWorkspace.Create(("Existing.cs", "line1\r\n"));

        var lineChanges = await ValidateAndApplyHelper.ComputeLineChangesAsync(workspace.Manager, [], CancellationToken.None);

        Assert.That(lineChanges, Is.Empty);
    }
}
