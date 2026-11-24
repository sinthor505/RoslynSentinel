using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

/// <summary>
/// Two steps. Step 1: two engines report a missing class through a result code; make them throw the typed exception the way a
/// sibling engine already does and simplify the tool helper (graded by the visible TypedThrowTests and OutcomeTests). Step 2 (spec to
/// test, starting from the step-1 golden): add tests for the new throws (graded by mutation: every new test must fail against the
/// step-1 seed engines).
/// </summary>
internal static class NotFoundToTypedThrowScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/NotFoundToTypedThrow/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/NotFoundToTypedThrow/";
    private const string ScaffoldPath = CoreFolder + "TestScaffoldEngine.cs";
    private const string BuilderPath = CoreFolder + "BuilderEngine.cs";
    private const string ToolsPath = CoreFolder + "GeneratorTools.cs";
    private const string OutcomeTestsPath = TestFolder + "OutcomeTests.cs";

    private const string TypedThrowTestsPath = TestFolder + "TypedThrowTests.cs";

    private const string TestProjectPath = "ContosoOrders.Tests/ContosoOrders.Tests.csproj";

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

    // Visible acceptance tests of step 1 (moved from the former hidden test file). They compile against the seed (ToolNotFoundException
    // exists there) and are red until both engines throw and GeneratorFailure has two parameters.
    private const string TypedThrowTestsSource = """
using System.Reflection;
using ContosoOrders.Evals.NotFoundToTypedThrow;
using Xunit;

namespace ContosoOrders.Tests.Evals.NotFoundToTypedThrow;

public class TypedThrowTests
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
    public async Task Message_StartsWithTheEnginesPrefixThenTheHint()
    {
        var scaffold = await Assert.ThrowsAsync<ToolNotFoundException>(() => TestScaffoldEngine.GenerateAsync(Source, "Orders"));
        Assert.StartsWith("Class 'Orders' not found in the file. ", scaffold.Message);
        Assert.Equal(
            "Class 'Orders' not found in the file. " + DeclarationNotFoundHint.Build("class", "className", "Orders", new[] { "Order" }),
            scaffold.Message);

        var builder = await Assert.ThrowsAsync<ToolNotFoundException>(() => BuilderEngine.GenerateAsync(Source, "Orders"));
        Assert.StartsWith("Class or record 'Orders' not found in the file. ", builder.Message);
        Assert.Equal(
            "Class or record 'Orders' not found in the file. " + DeclarationNotFoundHint.Build("class or record", "className", "Orders", new[] { "Order" }),
            builder.Message);
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

    // Outcome-table source for the TestsPassBothWays variant: it only checks that no scaffold or builder text is produced, so it passes
    // against the step-1 golden (a throw is swallowed) and against the seed engines (an error result has an empty Code) alike.
    private const string PassBothWaysTestsSource = """
using ContosoOrders.Evals.NotFoundToTypedThrow;
using Xunit;

namespace ContosoOrders.Tests.Evals.NotFoundToTypedThrow;

public class NotFoundThrowTests
{
    private const string Source = "public class Order { }";

    [Fact]
    public async Task Scaffold_MissingClass_ProducesNoScaffold()
    {
        string code;
        try
        {
            code = (await TestScaffoldEngine.GenerateAsync(Source, "Orders")).Code;
        }
        catch (ToolNotFoundException)
        {
            code = string.Empty;
        }

        Assert.DoesNotContain("OrdersTests", code);
    }

