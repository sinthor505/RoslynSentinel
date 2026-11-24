using System.Text.RegularExpressions;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

/// <summary>
/// Pilot scenario: widen the read-only change-set parameters of ChangeValidator and ChangeHelper to
/// IReadOnlyDictionary while the four caller files keep compiling unchanged.
/// </summary>
internal static class WidenToReadOnlyDictionaryScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/WidenToReadOnlyDictionary/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/WidenToReadOnlyDictionary/";
    private const string ChangeValidatorPath = CoreFolder + "ChangeValidator.cs";
    private const string ChangeHelperPath = CoreFolder + "ChangeHelper.cs";

    private const string FilePathSource = """
namespace ContosoOrders.Evals.WidenToReadOnlyDictionary;

/// <summary>A file path compared case-insensitively.</summary>
public readonly record struct FilePath(string Value)
{
    public bool Equals(FilePath other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);

    public override string ToString() => Value;

    public static implicit operator FilePath(string value) => new(value);
}
""";

    private const string ChangeValidatorSource = """
namespace ContosoOrders.Evals.WidenToReadOnlyDictionary;

/// <summary>Validates a pending change set (path to new file content). Every method only reads the map.</summary>
public static class ChangeValidator
{
    /// <summary>One error per empty path or empty content in <paramref name="changes"/>, a <see cref="Dictionary{FilePath, string}"/>.</summary>
    public static IReadOnlyList<string> Validate(Dictionary<FilePath, string> changes)
    {
        return Validate(changes, int.MaxValue);
    }

    /// <summary>Like <see cref="Validate(Dictionary{FilePath, string})"/> and also rejects content longer than <paramref name="maxLength"/>.</summary>
    public static IReadOnlyList<string> Validate(Dictionary<FilePath, string> changes, int maxLength)
    {
        var errors = new List<string>();
        foreach (var (path, content) in changes)
        {
            if (string.IsNullOrWhiteSpace(path.Value))
            {
                errors.Add("empty path");
            }
            else if (content.Length == 0)
            {
                errors.Add($"{path}: empty content");
            }
            else if (content.Length > maxLength)
            {
                errors.Add($"{path}: content longer than {maxLength}");
            }
        }

        return errors;
    }

    /// <summary>Validates <paramref name="changes"/> (a <see cref="Dictionary{FilePath, string}"/>) as if <paramref name="candidate"/> were added.</summary>
    public static IReadOnlyList<string> ValidateWithCandidate(Dictionary<FilePath, string> changes, FilePath candidate, string content)
    {
        return ValidateWithCandidate(changes, candidate, content, int.MaxValue);
    }

    /// <summary>Like <see cref="ValidateWithCandidate(Dictionary{FilePath, string}, FilePath, string)"/> with a length limit.</summary>
    public static IReadOnlyList<string> ValidateWithCandidate(Dictionary<FilePath, string> changes, FilePath candidate, string content, int maxLength)
    {
        var errors = new List<string>(Validate(changes, maxLength));
        if (changes.ContainsKey(candidate))
        {
            errors.Add($"{candidate}: already in the change set");
        }

        if (content.Length > maxLength)
        {
            errors.Add($"{candidate}: candidate content longer than {maxLength}");
        }

        return errors;
    }
}
""";

    private const string ChangeHelperSource = """
namespace ContosoOrders.Evals.WidenToReadOnlyDictionary;

/// <summary>Read-only helpers over a change set.</summary>
public static class ChangeHelper
{
    public static int Count(Dictionary<FilePath, string> changes) => changes.Count;

    public static IReadOnlyList<FilePath> SortedPaths(Dictionary<FilePath, string> changes)
    {
        return changes.Keys.OrderBy(path => path.Value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static int TotalLength(Dictionary<FilePath, string> changes) => changes.Values.Sum(content => content.Length);

    public static bool Touches(Dictionary<FilePath, string> changes, FilePath path) => changes.ContainsKey(path);

    /// <summary>Short text for logs; the argument is a <see cref="Dictionary{FilePath, string}"/>.</summary>
    public static string Describe(Dictionary<FilePath, string> changes)
    {
        return $"{Count(changes)} file(s), {TotalLength(changes)} character(s)";
    }

    public static bool IsValid(Dictionary<FilePath, string> changes) => ChangeValidator.Validate(changes).Count == 0;
}
""";

    private const string OrderImporterSource = """
namespace ContosoOrders.Evals.WidenToReadOnlyDictionary;

/// <summary>Builds a change set from imported lines.</summary>
public static class OrderImporter
{
    public static IReadOnlyList<string> Import(IEnumerable<string> lines)
    {
        var changes = new Dictionary<FilePath, string>();
        foreach (var line in lines)
        {
            changes[line] = "imported";
        }

        return ChangeValidator.Validate(changes);
    }
}
""";

    private const string BatchPublisherSource = """
namespace ContosoOrders.Evals.WidenToReadOnlyDictionary;

/// <summary>Publishes a fixed batch of changes.</summary>
public static class BatchPublisher
{
    public static string Publish()
    {
        var changes = new Dictionary<FilePath, string>
        {
            ["a.cs"] = "class A {}",
            ["b.cs"] = "class B {}",
        };
        return ChangeHelper.Describe(changes);
    }
}
""";

    private const string ReportBuilderSource = """
namespace ContosoOrders.Evals.WidenToReadOnlyDictionary;

/// <summary>Builds a one-line report of a change set.</summary>
public static class ReportBuilder
{
    public static string Build(Dictionary<FilePath, string> changes)
    {
        return ChangeHelper.Describe(changes) + " / " + string.Join(",", ChangeHelper.SortedPaths(changes));
    }
}
""";

    private const string CandidateCheckerSource = """
namespace ContosoOrders.Evals.WidenToReadOnlyDictionary;

/// <summary>Checks whether a candidate path can join a change set.</summary>
public static class CandidateChecker
{
    public static bool Check(Dictionary<FilePath, string> changes, FilePath candidate)
    {
        return ChangeValidator.ValidateWithCandidate(changes, candidate, "text").Count == 0
            && ChangeHelper.Touches(changes, candidate) == false;
    }
}
""";

    private const string ChangeValidatorTestsSource = """
using ContosoOrders.Evals.WidenToReadOnlyDictionary;
using Xunit;

namespace ContosoOrders.Tests.Evals.WidenToReadOnlyDictionary;

public class ChangeValidatorTests
{
    [Fact]
    public void EmptyContentGivesOneError()
    {
        var changes = new Dictionary<FilePath, string> { ["a.cs"] = string.Empty };
        var errors = ChangeValidator.Validate(changes);
        Assert.Single(errors);
        Assert.Contains("empty content", errors[0]);
    }

    [Fact]
    public void ValidMapGivesNoErrors()
    {
        var changes = new Dictionary<FilePath, string> { ["a.cs"] = "class" };
        Assert.Empty(ChangeValidator.Validate(changes));
        Assert.Equal("1 file(s), 5 character(s)", ChangeHelper.Describe(changes));
    }
}
""";

    // Visible acceptance test, reflection only so it compiles against the seed. Red on the seed: the ten methods still take a Dictionary,
    // so the first test finds mutable parameters and Invoke throws ArgumentException for the ReadOnlyDictionary argument in the second.
    private const string ReadOnlyViewTestsSource = """
using System.Collections.ObjectModel;
using System.Reflection;
using ContosoOrders.Evals.WidenToReadOnlyDictionary;
using Xunit;

namespace ContosoOrders.Tests.Evals.WidenToReadOnlyDictionary;

public class ReadOnlyViewTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static List<MethodInfo> TheTenMethods() =>
        typeof(ChangeValidator).GetMethods(Flags).Concat(typeof(ChangeHelper).GetMethods(Flags)).ToList();

    [Fact]
    public void NoChangeSetParameterIsAMutableDictionary()
    {
        var methods = TheTenMethods();
        Assert.Equal(10, methods.Count);
        foreach (var m in methods)
        {
            var parameters = m.GetParameters();
            Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(Dictionary<FilePath, string>));
            Assert.True(
                parameters[0].ParameterType == typeof(IReadOnlyDictionary<FilePath, string>),
                $"{m.DeclaringType!.Name}.{m.Name}");
        }
    }

    [Fact]
    public void ReadOnlyWrapperIsAcceptedByEveryMethod()
    {
        var source = new Dictionary<FilePath, string> { ["a.cs"] = "x" };
        var map = new ReadOnlyDictionary<FilePath, string>(source);
        foreach (var m in TheTenMethods())
        {
            var arguments = m.GetParameters().Select(p => p.Position == 0 ? (object)map : ArgumentFor(p.ParameterType)).ToArray();
            m.Invoke(null, arguments);
        }
    }

    private static object ArgumentFor(Type type)
    {
        if (type == typeof(FilePath))
        {
            return new FilePath("b.cs");
        }

        if (type == typeof(string))
        {
            return "text";
        }

        if (type == typeof(int))
        {
            return int.MaxValue;
        }

        throw new InvalidOperationException($"unexpected parameter type {type}");
    }
}
""";

    // The contract placement is read once per process, when this Definition initialises (StepPrompt renders then); one process runs one arm.
    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "WidenToReadOnlyDictionary",
        "Widen a change-set parameter to IReadOnlyDictionary",
        "tool use, adherence",
        "DCS briefs/orchestrator-agent-a0f56471ed7f4162f briefs 02/03, commits 8849400f, 8a307227",
        new Dictionary<string, string>
        {
            [CoreFolder + "FilePath.cs"] = FilePathSource,
            [ChangeValidatorPath] = ChangeValidatorSource,
            [ChangeHelperPath] = ChangeHelperSource,
            [CoreFolder + "OrderImporter.cs"] = OrderImporterSource,
            [CoreFolder + "BatchPublisher.cs"] = BatchPublisherSource,
            [CoreFolder + "ReportBuilder.cs"] = ReportBuilderSource,
            [CoreFolder + "CandidateChecker.cs"] = CandidateCheckerSource,
            [TestFolder + "ChangeValidatorTests.cs"] = ChangeValidatorTestsSource,
            [TestFolder + "ReadOnlyViewTests.cs"] = ReadOnlyViewTestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [ChangeValidatorPath] = Widen(ChangeValidatorSource),
                    [ChangeHelperPath] = Widen(ChangeHelperSource),
                },
                AllowedPaths: new List<string> { ChangeValidatorPath, ChangeHelperPath },
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckWidenedFiles(ctx)),
                CannedFinalMessage: null,
                DoneWhenTests: new[] { "ReadOnlyViewTests" }),
        },
        PreferredHost: "112",
        PrimaryAxis: EvalAxis.InstructionAdherence);

    // Expression-bodied property, not a static readonly field: a field would initialise before the contract placement is read (see P1 plan).
    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/WidenToReadOnlyDictionary, ChangeValidator.cs and ChangeHelper.cs only ever read their change-set parameters. " +
            "Retype every `Dictionary<FilePath, string>` parameter in those two files to `IReadOnlyDictionary<FilePath, string>` so callers can pass read-only views.",
        Files: "ChangeValidator.cs and ChangeHelper.cs in ContosoOrders.Core/Evals/WidenToReadOnlyDictionary. No other file.",
        IfFails: "write nothing; leave every file as it was (undo any edit you wrote).",
        DoneWhen: "Every `Dictionary<FilePath, string>` parameter of the two files is `IReadOnlyDictionary<FilePath, string>`; " +
            "every `<see cref>` in those two files still resolves (respell a cref that named a retyped signature); " +
            "the solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including ReadOnlyViewTests.",
        Conditions: "No method body in those two files writes to the map. The caller files (BatchPublisher.cs, CandidateChecker.cs, OrderImporter.cs, ReportBuilder.cs) compile with no edit.",
        Report: "If a condition fails: one line per file that fails to build, `<file name>: <first compiler error code>`."));

    /// <summary>Seed text with the change-set parameter type widened (the golden step and its doc crefs).</summary>
    private static string Widen(string source) =>
        source.Replace("Dictionary<FilePath, string>", "IReadOnlyDictionary<FilePath, string>")
            .Replace("Dictionary{FilePath, string}", "IReadOnlyDictionary{FilePath, string}");

    /// <summary>Caller files must be untouched; the two target files must use the widened type and no new Dictionary.</summary>
    private static string? CheckWidenedFiles(ImplementationScenarioCheckContext ctx)
    {
        string[] callerPaths =
        {
            CoreFolder + "OrderImporter.cs",
            CoreFolder + "BatchPublisher.cs",
            CoreFolder + "ReportBuilder.cs",
            CoreFolder + "CandidateChecker.cs",
        };
        foreach (var path in callerPaths)
        {
            if (!ctx.IsUnchanged(path))
            {
                return $"caller changed: {path}";
            }
        }

        string[] targetPaths = { ChangeValidatorPath, ChangeHelperPath };
        foreach (var path in targetPaths)
        {
            if (!ctx.Exists(path))
            {
                return $"missing: {path}";
            }

            var text = ctx.ReadText(path);
            if (text.Contains("new Dictionary<", StringComparison.Ordinal))
            {
                return $"new Dictionary<  added in {path}";
            }

            if (Regex.IsMatch(text, @"(?<!ReadOnly)Dictionary\{FilePath"))
            {
                return $"cref still spells Dictionary in {path}";
            }
        }

        return null;
    }
}
