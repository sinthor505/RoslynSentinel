using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

/// <summary>
/// Adherence scenario: implement a collection type exactly from a written spec (signatures given in the prompt) and write the
/// tests for it. The visible DocumentChangeSetSpecTests drive the spec'd signatures through reflection; the check wants no
/// comparer passed to the base class (decided on compiled symbols) and model-written tests that kill three mutants of the type.
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

    // Variant overlay: a model test file that runs every member but asserts nothing, so all three mutants survive.
    private const string WeakTestsSource = """
using ContosoOrders.Evals.ChangeSetTypeFromSpec;
using Xunit;

namespace ContosoOrders.Tests.Evals.ChangeSetTypeFromSpec;

public class DocumentChangeSetTests
{
    [Fact]
    public void ConstructorsRun()
    {
        var source = new Dictionary<FilePath, string> { ["a.cs"] = "1" };
        _ = new DocumentChangeSet();
        _ = new DocumentChangeSet(source);
        _ = new DocumentChangeSet(new List<KeyValuePair<FilePath, string>>());
    }

    [Fact]
    public void PathsAndIsEmptyRun()
    {
        var set = new DocumentChangeSet();
        _ = set.Paths;
        _ = set.IsEmpty;
    }

    [Fact]
    public void MergeRuns()
    {
        _ = new DocumentChangeSet().Merge(new DocumentChangeSet());
    }
}
""";

    // Variant overlay: four compact tests (a Theory, not a Fact per member) that still kill all three mutants.
    private const string TheoryStyleTestsSource = """
using System.Text.Json;
using ContosoOrders.Evals.ChangeSetTypeFromSpec;
using Xunit;

namespace ContosoOrders.Tests.Evals.ChangeSetTypeFromSpec;

public class DocumentChangeSetTests
{
    private static Dictionary<FilePath, string> Sample() => new() { ["a.cs"] = "1", ["b.cs"] = "2" };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorsCopyTheEntries(bool fromPairs)
    {
        var source = Sample();
        var set = fromPairs
            ? new DocumentChangeSet(source.ToList())
            : new DocumentChangeSet(source);
        source["c.cs"] = "3";
        Assert.Equal(2, set.Count);
        Assert.Equal(2, set.Paths.Count);
        Assert.False(set.IsEmpty);
        Assert.Equal("2", set["B.CS"]);
    }

    [Fact]
    public void MergeLetsTheLaterSetWinAndLeavesBothInputsAlone()
    {
        var first = new DocumentChangeSet(Sample());
        var second = new DocumentChangeSet(new Dictionary<FilePath, string> { ["b.cs"] = "second", ["c.cs"] = "3" });
        var merged = first.Merge(second);
        Assert.Equal(3, merged.Count);
        Assert.Equal("second", merged["b.cs"]);
        Assert.Equal(2, first.Count);
        Assert.Equal("2", first["b.cs"]);
        Assert.Equal(2, second.Count);
        Assert.Throws<ArgumentNullException>(() => first.Merge(null!));
    }

    [Fact]
    public void EmptySetCaseInsensitiveKeysAndJson()
    {
        var set = new DocumentChangeSet();
        Assert.True(set.IsEmpty);
        set["A.cs"] = "1";
        set["a.CS"] = "2";
        Assert.Single(set);
        Assert.Equal(JsonSerializer.Serialize(new Dictionary<FilePath, string>(set), JsonDefaults.Options), JsonSerializer.Serialize(set, JsonDefaults.Options));
    }
}
""";

    // Visible acceptance tests (the old hidden step-1 tests): the type is resolved by name from the Core assembly and driven with reflection
    // and dynamic, so this file compiles on the seed (no DocumentChangeSet yet) and every test reads "type not found" until the model adds it.
    private const string SpecTestsSource = """
using System.Reflection;
using System.Text.Json;
using ContosoOrders.Evals.ChangeSetTypeFromSpec;
using Xunit;

namespace ContosoOrders.Tests.Evals.ChangeSetTypeFromSpec;

public class DocumentChangeSetSpecTests
{
    private const string TypeName = "ContosoOrders.Evals.ChangeSetTypeFromSpec.DocumentChangeSet";

    private static Type SetType()
    {
        var type = typeof(FilePath).Assembly.GetType(TypeName);
        Assert.True(type is not null, $"type not found: {TypeName} (add DocumentChangeSet.cs to ContosoOrders.Core/Evals/ChangeSetTypeFromSpec)");
        return type!;
    }

    private static object Create(Type[] parameterTypes, params object[] arguments)
    {
        var constructor = SetType().GetConstructor(parameterTypes);
        Assert.True(constructor is not null, $"no public constructor taking ({string.Join(", ", parameterTypes.Select(t => t.Name))})");
        return constructor!.Invoke(arguments);
    }

    private static dynamic NewEmpty() => Create(Type.EmptyTypes);

    private static dynamic NewCopy(IDictionary<FilePath, string> source) =>
        Create(new[] { typeof(IDictionary<FilePath, string>) }, source);

    private static dynamic NewFromPairs(IEnumerable<KeyValuePair<FilePath, string>> pairs) =>
        Create(new[] { typeof(IEnumerable<KeyValuePair<FilePath, string>>) }, pairs);

    private static Dictionary<FilePath, string> Sample() => new() { ["a.cs"] = "1", ["b.cs"] = "2" };

    [Fact]
    public void TypeShape()
    {
        var type = SetType();
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.Equal(typeof(Dictionary<FilePath, string>), type.BaseType);
    }

    [Fact]
    public void MembersHaveTheSpecifiedSignatures()
    {
        var type = SetType();
        Assert.Equal(typeof(IReadOnlyCollection<FilePath>), type.GetProperty("Paths")?.PropertyType);
        Assert.Equal(typeof(bool), type.GetProperty("IsEmpty")?.PropertyType);
        var merge = type.GetMethod("Merge", new[] { type });
        Assert.NotNull(merge);
        Assert.Equal(type, merge!.ReturnType);
    }

    [Fact]
    public void EmptyConstructor()
    {
        var set = NewEmpty();
        Assert.True((bool)set.IsEmpty);
        Assert.Empty((IEnumerable<FilePath>)set.Paths);
        Assert.Equal(0, (int)set.Count);
    }

    [Fact]
    public void CopyConstructorCopies()
    {
        var source = Sample();
        var set = NewCopy(source);
        source["c.cs"] = "3";
        Assert.Equal(2, (int)set.Count);
        Assert.False((bool)set.IsEmpty);
    }

    [Fact]
    public void EnumerableConstructorCopies()
    {
        var pairs = new List<KeyValuePair<FilePath, string>> { new("x.cs", "1"), new("y.cs", "2") };
        var set = NewFromPairs(pairs);
        Dictionary<FilePath, string> map = set;
        Assert.Equal(2, map.Count);
        Assert.Equal("1", map["X.CS"]);
    }

    [Fact]
    public void PathsReturnsTheKeys()
    {
        var set = NewCopy(Sample());
        IReadOnlyCollection<FilePath> paths = set.Paths;
        Assert.Equal(2, paths.Count);
        Assert.Contains(new FilePath("b.cs"), paths);
    }

    [Fact]
    public void CaseDifferingKeyIsTheSameKey()
    {
        Dictionary<FilePath, string> map = NewEmpty();
        map["A.cs"] = "1";
        map["a.CS"] = "2";
        Assert.Single(map);
        Assert.Equal("2", map["a.cs"]);
    }

    [Fact]
    public void MergeLaterWinsAndDoesNotMutate()
    {
        var first = NewCopy(Sample());
        var second = NewCopy(new Dictionary<FilePath, string> { ["B.CS"] = "second", ["c.cs"] = "3" });
        Dictionary<FilePath, string> merged = first.Merge(second);
        Dictionary<FilePath, string> firstMap = first;
        Dictionary<FilePath, string> secondMap = second;
        Assert.Equal(3, merged.Count);
        Assert.Equal("second", merged["b.cs"]);
        Assert.Equal("2", firstMap["b.cs"]);
        Assert.Equal(2, firstMap.Count);
        Assert.Equal(2, secondMap.Count);
        Assert.NotSame((object)first, (object)merged);
        Assert.Equal(SetType(), ((object)merged).GetType());
    }

    [Fact]
    public void MergeNullThrows()
    {
        var set = NewEmpty();
        Assert.Throws<ArgumentNullException>(() => { set.Merge(null); });
    }

    [Fact]
    public void JsonEqualsPlainDictionary()
    {
        var plain = new Dictionary<FilePath, string> { ["a.cs"] = "x", ["b.cs"] = "y" };
        var type = SetType();
        var json = JsonSerializer.Serialize(plain, JsonDefaults.Options);
        object set = NewCopy(plain);
        Assert.Equal(json, JsonSerializer.Serialize(set, type, JsonDefaults.Options));
        var back = JsonSerializer.Deserialize(json, type, JsonDefaults.Options);
        Assert.NotNull(back);
        Assert.IsType(type, back);
        Assert.Equal(2, ((Dictionary<FilePath, string>)back!).Count);
    }
}
""";

    private const string CannedReport = "DONE: added DocumentChangeSet.cs and DocumentChangeSetTests.cs; the type passes no comparer to the base class.";

    // Edits are written with \n; the template's line endings are kept. Throws when the template no longer contains the text to
    // edit, so a template change cannot silently turn a mutant or variant into the golden.
    private static string Edited(string text, string oldText, string newText)
    {
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.Contains(oldText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ChangeSetTypeFromSpec edit: '{oldText}' is not in the template.");
        }

        return normalized.Replace(oldText, newText, StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal);
    }

    // The three mutants (Spec S4): the golden type with exactly one changed line each. A static readonly (not a const) so the one
    // changed line is stated against GoldenTypeSource and cannot drift from it; only read by the check, never by the Definition.
    // Merge mutates this instead of a new set.
    private static readonly string MergeMutatesInput = Edited(GoldenTypeSource, "var merged = new DocumentChangeSet(this);", "var merged = this;");

    // Paths returns nothing.
    private static readonly string PathsEmpty = Edited(GoldenTypeSource, "public IReadOnlyCollection<FilePath> Paths => Keys;", "public IReadOnlyCollection<FilePath> Paths => Array.Empty<FilePath>();");

    // The copy constructor copies nothing.
    private static readonly string CopyIgnoresSource = Edited(GoldenTypeSource, ": base(source)", ": base()");

    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/ChangeSetTypeFromSpec add DocumentChangeSet.cs (namespace ContosoOrders.Evals.ChangeSetTypeFromSpec) declaring " +
            "`public sealed class DocumentChangeSet : Dictionary<FilePath, string>` with: a parameterless constructor; a copy constructor taking `IDictionary<FilePath, string>`; " +
            "a constructor taking `IEnumerable<KeyValuePair<FilePath, string>>`; a property `Paths` of type `IReadOnlyCollection<FilePath>` returning the keys; " +
            "a `bool` property `IsEmpty`; and `public DocumentChangeSet Merge(DocumentChangeSet other)`, which returns a new set holding this set's entries overlaid with other's " +
            "(the later one wins, neither input is modified) and throws `ArgumentNullException` for null. " +
            "Also add ContosoOrders.Tests/Evals/ChangeSetTypeFromSpec/DocumentChangeSetTests.cs (namespace ContosoOrders.Tests.Evals.ChangeSetTypeFromSpec, same style as FilePathTests.cs) " +
            "with a `[Fact]` for each constructor, `Paths`, `IsEmpty`, `Merge`, `Merge` with null, a key that differs only by case, " +
            "and a check that serializing a DocumentChangeSet with `JsonDefaults.Options` gives the same JSON as the equivalent plain `Dictionary<FilePath, string>`.",
        Files: "DocumentChangeSet.cs and DocumentChangeSetTests.cs in the folders above. No other file.",
        Conditions: "Do not pass a comparer to the base class: FilePath already compares case-insensitively.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including DocumentChangeSetSpecTests and your DocumentChangeSetTests; " +
            "your tests would fail for a `Merge` that modifies an input, a `Paths` that returns nothing, and a copy constructor that copies nothing."));

    public static ImplementationScenario Definition
    {
        get;
    } = new ImplementationScenario(
        "ChangeSetTypeFromSpec",
        "Implement a Dictionary-derived change-set type from a written spec, with tests",
        "adherence",
        "DCS briefs/orchestrator-agent-a0f56471ed7f4162f brief 01, commit b0b1614a",
        new Dictionary<string, string>
        {
            [CoreFolder + "FilePath.cs"] = FilePathSource,
            [CoreFolder + "JsonDefaults.cs"] = JsonDefaultsSource,
            [TestFolder + "FilePathTests.cs"] = SeedTestsSource,
            [TestFolder + "DocumentChangeSetSpecTests.cs"] = SpecTestsSource,
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
                HiddenTestSource: null,
                ExtraCheck: static ctx => CheckTypeAndTestsAsync(ctx),
                CannedFinalMessage: CannedReport,
                Variants: BuildVariants(),
                DoneWhenTests: new[] { "DocumentChangeSetSpecTests", "DocumentChangeSetTests" }),
        },
        PreferredHost: "113",
        PrimaryAxis: EvalAxis.InstructionAdherence);

    private const string TestProjectPath = "ContosoOrders.Tests/ContosoOrders.Tests.csproj";

    private const string FilePathTypeName = "ContosoOrders.Evals.ChangeSetTypeFromSpec.FilePath";

    /// <summary>
    /// The type file exists and no base(...) initializer argument has a type that implements IEqualityComparer{FilePath} (decided on
    /// compiled symbols, so a comment or a name that merely says "comparer" is fine); the model's test file exists; and its test classes
    /// pass against the golden type and kill each of the three mutants. Scope, the build and the spec tests (sealed, base type, members)
    /// are decided by the shared pipeline and the test run, not here.
    /// </summary>
    private static async Task<string?> CheckTypeAndTestsAsync(ImplementationScenarioCheckContext ctx)
    {
        if (!ctx.Exists(TypePath))
        {
            return $"missing: {TypePath}";
        }

        var comparerFailure = CheckNoComparerPassed(ctx);
        if (comparerFailure is not null)
        {
            return comparerFailure;
        }

        if (!ctx.Exists(ModelTestPath))
        {
            return $"missing: {ModelTestPath}";
        }

        return await CheckTestsKillMutantsAsync(ctx);
    }

    private static string? CheckNoComparerPassed(ImplementationScenarioCheckContext ctx)
    {
        var compilation = ctx.Compile(CoreFolder.TrimEnd('/'));
        var filePath = compilation.Type(FilePathTypeName);
        var comparerDefinition = compilation.Compilation.GetTypeByMetadataName("System.Collections.Generic.IEqualityComparer`1");
        if (filePath is null || comparerDefinition is null)
        {
            return "FilePath is gone from the Core folder, so the base initializer cannot be checked";
        }

        var comparerOfFilePath = comparerDefinition.Construct(filePath);
        var initializers = compilation.ModelFor(TypePath).SyntaxTree.GetRoot().DescendantNodes()
            .OfType<ConstructorInitializerSyntax>()
            .Where(initializer => initializer.IsKind(SyntaxKind.BaseConstructorInitializer));
        foreach (var initializer in initializers)
        {
            foreach (var argument in initializer.ArgumentList.Arguments)
            {
                var type = compilation.TypeOf(argument.Expression);
                if (type is not null
                    && (SymbolEqualityComparer.Default.Equals(type, comparerOfFilePath)
                        || type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, comparerOfFilePath))))
                {
                    var line = initializer.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    return $"DocumentChangeSet.cs line {line} passes a comparer ({type.ToDisplayString()}) to the base constructor; the spec says to pass none";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the model's test classes against the golden type (must pass, and run) and against each mutant (must fail). The filter is the
    /// fully qualified names of the classes declared in the model's test file, found on the compilation over the test folder.
    /// </summary>
    private static async Task<string?> CheckTestsKillMutantsAsync(ImplementationScenarioCheckContext ctx)
    {
        var testModel = ctx.Compile(TestFolder.TrimEnd('/')).ModelFor(ModelTestPath);
        var classNames = testModel.SyntaxTree.GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(declaration => declaration.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .Select(declaration => testModel.GetDeclaredSymbol(declaration)?.ToDisplayString())
            .OfType<string>()
            .ToList();
        if (classNames.Count == 0)
        {
            return $"{ModelTestPath} declares no test class";
        }

        var filter = string.Join("|", classNames.Select(name => $"FullyQualifiedName~{name}"));
        var mutants = new[]
        {
            new ScenarioMutant("Golden", new Dictionary<string, string> { [TypePath] = GoldenTypeSource }),
            new ScenarioMutant(nameof(MergeMutatesInput), new Dictionary<string, string> { [TypePath] = MergeMutatesInput }),
            new ScenarioMutant(nameof(PathsEmpty), new Dictionary<string, string> { [TypePath] = PathsEmpty }),
            new ScenarioMutant(nameof(CopyIgnoresSource), new Dictionary<string, string> { [TypePath] = CopyIgnoresSource }),
        };

        // Sequential inside MutationGrader: one dotnet test at a time. The first entry is the unmutated golden, the rest are the mutants.
        var results = await MutationGrader.RunAsync(ctx.SolutionDirectory, TestProjectPath, filter, mutants, ctx.CancellationToken);
        var golden = results[0];
        if (golden.Killed)
        {
            var why = golden.Note.Length > 0 ? golden.Note : $"{golden.Failed} of {golden.Passed + golden.Failed} failed";
            return $"the model's tests ({string.Join(", ", classNames)}) do not pass against the reference DocumentChangeSet: {why}";
        }

        if (golden.Passed == 0)
        {
            return $"no test of {string.Join(", ", classNames)} ran against the reference DocumentChangeSet";
        }

        var survivors = results.Skip(1).Where(result => !result.Killed).Select(result => result.Name).ToList();
        if (survivors.Count > 0)
        {
            return $"the model's tests do not kill: {string.Join(", ", survivors)} (every test still passed against a type with that one defect)";
        }

        return null;
    }

    /// <summary>
    /// The executable outcome table (Spec S4): each variant overlays DocumentChangeSet.cs and/or DocumentChangeSetTests.cs (the golden plus
    /// one change) on step 1 (golden) with a DONE: final message. A failing row claims DONE, so its class is SilentIncomplete.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        const string Done = "DONE: added DocumentChangeSet.cs and DocumentChangeSetTests.cs.";

        // Row 1: the word Comparer only in a doc comment; no base argument, so the symbol check passes.
        var comparerInComment = Edited(
            GoldenTypeSource,
            "Keys compare through FilePath's own equality.",
            "No Comparer is passed to the base class; keys compare through FilePath's own equality.");

        // Row 2: the parameterless constructor passes the default comparer to the base class (it builds, and every test still passes).
        var comparerPassed = Edited(
            GoldenTypeSource,
            "public DocumentChangeSet()\n    {",
            "public DocumentChangeSet()\n        : base(EqualityComparer<FilePath>.Default)\n    {");

        return new[]
        {
            new ScenarioVariant(
                Name: "ComparerInComment",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [TypePath] = comparerInComment },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "ComparerPassed",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [TypePath] = comparerPassed },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "passes a comparer"),
            new ScenarioVariant(
                Name: "WeakTests",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [ModelTestPath] = WeakTestsSource },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "do not kill"),
            new ScenarioVariant(
                Name: "TheoryStyle",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [ModelTestPath] = TheoryStyleTestsSource },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
        };
    }
}
