using System.Text.Json;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="ToolResultFormat.Xml"/> rendering of tool-result JSON: raw tags, no entity escaping in values. The rendering
/// is the wording under test, so substring asserts on leaf values are used. Fixtures are shaped like real results (same as
/// <see cref="PlainToolResultRendererTests"/>).
/// </summary>
[TestFixture]
public class XmlToolResultRendererTests
{
    private const string Source = "namespace N;\n\npublic class C\n{\n\tprivate string _s = \"a\\\"b\"; // back\\slash\n    public IReadOnlyCollection<string> L => a < b && c > d;\n}\n";

    private static readonly IToolResultRenderer Renderer = ToolResultRenderers.For(ToolResultFormat.Xml);

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
    public void Render_NoEntitiesInValues()
    {
        var rendered = Renderer.Render(ReadFileSuccess());

        Assert.That(rendered, Does.Contain("public IReadOnlyCollection<string> L => a < b && c > d;"));
        Assert.That(rendered, Does.Not.Contain("&lt;"));
        Assert.That(rendered, Does.Not.Contain("&amp;"));
        Assert.That(rendered, Does.Not.Contain("&quot;"));
        Assert.That(rendered, Does.Not.Contain("\\n"), "no JSON newline escape may survive");
        Assert.That(rendered, Does.Contain("\tprivate string _s = \"a\\\"b\"; // back\\slash"), "the decoded line is copied raw, backslash included");
    }

    [Test]
    public void Render_InvalidXmlNameKey_UsesEntryKey()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["a b"] = "x",
            ["c\"d&e"] = "y",
            ["1st"] = "z",
            ["ok.name-1"] = "w",
            [""] = "v",
        });

        var rendered = Renderer.Render(json);

        Assert.That(rendered, Is.EqualTo(
            "<entry key=\"a b\">x</entry>\n"
            + "<entry key=\"c&quot;d&amp;e\">y</entry>\n"
            + "<entry key=\"1st\">z</entry>\n"
            + "<ok.name-1>w</ok.name-1>\n"
            + "<entry key=\"\">v</entry>"));
    }

    [Test]
    public void Render_ValueContainingOwnClosingTag_FallsBackToJsonEncoding()
    {
        var json = JsonSerializer.Serialize(new { note = "a </note> b \"q\"" });

        var rendered = Renderer.Render(json);

        Assert.That(rendered, Is.EqualTo("<note encoding=\"json\">\"a <\\/note> b \\\"q\\\"\"</note>"));
    }

    [Test]
    public void Render_ValueContainingAnotherTag_StaysRaw()
    {
        var json = JsonSerializer.Serialize(new { note = "see </other> here" });

        Assert.That(Renderer.Render(json), Is.EqualTo("<note>see </other> here</note>"));
    }

    [Test]
    public void Render_ArrayItems_HaveIndexAttribute()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                Renderer.Render("{\"a\":[\"x\",{\"k\":1}]}"),
                Is.EqualTo("<a>\n  <item i=\"0\">x</item>\n  <item i=\"1\">\n    <k>1</k>\n  </item>\n</a>"));
            Assert.That(
                Renderer.Render("[\"x\",null]"),
                Is.EqualTo("<item i=\"0\">x</item>\n<item i=\"1\"/>"));
        });
    }

    [Test]
    public void Render_MultiLineValue_TagsOnOwnLines()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Renderer.Render("{\"s\":\"l1\\nl2\"}"), Is.EqualTo("<s>\nl1\nl2\n</s>"));
            Assert.That(Renderer.Render("{\"o\":{\"s\":\"l1\\nl2\"}}"), Is.EqualTo("<o>\n  <s>\nl1\nl2\n  </s>\n</o>"));
            Assert.That(Renderer.Render("{\"s\":\"one line\"}"), Is.EqualTo("<s>one line</s>"));
        });
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

    [Test]
    public void Render_KeyOrderPreserved_LargeResultFirst()
    {
        var rendered = Renderer.Render(LargeResultPage());

        Assert.That(rendered.IndexOf("<largeResult>", StringComparison.Ordinal), Is.EqualTo(0));
        Assert.That(rendered.IndexOf("<isError>", StringComparison.Ordinal), Is.GreaterThan(rendered.IndexOf("</largeResult>", StringComparison.Ordinal)));
    }

    [Test]
    public void Render_ScalarsAndEmptyContainers_WrittenLiterally()
    {
        Assert.That(
            Renderer.Render("{\"a\":null,\"b\":true,\"c\":1.50,\"d\":{},\"e\":[],\"f\":\"\"}"),
            Is.EqualTo("<a/>\n<b>true</b>\n<c>1.50</c>\n<d></d>\n<e></e>\n<f></f>"));
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
