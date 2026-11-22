using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

/// <summary>
/// Retype only the public property of OperationSummary (a class with a primary constructor; a positional record would force the
/// property to keep the parameter's type) to DocumentChangeSet?, convert in the property initializer through a private helper,
/// leave the parameter and the From overloads alone and mark the parameter with a TODO. Callers and the wire shape stay unchanged.
/// </summary>
internal static class PropertyFirstRetypeScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/PropertyFirstRetype/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/PropertyFirstRetype/";
    private const string SummaryPath = CoreFolder + "OperationSummary.cs";
    private const string WireTestPath = TestFolder + "OperationSummaryWireTests.cs";

    private static readonly string[] CallerPaths =
    {
        CoreFolder + "RenameStep.cs",
        CoreFolder + "MoveStep.cs",
        CoreFolder + "DeleteStep.cs",
        CoreFolder + "ReportWriter.cs",
    };

    private const string FilePathSource = """
namespace ContosoOrders.Evals.PropertyFirstRetype;

/// <summary>A file path compared case-insensitively.</summary>
public readonly record struct FilePath(string Value)
{
    public bool Equals(FilePath other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);

    public override string ToString() => Value;

    public static implicit operator FilePath(string value) => new(value);
}
""";

    private const string DocumentChangeSetSource = """
namespace ContosoOrders.Evals.PropertyFirstRetype;

/// <summary>A change set: file path to new file content.</summary>
public sealed class DocumentChangeSet : Dictionary<FilePath, string>
{
    public DocumentChangeSet()
    {
    }

    public DocumentChangeSet(IDictionary<FilePath, string> source)
        : base(source)
    {
    }
}
""";

    private const string JsonDefaultsSource = """
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContosoOrders.Evals.PropertyFirstRetype;

/// <summary>Serializer options shared by the summary types; FilePath is written as a plain string, also as a dictionary key.</summary>
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

    private const string SeedSummarySource = """
namespace ContosoOrders.Evals.PropertyFirstRetype;

/// <summary>What an operation did: its name, the content it changed (path to new text) and how often it ran.</summary>
public sealed class OperationSummary(
    string name,
    Dictionary<FilePath, string>? changedContent,
    int attempts)
{
    public string Name { get; } = name;

    public Dictionary<FilePath, string>? ChangedContent { get; } = changedContent;

    public int Attempts { get; } = attempts;

    public static OperationSummary From(string name, Dictionary<FilePath, string>? changedContent)
    {
        return new OperationSummary(name, changedContent, 1);
    }

    public static OperationSummary From(string name, int attempts, Dictionary<FilePath, string>? changedContent)
    {
        return new OperationSummary(name, changedContent, attempts);
    }
}
""";

    private const string GoldenSummarySource = """
namespace ContosoOrders.Evals.PropertyFirstRetype;

/// <summary>What an operation did: its name, the content it changed (path to new text) and how often it ran.</summary>
public sealed class OperationSummary(
    string name,
    Dictionary<FilePath, string>? changedContent, // TODO retype this parameter
    int attempts)
{
    public string Name { get; } = name;

    public DocumentChangeSet? ChangedContent { get; } = ToChangeSet(changedContent);

    public int Attempts { get; } = attempts;

    public static OperationSummary From(string name, Dictionary<FilePath, string>? changedContent)
    {
        return new OperationSummary(name, changedContent, 1);
    }

    public static OperationSummary From(string name, int attempts, Dictionary<FilePath, string>? changedContent)
    {
        return new OperationSummary(name, changedContent, attempts);
    }

    private static DocumentChangeSet? ToChangeSet(Dictionary<FilePath, string>? source)
    {
        if (source is null)
        {
            return null;
        }

        return source as DocumentChangeSet ?? new DocumentChangeSet(source);
    }
}
""";

    private const string RenameStepSource = """
namespace ContosoOrders.Evals.PropertyFirstRetype;

