using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// The deterministic replacement for the retired ModelEval ReadFile-only scenario: ReadFile on the 300-method TierRates.cs
/// fixture goes through the real marshaller (mode On), then GetLargeResult is followed by nextOffset alone until none is
/// left. Pins the page offsets, that the file comes back once and whole, and that every response carries exactly one
/// paging instruction in the sequence-pager wording.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ReadFilePagingLoopTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;
    private static readonly MethodInfo AnyMethod = typeof(ReadFilePagingLoopTests).GetMethod(nameof(AnyMethodTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    // The marshaller's inline budget minus its echo reserve: a whole response must never exceed it.
    private const int PageBudget = 15360 - 2560;

    // Reproduced from LargeResultPagingAgentTests.BuildTierRatesSource (ModelEval cannot be referenced from here): keep the two in step.
    private const int RateMethodCount = 300;
    private const int NeedleMethodNumber = 137;
    private const string NeedleMethodName = "Rate_PlatinumAnnualContract";
    private const string NeedleLiteral = "0.1375m";

    // Wording that must never appear in any response of the loop (the old omitted/path flow, ReadFile's own re-query hint).
    private static readonly (string What, string Text)[] ForbiddenEverywhere =
    [
        ("the old omitted flow", "omitted"),
        ("a path argument", "path:"),
        ("ReadFile's re-query hint", "ReadFile("),
    ];

    // Message wording by position in the chain; change here when the wording changes.
    private const string FirstPageStart = "INCOMPLETE: lines 1-";
    private const string MiddlePageStart = "INCOMPLETE: lines ";
    private const string LastPageStart = "COMPLETE: lines ";
    private const string LastPageEnd = "end of the sequence";
    private const string InstructionMarker = "GetLargeResult(";

    // PINNED from a real run on the 300-method fixture (budget 12800 chars, 64-char margin on later pages); they change only when
    // the fixture, the page budget or the response shape changes, and then must be re-observed, not guessed.
    private static readonly int[] PinnedPageOffsets = [0, 310, 620, 928, 1233, 1541, 1849];
    private const int PinnedNeedlePageOffset = 928;

    // TODO share these SetUp/helpers with SequenceLargeResultPagingTests through a common base fixture instead of the copy below.
    private IWorkspaceManager _workspaceManager = null!;
    private WorkspaceTools _tools = null!;
    private string _tempDir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), "ReadFileLoop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", []));
        _tools = Battery.Basic.TestTools.WorkspaceTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static void AnyMethodTarget()
    {
    }

    private static int WireLength(object result) =>
        JsonSerializer.Serialize(result, Wire.GetTypeInfo(typeof(object))).Length;

    /// <summary>Deterministic TierRates.cs body: 300 one-expression rate methods, the 137th is the Platinum annual-contract needle.</summary>
    private static string BuildTierRatesSource()
    {
        var sb = new StringBuilder();
        sb.Append("namespace ContosoOrders.Core.FixtureHelpers;\r\n\r\n");
        sb.Append("public static class TierRates\r\n{\r\n");
        for (var i = 1; i <= RateMethodCount; i++)
        {
            var isNeedle = i == NeedleMethodNumber;
            var name = isNeedle ? NeedleMethodName : $"Rate_Tier{i:000}_Sku{i:0000}";
            var literal = isNeedle ? NeedleLiteral : $"0.{(i * 37) % 900 + 100:000}m";
            var summary = isNeedle
                ? "Platinum annual-contract rate: applies the platinum annual-contract percentage to the order amount."
                : $"Tier {i:000} SKU {i:0000} rate: applies a fixed percentage to the order amount.";
            sb.Append($"    /// <summary>{summary}</summary>\r\n");
            sb.Append($"    public static decimal {name}(decimal amount)\r\n");
            sb.Append("    {\r\n");
            sb.Append($"        var rate = {literal};\r\n");
            sb.Append("        return amount * rate;\r\n");
            sb.Append("    }\r\n\r\n");
        }

        sb.Append("}\r\n");
        return sb.ToString();
    }

    private static List<JsonNode?> Occurrences(JsonNode? node, string name)
    {
        var found = new List<JsonNode?>();
        void Walk(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if (key == name)
                        {
                            found.Add(value);
                        }

                        Walk(value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Walk(item);
                    }

                    break;
            }
        }

        Walk(node);
        return found;
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Test]
    public async Task WholeFileRead_FollowedByNextOffsetAlone_PagesTheFileOnce_WithOneInstructionPerResponse()
    {
        string source = BuildTierRatesSource();
        string path = Path.Combine(_tempDir, "TierRates.cs");
        await File.WriteAllTextAsync(path, source);

        var read = await _tools.ReadFileTool(reason: "test message", path);
        Assert.That(read.IsError, Is.False, read.ErrorData?.Message);
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance, () => _tempDir, () => ResultMarshallerMode.On);
        object? marshalled = await marshaller.MarshalAsync("ReadFile", AnyMethod, read, typeof(object), Wire, CancellationToken.None);
        var first = JsonNode.Parse((string)marshalled!)!;
        JsonNode pointer = first["largeResult"]!;
        string resultId = pointer["resultId"]!.GetValue<string>();

        var responses = new List<(int Offset, string Text, JsonNode Json, string Message, int? Next)>
        {
            (0, first["successData"]!["source"]!.GetValue<string>(), first, pointer["message"]!.GetValue<string>(), pointer["nextOffset"]?.GetValue<int>()),
        };
        int? next = responses[0].Next;
        for (int guard = 0; next is int offset && guard < 100; guard++)
        {
            var result = await _tools.GetLargeResultTool(reason: "test message", resultId: resultId, offset: offset);
            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            Assert.That(WireLength(result), Is.LessThanOrEqualTo(PageBudget));
            var page = (EnvelopePageResult)result.SuccessData!;
            JsonNode json = JsonSerializer.SerializeToNode(result, Wire.GetTypeInfo(typeof(object)))!;
            responses.Add((offset, page.Envelope!["successData"]!["source"]!.GetValue<string>(), json, result.StatusMessage!, page.NextOffset));
            next = page.NextOffset;
        }

        for (int i = 0; i < responses.Count; i++)
        {
            var (offset, _, json, message, nextOffset) = responses[i];
            bool isLast = i == responses.Count - 1;
            string wireText = json.ToJsonString();
            string where = $"response {i} (offset {offset})";

            foreach (var (what, text) in ForbiddenEverywhere)
            {
                Assert.That(wireText, Does.Not.Contain(text), $"{where} must not carry {what}");
            }

            Assert.That(CountOf(wireText, InstructionMarker), Is.EqualTo(isLast ? 0 : 1), $"{where}: exactly one paging instruction, none on the last page");
            var flags = Occurrences(json, "hasMoreData").Select(n => n!.GetValue<bool>()).ToList();
            Assert.That(flags, Is.Not.Empty.And.All.EqualTo(!isLast), $"{where}: every hasMoreData agrees with the cursor");
            Assert.That(Occurrences(json, "nextOffset").Count(n => n is not null), Is.EqualTo(isLast ? 0 : 1), $"{where}: exactly one nextOffset");
            Assert.That(nextOffset.HasValue, Is.EqualTo(!isLast));
            if (i == 0)
            {
                Assert.That(message, Does.StartWith(FirstPageStart));
            }
            else if (isLast)
            {
                Assert.That(message, Does.StartWith(LastPageStart).And.Contain(LastPageEnd));
            }
            else
            {
                Assert.That(message, Does.StartWith(MiddlePageStart));
            }
        }

        string all = string.Concat(responses.Select(r => r.Text));
        Assert.That(all, Is.EqualTo(source), "the pages concatenate to the original file");
        Assert.That(CountOf(all, NeedleMethodName), Is.EqualTo(1));
        Assert.That(CountOf(all, NeedleLiteral), Is.EqualTo(1));
        var needlePages = responses.Where(r => r.Text.Contains(NeedleMethodName, StringComparison.Ordinal)).Select(r => r.Offset).ToList();
        Assert.That(needlePages, Is.EqualTo(new[] { PinnedNeedlePageOffset }), "the needle sits on one page, at the pinned offset");
        Assert.That(responses.Select(r => r.Offset).ToArray(), Is.EqualTo(PinnedPageOffsets), $"pinned offsets chain without gap or overlap; actual {string.Join(", ", responses.Select(r => r.Offset))}");
    }

    // G11: a ranged read bigger than the budget used to be trimmed to a shorter endLine with a "Range shortened... call ReadFile(startLine)"
    // status that the sequence pager then stripped, so lines past the trimmed end had no instruction pointing at them. Now the requested
    // range is returned whole and the marshaller pages it by lines to exactly the requested endLine.
    [Test]
    public async Task LargeRange_PagesThroughToTheRequestedEndLine_WithNothingShortened()
    {
        const int requestedEnd = 1500;
        string source = BuildTierRatesSource();
        string path = Path.Combine(_tempDir, "TierRates.cs");
        await File.WriteAllTextAsync(path, source);
        List<string> sourceLines = ResultPager.SplitLines(source);
        Assert.That(sourceLines.Count, Is.GreaterThan(requestedEnd), "fixture sanity: the range ends inside the file");

        var read = await _tools.ReadFileTool(reason: "test message", path, startLine: 1, endLine: requestedEnd);
        Assert.That(read.IsError, Is.False, read.ErrorData?.Message);
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance, () => _tempDir, () => ResultMarshallerMode.On);
        object? marshalled = await marshaller.MarshalAsync("ReadFile", AnyMethod, read, typeof(object), Wire, CancellationToken.None);
        var first = JsonNode.Parse((string)marshalled!)!;
        JsonNode pointer = first["largeResult"]!;
        string resultId = pointer["resultId"]!.GetValue<string>();

        Assert.That(first["successData"]!["endLine"]!.GetValue<int>(), Is.EqualTo(requestedEnd), "the requested endLine is reported, not a trimmed one");
        Assert.That(first.ToJsonString(), Does.Not.Contain("shortened"));
        Assert.That(pointer["totalRecords"]!.GetValue<int>(), Is.EqualTo(requestedEnd), "the sequence is the requested lines only");

        var collected = new StringBuilder(first["successData"]!["source"]!.GetValue<string>());
        string lastMessage = pointer["message"]!.GetValue<string>();
        int? next = pointer["nextOffset"]?.GetValue<int>();
        for (int guard = 0; next is int offset && guard < 100; guard++)
        {
            var result = await _tools.GetLargeResultTool(reason: "test message", resultId: resultId, offset: offset);
            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            var page = (EnvelopePageResult)result.SuccessData!;
            collected.Append(page.Envelope!["successData"]!["source"]!.GetValue<string>());
            lastMessage = result.StatusMessage!;
            next = page.NextOffset;
        }

        Assert.That(collected.ToString(), Is.EqualTo(string.Concat(sourceLines.Take(requestedEnd))), "pages cover lines 1..requestedEnd exactly, no gap and no line beyond");
        Assert.That(lastMessage, Does.StartWith("COMPLETE:").And.Contain($"-{requestedEnd})"), "the last page ends at the requested line");
    }
}
