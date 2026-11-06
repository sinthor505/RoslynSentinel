using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// proposal_sequence_pager.md stage A (slices 3, 4, type part of 7): <see cref="SequencePager"/> golden and
/// property-style tests on hand-built envelopes. Nothing here touches the marshaller or GetLargeResult.
/// </summary>
[TestFixture]
[Category("SequencePager")]
public class SequencePagerTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;

    // ---- helpers ----

    public sealed class DeclaredProbeData
    {
        [ResultRecords]
        public List<string> Declared { get; set; } = [];

        public List<string> Other { get; set; } = [];
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static int Len(JsonNode node) => node.ToJsonString(Wire).Length;

    /// <summary>A record of uniform serialized size (ids and line numbers have fixed width).</summary>
    private static JsonObject Rec(string prefix, int i) => new()
    {
        ["id"] = prefix + i.ToString("D4"),
        ["file"] = "src/Some/Path/File" + i.ToString("D4") + ".cs",
        ["line"] = 100 + (i % 800),
    };

    private static JsonArray Recs(string prefix, int count)
    {
        var array = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            array.Add(Rec(prefix, i));
        }

        return array;
    }

    private static JsonObject Envelope(JsonNode? successData)
    {
        var envelope = new JsonObject
        {
            ["toolCall"] = new JsonObject { ["toolCallId"] = "abc123", ["name"] = "Probe" },
            ["isError"] = false,
            ["findings"] = new JsonArray(),
            ["totalRecords"] = 3,
            ["hasMoreData"] = false,
            ["statusMessage"] = "pinned status",
        };
        envelope["successData"] = successData;
        return envelope;
    }

    private static SequenceDescriptor Resolve(JsonObject envelope, PagingHints? hints = null, Type? type = null, int maxUnitChars = SequencePager.DefaultMaxUnitChars) =>
        SequencePager.ResolveSequence(envelope, hints, type, Wire, maxUnitChars: maxUnitChars);

    /// <summary>Serialized length of the envelope with the whole sequence emptied (what the builder starts from).</summary>
    private static int EmptyLen(JsonObject envelope, SequenceDescriptor sequence) =>
        Len(SequencePager.BuildPage(envelope, sequence, int.MaxValue, 0, int.MaxValue, Wire).Page);

    private static List<string> Ids(SequencePage page, string path) =>
        ((JsonArray)ResultPager.SelectNode(page.Page, path)!).Select(n => (string)n!["id"]!).ToList();

    private static int Count(SequencePage page, string path) => ((JsonArray)ResultPager.SelectNode(page.Page, path)!).Count;

    /// <summary>Pages the whole sequence from offset 0 with the given limit and budget; returns every page.</summary>
    private static List<SequencePage> PageAll(JsonObject envelope, SequenceDescriptor sequence, int limit, int budget)
    {
        var pages = new List<SequencePage>();
        int? offset = 0;
        while (offset is not null)
        {
            SequencePage page = SequencePager.BuildPage(envelope, sequence, offset.Value, limit, budget, Wire);
            pages.Add(page);
            Assert.That(pages, Has.Count.LessThan(100_000), "paging does not terminate");
            offset = page.NextOffset;
        }

        return pages;
    }

    // ---- lists ----

    // Red with ResultPager.Fit today (callers keep 1, implementations about 99): both big lists share the page.
    [Test]
    public void TwoLargeLists_ShareThePage_NeitherStarved()
    {
        var summary = new JsonObject { ["total"] = 550, ["note"] = "pinned summary" };
        JsonObject envelope = Envelope(new JsonObject
        {
            ["callers"] = Recs("c", 300),
            ["implementations"] = Recs("i", 250),
            ["summary"] = summary,
        });
        SequenceDescriptor sequence = Resolve(envelope);
        int recordSize = Len(Rec("c", 0));
        int budget = EmptyLen(envelope, sequence) + (100 * recordSize) + 98;

        SequencePage page = SequencePager.BuildPage(envelope, sequence, 0, 0, budget, Wire);

        Assert.That(sequence.Kind, Is.EqualTo(SequenceKind.Lists));
        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.callers", "successData.implementations" }));
        Assert.That(page.Total, Is.EqualTo(550));
        Assert.That(page.Returned, Is.InRange(98, 100));
        Assert.That(Count(page, "successData.callers"), Is.InRange(49, 51));
        Assert.That(Count(page, "successData.implementations"), Is.InRange(49, 51));
        Assert.That(page.NextOffset, Is.EqualTo(page.Returned));
        Assert.That(page.ContractViolation, Is.False);
        Assert.That(Len(page.Page), Is.LessThanOrEqualTo(budget));

        // Everything outside the sequence is unchanged, the input is untouched.
        Assert.That(JsonNode.DeepEquals(page.Page["successData"]!["summary"], summary), Is.True);
        Assert.That(JsonNode.DeepEquals(page.Page["statusMessage"], envelope["statusMessage"]), Is.True);
        Assert.That(JsonNode.DeepEquals(page.Page["toolCall"], envelope["toolCall"]), Is.True);
        Assert.That(((JsonArray)envelope["successData"]!["callers"]!).Count, Is.EqualTo(300));
        Assert.That(page.Page.ContainsKey("omitted"), Is.False);
    }

    [Test]
    public void Interleaved_TakesRoundRobin_Linear_FillsTheFirstListFirst()
    {
        JsonObject envelope = Envelope(new JsonObject { ["a"] = Recs("a", 3), ["b"] = Recs("b", 2) });
        var interleaved = Resolve(envelope, new PagingHints(["a", "b"]));
        var linear = Resolve(envelope, new PagingHints(["a", "b"], PagingOrder.Linear));

        SequencePage inter = SequencePager.BuildPage(envelope, interleaved, 0, 3, int.MaxValue, Wire);
        SequencePage lin = SequencePager.BuildPage(envelope, linear, 0, 3, int.MaxValue, Wire);

        Assert.That(interleaved.Order, Is.EqualTo(PagingOrder.Interleaved));
        Assert.That(linear.Order, Is.EqualTo(PagingOrder.Linear));
        Assert.That(Ids(inter, "successData.a"), Is.EqualTo(new[] { "a0000", "a0001" }));
        Assert.That(Ids(inter, "successData.b"), Is.EqualTo(new[] { "b0000" }));
        Assert.That(Ids(lin, "successData.a"), Is.EqualTo(new[] { "a0000", "a0001", "a0002" }));
        Assert.That(Count(lin, "successData.b"), Is.EqualTo(0));
        Assert.That(inter.NextOffset, Is.EqualTo(3));
        Assert.That(lin.NextOffset, Is.EqualTo(3));
    }

    [Test]
    public void SmallList_SurvivesWhole_WhenTheOtherListIsLarge()
    {
        JsonObject envelope = Envelope(new JsonObject { ["results"] = Recs("r", 300), ["warnings"] = Recs("w", 4) });
        SequenceDescriptor sequence = Resolve(envelope);
        int budget = EmptyLen(envelope, sequence) + (100 * Len(Rec("r", 0))) + 100;

        SequencePage page = SequencePager.BuildPage(envelope, sequence, 0, 0, budget, Wire);

        Assert.That(Count(page, "successData.warnings"), Is.EqualTo(4));
        Assert.That(Count(page, "successData.results"), Is.InRange(94, 98));
        Assert.That(page.Total, Is.EqualTo(304));
    }

    [Test]
    public void UnequalLists_AfterTheSmallOneIsExhausted_TheLargeOneContinuesAlone()
    {
        JsonObject envelope = Envelope(new JsonObject { ["a"] = Recs("a", 3), ["b"] = Recs("b", 10) });
        SequenceDescriptor sequence = Resolve(envelope);

        // Interleaved order: a0 b0 a1 b1 a2 b2 b3 ... b9.
        SequencePage first = SequencePager.BuildPage(envelope, sequence, 0, 5, int.MaxValue, Wire);
        SequencePage rest = SequencePager.BuildPage(envelope, sequence, 5, 0, int.MaxValue, Wire);

        Assert.That(Count(first, "successData.a"), Is.EqualTo(3));
        Assert.That(Ids(first, "successData.b"), Is.EqualTo(new[] { "b0000", "b0001" }));
        Assert.That(Count(rest, "successData.a"), Is.EqualTo(0));
        Assert.That(Ids(rest, "successData.b"), Is.EqualTo(Enumerable.Range(2, 8).Select(i => "b" + i.ToString("D4"))));
        Assert.That(rest.Returned, Is.EqualTo(8));
        Assert.That(rest.NextOffset, Is.Null);
        Assert.That(rest.Total, Is.EqualTo(13));
    }

    [Test]
    public void BareArraySuccessData_IsOneList()
    {
        JsonObject envelope = Envelope(Recs("x", 300));
        SequenceDescriptor sequence = Resolve(envelope);
        int budget = EmptyLen(envelope, sequence) + (50 * Len(Rec("x", 0))) + 49;

        List<SequencePage> pages = PageAll(envelope, sequence, limit: 0, budget);

        Assert.That(sequence.Kind, Is.EqualTo(SequenceKind.Lists));
        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData" }));
        Assert.That(pages[0].Page["successData"], Is.InstanceOf<JsonArray>());
        Assert.That(pages[0].Returned, Is.EqualTo(50));
        Assert.That(pages, Has.Count.EqualTo(6));
        var all = pages.SelectMany(p => ((JsonArray)p.Page["successData"]!).Select(n => (string)n!["id"]!)).ToList();
        Assert.That(all, Is.EqualTo(Enumerable.Range(0, 300).Select(i => "x" + i.ToString("D4"))));
    }

    // ---- lines ----

    private static string Numbered(int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            sb.Append("line ").Append(i.ToString("D3")).Append("\r\n");
        }

        return sb.ToString();
    }

    private static JsonObject FileEnvelope(string text) =>
        Envelope(new JsonObject { ["filePath"] = "src/F.cs", ["startLine"] = 1, ["source"] = text });

    [Test]
    public void StringLines_PageChain_ReconstructsTheExactText()
    {
        string text = Numbered(200);
        JsonObject envelope = FileEnvelope(text);
        SequenceDescriptor sequence = Resolve(envelope);
        int budget = EmptyLen(envelope, sequence) + (12 * 30);

        List<SequencePage> pages = PageAll(envelope, sequence, limit: 0, budget);

        Assert.That(sequence.Kind, Is.EqualTo(SequenceKind.Lines));
        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.source" }));
        Assert.That(pages, Has.Count.EqualTo(7));
        Assert.That(pages[0].Total, Is.EqualTo(200));
        Assert.That(pages[0].Returned, Is.EqualTo(30));
        Assert.That(pages.All(p => Len(p.Page) <= budget && !p.ContractViolation), Is.True);
        string rebuilt = string.Concat(pages.Select(p => (string)p.Page["successData"]!["source"]!));
        Assert.That(rebuilt, Is.EqualTo(text));
        Assert.That(JsonNode.DeepEquals(pages[3].Page["successData"]!["filePath"], envelope["successData"]!["filePath"]), Is.True);
        Assert.That(pages[^1].NextOffset, Is.Null);
    }

    [Test]
    public void StringLines_OffsetAndLimit_SelectTheLines()
    {
        string text = Numbered(40);
        JsonObject envelope = FileEnvelope(text);
        SequenceDescriptor sequence = Resolve(envelope);

        SequencePage page = SequencePager.BuildPage(envelope, sequence, 10, 5, int.MaxValue, Wire);

        Assert.That((string)page.Page["successData"]!["source"]!, Is.EqualTo(string.Concat(Enumerable.Range(10, 5).Select(i => "line " + i.ToString("D3") + "\r\n"))));
        Assert.That(page.Offset, Is.EqualTo(10));
        Assert.That(page.Returned, Is.EqualTo(5));
        Assert.That(page.NextOffset, Is.EqualTo(15));
        Assert.That(page.Total, Is.EqualTo(40));
    }

    [Test]
    public void StringSuccessData_IsTheLinesSequenceItself()
    {
        JsonObject envelope = Envelope(Numbered(10));

        SequenceDescriptor sequence = Resolve(envelope);
        SequencePage page = SequencePager.BuildPage(envelope, sequence, 4, 2, int.MaxValue, Wire);

        Assert.That(sequence.Kind, Is.EqualTo(SequenceKind.Lines));
        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData" }));
        Assert.That((string)page.Page["successData"]!, Is.EqualTo("line 004\r\nline 005\r\n"));
    }

    [Test]
    public void OverlongLine_IsSplitIntoChunks_AndTheChunksReconstructTheLine()
    {
        string longLine = new('x', 2500);
        string text = "head\n" + longLine + "\ntail\n";
        JsonObject envelope = FileEnvelope(text);
        SequenceDescriptor sequence = Resolve(envelope, maxUnitChars: 1000);
        int budget = EmptyLen(envelope, sequence) + 1100;

        List<SequencePage> pages = PageAll(envelope, sequence, limit: 0, budget);

        // head, three chunks of the long line (1000, 1000, 500 plus the terminator), tail.
        Assert.That(SequencePager.CountUnits(envelope, sequence), Is.EqualTo(5));
        Assert.That(pages[0].Total, Is.EqualTo(5));
        Assert.That(pages.All(p => Len(p.Page) <= budget && !p.ContractViolation), Is.True);
        Assert.That(string.Concat(pages.Select(p => (string)p.Page["successData"]!["source"]!)), Is.EqualTo(text));
        Assert.That(pages.Count, Is.GreaterThan(1));
    }

    // ---- unit rule ----

    [Test]
    public void OversizeRecord_GoesAlone_WithAContractViolation_AndPagingAdvances()
    {
        JsonArray items = Recs("i", 5);
        ((JsonObject)items[2]!)["pad"] = new string('p', 5000);
        JsonObject envelope = Envelope(new JsonObject { ["items"] = items });
        SequenceDescriptor sequence = Resolve(envelope);
        int budget = EmptyLen(envelope, sequence) + (2 * Len(Rec("i", 0))) + 1 + 10;

        SequencePage first = SequencePager.BuildPage(envelope, sequence, 0, 0, budget, Wire);
        SequencePage second = SequencePager.BuildPage(envelope, sequence, first.NextOffset!.Value, 0, budget, Wire);
        SequencePage third = SequencePager.BuildPage(envelope, sequence, second.NextOffset!.Value, 0, budget, Wire);

        Assert.That(Ids(first, "successData.items"), Is.EqualTo(new[] { "i0000", "i0001" }));
        Assert.That(first.NextOffset, Is.EqualTo(2));
        Assert.That(first.ContractViolation, Is.False);

        Assert.That(Ids(second, "successData.items"), Is.EqualTo(new[] { "i0002" }));
        Assert.That(second.ContractViolation, Is.True);
        Assert.That(second.ViolationDetail, Does.Contain("successData.items[2]"));
        Assert.That(second.NextOffset, Is.EqualTo(3));
        Assert.That(JsonNode.DeepEquals(((JsonArray)second.Page["successData"]!["items"]!)[0], items[2]), Is.True, "the record keeps its original JSON");

        Assert.That(Ids(third, "successData.items"), Is.EqualTo(new[] { "i0003", "i0004" }));
        Assert.That(third.ContractViolation, Is.False);
        Assert.That(third.NextOffset, Is.Null);
    }

    [Test]
    public void OversizeLineChunk_GoesAlone_WithAContractViolation()
    {
        JsonObject envelope = FileEnvelope("a\n" + new string('z', 300) + "\nb\n");
        SequenceDescriptor sequence = Resolve(envelope, maxUnitChars: 1000);
        int budget = EmptyLen(envelope, sequence) + 20;

        SequencePage second = SequencePager.BuildPage(envelope, sequence, 1, 0, budget, Wire);

        Assert.That(second.Returned, Is.EqualTo(1));
        Assert.That(second.ContractViolation, Is.True);
        Assert.That(second.ViolationDetail, Does.Contain("successData.source"));
        Assert.That(second.NextOffset, Is.EqualTo(2));
    }

    // ---- property-style: every unit exactly once ----

    [TestCase(PagingOrder.Interleaved, 0, 1)]
    [TestCase(PagingOrder.Interleaved, 0, 3)]
    [TestCase(PagingOrder.Interleaved, 0, 10)]
    [TestCase(PagingOrder.Interleaved, 7, 1000)]
    [TestCase(PagingOrder.Linear, 0, 1)]
    [TestCase(PagingOrder.Linear, 0, 4)]
    [TestCase(PagingOrder.Linear, 1, 1000)]
    public void PagingAcrossTheWholeResult_VisitsEveryListUnitExactlyOnce(PagingOrder order, int limit, int recordsPerPage)
    {
        JsonObject envelope = Envelope(new JsonObject
        {
            ["a"] = Recs("a", 23),
            ["b"] = Recs("b", 5),
            ["c"] = Recs("c", 40),
            ["summary"] = new JsonObject { ["n"] = 68 },
        });
        SequenceDescriptor sequence = Resolve(envelope, new PagingHints(["a", "b", "c"], order));
        int budget = EmptyLen(envelope, sequence) + (recordsPerPage * (Len(Rec("a", 0)) + 1));

        List<SequencePage> pages = PageAll(envelope, sequence, limit, budget);

        var visited = new List<string>();
        int expectedOffset = 0;
        foreach (SequencePage page in pages)
        {
            Assert.That(page.Offset, Is.EqualTo(expectedOffset));
            Assert.That(page.Returned, Is.GreaterThan(0));
            Assert.That(page.Total, Is.EqualTo(68));
            Assert.That(page.ContractViolation, Is.False);
            Assert.That(Len(page.Page), Is.LessThanOrEqualTo(budget));
            if (limit > 0)
            {
                Assert.That(page.Returned, Is.LessThanOrEqualTo(limit));
            }

            expectedOffset += page.Returned;
            foreach (string path in sequence.Paths)
            {
                visited.AddRange(Ids(page, path));
            }
        }

        Assert.That(expectedOffset, Is.EqualTo(68));
        Assert.That(visited, Has.Count.EqualTo(68));
        Assert.That(visited.Distinct().Count(), Is.EqualTo(68));
        Assert.That(pages[^1].NextOffset, Is.Null);
    }

    [TestCase(0, 60)]
    [TestCase(0, 200)]
    [TestCase(5, 1000)]
    [TestCase(1, 1000)]
    public void PagingAcrossTheWholeResult_VisitsEveryLineUnitExactlyOnce(int limit, int extraChars)
    {
        var text = new StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            text.Append(new string((char)('a' + (i % 26)), (i * 37) % 120));
            text.Append(i == 59 ? string.Empty : "\n");
        }

        JsonObject envelope = FileEnvelope(text.ToString());
        SequenceDescriptor sequence = Resolve(envelope, maxUnitChars: 50);
        int budget = EmptyLen(envelope, sequence) + extraChars;

        List<SequencePage> pages = PageAll(envelope, sequence, limit, budget);

        int total = SequencePager.CountUnits(envelope, sequence);
        Assert.That(total, Is.GreaterThan(60), "overlong lines add chunks");
        Assert.That(pages.Sum(p => p.Returned), Is.EqualTo(total));
        Assert.That(pages.All(p => Len(p.Page) <= budget && !p.ContractViolation), Is.True);
        Assert.That(string.Concat(pages.Select(p => (string)p.Page["successData"]!["source"]!)), Is.EqualTo(text.ToString()));
    }

    [Test]
    public void OffsetBeyondTotal_GivesAnEmptyPageAndNoNextOffset()
    {
        JsonObject envelope = Envelope(new JsonObject { ["a"] = Recs("a", 3), ["b"] = Recs("b", 2) });
        SequenceDescriptor sequence = Resolve(envelope);

        SequencePage page = SequencePager.BuildPage(envelope, sequence, 99, 10, 100_000, Wire);

        Assert.That(page.Returned, Is.EqualTo(0));
        Assert.That(page.Total, Is.EqualTo(5));
        Assert.That(page.Offset, Is.EqualTo(5));
        Assert.That(page.NextOffset, Is.Null);
        Assert.That(page.ContractViolation, Is.False);
        Assert.That(Count(page, "successData.a"), Is.EqualTo(0));
        Assert.That(Count(page, "successData.b"), Is.EqualTo(0));
    }

    [Test]
    public void NegativeOffset_IsTreatedAsZero()
    {
        JsonObject envelope = Envelope(new JsonObject { ["a"] = Recs("a", 3) });

        SequencePage page = SequencePager.BuildPage(envelope, Resolve(envelope), -4, 2, 100_000, Wire);

        Assert.That(page.Offset, Is.EqualTo(0));
        Assert.That(Ids(page, "successData.a"), Is.EqualTo(new[] { "a0000", "a0001" }));
    }

    [Test]
    public void PartsOutsideTheSequenceOverBudget_AreFlagged_SoTheCallerCanCutThemWithFit()
    {
        JsonObject envelope = Envelope(new JsonObject { ["items"] = Recs("i", 5), ["blob"] = new JsonObject { ["x"] = new string('z', 5000) } });
        SequenceDescriptor sequence = Resolve(envelope);

        SequencePage page = SequencePager.BuildPage(envelope, sequence, 0, 0, 1000, Wire);
        SequencePage small = SequencePager.BuildPage(envelope, sequence, 0, 0, 100_000, Wire);

        Assert.That(page.PartsOutsideOverBudget, Is.True);
        Assert.That(page.ContractViolation, Is.True);
        Assert.That(page.ViolationDetail, Does.Contain("outside the sequence"));
        Assert.That(small.PartsOutsideOverBudget, Is.False);
        Assert.That(small.NextOffset, Is.Null);
    }

    // ---- selection ----

    [Test]
    public void UnresolvedHintPath_IsIgnoredAndLogged()
    {
        JsonObject envelope = Envelope(new JsonObject { ["callers"] = Recs("c", 3), ["implementations"] = Recs("i", 3), ["name"] = "x" });
        var logger = new CapturingLogger();

        SequenceDescriptor sequence = SequencePager.ResolveSequence(
            envelope, new PagingHints(["callers", "typo", "name"]), null, Wire, logger);

        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.callers" }));
        Assert.That(logger.Messages, Has.Count.EqualTo(2));
        Assert.That(logger.Messages[0], Does.Contain("typo"));
        Assert.That(logger.Messages[1], Does.Contain("name"));
    }

    [Test]
    public void AllHintPathsUnresolved_FallBackToTheDefaultSequence()
    {
        JsonObject envelope = Envelope(new JsonObject { ["callers"] = Recs("c", 3), ["implementations"] = Recs("i", 3) });

        SequenceDescriptor sequence = Resolve(envelope, new PagingHints(["typo"]));

        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.callers", "successData.implementations" }));
    }

    [Test]
    public void HintPaths_AcceptRootedPathsAndPascalCaseNames()
    {
        JsonObject envelope = Envelope(new JsonObject { ["callers"] = Recs("c", 3), ["implementations"] = Recs("i", 3) });

        SequenceDescriptor sequence = Resolve(envelope, new PagingHints(["successData.implementations", "Callers", "callers"]));

        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.implementations", "successData.callers" }));
    }

    [Test]
    public void Precedence_HintsOverDeclaredAttribute_OverDefault()
    {
        JsonObject envelope = Envelope(new JsonObject { ["declared"] = Recs("d", 3), ["other"] = Recs("o", 3) });

        SequenceDescriptor hinted = Resolve(envelope, new PagingHints(["other"]), typeof(DeclaredProbeData));
        SequenceDescriptor declared = Resolve(envelope, null, typeof(DeclaredProbeData));
        SequenceDescriptor byDefault = Resolve(envelope, null, null);

        Assert.That(hinted.Paths, Is.EqualTo(new[] { "successData.other" }));
        Assert.That(declared.Paths, Is.EqualTo(new[] { "successData.declared" }));
        Assert.That(byDefault.Paths, Is.EqualTo(new[] { "successData.declared", "successData.other" }));
    }

    [Test]
    public void EmptyDeclaredList_FallsThroughToTheDefaultSequence()
    {
        // The TextSearchResult regex case: the declared literal list is empty, the other list holds the hits.
        JsonObject envelope = Envelope(new JsonObject { ["declared"] = new JsonArray(), ["other"] = Recs("o", 3) });

        SequenceDescriptor sequence = Resolve(envelope, null, typeof(DeclaredProbeData));

        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.other" }), "empty arrays are ignored by the default rule (Stage 1)");
        Assert.That(SequencePager.CountUnits(envelope, sequence), Is.EqualTo(3));
    }

    [Test]
    public void NoArrayAndNoString_ResolvesToNone_AndBuildPageReturnsAnUnchangedCopy()
    {
        JsonObject scalars = Envelope(new JsonObject { ["count"] = 3, ["flag"] = true });
        JsonObject dictionary = Envelope(new JsonObject { ["contents"] = new JsonObject { ["a.cs"] = "text" } });

        SequenceDescriptor none = Resolve(scalars);
        SequencePage page = SequencePager.BuildPage(scalars, none, 0, 0, 10, Wire);

        Assert.That(none.Kind, Is.EqualTo(SequenceKind.None));
        Assert.That(Resolve(dictionary).Kind, Is.EqualTo(SequenceKind.None));
        Assert.That(Resolve(Envelope(null)).Kind, Is.EqualTo(SequenceKind.None));
        Assert.That(JsonNode.DeepEquals(page.Page, scalars), Is.True);
        Assert.That(page.Returned, Is.EqualTo(0));
        Assert.That(page.NextOffset, Is.Null);
    }

    [Test]
    public void ArraysWinOverStrings_WhenTheyAreTheLargerPart_AndTheLongestStringWinsWithoutArrays()
    {
        // 20 records serialize to well over the 500-char string, so the lists are what is over budget (a smaller array than the
        // string would lose: see LargeStringPlusSmallNonEmptyArray_PagesByLines_WithTheArrayIntact).
        JsonObject withArray = Envelope(new JsonObject { ["source"] = new string('s', 500), ["items"] = Recs("i", 20) });
        JsonObject withStrings = Envelope(new JsonObject { ["filePath"] = "a.cs", ["source"] = "long text\nhere\n", ["signature"] = "void M()" });

        Assert.That(Resolve(withArray).Kind, Is.EqualTo(SequenceKind.Lists));
        SequenceDescriptor lines = Resolve(withStrings);
        Assert.That(lines.Kind, Is.EqualTo(SequenceKind.Lines));
        Assert.That(lines.Paths, Is.EqualTo(new[] { "successData.source" }));
    }

    // G12: GetMethodSource on a 19.7 KB method has "attributes": [] beside "source"; the empty array used to win and the page
    // held the whole empty sequence, so nothing could be paged.
    [Test]
    public void LargeStringPlusAnEmptyArray_PagesByLines()
    {
        string text = Numbered(2000);
        JsonObject envelope = Envelope(new JsonObject { ["signature"] = "void M()", ["source"] = text, ["attributes"] = new JsonArray() });

        SequenceDescriptor sequence = Resolve(envelope);
        SequencePage page = SequencePager.BuildPage(envelope, sequence, 0, 0, 3000, Wire);

        Assert.That(sequence.Kind, Is.EqualTo(SequenceKind.Lines));
        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.source" }));
        Assert.That(page.Total, Is.EqualTo(2000));
        Assert.That(page.Returned, Is.GreaterThan(0).And.LessThan(2000));
        Assert.That(page.NextOffset, Is.EqualTo(page.Returned), "paging advances through the lines");
        Assert.That(page.ContractViolation, Is.False);
        Assert.That((JsonArray)page.Page["successData"]!["attributes"]!, Is.Empty);
    }

    [Test]
    public void LargeStringPlusASmallNonEmptyArray_PagesByLines_WithTheArrayIntact()
    {
        string text = Numbered(2000);
        JsonArray small = Recs("a", 2);
        JsonObject envelope = Envelope(new JsonObject { ["source"] = text, ["attributes"] = small });

        SequenceDescriptor sequence = Resolve(envelope);
        SequencePage page = SequencePager.BuildPage(envelope, sequence, 0, 0, 3000, Wire);

        Assert.That(sequence.Kind, Is.EqualTo(SequenceKind.Lines));
        Assert.That(page.NextOffset, Is.Not.Null);
        Assert.That(JsonNode.DeepEquals(page.Page["successData"]!["attributes"], small), Is.True, "the small list stays whole on every page");
        Assert.That(Len(page.Page), Is.LessThanOrEqualTo(3000));
    }

    [Test]
    public void LargeArraysBesideASmallString_StillPageAsLists()
    {
        JsonObject envelope = Envelope(new JsonObject { ["signature"] = "void M()", ["callers"] = Recs("c", 300), ["implementations"] = Recs("i", 250) });

        SequenceDescriptor sequence = Resolve(envelope);

        Assert.That(sequence.Kind, Is.EqualTo(SequenceKind.Lists));
        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.callers", "successData.implementations" }));
    }

    [Test]
    public void EmptyArraysAndNoString_ResolveToNone_SoTheCallerKeepsFit()
    {
        JsonObject envelope = Envelope(new JsonObject { ["count"] = 3, ["items"] = new JsonArray(), ["more"] = new JsonArray() });

        Assert.That(Resolve(envelope).Kind, Is.EqualTo(SequenceKind.None));
    }

    [Test]
    public void EmptyArrays_DoNotJoinTheListSequence()
    {
        JsonObject envelope = Envelope(new JsonObject { ["callers"] = Recs("c", 30), ["warnings"] = new JsonArray(), ["implementations"] = Recs("i", 30) });

        SequenceDescriptor sequence = Resolve(envelope);

        Assert.That(sequence.Kind, Is.EqualTo(SequenceKind.Lists));
        Assert.That(sequence.Paths, Is.EqualTo(new[] { "successData.callers", "successData.implementations" }));
    }

    [Test]
    public void PagingHints_IsNotSerializedOnTheEnvelope()
    {
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = new { callers = new[] { "a" } },
            Paging = new PagingHints(["callers"], PagingOrder.Linear),
        };

        string json = JsonSerializer.Serialize(result, Wire);

        Assert.That(json, Does.Not.Contain("aging"), json);
        Assert.That(json, Does.Not.Contain("recordsPaths"), json);
        Assert.That(result.Paging!.Order, Is.EqualTo(PagingOrder.Linear));
    }
}
