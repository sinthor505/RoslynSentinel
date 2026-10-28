using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// C6-4 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins (LF, CRLF and Mixed output) for the two <c>MsToolAugmentEngine</c> methods that now end in
/// the EOL-aware <c>RoslynFormattingHelper</c> helpers (formerly a whole-subtree whitespace pass): <c>ExtractConstantSafeAsync</c> and
/// <c>GenerateToStringSafeAsync</c>. Each input has an untouched, badly spaced member (<c>UntouchedLine</c>) that the
/// rewrite does not change. "Mixed" is LF except the untouched line, which ends in a stray CRLF. The pins encode the
/// migrated contract (annotated-span formatting): the untouched line keeps its text, changed lines take the document's
/// dominant line ending, and a stray CRLF on an untouched line survives. The pre-migration whole-subtree pass re-spaced
/// the untouched member (<c>public void Untouched()</c> split over lines) and emitted LF for every input.
/// </summary>
[TestFixture]
[Category("MsToolAugmentEngine")] // sentinel:auto-category
public class MsToolAugmentNormalizeCharacterizationTests
{
    private const string UntouchedLine = "    public void  Untouched( )  { }";

    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;
    private string _tempDir;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Basic.TestEngines.MsToolAugmentEngine(_workspaceManager);
        _tempDir = Path.Combine(Path.GetTempPath(), "MsAugmentNormalize_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private void SetSource(string source, string fileName = "Entity.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private string WriteDiskFile(string source, string fileName = "Entity.cs")
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(source));
        return path;
    }

    private static string Build(string eol, string[] lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i == lines.Length - 1)
            {
                break;
            }

            var crlf = eol == "CRLF" || (eol == "Mixed" && lines[i] == UntouchedLine);
            sb.Append(crlf ? "\r\n" : "\n");
        }

        return sb.ToString();
    }

    private static string[] ConstantInput() =>
    [
        "public class Entity",
        "{",
        "    public string Name()",
        "    {",
        "        return \"hello\";",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] ConstantExpected() =>
    [
        "public class Entity",
        "{",
        "    private const string Greeting = \"hello\";",
        "    public string Name()",
        "    {",
        "        return Greeting;",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] ToStringInput() =>
    [
        "public class Entity",
        "{",
        "    public int Id { get; set; }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] ToStringExpected() =>
    [
        "public class Entity",
        "{",
        "    public int Id { get; set; }",
        "",
        UntouchedLine,
        "",
        "    public override string ToString() => $\"Entity {{ Id = {Id} }}\";",
        "}",
    ];

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ExtractConstantSafe_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        var path = WriteDiskFile(Build(eol, ConstantInput()));

        var result = await _engine.ExtractConstantSafeAsync(path, "\"hello\"", "Greeting");

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.UpdatedContent, Is.EqualTo(Build(eol, ConstantExpected())));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task GenerateToStringSafe_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol, ToStringInput()));

        var result = await _engine.GenerateToStringSafeAsync("Entity.cs", "Entity");

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.UpdatedContent, Is.EqualTo(Build(eol, ToStringExpected())));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    public async Task GenerateToStringSafe_MultipleMembers_StringContentHasNoExtraSpaces(string eol)
    {
        // Added with the migration: the formatter puts a space between two ADJACENT interpolated text nodes, which
        // would have changed the string's content (e.g. "{{  Id"); the engine now emits one coalesced text node.
        var lines = new[]
        {
            "public class Entity",
            "{",
            "    public int Id { get; set; }",
            "    public string Name { get; set; }",
            "}",
        };
        SetSource(Build(eol, lines));

        var result = await _engine.GenerateToStringSafeAsync("Entity.cs", "Entity");

        Assert.That(result.IsError, Is.False, result.Error);
        var expectedLines = new[]
        {
            "public class Entity",
            "{",
            "    public int Id { get; set; }",
            "    public string Name { get; set; }",
            "",
            "    public override string ToString() => $\"Entity {{ Id = {Id}, Name = {Name} }}\";",
            "}",
        };
        Assert.That(result.UpdatedContent, Is.EqualTo(Build(eol, expectedLines)));
    }
}
