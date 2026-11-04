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

        var omitted = pointer["omitted"]!.AsArray();
        Assert.That(omitted, Has.Count.EqualTo(1));
        Assert.That(omitted[0]!["path"]!.GetValue<string>(), Is.EqualTo("successData.regexResults"));
        Assert.That(omitted[0]!.AsObject().Select(p => p.Key), Is.EquivalentTo(new[] { "path", "returned", "total", "unit", "inferred" }));
        Assert.That(omitted[0]!["unit"]!.GetValue<string>(), Is.EqualTo("items"));
        Assert.That(omitted[0]!["inferred"]!.GetValue<bool>(), Is.True);
        int returned = omitted[0]!["returned"]!.GetValue<int>();
        Assert.That(returned, Is.InRange(1, 399));
        Assert.That(omitted[0]!["total"]!.GetValue<int>(), Is.EqualTo(400));
        Assert.That(pointer["totalRecords"]!.GetValue<int>(), Is.EqualTo(400), "compat only: omitted[0].total");
        Assert.That(envelope["successData"]!["regexResults"]!.AsArray(), Has.Count.EqualTo(returned));
        Assert.That(pointer["message"]!.GetValue<string>(), Does.Contain("GetLargeResult").And.Contain(resultId));
        Assert.That(pointer["message"]!.GetValue<string>(), Does.StartWith("INCOMPLETE:").And.Contain("path: \"successData.regexResults\""));

        // The store holds the FULL wire text, as an Envelope wrapper.
        string? stored = StoredFileFor(resultId);
        Assert.That(stored, Is.Not.Null, "file under .roslynsentinel/largeresults named by the usual convention");
        var wrapper = JsonNode.Parse(File.ReadAllText(stored!))!.AsObject();
        Assert.That(wrapper["Type"]!.GetValue<string>(), Is.EqualTo("Envelope"), "stored files use the shared options: PascalCase wrapper keys, enum by name");
        Assert.That(JsonNode.DeepEquals(wrapper["Data"], JsonNode.Parse(fullText)), Is.True);
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

        var omitted = JsonNode.Parse((string)marshalled!)!["largeResult"]!["omitted"]!.AsArray();
        Assert.That(omitted, Has.Count.EqualTo(1));
        Assert.That(omitted[0]!["path"]!.GetValue<string>(), Is.EqualTo("successData.records"));
        Assert.That(omitted[0]!["inferred"]!.GetValue<bool>(), Is.False);
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
        Assert.That(pointer["omitted"]!.AsArray(), Has.Count.EqualTo(1));
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
        Assert.That(envelope["largeResult"]!["omitted"]!.AsArray(), Is.Not.Empty);
        Assert.That(envelope["warningDetails"]!.GetValue<string>(), Does.Contain("could not be written"));
        Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Error), Is.EqualTo(1));
        Assert.That(((string)marshalled!), Does.Not.Contain(fileAsRoot), "no internal path in tool-visible output");
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
