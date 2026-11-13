using System.Text.Json;
using ModelContextProtocol.Protocol;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// Regenerate mode (cut turn 0): the model generates every turn live, the host scripts ALL recorded turns and serves the variant when
/// the model re-issues the source's first error call. Pipeline tests use the scripted model stand-in and the checked-in armF transcript
/// (its recorded message is the short armF text; variant armE adds a sentence, so the two are distinguishable); host tests call
/// <see cref="TranscriptToolHost.ScriptedInFull"/> directly.
/// </summary>
[TestFixture]
// LlmOptions.Configure is global state, so fixtures that call it must not run in parallel with each other.
[NonParallelizable]
public class ErrorVariantRegenerateTests
{
    private const string ModelKey = "regen-test-model";

    // A phrase only variant armE carries; the recorded armF message does not contain it.
    private const string VariantOnlyText = "lists every file in the solution";

    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "rs-errvariant-regen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
        LlmOptions.Configure(["--llm-model", ModelKey, "--llm-temperature", "0.2"]);
    }

    [TearDown]
    public void TearDown()
    {
        LlmOptions.Configure([]);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task Regenerate_ModelReissuesTheErrorCall_ServesTheVariantAndClassifiesTheNextTurn()
    {
        var transcript = SavedTranscript.Load(SourcePath());
        var errorCall = transcript.Turns[0].ToolCalls[0];
        var variant = ErrorVariantLoader.LoadFile(VariantPath("armE"));
        var handler = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse(errorCall.ToolName, errorCall.ArgumentsJson),
            ScriptedModelHandler.FunctionCallSse("Search", "{\"mode\":\"text\",\"query\":\"CustomsDeclarationPrinter\"}"))
        {
            ModelsJson = ScriptedModelHandler.LoadedModelsJson(ModelKey),
        };
        var runDirectory = Path.Combine(_tempRoot, "run");

        var rows = await ErrorVariantReplay.RunAsync(
            NewOptions("FileNotFound/armE"), handler, runDirectory, TestContext.CurrentContext.CancellationToken);

        var row = rows.Single();
        var turn1ToolMessage = ScriptedModelHandler.ToolOutput(handler.Requests[1]);
        var csvLines = File.ReadAllLines(Path.Combine(runDirectory, "variants.csv"));
        var sample = SavedTranscript.Load(Directory.GetFiles(runDirectory, "transcript.json", SearchOption.AllDirectories).Single());
        Assert.Multiple(() =>
        {
            Assert.That(row.HarnessError, Is.Empty);
            Assert.That(row.Mode, Is.EqualTo(VariantSampleRow.RegeneratedMode));
            Assert.That(row.RegenTurn, Is.EqualTo(1));
            Assert.That(row.CutTurn, Is.EqualTo(1), "regenerated rows record the source's first error turn E; nothing is cut");
            Assert.That(row.DecisionClass, Is.EqualTo("Widen:Search"));
            Assert.That(row.FirstTool, Is.EqualTo("Search"));
            Assert.That(row.StopReason, Is.EqualTo("TurnCap"));
            Assert.That(handler.Requests, Has.Count.EqualTo(2), "turn cap E + 1 = 2: both turns generated live, no seed");
            Assert.That(turn1ToolMessage, Does.Contain(VariantOnlyText), "the model read the variant text");
            Assert.That(turn1ToolMessage, Does.Contain(ResultOverride.ApplyToCall(errorCall, variant, null).ResultJson));
            Assert.That(turn1ToolMessage, Does.Not.Contain(errorCall.ResultJson), "not the recorded bytes");
            Assert.That(csvLines, Has.Length.EqualTo(2));
            Assert.That(VariantSampleRow.Parse(csvLines[1]), Is.EqualTo(row));
            Assert.That(sample.Turns, Has.Count.EqualTo(2), "both turns are the run's own, none seeded");
        });
    }

    [Test]
    public async Task Regenerate_ModelReadsADifferentPathAtTurn1_IsARegenMissNamingTheOffScriptTool()
    {
        var handler = new ScriptedModelHandler(
            ScriptedModelHandler.FunctionCallSse("ReadFile", "{\"filePath\":\"C:\\\\nowhere\\\\SomethingElse.cs\"}"))
        {
            ModelsJson = ScriptedModelHandler.LoadedModelsJson(ModelKey),
        };

        var rows = await ErrorVariantReplay.RunAsync(
            NewOptions("FileNotFound/armE"), handler, Path.Combine(_tempRoot, "run"), TestContext.CurrentContext.CancellationToken);

        var row = rows.Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.HarnessError, Is.Empty);
            Assert.That(row.Mode, Is.EqualTo(VariantSampleRow.RegeneratedMode));
            Assert.That(row.DecisionClass, Does.StartWith("RegenMiss:"));
            Assert.That(row.DecisionClass, Is.EqualTo("RegenMiss:ReadFile"));
            Assert.That(row.RegenTurn, Is.Null);
            Assert.That(row.FirstTool, Is.Empty, "a miss classifies nothing");
            Assert.That(row.StopReason, Is.EqualTo("OffScript:ReadFile"));
            Assert.That(handler.Requests, Has.Count.EqualTo(1), "the off-script call stops the run");
        });
    }

    [Test]
    public async Task Regenerate_ModelAnswersWithoutAnyCall_IsARegenMissWithNone()
    {
        var handler = new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("BLOCKED: no file to read."))
        {
            ModelsJson = ScriptedModelHandler.LoadedModelsJson(ModelKey),
        };

        var rows = await ErrorVariantReplay.RunAsync(
            NewOptions("FileNotFound/armE"), handler, Path.Combine(_tempRoot, "run"), TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(rows.Single().DecisionClass, Is.EqualTo("RegenMiss:none"));
            Assert.That(rows.Single().RegenTurn, Is.Null);
            Assert.That(rows.Single().Mode, Is.EqualTo(VariantSampleRow.RegeneratedMode));
        });
    }

    [Test]
    public void FromEnvironment_CutTurnZeroSelectsRegenerate_AndNegativeIsRefused()
    {
        var env = new Dictionary<string, string?>
        {
            [ErrorVariantReplayOptions.SourcesVariable] = "a",
            [ErrorVariantReplayOptions.FilesVariable] = "x/y",
            [ErrorVariantReplayOptions.CutTurnVariable] = "0",
        };

        var options = ErrorVariantReplayOptions.FromEnvironment(name => env.GetValueOrDefault(name));
        env[ErrorVariantReplayOptions.CutTurnVariable] = "-1";

        Assert.Multiple(() =>
        {
            Assert.That(options.CutTurn, Is.EqualTo(ErrorVariantReplayOptions.RegenerateCutTurn));
            Assert.That(
                () => ErrorVariantReplayOptions.FromEnvironment(name => env.GetValueOrDefault(name)),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains(ErrorVariantReplayOptions.CutTurnVariable));
        });
    }

    [Test]
    public async Task ScriptedInFull_ServesTheOverriddenErrorCallWithTheVariantBytes_AndLaterRecordedCallsAsRecorded()
    {
        var transcript = SavedTranscript.Load(SourcePath());
        var variant = ErrorVariantLoader.LoadFile(VariantPath("armE"));
        var recordedError = transcript.Turns[0].ToolCalls[0];
        var recordedSearch = transcript.Turns[1].ToolCalls[0];
        var host = TranscriptToolHost.ScriptedInFull(transcript, variant);

        var errorResult = await host.CallToolAsync(recordedError.ToolName, RunnerArguments(recordedError.ArgumentsJson), CancellationToken.None);
        var searchResult = await host.CallToolAsync(recordedSearch.ToolName, RunnerArguments(recordedSearch.ArgumentsJson), CancellationToken.None);
        var repeatResult = await host.CallToolAsync(recordedError.ToolName, RunnerArguments(recordedError.ArgumentsJson), CancellationToken.None);

        var expected = ResultOverride.ApplyToCall(recordedError, variant, null);
        Assert.Multiple(() =>
        {
            Assert.That(host.OffScript, Is.False);
            Assert.That(errorResult.IsError, Is.True);
            Assert.That(Blocks(errorResult)[0], Is.EqualTo(expected.ResultJson), "the byte-faithful splice of the variant message");
            Assert.That(Blocks(errorResult)[0], Does.Contain(VariantOnlyText));
            Assert.That(Blocks(errorResult)[0], Is.Not.EqualTo(recordedError.ResultJson));
            Assert.That(Blocks(searchResult), Is.EqualTo(new[] { recordedSearch.ResultJson }), "a later recorded call is served as recorded");
            Assert.That(Blocks(repeatResult)[0], Is.EqualTo(expected.ResultJson), "a repeat of the error call keeps the variant (sticky)");
            Assert.That(host.OverriddenServedCallIndex, Is.EqualTo(0), "the first serve of the overridden call is kept");
        });
    }

    [Test]
    public async Task ScriptedInFull_OverriddenCallServedAfterAnotherCall_ReportsItsCallIndex()
    {
        var transcript = SavedTranscript.Load(SourcePath());
        var variant = ErrorVariantLoader.LoadFile(VariantPath("armE"));
        var recordedError = transcript.Turns[0].ToolCalls[0];
        var recordedSearch = transcript.Turns[1].ToolCalls[0];
        var host = TranscriptToolHost.ScriptedInFull(transcript, variant);

        await host.CallToolAsync(recordedSearch.ToolName, RunnerArguments(recordedSearch.ArgumentsJson), CancellationToken.None);
        Assert.That(host.OverriddenServedCallIndex, Is.Null, "an ordinary scripted call is not the overridden one");
        await host.CallToolAsync(recordedError.ToolName, RunnerArguments(recordedError.ArgumentsJson), CancellationToken.None);

        Assert.That(host.OverriddenServedCallIndex, Is.EqualTo(1));
    }

    [Test]
    public void SeededHost_NeverScriptsTheCutTurn_SoItNeverReportsTheOverriddenCallServed()
    {
        var transcript = SavedTranscript.Load(SourcePath());
        var variant = ErrorVariantLoader.LoadFile(VariantPath("armE"));
        var recordedError = transcript.Turns[0].ToolCalls[0];
        var host = new TranscriptToolHost(transcript, cutTurn: 1, variant);

        _ = host.CallToolAsync(recordedError.ToolName, RunnerArguments(recordedError.ArgumentsJson), CancellationToken.None);

        Assert.That(host.OffScript, Is.True, "turn 1 is the seed, not script");
        Assert.That(host.OverriddenServedCallIndex, Is.Null);
    }

    private static ErrorVariantReplayOptions NewOptions(string variant) => new()
    {
        Sources = [SourcePath()],
        Variants = [variant],
        Samples = 1,
        ContinueTurns = 1,
        CutTurn = ErrorVariantReplayOptions.RegenerateCutTurn,
    };

    // armF's checked-in transcript has the full header (tool descriptions and schemas); its first error is turn 1, a ReadFile miss.
    private static string SourcePath() =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "FileNotFound.armF.sample-01.transcript.json");

    private static string VariantPath(string id) =>
        Path.Combine(AppContext.BaseDirectory, "ErrorVariants", "FileNotFound", id + ".md");

    private static Dictionary<string, object?> RunnerArguments(string argumentsJson) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)!;

    private static List<string> Blocks(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Select(block => block.Text).ToList();
}
