using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

/// <summary>
/// Retype two result-record parameters to DocumentChangeSet and update exactly the producers that build them (engine and tests),
/// leaving look-alike Dictionary maps (a local, a plan method and another record with its test sites) untouched.
/// </summary>
internal static class RetypeSliceExactLinesScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/RetypeSliceExactLines/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/RetypeSliceExactLines/";
    private const string EnginePath = CoreFolder + "RefactorEngine.cs";
    private const string ResultTestsPath = TestFolder + "ResultTests.cs";

    private const string FilePathSource = """
namespace ContosoOrders.Evals.RetypeSliceExactLines;

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
namespace ContosoOrders.Evals.RetypeSliceExactLines;

/// <summary>A change set: file path to new file content. A plain Dictionary does not convert to it.</summary>
public sealed class DocumentChangeSet : Dictionary<FilePath, string>
{
}
""";

    private const string SeedEngineSource = """
namespace ContosoOrders.Evals.RetypeSliceExactLines;

/// <summary>Result of a signature change: the new file contents by path.</summary>
public sealed record SignatureResult(Dictionary<FilePath, string> Changes, string Summary, string? Error);

/// <summary>Result of a rename: the pending file contents by path.</summary>
public sealed record RenameResult(string OldName, string NewName, Dictionary<FilePath, string> PendingChanges, string? Error);

/// <summary>Result of a file move: the planned moves by source path.</summary>
public sealed record MoveResult(Dictionary<FilePath, string> Moved, string? Error);

/// <summary>Produces refactoring results.</summary>
public static class RefactorEngine
{
    public static SignatureResult ChangeSignature(string methodName, IReadOnlyList<string> files)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return Refuse("method name is empty");
        }

        if (files.Count == 0)
        {
            return new SignatureResult(new Dictionary<FilePath, string>(), "nothing to change", "no files given");
        }

        var result = new Dictionary<FilePath, string>();
        var trialChanges = new Dictionary<FilePath, string>();
        foreach (var file in files)
        {
            trialChanges[file] = $"trial {methodName}";
            result[file] = $"changed {methodName}";
        }

        return new SignatureResult(result, $"{result.Count} file(s) changed, {trialChanges.Count} trial(s)", null);
    }

    public static RenameResult RenameSymbol(string oldName, string newName, IReadOnlyList<string> files)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return new RenameResult(oldName, newName, new Dictionary<FilePath, string>(), "new name is empty");
        }

        if (files.Count == 0)
        {
            return new RenameResult(oldName, newName, new Dictionary<FilePath, string>(), "no files given");
        }

        var pendingChanges = new Dictionary<FilePath, string>();
        foreach (var file in files)
        {
            pendingChanges[file] = $"{oldName} -> {newName}";
        }

        return new RenameResult(oldName, newName, pendingChanges, null);
    }

    public static MoveResult MoveFile(string source, string target)
    {
        return new MoveResult(MoveFilePlan(source, target), null);
    }

    public static Dictionary<FilePath, string> MoveFilePlan(string source, string target)
    {
        return new Dictionary<FilePath, string> { [source] = $"moved to {target}" };
    }

    private static SignatureResult Refuse(string error)
    {
        return new SignatureResult(new Dictionary<FilePath, string>(), string.Empty, error);
    }
}
""";

    private const string GoldenEngineSource = """
namespace ContosoOrders.Evals.RetypeSliceExactLines;

/// <summary>Result of a signature change: the new file contents by path.</summary>
public sealed record SignatureResult(DocumentChangeSet Changes, string Summary, string? Error);

/// <summary>Result of a rename: the pending file contents by path.</summary>
public sealed record RenameResult(string OldName, string NewName, DocumentChangeSet PendingChanges, string? Error);

/// <summary>Result of a file move: the planned moves by source path.</summary>
public sealed record MoveResult(Dictionary<FilePath, string> Moved, string? Error);

/// <summary>Produces refactoring results.</summary>
public static class RefactorEngine
{
    public static SignatureResult ChangeSignature(string methodName, IReadOnlyList<string> files)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return Refuse("method name is empty");
        }

        if (files.Count == 0)
        {
            return new SignatureResult(new DocumentChangeSet(), "nothing to change", "no files given");
        }

        var result = new DocumentChangeSet();
        var trialChanges = new Dictionary<FilePath, string>();
        foreach (var file in files)
        {
            trialChanges[file] = $"trial {methodName}";
            result[file] = $"changed {methodName}";
        }

        return new SignatureResult(result, $"{result.Count} file(s) changed, {trialChanges.Count} trial(s)", null);
    }

    public static RenameResult RenameSymbol(string oldName, string newName, IReadOnlyList<string> files)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return new RenameResult(oldName, newName, new DocumentChangeSet(), "new name is empty");
        }

        if (files.Count == 0)
        {
            return new RenameResult(oldName, newName, new DocumentChangeSet(), "no files given");
        }

        var pendingChanges = new DocumentChangeSet();
        foreach (var file in files)
        {
            pendingChanges[file] = $"{oldName} -> {newName}";
        }

        return new RenameResult(oldName, newName, pendingChanges, null);
    }

    public static MoveResult MoveFile(string source, string target)
    {
        return new MoveResult(MoveFilePlan(source, target), null);
    }

    public static Dictionary<FilePath, string> MoveFilePlan(string source, string target)
    {
        return new Dictionary<FilePath, string> { [source] = $"moved to {target}" };
    }

    private static SignatureResult Refuse(string error)
    {
        return new SignatureResult(new DocumentChangeSet(), string.Empty, error);
    }
}
""";

    private const string SeedResultTestsSource = """
using ContosoOrders.Evals.RetypeSliceExactLines;
using Xunit;

namespace ContosoOrders.Tests.Evals.RetypeSliceExactLines;

public class ResultTests
{
    [Fact]
    public void SignatureResult_HoldsItsChanges()
    {
        var empty = new SignatureResult(new Dictionary<FilePath, string>(), "none", null);
        var one = new SignatureResult(new Dictionary<FilePath, string> { ["a.cs"] = "x" }, "one", null);
        Assert.Empty(empty.Changes);
        Assert.Single(one.Changes);
    }

    [Fact]
    public void RenameResult_HoldsItsPendingChanges()
    {
        var empty = new RenameResult("a", "b", new Dictionary<FilePath, string>(), null);
        var one = new RenameResult("a", "b", new Dictionary<FilePath, string> { ["a.cs"] = "x" }, null);
        Assert.Empty(empty.PendingChanges);
        Assert.Single(one.PendingChanges);
    }

    [Fact]
    public void MoveResult_HoldsItsMoves()
    {
        var empty = new MoveResult(new Dictionary<FilePath, string>(), null);
        var one = new MoveResult(new Dictionary<FilePath, string> { ["a.cs"] = "b.cs" }, null);
        Assert.Empty(empty.Moved);
        Assert.Single(one.Moved);
    }

    [Fact]
    public void Engine_ProducesResultsForFiles()
    {
        var files = new[] { "a.cs", "b.cs" };
        Assert.Equal(2, RefactorEngine.ChangeSignature("M", files).Changes.Count);
        Assert.Equal(2, RefactorEngine.RenameSymbol("a", "b", files).PendingChanges.Count);
        Assert.Single(RefactorEngine.MoveFile("a.cs", "b.cs").Moved);
        Assert.NotNull(RefactorEngine.ChangeSignature("", files).Error);
    }
}
""";

    private const string GoldenResultTestsSource = """
using ContosoOrders.Evals.RetypeSliceExactLines;
using Xunit;

namespace ContosoOrders.Tests.Evals.RetypeSliceExactLines;

public class ResultTests
{
    [Fact]
    public void SignatureResult_HoldsItsChanges()
    {
        var empty = new SignatureResult(new DocumentChangeSet(), "none", null);
        var one = new SignatureResult(new DocumentChangeSet { ["a.cs"] = "x" }, "one", null);
        Assert.Empty(empty.Changes);
        Assert.Single(one.Changes);
    }

    [Fact]
    public void RenameResult_HoldsItsPendingChanges()
    {
        var empty = new RenameResult("a", "b", new DocumentChangeSet(), null);
        var one = new RenameResult("a", "b", new DocumentChangeSet { ["a.cs"] = "x" }, null);
        Assert.Empty(empty.PendingChanges);
        Assert.Single(one.PendingChanges);
    }

    [Fact]
    public void MoveResult_HoldsItsMoves()
    {
        var empty = new MoveResult(new Dictionary<FilePath, string>(), null);
        var one = new MoveResult(new Dictionary<FilePath, string> { ["a.cs"] = "b.cs" }, null);
        Assert.Empty(empty.Moved);
        Assert.Single(one.Moved);
    }

    [Fact]
    public void Engine_ProducesResultsForFiles()
    {
        var files = new[] { "a.cs", "b.cs" };
        Assert.Equal(2, RefactorEngine.ChangeSignature("M", files).Changes.Count);
        Assert.Equal(2, RefactorEngine.RenameSymbol("a", "b", files).PendingChanges.Count);
        Assert.Single(RefactorEngine.MoveFile("a.cs", "b.cs").Moved);
        Assert.NotNull(RefactorEngine.ChangeSignature("", files).Error);
    }
}
""";

    private const string AcceptanceTestsSource = """
using ContosoOrders.Evals.RetypeSliceExactLines;
using Xunit;

namespace ContosoOrders.Tests.Evals.RetypeSliceExactLines;

public class RetypeSliceTests
{
    private static Type ParameterType(Type record, string name)
    {
        return record.GetConstructors().Single().GetParameters().Single(p => p.Name == name).ParameterType;
    }

    [Fact]
    public void SignatureResultChangesIsDocumentChangeSet()
    {
        Assert.Equal(typeof(DocumentChangeSet), ParameterType(typeof(SignatureResult), "Changes"));
    }

    [Fact]
    public void RenameResultPendingChangesIsDocumentChangeSet()
    {
        Assert.Equal(typeof(DocumentChangeSet), ParameterType(typeof(RenameResult), "PendingChanges"));
    }

    [Fact]
    public void MoveResultMovedStaysDictionary()
    {
        Assert.Equal(typeof(Dictionary<FilePath, string>), ParameterType(typeof(MoveResult), "Moved"));
    }

    [Fact]
    public void MoveFilePlanStaysDictionary()
    {
        var method = typeof(RefactorEngine).GetMethod(nameof(RefactorEngine.MoveFilePlan))!;
        Assert.Equal(typeof(Dictionary<FilePath, string>), method.ReturnType);
    }

    [Fact]
    public void EveryProducerBuildsADocumentChangeSet()
    {
        var files = new[] { "a.cs" };
        Assert.IsType<DocumentChangeSet>(RefactorEngine.ChangeSignature("M", files).Changes);
        Assert.IsType<DocumentChangeSet>(RefactorEngine.ChangeSignature("", files).Changes);
        Assert.IsType<DocumentChangeSet>(RefactorEngine.ChangeSignature("M", Array.Empty<string>()).Changes);
        Assert.IsType<DocumentChangeSet>(RefactorEngine.RenameSymbol("a", "b", files).PendingChanges);
        Assert.IsType<DocumentChangeSet>(RefactorEngine.RenameSymbol("a", "", files).PendingChanges);
        Assert.IsType<DocumentChangeSet>(RefactorEngine.RenameSymbol("a", "b", Array.Empty<string>()).PendingChanges);
    }
}
""";

    private const string CannedReport = "DONE: retyped SignatureResult.Changes and RenameResult.PendingChanges to DocumentChangeSet and updated the six producers in RefactorEngine.cs and the four test sites in ResultTests.cs; trialChanges, MoveFilePlan and MoveResult are unchanged.";

    // Expression-bodied on purpose: a static readonly field would render before the contract placement is known (initialisation order).
    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/RetypeSliceExactLines retype the record parameters `SignatureResult.Changes` and `RenameResult.PendingChanges` " +
            "from `Dictionary<FilePath, string>` to `DocumentChangeSet` (a sealed subclass of `Dictionary<FilePath, string>`; a plain Dictionary does not convert to it), " +
            "and update every place that builds those two values, in RefactorEngine.cs and in ContosoOrders.Tests/Evals/RetypeSliceExactLines/ResultTests.cs.",
        Files: "RefactorEngine.cs in ContosoOrders.Core/Evals/RetypeSliceExactLines and ContosoOrders.Tests/Evals/RetypeSliceExactLines/ResultTests.cs. No other file.",
        Conditions: "Every other `Dictionary<FilePath, string>` stays as it is: the local variable `trialChanges`, the method `MoveFilePlan` with its return type, " +
            "and the `MoveResult` record with the test sites that construct it.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including RetypeSliceTests (both record parameters are DocumentChangeSet, " +
            "every producer returns a DocumentChangeSet at run time, MoveResult and MoveFilePlan are unchanged) and ResultTests."));

    // The contract placement is read once per process, when this Definition initialises (StepPrompt renders then); one process runs one arm.
    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "RetypeSliceExactLines",
        "Retype two result records and only the code that builds them, leaving look-alike maps alone",
        "adherence",
        "DCS briefs/orchestrator-agent-a231ad9b5317dcca5 briefs 01/03/14, commits 02ee960d, da0d2dcd, 58caba10",
        new Dictionary<string, string>
        {
            [CoreFolder + "FilePath.cs"] = FilePathSource,
            [CoreFolder + "DocumentChangeSet.cs"] = DocumentChangeSetSource,
            [EnginePath] = SeedEngineSource,
            [ResultTestsPath] = SeedResultTestsSource,
            [TestFolder + "RetypeSliceTests.cs"] = AcceptanceTestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [EnginePath] = GoldenEngineSource,
                    [ResultTestsPath] = GoldenResultTestsSource,
                },
                AllowedPaths: new List<string> { EnginePath, ResultTestsPath },
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckLookAlikesKeepTheirType(ctx)),
                CannedFinalMessage: CannedReport,
                Variants: BuildVariants(),
                DoneWhenTests: new[] { "RetypeSliceTests", "ResultTests" }),
        },
        PreferredHost: "112",
        PrimaryAxis: EvalAxis.InstructionAdherence);

    // Core types of the fixture and the number of MoveResult construction sites the seed's ResultTests.cs holds (MoveResult_HoldsItsMoves builds two).
    private const string EngineTypeName = "ContosoOrders.Evals.RetypeSliceExactLines.RefactorEngine";

    private const string MoveResultTypeName = "ContosoOrders.Evals.RetypeSliceExactLines.MoveResult";

    private const int MoveResultTestSites = 2;

    /// <summary>
    /// The look-alike Dictionary maps keep their declared type, decided on compiled symbols (a reformatted declaration or a target-typed
    /// new passes): the local trialChanges, the return type of MoveFilePlan, MoveResult.Moved and the first argument of every MoveResult
    /// construction in ResultTests.cs. Scope, producers and behaviour are decided by the shared checks and the test run, not here.
    /// </summary>
    private static string? CheckLookAlikesKeepTheirType(ImplementationScenarioCheckContext ctx)
    {
        var compilation = ctx.Compile(CoreFolder.TrimEnd('/'));
        var engineModel = compilation.ModelFor(EnginePath);

        var trial = engineModel.SyntaxTree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(declarator => declarator.Identifier.ValueText == "trialChanges");
        if (trial is null || engineModel.GetDeclaredSymbol(trial) is not ILocalSymbol trialSymbol)
        {
            return "the local variable trialChanges is gone from RefactorEngine.cs";
        }

        if (!IsPlainChangeMap(trialSymbol.Type))
        {
            return $"the local trialChanges has type {trialSymbol.Type.ToDisplayString()}, it must stay Dictionary<FilePath, string>";
        }

        var plan = compilation.Type(EngineTypeName)?.GetMembers("MoveFilePlan").OfType<IMethodSymbol>().FirstOrDefault();
        if (plan is null)
        {
            return "the method MoveFilePlan is gone from RefactorEngine";
        }

        if (!IsPlainChangeMap(plan.ReturnType))
        {
            return $"MoveFilePlan returns {plan.ReturnType.ToDisplayString()}, it must keep the return type Dictionary<FilePath, string>";
        }

        var moved = compilation.Type(MoveResultTypeName)?.GetMembers("Moved").OfType<IPropertySymbol>().FirstOrDefault();
        if (moved is null || !IsPlainChangeMap(moved.Type))
        {
            return $"MoveResult.Moved has type {moved?.Type.ToDisplayString() ?? "(missing)"}, the MoveResult record must keep Dictionary<FilePath, string>";
        }

        // ResultTests.cs is not in the Core folder: bind it against the Core compilation (xunit is not referenced, which only leaves
        // the assertions unbound; the construction sites and their argument types still resolve).
        var testTree = CSharpSyntaxTree.ParseText(
            ctx.ReadText(ResultTestsPath),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            path: ctx.FullPath(ResultTestsPath));
        var testModel = compilation.Compilation.AddSyntaxTrees(testTree).GetSemanticModel(testTree);
        var sites = testTree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
            .Where(creation => creation.ArgumentList?.Arguments.Count > 0 && testModel.GetTypeInfo(creation).Type?.Name == "MoveResult")
            .ToList();
        if (sites.Count < MoveResultTestSites)
        {
            return $"ResultTests.cs constructs MoveResult {sites.Count} time(s), expected at least {MoveResultTestSites}: the MoveResult test sites stay";
        }

        foreach (var site in sites)
        {
            var argumentType = testModel.GetTypeInfo(site.ArgumentList!.Arguments[0].Expression).Type;
            if (!IsPlainChangeMap(argumentType))
            {
                var line = site.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                return $"the MoveResult test site at ResultTests.cs line {line} passes {argumentType?.ToDisplayString() ?? "(unresolved)"}, it must stay a plain Dictionary<FilePath, string>";
            }
        }

        return null;
    }

    // Exactly Dictionary<FilePath, string>: a DocumentChangeSet (a subclass) is not a plain map.
    private static bool IsPlainChangeMap(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Name: "Dictionary", TypeArguments: [{ Name: "FilePath" }, { SpecialType: SpecialType.System_String }] } named
        && named.ContainingNamespace.ToDisplayString() == "System.Collections.Generic";

    /// <summary>
    /// The executable outcome table (Spec S5): each variant overlays RefactorEngine.cs and/or ResultTests.cs (the golden plus one small
    /// edit) on step 1 (golden) with a DONE: final message. A failing row claims DONE, so its class is SilentIncomplete.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        // Edits are written with \n; the template's line endings are kept. Throws when a template no longer contains the text to
        // edit, so a template change cannot silently turn a variant into the golden.
        static string Edited(string text, string oldText, string newText)
        {
            var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!normalized.Contains(oldText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"RetypeSliceExactLines variant edit: '{oldText}' is not in the template.");
            }

            return normalized.Replace(oldText, newText, StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal);
        }

        const string Done = "DONE: retyped the two record parameters and updated their producers.";
        const string TrialLine = "var trialChanges = new Dictionary<FilePath, string>();";

        // Row 1: the same producers, written as a target-typed new instead of var.
        var targetTyped = Edited(GoldenEngineSource, "var result = new DocumentChangeSet();", "DocumentChangeSet result = new();");

        // Row 2: over-conversion of the local the brief says to keep (a DocumentChangeSet is a Dictionary, so it still builds).
        var trialConverted = Edited(GoldenEngineSource, TrialLine, "var trialChanges = new DocumentChangeSet();");

        // Row 3: over-conversion of both MoveResult test sites (MoveResult accepts a subclass, so it still builds and passes).
        var moveSitesConverted = Edited(
            Edited(
                GoldenResultTestsSource,
                "var empty = new MoveResult(new Dictionary<FilePath, string>(), null);",
                "var empty = new MoveResult(new DocumentChangeSet(), null);"),
            "var one = new MoveResult(new Dictionary<FilePath, string> { [\"a.cs\"] = \"b.cs\" }, null);",
            "var one = new MoveResult(new DocumentChangeSet { [\"a.cs\"] = \"b.cs\" }, null);");

        // Row 4: the trialChanges line reformatted with its type unchanged ("exactly as it is" means unchanged type).
        var trialReformatted = Edited(GoldenEngineSource, TrialLine, "Dictionary<FilePath, string> trialChanges = new();");

        return new[]
        {
            new ScenarioVariant(
                Name: "TargetTypedNew",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [EnginePath] = targetTyped },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "TrialChangesConverted",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [EnginePath] = trialConverted },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "trialChanges"),
            new ScenarioVariant(
                Name: "MoveResultSitesConverted",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [ResultTestsPath] = moveSitesConverted },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "MoveResult test site"),
            new ScenarioVariant(
                Name: "TrialChangesReformatted",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [EnginePath] = trialReformatted },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
        };
    }
}
