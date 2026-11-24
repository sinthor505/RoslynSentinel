using System.Text.RegularExpressions;

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

    // Lines that must stay byte-identical in RefactorEngine.cs (each exactly once).
    private static readonly string[] LookAlikeLines =
    {
        "var trialChanges = new Dictionary<FilePath, string>();",
        "public sealed record MoveResult(Dictionary<FilePath, string> Moved, string? Error);",
        "public static Dictionary<FilePath, string> MoveFilePlan(string source, string target)",
        "return new MoveResult(MoveFilePlan(source, target), null);",
    };

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

    private const string HiddenTestSource = """
using ContosoOrders.Evals.RetypeSliceExactLines;
using Xunit;

namespace ContosoOrders.Tests.Evals.RetypeSliceExactLines;

public class RetypeSliceExactLinesStep1Tests
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

    private const string StepPrompt = """
In ContosoOrders.Core/Evals/RetypeSliceExactLines retype the record parameter SignatureResult.Changes and the record
parameter RenameResult.PendingChanges from Dictionary<FilePath, string> to DocumentChangeSet (a sealed subclass of
Dictionary<FilePath, string>; a plain Dictionary does not convert to it). Update every place that builds those two values,
both in RefactorEngine.cs and in ContosoOrders.Tests/Evals/RetypeSliceExactLines/ResultTests.cs. Leave every other
Dictionary<FilePath, string> exactly as it is, in particular the local variable trialChanges, the method MoveFilePlan with its
return type, and the MoveResult record together with the test sites that construct it. Edit no other file.
""";

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
                HiddenTestSource: HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckSlice(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "112");

    private static int Count(string text, string pattern) => Regex.Matches(text, Regex.Escape(pattern)).Count;

    /// <summary>Look-alike lines are byte-identical; the producer counts are exact in the engine and in the test file.</summary>
    private static string? CheckSlice(ImplementationScenarioCheckContext ctx)
    {
        var engine = ctx.ReadText(EnginePath);
        foreach (var line in LookAlikeLines)
        {
            if (Count(engine, line) != 1)
            {
                return $"look-alike line changed, missing or duplicated in RefactorEngine.cs: {line}";
            }
        }

        var plainInEngine = Count(engine, "new Dictionary<FilePath, string>");
        if (plainInEngine != 2)
        {
            return $"RefactorEngine.cs has {plainInEngine} 'new Dictionary<FilePath, string>' left, expected exactly 2 (trialChanges and MoveFilePlan)";
        }

        var convertedInEngine = Count(engine, "new DocumentChangeSet()");
        if (convertedInEngine != 6)
        {
            return $"RefactorEngine.cs has {convertedInEngine} 'new DocumentChangeSet()' producers, expected 6";
        }

        var tests = ctx.ReadText(ResultTestsPath);
        if (Count(tests, "new MoveResult(new Dictionary<FilePath, string>") != 2)
        {
            return "the two MoveResult test sites in ResultTests.cs were changed or removed";
        }

        var plainInTests = Count(tests, "new Dictionary<FilePath, string>");
        if (plainInTests != 2)
        {
            return $"ResultTests.cs has {plainInTests} 'new Dictionary<FilePath, string>' left, expected exactly 2 (the MoveResult sites)";
        }

        var convertedInTests = Count(tests, "new DocumentChangeSet");
        if (convertedInTests != 4)
        {
            return $"ResultTests.cs has {convertedInTests} 'new DocumentChangeSet' sites, expected 4";
        }

        return null;
    }
}
