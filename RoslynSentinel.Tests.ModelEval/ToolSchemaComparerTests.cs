using System.Text.Json;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Classification of recorded versus live tool schemas (<see cref="ToolSchemaComparer"/>) and the metadata that
/// <see cref="ResumeSource"/> reads back from a transcript. No live model.
/// </summary>
[TestFixture]
public class ToolSchemaComparerTests
{
    private const string Commit = "abc123";

    [Test]
    public void Identical_ReturnsNone()
    {
        var result = CompareOne(
            """{"type":"object","properties":{"a":{"type":"string","description":"A"}}}""",
            """{"type":"object","properties":{"a":{"type":"string","description":"A"}}}""");

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.None));
        Assert.That(result.Detail, Is.EqualTo(""));
    }

    [Test]
    public void PropertyDescriptionChanged_ReturnsDescriptionOnly()
    {
        var result = CompareOne(
            """{"type":"object","properties":{"a":{"type":"string","description":"old text"}}}""",
            """{"type":"object","properties":{"a":{"type":"string","description":"new text"}}}""");

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.DescriptionOnly));
        Assert.That(result.Detail, Does.Contain("T"));
        Assert.That(result.Detail, Does.Contain("parameter-level"));
    }

    [Test]
    public void ToolLevelDescriptionChanged_ReturnsDescriptionOnly()
    {
        var result = CompareOne(
            """{"type":"object"}""",
            """{"type":"object"}""",
            recordedText: "old tool text",
            liveText: "new tool text");

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.DescriptionOnly));
        Assert.That(result.Detail, Does.Contain("tool-level"));
    }

    [Test]
    public void PropertyTypeChanged_ReturnsStructuralNamingToolAndCommit()
    {
        var result = CompareOne(
            """{"type":"object","properties":{"a":{"type":"string"}}}""",
            """{"type":"object","properties":{"a":{"type":"integer"}}}""");

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.Structural));
        Assert.That(result.Detail, Does.Contain("T"));
        Assert.That(result.Detail, Does.Contain(Commit));
    }

    [Test]
    public void NewRequiredParameter_ReturnsStructural()
    {
        var result = CompareOne(
            """{"type":"object","properties":{"a":{"type":"string"}}}""",
            """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}},"required":["b"]}""");

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.Structural));
    }

    [Test]
    public void EnumValueAdded_ReturnsStructural()
    {
        var result = CompareOne(
            """{"type":"object","properties":{"m":{"type":"string","enum":["a","b"]}}}""",
            """{"type":"object","properties":{"m":{"type":"string","enum":["a","b","c"]}}}""");

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.Structural));
    }

    [Test]
    public void RequiredArrayReordered_ReturnsNone()
    {
        var result = CompareOne(
            """{"type":"object","required":["a","b"]}""",
            """{"type":"object","required":["b","a"]}""");

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.None));
    }

    [Test]
    public void RecordedSchemasNull_ReturnsMissing()
    {
        var result = ToolSchemaComparer.Compare(
            new[] { "T" },
            null,
            null,
            LiveOne("T", """{"type":"object"}"""),
            new Dictionary<string, string>(),
            Commit);

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.Missing));
        Assert.That(result.Detail, Does.Contain(Commit));
    }

    [Test]
    public void CalledToolMissingFromRecorded_ReturnsMissing()
    {
        var result = ToolSchemaComparer.Compare(
            new[] { "T" },
            new Dictionary<string, JsonElement>(),
            null,
            LiveOne("T", """{"type":"object"}"""),
            new Dictionary<string, string>(),
            Commit);

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.Missing));
        Assert.That(result.Detail, Does.Contain("T"));
    }

    [Test]
    public void ToolMissingFromLive_ReturnsStructural()
    {
        var result = ToolSchemaComparer.Compare(
            new[] { "T" },
            RecordedOne("T", """{"type":"object"}"""),
            null,
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, string>(),
            Commit);

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.Structural));
        Assert.That(result.Detail, Does.Contain("no longer offered"));
    }

    [Test]
    public void OneDescriptionOnlyAndOneStructural_ReturnsStructural()
    {
        var recorded = new Dictionary<string, JsonElement>
        {
            ["A"] = Schema("""{"type":"object"}"""),
            ["B"] = Schema("""{"type":"object","properties":{"x":{"type":"string"}}}"""),
        };
        var live = new Dictionary<string, JsonElement>
        {
            ["A"] = Schema("""{"type":"object"}"""),
            ["B"] = Schema("""{"type":"object","properties":{"x":{"type":"integer"}}}"""),
        };
        var liveDescriptions = new Dictionary<string, string> { ["A"] = "new A text", ["B"] = "d" };
        var recordedDescriptions = new Dictionary<string, string> { ["A"] = "old A text", ["B"] = "d" };

        var result = ToolSchemaComparer.Compare(
            new[] { "A", "B" },
            recorded,
            recordedDescriptions,
            live,
            liveDescriptions,
            Commit);

        Assert.That(result.Kind, Is.EqualTo(SchemaDifferenceKind.Structural));
        Assert.That(result.Detail, Does.Contain("B"));
    }

    [Test]
    public void ResumeSource_ReadsBackSchemasDescriptionsAndCommit()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                path,
                """{"SystemPrompt":"s","UserPrompt":"u","CommitHash":"abc","ToolDescriptions":{"T":"d"},"ToolSchemas":{"T":{"type":"object"}},"Turns":[]}""");

            ResumeSource source = TranscriptResume.Load(path);

            Assert.That(source.CommitHash, Is.EqualTo("abc"));
            Assert.That(source.ToolDescriptions, Is.Not.Null);
            Assert.That(source.ToolDescriptions!["T"], Is.EqualTo("d"));
            Assert.That(source.ToolSchemas, Is.Not.Null);
            Assert.That(source.ToolSchemas!["T"].GetProperty("type").GetString(), Is.EqualTo("object"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ResumeSource_WithoutToolMetadata_HasNulls()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"SystemPrompt":"s","UserPrompt":"u","Turns":[]}""");

            ResumeSource source = TranscriptResume.Load(path);

            Assert.That(source.CommitHash, Is.Null);
            Assert.That(source.ToolDescriptions, Is.Null);
            Assert.That(source.ToolSchemas, Is.Null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> RecordedOne(string tool, string json) =>
        new() { [tool] = Schema(json) };

    private static Dictionary<string, JsonElement> LiveOne(string tool, string json) =>
        new() { [tool] = Schema(json) };

    private static (SchemaDifferenceKind Kind, string Detail) CompareOne(
        string recordedJson,
        string liveJson,
        string recordedText = "d",
        string liveText = "d")
    {
        return ToolSchemaComparer.Compare(
            new[] { "T" },
            RecordedOne("T", recordedJson),
            new Dictionary<string, string> { ["T"] = recordedText },
            LiveOne("T", liveJson),
            new Dictionary<string, string> { ["T"] = liveText },
            Commit);
    }
}
