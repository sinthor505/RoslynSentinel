using System.Text.Json;
using System.Text.RegularExpressions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Model-free check of plan_modeleval_first_slice_scenario_rework R7: the S3 brief (WideningRejectedByGate) asks the model to report,
/// per file that fails to build, the first compiler error code, so the edit gate's rejection text must show all three files with
/// their codes (ChangeWriter.cs CS1503, ChangePublisher.cs CS9174, ChangeWriterTests.cs CS0144). The world is built over the seed
/// like a scenario run (same tool surface), the three-file retype of the RetypedAndBuildBroken variant is sent through the real
/// ReplaceSnippet tool in one batchEdits call, and the reply is read for (code, file) pairs. No LM Studio is needed: the agent
/// client is never used. The fixture needs a package restore (NuGet cache or network) and dotnet, so the class is explicit.
/// Filter: <c>WideningGateMessageTests</c>.
/// </summary>
// SchemaOptions, HiddenSchemaParams and the ToolArgumentValidator caches are process-static and every world resets them, so this
// fixture must not run in parallel with other world-building fixtures.
[TestFixture]
[NonParallelizable]
[Explicit("Needs dotnet and a package restore (a few minutes the first time), no model. Run: WideningGateMessageTests")]
public class WideningGateMessageTests
{
    private const string ScenarioId = "WideningRejectedByGate";
    private const string VariantName = "RetypedAndBuildBroken";

    // The same edit as the variant's Retyped helper: the Apply declaration in interface, class and fake.
    private const string OldText = "Apply(Dictionary<FilePath, string> changes";
    private const string NewText = "Apply(IReadOnlyDictionary<FilePath, string> changes";

    private static readonly Regex CodeAtFile = new(@"(?<code>CS\d{4}) at [^\r\n]*?(?<file>[A-Za-z0-9_.]+\.cs):\d+", RegexOptions.Compiled);

    private ModelEvalMcpEnvironment _world = null!;
    private ImplementationScenario _scenario = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _scenario = ImplementationScenarioRegistry.Find(ScenarioId);
        var sampleDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            nameof(WideningGateMessageTests),
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(sampleDirectory);

        // The seed is written before the solution loads, so the server never sees the writes; the tool surface is the one a scenario run uses.
        _world = await ModelEvalMcpEnvironment.CreateAsync(
            sampleDirectory,
            restorePackages: true,
            beforeLoad: (fixture, _, _) =>
            {
                WriteFiles(fixture.SolutionDirectory, _scenario.FilesForStep(0));
                return Task.CompletedTask;
            },
            afterLoad: null,
            TestContext.CurrentContext.CancellationToken,
            configureToolSurface: EditArmToolSurface.RegisterAdvanced,
            fullToolSchema: _scenario.OfferFullToolSchema);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }
    }

    [Test]
    public async Task Gate_RejectsTheRetypeEdit_AndNamesAllThreeFilesWithCodes()
    {
        // The three files and their post-edit text come from the variant overlay, so the test and the outcome table share one retype.
        var overlay = _scenario.Steps[0].Variants!.Single(variant => variant.Name == VariantName).Files;
        Assert.That(overlay, Has.Count.EqualTo(3), "the " + VariantName + " variant overlays exactly the three files of Files");
        Assert.That(overlay.Values, Has.All.Contains(NewText), "every overlay file carries the retyped Apply declaration");

        var edits = overlay.Keys
            .Select(relativePath => new Dictionary<string, object?>
            {
                ["filePath"] = Path.Combine(_world.SolutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                ["oldContent"] = OldText,
                ["newContent"] = NewText,
            })
            .ToList();

        var result = await _world.Client.CallToolAsync(
            "ReplaceSnippet",
            new Dictionary<string, object?>
            {
                ["reason"] = "Retypes the changes parameter of Apply to IReadOnlyDictionary in the interface, the class and the fake",
                ["action"] = "apply",
                ["batchEdits"] = edits,
            },
            cancellationToken: TestContext.CurrentContext.CancellationToken).AsTask();
        var text = TextOf(result);
        TestContext.Out.WriteLine(text);

        var found = DiagnosticsIn(text);
        TestContext.Out.WriteLine("(code, file) pairs: " + string.Join(", ", found.Select(pair => $"{pair.Code} {pair.File}")));
        Assert.Multiple(() =>
        {
            Assert.That(found.Contains(("CS1503", "ChangeWriter.cs")), Is.True, "the gate reply does not show ChangeWriter.cs CS1503: " + text);
            Assert.That(found.Contains(("CS9174", "ChangePublisher.cs")), Is.True, "the gate reply does not show ChangePublisher.cs CS9174: " + text);
            Assert.That(found.Contains(("CS0144", "ChangeWriterTests.cs")), Is.True, "the gate reply does not show ChangeWriterTests.cs CS0144: " + text);
        });

        // Rejected means nothing was written.
        foreach (var relativePath in overlay.Keys)
        {
            var onDisk = File.ReadAllText(Path.Combine(_world.SolutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.That(onDisk, Does.Not.Contain(NewText), relativePath + " was written although the gate rejects the edit");
        }
    }

    private static void WriteFiles(string solutionDirectory, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (relativePath, text) in files)
        {
            var path = Path.Combine(solutionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    private static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    // The reply is a JSON envelope; compiler errors appear as structured diagnostics (id + filePath objects) and as "CSxxxx at <path>:<line>"
    // lines inside message strings. Both forms are read so a change of presentation does not hide the pairs; a non-JSON reply is read as lines.
    private static HashSet<(string Code, string File)> DiagnosticsIn(string text)
    {
        var found = new HashSet<(string Code, string File)>();
        try
        {
            using var document = JsonDocument.Parse(text);
            Walk(document.RootElement, found);
        }
        catch (JsonException)
        {
            AddFromLines(text, found);
        }

        return found;
    }

    private static void Walk(JsonElement element, HashSet<(string Code, string File)> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("filePath", out var path) && path.ValueKind == JsonValueKind.String)
                {
                    found.Add((id.GetString()!, FileName(path.GetString()!)));
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, found);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, found);
                }

                break;
            case JsonValueKind.String:
                AddFromLines(element.GetString()!, found);
                break;
        }
    }

    private static void AddFromLines(string text, HashSet<(string Code, string File)> found)
    {
        foreach (Match match in CodeAtFile.Matches(text))
        {
            found.Add((match.Groups["code"].Value, match.Groups["file"].Value));
        }
    }

    private static string FileName(string path) => path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
}
