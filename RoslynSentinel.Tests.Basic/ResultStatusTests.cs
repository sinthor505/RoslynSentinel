using System.Text.Json;

using RoslynSentinel.Common;

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
}
