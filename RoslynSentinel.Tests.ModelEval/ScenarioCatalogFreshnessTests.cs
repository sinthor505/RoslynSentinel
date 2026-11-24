using System.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Freshness check of docs/generated/modeleval_scenarios.md, same shape as ArchitectureDocFreshnessTests in Tests.Server:
/// one test regenerates the file when ROSLYNSENTINEL_UPDATE_GENERATED_DOCS is set, one Ratchet test compares the committed file.
/// Model-free; scripts/Generate-ArchitectureMap.ps1 sets the variable and runs the regeneration test.
/// </summary>
[TestFixture]
public class ScenarioCatalogFreshnessTests
{
    private const string UpdateEnvVar = "ROSLYNSENTINEL_UPDATE_GENERATED_DOCS";
    private const string FileName = "modeleval_scenarios.md";
    private const string RegenerateHint = "Regenerate with: scripts/Generate-ArchitectureMap.ps1";

    // No BOM: the written file must equal Render() exactly.
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static string GetRepoRoot() =>
        RepoRootLocator.TryFind(AppContext.BaseDirectory) ?? throw new InvalidOperationException("Could not find RoslynSentinel.slnx - unable to locate repo root");

    private static bool IsUpdateMode() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(UpdateEnvVar));

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string Clip(string? line, int maxLength)
    {
        if (line is null)
        {
            return "<no such line>";
        }

        return line.Length <= maxLength ? line : line.Substring(0, maxLength) + "...";
    }

    // Positional line diff after line-ending normalisation: the first maxLines differing lines (expected = fresh, actual = committed) plus a count of the rest.
    private static string DescribeFirstDifferences(string expected, string actual, int maxLines = 10, int maxLineLength = 200)
    {
        var expectedLines = NormalizeLineEndings(expected).Split('\n');
        var actualLines = NormalizeLineEndings(actual).Split('\n');
        var lineCount = Math.Max(expectedLines.Length, actualLines.Length);

        var details = new StringBuilder();
        var total = 0;
        var shown = 0;
        for (var index = 0; index < lineCount; index++)
        {
            var expectedLine = index < expectedLines.Length ? expectedLines[index] : null;
            var actualLine = index < actualLines.Length ? actualLines[index] : null;
            if (string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                continue;
            }

            total++;
            if (shown >= maxLines)
            {
                continue;
            }

            shown++;
            details.Append("  line ").Append(index + 1).Append(":\n");
            details.Append("    expected: ").Append(Clip(expectedLine, maxLineLength)).Append('\n');
            details.Append("    actual:   ").Append(Clip(actualLine, maxLineLength)).Append('\n');
        }

        if (total > shown)
        {
            details.Append("  ... and ").Append(total - shown).Append(" more differing line(s)\n");
        }

        return $"{total} differing line(s) (expected = freshly generated, actual = committed file):\n{details}";
    }

    [Test]
    public void ScenarioCatalogFreshness_WhenUpdateEnvVarSet_RegeneratesFile()
    {
        if (!IsUpdateMode())
        {
            Assert.Ignore($"Regeneration skipped: set {UpdateEnvVar}=1 to rewrite docs/generated/{FileName}. " +
                          "This test writes a file and asserts nothing, so a green run would not prove it ran.");
        }

        var docsGeneratedDirectory = Path.Combine(GetRepoRoot(), "docs", "generated");
        Directory.CreateDirectory(docsGeneratedDirectory);
        File.WriteAllText(Path.Combine(docsGeneratedDirectory, FileName), ScenarioCatalogGenerator.Render(), Utf8NoBom);
    }

    [Test]
    [Category("Ratchet")]
    public void ScenarioCatalogFreshness_WhenCommittedFileExists_MatchesRegeneratedContent()
    {
        if (IsUpdateMode())
        {
            Assert.Ignore("Update mode active - regenerating instead of asserting");
        }

        var path = Path.Combine(GetRepoRoot(), "docs", "generated", FileName);
        if (!File.Exists(path))
        {
            // The committed file is created by the first regeneration (Step 66); until then there is nothing to compare.
            Assert.Ignore($"No committed catalog at {path}. {RegenerateHint}");
        }

        var committed = NormalizeLineEndings(File.ReadAllText(path, Utf8NoBom));
        var fresh = NormalizeLineEndings(ScenarioCatalogGenerator.Render());
        if (!string.Equals(committed, fresh, StringComparison.Ordinal))
        {
            Assert.Fail($"Scenario catalog is stale: {path}\n{DescribeFirstDifferences(fresh, committed)}{RegenerateHint}");
        }
    }
}
