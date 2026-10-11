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
        IResultStatus ok = new ControlFlowAnalysisResult("m", true, [], [], [], [], null);
        IResultStatus bad = new ControlFlowAnalysisResult("m", true, [], [], [], [], "boom");

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
        IResultStatus ok = new DataFlowAnalysisResult("m", [], [], [], [], [], [], null);
        IResultStatus bad = new DataFlowAnalysisResult("m", [], [], [], [], [], [], "boom");

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
        IResultStatus ok = new TestScaffoldResult("c", "tc", "ns", "code", null);
        IResultStatus bad = new TestScaffoldResult("c", "tc", "ns", "code", "boom");

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
}
