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
        IOperationResult result = new SentinelCallToolResult<string> { IsError = false, SuccessData = "x" };

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
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
        IOperationResult result = new SentinelCallToolResult<string>
        {
            IsError = true,
            ErrorData = new ResultError(code, "boom")
        };

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(expected));
    }

    [Test]
    public void Envelope_FailureWithoutErrorData_IsFailed()
    {
        IOperationResult result = new SentinelCallToolResult<string> { IsError = true };

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
    [TestCase(EditOutcome.NoCandidatesFound, ResultStatus.NotFound, ToolErrorCode.NotFound)]
    [TestCase(EditOutcome.Unset, ResultStatus.Failed, ToolErrorCode.Exception)]
    public void EditOutcome_MapsToStatusAndCode_AndResultAgrees(EditOutcome outcome, ResultStatus expectedStatus, string? expectedCode)
    {
        Assert.That(outcome.ToResultStatus(), Is.EqualTo(expectedStatus));
        Assert.That(outcome.ToErrorCode(), Is.EqualTo(expectedCode));

        IOperationResult result = new DocumentEditResult(outcome, default);
        Assert.That(result.Status, Is.EqualTo(expectedStatus));
        Assert.That(result.IsError, Is.EqualTo(expectedStatus.IsErrorStatus()));
    }

    [Test]
    public void EditOutcome_EveryMember_ErrorStatusAgreesWithCode_AndUnsetIsError()
    {
        foreach (var outcome in Enum.GetValues<EditOutcome>())
        {
            var status = outcome.ToResultStatus();
            Assert.That(outcome.ToErrorCode() is null, Is.EqualTo(!status.IsErrorStatus()), $"{outcome}: code presence must match IsError");
        }

        IOperationResult unset = new DocumentEditResult(EditOutcome.Unset, default);
        Assert.That(unset.IsError, Is.True);

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
    public void ToResultError_AmbiguousWithCandidates_CarriesCandidatesAsStructuredDetail()
    {
        var candidates = new[]
        {
            new AmbiguousCandidate("A.cs", 3, "Method", "void Run()", "A", "public void Run()", null, true),
            new AmbiguousCandidate("B.cs", 7, "Method", "void Run()", "B", "public void Run()", "// b", true),
        };

        ResultError error = ToolErrorMapper.ToResultError(new ToolAmbiguousMatchException("two matches", candidates), null!, "Op");

        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(error.Message, Is.EqualTo("Op failed: two matches"));
        Assert.That(error.Detail, Is.Null, "Detail must not repeat the text Message already carries.");
        Assert.That(error.StructuredDetail, Is.Not.Null);
        Assert.That(error.StructuredDetail!, Has.Count.EqualTo(2));
        Assert.That(error.StructuredDetail![0], Is.InstanceOf<AmbiguousCandidate>());
        Assert.That(error.StructuredDetail![1], Is.InstanceOf<AmbiguousCandidate>());
        Assert.That(((AmbiguousCandidate)error.StructuredDetail![0]).FilePath, Is.EqualTo("A.cs"));
        Assert.That(((AmbiguousCandidate)error.StructuredDetail![0]).ContextSnippet, Is.EqualTo("public void Run()"));
        Assert.That(((AmbiguousCandidate)error.StructuredDetail![1]).FilePath, Is.EqualTo("B.cs"));
        Assert.That(((AmbiguousCandidate)error.StructuredDetail![1]).ContextSnippet, Is.EqualTo("public void Run()"));
    }

    [Test]
    public void ToResultError_AmbiguousWithoutCandidates_LeavesStructuredDetailNull()
    {
        ResultError noList = ToolErrorMapper.ToResultError(new ToolAmbiguousMatchException("no list"), null!, "Op");
        ResultError emptyList = ToolErrorMapper.ToResultError(new ToolAmbiguousMatchException("empty", Array.Empty<AmbiguousCandidate>()), null!, "Op");

        Assert.That(noList.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(noList.StructuredDetail, Is.Null);
        Assert.That(emptyList.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(emptyList.StructuredDetail, Is.Null);
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
        IOperationResult ok = new ExtractMethodResult(false, null, "b", "c", "m", "u");
        IOperationResult bad = new ExtractMethodResult(true, "cannot extract", null, null, null, null);

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status.IsErrorStatus(), Is.True);

        string json = JsonSerializer.Serialize((ExtractMethodResult)bad);
        Assert.That(json, Does.Not.Contain("\"Status\"").And.Not.Contain("\"Code\"").And.Not.Contain("\"Message\""));
    }

    [Test]
    public void ApplyChangesResult_StatusAgreesWithIsError_AndRefusalCodeWins()
    {
        IOperationResult ok = new ApplyChangesResult(false, [], [], "wrote 1 file");
        IOperationResult failed = new ApplyChangesResult(true, [], [], "write failed");
        IOperationResult refused = new ApplyChangesResult(true, [], [], "eol refused", RefusalCode: ToolErrorCode.EolChangeRefused);

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(failed.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(refused.IsError, Is.True);
        Assert.That(refused.Status.IsErrorStatus(), Is.True);

        string json = JsonSerializer.Serialize((ApplyChangesResult)failed);
        Assert.That(json, Does.Not.Contain("\"Status\"").And.Not.Contain("\"Code\"").And.Not.Contain("\"Message\""));
    }

    [Test]
    public void DiagnosticReport_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IOperationResult ok = new DiagnosticReport(false, []);
        IOperationResult bad = new DiagnosticReport(true, [new DiagnosticInfo("CS0103", "Error", "m", "C.cs", 1, 1, 1, 2)]);

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status.IsErrorStatus(), Is.True);

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((DiagnosticReport)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Is.EquivalentTo(new[] { "IsError", "Diagnostics" }));
    }

    [Test]
    public void MsAugmentResult_StatusAgreesWithIsError_AndExplicitErrorCodeWins()
    {
        IOperationResult ok = MsAugmentResult.Ok("text");
        IOperationResult plain = MsAugmentResult.Fail("boom");
        IOperationResult coded = MsAugmentResult.Fail("nope", ToolErrorCode.TargetIneligible);

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(plain.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(coded.Status, Is.EqualTo(ResultStatus.NotApplicable));
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
        IOperationResult result = new EngineResultWrapper<int>(outcome, outcome == EngineOutcome.Success ? 1 : default, outcome == EngineOutcome.Success ? null : new EngineError("m"));

        Assert.That(result.IsError, Is.EqualTo(expectedStatus.IsErrorStatus()));
        Assert.That(result.Status, Is.EqualTo(expectedStatus));
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
        IOperationResult ok = new ImpactReport("s", "k", new List<ReferenceInfo>(), 0, 0, null);
        IOperationResult bad = new ImpactReport("", "", new List<ReferenceInfo>(), 0, 0, "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));

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
        IOperationResult ok = new TypeHierarchyReport { TypeName = "T" };
        IOperationResult bad = new TypeHierarchyReport { Error = "boom" };

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));

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
        IOperationResult ok = new VariableLifetimeReport { VariableName = "v" };
        IOperationResult bad = new VariableLifetimeReport { Error = "boom" };

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));

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
        IOperationResult changed = new ToolsetChange(default, true, true, [], [], [], [], []);
        IOperationResult noop = new ToolsetChange(default, false, false, [], [], [], [], []);

        Assert.That(changed.IsError, Is.False);
        Assert.That(changed.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(noop.IsError, Is.False);
        Assert.That(noop.Status, Is.EqualTo(ResultStatus.AlreadyInState));

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
        IOperationResult ok = new ChangeSignatureResult(new Dictionary<FilePathWrapper, string>(), [], null);
        IOperationResult bad = new ChangeSignatureResult(new Dictionary<FilePathWrapper, string>(), [], "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));

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
        IOperationResult ok = new RenameSymbolResult("a", "b", new Dictionary<FilePathWrapper, string>());
        IOperationResult bad = new RenameSymbolResult("a", "b", new Dictionary<FilePathWrapper, string>(), "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));

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
        IOperationResult result = new FormatPreviewResult(changed, 0, []);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(expected));

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
        IOperationResult ok = new SymbolHoverInfo("n", "k", "sig", null, null, null, "public", null, null, [], null);
        IOperationResult bad = new SymbolHoverInfo("n", "k", "sig", null, null, null, "public", null, null, [], "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));

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
        IOperationResult ok = new NamedArgumentsOutcome(new Dictionary<FilePathWrapper, string>(), [], null);
        IOperationResult bad = new NamedArgumentsOutcome(new Dictionary<FilePathWrapper, string>(), [], new ResultError(ToolErrorCode.NotFound, "gone"));

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.NotFound));

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
        IOperationResult ok = new SemanticReplaceOutcome([], new Dictionary<FilePathWrapper, string>(), null);
        IOperationResult bad = new SemanticReplaceOutcome([], new Dictionary<FilePathWrapper, string>(), new ResultError(ToolErrorCode.NotFound, "gone"));

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.NotFound));

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

        IOperationResult nf = notFound;
        Assert.That(nf.IsError, Is.True);
        Assert.That(nf.Status, Is.EqualTo(ResultStatus.NotFound));

        IOperationResult amb = ambiguous;
        Assert.That(amb.IsError, Is.True);
        Assert.That(amb.Status, Is.EqualTo(ResultStatusExtensions.FromFlag(true, ToolErrorCode.Ambiguous)));

        // Found with no Document is not a usable result, so it reads as an error (IsFound is false).
        IOperationResult found = foundWithoutDocument;
        Assert.That(found.IsError, Is.True);

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

    [Test]
    public void FailureCountHelpers_ZeroFailed_IsSuccessWithNoMessage()
    {
        Assert.That(ResultStatusExtensions.FromFailureCount(0, 3), Is.EqualTo(ResultStatus.Success));
        Assert.That(ResultStatusExtensions.FailureCountMessage(3, 0), Is.Null);
    }

    [Test]
    public void FailureCountHelpers_SomeFailedSomeSucceeded_IsPartialFailureWithCountMessage()
    {
        Assert.That(ResultStatusExtensions.FromFailureCount(2, 3), Is.EqualTo(ResultStatus.PartialFailure));
        Assert.That(ResultStatusExtensions.FailureCountMessage(3, 2), Is.EqualTo("2 of 5 failed"));
    }

    [Test]
    public void FailureCountHelpers_AllFailed_IsFailed()
    {
        Assert.That(ResultStatusExtensions.FromFailureCount(2, 0), Is.EqualTo(ResultStatus.Failed));
    }

    [Test]
    public void PartialFailure_IsAnErrorStatus()
    {
        Assert.That(ResultStatus.PartialFailure.IsErrorStatus(), Is.True);
    }

    private static OperationSummary SummarizeCounts(int succeeded, int alreadySatisfied, int failed, int blocked) =>
        OperationSummary.FromCounts("blob", "change", succeeded, alreadySatisfied, 0, failed, blocked,
            succeeded + alreadySatisfied + failed + blocked, Array.Empty<ItemFailure>(), false, "directive", false);

    [Test]
    public void OperationSummary_CompletedFully_IsSuccess()
    {
        OperationSummary summary = SummarizeCounts(3, 0, 0, 0);

        Assert.That(summary, Is.InstanceOf<IOperationResult>());
        Assert.That(summary.Outcome, Is.EqualTo(OperationOutcome.CompletedFully));
        Assert.That(summary.IsError, Is.False);
        Assert.That(summary.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(summary.Code, Is.Null);
        Assert.That(summary.Message, Is.Null);
    }

    [Test]
    public void OperationSummary_PartialFailure_IsPartialFailureWithCountMessage()
    {
        OperationSummary summary = SummarizeCounts(3, 0, 2, 0);

        Assert.That(summary.Outcome, Is.EqualTo(OperationOutcome.PartialProgress));
        Assert.That(summary.IsError, Is.True);
        Assert.That(summary.Status, Is.EqualTo(ResultStatus.PartialFailure));
        Assert.That(summary.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(summary.Message, Is.EqualTo("2 of 5 failed"));
    }

    [Test]
    public void OperationSummary_BlockedOnly_IsNoProgressFailed()
    {
        OperationSummary summary = SummarizeCounts(0, 0, 0, 2);

        Assert.That(summary.Outcome, Is.EqualTo(OperationOutcome.NoProgress));
        Assert.That(summary.IsError, Is.True);
        Assert.That(summary.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(summary.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(summary.Message, Is.EqualTo("2 of 2 failed"));
    }

    [Test]
    public void OperationSummary_NoProgressWithoutFailureCount_UsesFallbackMessage()
    {
        var summary = new OperationSummary { Outcome = OperationOutcome.NoProgress };

        Assert.That(summary.IsError, Is.True);
        Assert.That(summary.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(summary.Message, Is.EqualTo("No progress was made"));
    }

    [Test]
    public void OperationSummary_NothingToDo_IsAlreadyInStateNotError()
    {
        OperationSummary summary = SummarizeCounts(0, 0, 0, 0);

        Assert.That(summary.Outcome, Is.EqualTo(OperationOutcome.NothingToDo));
        Assert.That(summary.IsError, Is.False);
        Assert.That(summary.Status, Is.EqualTo(ResultStatus.AlreadyInState));
        Assert.That(summary.Code, Is.Null);
        Assert.That(summary.Message, Is.Null);
    }

    [Test]
    public void OperationSummary_ComputedStatusMembers_AreJsonIgnored()
    {
        foreach (string name in new[] { "IsError", "Status", "Code", "Message" })
        {
            Assert.That(typeof(OperationSummary).GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty);
        }
    }

    [Test]
    public void AsyncifyLoopResult_Success_IsSuccessWithNoMessage()
    {
        var result = new AsyncifyLoopResult { TotalSucceeded = 4, TotalFailed = 0 };

        Assert.That(result, Is.InstanceOf<IOperationResult>());
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void AsyncifyLoopResult_PartialFailure_IsPartialFailureWithCountMessage()
    {
        var result = new AsyncifyLoopResult { TotalSucceeded = 3, TotalFailed = 2 };

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.PartialFailure));
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("2 of 5 failed"));
    }

    [Test]
    public void BridgeAsyncMethodsResult_Success_IsSuccessWithNoMessage()
    {
        var result = new BridgeAsyncMethodsResult { Summary = new BatchResultSummary { Succeeded = 4, Failed = 0 } };

        Assert.That(result, Is.InstanceOf<IOperationResult>());
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void BridgeAsyncMethodsResult_PartialFailure_IsPartialFailureWithCountMessage()
    {
        var result = new BridgeAsyncMethodsResult { Summary = new BatchResultSummary { Succeeded = 3, Failed = 2 } };

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.PartialFailure));
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("2 of 5 failed"));
    }

    [Test]
    public void UpliftCallersResult_Success_IsSuccessWithNoMessage()
    {
        var result = new UpliftCallersResult { Summary = new BatchResultSummary { Succeeded = 4, Failed = 0 } };

        Assert.That(result, Is.InstanceOf<IOperationResult>());
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void UpliftCallersResult_PartialFailure_IsPartialFailureWithCountMessage()
    {
        var result = new UpliftCallersResult { Summary = new BatchResultSummary { Succeeded = 3, Failed = 2 } };

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.PartialFailure));
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("2 of 5 failed"));
    }

    [Test]
    public void BatchAggregates_ComputedStatusMembers_AreJsonIgnored()
    {
        foreach (Type type in new[] { typeof(AsyncifyLoopResult), typeof(BridgeAsyncMethodsResult), typeof(UpliftCallersResult) })
        {
            foreach (string name in new[] { "IsError", "Status", "Code", "Message" })
            {
                Assert.That(type.GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, $"{type.Name}.{name}");
            }
        }
    }

    [Test]
    public void ProjectTestSummary_Passed_IsSuccess()
    {
        var summary = new ProjectTestSummary("P", true, 3, 3, 0, 0, null);

        Assert.That(summary.IsError, Is.False);
        Assert.That(summary.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(summary.Code, Is.Null);
        Assert.That(summary.Message, Is.Null);
    }

    [Test]
    public void ProjectTestSummary_RunFailed_IsFailedWithDetail()
    {
        var summary = new ProjectTestSummary("P", false, 3, 2, 1, 0, "boom");

        Assert.That(summary.IsError, Is.True);
        Assert.That(summary.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(summary.Code, Is.EqualTo(ToolErrorCode.TestRunFailed));
        Assert.That(summary.Message, Is.EqualTo("boom"));
    }

    private static TestRunResult MakeRun(bool runSucceeded, bool runCompleted, int failed) => new(
        runSucceeded, 0, 5, 5 - failed, failed, 0,
        new List<GroupedCountSummary>(), new List<TestCaseResult>(), null, null, TimeSpan.Zero,
        null, runCompleted);

    [Test]
    public void TestRunResult_Success_IsSuccess()
    {
        TestRunResult run = MakeRun(true, true, 0);

        Assert.That(run.IsError, Is.False);
        Assert.That(run.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(run.Code, Is.Null);
        Assert.That(run.Message, Is.Null);
    }

    [Test]
    public void TestRunResult_TestFailures_IsFailedWithCountMessage()
    {
        TestRunResult run = MakeRun(false, true, 2);

        Assert.That(run.IsError, Is.True);
        Assert.That(run.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(run.Code, Is.EqualTo(ToolErrorCode.TestRunFailed));
        Assert.That(run.Message, Is.EqualTo("2 of 5 tests failed"));
    }

    [Test]
    public void TestRunResult_IncompleteRun_IsFailedWithIncompleteMessage()
    {
        TestRunResult run = MakeRun(false, false, 0);

        Assert.That(run.IsError, Is.True);
        Assert.That(run.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(run.Code, Is.EqualTo(ToolErrorCode.TestRunFailed));
        Assert.That(run.Message, Is.EqualTo("Test run did not complete"));
    }

    [Test]
    public void TestRunTypes_ComputedStatusMembers_AreJsonIgnored()
    {
        foreach (Type type in new[] { typeof(ProjectTestSummary), typeof(TestRunResult) })
        {
            foreach (string name in new[] { "IsError", "Status", "Code", "Message" })
            {
                Assert.That(type.GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, $"{type.Name}.{name}");
            }
        }
    }

    private static TestCategoryProjectApplyResult MakeProjectApply(ResultError? error) => new(
        "P", 0, 0, 0, 0, 0, Array.Empty<string>(), null, error, Array.Empty<TestCategoryEditSkip>());

    [Test]
    public void TestCategoryProjectApplyResult_Success_IsSuccess()
    {
        TestCategoryProjectApplyResult result = MakeProjectApply(null);

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void TestCategoryProjectApplyResult_Rejected_CarriesErrorCodeAndMessage()
    {
        TestCategoryProjectApplyResult result = MakeProjectApply(new ResultError(ToolErrorCode.Exception, "compile errors"));

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("compile errors"));
    }

    [Test]
    public void TestCategoryApplyResult_AllProjectsApplied_IsSuccess()
    {
        var result = new TestCategoryApplyResult(new[] { MakeProjectApply(null), MakeProjectApply(null) });

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void TestCategoryApplyResult_OneProjectRejected_IsPartialFailureWithCountMessage()
    {
        var result = new TestCategoryApplyResult(new[]
        {
            MakeProjectApply(null),
            MakeProjectApply(new ResultError(ToolErrorCode.Exception, "boom")),
        });

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.PartialFailure));
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("1 of 2 failed"));
    }

    [Test]
    public void TestCategoryTypes_ComputedStatusMembers_AreJsonIgnored()
    {
        foreach (Type type in new[] { typeof(TestCategoryProjectApplyResult), typeof(TestCategoryApplyResult) })
        {
            foreach (string name in new[] { "IsError", "Status", "Code", "Message" })
            {
                Assert.That(type.GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, $"{type.Name}.{name}");
            }
        }
    }

    [Test]
    public void AddConstructorParameterCascadeResult_FullChangeSet_IsSuccess()
    {
        var classEdit = new DocumentEditResult { Outcome = EditOutcome.Modified };
        var result = new AddConstructorParameterCascadeResult(classEdit, new Dictionary<FilePathWrapper, string>(), 2, 0);

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(((IOperationResult)result).IsError, Is.False);
    }

    [Test]
    public void AddConstructorParameterCascadeResult_ComputedStatusMembers_AreJsonIgnored()
    {
        Assert.That(typeof(AddConstructorParameterCascadeResult).GetProperty("Status")!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, "Status");
    }

    private static SubAgentEvalResult EvalResult(bool converged = true, bool buildSucceeded = true, int buildErrors = 0, int testsFailed = 0, string? excerpt = null) =>
        new()
        {
            Converged = converged,
            StopReason = "end_turn",
            TurnCount = 3,
            BuildSucceeded = buildSucceeded,
            BuildErrorCount = buildErrors,
            TestPassedCount = 5,
            TestFailedCount = testsFailed,
            TouchedPaths = Array.Empty<string>(),
            FailureExcerpt = excerpt,
            TranscriptPath = "t.json",
        };

    [Test]
    public void SubAgentResult_Converged_IsSuccess()
    {
        var result = new SubAgentResult { Output = "done", Converged = true, StopReason = "end_turn", TurnCount = 2, TranscriptPath = "t.json" };

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void SubAgentResult_NotConverged_IsFailedWithExceptionCode()
    {
        var result = new SubAgentResult { Output = "partial", Converged = false, StopReason = "max_turns", TurnCount = 9, TranscriptPath = "t.json" };

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("Sub-agent stopped without converging: max_turns"));
    }

    [Test]
    public void SubAgentEvalResult_CleanRun_IsSuccess()
    {
        var result = EvalResult();

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void SubAgentEvalResult_NotConverged_IsExceptionWithStopReasonMessage()
    {
        var result = EvalResult(converged: false);

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("Did not converge: end_turn"));
    }

    [Test]
    public void SubAgentEvalResult_BuildFailed_IsBuildFailedWithErrorCountMessage()
    {
        var result = EvalResult(buildSucceeded: false, buildErrors: 3);

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.BuildFailed));
        Assert.That(result.Message, Is.EqualTo("Build failed with 3 error(s)"));
    }

    [Test]
    public void SubAgentEvalResult_TestsFailed_IsTestRunFailedWithCountMessage()
    {
        var result = EvalResult(testsFailed: 2);

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.TestRunFailed));
        Assert.That(result.Message, Is.EqualTo("2 test(s) failed"));
    }

    [Test]
    public void SubAgentEvalResult_FailureExcerpt_WinsOverDerivedMessage()
    {
        var result = EvalResult(buildSucceeded: false, buildErrors: 1, excerpt: "boom");

        Assert.That(result.Message, Is.EqualTo("boom"));
    }

    [Test]
    public void SubAgentTypes_ComputedStatusMembers_AreJsonIgnored()
    {
        foreach (Type type in new[] { typeof(SubAgentResult), typeof(SubAgentEvalResult) })
        {
            foreach (string name in new[] { "IsError", "Status", "Code", "Message" })
            {
                Assert.That(type.GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, $"{type.Name}.{name}");
            }
        }
    }

    private static AppliedChangeSummary AppliedSummary(string? changeId, bool hasFile, bool dryRun = false, bool proposeOnly = false) =>
        new(
            changeId,
            hasFile ? new List<FilePathWrapper> { new FilePathWrapper("C:/x/f0.cs") } : new List<FilePathWrapper>(),
            "desc",
            dryRun,
            ChangedContent: proposeOnly ? new Dictionary<FilePathWrapper, string>() : null);

    private static void AssertAppliedSummaryNotError(AppliedChangeSummary summary, ResultStatus expected)
    {
        var result = (IOperationResult)summary;
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(expected));
    }

    [Test]
    public void AppliedChangeSummary_DryRunOk_IsSuccess()
    {
        var summary = AppliedSummary(null, hasFile: true, dryRun: true);

        Assert.That(summary.Status, Is.EqualTo("dry_run_ok"));
        AssertAppliedSummaryNotError(summary, ResultStatus.Success);
    }

    [Test]
    public void AppliedChangeSummary_NotWritten_IsSuccess()
    {
        var summary = AppliedSummary(null, hasFile: true, proposeOnly: true);

        Assert.That(summary.Status, Is.EqualTo("not_written"));
        AssertAppliedSummaryNotError(summary, ResultStatus.Success);
    }

    [Test]
    public void AppliedChangeSummary_NoChanges_IsAlreadyInState()
    {
        var summary = AppliedSummary(null, hasFile: false);

        Assert.That(summary.Status, Is.EqualTo("no_changes"));
        AssertAppliedSummaryNotError(summary, ResultStatus.AlreadyInState);
    }

    [Test]
    public void AppliedChangeSummary_Applied_IsSuccess()
    {
        var summary = AppliedSummary("c1", hasFile: true);

        Assert.That(summary.Status, Is.EqualTo("applied"));
        AssertAppliedSummaryNotError(summary, ResultStatus.Success);
    }

    [Test]
    public void AppliedChangeSummary_ComputedStatusMembers_AreJsonIgnored()
    {
        foreach (string name in new[] { "IsError", "Code", "Message" })
        {
            Assert.That(typeof(AppliedChangeSummary).GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, name);
        }

        var explicitStatus = typeof(AppliedChangeSummary).GetProperty("RoslynSentinel.Common.IOperationResult.Status", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.That(explicitStatus, Is.Not.Null);
        Assert.That(explicitStatus!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, "IOperationResult.Status");
    }
}