/// <summary>Builds the summary of a rename.</summary>
public static class RenameStep
{
    public static OperationSummary Run(IEnumerable<string> paths)
    {
        var changed = new Dictionary<FilePath, string>();
        foreach (var path in paths)
        {
            changed[path] = "renamed";
        }

        return OperationSummary.From("rename", changed);
    }
}
""";

    private const string MoveStepSource = """
namespace ContosoOrders.Evals.PropertyFirstRetype;

/// <summary>Builds the summary of a move from an existing change set.</summary>
public static class MoveStep
{
    public static OperationSummary Run(DocumentChangeSet changes)
    {
        return OperationSummary.From("move", 2, changes);
    }
}
""";

    private const string DeleteStepSource = """
namespace ContosoOrders.Evals.PropertyFirstRetype;

/// <summary>Builds the summary of a delete that changed nothing.</summary>
public static class DeleteStep
{
    public static OperationSummary Run()
    {
        return OperationSummary.From("delete", null);
    }
}
""";

    private const string ReportWriterSource = """
namespace ContosoOrders.Evals.PropertyFirstRetype;

/// <summary>Describes summaries as text.</summary>
public static class ReportWriter
{
    public static string Describe(OperationSummary summary)
    {
        Dictionary<FilePath, string>? content = summary.ChangedContent;
        var count = content?.Count ?? 0;
        return $"{summary.Name}: {count} file(s) in {summary.Attempts} attempt(s)";
    }

    public static IReadOnlyList<string> Paths(OperationSummary summary)
    {
        return summary.ChangedContent?.Keys.Select(path => path.Value).ToList() ?? new List<string>();
    }
}
""";

    private const string WireTestSource = """
using System.Text.Json;
using ContosoOrders.Evals.PropertyFirstRetype;
using Xunit;

namespace ContosoOrders.Tests.Evals.PropertyFirstRetype;

public class OperationSummaryWireTests
{
    [Fact]
    public void SerializesToTheKnownShape()
    {
        var summary = OperationSummary.From("rename", new Dictionary<FilePath, string> { ["a.cs"] = "x" });
        var json = JsonSerializer.Serialize(summary, JsonDefaults.Options);
        Assert.Equal("{\"Name\":\"rename\",\"ChangedContent\":{\"a.cs\":\"x\"},\"Attempts\":1}", json);
    }

    [Fact]
    public void NullContentSerializesAsNull()
    {
        var json = JsonSerializer.Serialize(OperationSummary.From("noop", 2, null), JsonDefaults.Options);
        Assert.Equal("{\"Name\":\"noop\",\"ChangedContent\":null,\"Attempts\":2}", json);
    }
}
""";

    private const string HiddenTestSource = """
using System.Reflection;
using ContosoOrders.Evals.PropertyFirstRetype;
using Xunit;

namespace ContosoOrders.Tests.Evals.PropertyFirstRetype;

public class PropertyFirstRetypeStep1Tests
{
    [Fact]
    public void ChangedContentPropertyIsDocumentChangeSet()
    {
        var property = typeof(OperationSummary).GetProperty(nameof(OperationSummary.ChangedContent))!;
        Assert.Equal(typeof(DocumentChangeSet), property.PropertyType);
    }

    [Fact]
    public void ConstructorParameterStaysDictionary()
    {
        var constructor = typeof(OperationSummary).GetConstructors().Single();
        var parameter = constructor.GetParameters().Single(p => p.Name == "changedContent");
        Assert.Equal(typeof(Dictionary<FilePath, string>), parameter.ParameterType);
    }

