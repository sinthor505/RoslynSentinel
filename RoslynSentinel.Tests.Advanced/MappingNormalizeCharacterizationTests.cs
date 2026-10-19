using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C6-3 characterization tests for <c>MappingEngine.GenerateMappingAsync</c>, which now formats its snippet with
/// <c>RoslynFormattingHelper.FormatNewSnippetAsync</c>. The generated mapping method is a standalone snippet
/// (it is not inserted into the document), so it is a new node with no siblings to protect. These pins are
/// byte-exact: the snippet is formatted for the line ending of the source document (dominant ending), with no leading
/// or trailing blank line.
/// </summary>
[TestFixture]
[Category("MappingEngine")] // sentinel:auto-category
public class MappingNormalizeCharacterizationTests
{
    private IWorkspaceManager _workspaceManager;
    private MappingEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new MappingEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static readonly string[] SourceLines =
    [
        "public class SourceDto",
        "{",
        "    public string Name { get; set; }",
        "    public   int  Count  { get; set; }",
        "}",
        "",
        "public class DestEntity",
        "{",
        "    public string Name { get; set; }",
        "    public int Count { get; set; }",
        "}",
        "",
    ];

    private static readonly string[] ExpectedLines =
    [
        "public static void MapSourceDtoToDestEntity(SourceDto source, DestEntity dest)",
        "{",
        "    dest.Name = source.Name;",
        "    dest.Count = source.Count;",
        "}",
    ];

    /// <summary>Joins lines with <paramref name="eol"/>; <paramref name="strayAt"/> (a line index) ends in <paramref name="strayEol"/> instead.</summary>
    private static string Join(string[] lines, string eol, int strayAt = -1, string strayEol = "")
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Length - 1)
            {
                sb.Append(i == strayAt ? strayEol : eol);
            }
        }

        return sb.ToString();
    }

    private async Task<string> GenerateAsync(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Models.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.GenerateMappingAsync("Models.cs", "SourceDto", "DestEntity");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        return result.UpdatedText!;
    }

    [Test]
    public async Task GenerateMapping_LfDocument_ByteExactLfSnippet()
    {
        var text = await GenerateAsync(Join(SourceLines, "\n"));

        Assert.That(text, Is.EqualTo(Join(ExpectedLines, "\n")));
    }

    [Test]
    public async Task GenerateMapping_CrlfDocument_ByteExactCrlfSnippet()
    {
        var text = await GenerateAsync(Join(SourceLines, "\r\n"));

        Assert.That(text, Is.EqualTo(Join(ExpectedLines, "\r\n")));
    }

    [Test]
    public async Task GenerateMapping_LfDominantMixedDocument_StrayCrlfLineDoesNotLeakIntoSnippet()
    {
        // Line index 2 (a property line) ends in a stray CRLF; the dominant ending is still LF.
        var text = await GenerateAsync(Join(SourceLines, "\n", strayAt: 2, strayEol: "\r\n"));

        Assert.That(text, Is.EqualTo(Join(ExpectedLines, "\n")));
    }

    [Test]
    public async Task GenerateMapping_CrlfDominantMixedDocument_StrayLfLineDoesNotLeakIntoSnippet()
    {
        var text = await GenerateAsync(Join(SourceLines, "\r\n", strayAt: 2, strayEol: "\n"));

        Assert.That(text, Is.EqualTo(Join(ExpectedLines, "\r\n")));
    }

    [Test]
    public async Task GenerateMapping_NoMatchingProperties_ByteExactEmptyBody()
    {
        var source = "public class SourceDto\n{\n    public string A { get; set; }\n}\n\npublic class DestEntity\n{\n    public string B { get; set; }\n}\n";

        var text = await GenerateAsync(source);

        Assert.That(text, Is.EqualTo("public static void MapSourceDtoToDestEntity(SourceDto source, DestEntity dest)\n{\n}"));
    }

    [Test]
    public async Task GenerateMapping_CrOnlyDocument_SnippetTakesTheDocumentsCrEnding()
    {
        // Accepted change of the C6-3 migration: the former new-child whitespace helper only accepted LF or CRLF, so the snippet of a
        // legacy CR-only document used to be LF. Every formatted line now takes the document's dominant ending.
        var text = await GenerateAsync(Join(SourceLines, "\r"));

        Assert.That(text, Is.EqualTo(Join(ExpectedLines, "\r")));
    }
}
