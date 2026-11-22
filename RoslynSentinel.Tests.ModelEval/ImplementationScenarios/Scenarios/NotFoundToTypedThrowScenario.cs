using System.Text.RegularExpressions;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

/// <summary>
/// Two steps. Step 1: two engines report a missing class through a result code; make them throw the typed exception the way a
/// sibling engine already does and simplify the tool helper. Step 2 (on the step-1 golden): add tests for the new throws.
/// </summary>
internal static class NotFoundToTypedThrowScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/NotFoundToTypedThrow/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/NotFoundToTypedThrow/";
    private const string ScaffoldPath = CoreFolder + "TestScaffoldEngine.cs";
    private const string BuilderPath = CoreFolder + "BuilderEngine.cs";
    private const string ToolsPath = CoreFolder + "GeneratorTools.cs";
    private const string OutcomeTestsPath = TestFolder + "OutcomeTests.cs";
    private const string ModelTestsPath = TestFolder + "NotFoundThrowTests.cs";

    private const string ExceptionSource = """
namespace ContosoOrders.Evals.NotFoundToTypedThrow;

/// <summary>Kinds of declaration a tool can fail to find.</summary>
public static class MissingTargetKinds
{
    public const string Class = "class";
    public const string Type = "type";
}

/// <summary>Thrown when a named target is missing; carries the kind and the missing name.</summary>
public sealed class ToolNotFoundException(string message, string kind, string name) : Exception(message)
{
    public string Kind { get; } = kind;

    public string Name { get; } = name;
}
""";

    private const string HintSource = """
namespace ContosoOrders.Evals.NotFoundToTypedThrow;

/// <summary>Builds the hint text that follows a not-found message.</summary>
public static class DeclarationNotFoundHint
{
    public static string Build(string kind, string parameterName, string missing, IEnumerable<string> available)
    {
        var names = available.ToList();
        var nearest = names.FirstOrDefault(name =>
            name.StartsWith(missing, StringComparison.OrdinalIgnoreCase)
            || missing.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        if (nearest is not null)
        {
            return $"Did you mean '{nearest}'? Retry with {parameterName}: '{nearest}'.";
        }

        return names.Count == 0
            ? $"No {kind} is declared in the file."
            : $"Declared {kind} names: {string.Join(", ", names)}.";
    }
}
""";

    private const string StructuralSource = """
using System.Text.RegularExpressions;

namespace ContosoOrders.Evals.NotFoundToTypedThrow;

/// <summary>Structural edits. A missing class is thrown as ToolNotFoundException (the pattern the generators should follow).</summary>
public static class StructuralEngine
{
    public static async Task<string> RenameClassAsync(string source, string className, string newName)
    {
        await Task.CompletedTask;
        var classes = DeclaredClasses(source);
        if (!classes.Contains(className))
        {
            throw new ToolNotFoundException(
                $"Class '{className}' not found in the file. "
                + DeclarationNotFoundHint.Build("class", "className", className, classes),
                MissingTargetKinds.Class,
                className);
        }

        return Regex.Replace(source, $@"\bclass\s+{Regex.Escape(className)}\b", $"class {newName}");
    }

    internal static List<string> DeclaredClasses(string source)
    {
        return Regex.Matches(source, @"\bclass\s+(\w+)").Select(match => match.Groups[1].Value).ToList();
    }

    internal static List<string> DeclaredClassesOrRecords(string source)
    {
        return Regex.Matches(source, @"\b(?:class|record)\s+(\w+)").Select(match => match.Groups[1].Value).ToList();
    }
}
""";

    private const string SeedScaffoldSource = """
using System.Text.RegularExpressions;

namespace ContosoOrders.Evals.NotFoundToTypedThrow;

/// <summary>A generated test scaffold, or an error code.</summary>
public sealed record TestScaffoldResult(string Code, string? Error)
{
    public string? ErrorCode { get; init; }
}

/// <summary>Generates a test scaffold for a class declared in a source text.</summary>
public static class TestScaffoldEngine
{
    public static async Task<TestScaffoldResult> GenerateAsync(string source, string className)
    {
        await Task.CompletedTask;
        var classes = StructuralEngine.DeclaredClasses(source);
        if (!classes.Contains(className))
        {
            return new TestScaffoldResult(string.Empty, $"Class '{className}' not found in the file.") { ErrorCode = "NotFound" };
        }

        if (Regex.IsMatch(source, $@"\bstatic\s+class\s+{Regex.Escape(className)}\b"))
        {
            return new TestScaffoldResult(string.Empty, $"Class '{className}' is static.") { ErrorCode = "TargetIneligible" };
        }

        return new TestScaffoldResult($"public class {className}Tests {{ }}", null);
    }
}
""";

    private const string GoldenScaffoldSource = """
using System.Text.RegularExpressions;

namespace ContosoOrders.Evals.NotFoundToTypedThrow;

/// <summary>A generated test scaffold, or an error code.</summary>
public sealed record TestScaffoldResult(string Code, string? Error)
{
    public string? ErrorCode { get; init; }
}

/// <summary>Generates a test scaffold for a class declared in a source text.</summary>
public static class TestScaffoldEngine
{
    public static async Task<TestScaffoldResult> GenerateAsync(string source, string className)
    {
        await Task.CompletedTask;
        var classes = StructuralEngine.DeclaredClasses(source);
        if (!classes.Contains(className))
        {
            throw new ToolNotFoundException(
                $"Class '{className}' not found in the file. "
                + DeclarationNotFoundHint.Build("class", "className", className, classes),
                MissingTargetKinds.Class,
                className);
        }

        if (Regex.IsMatch(source, $@"\bstatic\s+class\s+{Regex.Escape(className)}\b"))
        {
            return new TestScaffoldResult(string.Empty, $"Class '{className}' is static.") { ErrorCode = "TargetIneligible" };
        }

        return new TestScaffoldResult($"public class {className}Tests {{ }}", null);
    }
}
""";

    private const string SeedBuilderSource = """
using System.Text.RegularExpressions;

namespace ContosoOrders.Evals.NotFoundToTypedThrow;

/// <summary>A generated fluent builder, or an error code.</summary>
public sealed record BuilderResult(string Code, string? Error)
{
    public string? ErrorCode { get; init; }
}

/// <summary>Generates a fluent builder for a class or record declared in a source text.</summary>
public static class BuilderEngine
{
    public static async Task<BuilderResult> GenerateAsync(string source, string className)
    {
        await Task.CompletedTask;
        var types = StructuralEngine.DeclaredClassesOrRecords(source);
        if (!types.Contains(className))
        {
            return new BuilderResult(string.Empty, $"Class or record '{className}' not found in the file.") { ErrorCode = "NotFound" };
        }

        if (Regex.IsMatch(source, $@"\bstatic\s+class\s+{Regex.Escape(className)}\b"))
        {
            return new BuilderResult(string.Empty, $"Class '{className}' is static.") { ErrorCode = "TargetIneligible" };
        }

        return new BuilderResult($"public class {className}Builder {{ }}", null);
    }
}
""";

    private const string GoldenBuilderSource = """
using System.Text.RegularExpressions;

namespace ContosoOrders.Evals.NotFoundToTypedThrow;

/// <summary>A generated fluent builder, or an error code.</summary>
public sealed record BuilderResult(string Code, string? Error)
{
    public string? ErrorCode { get; init; }
}

/// <summary>Generates a fluent builder for a class or record declared in a source text.</summary>
public static class BuilderEngine
{
    public static async Task<BuilderResult> GenerateAsync(string source, string className)
    {
        await Task.CompletedTask;
        var types = StructuralEngine.DeclaredClassesOrRecords(source);
        if (!types.Contains(className))
        {
            throw new ToolNotFoundException(
                $"Class or record '{className}' not found in the file. "
                + DeclarationNotFoundHint.Build("class or record", "className", className, types),
                MissingTargetKinds.Type,
                className);
        }

        if (Regex.IsMatch(source, $@"\bstatic\s+class\s+{Regex.Escape(className)}\b"))
        {
            return new BuilderResult(string.Empty, $"Class '{className}' is static.") { ErrorCode = "TargetIneligible" };
        }

        return new BuilderResult($"public class {className}Builder {{ }}", null);
    }
}
""";

    private const string SeedToolsSource = """
namespace ContosoOrders.Evals.NotFoundToTypedThrow;

/// <summary>What a generator tool returns.</summary>
public sealed record GeneratorOutcome(string? Code, string? Error, string? Hint, string? ErrorCode);

/// <summary>Records the kinds of target that were missing.</summary>
public static class MissingTargetLog
{
    public static List<string> Recorded { get; } = new();

    public static void Record(string kind) => Recorded.Add(kind);
}

/// <summary>Tool wrappers around the generator engines.</summary>
public static class GeneratorTools
{
    public static async Task<GeneratorOutcome> GenerateScaffoldAsync(string source, string className)
    {
        var result = await TestScaffoldEngine.GenerateAsync(source, className);
        if (result.ErrorCode is not null)
        {
            return GeneratorFailure(result.ErrorCode, result.Error!, MissingTargetKinds.Class);
        }

        return new GeneratorOutcome(result.Code, null, null, null);
    }

    public static async Task<GeneratorOutcome> GenerateBuilderAsync(string source, string className)
    {
        var result = await BuilderEngine.GenerateAsync(source, className);
        if (result.ErrorCode is not null)
        {
            return GeneratorFailure(result.ErrorCode, result.Error!, MissingTargetKinds.Type);
        }

        return new GeneratorOutcome(result.Code, null, null, null);
    }

    // Result-level failures of the generators: a missing target records its kind, an ineligible target carries a fixed hint.
    private static GeneratorOutcome GeneratorFailure(string code, string error, string missingTargetKind)
    {
        string? hint;
        if (code == "NotFound")
        {
            MissingTargetLog.Record(missingTargetKind);
            hint = "Check the class name and retry.";
        }
        else if (code == "TargetIneligible")
        {
            hint = "Pick a non-static class.";
        }
        else
        {
            hint = null;
        }

        return new GeneratorOutcome(null, error, hint, code);
    }
}
""";

    private const string GoldenToolsSource = """
namespace ContosoOrders.Evals.NotFoundToTypedThrow;

/// <summary>What a generator tool returns.</summary>
public sealed record GeneratorOutcome(string? Code, string? Error, string? Hint, string? ErrorCode);

/// <summary>Records the kinds of target that were missing.</summary>
public static class MissingTargetLog
{
    public static List<string> Recorded { get; } = new();

    public static void Record(string kind) => Recorded.Add(kind);
}

/// <summary>Tool wrappers around the generator engines.</summary>
public static class GeneratorTools
{
    public static async Task<GeneratorOutcome> GenerateScaffoldAsync(string source, string className)
    {
        var result = await TestScaffoldEngine.GenerateAsync(source, className);
        if (result.ErrorCode is not null)
        {
            return GeneratorFailure(result.ErrorCode, result.Error!);
        }

        return new GeneratorOutcome(result.Code, null, null, null);
    }

    public static async Task<GeneratorOutcome> GenerateBuilderAsync(string source, string className)
    {
        var result = await BuilderEngine.GenerateAsync(source, className);
        if (result.ErrorCode is not null)
        {
            return GeneratorFailure(result.ErrorCode, result.Error!);
        }

        return new GeneratorOutcome(result.Code, null, null, null);
    }

    // Result-level failures of the generators. A missing target is thrown by the engines (ToolNotFoundException); only
    // ineligible targets and other coded results arrive here.
    private static GeneratorOutcome GeneratorFailure(string code, string error)
    {
        var hint = code == "TargetIneligible" ? "Pick a non-static class." : null;
        return new GeneratorOutcome(null, error, hint, code);
    }
}
""";

    private const string OutcomeTestsSource = """
using ContosoOrders.Evals.NotFoundToTypedThrow;
using Xunit;

namespace ContosoOrders.Tests.Evals.NotFoundToTypedThrow;

public class OutcomeTests
{
    [Fact]
    public async Task Scaffold_ExistingClass_ReturnsCode()
    {
        var outcome = await GeneratorTools.GenerateScaffoldAsync("public class Order { }", "Order");
        Assert.Contains("OrderTests", outcome.Code);
        Assert.Null(outcome.ErrorCode);
    }

    [Fact]
    public async Task Builder_ExistingClass_ReturnsCode()
    {
        var outcome = await GeneratorTools.GenerateBuilderAsync("public record Order(int Id);", "Order");
        Assert.Contains("OrderBuilder", outcome.Code);
        Assert.Null(outcome.ErrorCode);
    }

    [Fact]
    public async Task Scaffold_StaticClass_ReturnsTargetIneligibleWithHint()
    {
        var outcome = await GeneratorTools.GenerateScaffoldAsync("public static class Helpers { }", "Helpers");
        Assert.Equal("TargetIneligible", outcome.ErrorCode);
        Assert.Equal("Pick a non-static class.", outcome.Hint);
    }

    [Fact]
    public async Task Builder_StaticClass_ReturnsTargetIneligible()
    {
        var outcome = await GeneratorTools.GenerateBuilderAsync("public static class Helpers { }", "Helpers");
        Assert.Equal("TargetIneligible", outcome.ErrorCode);
    }
}
""";

    private const string Step1HiddenTestSource = """
using System.Reflection;
using ContosoOrders.Evals.NotFoundToTypedThrow;
using Xunit;

namespace ContosoOrders.Tests.Evals.NotFoundToTypedThrow;

public class NotFoundToTypedThrowStep1Tests
{
    private const string Source = "public class Order { }";

    [Fact]
    public async Task Scaffold_MissingClass_ThrowsWithKindNameAndHint()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => TestScaffoldEngine.GenerateAsync(Source, "Orders"));
        Assert.Equal(MissingTargetKinds.Class, ex.Kind);
        Assert.Equal("Orders", ex.Name);
        Assert.Contains("Class 'Orders' not found", ex.Message);
        Assert.Contains("Did you mean 'Order'?", ex.Message);
        Assert.Contains("Retry with className: 'Order'.", ex.Message);
    }

    [Fact]
    public async Task Builder_MissingClass_ThrowsWithKindNameAndHint()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => BuilderEngine.GenerateAsync(Source, "Orders"));
        Assert.Equal(MissingTargetKinds.Type, ex.Kind);
        Assert.Equal("Orders", ex.Name);
        Assert.Contains("Class or record 'Orders' not found", ex.Message);
        Assert.Contains("Did you mean 'Order'?", ex.Message);
        Assert.Contains("Retry with className: 'Order'.", ex.Message);
    }

    [Fact]
    public async Task Tools_LetTheThrowPropagate()
    {
        await Assert.ThrowsAsync<ToolNotFoundException>(() => GeneratorTools.GenerateScaffoldAsync(Source, "Orders"));
        await Assert.ThrowsAsync<ToolNotFoundException>(() => GeneratorTools.GenerateBuilderAsync(Source, "Orders"));
    }

    [Fact]
    public void GeneratorFailure_HasTwoParameters()
    {
        var method = typeof(GeneratorTools).GetMethod("GeneratorFailure", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(2, method.GetParameters().Length);
    }

    [Fact]
    public async Task StaticClass_StillReturnsTheResultLevelIneligibleCode()
    {
        var outcome = await GeneratorTools.GenerateScaffoldAsync("public static class Helpers { }", "Helpers");
        Assert.Equal("TargetIneligible", outcome.ErrorCode);
    }

    [Fact]
    public async Task StructuralEngine_StillThrows()
    {
        await Assert.ThrowsAsync<ToolNotFoundException>(() => StructuralEngine.RenameClassAsync(Source, "Orders", "Other"));
    }
}
""";

    private const string GoldenModelTestsSource = """
using ContosoOrders.Evals.NotFoundToTypedThrow;
using Xunit;

namespace ContosoOrders.Tests.Evals.NotFoundToTypedThrow;

public class NotFoundThrowTests
{
    private const string Source = "public class Order { }";

    [Fact]
    public async Task Scaffold_MissingClass_ThrowsNotFoundWithKindNameAndHint()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await TestScaffoldEngine.GenerateAsync(Source, "Orders"));
        Assert.Equal(MissingTargetKinds.Class, ex.Kind);
        Assert.Equal("Orders", ex.Name);
        Assert.Contains("Did you mean 'Order'?", ex.Message);
        Assert.Contains("Retry with className: 'Order'.", ex.Message);
    }

    [Fact]
    public async Task Builder_MissingClass_ThrowsNotFoundWithKindNameAndHint()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await BuilderEngine.GenerateAsync(Source, "Orders"));
        Assert.Equal(MissingTargetKinds.Type, ex.Kind);
        Assert.Equal("Orders", ex.Name);
        Assert.Contains("Did you mean 'Order'?", ex.Message);
        Assert.Contains("Retry with className: 'Order'.", ex.Message);
    }
}
""";

    private const string Step1Prompt = """
In ContosoOrders.Core/Evals/NotFoundToTypedThrow, TestScaffoldEngine.GenerateAsync and BuilderEngine.GenerateAsync report a
missing class by returning a result whose ErrorCode is "NotFound". Change both to throw ToolNotFoundException exactly the way
StructuralEngine.RenameClassAsync does: the engine's own "... not found in the file. " prefix (the builder says "Class or
record '<name>' not found in the file. ") followed by DeclarationNotFoundHint.Build, then the kind constant and the missing
name. Use MissingTargetKinds.Class for the scaffold and MissingTargetKinds.Type for the builder (for the builder the available
names are its classes and records, kind text "class or record"). Then simplify GeneratorTools.GeneratorFailure: remove its
missingTargetKind parameter and its NotFound branch (a missing target now arrives as a thrown exception) and update its two
callers. Do not edit any test file or any other file.
""";

    private const string Step2Prompt = """
The two generator engines in ContosoOrders.Core/Evals/NotFoundToTypedThrow (TestScaffoldEngine.GenerateAsync and
BuilderEngine.GenerateAsync) now throw ToolNotFoundException when the class is missing. Add xUnit tests for that in one new
file under ContosoOrders.Tests/Evals/NotFoundToTypedThrow/ (do not edit OutcomeTests.cs or any production file): one test per
engine that passes the source `public class Order { }` and the near-miss name "Orders", awaits
`Assert.ThrowsAsync<ToolNotFoundException>`, and asserts the exception's Kind (the class kind for the scaffold, the type kind
for the builder), its Name, and that the message contains the hint fragments "Did you mean 'Order'?" and
"Retry with className: 'Order'.".
""";

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "NotFoundToTypedThrow",
        "Turn not-found result codes into a typed throw following an existing pattern, then test the throws",
        "multi-step state, following a pattern",
        "ModelEval-Scenario-Ideas 05, commits 709da383 and 224a833c",
        new Dictionary<string, string>
        {
            [CoreFolder + "ToolNotFoundException.cs"] = ExceptionSource,
            [CoreFolder + "DeclarationNotFoundHint.cs"] = HintSource,
            [CoreFolder + "StructuralEngine.cs"] = StructuralSource,
            [ScaffoldPath] = SeedScaffoldSource,
            [BuilderPath] = SeedBuilderSource,
            [ToolsPath] = SeedToolsSource,
            [OutcomeTestsPath] = OutcomeTestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: Step1Prompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [ScaffoldPath] = GoldenScaffoldSource,
                    [BuilderPath] = GoldenBuilderSource,
                    [ToolsPath] = GoldenToolsSource,
                },
                AllowedPaths: new List<string> { ScaffoldPath, BuilderPath, ToolsPath },
                HiddenTestSource: Step1HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckStepOne(ctx)),
                CannedFinalMessage: null),
            new ImplementationScenarioStep(
                Prompt: Step2Prompt,
                GoldenFiles: new Dictionary<string, string> { [ModelTestsPath] = GoldenModelTestsSource },
                AllowedPaths: new List<string> { TestFolder },
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckStepTwo(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "113");

    /// <summary>The seed outcome tests are untouched and no file still speaks the old not-found result code or kind argument.</summary>
    private static string? CheckStepOne(ImplementationScenarioCheckContext ctx)
    {
        if (!ctx.IsUnchanged(OutcomeTestsPath))
        {
            return "OutcomeTests.cs changed but must stay as it was";
        }

        foreach (var path in new[] { ScaffoldPath, BuilderPath, ToolsPath })
        {
            if (ctx.ReadText(path).Contains("\"NotFound\""))
            {
                return $"{path} still mentions the \"NotFound\" result code";
            }
        }

        if (ctx.ReadText(ToolsPath).Contains("missingTargetKind"))
        {
            return "GeneratorTools.cs still has the missingTargetKind parameter";
        }

        return null;
    }

    /// <summary>The model wrote a new test file with at least two awaited ThrowsAsync facts asserting kind, name and the hint fragments.</summary>
    private static string? CheckStepTwo(ImplementationScenarioCheckContext ctx)
    {
        if (!ctx.IsUnchanged(OutcomeTestsPath))
        {
            return "OutcomeTests.cs changed but must stay as it was";
        }

        var modelFiles = Directory.GetFiles(ctx.FullPath(TestFolder), "*.cs")
            .Where(file => !string.Equals(Path.GetFileName(file), "OutcomeTests.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (modelFiles.Count == 0)
        {
            return $"no new test file found under {TestFolder}";
        }

        var text = string.Concat(modelFiles.Select(File.ReadAllText));
        var facts = Regex.Matches(text, @"\[Fact").Count;
        if (facts < 2)
        {
            return $"the new test file has {facts} [Fact] test(s), expected at least 2";
        }

        var throws = Regex.Matches(text, @"await\s+Assert\.ThrowsAsync<ToolNotFoundException>").Count;
        if (throws < 2)
        {
            return $"the new tests have {throws} awaited Assert.ThrowsAsync<ToolNotFoundException> call(s), expected at least 2";
        }

        foreach (var fragment in new[] { ".Kind", ".Name", "Did you mean", "Retry with", "TestScaffoldEngine", "BuilderEngine" })
        {
            if (!text.Contains(fragment))
            {
                return $"the new tests never mention {fragment}";
            }
        }

        return null;
    }
}
