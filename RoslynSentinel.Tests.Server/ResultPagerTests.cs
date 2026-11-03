using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using ModelContextProtocol;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// plan_wire_result_marshaller.md Step 3: <see cref="ResultPager"/>. Golden payloads are embedded resources under
/// Resources/Golden (synthetic, real envelope shape; regenerate with scripts/New-WireResultGoldens.ps1); the
/// property-style cases build small envelopes in code.
/// </summary>
[TestFixture]
[Category("ResultPager")]
public class ResultPagerTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;

    // ---- helpers ----

    private static JsonNode Load(string fileName)
    {
        var assembly = typeof(ResultPagerTests).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonNode.Parse(stream)!;
    }

    private static int Len(JsonNode node) => node.ToJsonString(Wire).Length;

    private static JsonObject Envelope(JsonNode? successData = null, JsonNode? errorData = null)
    {
        var envelope = new JsonObject
        {
            ["toolCall"] = new JsonObject { ["toolCallId"] = "abc123", ["name"] = "Probe" },
            ["isError"] = errorData is not null,
            ["findings"] = new JsonArray(),
            ["totalRecords"] = 3,
            ["hasMoreData"] = false,
            ["warningDetails"] = "pinned warning",
            ["workspaceVersion"] = 7,
            ["statusMessage"] = "pinned status",
            ["listSummary"] = new JsonObject { ["totalCount"] = 3 },
        };
        if (successData is not null)
        {
            envelope["successData"] = successData;
        }

        if (errorData is not null)
        {
            envelope["errorData"] = errorData;
        }

        return envelope;
    }

    private static void AssertPinnedKeysSurvive(JsonNode original, JsonNode inline)
    {
        foreach (var property in original.AsObject())
        {
            if (property.Key is "successData" or "errorData")
            {
                continue;
            }

            Assert.That(JsonNode.DeepEquals(property.Value, inline[property.Key]), Is.True, "pinned key '" + property.Key + "' changed");
        }

        if (original["errorData"] is JsonObject originalError)
        {
            foreach (string key in new[] { "errorCode", "message" })
            {
                Assert.That(JsonNode.DeepEquals(originalError[key], inline["errorData"]?[key]), Is.True, "errorData." + key + " changed");
            }
        }
    }

    private static OmittedPart Part(IReadOnlyList<OmittedPart> omitted, string path) =>
        omitted.Single(p => p.Path == path);

    // ---- hinted success-data types ----

    public sealed class SearchProbeData
    {
        public List<string> LiteralResults { get; set; } = [];

        [ResultRecords]
        public List<string> RegexResults { get; set; } = [];

        public bool RegexPatternValid { get; set; }
    }

    public sealed class NeverInlineProbeData
    {
        public int Count { get; set; }

        [NeverInline]
        public string Contents { get; set; } = string.Empty;

        [NeverInline]
        [JsonPropertyName("full_list")]
        public List<string> FullList { get; set; } = [];

        [NeverInline]
        public object? Blob { get; set; }

        public List<string> Visible { get; set; } = [];
    }

    // ---- golden payloads ----

    [Test]
    public void Golden_Build2000Errors_CutsTheDiagnosticsList()
    {
        JsonNode original = Load("build_2000_errors.json");
        const int budget = 20_000;

        var (inline, omitted) = ResultPager.Fit(original, budget, Wire, successDataType: null);

        Assert.That(Len(inline), Is.LessThanOrEqualTo(budget));
        Assert.That(omitted, Has.Count.EqualTo(1));
        var part = Part(omitted, "successData.details");
        Assert.That(part.Total, Is.EqualTo(2000));
        Assert.That(part.Returned, Is.InRange(1, 1999));
        Assert.That(part.Unit, Is.EqualTo("items"));
        Assert.That(part.Inferred, Is.True);

        var kept = inline["successData"]!["details"]!.AsArray();
        Assert.That(kept, Has.Count.EqualTo(part.Returned));
        Assert.That(JsonNode.DeepEquals(kept[0], original["successData"]!["details"]![0]), Is.True, "prefix, not a sample");
        Assert.That(inline["successData"]!["errors"]!.GetValue<int>(), Is.EqualTo(2000));
        Assert.That(inline["successData"]!["buildVerification"]!["success"]!.GetValue<bool>(), Is.False);
        AssertPinnedKeysSurvive(original, inline);
    }

    [Test]
    public void Golden_Search300Hits_CutsTheLargestArray_AndMarksItInferred()
    {
        JsonNode original = Load("search_300_hits.json");
        const int budget = 20_000;

        var (inline, omitted) = ResultPager.Fit(original, budget, Wire, successDataType: null);

        Assert.That(Len(inline), Is.LessThanOrEqualTo(budget));
        var part = Part(omitted, "successData.regexResults");
        Assert.That(omitted, Has.Count.EqualTo(1));
        Assert.That((part.Total, part.Unit, part.Inferred), Is.EqualTo((300, "items", true)));
        Assert.That(part.Returned, Is.InRange(1, 299));
        Assert.That(inline["successData"]!["regexResults"]!.AsArray(), Has.Count.EqualTo(part.Returned));
        Assert.That(inline["successData"]!["literalResults"]!.AsArray(), Is.Empty, "an empty array is not a cut");
        AssertPinnedKeysSurvive(original, inline);
    }

    [Test]
    public void Golden_Search300Hits_WithADeclaredRecordsHint_IsNotInferred()
    {
        JsonNode original = Load("search_300_hits.json");
        const int budget = 20_000;

        var (inline, omitted) = ResultPager.Fit(original, budget, Wire, typeof(SearchProbeData));

        Assert.That(Len(inline), Is.LessThanOrEqualTo(budget));
        var part = Part(omitted, "successData.regexResults");
        Assert.That(part.Inferred, Is.False);
        Assert.That((part.Total, part.Unit), Is.EqualTo((300, "items")));
        AssertPinnedKeysSurvive(original, inline);
    }

    [Test]
    public void Golden_GitDiff44Kb_KeepsWholeLinesOfTheDiffString()
    {
        JsonNode original = Load("git_diff_44kb.json");
        string diff = original["successData"]!["diff"]!.GetValue<string>();
        const int budget = 12_000;
        Assert.That(diff.Length, Is.GreaterThan(43_000), "fixture sanity: about 44 KB");

        var (inline, omitted) = ResultPager.Fit(original, budget, Wire, successDataType: null);

        Assert.That(Len(inline), Is.LessThanOrEqualTo(budget));
        Assert.That(omitted, Has.Count.EqualTo(1));
        var part = Part(omitted, "successData.diff");
        Assert.That((part.Total, part.Unit, part.Inferred), Is.EqualTo((diff.Split('\n').Length - 1, "lines", true)));

        string kept = inline["successData"]!["diff"]!.GetValue<string>();
        Assert.That(diff, Does.StartWith(kept), "an exact prefix");
        Assert.That(kept, Does.EndWith("\n"), "whole lines only");
        Assert.That(kept.Count(c => c == '\n'), Is.EqualTo(part.Returned));
        Assert.That(inline["successData"]!["filesChanged"]!.GetValue<int>(), Is.EqualTo(7));
        AssertPinnedKeysSurvive(original, inline);
    }

    [Test]
    public void Golden_SemanticFindReplacePreview122Sites_CutsTheSitesList()
    {
        JsonNode original = Load("semantic_find_replace_preview_122_sites.json");
        const int budget = 15_000;

        var (inline, omitted) = ResultPager.Fit(original, budget, Wire, successDataType: null);

        Assert.That(Len(inline), Is.LessThanOrEqualTo(budget));
        Assert.That(omitted, Has.Count.EqualTo(1));
        var part = Part(omitted, "successData.sites");
        Assert.That((part.Total, part.Unit, part.Inferred), Is.EqualTo((122, "items", true)));
        Assert.That(part.Returned, Is.InRange(1, 121));
        Assert.That(inline["successData"]!["siteCount"]!.GetValue<int>(), Is.EqualTo(122));
        Assert.That(inline["successData"]!["applied"]!.GetValue<bool>(), Is.False);
        AssertPinnedKeysSurvive(original, inline);
    }

    [Test]
    public void Golden_BigError_CutsTheBulkUnderErrorData_AndKeepsCodeAndMessage()
    {
        JsonNode original = Load("big_error.json");
        const int budget = 12_000;

        var (inline, omitted) = ResultPager.Fit(original, budget, Wire, successDataType: null);

        Assert.That(Len(inline), Is.LessThanOrEqualTo(budget));
        Assert.That(omitted.Select(p => p.Path), Is.EquivalentTo(new[] { "errorData.structuredDetail", "errorData.detail" }));

        // The list alone cannot make the envelope fit, so it goes to its minimum of one element; the detail text then fits by lines.
        var structured = Part(omitted, "errorData.structuredDetail");
        Assert.That((structured.Returned, structured.Total, structured.Unit, structured.Inferred), Is.EqualTo((1, 400, "items", true)));
        var detail = Part(omitted, "errorData.detail");
        Assert.That((detail.Total, detail.Unit, detail.Inferred), Is.EqualTo((220, "lines", true)));
        Assert.That(detail.Returned, Is.InRange(1, 219));

        Assert.That(inline["errorData"]!["errorCode"]!.GetValue<string>(), Is.EqualTo("Ambiguous"));
        Assert.That(inline["isError"]!.GetValue<bool>(), Is.True);
        AssertPinnedKeysSurvive(original, inline);
    }

    // ---- property-style ----

    private static JsonObject ItemsEnvelope(int count, int itemChars)
    {
        var items = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            items.Add(new JsonObject { ["i"] = i, ["text"] = new string((char)('a' + (i % 26)), itemChars) });
        }

        return Envelope(new JsonObject { ["name"] = "kept", ["items"] = items });
    }

    [Test]
    public void ArrayCut_NeverGoesBelowOne_ReturnsAPrefix_AndFits_AcrossEveryBudget()
    {
        JsonObject original = ItemsEnvelope(count: 40, itemChars: 30);
        int fullLength = Len(original);
        int oneItemLength = Len(ItemsEnvelope(1, 30));

        // From "exactly one element fits" up to "all but a little fits".
        for (int budget = oneItemLength; budget < fullLength; budget += 17)
        {
            var (inline, omitted) = ResultPager.Fit(original, budget, Wire, successDataType: null);

            Assert.That(Len(inline), Is.LessThanOrEqualTo(budget), "budget " + budget);
            var part = Part(omitted, "successData.items");
            Assert.That(part.Returned, Is.GreaterThanOrEqualTo(1), "budget " + budget);
            Assert.That(part.Total, Is.EqualTo(40));
            var kept = inline["successData"]!["items"]!.AsArray();
            Assert.That(kept, Has.Count.EqualTo(part.Returned));
            for (int i = 0; i < kept.Count; i++)
            {
                Assert.That(JsonNode.DeepEquals(kept[i], original["successData"]!["items"]![i]), Is.True, "element " + i + " at budget " + budget);
            }

            // The longest prefix that fits: one more element would not.
            if (part.Returned < 40)
            {
                var oneMore = ItemsEnvelope(part.Returned + 1, 30);
                Assert.That(Len(oneMore), Is.GreaterThan(budget), "not maximal at budget " + budget);
            }
        }
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    public void StringCut_KeepsWholeLines_ExactPrefix_AcrossEveryBudget(string newline)
    {
        var text = new StringBuilder();
        for (int i = 0; i < 80; i++)
        {
            text.Append("line ").Append(i).Append(' ').Append(new string('x', i % 23)).Append(newline);
        }

        JsonObject original = Envelope(new JsonObject { ["text"] = text.ToString(), ["tag"] = "t" });
        string full = text.ToString();
        int fullLength = Len(original);
        int floor = Len(Envelope(new JsonObject { ["text"] = "line 0 " + newline, ["tag"] = "t" }));

        for (int budget = floor; budget < fullLength; budget += 41)
        {
            var (inline, omitted) = ResultPager.Fit(original, budget, Wire, successDataType: null);

            Assert.That(Len(inline), Is.LessThanOrEqualTo(budget), "budget " + budget);
            var part = Part(omitted, "successData.text");
            Assert.That(part.Unit, Is.EqualTo("lines"));
            Assert.That(part.Total, Is.EqualTo(80));
            Assert.That(part.Returned, Is.GreaterThanOrEqualTo(1));

            string kept = inline["successData"]!["text"]!.GetValue<string>();
            Assert.That(full, Does.StartWith(kept), "exact prefix at budget " + budget);
            Assert.That(kept, Does.EndWith(newline), "whole lines at budget " + budget);
            Assert.That(kept.Split('\n').Length - 1, Is.EqualTo(part.Returned));
        }
    }

    [Test]
    public void SingleElementTooBigAlone_IsReplacedByAnEmptyPrefix_AndRecorded()
    {
        var items = new JsonArray { new JsonObject { ["blob"] = new string('z', 10_000) } };
        JsonObject original = Envelope(new JsonObject { ["items"] = items, ["count"] = 1 });

        var (inline, omitted) = ResultPager.Fit(original, 500, Wire, successDataType: null);

        Assert.That(Len(inline), Is.LessThanOrEqualTo(500));
        var part = Part(omitted, "successData.items");
        Assert.That((part.Returned, part.Total, part.Unit, part.Inferred), Is.EqualTo((0, 1, "items", true)));
        Assert.That(inline["successData"]!["items"]!.AsArray(), Is.Empty);
        Assert.That(inline["successData"]!["count"]!.GetValue<int>(), Is.EqualTo(1));
        AssertPinnedKeysSurvive(original, inline);
    }

    [Test]
    public void SingleLineStringTooBigAlone_IsReplacedByAnEmptyString_AndRecorded()
    {
        JsonObject original = Envelope(new JsonObject { ["text"] = new string('q', 6_000), ["tag"] = "t" });

        var (inline, omitted) = ResultPager.Fit(original, 600, Wire, successDataType: null);

        Assert.That(Len(inline), Is.LessThanOrEqualTo(600));
        var part = Part(omitted, "successData.text");
        Assert.That((part.Returned, part.Total, part.Unit), Is.EqualTo((0, 1, "lines")));
        Assert.That(inline["successData"]!["text"]!.GetValue<string>(), Is.Empty);
        Assert.That(inline["successData"]!["tag"]!.GetValue<string>(), Is.EqualTo("t"));
    }

    [Test]
    public void NestedArray_GetsAnIndexedPath()
    {
        var items = new JsonArray();
        for (int i = 0; i < 300; i++)
        {
            items.Add(new JsonObject { ["n"] = i, ["label"] = "item number " + i });
        }

        JsonObject original = Envelope(new JsonObject { ["groups"] = new JsonArray { new JsonObject { ["name"] = "only", ["items"] = items } } });

        var (inline, omitted) = ResultPager.Fit(original, 3_000, Wire, successDataType: null);

        Assert.That(Len(inline), Is.LessThanOrEqualTo(3_000));
        var part = Part(omitted, "successData.groups[0].items");
        Assert.That((part.Total, part.Unit), Is.EqualTo((300, "items")));
        Assert.That(inline["successData"]!["groups"]![0]!["name"]!.GetValue<string>(), Is.EqualTo("only"));
    }

    [Test]
    public void DeclaredRecordsPath_IsCutBeforeALargerNode()
    {
        var records = new JsonArray();
        for (int i = 0; i < 100; i++)
        {
            records.Add("record " + i);
        }

        var bigText = new StringBuilder();
        for (int i = 0; i < 300; i++)
        {
            bigText.Append("a fairly long line of explanatory text number ").Append(i).Append('\n');
        }

        JsonObject original = Envelope(new JsonObject { ["records"] = records, ["notes"] = bigText.ToString() });
        int budget = Len(original) - 600; // less than the 100 short records weigh, so the declared list can absorb it alone

        var (hinted, hintedOmitted) = ResultPager.Fit(original, budget, Wire, "successData.records", []);
        var (inferred, inferredOmitted) = ResultPager.Fit(original, budget, Wire, null, []);

        Assert.That(Len(hinted), Is.LessThanOrEqualTo(budget));
        Assert.That(hintedOmitted.Select(p => (p.Path, p.Inferred)), Is.EqualTo(new[] { ("successData.records", false) }));
        Assert.That(hinted["successData"]!["notes"]!.GetValue<string>(), Is.EqualTo(bigText.ToString()), "the larger node is untouched while the declared one can still absorb the cut");

        Assert.That(Len(inferred), Is.LessThanOrEqualTo(budget));
        Assert.That(inferredOmitted.Select(p => (p.Path, p.Inferred)), Is.EqualTo(new[] { ("successData.notes", true) }));
    }

    [Test]
    public void PinnedPartLargerThanTheBudget_StillReturns_WithBulkRemovedAndPinnedIntact()
    {
        JsonObject original = Envelope(new JsonObject { ["items"] = new JsonArray("a", "b", "c") });
        original["statusMessage"] = new string('s', 2_000);

        var (inline, omitted) = ResultPager.Fit(original, 100, Wire, successDataType: null);

        Assert.That(inline["statusMessage"]!.GetValue<string>(), Has.Length.EqualTo(2_000));
        Assert.That(inline["successData"]!["items"]!.AsArray(), Is.Empty);
        Assert.That(Part(omitted, "successData.items").Returned, Is.EqualTo(0));
    }

    [Test]
    public void AnEnvelopeThatAlreadyFits_IsReturnedEqual_WithNoOmittedParts()
    {
        JsonObject original = ItemsEnvelope(count: 5, itemChars: 10);

        var (inline, omitted) = ResultPager.Fit(original, 1_000_000, Wire, successDataType: null);

        Assert.That(omitted, Is.Empty);
        Assert.That(JsonNode.DeepEquals(original, inline), Is.True);
    }

    [Test]
    public void Fit_DoesNotModifyItsInput()
    {
        JsonNode original = Load("search_300_hits.json");
        string before = original.ToJsonString(Wire);

        _ = ResultPager.Fit(original, 5_000, Wire, successDataType: null);

        Assert.That(original.ToJsonString(Wire), Is.EqualTo(before));
    }

    // ---- hints ----

    [Test]
    public void ResolveHints_MapsToWireNames_AndIsCached()
    {
        var first = ResultPager.ResolveHints(typeof(NeverInlineProbeData), Wire);
        var second = ResultPager.ResolveHints(typeof(NeverInlineProbeData), Wire);
        var search = ResultPager.ResolveHints(typeof(SearchProbeData), Wire);

        Assert.That(first.RecordsPath, Is.Null);
        Assert.That(first.NeverInlinePaths, Is.EquivalentTo(new[] { "successData.contents", "successData.full_list", "successData.blob" }));
        Assert.That(second.NeverInlinePaths, Is.SameAs(first.NeverInlinePaths), "cached per type");
        Assert.That(search.RecordsPath, Is.EqualTo("successData.regexResults"));
        Assert.That(search.NeverInlinePaths, Is.Empty);
        var none = ResultPager.ResolveHints(null, Wire);
        Assert.That(none.RecordsPath, Is.Null);
        Assert.That(none.NeverInlinePaths, Is.Empty);
        Assert.That(ResultPager.ResolveHints(typeof(string), Wire).RecordsPath, Is.Null);
    }

    [Test]
    public void NeverInline_IsAlwaysMovedOut_EvenWhenTheEnvelopeFits()
    {
        JsonObject original = Envelope(new JsonObject
        {
            ["count"] = 2,
            ["contents"] = "first\nsecond\nthird\n",
            ["full_list"] = new JsonArray("x", "y"),
            ["blob"] = new JsonObject { ["k"] = "v" },
            ["visible"] = new JsonArray("stays"),
        });

        var (inline, omitted) = ResultPager.Fit(original, 1_000_000, Wire, typeof(NeverInlineProbeData));

        Assert.That(inline["successData"]!["count"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(inline["successData"]!["visible"]!.AsArray(), Has.Count.EqualTo(1));
        Assert.That(inline["successData"]!["contents"]!.GetValue<string>(), Is.Empty);
        Assert.That(inline["successData"]!["full_list"]!.AsArray(), Is.Empty);
        Assert.That(inline["successData"]!.AsObject().ContainsKey("blob"), Is.False, "a node of another kind is removed");

        Assert.That(omitted.All(p => !p.Inferred && p.Returned == 0), Is.True);
        Assert.That((Part(omitted, "successData.contents").Total, Part(omitted, "successData.contents").Unit), Is.EqualTo((3, "lines")));
        Assert.That((Part(omitted, "successData.full_list").Total, Part(omitted, "successData.full_list").Unit), Is.EqualTo((2, "items")));
        Assert.That(Part(omitted, "successData.blob").Unit, Is.EqualTo("chars"));
        AssertPinnedKeysSurvive(original, inline);
    }
}
