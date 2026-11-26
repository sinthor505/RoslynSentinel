using System.Text.Json;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="ToolResultFormat.Plain"/> rendering of tool-result JSON. The rendering is the wording under test, so
/// substring asserts on leaf values are used. Fixtures are shaped like real results: a ReadFile success whose source
/// holds quotes, backslashes, angle brackets, ampersands, tabs and leading indentation; an error envelope with
/// <c>structuredDetail</c>; a <c>largeResult</c> page with <c>omitted[]</c>.
/// </summary>
[TestFixture]
public class PlainToolResultRendererTests
{
    private const string Source = "namespace N;\n\npublic class C\n{\n\tprivate string _s = \"a\\\"b\"; // back\\slash\n    public IReadOnlyCollection<string> L => a < b && c > d;\n}\n";

    private static readonly IToolResultRenderer Renderer = ToolResultRenderers.For(ToolResultFormat.Plain);

    private static string ReadFileSuccess() => JsonSerializer.Serialize(new
    {
        isError = false,
        successData = new
        {
            filePath = "RoslynSentinel.Common/C.cs",
            startLine = 1,
            totalLines = 7,
            source = Source,
            hasMoreData = false,
        },
        findings = Array.Empty<object>(),
    });

    private static string ErrorEnvelope() => JsonSerializer.Serialize(new
    {
        isError = true,
        errorData = new
        {
            errorCode = "ValidationFailed",
            message = "Snippet not found.\nTry again with the exact text.",
            structuredDetail = new object[]
            {
                new
                {
                    verdicts = Array.Empty<object>(),
                    diagnostics = new object[]
                    {
                        new { id = "CS0246", severity = "Error", message = "The type 'X' could not be found", startLine = 34, sourceLine = "var x = new X<int>();" },
                    },
                },
            },
        },
    });

    private static string LargeResultPage() => JsonSerializer.Serialize(new
    {
        largeResult = new { resultType = "Envelope", resultId = "abc123", sizeBytes = 14543, nextOffset = 280 },
        isError = false,
        successData = new
        {
            hits = new[]
            {
                new { file = "A.cs", text = "line1\nline2" },
                new { file = "B.cs", text = "single" },
            },
            omitted = new[] { new { path = "successData.hits", from = 2, total = 50 } },
        },
    });

    [TestCase("readFile")]
    [TestCase("error")]
    [TestCase("largeResult")]
    public void Render_EveryLeafValueAppearsVerbatim(string fixture)
    {
        var json = fixture switch
        {
            "readFile" => ReadFileSuccess(),
            "error" => ErrorEnvelope(),
            _ => LargeResultPage(),
        };

        var rendered = Renderer.Render(json);

        using var document = JsonDocument.Parse(json);
        AssertEveryLeafAppears(document.RootElement, rendered);
    }

    [Test]
    public void Render_MultiLineSource_IsBlockWithPathTerminator()
    {
        var rendered = Renderer.Render(ReadFileSuccess());

        Assert.That(rendered, Does.Contain("successData:\n  filePath: RoslynSentinel.Common/C.cs\n"));
        Assert.That(rendered, Does.Contain("  source:\nnamespace N;\n\npublic class C\n{\n"));
        Assert.That(rendered, Does.Contain("}\n\n(end of successData.source)\n  hasMoreData: false"));
        Assert.That(rendered, Does.Not.Contain("\\n"), "no JSON newline escape may survive");
        Assert.That(rendered, Does.Contain("\tprivate string _s = \"a\\\"b\"; // back\\slash"), "the decoded line is copied raw, backslash included");
    }

    [Test]
    public void Render_ArrayItemBlock_UsesIndexedPathTerminator()
    {
        var rendered = Renderer.Render(LargeResultPage());

        Assert.That(
            rendered,
            Does.Contain("    - [0]\n      file: A.cs\n      text:\nline1\nline2\n(end of successData.hits[0].text)\n    - [1]\n      file: B.cs\n      text: single"));
    }

