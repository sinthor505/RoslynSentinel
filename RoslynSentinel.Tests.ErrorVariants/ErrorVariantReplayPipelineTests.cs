using System.Net;
using System.Text.Json;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// The whole replay pipeline with a scripted stand-in for LM Studio (no model, no MCP server): one classified row plus a loadable
/// transcript, a harness-error row when no model is reachable, and a refusal (no model request) when the source was recorded under
/// different sampling settings. Plus the env-variable parsing and the source/variant resolution.
/// </summary>
[TestFixture]
// LlmOptions.Configure is global state, so fixtures that call it must not run in parallel with each other.
[NonParallelizable]
public class ErrorVariantReplayPipelineTests
{
    private const string ModelKey = "replay-test-model";

    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "rs-errvariant-replay-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
        LlmOptions.Configure(["--llm-model", ModelKey, "--llm-temperature", "0.2"]);
    }

    [TearDown]
    public void TearDown()
    {
        // Re-read args (none) and the environment, undoing the test-only model and temperature.
        LlmOptions.Configure([]);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task EndToEnd_StubModel_WritesOneClassifiedRowAndALoadableTranscript()
    {
        var handler = new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("BLOCKED: the file does not exist."))
        {
            ModelsJson = ScriptedModelHandler.LoadedModelsJson(ModelKey),
        };
        var runDirectory = Path.Combine(_tempRoot, "run");

        var rows = await ErrorVariantReplay.RunAsync(
            NewOptions(ArmESource(), "FileNotFound/armF"), handler, runDirectory, TestContext.CurrentContext.CancellationToken);

        var csvLines = File.ReadAllLines(Path.Combine(runDirectory, "variants.csv"));
        var transcripts = Directory.GetFiles(runDirectory, "transcript.json", SearchOption.AllDirectories);
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(csvLines, Has.Length.EqualTo(2), "header plus exactly one row");
            Assert.That(csvLines[0], Is.EqualTo(VariantSampleRow.Header));
            Assert.That(VariantSampleRow.Parse(csvLines[1]), Is.EqualTo(rows[0]));
            Assert.That(rows[0].HarnessError, Is.Empty);
            Assert.That(rows[0].DecisionClass, Is.EqualTo("StopLeading"));
            Assert.That(rows[0].CallCount, Is.Zero);
            Assert.That(rows[0].BlockedAnywhere, Is.True);
            Assert.That(rows[0].VariantId, Is.EqualTo("armF"));
            Assert.That(rows[0].ErrorCode, Is.EqualTo("FileNotFound"));
            Assert.That(rows[0].ModelKey, Is.EqualTo(ModelKey));
            Assert.That(rows[0].Temperature, Is.EqualTo("0.2"));
            Assert.That(rows[0].SourceSettings, Is.EqualTo("unknown"), "the armE source header records no model key");
            Assert.That(rows[0].CutTurn, Is.GreaterThanOrEqualTo(1));
            Assert.That(rows[0].RecordedMessageHash, Has.Length.EqualTo(12));
            Assert.That(handler.Requests, Has.Count.EqualTo(1), "exactly one model request: one continuation turn");
            Assert.That(transcripts, Has.Length.EqualTo(1));
        });

        var sample = SavedTranscript.Load(transcripts[0]);
        Assert.Multiple(() =>
        {
            Assert.That(sample.Turns, Has.Count.EqualTo(rows[0].CutTurn + 1), "seeded turns plus the one live turn");
            Assert.That(sample.Turns[^1].ModelMessage.Content, Does.StartWith("BLOCKED:"));
            Assert.That(sample.ModelKey, Is.EqualTo(ModelKey), "the sample's own header records the run's model key");
            Assert.That(sample.Temperature, Is.EqualTo(0.2));
        });
    }

    [TestCase("refused", TestName = "NoModelReachable_ConnectionRefused_WritesHarnessErrorRow")]
    [TestCase("500", TestName = "NoModelReachable_ServerError_WritesHarnessErrorRow")]
    [TestCase("notjson", TestName = "NoModelReachable_NotLmStudio_WritesHarnessErrorRow")]
    [TestCase("notloaded", TestName = "ModelNotLoaded_WritesHarnessErrorRow")]
    public async Task ModelUnavailable_WritesOneHarnessErrorRow_WithoutThrowingOrCallingTheModel(string failure)
    {
        var handler = failure switch
        {
            "refused" => new ScriptedModelHandler { FailWith = new HttpRequestException("No connection could be made because the target machine actively refused it.") },
            "500" => new ScriptedModelHandler { FailStatus = HttpStatusCode.InternalServerError },
            "notjson" => new ScriptedModelHandler { ModelsJson = "<html>not LM Studio</html>" },
            _ => new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("BLOCKED: unreachable")) { ModelsJson = ScriptedModelHandler.LoadedModelsJson("some-other-model") },
        };
        var runDirectory = Path.Combine(_tempRoot, "run");

        var rows = await ErrorVariantReplay.RunAsync(
            NewOptions(ArmESource(), "FileNotFound/armF"), handler, runDirectory, TestContext.CurrentContext.CancellationToken);

        var csvLines = File.ReadAllLines(Path.Combine(runDirectory, "variants.csv"));
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(csvLines, Has.Length.EqualTo(2));
            Assert.That(rows[0].DecisionClass, Is.EqualTo(ErrorVariantReplay.HarnessErrorClass));
            Assert.That(rows[0].HarnessError, Does.StartWith("Model check failed, no model request was made"));
            Assert.That(rows[0].VariantId, Is.EqualTo("armF"), "the row still says which cell it stands for");
            Assert.That(handler.Requests, Is.Empty, "no model request");
            Assert.That(Directory.GetFiles(runDirectory, "transcript.json", SearchOption.AllDirectories), Is.Empty);
        });
    }

    [Test]
    public async Task SourceRecordedUnderDifferentTemperature_IsRefusedWithoutAModelRequest()
    {
        var source = WriteSourceWithHeader(temperature: 0.7);
        var handler = new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("BLOCKED: should never be asked"))
        {
            ModelsJson = ScriptedModelHandler.LoadedModelsJson(ModelKey),
        };
        var runDirectory = Path.Combine(_tempRoot, "run");

        var rows = await ErrorVariantReplay.RunAsync(
            NewOptions(source, "FileNotFound/armF"), handler, runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].SourceSettings, Is.EqualTo("mismatch"));
            Assert.That(rows[0].DecisionClass, Is.EqualTo(ErrorVariantReplay.HarnessErrorClass));
            Assert.That(rows[0].HarnessError, Does.StartWith("Refused, no model request was made"));
            Assert.That(rows[0].HarnessError, Does.Contain("temperature 0.7 recorded vs 0.2 now"));
            Assert.That(handler.Requests, Is.Empty, "no model request");
        });
    }

    [Test]
    public async Task SourceRecordedUnderTheSameSettings_RunsAndIsMarkedMatched()
    {
        var source = WriteSourceWithHeader(temperature: 0.2);
        var handler = new ScriptedModelHandler(ScriptedModelHandler.FinalMessageSse("BLOCKED: the file does not exist."))
        {
            ModelsJson = ScriptedModelHandler.LoadedModelsJson(ModelKey),
        };

        var rows = await ErrorVariantReplay.RunAsync(
            NewOptions(source, "FileNotFound/armF"), handler, Path.Combine(_tempRoot, "run"), TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].SourceSettings, Is.EqualTo("matched"));
            Assert.That(rows[0].HarnessError, Is.Empty);
            Assert.That(rows[0].DecisionClass, Is.EqualTo("StopLeading"));
            Assert.That(handler.Requests, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task UnreadableSourceTranscript_WritesAHarnessErrorRowPerSample()
    {
        var broken = Path.Combine(_tempRoot, "broken", "sample-01");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "transcript.json"), "{ not json");
        var handler = new ScriptedModelHandler { ModelsJson = ScriptedModelHandler.LoadedModelsJson(ModelKey) };

        var rows = await ErrorVariantReplay.RunAsync(
            NewOptions(Path.Combine(_tempRoot, "broken"), "FileNotFound/armE", samples: 2), handler, Path.Combine(_tempRoot, "run"), TestContext.CurrentContext.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Select(row => row.HarnessError), Has.All.StartWith("Could not load the source transcript"));
            Assert.That(rows.Select(row => row.SampleIndex), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(handler.Requests, Is.Empty);
        });
    }

    [Test]
    public void FromEnvironment_ReadsAllVariablesAndAppliesDefaults()
    {
        var env = new Dictionary<string, string?>
        {
            [ErrorVariantReplayOptions.SourcesVariable] = @"C:\runs\a; C:\runs\b\sample-01\transcript.json",
            [ErrorVariantReplayOptions.FilesVariable] = "FileNotFound/armE, FileNotFound/armF",
            [ErrorVariantReplayOptions.AbsentTargetsVariable] = "Missing.cs,Other.cs",
        };

        var options = ErrorVariantReplayOptions.FromEnvironment(name => env.GetValueOrDefault(name));

        Assert.Multiple(() =>
        {
            Assert.That(options.Sources, Is.EqualTo(new[] { @"C:\runs\a", @"C:\runs\b\sample-01\transcript.json" }));
            Assert.That(options.Variants, Is.EqualTo(new[] { "FileNotFound/armE", "FileNotFound/armF" }));
            Assert.That(options.AbsentTargets, Is.EqualTo(new[] { "Missing.cs", "Other.cs" }));
            Assert.That(options.Samples, Is.EqualTo(3));
            Assert.That(options.ContinueTurns, Is.EqualTo(1));
            Assert.That(options.CutTurn, Is.Null);
        });
    }

    [Test]
    public void FromEnvironment_ReadsExplicitNumbers()
    {
        var env = new Dictionary<string, string?>
        {
            [ErrorVariantReplayOptions.SourcesVariable] = "a",
            [ErrorVariantReplayOptions.FilesVariable] = "x/y",
            [ErrorVariantReplayOptions.SamplesVariable] = "5",
            [ErrorVariantReplayOptions.ContinueTurnsVariable] = "2",
            [ErrorVariantReplayOptions.CutTurnVariable] = "4",
        };

        var options = ErrorVariantReplayOptions.FromEnvironment(name => env.GetValueOrDefault(name));

        Assert.Multiple(() =>
        {
            Assert.That(options.Samples, Is.EqualTo(5));
            Assert.That(options.ContinueTurns, Is.EqualTo(2));
            Assert.That(options.CutTurn, Is.EqualTo(4));
        });
    }

    [TestCase(ErrorVariantReplayOptions.SourcesVariable)]
    [TestCase(ErrorVariantReplayOptions.FilesVariable)]
    public void FromEnvironment_MissingRequiredVariable_NamesIt(string missing)
    {
        var env = new Dictionary<string, string?>
        {
            [ErrorVariantReplayOptions.SourcesVariable] = "a",
            [ErrorVariantReplayOptions.FilesVariable] = "x/y",
        };
        env.Remove(missing);

        Assert.That(
            () => ErrorVariantReplayOptions.FromEnvironment(name => env.GetValueOrDefault(name)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains(missing));
    }

    [Test]
    public void FromEnvironment_BadNumber_NamesTheVariable()
    {
        var env = new Dictionary<string, string?>
        {
            [ErrorVariantReplayOptions.SourcesVariable] = "a",
            [ErrorVariantReplayOptions.FilesVariable] = "x/y",
            [ErrorVariantReplayOptions.SamplesVariable] = "0",
        };

        Assert.That(
            () => ErrorVariantReplayOptions.FromEnvironment(name => env.GetValueOrDefault(name)),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains(ErrorVariantReplayOptions.SamplesVariable));
    }

    [Test]
    public void ResolveVariants_WildcardAndBareIdAndRejections()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ErrorVariants");

        var all = ErrorVariantReplay.ResolveVariants(["FileNotFound/*"], root);
        var bare = ErrorVariantReplay.ResolveVariants(["armE", "FileNotFound/armE"], root);

        Assert.Multiple(() =>
        {
            Assert.That(all.Select(variant => variant.Id), Is.EqualTo(new[] { "armE", "armF", "armG" }));
            Assert.That(bare, Has.Count.EqualTo(1), "the same variant named twice is loaded once");
            Assert.That(bare[0].ErrorCode, Is.EqualTo("FileNotFound"));
            Assert.That(() => ErrorVariantReplay.ResolveVariants(["*"], root), Throws.TypeOf<InvalidOperationException>().With.Message.Contains("<errorCode>/*"));
            Assert.That(() => ErrorVariantReplay.ResolveVariants(["NoSuchCode/armE"], root), Throws.TypeOf<InvalidOperationException>().With.Message.Contains("FileNotFound"));
            Assert.That(() => ErrorVariantReplay.ResolveVariants(["FileNotFound/nope"], root), Throws.TypeOf<FileNotFoundException>().With.Message.Contains("armE"));
        });
    }

    [Test]
    public void ResolveSources_RunFolderExpandsToItsSampleChildrenInOrder()
    {
        foreach (var name in new[] { "sample-02", "sample-01", "notes" })
        {
            Directory.CreateDirectory(Path.Combine(_tempRoot, "run1", name));
        }

        File.WriteAllText(Path.Combine(_tempRoot, "run1", "sample-01", "transcript.json"), "{}");
        File.WriteAllText(Path.Combine(_tempRoot, "run1", "sample-02", "transcript.json"), "{}");

        var fromRun = ErrorVariantReplay.ResolveSources([Path.Combine(_tempRoot, "run1")]);
        var fromSample = ErrorVariantReplay.ResolveSources([Path.Combine(_tempRoot, "run1", "sample-02")]);
        var fromFile = ErrorVariantReplay.ResolveSources([Path.Combine(_tempRoot, "run1", "sample-01", "transcript.json")]);

        Assert.Multiple(() =>
        {
            Assert.That(fromRun.Select(source => source.Sample), Is.EqualTo(new[] { "sample-01", "sample-02" }));
            Assert.That(fromRun.Select(source => Path.GetFileName(source.RunFolder)), Has.All.EqualTo("run1"));
            Assert.That(fromSample.Single().Sample, Is.EqualTo("sample-02"));
            Assert.That(Path.GetFileName(fromSample.Single().RunFolder), Is.EqualTo("run1"));
            Assert.That(fromFile.Single().Sample, Is.EqualTo("sample-01"));
            Assert.That(() => ErrorVariantReplay.ResolveSources([Path.Combine(_tempRoot, "run1", "notes")]), Throws.TypeOf<InvalidOperationException>());
            Assert.That(() => ErrorVariantReplay.ResolveSources([Path.Combine(_tempRoot, "missing")]), Throws.TypeOf<FileNotFoundException>());
        });
    }

    private static ErrorVariantReplayOptions NewOptions(string source, string variant, int samples = 1) => new()
    {
        Sources = [source],
        Variants = [variant],
        Samples = samples,
        ContinueTurns = 1,
    };

    private static string ArmESource() =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "FileNotFound.armE.sample-01.transcript.json");

    // A run folder holding sample-01/transcript.json: the armE transcript with a settings header, as a post-Step-10 live run writes it.
    // TopP follows LlmOptions so an inherited ROSLYNSENTINEL_LLM_TOP_P does not turn a "matched" case into a mismatch.
    private string WriteSourceWithHeader(double temperature)
    {
        var transcript = SavedTranscript.Load(ArmESource());
        transcript.ModelKey = ModelKey;
        transcript.Temperature = temperature;
        transcript.TopP = LlmOptions.TopP;

        var runFolder = Path.Combine(_tempRoot, "source-run");
        var sampleFolder = Path.Combine(runFolder, "sample-01");
        Directory.CreateDirectory(sampleFolder);
        File.WriteAllText(
            Path.Combine(sampleFolder, "transcript.json"),
            JsonSerializer.Serialize(transcript, new JsonSerializerOptions { WriteIndented = true }));
        return runFolder;
    }
}
