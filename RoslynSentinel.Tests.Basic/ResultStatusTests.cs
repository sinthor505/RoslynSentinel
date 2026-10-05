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
}
