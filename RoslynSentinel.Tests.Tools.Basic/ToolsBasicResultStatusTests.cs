using System.Text.Json;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("ResultStatus")]
public class ToolsBasicResultStatusTests
{
    [Test]
    public void GitResult_Success_IsSuccessWithNoCode()
    {
        IResultStatus ok = new GitResult { IsError = false };

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
    }

    [TestCase(GitErrorCodes.PathNotFound, ResultStatus.NotFound)]
    [TestCase(GitErrorCodes.TargetNotFound, ResultStatus.NotFound)]
    [TestCase(GitErrorCodes.RefRequired, ResultStatus.InvalidInput)]
    [TestCase(GitErrorCodes.ActionRequired, ResultStatus.InvalidInput)]
    [TestCase(GitErrorCodes.InvalidName, ResultStatus.InvalidInput)]
    [TestCase(GitErrorCodes.HunkSelection, ResultStatus.InvalidInput)]
    [TestCase(GitErrorCodes.HunkStale, ResultStatus.InvalidInput)]
    [TestCase(GitErrorCodes.IgnoredPath, ResultStatus.NotApplicable)]
    [TestCase(GitErrorCodes.NothingToCommit, ResultStatus.NotApplicable)]
    [TestCase(GitErrorCodes.NoChanges, ResultStatus.NotApplicable)]
    [TestCase(GitErrorCodes.OperationInProgress, ResultStatus.NotApplicable)]
    [TestCase(GitErrorCodes.AlreadyExists, ResultStatus.NotApplicable)]
    [TestCase(GitErrorCodes.WorkingTreeDirty, ResultStatus.NotApplicable)]
    [TestCase(GitErrorCodes.StashConflict, ResultStatus.NotApplicable)]
    [TestCase(GitErrorCodes.Refused, ResultStatus.NotApplicable)]
    [TestCase(GitErrorCodes.HunkUnsupported, ResultStatus.NotApplicable)]
    [TestCase(GitErrorCodes.Fallback, ResultStatus.Failed)]
    public void GitResult_ClassifiedFailure_MapsCodeToStatus_AndStaysAnError(string kind, ResultStatus expected)
    {
        IResultStatus bad = new GitResult { IsError = true, Error = "refused", ErrorKind = kind };

        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(expected));
        Assert.That(bad.Status.IsErrorStatus(), Is.True);
        Assert.That(bad.Code, Is.EqualTo(kind));
        Assert.That(bad.Message, Is.EqualTo("refused"));
    }

    [Test]
    public void GitResult_UnclassifiedFailure_UsesFallbackCode_AndFailed()
    {
        IResultStatus bad = new GitResult { IsError = true, Error = "boom" };

        Assert.That(bad.Code, Is.EqualTo(GitErrorCodes.Fallback));
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
    }

    [Test]
    public void GitResult_Serialization_DoesNotGainStatusFields()
    {
        string json = JsonSerializer.Serialize(new GitResult { IsError = true, Error = "x", ErrorKind = GitErrorCodes.Refused });

        Assert.That(json, Does.Not.Contain("\"Status\"").And.Not.Contain("\"Code\"").And.Not.Contain("\"Message\""));
    }

    [Test]
    public void DocWriteResult_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new DocWriteResult { IsError = false, Filename = "a.md" };
        IResultStatus bad = new DocWriteResult { IsError = true, Error = "disk full" };

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("disk full"));

        string json = JsonSerializer.Serialize((DocWriteResult)bad);
        Assert.That(json, Does.Not.Contain("\"Status\"").And.Not.Contain("\"Code\"").And.Not.Contain("\"Message\""));
    }

    [Test]
    public void DocReadResult_FoundNotFoundAndError_StatusAgreesWithFlagAndKeepsDeclaredProperties()
    {
        IResultStatus found = new DocReadResult { Found = true, Filename = "a.md", Content = "x", Warning = "fallback used" };
        IResultStatus notFound = new DocReadResult { Found = false, Filename = "a.md" };
        IResultStatus bad = new DocReadResult { Found = true, Error = "boom" };

        // A Warning alone does not make the result an error.
        Assert.That(found.IsError, Is.False);
        Assert.That(found.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(found.Code, Is.Null);
        Assert.That(found.Message, Is.Null);
        Assert.That(notFound.IsError, Is.True);
        Assert.That(notFound.Status, Is.EqualTo(ResultStatus.NotFound));
        Assert.That(notFound.Code, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(notFound.Message, Is.EqualTo("Document not found"));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((DocReadResult)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Is.EquivalentTo(new[] { "Found", "Filename", "Content", "Error", "Warning" }));
    }

    [Test]
    public void DocListResult_SuccessAndError_StatusAgreesWithError_AndKeepsDeclaredProperties()
    {
        IResultStatus ok = new DocListResult { Files = ["a.md"], Count = 1 };
        IResultStatus bad = new DocListResult { Error = "boom" };

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((DocListResult)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Is.EquivalentTo(new[] { "Files", "Count", "Error" }));
    }
}
