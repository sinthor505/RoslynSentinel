using System.Security.Cryptography;
using System.Text;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.Basic;

public class ErrorPolicyDocumentTests
{
    private const string FourSections =
        "### FileNotFound.Lookup\n" +
        "Lookup body.\n" +
        "\n" +
        "### FileNotFound.Edit\n" +
        "Edit body.\n" +
        "\n" +
        "### SymbolNotFound.Lookup\n" +
        "Symbol lookup body.\n" +
        "\n" +
        "### SymbolNotFound.Edit\n" +
        "Symbol edit body.\n";

    [Test]
    public void Parse_FourSections_EntriesHoldEachBodyAndNothingIsMissing()
    {
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse(FourSections);

        Assert.That(doc.Entries.Count, Is.EqualTo(4));
        Assert.That(doc.Entries[ErrorPolicyKey.FileNotFoundLookup], Is.EqualTo("Lookup body."));
        Assert.That(doc.Entries[ErrorPolicyKey.FileNotFoundEdit], Is.EqualTo("Edit body."));
        Assert.That(doc.Entries[ErrorPolicyKey.SymbolNotFoundLookup], Is.EqualTo("Symbol lookup body."));
        Assert.That(doc.Entries[ErrorPolicyKey.SymbolNotFoundEdit], Is.EqualTo("Symbol edit body."));
        Assert.That(doc.MissingKeys, Is.Empty);
    }

    [Test]
    public void Parse_PreambleBeforeFirstHeading_IsIgnored()
    {
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse(
            "# Error policy\n\nSome intro text that is not guidance.\n\n### FileNotFound.Lookup\nUse the tool.\n");

        Assert.That(doc.Entries.Count, Is.EqualTo(1));
        Assert.That(doc.Entries[ErrorPolicyKey.FileNotFoundLookup], Is.EqualTo("Use the tool."));
    }

    [Test]
    public void Parse_MarkdownInsideBody_IsPreserved()
    {
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse(
            "### SymbolNotFound.Edit\n" +
            "Intro line.\n" +
            "\n" +
            "## Detail\n" +
            "\n" +
            "- one\n" +
            "- two\n");

        Assert.That(doc.Entries[ErrorPolicyKey.SymbolNotFoundEdit], Is.EqualTo("Intro line.\n\n## Detail\n\n- one\n- two"));
    }

    [Test]
    public void Parse_UnknownHeading_ThrowsFormatExceptionNamingHeadingAndLine()
    {
        string markdown =
            "### FileNotFound.Lookup\n" +
            "A\n" +
            "### FileNotFound.Delete\n" +
            "B\n";

        var ex = Assert.Throws<FormatException>(() => ErrorPolicyDocument.Parse(markdown));

        Assert.That(ex!.Message, Does.Contain("FileNotFound.Delete"));
        Assert.That(ex.Message, Does.Contain("line 3"));
        Assert.That(ex.Message, Does.Contain("FileNotFound.Lookup"));
    }

    [Test]
    public void Parse_DuplicateHeading_ThrowsFormatException()
    {
        string markdown =
            "### FileNotFound.Lookup\n" +
            "A\n" +
            "\n" +
            "### FileNotFound.Lookup\n" +
            "B\n";

        var ex = Assert.Throws<FormatException>(() => ErrorPolicyDocument.Parse(markdown));

        Assert.That(ex!.Message, Does.Contain("FileNotFound.Lookup"));
        Assert.That(ex.Message, Does.Contain("line 4"));
    }

    [Test]
    public void Parse_EmptyBody_ThrowsFormatExceptionNamingHeading()
    {
        string markdown =
            "### FileNotFound.Lookup\n" +
            "\n" +
            "### FileNotFound.Edit\n" +
            "B\n";

        var ex = Assert.Throws<FormatException>(() => ErrorPolicyDocument.Parse(markdown));

        Assert.That(ex!.Message, Does.Contain("FileNotFound.Lookup"));
        Assert.That(ex.Message, Does.Contain("line 1"));
    }

    [Test]
    public void Parse_MissingKey_IsAllowedAndListedInMissingKeys()
    {
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse(
            "### FileNotFound.Lookup\nA\n\n### FileNotFound.Edit\nB\n\n### SymbolNotFound.Lookup\nC\n");

        Assert.That(doc.Entries.Count, Is.EqualTo(3));
        Assert.That(doc.MissingKeys, Is.EqualTo(new[] { ErrorPolicyKey.SymbolNotFoundEdit }));
    }

    [Test]
    public void ContentHash_IsTwelveLowercaseHexCharacters()
    {
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse(FourSections);

        Assert.That(doc.ContentHash, Does.Match("^[0-9a-f]{12}$"));
        Assert.That(ErrorPolicyDocument.ShortHashLength, Is.EqualTo(12));
    }