    [Fact]
    public void FromParametersStayDictionary()
    {
        var methods = typeof(OperationSummary).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "From")
            .ToList();
        Assert.Equal(2, methods.Count);
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            Assert.Equal(typeof(Dictionary<FilePath, string>), parameters[parameters.Length - 1].ParameterType);
        }
    }

    [Fact]
    public void ExistingChangeSetIsKeptByReference()
    {
        var set = new DocumentChangeSet { ["a.cs"] = "x" };
        var summary = OperationSummary.From("move", set);
        Assert.Same(set, summary.ChangedContent);
    }

    [Fact]
    public void PlainDictionaryIsCopiedIntoAChangeSet()
    {
        var plain = new Dictionary<FilePath, string> { ["a.cs"] = "x" };
        var summary = OperationSummary.From("rename", plain);
        Assert.NotNull(summary.ChangedContent);
        Assert.NotSame(plain, summary.ChangedContent);
        Assert.Equal("x", summary.ChangedContent!["a.cs"]);
    }

    [Fact]
    public void NullStaysNull()
    {
        Assert.Null(OperationSummary.From("noop", null).ChangedContent);
    }
}
""";

    private const string StepPrompt = """
In ContosoOrders.Core/Evals/PropertyFirstRetype make the public property OperationSummary.ChangedContent of type
DocumentChangeSet? (it is Dictionary<FilePath, string>? today). Do not change the type of the primary-constructor
parameter changedContent or of any parameter of the From overloads, and edit no other file: the callers must compile
unchanged. Initialize the property from the parameter through a new private static helper that returns an existing
DocumentChangeSet as it is (the same reference), copies a plain Dictionary<FilePath, string> into a new DocumentChangeSet,
and returns null for null. Leave the comment `// TODO retype this parameter` on the changedContent parameter's line (or the
line directly above it) in the primary constructor. The JSON the type serializes to must not change.
""";

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "PropertyFirstRetype",
        "Retype a public property before its constructor parameter, keeping the wire shape",
        "adherence, multi-step state",
        "DCS briefs/orchestrator-agent-a231ad9b5317dcca5 brief 13, landed later as 36b4a43c",
        new Dictionary<string, string>
        {
            [CoreFolder + "FilePath.cs"] = FilePathSource,
            [CoreFolder + "DocumentChangeSet.cs"] = DocumentChangeSetSource,
            [CoreFolder + "JsonDefaults.cs"] = JsonDefaultsSource,
            [SummaryPath] = SeedSummarySource,
            [CallerPaths[0]] = RenameStepSource,
            [CallerPaths[1]] = MoveStepSource,
            [CallerPaths[2]] = DeleteStepSource,
            [CallerPaths[3]] = ReportWriterSource,
            [WireTestPath] = WireTestSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string> { [SummaryPath] = GoldenSummarySource },
                AllowedPaths: new List<string> { SummaryPath },
                HiddenTestSource: HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckTodoAndCallers(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "112");

    /// <summary>The four callers and the wire test are unchanged; a TODO about retyping sits on the changedContent parameter's line or the line above.</summary>
    private static string? CheckTodoAndCallers(ImplementationScenarioCheckContext ctx)
    {
        foreach (var path in CallerPaths.Append(WireTestPath))
        {
            if (!ctx.IsUnchanged(path))
            {
                return $"changed but must stay as it was: {path}";
            }
        }

        var parameter = ctx.Syntax(SummaryPath).GetRoot().DescendantNodes().OfType<ParameterSyntax>()
            .FirstOrDefault(p => p.Identifier.ValueText == "changedContent" && p.Parent?.Parent is TypeDeclarationSyntax);
        if (parameter is null)
        {
            return "the primary-constructor parameter changedContent is gone";
        }

        var lines = ctx.ReadText(SummaryPath).Replace("\r\n", "\n").Split('\n');
        var line = parameter.GetLocation().GetLineSpan().StartLinePosition.Line;
        var window = lines[line] + "\n" + (line > 0 ? lines[line - 1] : string.Empty);
        if (!Regex.IsMatch(window, @"//\s*TODO[^\n]*retype", RegexOptions.IgnoreCase))
        {
            return "no '// TODO retype this parameter' comment on the changedContent parameter's line or the line above it";
        }

        return null;
    }
}
