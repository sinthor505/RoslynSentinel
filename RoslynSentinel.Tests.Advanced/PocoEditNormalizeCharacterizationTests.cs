using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Slice C8 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for the existing-root sites of <c>DocumentationEngine</c> (GenerateXmlDocumentationStubs,
/// DocumentPocoFields) and <c>ApiGenerationEngine.AddValidationToPoco</c>. Each input has untouched, irregularly spaced
/// code (<c>UntouchedLine</c>). A line with a trailing "~" ends in a stray CRLF in the "Mixed" (otherwise LF) variant.
/// Since the C8 migration every existing line is byte-exact (spacing and terminator) and only the inserted lines (doc
/// stubs, attribute lists, using directive) are new; they take the document's dominant line ending (LF for "Mixed").
/// </summary>
[TestFixture]
[Category("DocumentationEngine")]
public class PocoEditNormalizeCharacterizationTests
{
    private const string UntouchedLine = "    private void  Untouched( )  { }~";

    private PersistentWorkspaceManager _workspaceManager;

    [SetUp]
    public void Setup() => _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string content)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", content)]);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>
    /// Joins lines without a trailing newline: LF, CRLF, or "Mixed" (LF, except that a line written with a trailing "~"
    /// ends in CRLF). The "~" marker is stripped in every variant.
    /// </summary>
    private static string Build(string eol, params string[] lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var odd = line.EndsWith('~');
            sb.Append(odd ? line[..^1] : line);
            if (i == lines.Length - 1)
            {
                break;
            }

            var crlf = eol == "CRLF" || (eol == "Mixed" && odd);
            sb.Append(crlf ? "\r\n" : "\n");
        }

        return sb.ToString();
    }

    /// <summary>Writes the actual text with visible line endings (NUnit clips long diffs), then compares byte for byte.</summary>
    private static void AssertPinned(string? actual, string expected)
    {
        TestContext.Out.WriteLine((actual ?? "<null>").Replace("\r", "\\r").Replace("\n", "\\n\n"));
        Assert.That(actual, Is.EqualTo(expected).NoClip);
    }

    private static string[] PersonInput() =>
    [
        "using System;",
        "",
        "namespace Demo;",
        "",
        "public class Person",
        "{",
        "    // the name~",
        "    public string Name { get; set; }",
        "",
        "    [Required]",
        "    public string  Email { get; set; }~",
        "    [Obsolete]",
        "    public int  Age { get; set; }",
        UntouchedLine,
        "}",
    ];

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task GenerateXmlDocumentationStubs_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol,
            "using System;",
            "",
            "namespace Demo;",
            "",
            "public class Svc",
            "{",
            "    // existing comment~",
            "    public int Add(int a, int  b) => a + b;",
            "",
            "    /// <summary>Documented.</summary>",
            "    public void Done() { }",
            "",
            "    public void Run()~",
            "    {",
            "        var  x = 1;~",
            "    }",
            UntouchedLine,
            "}"));

        var result = await new DocumentationEngine(_workspaceManager).GenerateXmlDocumentationStubsAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System;",
            "",
            "namespace Demo;",
            "",
            "public class Svc",
            "{",
            "    // existing comment~",
            "    /// <summary>",
            "    /// TODO: Add description for Add.",
            "    /// </summary>",
            "    /// <param name=\"a\">TODO: Describe a.</param>",
            "    /// <param name=\"b\">TODO: Describe b.</param>",
            "    /// <returns>TODO: Describe return value.</returns>",
            "    public int Add(int a, int  b) => a + b;",
            "",
            "    /// <summary>Documented.</summary>",
            "    public void Done() { }",
            "",
            "    /// <summary>",
            "    /// TODO: Add description for Run.",
            "    /// </summary>",
            "    public void Run()~",
            "    {",
            "        var  x = 1;~",
            "    }",
            UntouchedLine,
            "}"));
    }

    [Test]
    public async Task GenerateXmlDocumentationStubs_MethodNotStartingItsLine_StubGoesOnNewLines()
    {
        // The method shares its line with the class brace: the stub starts on a new line at that line's indentation.
        // The space after "{" is that brace's own trailing trivia, which the per-method rewrite does not touch.
        SetSource("namespace Demo;\r\n\r\n    public class Svc { public void Run() { } }\r\n");

        var result = await new DocumentationEngine(_workspaceManager).GenerateXmlDocumentationStubsAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText,
            "namespace Demo;\r\n\r\n    public class Svc { \r\n    /// <summary>\r\n    /// TODO: Add description for Run.\r\n    /// </summary>\r\n    public void Run() { } }\r\n");
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task DocumentPocoFields_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol, PersonInput()));

        var result = await new DocumentationEngine(_workspaceManager).DocumentPocoFieldsAsync("Test.cs", "Person");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System;",
            "using System.ComponentModel;",
            "",
            "namespace Demo;",
            "",
            "public class Person",
            "{",
            "    // the name~",
            "    [Description(\"Gets or sets the Name.\")]",
            "    public string Name { get; set; }",
            "",
            "    [Required]",
            "    [Description(\"Gets or sets the Email.\")]",
            "    public string  Email { get; set; }~",
            "    [Obsolete]",
            "    [Description(\"Gets or sets the Age.\")]",
            "    public int  Age { get; set; }",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task AddValidationToPoco_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol, PersonInput()));

        var result = await new ApiGenerationEngine(_workspaceManager).AddValidationToPocoAsync("Test.cs", "Person");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System;",
            "using System.ComponentModel.DataAnnotations;",
            "",
            "namespace Demo;",
            "",
            "public class Person",
            "{",
            "    // the name~",
            "    [Required]",
            "    [StringLength(256)]",
            "    public string Name { get; set; }",
            "",
            "    [Required]",
            "    [StringLength(256)]",
            "    public string  Email { get; set; }~",
            "    [Obsolete]",
            "    [Range(0, 2147483647)]",
            "    public int  Age { get; set; }",
            UntouchedLine,
            "}"));
    }

    [Test]
    public async Task DocumentPocoFields_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await new DocumentationEngine(_workspaceManager).DocumentPocoFieldsAsync("Missing.cs", "Person"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}
