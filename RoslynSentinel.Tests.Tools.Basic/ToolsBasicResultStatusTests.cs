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

    private static TestCategoryProjectOutcome MakeOutcome(string status, string? errorCode = null, string? error = null) => new(
        "P", status, 0, 0, 0, 0, 0, null, new List<string>(), errorCode, error, null, new List<TestCategorySkippedEdit>());

    [Test]
    public void TestCategoryProjectOutcome_Applied_IsSuccess()
    {
        TestCategoryProjectOutcome outcome = MakeOutcome("applied");
        IResultStatus view = outcome;

        Assert.That(outcome.IsError, Is.False);
        Assert.That(view.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(outcome.Code, Is.Null);
        Assert.That(outcome.Message, Is.Null);
    }

    [Test]
    public void TestCategoryProjectOutcome_NoChanges_IsAlreadyInState()
    {
        TestCategoryProjectOutcome outcome = MakeOutcome("no_changes");
        IResultStatus view = outcome;

        Assert.That(outcome.IsError, Is.False);
        Assert.That(view.Status, Is.EqualTo(ResultStatus.AlreadyInState));
        Assert.That(outcome.Code, Is.Null);
        Assert.That(outcome.Message, Is.Null);
    }

    [Test]
    public void TestCategoryProjectOutcome_Failed_IsFailedWithErrorCodeAndMessage()
    {
        TestCategoryProjectOutcome outcome = MakeOutcome("failed", ToolErrorCode.Exception, "boom");
        IResultStatus view = outcome;

        Assert.That(outcome.IsError, Is.True);
        Assert.That(view.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(outcome.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(outcome.Message, Is.EqualTo("boom"));
        Assert.That(outcome.Status, Is.EqualTo("failed"));
    }

    private static TestCategoryAppliedSummary MakeSummary(int failed, bool wroteFiles, params TestCategoryProjectOutcome[] projects) => new(
        null, new List<string>(), wroteFiles ? new List<FilePathWrapper> { null! } : new List<FilePathWrapper>(),
        "desc", false, true, null, 0, 0, 0, 0, failed, projects.ToList(), new List<string>());

    [Test]
    public void TestCategoryAppliedSummary_Applied_IsSuccess()
    {
        TestCategoryAppliedSummary summary = MakeSummary(0, true, MakeOutcome("applied"));
        IResultStatus view = summary;

        Assert.That(summary.Status, Is.EqualTo("applied"));
        Assert.That(view.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(summary.IsError, Is.False);
        Assert.That(summary.Code, Is.Null);
        Assert.That(summary.Message, Is.Null);
    }

    [Test]
    public void TestCategoryAppliedSummary_PartiallyApplied_IsFailedWithCountMessage()
    {
        TestCategoryAppliedSummary summary = MakeSummary(1, true, MakeOutcome("applied"), MakeOutcome("failed", null, "boom"));
        IResultStatus view = summary;

        Assert.That(summary.Status, Is.EqualTo("partially_applied"));
        Assert.That(view.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(summary.IsError, Is.True);
        Assert.That(summary.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(summary.Message, Is.EqualTo("1 of 2 failed"));
    }

    [Test]
    public void TestCategoryAppliedSummary_NoChanges_IsAlreadyInState()
    {
        TestCategoryAppliedSummary summary = MakeSummary(0, false, MakeOutcome("no_changes"));
        IResultStatus view = summary;

        Assert.That(summary.Status, Is.EqualTo("no_changes"));
        Assert.That(view.Status, Is.EqualTo(ResultStatus.AlreadyInState));
        Assert.That(summary.IsError, Is.False);
        Assert.That(summary.Code, Is.Null);
        Assert.That(summary.Message, Is.Null);
    }

    [Test]
    public void TestCategoryTypes_PublicPropertiesUnchanged_AndNewMembersJsonIgnored()
    {
        string[] outcomeNonComputed = typeof(TestCategoryProjectOutcome)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n is not ("IsError" or "Code" or "Message"))
            .ToArray();
        Assert.That(outcomeNonComputed, Is.EquivalentTo(new[]
        {
            "Project", "Status", "ClassLevelAdded", "MethodLevelAdded", "RemovedStale", "SkippedExisting", "Failed",
            "ChangeId", "AffectedFiles", "ErrorCode", "Error", "ErrorDetail", "SkippedEdits",
        }));

        string[] summaryNonComputed = typeof(TestCategoryAppliedSummary)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n is not ("IsError" or "Code" or "Message"))
            .ToArray();
        Assert.That(summaryNonComputed, Is.EquivalentTo(new[]
        {
            "ChangeId", "ChangeIds", "AffectedFiles", "Description", "DryRun", "Validated", "WorkspaceVersion",
            "ClassLevelAdded", "MethodLevelAdded", "RemovedStale", "SkippedExisting", "Failed", "Projects", "Warnings",
            "Status", "Note",
        }));

        foreach (Type type in new[] { typeof(TestCategoryProjectOutcome), typeof(TestCategoryAppliedSummary) })
        {
            foreach (string name in new[] { "IsError", "Code", "Message" })
            {
                Assert.That(type.GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, $"{type.Name}.{name}");
            }

            // Explicit IResultStatus.Status is a non-public property named after the interface.
            System.Reflection.PropertyInfo explicitStatus = type.GetProperty("RoslynSentinel.Common.IResultStatus.Status", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.That(explicitStatus, Is.Not.Null, type.Name);
            Assert.That(explicitStatus.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, $"{type.Name}.IResultStatus.Status");
        }
    }
}
