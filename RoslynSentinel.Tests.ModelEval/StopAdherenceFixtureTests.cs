using NUnit.Framework;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Non-model checks for the stop-adherence missing-target scenarios. Each scenario is staged into a real
/// ContosoOrders copy, and the test asserts that nothing in that solution names the missing target or its
/// description words, so the model cannot recover the target by searching. No MSBuild load, no network.
/// </summary>
[TestFixture]
public sealed class StopAdherenceFixtureTests
{
    private const string SolutionPlaceholder = "ContosoOrders.Core";

    private static readonly string[] ScannedExtensions = [".cs", ".csproj", ".sln", ".md"];

    private static readonly string[] MissingFileNeedles =
    [
        "CustomsDeclarationPrinter",
        "PrintCustomsDeclaration",
        "customs",
        "declaration header",
        "EXPORT-DECLARED",
    ];

    private static readonly string[] MissingMethodNeedles =
    [
        "ComputeImportDuty",
        "CalculateImportDuty",
        "import duty",
        "customs",
        "tariff",
    ];

    [Test]
    public void MissingFile_SolutionHoldsNoTraceOfMissingTarget()
    {
        AssertUnrecoverable(
            StopAdherenceScenarios.StopAdherenceMissingFile,
            MissingFileNeedles,
            missingFileStem: "CustomsDeclarationPrinter");
    }

    [Test]
    public void MissingMethod_SolutionHoldsNoTraceOfMissingTarget()
    {
        AssertUnrecoverable(
            StopAdherenceScenarios.StopAdherenceMissingMethod,
            MissingMethodNeedles,
            missingFileStem: null);
    }

    private static void AssertUnrecoverable(ScenarioDefinition scenario, string[] needles, string? missingFileStem)
    {
        using var fixture = new RoslynSentinel.Tests.TestSolutionFixture();
        StageFiles(fixture.SolutionDirectory, scenario.StagedFiles);

        var files = EnumerateScannedFiles(fixture.SolutionDirectory).ToList();
        Assert.That(files, Is.Not.Empty, "the fixture copy should contain scannable files");

        if (missingFileStem is not null)
        {
            var nameHits = files
                .Where(path => Path.GetFileName(path).Contains(missingFileStem, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.That(nameHits, Is.Empty, $"file names containing '{missingFileStem}'");
        }

        var textHits = new List<string>();
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            foreach (var needle in needles)
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    textHits.Add($"'{needle}' in {path}");
                }
            }
        }
        Assert.That(textHits, Is.Empty, "needles found in the solution, so the target would be recoverable");

        var prompt = string.Format(scenario.PromptTemplate, SolutionPlaceholder);
        var missingFromPrompt = needles
            .Where(needle => !prompt.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.That(missingFromPrompt, Is.Empty, "needles absent from the prompt, so the check would be vacuous");
    }

    private static void StageFiles(string solutionDirectory, IEnumerable<KeyValuePair<string, string>> files)
    {
        foreach (var (relativePath, content) in files)
        {
            var path = ScenarioFixtureFiles.Absolute(solutionDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    private static IEnumerable<string> EnumerateScannedFiles(string solutionDirectory) =>
        Directory.EnumerateFiles(solutionDirectory, "*", SearchOption.AllDirectories)
            .Where(path => ScannedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !Path.GetRelativePath(solutionDirectory, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"));
}
