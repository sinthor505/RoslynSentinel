using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// proposal_side_stores_never_inline.md slice 2: payloads declared with <c>SentinelCallToolResult.Detach</c> or
/// <c>ToolCallContext.Detach</c> are grafted at <c>successData.&lt;name&gt;</c>, stored as one Envelope and moved out of the text by
/// <see cref="ToolResultMarshaller"/> in mode <c>on</c>, before the budget check; the other modes drop them.
/// </summary>
[TestFixture]
[Category("SentinelToolFactory")]
public class ToolResultMarshallerDetachedTests
{
    private const string Name = "fullResults";
    private const string Marker = "STACKTRACE-MARKER";

    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;
    private static readonly MethodInfo AnyMethod = typeof(ToolResultMarshallerDetachedTests).GetMethod(nameof(AnyMethodTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rs-detach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
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

    private static ToolResultMarshaller Marshaller(string? solutionRoot, ResultMarshallerMode mode = ResultMarshallerMode.On, ILogger<ToolResultMarshaller>? logger = null) =>
        new(logger ?? NullLogger<ToolResultMarshaller>.Instance, () => solutionRoot, () => mode);

    private static async Task<string> MarshalAsync(ToolResultMarshaller marshaller, object result) =>
        (string)(await marshaller.MarshalAsync("Probe", AnyMethod, result, typeof(object), Wire, TestContext.CurrentContext.CancellationToken))!;

    private static string FullText(object result) =>
        JsonSerializer.Serialize(result, Wire.GetTypeInfo(typeof(object)));

    // A small RunTest-like result: a few inline fields and one projected row.
    private static SentinelCallToolResult<object> Summary() =>
        new()
        {
            IsError = false,
            SuccessData = new { passed = 120, failed = 7, results = new List<string> { "projected row" } },
            StatusMessage = "pinned status message",
        };

    private static List<object> Rows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => (object)new { id = i, name = $"Tests.Case{i}", stackTrace = $"{Marker}-{i} at Some.Namespace.Type.Method()" })
            .ToList();

    private static SentinelCallToolResult<object> BigSearch(int count) =>
        new()
        {
            IsError = false,
            SuccessData = new
            {
                literalResults = new List<string>(),
                regexResults = Enumerable.Range(1, count)
                    .Select(i => new { filePath = $"RoslynSentinel.Common/File{i}.cs", line = i, preview = $"var value{i} = Compute({i});" })
                    .ToList(),
            },
            TotalRecords = count,
            StatusMessage = "pinned status message",
        };

    private IEnumerable<string> StoredFiles() =>
        Directory.Exists(Path.Combine(_root, ".roslynsentinel", "largeresults"))
            ? Directory.EnumerateFiles(Path.Combine(_root, ".roslynsentinel", "largeresults"), "largeresult_*.json")
            : [];

    private JsonObject StoredWrapper(string resultId) =>
        JsonNode.Parse(File.ReadAllText(StoredFiles().Single(f => f.EndsWith($"_{resultId}.json", StringComparison.Ordinal))))!.AsObject();

    // ---- under budget ----

    [Test]
    public async Task Detach_UnderBudget_LeavesAPointerAndNoPayload_AndStoresTheFullTreeForPathPaging()
    {
        var result = Summary();
        result.Detach(Name, Rows(7));

        string text = await MarshalAsync(Marshaller(_root), result);

        Assert.That(text, Does.Not.Contain(Marker), "the payload is not in the wire text");
        var envelope = JsonNode.Parse(text)!.AsObject();
        Assert.That(envelope.First().Key, Is.EqualTo("largeResult"));
        Assert.That(envelope["successData"]!.AsObject().ContainsKey(Name), Is.False, "no empty shell is left behind");
        Assert.That(envelope["successData"]!["passed"]!.GetValue<int>(), Is.EqualTo(120));
        Assert.That(envelope["successData"]!["results"]!.AsArray(), Has.Count.EqualTo(1), "the inline projection is untouched");
        Assert.That(envelope["statusMessage"]!.GetValue<string>(), Is.EqualTo("pinned status message"));

        var pointer = envelope["largeResult"]!.AsObject();
        Assert.That(pointer["resultType"]!.GetValue<string>(), Is.EqualTo("Envelope"));
        Assert.That(pointer["writtenToFile"]!.GetValue<bool>(), Is.True);
        string resultId = pointer["resultId"]!.GetValue<string>();
        string message = pointer["message"]!.GetValue<string>();
        Assert.That(message, Is.EqualTo($"Stored, not inline: {Name} (7 records). {LargeResultHints.FetchCall(resultId, path: "successData." + Name)}"));
        Assert.That(message, Does.Not.Contain("cut to fit").And.Not.Contain("INCOMPLETE"));
        var omitted = pointer["omitted"]!.AsArray();
        Assert.That(omitted, Has.Count.EqualTo(1));
        Assert.That(omitted[0]!["path"]!.GetValue<string>(), Is.EqualTo("successData." + Name));
        Assert.That(omitted[0]!["total"]!.GetValue<int>(), Is.EqualTo(7));
        Assert.That(omitted[0]!["returned"]!.GetValue<int>(), Is.EqualTo(0));
        Assert.That(omitted[0]!["unit"]!.GetValue<string>(), Is.EqualTo(ResultPager.UnitItems), "the unit ResultPager.MoveOut writes for every array");

        // What GetLargeResult(path: successData.fullResults) pages: the stored Envelope's Data holds the rows in full.
        JsonObject wrapper = StoredWrapper(resultId);
        Assert.That(wrapper["Type"]!.GetValue<string>(), Is.EqualTo("Envelope"));
        Assert.That(wrapper["Omitted"]!.AsArray(), Has.Count.EqualTo(1));
        Assert.That(wrapper.ContainsKey("Sequence"), Is.False, "a path-addressed file, not a sequence file");
        var rows = ResultPager.SelectNode(wrapper["Data"]!, "successData." + Name) as JsonArray;
        Assert.That(rows, Is.Not.Null);
        Assert.That(rows!, Has.Count.EqualTo(7));
        Assert.That(rows[6]!["stackTrace"]!.GetValue<string>(), Does.StartWith($"{Marker}-7"));
        Assert.That(wrapper["Data"]!["successData"]!["passed"]!.GetValue<int>(), Is.EqualTo(120), "the rest of the envelope is stored too");
    }

    [Test]
    public async Task Detach_OnTheCallContext_IsHandledLikeADetachOnTheResult()
    {
        var context = new ToolCallContext(null, null, "Probe", "test reason", 0);
        string text;
        try
        {
            ToolCallContext.Set(context);
            ToolCallContext.Current!.Detach(Name, Rows(4));

            text = await MarshalAsync(Marshaller(_root), Summary());
        }
        finally
        {
            ToolCallContext.Set(null);
        }

        Assert.That(text, Does.Not.Contain(Marker));
        var pointer = JsonNode.Parse(text)!["largeResult"]!.AsObject();
        Assert.That(pointer["message"]!.GetValue<string>(), Does.StartWith($"Stored, not inline: {Name} (4 records)."));
        var rows = ResultPager.SelectNode(StoredWrapper(pointer["resultId"]!.GetValue<string>())["Data"]!, "successData." + Name) as JsonArray;
        Assert.That(rows!, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task Detach_AStringPayload_IsStoredAsLines()
    {
        var result = Summary();
        result.Detach("log", "line one\nline two\nline three\n");

        string text = await MarshalAsync(Marshaller(_root), result);

        var pointer = JsonNode.Parse(text)!["largeResult"]!.AsObject();
        Assert.That(pointer["omitted"]![0]!["unit"]!.GetValue<string>(), Is.EqualTo(ResultPager.UnitLines));
        Assert.That(pointer["message"]!.GetValue<string>(), Does.StartWith("Stored, not inline: log (3 lines)."));
        var stored = ResultPager.SelectNode(StoredWrapper(pointer["resultId"]!.GetValue<string>())["Data"]!, "successData.log");
        Assert.That(stored!.GetValue<string>(), Is.EqualTo("line one\nline two\nline three\n"));
    }

    [Test]
    public async Task Detach_AnEmptyPayload_StoresNothingAndLeavesTheTextAlone()
    {
        var result = Summary();
        result.Detach(Name, new List<object>());

        string text = await MarshalAsync(Marshaller(_root), result);

        Assert.That(text, Is.EqualTo(FullText(result)));
        Assert.That(StoredFiles(), Is.Empty);
    }

    [Test]
    public async Task Detach_OnAResultWithNoSuccessData_DropsThePayloadAndDoesNotThrow()
    {
        var failure = new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ToolErrorCode.NotFound, "nothing there") };
        failure.Detach(Name, Rows(3));
        var logger = new CapturingLogger<ToolResultMarshaller>();

        string text = await MarshalAsync(Marshaller(_root, logger: logger), failure);

        Assert.That(text, Is.EqualTo(FullText(failure)));
        Assert.That(StoredFiles(), Is.Empty);
        Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning), Is.EqualTo(1));
    }

    // ---- over budget: the detached entry plus the normal cut ----

    [Test]
    public async Task Detach_WhenTheRemainderIsAlsoOverBudget_KeepsTheUsualPageAndAppendsTheDetachedPointer()
    {
        var result = BigSearch(400);
        result.Detach(Name, Rows(7));

        string text = await MarshalAsync(Marshaller(_root), result);

        Assert.That(text.Length, Is.LessThanOrEqualTo(LargeResultHelper.InlineBudgetChars));
        Assert.That(text, Does.Not.Contain(Marker));
        var envelope = JsonNode.Parse(text)!.AsObject();
        Assert.That(envelope.First().Key, Is.EqualTo("largeResult"));
        var pointer = envelope["largeResult"]!.AsObject();
        string pagedId = pointer["resultId"]!.GetValue<string>();

        // The usual sequence page of the remainder: cursor fields and its own message.
        int returned = pointer["returned"]!.GetValue<int>();
        Assert.That(returned, Is.InRange(1, 399));
        Assert.That(pointer["nextOffset"]!.GetValue<int>(), Is.EqualTo(returned));
        string message = pointer["message"]!.GetValue<string>();
        Assert.That(message, Does.StartWith($"INCOMPLETE: {returned} of 400 records returned."));

        // Two files: the paged remainder (a sequence file, payload-free) and the detached payload (path-addressed).
        Assert.That(StoredFiles().Count(), Is.EqualTo(2));
        JsonObject pagedWrapper = StoredWrapper(pagedId);
        Assert.That(pagedWrapper.ContainsKey("Sequence"), Is.True);
        Assert.That(pagedWrapper["Data"]!["successData"]!.AsObject().ContainsKey(Name), Is.False);

        string detachedSentence = message[message.IndexOf("Stored, not inline:", StringComparison.Ordinal)..];
        Assert.That(detachedSentence, Does.StartWith($"Stored, not inline: {Name} (7 records)."));
        string detachedId = StoredFiles()
            .Select(f => Path.GetFileNameWithoutExtension(f).Split('_').Last())
            .Single(id => id != pagedId);
        Assert.That(detachedSentence, Is.EqualTo($"Stored, not inline: {Name} (7 records). {LargeResultHints.FetchCall(detachedId, path: "successData." + Name)}"));
        var rows = ResultPager.SelectNode(StoredWrapper(detachedId)["Data"]!, "successData." + Name) as JsonArray;
        Assert.That(rows!, Has.Count.EqualTo(7));
    }

    // ---- failures ----

    [Test]
    public async Task Detach_NoSolutionRoot_EmitsAnUnstoredPointerWithAWarning_AndNoPayload()
    {
        var result = Summary();
        result.Detach(Name, Rows(7));

        string text = await MarshalAsync(Marshaller(solutionRoot: null), result);

        Assert.That(text, Does.Not.Contain(Marker));
        var envelope = JsonNode.Parse(text)!.AsObject();
        Assert.That(envelope.First().Key, Is.EqualTo("largeResult"));
        var pointer = envelope["largeResult"]!.AsObject();
        Assert.That(pointer.ContainsKey("resultId"), Is.False);
        Assert.That(pointer["writtenToFile"]!.GetValue<bool>(), Is.False);
        Assert.That(pointer["omitted"]![0]!["path"]!.GetValue<string>(), Is.EqualTo("successData." + Name));
        Assert.That(envelope["warningDetails"]!.GetValue<string>(), Does.Contain("no solution is loaded").And.Contain("not retrievable"));
        Assert.That(envelope["successData"]!["passed"]!.GetValue<int>(), Is.EqualTo(120));
    }

    [Test]
    public async Task Detach_StoreThrows_StillReturnsTheResultWithoutThePayload_AndAWarning()
    {
        // A solution root that is a file: creating <root>/.roslynsentinel/largeresults fails with an IOException.
        string fileAsRoot = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(fileAsRoot, "x");
        var logger = new CapturingLogger<ToolResultMarshaller>();
        var result = Summary();
        result.Detach(Name, Rows(7));

        string text = await MarshalAsync(Marshaller(fileAsRoot, logger: logger), result);

        Assert.That(text, Does.Not.Contain(Marker));
        Assert.That(text, Does.Not.Contain(fileAsRoot), "no internal path in tool-visible output");
        var envelope = JsonNode.Parse(text)!.AsObject();
        Assert.That(envelope["largeResult"]!.AsObject().ContainsKey("resultId"), Is.False);
        Assert.That(envelope["warningDetails"]!.GetValue<string>(), Does.Contain("could not be written"));
        Assert.That(envelope["successData"]!["failed"]!.GetValue<int>(), Is.EqualTo(7));
        Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Error), Is.EqualTo(1));
    }

    // ---- other modes and absent context ----

    [TestCase(ResultMarshallerMode.Passthrough)]
    [TestCase(ResultMarshallerMode.Off)]
    public async Task Detach_InAModeOtherThanOn_DropsThePayloadAndStoresNothing(ResultMarshallerMode mode)
    {
        var result = Summary();
        result.Detach(Name, Rows(7));
        var context = new ToolCallContext(null, null, "Probe", "test reason", 0);
        string text;
        try
        {
            ToolCallContext.Set(context);
            context.Detach("other", Rows(2));

            text = await MarshalAsync(Marshaller(_root, mode), result);
        }
        finally
        {
            ToolCallContext.Set(null);
        }

        Assert.That(text, Is.EqualTo(FullText(result)), "byte-identical to what the SDK would send for the result alone");
        Assert.That(text, Does.Not.Contain(Marker));
        Assert.That(StoredFiles(), Is.Empty);
    }

    [Test]
    public async Task DetachCurrent_WithNoContext_IsANoOpThatDoesNotThrow()
    {
        ToolCallContext.Set(null);

        Assert.DoesNotThrow(() => ToolCallContext.DetachCurrent(Name, Rows(3)));
        Assert.DoesNotThrow(() => ToolCallContext.DetachCurrent("not.a.valid.name", new Dictionary<string, string>()), "a no-op validates nothing");

        var result = Summary();
        string text = await MarshalAsync(Marshaller(_root), result);
        Assert.That(text, Is.EqualTo(FullText(result)));
        Assert.That(StoredFiles(), Is.Empty);
    }

    // ---- refused payloads ----

    [Test]
    public void Detach_RefusesAPayloadThatIsNotAListOrString([Values("dictionary", "number", "object")] string kind)
    {
        object payload = kind switch
        {
            "dictionary" => new Dictionary<string, string> { ["a.cs"] = "text" },
            "number" => 42,
            _ => new { a = 1 },
        };
        var result = Summary();
        var context = new ToolCallContext(null, null, "Probe", "test reason", 0);

        Assert.That(() => result.Detach(Name, payload), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => context.Detach(Name, payload), Throws.InstanceOf<ArgumentException>());
        Assert.That(result.Detached, Is.Empty);
        Assert.That(context.Detached, Is.Empty);
    }

    [Test]
    public void Detach_RefusesABadNameANullPayloadAndADuplicate()
    {
        var result = Summary();
        var context = new ToolCallContext(null, null, "Probe", "test reason", 0);
        var rows = Rows(1);

        foreach (string bad in new[] { "", " ", "a.b", "x[0]" })
        {
            Assert.That(() => result.Detach(bad, rows), Throws.InstanceOf<ArgumentException>(), $"name '{bad}'");
            Assert.That(() => context.Detach(bad, rows), Throws.InstanceOf<ArgumentException>(), $"name '{bad}'");
        }

        Assert.That(() => result.Detach(Name, null!), Throws.InstanceOf<ArgumentException>());

        result.Detach(Name, rows);
        context.Detach(Name, rows);
        Assert.That(() => result.Detach(Name, rows), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => context.Detach(Name, rows), Throws.InstanceOf<ArgumentException>());
        Assert.That(result.Detached, Has.Count.EqualTo(1));
        Assert.That(context.Detached, Has.Count.EqualTo(1));
    }

    [Test]
    public void Detached_IsNeverSerialized()
    {
        var result = Summary();
        result.Detach(Name, Rows(3));

        string text = FullText(result);

        Assert.That(text, Does.Not.Contain(Marker).And.Not.Contain("detached").And.Not.Contain("Detached"));
    }
}
