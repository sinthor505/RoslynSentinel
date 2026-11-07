using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// proposal_side_stores_never_inline.md slice 4: a real apply no longer stores its changed content in a Raw large-result file;
/// ValidateAndApplyHelper.DetachChangedFiles declares it on the call's ToolCallContext as the "changedFiles" payload and the
/// result marshaller stores it (asserted end to end in Tests.Server ToolResultMarshallerDetachedTests).
/// </summary>
[TestFixture]
public class ChangedContentOffloadTests
{
    [SetUp]
    [TearDown]
    public void ResetContext() => ToolCallContext.Set(null);

    private static ToolCallContext NewContext()
    {
        var context = new ToolCallContext("agent-1", "implementer", "Member", "detach changed files", 0);
        ToolCallContext.Set(context);
        return context;
    }

    [Test]
    public void DetachChangedFiles_DetachesOnePathAndTextRowPerFile_UnderChangedFiles()
    {
        var context = NewContext();
        var changes = new Dictionary<FilePathWrapper, string>
        {
            { new FilePathWrapper("path/to/file1.cs", validated: true), "content1" },
            { new FilePathWrapper("path/to/file2.cs", validated: true), "content2" }
        };

        ValidateAndApplyHelper.DetachChangedFiles(changes);

        var detached = context.Detached;
        Assert.That(detached, Has.Count.EqualTo(1));
        Assert.That(detached[0].Name, Is.EqualTo("changedFiles"));
        var rows = detached[0].Payload as List<ChangedFile>;
        Assert.That(rows, Is.Not.Null, "a list, so GetLargeResult can page it by path");
        Assert.That(rows!.Select(r => (r.Path, r.Text)), Is.EquivalentTo(new[]
        {
            ((string)new FilePathWrapper("path/to/file1.cs", validated: true), "content1"),
            ((string)new FilePathWrapper("path/to/file2.cs", validated: true), "content2"),
        }), "the path is the same wire form the ChangedContent dictionary key had");
    }

    [Test]
    public void DetachChangedFiles_SecondBatchInTheSameCall_AppendsToTheSamePayload()
    {
        // QualifyCrefs and the test-category apply run ValidateAndApplyAsync once per project in one tool call.
        var context = NewContext();

        ValidateAndApplyHelper.DetachChangedFiles(new Dictionary<FilePathWrapper, string> { { new FilePathWrapper("a/one.cs", validated: true), "one" } });
        Assert.DoesNotThrow(() => ValidateAndApplyHelper.DetachChangedFiles(new Dictionary<FilePathWrapper, string> { { new FilePathWrapper("b/two.cs", validated: true), "two" } }));

        Assert.That(context.Detached, Has.Count.EqualTo(1));
        var rows = (List<ChangedFile>)context.Detached[0].Payload;
        Assert.That(rows.Select(r => r.Text), Is.EqualTo(new[] { "one", "two" }));
    }

    [Test]
    public void DetachChangedFiles_NoToolCallContext_IsANoOp()
    {
        var changes = new Dictionary<FilePathWrapper, string>
        {
            { new FilePathWrapper("path/to/file1.cs", validated: true), "content1" }
        };

        Assert.DoesNotThrow(() => ValidateAndApplyHelper.DetachChangedFiles(changes));
        Assert.That(ToolCallContext.Current, Is.Null);
    }

    [Test]
    public void DetachChangedFiles_EmptyChanges_DetachesNothing()
    {
        var context = NewContext();

        ValidateAndApplyHelper.DetachChangedFiles(new Dictionary<FilePathWrapper, string>());

        Assert.That(context.Detached, Is.Empty);
    }
}
