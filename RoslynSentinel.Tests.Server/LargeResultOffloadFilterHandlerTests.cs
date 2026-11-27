using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

// Calls LargeResultOffloadFilter.InvokeAsync directly with a fake next, so each threshold rule is pinned without a host.
// Several tests set the process-wide inline budget, so the whole fixture is NonParallelizable.
[TestFixture]
[NonParallelizable]
public class LargeResultOffloadFilterHandlerTests
{
    private const string ToolName = "SomeTool";
    private const string ErrorCode = "CompileGate";

    private string _tempRoot = null!;

    private static int DefaultThreshold => LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Default);

    private static int WholeThreshold => LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Whole);

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "offload-handler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        InlineBudgetOptions.Configure([]);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task HonoursSignalThreshold()
    {
        var body = BodyOfLength((DefaultThreshold + WholeThreshold) / 2, SuccessBody);

        var result = await RunFilterAsync(() =>
        {
            InlineBudgetSignal.SetThreshold(WholeThreshold);
            return ValueTask.FromResult(TextResult(body));
        });

        Assert.That(TextOf(result), Is.EqualTo(body));
    }

    [Test]
    public async Task ErrorOnlyWithoutSignalUsesWhole()
    {
        var body = BodyOfLength((DefaultThreshold + WholeThreshold) / 2, ErrorBody);

        var result = await RunFilterAsync(() => ValueTask.FromResult(TextResult(body)));

        Assert.That(TextOf(result), Is.EqualTo(body));
    }

    [Test]
    public async Task NonErrorAboveDefaultOffloaded()
    {
        var body = BodyOfLength(DefaultThreshold + 1000, SuccessBody);

        var result = await RunFilterAsync(() => ValueTask.FromResult(TextResult(body)));

        var pointer = ParsePointer(result);
        Assert.That(pointer.Offloaded, Is.True);
        Assert.That(pointer.ResultId, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task SkipsWhenOversizeUnitRaised()
    {
        var body = BodyOfLength(DefaultThreshold + 1000, SuccessBody);

        var result = await RunFilterAsync(() =>
        {
            OversizeUnitSignal.Raise();
            return ValueTask.FromResult(TextResult(body));
        });

        Assert.That(TextOf(result), Is.EqualTo(body));
    }

    [Test]
    public async Task OverThresholdAfterMarshallerIsOffloadedWithCap()
    {
        var message = new string('x', WholeThreshold + 1000);
        var body = ErrorBody(message).ToJsonString();

        var result = await RunFilterAsync(() =>
        {
            InlineBudgetSignal.SetThreshold(WholeThreshold);
            return ValueTask.FromResult(TextResult(body));
        });

        var pointer = ParsePointer(result);
        Assert.That(pointer.ErrorData, Is.Not.Null);
        Assert.That(pointer.ErrorData!.Message.Length, Is.EqualTo(LargeResultHelper.DefaultBudgetChars));
        Assert.That(pointer.ErrorMessageTotalChars, Is.EqualTo(message.Length));
    }

    [Test]
    public async Task HonoursConfiguredDefault()
    {
        InlineBudgetOptions.Configure([$"{InlineBudgetOptions.BudgetCharsSetting.Flag}={2 * InlineBudgetOptions.StockBudgetChars}"]);
        var stockThreshold = (int)(InlineBudgetOptions.StockBudgetChars * LargeResultHelper.ToleranceFactor);
        var body = BodyOfLength((stockThreshold + DefaultThreshold) / 2, SuccessBody);
        Assert.That(body.Length, Is.GreaterThan(stockThreshold));

        var result = await RunFilterAsync(() => ValueTask.FromResult(TextResult(body)));

        Assert.That(TextOf(result), Is.EqualTo(body));
    }

    [Test]
    public async Task FailurePointerKeepsErrorCodeAndMessage()
    {
        const string message = "Edit rejected by the compile gate.";
        var body = BodyOfLength(WholeThreshold + 1000, pad => new JsonObject
        {
            ["isError"] = true,
            ["errorData"] = new JsonObject { ["errorCode"] = ErrorCode, ["message"] = message },
            ["detail"] = pad,
        });

        var result = await RunFilterAsync(() => ValueTask.FromResult(TextResult(body)));

        var pointer = ParsePointer(result);
        Assert.That(pointer.ErrorData, Is.Not.Null);
        Assert.That(pointer.ErrorData!.ErrorCode, Is.EqualTo(ErrorCode));
        Assert.That(pointer.ErrorData.Message, Is.EqualTo(message));
    }

    [Test]
    public async Task PointerWireNames()
    {
        const string statusMessage = "Found 'two' <items>";
        var body = BodyOfLength(DefaultThreshold + 1000, pad => new JsonObject
        {
            ["isError"] = false,
            ["statusMessage"] = statusMessage,
            ["totalRecords"] = 2,
            ["listSummary"] = new JsonObject { ["totalCount"] = 2, ["fileCount"] = 1 },
            ["successData"] = pad,
        });

        var result = await RunFilterAsync(() => ValueTask.FromResult(TextResult(body)));

        var text = TextOf(result);
        var pointer = ParsePointer(result);
        Assert.That(pointer.StatusMessage, Is.EqualTo(statusMessage));
        Assert.That(pointer.ItemCount, Is.EqualTo(2));
        Assert.That(TopLevelKeys(text), Is.EquivalentTo(new[]
        {
            "solutionRoot", "offloaded", "resultId", "sizeBytes", "sizeChars",
            "itemCount", "isError", "statusMessage", "listSummary", "message",
        }));
        Assert.That(NullValueKeys(text), Is.Empty);
        Assert.That(text, Does.Not.Contain("\\u00"));

        var failure = await RunFilterAsync(() => ValueTask.FromResult(TextResult(
            BodyOfLength(WholeThreshold + 1000, pad => new JsonObject
            {
                ["isError"] = true,
                ["errorData"] = new JsonObject { ["errorCode"] = ErrorCode, ["message"] = pad },
            }))));

        var failureText = TextOf(failure);
        Assert.That(TopLevelKeys(failureText), Does.Contain("errorData"));
        Assert.That(TopLevelKeys(failureText), Does.Contain("errorMessageTotalChars"));
        Assert.That(NullValueKeys(failureText), Is.Empty);
    }

    private ValueTask<CallToolResult> RunFilterAsync(Func<ValueTask<CallToolResult>> next) =>
        LargeResultOffloadFilter.InvokeAsync(ToolName, () => _tempRoot, null, next, CancellationToken.None);

    private static CallToolResult TextResult(string body) =>
        new() { Content = [new TextContentBlock { Text = body }] };

    private static string TextOf(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Single().Text;

    private static OffloadPointerResult ParsePointer(CallToolResult result) =>
        JsonSerializer.Deserialize<OffloadPointerResult>(TextOf(result), SentinelToolFactory.WireSerializerOptions)!;

    private static List<string> TopLevelKeys(string text) =>
        JsonNode.Parse(text)!.AsObject().Select(pair => pair.Key).ToList();

    private static List<string> NullValueKeys(string text) =>
        JsonNode.Parse(text)!.AsObject()
            .Where(pair => pair.Value is null || pair.Value.GetValueKind() == JsonValueKind.Null)
            .Select(pair => pair.Key)
            .ToList();

    private static JsonObject SuccessBody(string pad) => new()
    {
        ["isError"] = false,
        ["successData"] = pad,
    };

    private static JsonObject ErrorBody(string message) => new()
    {
        ["isError"] = true,
        ["errorData"] = new JsonObject { ["errorCode"] = ErrorCode, ["message"] = message },
    };

    // Pads the body with filler so its serialized length is exactly chars.
    private static string BodyOfLength(int chars, Func<string, JsonObject> build)
    {
        var emptyLength = build(string.Empty).ToJsonString().Length;
        var body = build(new string('x', chars - emptyLength)).ToJsonString();
        Assert.That(body.Length, Is.EqualTo(chars));
        return body;
    }
}