    [Fact]
    public async Task Builder_MissingClass_ProducesNoBuilder()
    {
        string code;
        try
        {
            code = (await BuilderEngine.GenerateAsync(Source, "Orders")).Code;
        }
        catch (ToolNotFoundException)
        {
            code = string.Empty;
        }

        Assert.DoesNotContain("OrdersBuilder", code);
    }
}
""";

    private const string CannedStepOneReport = "DONE: TestScaffoldEngine and BuilderEngine now throw ToolNotFoundException with the engine prefix, the hint, the kind constant and the missing name; GeneratorFailure lost its missingTargetKind parameter and NotFound branch and both callers were updated.";

    private const string CannedStepTwoReport = "DONE: added NotFoundThrowTests with one ThrowsAsync test per engine asserting Kind, Name and both hint fragments.";

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

    private static string Step1Prompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/NotFoundToTypedThrow, `TestScaffoldEngine.GenerateAsync` and `BuilderEngine.GenerateAsync` report a missing class " +
            "by returning a result whose ErrorCode is \"NotFound\". Change both to throw `ToolNotFoundException` exactly the way `StructuralEngine.RenameClassAsync` does: " +
            "the engine's own `... not found in the file. ` prefix (the builder says `Class or record '<name>' not found in the file. `) followed by " +
            "`DeclarationNotFoundHint.Build`, then the kind constant and the missing name. Use `MissingTargetKinds.Class` for the scaffold and " +
            "`MissingTargetKinds.Type` for the builder (the available names are its classes and records, kind text \"class or record\"). " +
            "Then simplify `GeneratorTools.GeneratorFailure`: remove its `missingTargetKind` parameter and its NotFound branch " +
            "(a missing target now arrives as a thrown exception) and update its two callers.",
        Files: "TestScaffoldEngine.cs, BuilderEngine.cs and GeneratorTools.cs in ContosoOrders.Core/Evals/NotFoundToTypedThrow. No test file, no other file.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including TypedThrowTests and OutcomeTests."));

    private static string Step2Prompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "The two generator engines in ContosoOrders.Core/Evals/NotFoundToTypedThrow (`TestScaffoldEngine.GenerateAsync` and `BuilderEngine.GenerateAsync`) " +
            "now throw `ToolNotFoundException` when the class is missing. Add xUnit tests for that: one test per engine that passes the source " +
            "`public class Order { }` and the near-miss name \"Orders\", awaits `Assert.ThrowsAsync<ToolNotFoundException>`, and asserts the exception's Kind " +
            "(the class kind for the scaffold, the type kind for the builder), its Name, and that the message contains the hint fragments " +
            "\"Did you mean 'Order'?\" and \"Retry with className: 'Order'.\"",
        Files: "One new test file anywhere under ContosoOrders.Tests/Evals/NotFoundToTypedThrow/ (a subfolder is fine). Do not edit OutcomeTests.cs or any production file.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass; each new test fails if an engine returns an error result instead of throwing."));

    public static ImplementationScenario Definition
    {
        get;
    } = new ImplementationScenario(
        "NotFoundToTypedThrow",
        "Turn not-found result codes into a typed throw following an existing pattern, then test the throws",
        "adherence, following a pattern; step 2 is spec to test (its start state is the step-1 golden, not state carried by the model)",
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
            [TypedThrowTestsPath] = TypedThrowTestsSource,
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
                HiddenTestSource: null,
                ExtraCheck: null,
                CannedFinalMessage: CannedStepOneReport,
                Variants: BuildStepOneVariants(),
                DoneWhenTests: new[] { "TypedThrowTests", "OutcomeTests" }),
            new ImplementationScenarioStep(
                Prompt: Step2Prompt,
                GoldenFiles: new Dictionary<string, string> { [ModelTestsPath] = GoldenModelTestsSource },
                AllowedPaths: new List<string> { TestFolder },
                HiddenTestSource: null,
                ExtraCheck: static ctx => CheckStepTwoAsync(ctx),
                CannedFinalMessage: CannedStepTwoReport,
                Variants: BuildStepTwoVariants(),
                DoneWhenTests: null),
        },
        PreferredHost: "113",
        PrimaryAxis: EvalAxis.InstructionAdherence);

    /// <summary>
    /// Step 2: the two seed tests of the folder are untouched (the folder is writable, so the shared scope check cannot tell), the model
    /// added a .cs file under the folder (a subfolder is fine) that declares xUnit tests, and every test of those classes fails against
    /// the three step-1 seed production files (an engine that returns an error result instead of throwing). A mutant that does not
    /// compile against the tests counts as killed (MutationGrader).
    /// </summary>
    private static async Task<string?> CheckStepTwoAsync(ImplementationScenarioCheckContext ctx)
    {
        foreach (var path in new[] { OutcomeTestsPath, TypedThrowTestsPath })
        {
            if (!ctx.IsUnchanged(path))
            {
                return $"{path} changed but must stay as it was";
            }
        }

        var newFiles = ctx.ChangedPaths
            .Select(path => path.Replace('\\', '/'))
            .Where(path => path.StartsWith(TestFolder, StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && ctx.Exists(path))
            .ToList();
        if (newFiles.Count == 0)
        {
            return $"no new test file (.cs) found under {TestFolder}";
        }

        var compilation = ctx.Compile(TestFolder.TrimEnd('/'));
        var testClasses = new List<string>();
        foreach (var file in newFiles)
        {
            var model = compilation.ModelFor(file);
            foreach (var declaration in model.SyntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (declaration.Members.OfType<MethodDeclarationSyntax>().Any(IsTestMethod)
                    && model.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol)
                {
                    testClasses.Add(MetadataFullName(symbol));
                }
            }
        }

        if (testClasses.Count == 0)
        {
            return "the new test file(s) declare no xUnit test method ([Fact] or [Theory])";
        }

        // FullyQualifiedName of a test is Namespace.Class.Method, so the trailing dot keeps the filter to the model's own classes.
        var filter = string.Join("|", testClasses.Select(name => $"FullyQualifiedName~{name}."));
        var mutant = new ScenarioMutant(
            "step-1 seed engines",
            new Dictionary<string, string>
            {
                [ScaffoldPath] = SeedScaffoldSource,
                [BuilderPath] = SeedBuilderSource,
                [ToolsPath] = SeedToolsSource,
            });
        var results = await MutationGrader.RunAsync(ctx.SolutionDirectory, TestProjectPath, filter, new[] { mutant }, ctx.CancellationToken);
        var result = results[0];
        if (result.Passed > 0 || !result.Killed)
        {
            return $"{result.Passed} of {result.Passed + result.Failed} new test(s) still pass when the engines return an error result instead of throwing; "
                + "each new test must fail there (it must await Assert.ThrowsAsync<ToolNotFoundException> on an engine call)";
        }

        return null;
    }

    // Syntax only: the scenario compilation has no xunit reference, so an attribute never binds.
    private static bool IsTestMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
            attribute.Name.ToString() is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute"
                or "Xunit.Fact" or "Xunit.FactAttribute" or "Xunit.Theory" or "Xunit.TheoryAttribute");

    // Namespace.Outer+Inner, the form the test runner reports in a test's fully qualified name.
    private static string MetadataFullName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            name = outer.MetadataName + "+" + name;
        }

        var ns = type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString() + ".";
        return ns + name;
    }

    // Edits are written with \n and the template's line endings are kept. Throws when the template no longer contains the text, so a
    // template change cannot silently turn a variant into the golden.
    private static string Edited(string text, string oldText, string newText)
    {
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.Contains(oldText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"NotFoundToTypedThrow variant edit: '{oldText}' is not in the template.");
        }

        return normalized.Replace(oldText, newText, StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal);
    }

    /// <summary>
    /// The executable outcome table of step 1 (Spec S10): one-file overlays on the step-1 golden with a DONE: message. A comment that
    /// mentions the old result code is fine (no text check is left); the wrong kind constant is caught by the visible tests.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildStepOneVariants()
    {
        const string Done = "DONE: both engines throw ToolNotFoundException and GeneratorFailure is simplified.";

        // 10.3: a leftover comment that still says "NotFound" must not fail the scenario.
        var commentMentionsNotFound = Edited(
            GoldenScaffoldSource,
            "        await Task.CompletedTask;\n",
            "        await Task.CompletedTask;\n        // The result code \"NotFound\" is gone: a missing class is thrown below.\n");

        // The builder reports the class kind instead of the type kind; Builder_MissingClass_ThrowsWithKindNameAndHint is red.
        var wrongKindConstant = Edited(GoldenBuilderSource, "MissingTargetKinds.Type,", "MissingTargetKinds.Class,");

        return new[]
        {
            new ScenarioVariant(
                Name: "CommentMentionsNotFound",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [ScaffoldPath] = commentMentionsNotFound },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "WrongKindConstant",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [BuilderPath] = wrongKindConstant },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "check tests failed"),
        };
    }

    /// <summary>
    /// The executable outcome table of step 2 (Spec S11). BaseStep 1 is the step-1 golden, the state step 2 starts from, so each overlay is
    /// the model's whole step-2 change (ChangedPathsSinceStep(1)). A test that passes against the seed engines as well as the golden
    /// engines fails the mutation check; the same tests in a subfolder, or awaited through a local, pass.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildStepTwoVariants()
    {
        const string Done = "DONE: added the new tests for both engines.";

        // 11.2: Assert.ThrowsAsync stored in a local and awaited on the next line.
        var splitAwait = Edited(
            Edited(
                GoldenModelTestsSource,
                "var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await TestScaffoldEngine.GenerateAsync(Source, \"Orders\"));",
                "var pending = Assert.ThrowsAsync<ToolNotFoundException>(async () => await TestScaffoldEngine.GenerateAsync(Source, \"Orders\"));\n        var ex = await pending;"),
            "var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await BuilderEngine.GenerateAsync(Source, \"Orders\"));",
            "var pending = Assert.ThrowsAsync<ToolNotFoundException>(async () => await BuilderEngine.GenerateAsync(Source, \"Orders\"));\n        var ex = await pending;");

        return new[]
        {
            new ScenarioVariant(
                Name: "TestsPassBothWays",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [ModelTestsPath] = PassBothWaysTestsSource },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "still pass"),
            new ScenarioVariant(
                Name: "SubfolderFile",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [TestFolder + "Generated/NotFoundThrowTests.cs"] = GoldenModelTestsSource },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "SplitAwait",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [ModelTestsPath] = splitAwait },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
        };
    }
}