    [Test]
    public void Render_StringContainingTerminator_FallsBackToJsonQuoted()
    {
        const string json = "{\"successData\":{\"source\":\"a\\n(end of successData.source)\\nb\"}}";

        var rendered = Renderer.Render(json);

        Assert.That(rendered, Is.EqualTo("successData:\n  source: \"a\\n(end of successData.source)\\nb\""));
    }

    [Test]
    public void Render_ArrayItems_ShowIndex()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Renderer.Render("{\"a\":[\"x\",\"y\"]}"), Is.EqualTo("a:\n  - [0] x\n  - [1] y"));
            Assert.That(
                Renderer.Render("{\"a\":[{\"k\":1},[true,null]]}"),
                Is.EqualTo("a:\n  - [0]\n    k: 1\n  - [1]\n    - [0] true\n    - [1] null"));
            Assert.That(Renderer.Render("[\"x\",{\"k\":2}]"), Is.EqualTo("- [0] x\n- [1]\n  k: 2"));
        });
    }

    [Test]
    public void Render_KeyOrderPreserved_LargeResultFirst()
    {
        var rendered = Renderer.Render(LargeResultPage());

        var largeResult = rendered.IndexOf("largeResult:", StringComparison.Ordinal);
        var isError = rendered.IndexOf("isError:", StringComparison.Ordinal);
        var successData = rendered.IndexOf("successData:", StringComparison.Ordinal);
        Assert.That(largeResult, Is.EqualTo(0));
        Assert.That(isError, Is.GreaterThan(largeResult));
        Assert.That(successData, Is.GreaterThan(isError));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("Error: the call was rejected.")]
    [TestCase("{not json")]
    [TestCase("42")]
    [TestCase("\"just a string\"")]
    [TestCase("{\"a\":1} trailing")]
    [TestCase("{}")]
    [TestCase("[]")]
    public void Render_NonJsonBody_ReturnedUnchanged(string body)
    {
        Assert.That(Renderer.Render(body), Is.EqualTo(body));
    }

    [TestCase("  indented", "s:\n  indented\n(end of s)")]
    [TestCase("trailing  ", "s:\ntrailing  \n(end of s)")]
    [TestCase("", "s:\n\n(end of s)")]
    [TestCase("line1\r\nline2", "s:\nline1\r\nline2\n(end of s)")]
    public void Render_LeadingWhitespaceString_IsBlock(string value, string expected)
    {
        var json = JsonSerializer.Serialize(new { s = value });

        Assert.That(Renderer.Render(json), Is.EqualTo(expected));
    }

    [Test]
    public void Render_PlainSingleLineString_IsInline()
    {
        Assert.That(Renderer.Render("{\"s\":\"a \\\"quoted\\\" word\"}"), Is.EqualTo("s: a \"quoted\" word"));
    }

    [Test]
    public void Render_ScalarsAndEmptyContainers_WrittenLiterally()
    {
        Assert.That(
            Renderer.Render("{\"a\":null,\"b\":true,\"c\":1.50,\"d\":{},\"e\":[]}"),
            Is.EqualTo("a: null\nb: true\nc: 1.50\nd: {}\ne: []"));
    }

    private static void AssertEveryLeafAppears(JsonElement element, string rendered)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Assert.That(rendered, Does.Contain(property.Name), $"key '{property.Name}'");
                    AssertEveryLeafAppears(property.Value, rendered);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertEveryLeafAppears(item, rendered);
                }

                break;
            case JsonValueKind.String:
                foreach (var line in element.GetString()!.Split('\n'))
                {
                    if (line.Length > 0)
                    {
                        Assert.That(rendered, Does.Contain(line), $"string line '{line}'");
                    }
                }

                break;
            case JsonValueKind.Number:
                Assert.That(rendered, Does.Contain(element.GetRawText()), "number");
                break;
        }
    }
}
