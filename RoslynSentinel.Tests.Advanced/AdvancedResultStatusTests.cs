using System.Text.Json;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class AdvancedResultStatusTests
{
    [Test]
    public void ControlFlowAnalysisResult_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IOperationResult ok = new ControlFlowAnalysisResult("m", true, [], [], [], [], null);
        IOperationResult bad = new ControlFlowAnalysisResult("m", true, [], [], [], [], "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((ControlFlowAnalysisResult)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void DataFlowAnalysisResult_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IOperationResult ok = new DataFlowAnalysisResult("m", [], [], [], [], [], [], null);
        IOperationResult bad = new DataFlowAnalysisResult("m", [], [], [], [], [], [], "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((DataFlowAnalysisResult)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Code"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
    }

    [Test]
    public void TestScaffoldResult_StatusAgreesWithIsError_AndSerializationHasNoStatusFields()
    {
        IOperationResult ok = new TestScaffoldResult("c", "tc", "ns", "code", null);
        IOperationResult bad = new TestScaffoldResult("c", "tc", "ns", "code", "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        // The record's positional Code is the generated test source; the status code is an explicit
        // interface member, so the property keeps its meaning and its wire value.
        var concrete = (TestScaffoldResult)bad;
        Assert.That(concrete.Code, Is.EqualTo("code"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(concrete));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Does.Not.Contain("IsError"));
        Assert.That(rootProperties, Does.Not.Contain("Status"));
        Assert.That(rootProperties, Does.Not.Contain("Message"));
        Assert.That(doc.RootElement.GetProperty("Code").GetString(), Is.EqualTo("code"));
    }

    [Test]
    public void FluentBuilderResult_StatusAgreesWithIsError_AndSerializationKeepsDeclaredPropertiesOnly()
    {
        IOperationResult ok = new FluentBuilderResult("b", "c", "u", null);
        IOperationResult bad = new FluentBuilderResult("b", "c", "u", "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize((FluentBuilderResult)bad));
        string[] rootProperties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.That(rootProperties, Is.EquivalentTo(new[] { "BuilderClassName", "BuilderCode", "UsageExample", "Error" }));
    }

    [Test]
    public void MemberCommentOutcome_StatusAgreesWithSucceeded_AndSerializationKeepsDeclaredPropertiesOnly()
    {
        IOperationResult ok = new CommentingEngine.MemberCommentOutcome(null!, true, null);
        IOperationResult bad = new CommentingEngine.MemberCommentOutcome(null!, false, "boom");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        // Serializing this record throws (STJ cannot build metadata for the pre-existing Site type graph),
        // so verify the surface by reflection: declared properties only, computed members JsonIgnore-marked.
        var type = typeof(CommentingEngine.MemberCommentOutcome);
        string[] nonComputed = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n is not ("IsError" or "Status" or "Code" or "Message"))
            .ToArray();
        Assert.That(nonComputed, Is.EquivalentTo(new[] { "Site", "Succeeded", "FailureReason" }));
        foreach (string computed in new[] { "IsError", "Status", "Code", "Message" })
        {
            Assert.That(type.GetProperty(computed)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, computed);
        }
    }

    [Test]
    public void PropagateCtResult_StatusAgreesWithMethodFoundAndError_AndSurfaceIsDeclaredOnly()
    {
        IOperationResult ok = new PropagateCtResult { MethodFound = true };
        IOperationResult notFound = new PropagateCtResult { MethodFound = false };
        IOperationResult bad = new PropagateCtResult { MethodFound = true, Error = "boom" };

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(notFound.IsError, Is.True);
        Assert.That(notFound.Status, Is.EqualTo(ResultStatus.NotFound));
        Assert.That(notFound.Code, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(notFound.Message, Is.EqualTo("Method not found"));
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        var type = typeof(PropagateCtResult);
        string[] nonComputed = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n is not ("IsError" or "Status" or "Code" or "Message"))
            .ToArray();
        Assert.That(nonComputed, Is.EquivalentTo(new[] { "ChangeId", "MethodName", "TokenParameterName", "Forwarded", "Skipped", "ForwardedCount", "SkippedCount", "MethodFound", "Error" }));
        foreach (string computed in new[] { "IsError", "Status", "Code", "Message" })
        {
            Assert.That(type.GetProperty(computed)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, computed);
        }
    }

    [Test]
    public void UpliftBatchMultiMethodResult_StatusAgreesWithError_AndSurfaceIsDeclaredOnly()
    {
        IOperationResult ok = new UpliftBatchMultiMethodResult { BridgedMethodName = "m" };
        IOperationResult bad = new UpliftBatchMultiMethodResult { Error = "boom" };

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(ok.Code, Is.Null);
        Assert.That(ok.Message, Is.Null);
        Assert.That(bad.IsError, Is.True);
        Assert.That(bad.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(bad.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(bad.Message, Is.EqualTo("boom"));

        var type = typeof(UpliftBatchMultiMethodResult);
        string[] nonComputed = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n is not ("IsError" or "Status" or "Code" or "Message"))
            .ToArray();
        Assert.That(nonComputed, Is.EquivalentTo(new[] { "BridgedMethodName", "ProjectName", "Result", "Error" }));
        foreach (string computed in new[] { "IsError", "Status", "Code", "Message" })
        {
            Assert.That(type.GetProperty(computed)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, computed);
        }
    }

    private static List<UpliftSkippedInfo> SkipList() =>
        new() { new UpliftSkippedInfo(null!, "Caller", "reason", new List<DiagnosticInfo>()) };

    [Test]
    public void UpliftBatchResult_NoSkips_IsSuccess()
    {
        var result = new UpliftBatchResult(new List<UpliftCallerInfo>(), new List<UpliftSkippedInfo>(), 0, "batch_complete");

        Assert.That(result, Is.InstanceOf<IOperationResult>());
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void UpliftBatchResult_AllSkippedAndStoppedShort_IsFailed()
    {
        var result = new UpliftBatchResult(new List<UpliftCallerInfo>(), SkipList(), 1, "batch_complete");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("1 of 1 failed"));
    }

    [Test]
    public void UpliftBatchResult_DryRunWithSkips_IsNotError()
    {
        var result = new UpliftBatchResult(new List<UpliftCallerInfo>(), SkipList(), 1, "dry_run");

        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void UpliftBatchMultiResult_AllMethodsOk_IsSuccess()
    {
        var result = new UpliftBatchMultiResult
        {
            PerMethod = new List<UpliftBatchMultiMethodResult> { new() { BridgedMethodName = "a" } },
        };

        Assert.That(result, Is.InstanceOf<IOperationResult>());
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void UpliftBatchMultiResult_PartialFailure_IsFailedWithCountMessage()
    {
        var result = new UpliftBatchMultiResult
        {
            PerMethod = new List<UpliftBatchMultiMethodResult>
            {
                new() { BridgedMethodName = "a" },
                new() { BridgedMethodName = "b", Error = "boom" },
            },
        };

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("1 of 2 failed"));
    }

    [Test]
    public void PropagateCtFileResult_NoMethods_IsSuccess()
    {
        var result = new PropagateCtFileResult();

        Assert.That(result, Is.InstanceOf<IOperationResult>());
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void PropagateCtFileResult_MethodFailed_IsFailedWithCountMessage()
    {
        var result = new PropagateCtFileResult
        {
            PerMethod = new List<PropagateCtResult> { new() { MethodName = "m", Error = "boom" } },
        };

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("1 of 1 failed"));
    }

    [Test]
    public void PropagateCtBatchResult_AllApplied_IsSuccess()
    {
        var result = new PropagateCtBatchResult
        {
            Applied = new List<PropagateCtFileResult> { new() },
        };

        Assert.That(result, Is.InstanceOf<IOperationResult>());
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void PropagateCtBatchResult_PartialFailure_IsFailedWithCountMessage()
    {
        var result = new PropagateCtBatchResult
        {
            Applied = new List<PropagateCtFileResult> { new() },
            Failed = new List<PropagateCtFileFailure> { new() },
        };

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("1 of 2 failed"));
    }

    [Test]
    public void BatchResults_ComputedStatusMembers_AreJsonIgnored()
    {
        foreach (Type type in new[] { typeof(UpliftBatchResult), typeof(UpliftBatchMultiResult), typeof(PropagateCtFileResult), typeof(PropagateCtBatchResult) })
        {
            foreach (string name in new[] { "IsError", "Status", "Code", "Message" })
            {
                Assert.That(type.GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, $"{type.Name}.{name}");
            }
        }
    }
}
