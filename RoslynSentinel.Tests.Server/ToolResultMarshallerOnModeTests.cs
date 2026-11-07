using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// plan_wire_result_marshaller.md Step 4: <see cref="ToolResultMarshaller"/> in mode <c>on</c> (fit, store the full
/// envelope, add the <c>largeResult</c> pointer), the Envelope store and its second reader
/// (<see cref="SubAgentEvalResult.ResolveOffloadedResult"/>), and the hooked-tool registry the filter's miss detector reads.
/// </summary>
[TestFixture]
[Category("SentinelToolFactory")]
public class ToolResultMarshallerOnModeTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;
    private static readonly MethodInfo AnyMethod = typeof(ToolResultMarshallerOnModeTests).GetMethod(nameof(AnyMethodTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rs-marshal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AnyMethodTarget()
    {
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    public sealed class OnModeProbeData
    {
        public List<string> Notes { get; set; } = [];

        [ResultRecords]
        public List<string> Records { get; set; } = [];
    }

    private ToolResultMarshaller On(string? solutionRoot, ILogger<ToolResultMarshaller>? logger = null) =>
        new(logger ?? NullLogger<ToolResultMarshaller>.Instance, () => solutionRoot, () => ResultMarshallerMode.On);

    private static string FullText(object result) =>
        JsonSerializer.Serialize(result, Wire.GetTypeInfo(typeof(object)));

    private static async Task<object?> MarshalAsync(ToolResultMarshaller marshaller, object? result) =>
        await marshaller.MarshalAsync("Probe", AnyMethod, result, typeof(object), Wire, TestContext.CurrentContext.CancellationToken);

    private static SentinelCallToolResult<object> BigSearch(int count) =>
        new()
        {
            IsError = false,
            SuccessData = new
            {
                literalResults = new List<string>(),
                regexResults = Enumerable.Range(1, count)
                    .Select(i => new { filePath = $"RoslynSentinel.Common/File{i}.cs", line = i, preview = $"var value{i} = Compute({i}); // café" })
                    .ToList(),
            },
            TotalRecords = count,
            HasMoreData = false,
            StatusMessage = "pinned status message",
            WarningDetails = "pinned warning",
        };

    private string? StoredFileFor(string resultId) =>
        Directory.Exists(Path.Combine(_root, ".roslynsentinel", "largeresults"))
            ? Directory.EnumerateFiles(Path.Combine(_root, ".roslynsentinel", "largeresults"), $"largeresult_*_{resultId}.json").SingleOrDefault()
            : null;

    // ---- small results: identical to pass-through ----

    [Test]
    public async Task On_ResultThatFits_IsByteIdenticalToPassthrough_AndStoresNothing()
    {
        var small = BigSearch(5);
        var passthrough = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance);

        object? onText = await MarshalAsync(On(_root), small);
        object? passthroughText = await passthrough.MarshalAsync("Probe", AnyMethod, small, typeof(object), Wire, TestContext.CurrentContext.CancellationToken);

        Assert.That(onText, Is.TypeOf<string>());
        Assert.That(onText, Is.EqualTo(passthroughText));
        Assert.That(onText, Is.EqualTo(FullText(small)));
        Assert.That(Directory.Exists(Path.Combine(_root, ".roslynsentinel")), Is.False);
    }

    [Test]
    public async Task On_ContentShapes_AreStillReturnedUnchanged()
    {
        string bigString = new('x', 40_000);

        object? marshalled = await MarshalAsync(On(_root), bigString);

        Assert.That(marshalled, Is.SameAs(bigString), "a string result is the tool's own text; the filter backstop handles its size");
    }

    [Test]
    public async Task On_OverBudgetResultWithANonObjectRoot_IsReturnedAsFullText()
    {
        var list = Enumerable.Range(1, 2_000).Select(i => "entry " + i).ToList();

        object? marshalled = await MarshalAsync(On(_root), list);

        Assert.That(marshalled, Is.EqualTo(FullText(list)));
        Assert.That(Directory.Exists(Path.Combine(_root, ".roslynsentinel")), Is.False);
    }

    // ---- over budget: fit, store, pointer ----

    [Test]
    public async Task On_OverBudget_ReturnsTheFittedEnvelopeWithAnEnvelopePointer_AndStoresTheFullText()
    {
        var big = BigSearch(400);
        string fullText = FullText(big);
        Assert.That(fullText.Length, Is.GreaterThan(LargeResultHelper.InlineBudgetChars), "fixture sanity");

        object? marshalled = await MarshalAsync(On(_root), big);

        Assert.That(marshalled, Is.TypeOf<string>());
        string text = (string)marshalled!;
        Assert.That(text.Length, Is.LessThanOrEqualTo(LargeResultHelper.InlineBudgetChars), "fitted result plus pointer stays under the filter threshold");

        var envelope = JsonNode.Parse(text)!.AsObject();
        Assert.That(envelope.First().Key, Is.EqualTo("largeResult"), "the pointer leads the envelope so a model reads it first");
        Assert.That(envelope["isError"]!.GetValue<bool>(), Is.False);
        Assert.That(envelope["statusMessage"]!.GetValue<string>(), Is.EqualTo("pinned status message"));
        Assert.That(envelope["warningDetails"]!.GetValue<string>(), Is.EqualTo("pinned warning"));
        Assert.That(envelope["totalRecords"]!.GetValue<int>(), Is.EqualTo(400), "domain totalRecords is untouched");

        var pointer = envelope["largeResult"]!.AsObject();
        Assert.That(pointer["resultType"]!.GetValue<string>(), Is.EqualTo("Envelope"));
        Assert.That(pointer["writtenToFile"]!.GetValue<bool>(), Is.True);
        string resultId = pointer["resultId"]!.GetValue<string>();
        Assert.That(resultId, Has.Length.EqualTo(32));
        Assert.That(pointer["sizeBytes"]!.GetValue<long>(), Is.GreaterThan(fullText.Length - 1));

        // Sequence-paged: the pointer carries the page cursor, not omitted[].
        Assert.That(pointer.ContainsKey("omitted"), Is.False, "sequence pages write no omitted entry");
        int returned = pointer["returned"]!.GetValue<int>();
        Assert.That(returned, Is.InRange(1, 399));
        Assert.That(pointer["nextOffset"]!.GetValue<int>(), Is.EqualTo(returned));
        Assert.That(pointer["totalRecords"]!.GetValue<int>(), Is.EqualTo(400), "the sequence total");
        Assert.That(envelope["successData"]!["literalResults"]!.AsArray(), Is.Empty);
        Assert.That(envelope["successData"]!["regexResults"]!.AsArray(), Has.Count.EqualTo(returned));
        string message = pointer["message"]!.GetValue<string>();
        Assert.That(message, Is.EqualTo($"INCOMPLETE: {returned} of 400 records returned. Call {LargeResultHints.FetchCall(resultId, offset: returned)} for the next page."));
        Assert.That(message, Does.Contain("GetLargeResult").And.Contain(resultId).And.Contain($"offset: {returned}"));

        // The store holds the FULL wire text, as an Envelope wrapper, plus the sequence the first page was built from.
        string? stored = StoredFileFor(resultId);
        Assert.That(stored, Is.Not.Null, "file under .roslynsentinel/largeresults named by the usual convention");
        var wrapper = JsonNode.Parse(File.ReadAllText(stored!))!.AsObject();
        Assert.That(wrapper["Type"]!.GetValue<string>(), Is.EqualTo("Envelope"), "stored files use the shared options: PascalCase wrapper keys, enum by name");
        Assert.That(JsonNode.DeepEquals(wrapper["Data"], JsonNode.Parse(fullText)), Is.True);
        Assert.That(wrapper.ContainsKey("Omitted"), Is.False);
        var sequence = wrapper["Sequence"]!.AsObject();
        Assert.That(sequence["Kind"]!.GetValue<string>(), Is.EqualTo("Lists"));
        Assert.That(sequence["Order"]!.GetValue<string>(), Is.EqualTo("Interleaved"));
        Assert.That(sequence["Paths"]!.AsArray().Select(p => p!.GetValue<string>()), Is.EqualTo(new[] { "successData.regexResults" }), "the empty literalResults list no longer joins the default sequence");
        var descriptor = wrapper["Sequence"].Deserialize<SequenceDescriptor>(SharedJsonOptions.Default)!;
        Assert.That(descriptor.Kind, Is.EqualTo(SequenceKind.Lists));
        Assert.That(descriptor.Paths, Is.EqualTo(sequence["Paths"]!.AsArray().Select(p => p!.GetValue<string>())));
    }

    [Test]
    public async Task On_OverBudgetSequencePage_SetsSizeCharsToTheFullTextLength_AndSizeBytesIsNoSmaller()
    {
        var big = BigSearch(400);
        string fullText = FullText(big);

        var (_, pointer, _) = await MarshalPagedAsync(big);

        Assert.That(pointer.ContainsKey("returned"), Is.True, "sequence path: the pointer carries the page cursor");
        long sizeChars = pointer["sizeChars"]!.GetValue<long>();
        long sizeBytes = pointer["sizeBytes"]!.GetValue<long>();
        Assert.That(sizeChars, Is.EqualTo(fullText.Length), "sizeChars counts the full wire text, not the first page");
        Assert.That(sizeChars, Is.LessThanOrEqualTo(sizeBytes), "UTF-8 bytes are never fewer than UTF-16 chars");
    }

    [Test]
    public async Task On_ADeclaredRecordsHintOnTheRuntimeSuccessDataType_IsUsed_AndNotInferred()
    {
        var data = new OnModeProbeData
        {
            Notes = Enumerable.Range(1, 100).Select(i => "note " + i).ToList(),
            Records = Enumerable.Range(1, 300).Select(i => "record number " + i + " with some padding text").ToList(),
        };
        var result = new SentinelCallToolResult<OnModeProbeData> { IsError = false, SuccessData = data, StatusMessage = "s" };
        Assert.That(FullText(result).Length, Is.GreaterThan(12_800), "fixture sanity");

        object? marshalled = await MarshalAsync(On(_root), result);

        var envelope = JsonNode.Parse((string)marshalled!)!.AsObject();
        var pointer = envelope["largeResult"]!.AsObject();
        Assert.That(pointer.ContainsKey("omitted"), Is.False);
        Assert.That(pointer["totalRecords"]!.GetValue<int>(), Is.EqualTo(300), "only the declared list forms the sequence");
        Assert.That(envelope["successData"]!["notes"]!.AsArray(), Has.Count.EqualTo(100), "a list outside the sequence is returned whole");
        Assert.That(envelope["successData"]!["records"]!.AsArray(), Has.Count.EqualTo(pointer["returned"]!.GetValue<int>()));
        Assert.That(pointer["returned"]!.GetValue<int>(), Is.InRange(1, 299));
    }

    // ---- store failure ----

    [Test]
    public async Task On_NoSolutionRoot_ReturnsTheFittedResultWithOmittedAndAWarning_AndNoResultId()
    {
        object? marshalled = await MarshalAsync(On(solutionRoot: null), BigSearch(400));

        var envelope = JsonNode.Parse((string)marshalled!)!.AsObject();
        Assert.That(envelope.First().Key, Is.EqualTo("largeResult"), "the pointer leads the envelope in the failure branch too");
        Assert.That(((string)marshalled!).Length, Is.LessThanOrEqualTo(LargeResultHelper.InlineBudgetChars));
        var pointer = envelope["largeResult"]!.AsObject();
        Assert.That(pointer.ContainsKey("resultId"), Is.False);
        Assert.That(pointer["writtenToFile"]!.GetValue<bool>(), Is.False);
        Assert.That(pointer.ContainsKey("omitted"), Is.False);
        Assert.That(pointer["returned"]!.GetValue<int>(), Is.InRange(1, 399));
        Assert.That(pointer["totalRecords"]!.GetValue<int>(), Is.EqualTo(400));
        Assert.That(envelope["warningDetails"]!.GetValue<string>(), Does.StartWith("pinned warning").And.Contain("not retrievable"));
        Assert.That(envelope["successData"]!["regexResults"]!.AsArray().Count, Is.LessThan(400));
    }

    [Test]
    public async Task On_StoreThrows_ReturnsTheFittedResultWithoutAResultId_AndLogsAtErrorLevel()
    {
        // A solution root that is a file: creating <root>/.roslynsentinel/largeresults fails with an IOException.
        string fileAsRoot = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(fileAsRoot, "x");
        var logger = new CapturingLogger<ToolResultMarshaller>();

        object? marshalled = await MarshalAsync(On(fileAsRoot, logger), BigSearch(400));

        var envelope = JsonNode.Parse((string)marshalled!)!.AsObject();
        Assert.That(envelope["largeResult"]!.AsObject().ContainsKey("resultId"), Is.False);
        Assert.That(envelope["largeResult"]!["returned"]!.GetValue<int>(), Is.GreaterThan(0));
        Assert.That(envelope["warningDetails"]!.GetValue<string>(), Does.Contain("could not be written"));
        Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Error), Is.EqualTo(1));
        Assert.That(((string)marshalled!), Does.Not.Contain(fileAsRoot), "no internal path in tool-visible output");
    }

    // ---- sequence paging (proposal_sequence_pager.md slice 5) ----

    private static List<object> Hits(string prefix, int count) =>
        Enumerable.Range(1, count)
            .Select(i => (object)new { id = prefix + i, file = $"src/Some/Path/File{i}.cs", preview = "some preview text padding to make the hit sizeable" })
            .ToList();

    private async Task<(JsonObject Envelope, JsonObject Pointer, string Text)> MarshalPagedAsync(object result, ToolResultMarshaller? marshaller = null, bool checkLength = true)
    {
        object? marshalled = await MarshalAsync(marshaller ?? On(_root), result);
        string text = (string)marshalled!;
        var envelope = JsonNode.Parse(text)!.AsObject();
        Assert.That(envelope.First().Key, Is.EqualTo("largeResult"));
        if (checkLength)
        {
            Assert.That(text.Length, Is.LessThanOrEqualTo(LargeResultHelper.InlineBudgetChars));
        }

        return (envelope, envelope["largeResult"]!.AsObject(), text);
    }

    [Test]
    public async Task On_TwoLargeLists_BothAppearInTheFirstPage_WithTheCursorAndMessage()
    {
        var summary = new { total = 550, note = "pinned summary" };
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = new { callers = Hits("c", 300), implementations = Hits("i", 250), summary },
        };
        Assert.That(FullText(result).Length, Is.GreaterThan(LargeResultHelper.InlineBudgetChars), "fixture sanity");

        var (envelope, pointer, _) = await MarshalPagedAsync(result);

        int callers = envelope["successData"]!["callers"]!.AsArray().Count;
        int implementations = envelope["successData"]!["implementations"]!.AsArray().Count;
        Assert.That(callers, Is.GreaterThan(10));
        Assert.That(Math.Abs(callers - implementations), Is.LessThanOrEqualTo(1), "interleaved: neither list is starved");
        Assert.That(pointer["returned"]!.GetValue<int>(), Is.EqualTo(callers + implementations));
        Assert.That(pointer["nextOffset"]!.GetValue<int>(), Is.EqualTo(callers + implementations));
        Assert.That(pointer["totalRecords"]!.GetValue<int>(), Is.EqualTo(550));
        Assert.That(pointer.ContainsKey("omitted"), Is.False);
        Assert.That(envelope["successData"]!["summary"]!["note"]!.GetValue<string>(), Is.EqualTo("pinned summary"));

        string resultId = pointer["resultId"]!.GetValue<string>();
        Assert.That(
            pointer["message"]!.GetValue<string>(),
            Is.EqualTo($"INCOMPLETE: {callers + implementations} of 550 records returned. Call GetLargeResult(reason: \"page through the stored result\", resultId: \"{resultId}\", offset: {callers + implementations}) for the next page."));
    }

    [Test]
    public async Task On_ReadFileLikeStringResult_PagesByLines_WithTheFileLineRange()
    {
        string source = string.Concat(Enumerable.Range(1, 3000).Select(i => $"var line{i} = {i};\r\n"));
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = new { filePath = "src/F.cs", startLine = 10, endLine = 3009, totalLines = 4000, source },
        };

        var (envelope, pointer, _) = await MarshalPagedAsync(result);

        string shown = envelope["successData"]!["source"]!.GetValue<string>();
        int lines = pointer["returned"]!.GetValue<int>();
        Assert.That(lines, Is.InRange(1, 2999));
        Assert.That(source, Does.StartWith(shown));
        Assert.That(ResultPager.SplitLines(shown), Has.Count.EqualTo(lines));
        Assert.That(envelope["successData"]!["filePath"]!.GetValue<string>(), Is.EqualTo("src/F.cs"));
        Assert.That(pointer["totalRecords"]!.GetValue<int>(), Is.EqualTo(3000));
        Assert.That(pointer["nextOffset"]!.GetValue<int>(), Is.EqualTo(lines));
        string resultId = pointer["resultId"]!.GetValue<string>();
        Assert.That(
            pointer["message"]!.GetValue<string>(),
            Is.EqualTo($"INCOMPLETE: lines 1-{lines} of 3000 returned (file lines 10-{9 + lines}). Call {LargeResultHints.FetchCall(resultId, offset: lines)} for the next page."));

        var wrapper = JsonNode.Parse(File.ReadAllText(StoredFileFor(resultId)!))!.AsObject();
        Assert.That(wrapper["Sequence"]!["Kind"]!.GetValue<string>(), Is.EqualTo("Lines"));
        Assert.That(wrapper["Sequence"]!["MaxUnitChars"]!.GetValue<int>(), Is.EqualTo(SequencePager.DefaultMaxUnitChars));
    }

    [Test]
    public async Task On_ALineSequenceWithoutAStartLine_OmitsTheFileRange()
    {
        string source = string.Concat(Enumerable.Range(1, 3000).Select(i => $"var line{i} = {i};\r\n"));
        var result = new SentinelCallToolResult<object> { IsError = false, SuccessData = new { signature = "void M()", source } };

        var (_, pointer, _) = await MarshalPagedAsync(result);

        Assert.That(pointer["message"]!.GetValue<string>(), Does.StartWith("INCOMPLETE: lines 1-").And.Not.Contain("file lines"));
    }

    [Test]
    public async Task On_PagingHintsOnTheEnvelope_AreRespected()
    {
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = new { a = Hits("a", 300), b = Hits("b", 300), small = new[] { "x", "y" } },
            Paging = new PagingHints(["a", "b"], PagingOrder.Linear),
        };

        var (envelope, pointer, _) = await MarshalPagedAsync(result);

        int a = envelope["successData"]!["a"]!.AsArray().Count;
        Assert.That(a, Is.GreaterThan(0));
        Assert.That(envelope["successData"]!["b"]!.AsArray(), Is.Empty, "Linear: the first list fills the page before the second");
        Assert.That(envelope["successData"]!["small"]!.AsArray(), Has.Count.EqualTo(2), "a list outside the hints is returned whole");
        Assert.That(pointer["totalRecords"]!.GetValue<int>(), Is.EqualTo(600), "the small list is not part of the sequence");
        var wrapper = JsonNode.Parse(File.ReadAllText(StoredFileFor(pointer["resultId"]!.GetValue<string>())!))!.AsObject();
        Assert.That(wrapper["Sequence"]!["Order"]!.GetValue<string>(), Is.EqualTo("Linear"));
        Assert.That(wrapper["Sequence"]!["Paths"]!.AsArray().Select(p => p!.GetValue<string>()), Is.EqualTo(new[] { "successData.a", "successData.b" }));
    }

    [Test]
    public async Task On_ADictionaryOfStrings_HasNoSequence_AndKeepsTheFitPath()
    {
        var files = Enumerable.Range(1, 60).ToDictionary(i => $"File{i}.cs", i => new string((char)('a' + (i % 26)), 1_000));
        var result = new SentinelCallToolResult<object> { IsError = false, SuccessData = new { contents = files } };
        Assert.That(FullText(result).Length, Is.GreaterThan(LargeResultHelper.InlineBudgetChars), "fixture sanity");

        // Fit's omitted list alone (one entry per emptied string) can exceed the budget here: Decided 9's reason for [NeverInline].
        var (_, pointer, _) = await MarshalPagedAsync(result, checkLength: false);

        Assert.That(pointer["omitted"]!.AsArray(), Is.Not.Empty);
        Assert.That(pointer.ContainsKey("returned"), Is.False);
        Assert.That(pointer.ContainsKey("nextOffset"), Is.False);
        Assert.That(pointer["message"]!.GetValue<string>(), Does.Contain("path: "));
    }

    [Test]
    public async Task On_OverBudgetFit_SetsSizeCharsToTheFullTextLength_AndSizeBytesIsNoSmaller()
    {
        var files = Enumerable.Range(1, 60).ToDictionary(i => $"File{i}.cs", i => new string((char)('a' + (i % 26)), 1_000));
        var result = new SentinelCallToolResult<object> { IsError = false, SuccessData = new { contents = files } };
        string fullText = FullText(result);
        Assert.That(fullText.Length, Is.GreaterThan(LargeResultHelper.InlineBudgetChars), "fixture sanity");

        var (_, pointer, _) = await MarshalPagedAsync(result, checkLength: false);

        Assert.That(pointer["omitted"]!.AsArray(), Is.Not.Empty, "Fit path: the cut is listed in omitted");
        Assert.That(pointer.ContainsKey("returned"), Is.False, "Fit path: no sequence");
        long sizeChars = pointer["sizeChars"]!.GetValue<long>();
        long sizeBytes = pointer["sizeBytes"]!.GetValue<long>();
        Assert.That(sizeChars, Is.EqualTo(fullText.Length), "sizeChars counts the full wire text, not the fitted inline text");
        Assert.That(sizeChars, Is.LessThanOrEqualTo(sizeBytes), "UTF-8 bytes are never fewer than UTF-16 chars");
    }

    [Test]
    public async Task On_ASequenceThatFitsWhole_ButAnOversizePartOutsideIt_FallsBackToFit()
    {
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = new { items = new[] { "one", "two", "three" }, contents = new Dictionary<string, string> { ["big.cs"] = new string('z', 30_000) } },
        };

        var (_, pointer, _) = await MarshalPagedAsync(result);

        Assert.That(pointer["omitted"]!.AsArray(), Is.Not.Empty, "nothing is paged by sequence, so the old cut applies");
        Assert.That(pointer.ContainsKey("returned"), Is.False);
    }

    [Test]
    public async Task On_AnOversizeRecord_IsLoggedAsAContractViolation_AndStillPages()
    {
        var records = Hits("r", 30);
        records.Insert(0, new { id = "huge", file = "x.cs", preview = new string('p', 20_000) });
        var result = new SentinelCallToolResult<object> { IsError = false, SuccessData = new { items = records } };
        var logger = new CapturingLogger<ToolResultMarshaller>();

        object? marshalled = await MarshalAsync(On(_root, logger), result);

        var envelope = JsonNode.Parse((string)marshalled!)!.AsObject();
        Assert.That(envelope["largeResult"]!["returned"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(envelope["largeResult"]!["nextOffset"]!.GetValue<int>(), Is.EqualTo(1));
        var warning = logger.Entries.Single(e => e.Level == LogLevel.Warning);
        Assert.That(warning.Message, Does.Contain("Probe").And.Contain("successData.items[0]"));
    }

    // Decided 4 (G2): a sequence that is exactly one oversize unit is returned alone, whole - no cut, no omitted, no new resultId.
    [Test]
    public async Task On_OneOversizeRecord_FirstResponseIsReturnedAlone_WithoutOmittedOrAStore()
    {
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = new { items = new[] { new { id = "huge", file = "x.cs", preview = new string('p', 20_000) } } },
        };
        var logger = new CapturingLogger<ToolResultMarshaller>();
        using var signal = OversizeUnitSignal.Open();

        object? marshalled = await MarshalAsync(On(_root, logger), result);

        Assert.That(marshalled, Is.EqualTo(FullText(result)), "the text is returned unchanged");
        Assert.That(marshalled as string, Does.Not.Contain("largeResult").And.Not.Contain("omitted"));
        Assert.That(signal.Raised, Is.True, "the offload filter is told to leave it alone");
        var warning = logger.Entries.Single(e => e.Level == LogLevel.Warning);
        Assert.That(warning.Message, Does.Contain("Probe").And.Contain("successData.items[0]"));
        Assert.That(Directory.Exists(Path.Combine(_root, ".roslynsentinel", "largeresults")), Is.False, "nothing was stored");
    }

    // Stage 1 item 3: a GetLargeResult page (AlreadyPaged hint) that one oversize unit leaves over budget is never re-paged.
    [Test]
    public async Task On_APageMarkedAlreadyPaged_IsReturnedUnchanged_EvenOverBudget_WithoutANewResultId()
    {
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = new { path = "successData.items", offset = 3, returned = 1, total = 40, unit = "records", items = Hits("r", 200) },
            Paging = new PagingHints([], PagingOrder.Interleaved, AlreadyPaged: true),
        };
        Assert.That(FullText(result).Length, Is.GreaterThan(LargeResultHelper.InlineBudgetChars), "fixture sanity");
        using var signal = OversizeUnitSignal.Open();

        object? marshalled = await MarshalAsync(On(_root), result);

        Assert.That(marshalled, Is.EqualTo(FullText(result)));
        Assert.That(signal.Raised, Is.True);
        Assert.That(Directory.Exists(Path.Combine(_root, ".roslynsentinel", "largeresults")), Is.False, "no second resultId is minted");
    }

    // G12 end to end through the marshaller: an empty attributes list beside a large source no longer wins the sequence.
    [Test]
    public async Task On_MethodSourceLikeResult_WithAnEmptyAttributesList_PagesByLines_WithFileRangeFromTheEnvelope()
    {
        string source = string.Concat(Enumerable.Range(1, 1500).Select(i => $"    var value{i} = Compute({i});\r\n"));
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = new
            {
                envelope = new { lineCount = 1500, returnedFromLine = 1540, returnedToLine = 3039, isComplete = true },
                signature = "void Big()",
                source,
                attributes = new List<string>(),
            },
        };

        var (envelope, pointer, _) = await MarshalPagedAsync(result);

        int lines = pointer["returned"]!.GetValue<int>();
        Assert.That(lines, Is.InRange(1, 1499));
        Assert.That(pointer["nextOffset"]!.GetValue<int>(), Is.EqualTo(lines));
        Assert.That(pointer["totalRecords"]!.GetValue<int>(), Is.EqualTo(1500));
        Assert.That(pointer.ContainsKey("omitted"), Is.False);
        Assert.That(pointer["message"]!.GetValue<string>(), Does.Contain($"(file lines 1540-{1539 + lines})"));
        Assert.That(source, Does.StartWith(envelope["successData"]!["source"]!.GetValue<string>()));
    }

    // ---- the second reader ----

    [Test]
    public async Task ResolveOffloadedResult_OnAnEnvelopePointer_ReturnsTheFullStoredEnvelope()
    {
        var big = BigSearch(400);
        object? marshalled = await MarshalAsync(On(_root), big);
        string toolText = (string)marshalled!;
        Assert.That(JsonNode.Parse(toolText)!["largeResult"]!["filePath"], Is.Not.Null, "the pointer names the file");

        string? resolved = SubAgentEvalResult.ResolveOffloadedResult(toolText, _root);

        Assert.That(resolved, Is.Not.Null.And.Not.EqualTo(toolText), "the pointer was followed");
        var full = JsonNode.Parse(resolved!)!.AsObject();
        Assert.That(full["successData"]!["regexResults"]!.AsArray(), Has.Count.EqualTo(400));
        Assert.That(JsonNode.DeepEquals(full, JsonNode.Parse(FullText(big))), Is.True, "Data is the envelope object, unwrapped");
    }

    [Test]
    public async Task ResolveOffloadedResult_OnAnEnvelopeFileHoldingAnErrorEnvelope_ReturnsItWhole()
    {
        string errorEnvelope = FullText(new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(ToolErrorCode.NotFound, "snippet not found", "detail", ["one", "two"]),
        });
        var stored = await LargeResultHelper.StoreEnvelopeAsync(errorEnvelope, _root, CancellationToken.None);
        Assert.That(stored.offloaded, Is.True);

        // Both pointer shapes the reader follows: the typed largeResult.filePath and the backstop's offloaded + resultId.
        string typedPointer = new JsonObject { ["largeResult"] = new JsonObject { ["filePath"] = stored.filePath.ToString() } }.ToJsonString();
        string backstopPointer = new JsonObject { ["offloaded"] = true, ["resultId"] = stored.resultId }.ToJsonString();

        foreach (string pointer in new[] { typedPointer, backstopPointer })
        {
            string? resolved = SubAgentEvalResult.ResolveOffloadedResult(pointer, _root);

            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(resolved!), JsonNode.Parse(errorEnvelope)), Is.True, pointer);
        }
    }

    [Test]
    public async Task EnvelopeFiles_ShareTheRetentionAndNamingOfOtherResultTypes()
    {
        var stored = await LargeResultHelper.StoreEnvelopeAsync("{\"isError\":false,\"successData\":[1]}", _root, CancellationToken.None);

        string name = Path.GetFileName(stored.filePath.ToString());
        Assert.That(name, Does.Match(@"^largeresult_\d{8}T\d{6}Z_" + stored.resultId + @"\.json$"));
        Assert.That(Path.GetDirectoryName(stored.filePath.ToString()), Does.EndWith(Path.Combine(".roslynsentinel", "largeresults")));

        Assert.That(LargeResultHelper.SweepExpired(_root, TimeSpan.FromDays(LargeResultHelper.RetentionDays)), Is.EqualTo(0), "fresh file kept");
        Assert.That(LargeResultHelper.SweepExpired(_root, TimeSpan.FromDays(-1)), Is.EqualTo(1), "an expired Envelope file is swept like any other");
    }

    [Test]
    public async Task StoreEnvelopeAsync_WithNoSolutionRoot_FailsClosed()
    {
        var stored = await LargeResultHelper.StoreEnvelopeAsync("{\"a\":1}", null, CancellationToken.None);

        Assert.That(stored.offloaded, Is.False);
        Assert.That(stored.resultId, Is.Null);
    }

    // ---- the hooked-tool registry (read by the offload filter's miss detector) ----

    [Test]
    public void IsHooked_IsTrueOnlyForToolsBuiltByTheReplicaFactory()
    {
        MethodInfo method = typeof(SentinelToolFactoryParityTests.ParityStaticProbe).GetMethod(nameof(SentinelToolFactoryParityTests.ParityStaticProbe.Echo))!;
        Assert.That(SentinelToolFactory.IsHooked("HookedRegistryProbe"), Is.False);

        _ = SentinelToolFactory.Create(method, method.DeclaringType!, new McpServerToolCreateOptions { Name = "HookedRegistryProbe" });

        Assert.That(SentinelToolFactory.IsHooked("HookedRegistryProbe"), Is.True);
        Assert.That(SentinelToolFactory.IsHooked("NeverBuiltAnywhere"), Is.False);
        Assert.That(SentinelToolFactory.IsHooked(null), Is.False);
    }
}
