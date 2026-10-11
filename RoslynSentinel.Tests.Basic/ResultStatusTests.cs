using System.Text.Json;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class ResultStatusTests
{
    [Test]
    public void Envelope_Success_IsNotError_AndStatusIsSuccess()
    {
        IResultStatus result = new SentinelCallToolResult<string> { IsError = false, SuccessData = "x" };

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [TestCase(ToolErrorCode.NotFound, ResultStatus.NotFound)]
    [TestCase(ToolErrorCode.NoMatches, ResultStatus.NotFound)]
    [TestCase(ToolErrorCode.Ambiguous, ResultStatus.Ambiguous)]
    [TestCase(ToolErrorCode.InvalidArgument, ResultStatus.InvalidInput)]
    [TestCase(ToolErrorCode.TargetIneligible, ResultStatus.NotApplicable)]
    [TestCase(ToolErrorCode.FeatureDisabled, ResultStatus.FeatureDisabled)]
    [TestCase(ToolErrorCode.Cancelled, ResultStatus.Cancelled)]
    [TestCase(ToolErrorCode.TimedOut, ResultStatus.TimedOut)]
    [TestCase(ToolErrorCode.SolutionLoadTimeout, ResultStatus.TimedOut)]
    [TestCase(ToolErrorCode.Exception, ResultStatus.Failed)]
    [TestCase("SomeUnknownCode", ResultStatus.Failed)]
    public void Envelope_Failure_MapsErrorCodeToStatus_AndIsError(string code, ResultStatus expected)
    {
        IResultStatus result = new SentinelCallToolResult<string>
        {
            IsError = true,
            ErrorData = new ResultError(code, "boom")
        };

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(expected));
        Assert.That(result.Code, Is.EqualTo(code));
        Assert.That(result.Message, Is.EqualTo("boom"));
    }

    [Test]
    public void Envelope_FailureWithoutErrorData_IsFailed()
    {
        IResultStatus result = new SentinelCallToolResult<string> { IsError = true };

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
    }

    [Test]
    public void IsErrorStatus_OnlySuccessAndAlreadyInStateAreNotErrors()
    {
        var notErrors = Enum.GetValues<ResultStatus>().Where(s => !s.IsErrorStatus()).ToArray();

        Assert.That(notErrors, Is.EquivalentTo(new[] { ResultStatus.Success, ResultStatus.AlreadyInState }));
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public void Envelope_Serialization_EmitsIsErrorAndNoIsSuccess(bool isError, bool expectedOnWire)
    {
        var json = JsonSerializer.Serialize(
            new SentinelCallToolResult<string> { IsError = isError },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.GetProperty("isError").GetBoolean(), Is.EqualTo(expectedOnWire));
        Assert.That(doc.RootElement.TryGetProperty("isSuccess", out _), Is.False);
        Assert.That(doc.RootElement.TryGetProperty("status", out _), Is.False);
        Assert.That(doc.RootElement.TryGetProperty("code", out _), Is.False);
        Assert.That(doc.RootElement.TryGetProperty("message", out _), Is.False);
    }

    [Test]
    public void Envelope_DefaultsToError_SoAForgottenFlagFailsClosed()
    {
        var result = new SentinelCallToolResult<string>();

        Assert.That(result.IsError, Is.True);
        Assert.That(!result.IsError, Is.False);
    }

    [Test]
    public void Envelope_RoundTripsThroughJson()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(new SentinelCallToolResult<string> { IsError = false, SuccessData = "x" }, options);

        var back = JsonSerializer.Deserialize<SentinelCallToolResult<string>>(json, options);

        Assert.That(back!.IsError, Is.False);
        Assert.That(back.SuccessData, Is.EqualTo("x"));
    }

    [TestCase(EditOutcome.Modified, ResultStatus.Success, null)]
    [TestCase(EditOutcome.AlreadyInTargetState, ResultStatus.AlreadyInState, null)]
    [TestCase(EditOutcome.TargetNotFound, ResultStatus.NotFound, ToolErrorCode.NotFound)]
    [TestCase(EditOutcome.DocumentNotFound, ResultStatus.NotFound, ToolErrorCode.NotFound)]
    [TestCase(EditOutcome.NoCandidatesFound, ResultStatus.NotFound, ToolErrorCode.NotFound)]
    [TestCase(EditOutcome.SourceInvalid, ResultStatus.InvalidInput, ToolErrorCode.InvalidArgument)]
    [TestCase(EditOutcome.FeatureDisabled, ResultStatus.FeatureDisabled, ToolErrorCode.FeatureDisabled)]
    [TestCase(EditOutcome.CannotOptimize, ResultStatus.NotApplicable, ToolErrorCode.TargetIneligible)]
    [TestCase(EditOutcome.CannotEdit, ResultStatus.NotApplicable, ToolErrorCode.TargetIneligible)]
    [TestCase(EditOutcome.CannotMove, ResultStatus.NotApplicable, ToolErrorCode.TargetIneligible)]
    [TestCase(EditOutcome.CannotRemove, ResultStatus.NotApplicable, ToolErrorCode.TargetIneligible)]
    [TestCase(EditOutcome.CannotConvert, ResultStatus.NotApplicable, ToolErrorCode.TargetIneligible)]
    [TestCase(EditOutcome.Error, ResultStatus.Failed, ToolErrorCode.Exception)]
    [TestCase(EditOutcome.Unset, ResultStatus.Failed, ToolErrorCode.Exception)]
    public void EditOutcome_MapsToStatusAndCode_AndResultAgrees(EditOutcome outcome, ResultStatus expectedStatus, string? expectedCode)
    {
        Assert.That(outcome.ToResultStatus(), Is.EqualTo(expectedStatus));
        Assert.That(outcome.ToErrorCode(), Is.EqualTo(expectedCode));

        IResultStatus result = new DocumentEditResult(outcome, default);
        Assert.That(result.Status, Is.EqualTo(expectedStatus));
        Assert.That(result.IsError, Is.EqualTo(expectedStatus.IsErrorStatus()));
        Assert.That(result.Code, Is.EqualTo(expectedCode));
    }

    [Test]
    public void EditOutcome_EveryMember_ErrorStatusAgreesWithCode_AndExplicitErrorCodeWins()
    {
        foreach (var outcome in Enum.GetValues<EditOutcome>())
        {
            var status = outcome.ToResultStatus();
            Assert.That(outcome.ToErrorCode() is null, Is.EqualTo(!status.IsErrorStatus()), $"{outcome}: code presence must match IsError");
        }

        IResultStatus withCode = new DocumentEditResult(EditOutcome.CannotEdit, default) { ErrorCode = ToolErrorCode.Ambiguous };
        Assert.That(withCode.Code, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(withCode.IsError, Is.True);

        var json = JsonSerializer.Serialize(new DocumentEditResult(EditOutcome.Modified, default));
        Assert.That(json, Does.Not.Contain("\"IsError\"").And.Not.Contain("\"Status\"").And.Not.Contain("\"Code\""));
    }

    [Test]
    public void ToResultError_OperationCanceled_MapsToCancelled()
    {
        ResultError error = ToolErrorMapper.ToResultError(new OperationCanceledException("c"), null!, "Op");

        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.Cancelled));
        Assert.That(ResultStatusExtensions.FromErrorCode(error.ErrorCode), Is.EqualTo(ResultStatus.Cancelled));

        ResultError taskCanceled = ToolErrorMapper.ToResultError(new System.Threading.Tasks.TaskCanceledException("t"), null!, "Op");

        Assert.That(taskCanceled.ErrorCode, Is.EqualTo(ToolErrorCode.Cancelled));
        Assert.That(ResultStatusExtensions.FromErrorCode(taskCanceled.ErrorCode), Is.EqualTo(ResultStatus.Cancelled));
    }

    [Test]
    public void ToResultError_TimeoutException_MapsToTimedOut()
    {
        ResultError error = ToolErrorMapper.ToResultError(new TimeoutException("t"), null!, "Op");

        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.TimedOut));
        Assert.That(ResultStatusExtensions.FromErrorCode(error.ErrorCode), Is.EqualTo(ResultStatus.TimedOut));
    }

    [Test]
    public void ToResultError_ToolNotFoundAndPlainException_KeepTheirExistingCodes()
    {
        ResultError notFound = ToolErrorMapper.ToResultError(new ToolNotFoundException("missing"), null!, "Op");
        ResultError plain = ToolErrorMapper.ToResultError(new InvalidOperationException("boom"), null!, "Op");

        Assert.That(notFound.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(plain.ErrorCode, Is.EqualTo(ToolErrorCode.Exception));
    }

    [Test]
    public void FromFlag_NotError_IsSuccess_AndErrorWithoutOrNonErrorCode_IsFailed()
    {
        Assert.That(ResultStatusExtensions.FromFlag(false, ToolErrorCode.NotFound), Is.EqualTo(ResultStatus.Success));
        Assert.That(ResultStatusExtensions.FromFlag(true, null), Is.EqualTo(ResultStatus.Failed));
        Assert.That(ResultStatusExtensions.FromFlag(true, ToolErrorCode.NotFound), Is.EqualTo(ResultStatus.NotFound));
    }

    [Test]
    public void ExtractMethodResult_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new ExtractMethodResult(false, null, "b", "c", "m", "u");
        IResultStatus bad = new ExtractMethodResult(true, "cannot extract", null, null, null, null);

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status.IsErrorStatus(), Is.True);
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("cannot extract"));

        string json = JsonSerializer.Serialize((ExtractMethodResult)bad);
        Assert.That(json, Does.Not.Contain("\"Status\"").And.Not.Contain("\"Code\"").And.Not.Contain("\"Message\""));
    }

    [Test]
    public void ApplyChangesResult_StatusAgreesWithIsError_AndRefusalCodeWins()
    {
        IResultStatus ok = new ApplyChangesResult(false, [], [], "wrote 1 file");
        IResultStatus failed = new ApplyChangesResult(true, [], [], "write failed");
        IResultStatus refused = new ApplyChangesResult(true, [], [], "eol refused", RefusalCode: ToolErrorCode.EolChangeRefused);

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(failed.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(failed.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(failed.Message, Is.EqualTo("write failed"));
        Assert.That(refused.IsError, Is.True);
        Assert.That(refused.Code, Is.EqualTo(ToolErrorCode.EolChangeRefused));
        Assert.That(refused.Status.IsErrorStatus(), Is.True);

        string json = JsonSerializer.Serialize((ApplyChangesResult)failed);
        Assert.That(json, Does.Not.Contain("\"Status\"").And.Not.Contain("\"Code\"").And.Not.Contain("\"Message\""));
    }

    [Test]
    public void DiagnosticReport_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new DiagnosticReport(false, []);
        IResultStatus bad = new DiagnosticReport(true, [new DiagnosticInfo("CS0103", "Error", "m", "C.cs", 1, 1, 1, 2)]);

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status.IsErrorStatus(), Is.True);
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(bad.Message, Does.StartWith("1 error diagnostic"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((DiagnosticReport)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Is.EquivalentTo(new[] { "IsError", "Diagnostics" }));
    }

    [Test]
    public void MsAugmentResult_StatusAgreesWithIsError_AndExplicitErrorCodeWins()
    {
        IResultStatus ok = MsAugmentResult.Ok("text");
        IResultStatus plain = MsAugmentResult.Fail("boom");
        IResultStatus coded = MsAugmentResult.Fail("nope", ToolErrorCode.TargetIneligible);

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(plain.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(plain.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(coded.Status, Is.EqualTo(ResultStatus.NotApplicable));
        Assert.That(coded.Code, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(coded.Message, Is.EqualTo("nope"));
    }

    [TestCase(EngineOutcome.Success, ResultStatus.Success, null)]
    [TestCase(EngineOutcome.Failure, ResultStatus.Failed, ToolErrorCode.Exception)]
    [TestCase(EngineOutcome.DocumentNotFound, ResultStatus.NotFound, ToolErrorCode.NotFound)]
    [TestCase(EngineOutcome.TargetNotFound, ResultStatus.NotFound, ToolErrorCode.NotFound)]
    [TestCase(EngineOutcome.InvalidInput, ResultStatus.InvalidInput, ToolErrorCode.InvalidArgument)]
    [TestCase(EngineOutcome.Timeout, ResultStatus.TimedOut, ToolErrorCode.TimedOut)]
    [TestCase(EngineOutcome.InternalError, ResultStatus.Failed, ToolErrorCode.Exception)]
    public void EngineResultWrapper_EveryOutcome_StatusAndCodeAgreeWithIsError(EngineOutcome outcome, ResultStatus expectedStatus, string? expectedCode)
    {
        IResultStatus result = new EngineResultWrapper<int>(outcome, outcome == EngineOutcome.Success ? 1 : default, outcome == EngineOutcome.Success ? null : new EngineError("m"));

        Assert.That(result.IsError, Is.EqualTo(expectedStatus.IsErrorStatus()));
        Assert.That(result.Status, Is.EqualTo(expectedStatus));
        Assert.That(result.Code, Is.EqualTo(expectedCode));
    }

    [Test]
    public void EngineError_ToToolResponse_EmitsIsErrorAndNoSuccessField()
    {
        using var doc = JsonDocument.Parse(new EngineError("boom").ToToolResponse());

        Assert.That(doc.RootElement.GetProperty("isError").GetBoolean(), Is.True);
        Assert.That(doc.RootElement.TryGetProperty("success", out _), Is.False);
    }

    [Test]
    public void ImpactReport_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new ImpactReport("s", "k", new List<ReferenceInfo>(), 0, 0, null);
        IResultStatus bad = new ImpactReport("", "", new List<ReferenceInfo>(), 0, 0, "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((ImpactReport)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void TypeHierarchyReport_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new TypeHierarchyReport { TypeName = "T" };
        IResultStatus bad = new TypeHierarchyReport { Error = "boom" };

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((TypeHierarchyReport)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void VariableLifetimeReport_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new VariableLifetimeReport { VariableName = "v" };
        IResultStatus bad = new VariableLifetimeReport { Error = "boom" };

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((VariableLifetimeReport)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void ToolsetChange_ChangedMapsToSuccessOrAlreadyInState_AndSerializationHasNoStatusFields()
    {
        IResultStatus changed = new ToolsetChange(default, true, true, [], [], [], [], []);
        IResultStatus noop = new ToolsetChange(default, false, false, [], [], [], [], []);

        Assert.That(changed.IsError, Is.False);
        Assert.That(changed.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(changed.Code, Is.Null);
        Assert.That(changed.Message, Is.Null);
        Assert.That(noop.IsError, Is.False);
        Assert.That(noop.Status, Is.EqualTo(ResultStatus.AlreadyInState));
        Assert.That(noop.Code, Is.Null);
        Assert.That(noop.Message, Is.Null);

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((ToolsetChange)noop));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void ChangeSignatureResult_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new ChangeSignatureResult(new Dictionary<FilePathWrapper, string>(), [], null);
        IResultStatus bad = new ChangeSignatureResult(new Dictionary<FilePathWrapper, string>(), [], "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((ChangeSignatureResult)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void RenameSymbolResult_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new RenameSymbolResult("a", "b", new Dictionary<FilePathWrapper, string>());
        IResultStatus bad = new RenameSymbolResult("a", "b", new Dictionary<FilePathWrapper, string>(), "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((RenameSymbolResult)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [TestCase(true, ResultStatus.Success)]
    [TestCase(false, ResultStatus.AlreadyInState)]
    public void FormatPreviewResult_ChangedMapsToSuccessOrAlreadyInState_AndSerializationHasNoStatusFields(bool changed, ResultStatus expected)
    {
        IResultStatus result = new FormatPreviewResult(changed, 0, []);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(expected));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((FormatPreviewResult)result));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void SymbolHoverInfo_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new SymbolHoverInfo("n", "k", "sig", null, null, null, "public", null, null, [], null);
        IResultStatus bad = new SymbolHoverInfo("n", "k", "sig", null, null, null, "public", null, null, [], "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((SymbolHoverInfo)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void NamedArgumentsOutcome_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new NamedArgumentsOutcome(new Dictionary<FilePathWrapper, string>(), [], null);
        IResultStatus bad = new NamedArgumentsOutcome(new Dictionary<FilePathWrapper, string>(), [], new ResultError(ToolErrorCode.NotFound, "gone"));

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.NotFound));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(bad.Message, Is.EqualTo("gone"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((NamedArgumentsOutcome)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void SemanticReplaceOutcome_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IResultStatus ok = new SemanticReplaceOutcome([], new Dictionary<FilePathWrapper, string>(), null);
        IResultStatus bad = new SemanticReplaceOutcome([], new Dictionary<FilePathWrapper, string>(), new ResultError(ToolErrorCode.NotFound, "gone"));

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.NotFound));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(bad.Message, Is.EqualTo("gone"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((SemanticReplaceOutcome)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void DocumentLookupResult_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        var notFound = new DocumentLookupResult(DocumentLookupStatus.NotFound, "a.cs", null, Array.Empty<string>(), 2);
        var ambiguous = new DocumentLookupResult(DocumentLookupStatus.Ambiguous, "a.cs", null, new[] { "x/a.cs", "y/a.cs" }, 2);
        var foundWithoutDocument = new DocumentLookupResult(DocumentLookupStatus.Found, "a.cs", null, Array.Empty<string>(), 2);

        IResultStatus nf = notFound;
        Assert.That(nf.IsError, Is.True);
        Assert.That(nf.Status, Is.EqualTo(ResultStatus.NotFound));
        Assert.That(nf.Code, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(nf.Message, Is.Not.Null.And.Not.Empty);

        IResultStatus amb = ambiguous;
        Assert.That(amb.IsError, Is.True);
        Assert.That(amb.Status, Is.EqualTo(ResultStatusExtensions.FromFlag(true, ToolErrorCode.Ambiguous)));
        Assert.That(amb.Code, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(amb.Message, Is.Not.Null.And.Not.Empty);

        // Found with no Document is not a usable result, so it reads as an error (IsFound is false).
        IResultStatus found = foundWithoutDocument;
        Assert.That(found.IsError, Is.True);
        Assert.That(found.Code, Is.EqualTo(ToolErrorCode.NotFound));

        // Status is explicit (the positional Status is DocumentLookupStatus), so check the declared type.
        Assert.That(typeof(DocumentLookupResult).GetProperty("Status")!.PropertyType, Is.EqualTo(typeof(DocumentLookupStatus)));
        foreach (string name in new[] { "IsError", "Code", "Message" })
        {
            Assert.That(typeof(DocumentLookupResult).GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty);
        }
    }

    private static BuildResult MakeBuildResult(BuildOutcome outcome, string? detail = null) => new(
        outcome, default(BuildVerifyLevel), new List<string>(), true, 0, 0,
        new List<DiagnosticInfo>(), new List<DiagnosticInfo>(),
        new List<DiagnosticGroupSummary>(), new List<DiagnosticGroupSummary>(),
        null, null, TimeSpan.Zero, Detail: detail);

    [Test]
    public void BuildResult_OutcomeDrivesStatus_AndSerializationHasNoStatusFields()
    {
        BuildResult ok = MakeBuildResult(BuildOutcome.Succeeded);
        BuildResult notRun = MakeBuildResult(BuildOutcome.NotRun);
        BuildResult failed = MakeBuildResult(BuildOutcome.Failed, "compile broke");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatusExtensions.FromFlag(false, null)));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);

        Assert.That(notRun.IsError, Is.True);
        Assert.That(notRun.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(notRun.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(notRun.Message, Is.EqualTo("Build was not run"));

        Assert.That(failed.IsError, Is.True);
        Assert.That(failed.Status, Is.EqualTo(ResultStatusExtensions.FromFlag(true, ToolErrorCode.BuildFailed)));
        Assert.That(failed.Code, Is.EqualTo(ToolErrorCode.BuildFailed));
        Assert.That(failed.Message, Is.EqualTo("compile broke"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(failed));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Contain("Outcome"));
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }
}
