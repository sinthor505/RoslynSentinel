using System.Text.RegularExpressions;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

/// <summary>
/// Adherence scenario: implement a collection type exactly from a written spec (signatures given in the prompt) and write the
/// tests for it. Hidden tests compile against the spec'd signatures; the check also wants a model test file with at least six
/// tests and no comparer passed to the base class.
/// </summary>
internal static class ChangeSetTypeFromSpecScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/ChangeSetTypeFromSpec/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/ChangeSetTypeFromSpec/";
    private const string TypePath = CoreFolder + "DocumentChangeSet.cs";
    private const string ModelTestPath = TestFolder + "DocumentChangeSetTests.cs";

    private const string FilePathSource = """
namespace ContosoOrders.Evals.ChangeSetTypeFromSpec;

/// <summary>A file path compared case-insensitively.</summary>
public readonly record struct FilePath(string Value)
{
    public bool Equals(FilePath other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);

    public override string ToString() => Value;

    public static implicit operator FilePath(string value) => new(value);
}
""";

    private const string JsonDefaultsSource = """
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContosoOrders.Evals.ChangeSetTypeFromSpec;

/// <summary>Serializer options shared by the change-set types; FilePath is written as a plain string, also as a dictionary key.</summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new FilePathConverter());
        return options;
    }

    private sealed class FilePathConverter : JsonConverter<FilePath>
    {
        public override FilePath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString() ?? string.Empty);

        public override void Write(Utf8JsonWriter writer, FilePath value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);

        public override FilePath ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString() ?? string.Empty);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, FilePath value, JsonSerializerOptions options)
            => writer.WritePropertyName(value.Value);
    }
}
""";

    private const string SeedTestsSource = """
using System.Text.Json;
using ContosoOrders.Evals.ChangeSetTypeFromSpec;
using Xunit;

namespace ContosoOrders.Tests.Evals.ChangeSetTypeFromSpec;

public class FilePathTests
{
    [Fact]
    public void EqualityIgnoresCase()
    {
        Assert.Equal(new FilePath("A.cs"), new FilePath("a.CS"));
        Assert.Equal(new FilePath("A.cs").GetHashCode(), new FilePath("a.CS").GetHashCode());
    }

    [Fact]
    public void PlainDictionaryRoundTripsThroughJson()
    {
        var map = new Dictionary<FilePath, string> { ["a.cs"] = "x" };
        var json = JsonSerializer.Serialize(map, JsonDefaults.Options);
        Assert.Equal("{\"a.cs\":\"x\"}", json);
        var back = JsonSerializer.Deserialize<Dictionary<FilePath, string>>(json, JsonDefaults.Options);
        Assert.Equal("x", back!["A.cs"]);
    }
}
""";

    private const string GoldenTypeSource = """
namespace ContosoOrders.Evals.ChangeSetTypeFromSpec;

/// <summary>A change set: file path to new file content. Keys compare through FilePath's own equality.</summary>
public sealed class DocumentChangeSet : Dictionary<FilePath, string>
{
    public DocumentChangeSet()
    {
    }

    public DocumentChangeSet(IDictionary<FilePath, string> source)
        : base(source)
    {
    }

    public DocumentChangeSet(IEnumerable<KeyValuePair<FilePath, string>> pairs)
        : base(pairs)
    {
    }

    public IReadOnlyCollection<FilePath> Paths => Keys;

    public bool IsEmpty => Count == 0;

    public DocumentChangeSet Merge(DocumentChangeSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var merged = new DocumentChangeSet(this);
        foreach (var (path, content) in other)
        {
            merged[path] = content;
        }

        return merged;
    }
}
""";

    private const string GoldenTestsSource = """
using System.Text.Json;
using ContosoOrders.Evals.ChangeSetTypeFromSpec;
using Xunit;

namespace ContosoOrders.Tests.Evals.ChangeSetTypeFromSpec;

public class DocumentChangeSetTests
{
    private static Dictionary<FilePath, string> Sample() => new() { ["a.cs"] = "1", ["b.cs"] = "2" };

    [Fact]
    public void EmptyConstructorGivesAnEmptySet()
    {
        var set = new DocumentChangeSet();
        Assert.True(set.IsEmpty);
        Assert.Empty(set.Paths);
    }

    [Fact]
    public void CopyConstructorCopiesTheEntries()
    {
        var source = Sample();
        var set = new DocumentChangeSet(source);
        source["c.cs"] = "3";
        Assert.Equal(2, set.Count);
        Assert.False(set.IsEmpty);
    }

    [Fact]
    public void EnumerableConstructorCopiesTheEntries()
    {
        var pairs = new List<KeyValuePair<FilePath, string>> { new("x.cs", "1"), new("y.cs", "2") };
        var set = new DocumentChangeSet(pairs);
        Assert.Equal(2, set.Count);
        Assert.Equal("2", set["Y.CS"]);
    }

    [Fact]
    public void PathsListsTheKeys()
    {
        var set = new DocumentChangeSet(Sample());
        Assert.Equal(2, set.Paths.Count);
        Assert.Contains(new FilePath("a.cs"), set.Paths);
    }

    [Fact]
    public void MergeLetsTheLaterSetWin()
    {
        var first = new DocumentChangeSet(Sample());
        var second = new DocumentChangeSet(new Dictionary<FilePath, string> { ["b.cs"] = "second", ["c.cs"] = "3" });
        var merged = first.Merge(second);
        Assert.Equal(3, merged.Count);
        Assert.Equal("second", merged["b.cs"]);
        Assert.Equal("2", first["b.cs"]);
    }

    [Fact]
    public void MergeRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new DocumentChangeSet().Merge(null!));
    }

    [Fact]
    public void KeysThatDifferOnlyByCaseAreTheSameKey()
    {
        var set = new DocumentChangeSet();
        set["A.cs"] = "1";
        set["a.CS"] = "2";
        Assert.Single(set);
        Assert.Equal("2", set["a.cs"]);
    }

    [Fact]
    public void JsonMatchesAPlainDictionary()
    {
        var plain = Sample();
        var set = new DocumentChangeSet(plain);
        Assert.Equal(JsonSerializer.Serialize(plain, JsonDefaults.Options), JsonSerializer.Serialize(set, JsonDefaults.Options));
    }
}
""";

    private const string HiddenTestSource = """
using System.Text.Json;
using ContosoOrders.Evals.ChangeSetTypeFromSpec;
using Xunit;

namespace ContosoOrders.Tests.Evals.ChangeSetTypeFromSpec;

public class ChangeSetTypeFromSpecStep1Tests
{
    [Fact]
    public void TypeShape()
    {
        var type = typeof(DocumentChangeSet);
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.Equal(typeof(Dictionary<FilePath, string>), type.BaseType);
    }

    [Fact]
    public void EmptyConstructor()
    {
        var set = new DocumentChangeSet();
        Assert.True(set.IsEmpty);
        Assert.Empty(set.Paths);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void CopyConstructorCopies()
    {
        var source = new Dictionary<FilePath, string> { ["a.cs"] = "1", ["b.cs"] = "2" };
        var set = new DocumentChangeSet(source);
        source["c.cs"] = "3";
        Assert.Equal(2, set.Count);
        Assert.False(set.IsEmpty);
    }

    [Fact]
    public void EnumerableConstructorCopies()
    {
        var pairs = new List<KeyValuePair<FilePath, string>> { new("x.cs", "1"), new("y.cs", "2") };
        var set = new DocumentChangeSet(pairs);
        Assert.Equal(2, set.Count);
        Assert.Equal("1", set["X.CS"]);
    }

    [Fact]
    public void PathsReturnsTheKeys()
    {
        var set = new DocumentChangeSet(new Dictionary<FilePath, string> { ["a.cs"] = "1", ["b.cs"] = "2" });
        IReadOnlyCollection<FilePath> paths = set.Paths;
        Assert.Equal(2, paths.Count);
        Assert.Contains(new FilePath("b.cs"), paths);
    }

    [Fact]
    public void CaseDifferingKeyIsTheSameKey()
    {
        var set = new DocumentChangeSet();
        set["A.cs"] = "1";
        set["a.CS"] = "2";
        Assert.Single(set);
        Assert.Equal("2", set["a.cs"]);
    }

    [Fact]
    public void MergeLaterWinsAndDoesNotMutate()
    {
        var first = new DocumentChangeSet(new Dictionary<FilePath, string> { ["a.cs"] = "1", ["b.cs"] = "2" });
        var second = new DocumentChangeSet(new Dictionary<FilePath, string> { ["B.CS"] = "second", ["c.cs"] = "3" });
        var merged = first.Merge(second);
        Assert.Equal(3, merged.Count);
        Assert.Equal("second", merged["b.cs"]);
        Assert.Equal("2", first["b.cs"]);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void MergeNullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new DocumentChangeSet().Merge(null!));
    }

    [Fact]
    public void JsonEqualsPlainDictionary()
    {
        var plain = new Dictionary<FilePath, string> { ["a.cs"] = "x", ["b.cs"] = "y" };
        var json = JsonSerializer.Serialize(plain, JsonDefaults.Options);
        Assert.Equal(json, JsonSerializer.Serialize(new DocumentChangeSet(plain), JsonDefaults.Options));
        var back = JsonSerializer.Deserialize<DocumentChangeSet>(json, JsonDefaults.Options);
        Assert.NotNull(back);
        Assert.Equal(2, back!.Count);
    }
}
""";

    private const string StepPrompt = """
In ContosoOrders.Core/Evals/ChangeSetTypeFromSpec add the file DocumentChangeSet.cs (namespace
ContosoOrders.Evals.ChangeSetTypeFromSpec) declaring `public sealed class DocumentChangeSet : Dictionary<FilePath, string>`.
Do not pass a comparer to the base class: FilePath already compares case-insensitively. Members: a parameterless
constructor; a copy constructor taking IDictionary<FilePath, string>; a constructor taking
IEnumerable<KeyValuePair<FilePath, string>>; a property Paths of type IReadOnlyCollection<FilePath> returning the keys;
a bool property IsEmpty; and `public DocumentChangeSet Merge(DocumentChangeSet other)` which returns a new set holding this
set's entries overlaid with other's (the later one wins, neither input is modified) and throws ArgumentNullException for
null. Also add the xUnit test file ContosoOrders.Tests/Evals/ChangeSetTypeFromSpec/DocumentChangeSetTests.cs (namespace
ContosoOrders.Tests.Evals.ChangeSetTypeFromSpec, same style as FilePathTests.cs in that folder) with a [Fact] for each
constructor, Paths, IsEmpty, Merge, Merge with null, a key that differs only by case, and a check that serializing a
DocumentChangeSet with JsonDefaults.Options gives the same JSON as the equivalent plain Dictionary<FilePath, string>.
Change no other file.
""";

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "ChangeSetTypeFromSpec",
        "Implement a Dictionary-derived change-set type from a written spec, with tests",
        "adherence",
        "DCS briefs/orchestrator-agent-a0f56471ed7f4162f brief 01, commit b0b1614a",
        new Dictionary<string, string>
        {
            [CoreFolder + "FilePath.cs"] = FilePathSource,
            [CoreFolder + "JsonDefaults.cs"] = JsonDefaultsSource,
            [TestFolder + "FilePathTests.cs"] = SeedTestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [TypePath] = GoldenTypeSource,
                    [ModelTestPath] = GoldenTestsSource,
                },
                AllowedPaths: new List<string> { TypePath, ModelTestPath },
                HiddenTestSource: HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckTypeAndTests(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "113");

    /// <summary>The type file passes no comparer; the model's test file exists and has at least six tests.</summary>
    private static string? CheckTypeAndTests(ImplementationScenarioCheckContext ctx)
    {
        if (!ctx.Exists(TypePath))
        {
            return $"missing: {TypePath}";
        }

        if (ctx.ReadText(TypePath).Contains("Comparer", StringComparison.OrdinalIgnoreCase))
        {
            return "DocumentChangeSet.cs mentions a comparer; the spec says to pass none";
        }

        if (!ctx.Exists(ModelTestPath))
        {
            return $"missing: {ModelTestPath}";
        }

        var tests = Regex.Matches(ctx.ReadText(ModelTestPath), @"\[(Fact|Theory)\b").Count;
        if (tests < 6)
        {
            return $"the model's test file has {tests} test(s), expected at least 6";
        }

        return null;
    }
}