    [Test]
    public void ContentHash_IsSha256OfExactBytes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(FourSections);
        string expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..12];

        Assert.That(ErrorPolicyDocument.ParseBytes(bytes).ContentHash, Is.EqualTo(expected));
        Assert.That(ErrorPolicyDocument.Parse(FourSections).ContentHash, Is.EqualTo(expected));
    }

    [Test]
    public void ContentHash_IdenticalBytes_Match()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(FourSections);

        Assert.That(ErrorPolicyDocument.ParseBytes(bytes).ContentHash,
            Is.EqualTo(ErrorPolicyDocument.ParseBytes((byte[])bytes.Clone()).ContentHash));
    }

    [Test]
    public void ContentHash_OneByteChange_Differs()
    {
        byte[] original = Encoding.UTF8.GetBytes(FourSections);
        byte[] changed = Encoding.UTF8.GetBytes(FourSections.Replace("Lookup body.", "Lookup body!"));

        Assert.That(ErrorPolicyDocument.ParseBytes(changed).ContentHash,
            Is.Not.EqualTo(ErrorPolicyDocument.ParseBytes(original).ContentHash));
    }

    [Test]
    public void ParseBytes_WithBom_StripsBomForParsingAndHashesOriginalBytes()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(FourSections);
        byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8).ToArray();

        ErrorPolicyDocument bomDoc = ErrorPolicyDocument.ParseBytes(withBom);
        ErrorPolicyDocument plainDoc = ErrorPolicyDocument.ParseBytes(utf8);

        // Without stripping, the BOM would glue onto the first heading and it would be ignored as preamble.
        Assert.That(bomDoc.Entries.Count, Is.EqualTo(4));
        Assert.That(bomDoc.Entries[ErrorPolicyKey.FileNotFoundLookup], Is.EqualTo("Lookup body."));
        Assert.That(bomDoc.ContentHash, Is.Not.EqualTo(plainDoc.ContentHash));
    }

    [Test]
    public void Load_TempFile_MatchesParseBytesOfSameBytes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(FourSections);
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);

            ErrorPolicyDocument loaded = ErrorPolicyDocument.Load(path);
            ErrorPolicyDocument parsed = ErrorPolicyDocument.ParseBytes(bytes);

            Assert.That(loaded.ContentHash, Is.EqualTo(parsed.ContentHash));
            Assert.That(loaded.Entries, Is.EqualTo(parsed.Entries));
            Assert.That(loaded.MissingKeys, Is.EqualTo(parsed.MissingKeys));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestCase(ErrorPolicyKey.FileNotFoundLookup, "Error handling: FileNotFound.Lookup")]
    [TestCase(ErrorPolicyKey.FileNotFoundEdit, "Error handling: FileNotFound.Edit")]
    [TestCase(ErrorPolicyKey.SymbolNotFoundLookup, "Error handling: SymbolNotFound.Lookup")]
    [TestCase(ErrorPolicyKey.SymbolNotFoundEdit, "Error handling: SymbolNotFound.Edit")]
    public void RenderedHeading_MatchesLiteralHeading(ErrorPolicyKey key, string expected)
    {
        Assert.That(ErrorPolicyDocument.RenderedHeading(key), Is.EqualTo(expected));
    }

    [Test]
    public void WithErrorHandling_AppendsBlockWithHeadingsInAllOrderAndKeepsPromptPrefix()
    {
        const string prompt = "You are a coding agent.\n";
        // Sections deliberately written in reverse so the output order must come from ErrorPolicyKeys.All.
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse(
            "### SymbolNotFound.Edit\nSymbol edit body.\n\n" +
            "### SymbolNotFound.Lookup\nSymbol lookup body.\n\n" +
            "### FileNotFound.Edit\nEdit body.\n\n" +
            "### FileNotFound.Lookup\nLookup body.\n");

        string result = AgentSystemPrompts.WithErrorHandling(prompt, doc);

        Assert.That(result, Does.StartWith("You are a coding agent."));
        Assert.That(result, Does.Contain("\n\n## Error handling\n\n"));
        var headings = result.Split('\n').Where(line => line.StartsWith("### ", StringComparison.Ordinal)).ToList();
        Assert.That(headings, Is.EqualTo(ErrorPolicyKeys.All.Select(key => "### " + ErrorPolicyDocument.RenderedHeading(key)).ToList()));
        Assert.That(result, Does.EndWith("Symbol edit body.\n"));
    }

    [Test]
    public void WithErrorHandling_EmptyDocument_ReturnsPromptUnchanged()
    {
        const string prompt = "You are a coding agent.\n";
        ErrorPolicyDocument doc = ErrorPolicyDocument.Parse("# Only a preamble, no sections.\n");

        Assert.That(AgentSystemPrompts.WithErrorHandling(prompt, doc), Is.EqualTo(prompt));
    }
}
