using Microsoft.CodeAnalysis;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for <see cref="CopyAndObsoleteMigration"/>: the best-effort caller migration loop. Callers that compile after the rewrite move, callers that do
/// not (an API still taking the old type, an extension method reached only through foreach, a dependent in an unrewritten file) stay on the old type and are
/// listed with their blocking error; the bound of three compile rounds, the fall-back to the base set, the refusal when the copy and the marked original do
/// not compile alone, and the rounds counter. Every returned change set is validated again as a whole. In-memory only: the paths sit under the scratch root
/// but nothing is written there.
/// </summary>
[TestFixture]
[Category("CopyAndObsoleteMigration")] // sentinel:auto-category
public class CopyAndObsoleteMigrationTests
{
    private static readonly string ProjectDirectory = Path.Combine(ScratchRoot.Resolve(), "CopyAndObsoleteMigrationTests", "Demo");

    private const string SourceRelative = "Models/Foo.cs";

    private const string DestinationRelative = "Other/Foo.cs";

    // A project-relative path written with '/' becomes an absolute path with the platform's separators.
    private static string Abs(string relativePath) => Path.Combine(ProjectDirectory, Path.Combine(relativePath.Split('/')));

    // Lines joined with LF plus a final one: the tests spell file text this way so it does not depend on this file's own line endings.
    private static string Lf(params string[] lines) => string.Join("\n", lines) + "\n";

    private static readonly string FooSource = Lf("namespace Demo.Models;", "", "public class Foo { public static int Make() => 1; }");

    // A generated file is never rewritten, so an API declared there keeps taking the old type; passing it the new type is the typical blocker of a caller.
    private static readonly (string Relative, string Content) GeneratedApi = ("Gen/Api.g.cs", Lf("namespace Demo.Gen;", "", "public static class Api { public static void Take(Demo.Models.Foo foo) { } }"));

    private static readonly (string Relative, string Content) GoodCaller = ("Consumers/Good.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class Good { public Foo F = new Foo(); }"));

    private static (string Relative, string Content) Caller(string name, string body) => ($"Consumers/{name}.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", body));

    private static Solution CreateSolution(params (string Relative, string Content)[] others)
    {
        var documents = new[] { (Relative: SourceRelative, Content: FooSource) }.Concat(others)
            .Select(file => (name: Path.GetFileName(file.Relative), content: file.Content, filePath: Abs(file.Relative)))
            .ToArray();
        return TestSolutionBuilder.CreateSolutionWithProject("Demo", Path.Combine(ProjectDirectory, "Demo.csproj"), documents);
    }

    private static async Task<CopyAndObsoleteMigrationResult> RunAsync(Solution solution, string destinationRelative = DestinationRelative, CancellationToken cancellationToken = default)
    {
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, Abs(SourceRelative), Abs(destinationRelative), cancellationToken);
        Assert.That(check.IsRefusal, Is.False, check.Refusal?.Message);
        return await CopyAndObsoleteMigration.RunAsync(solution, Abs(SourceRelative), Abs(destinationRelative), FooSource, check, cancellationToken);
    }

    // The returned change set must compile with no newly introduced error.
    private static async Task AssertChangesCompileAsync(Solution solution, CopyAndObsoleteMigrationResult result)
    {
        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        var report = await ValidationEngine.ValidateChangesAsync(solution, result.Changes!);
        Assert.That(report.IsError, Is.False, string.Join(" | ", report.Diagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.Message}")));
    }

    private static RemainingObsoleteCaller RemainingOf(CopyAndObsoleteMigrationResult result, string relative) => result.RemainingCallers.Single(caller => caller.FilePath == Abs(relative));

    private static void AssertMigrated(CopyAndObsoleteMigrationResult result, params string[] relatives)
        => Assert.That(result.MigratedCallers.Select(file => file.FilePath.Absolute), Is.EquivalentTo(relatives.Select(Abs)).IgnoreCase);

    private static void AssertRemaining(CopyAndObsoleteMigrationResult result, params string[] relatives)
        => Assert.That(result.RemainingCallers.Select(caller => caller.FilePath.Absolute), Is.EquivalentTo(relatives.Select(Abs)).IgnoreCase);

    [Test]
    public async Task NoCallers_IsDoneInOneRound_WithTheBaseSetOnly()
    {
        var solution = CreateSolution();

        var result = await RunAsync(solution);

        await AssertChangesCompileAsync(solution, result);
        Assert.That(result.MigratedCallers, Is.Empty);
        Assert.That(result.RemainingCallers, Is.Empty);
        Assert.That(result.RoundsUsed, Is.EqualTo(1));
        Assert.That(result.FellBackToBaseSet, Is.False);
        Assert.That(result.Changes!.Keys.Select(path => path.Absolute), Is.EquivalentTo(new[] { Abs(DestinationRelative), Abs(SourceRelative) }).IgnoreCase);
        Assert.That(result.Changes[Abs(DestinationRelative)], Does.Contain("namespace Demo.Other;").And.Not.Contain("Obsolete"));
        Assert.That(result.Changes[Abs(SourceRelative)], Does.Contain("[System.Obsolete(\"Use Demo.Other.Foo\")]"));
    }

    [Test]
    public async Task CleanCaller_IsMigratedInTheFirstRound()
    {
        var solution = CreateSolution(GoodCaller);

        var result = await RunAsync(solution);

        await AssertChangesCompileAsync(solution, result);
        AssertMigrated(result, "Consumers/Good.cs");
        Assert.That(result.MigratedCallers.Single().Mode, Is.EqualTo(CallerRewriteMode.Using));
        Assert.That(result.RemainingCallers, Is.Empty);
        Assert.That(result.RoundsUsed, Is.EqualTo(1));
        Assert.That(result.Changes![Abs("Consumers/Good.cs")], Does.Contain("using Demo.Other;").And.Not.Contain("using Demo.Models;"));
    }

    [Test]
    public async Task CallerPassingTheNewTypeToAnApiThatStillTakesTheOldOne_StaysAndIsListedWithItsError()
    {
        var stuck = Caller("Stuck", "public class Stuck { public void Run() { Demo.Gen.Api.Take(new Foo()); } }");
        var solution = CreateSolution(GeneratedApi, GoodCaller, stuck);

        var result = await RunAsync(solution);

        await AssertChangesCompileAsync(solution, result);
        AssertMigrated(result, "Consumers/Good.cs");
        AssertRemaining(result, "Consumers/Stuck.cs");
        var remaining = RemainingOf(result, "Consumers/Stuck.cs");
        Assert.That(remaining.ErrorCode, Is.EqualTo("CS1503"));
        Assert.That(remaining.BlockingFile, Is.EqualTo((FilePathWrapper)Abs("Consumers/Stuck.cs")));
        Assert.That(remaining.FirstReferenceLine, Is.EqualTo(5), "The class line holds the first reference to Foo.");
        Assert.That(remaining.ErrorMessage, Does.Not.Contain("\n"));
        Assert.That(result.Changes!.ContainsKey(Abs("Consumers/Stuck.cs")), Is.False, "A remaining caller keeps its text, so it is not in the change set.");
        Assert.That(result.RoundsUsed, Is.EqualTo(3), "Round 1 fails in Using mode, round 2 fails in Qualify mode and reverts the file, round 3 compiles clean.");
        Assert.That(result.FellBackToBaseSet, Is.False);
    }

    [Test]
    public async Task CallerRelyingOnAnExtensionGetEnumerator_IsRewrittenQualifiedInTheFirstRound_AndMigrates()
    {
        // The foreach finds the extension GetEnumerator through 'using Demo.Models;', so the rewrite pass sees it and picks Qualify mode in round 1; no retry is needed.
        var extension = ("Models/FooExtensions.cs", Lf("namespace Demo.Models;", "", "public static class FooExtensions { public static System.Collections.IEnumerator GetEnumerator(this Foo foo) => new int[] { 1, 2 }.GetEnumerator(); }"));
        var counter = Caller("Counter", "public class Counter { public int Count(Foo foo) { var n = 0; foreach (var item in foo) { n++; } return n; } }");
        var solution = CreateSolution(extension, counter);
        var baselineErrors = (await solution.Projects.Single().GetCompilationAsync())!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        Assert.That(baselineErrors, Is.Empty, "The fixture itself compiles: an extension GetEnumerator drives foreach.");

        var result = await RunAsync(solution);

        await AssertChangesCompileAsync(solution, result);
        AssertMigrated(result, "Consumers/Counter.cs", "Models/FooExtensions.cs");
        AssertRemaining(result);
        var migrated = result.MigratedCallers.Single(file => file.FilePath == Abs("Consumers/Counter.cs"));
        Assert.That(migrated.Mode, Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(migrated.NewText, Does.Contain("Demo.Other.Foo foo").And.Contain("using Demo.Models;"));
        Assert.That(result.RoundsUsed, Is.EqualTo(1), "The rewrite pass chose Qualify for Counter in round 1, so no retry round is needed.");
        Assert.That(result.Facts.Any(fact => fact.Contains("retried", StringComparison.Ordinal)), Is.False, string.Join(" | ", result.Facts));
    }

    [Test]
    public async Task ErrorInAnUnrewrittenDependent_RevertsTheRewrittenFileItBindsTo()
    {
        // Dependent has no reference to Foo, so it is not rewritten; it breaks because Maker.Make now returns the new type.
        var maker = Caller("Maker", "public class Maker { public static Foo Make() => new Foo(); }");
        var dependent = ("Consumers/Dependent.cs", Lf("namespace Demo.Consumers;", "", "public class Dependent { public void Use() { Demo.Gen.Api.Take(Maker.Make()); } }"));
        var solution = CreateSolution(GeneratedApi, GoodCaller, maker, dependent);

        var result = await RunAsync(solution);

        await AssertChangesCompileAsync(solution, result);
        AssertMigrated(result, "Consumers/Good.cs");
        AssertRemaining(result, "Consumers/Maker.cs");
        var remaining = RemainingOf(result, "Consumers/Maker.cs");
        Assert.That(remaining.ErrorCode, Is.EqualTo("CS1503"));
        Assert.That(remaining.BlockingFile, Is.EqualTo((FilePathWrapper)Abs("Consumers/Dependent.cs")), "The error sits in the dependent, not in the reverted file.");
        Assert.That(remaining.BlockingLine, Is.EqualTo(3));
        Assert.That(remaining.FirstReferenceLine, Is.EqualTo(5));
        Assert.That(result.Changes!.ContainsKey(Abs("Consumers/Dependent.cs")), Is.False);
        Assert.That(result.Changes.ContainsKey(Abs("Consumers/Maker.cs")), Is.False);
        Assert.That(result.RoundsUsed, Is.EqualTo(2));
    }

    [Test]
    public async Task ACascadeBeyondTheRounds_EndsWithThoseCallersReverted_AndTheRestValid()
    {
        // X cannot migrate (round 1 Using, round 2 Qualify, then reverted); with X back on the old type B breaks (round 3, the last), so B is reverted without a repair.
        var x = Caller("X", "public class X { public static void Get(Foo foo) { Demo.Gen.Api.Take(foo); } }");
        var b = Caller("B", "public class B { public void M() { X.Get(new Foo()); } }");
        var solution = CreateSolution(GeneratedApi, GoodCaller, x, b);

        var result = await RunAsync(solution);

        await AssertChangesCompileAsync(solution, result);
        AssertMigrated(result, "Consumers/Good.cs");
        AssertRemaining(result, "Consumers/B.cs", "Consumers/X.cs");
        Assert.That(RemainingOf(result, "Consumers/X.cs").ErrorCode, Is.EqualTo("CS1503"));
        Assert.That(RemainingOf(result, "Consumers/B.cs").ErrorCode, Is.EqualTo("CS1503"));
        Assert.That(RemainingOf(result, "Consumers/B.cs").BlockingFile, Is.EqualTo((FilePathWrapper)Abs("Consumers/B.cs")));
        Assert.That(result.RoundsUsed, Is.EqualTo(CopyAndObsoleteMigration.MaxCompileRounds));
        Assert.That(result.FellBackToBaseSet, Is.False, "The final verification of the survivors was clean.");
    }

    [Test]
    public async Task WhenTheSurvivorsStillFail_EveryCallerIsReverted_AndTheBaseSetIsReturned()
    {
        // As above, and C depends on B, which is reverted in the last round: the survivors {C, Good} still fail, so nothing of the callers is kept.
        var x = Caller("X", "public class X { public static void Get(Foo foo) { Demo.Gen.Api.Take(foo); } }");
        var b = Caller("B", "public class B { public void Back(Foo foo) { X.Get(foo); } }");
        var c = Caller("C", "public class C { public void N() { new B().Back(new Foo()); } }");
        var solution = CreateSolution(GeneratedApi, GoodCaller, x, b, c);

        var result = await RunAsync(solution);

        await AssertChangesCompileAsync(solution, result);
        Assert.That(result.FellBackToBaseSet, Is.True);
        AssertMigrated(result);
        AssertRemaining(result, "Consumers/B.cs", "Consumers/C.cs", "Consumers/Good.cs", "Consumers/X.cs");
        Assert.That(result.RemainingCallers.All(caller => caller.ErrorCode == "CS1503"), Is.True, string.Join(" | ", result.RemainingCallers.Select(caller => caller.ErrorCode)));
        Assert.That(result.Changes!.Keys.Select(path => path.Absolute), Is.EquivalentTo(new[] { Abs(DestinationRelative), Abs(SourceRelative) }).IgnoreCase);
        Assert.That(result.RoundsUsed, Is.EqualTo(CopyAndObsoleteMigration.MaxCompileRounds));
    }

    [Test]
    public async Task CopyThatCollidesWithAnExistingType_RefusesBeforeAnyCallerIsRewritten()
    {
        var existing = ("Other/Existing.cs", Lf("namespace Demo.Other;", "", "public class Foo { }"));
        var solution = CreateSolution(existing, GoodCaller);

        var result = await RunAsync(solution);

        Assert.That(result.IsRefusal, Is.True);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(result.Refusal.Message, Does.Contain("CS0101").And.Contain("Options:").And.Contain("namespaceHandling keep"));
        Assert.That(result.Changes, Is.Null);
        Assert.That(result.MigratedCallers, Is.Empty);
        Assert.That(result.RemainingCallers, Is.Empty);
    }

    [Test]
    public async Task RefusedCheck_IsPassedThroughAsTheRefusal()
    {
        var solution = CreateSolution(GoodCaller);
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, Abs(SourceRelative), Abs("Models/Foo2.cs"));
        Assert.That(check.IsRefusal, Is.True, "The destination folder gives the namespace the source already declares.");

        var result = await CopyAndObsoleteMigration.RunAsync(solution, Abs(SourceRelative), Abs("Models/Foo2.cs"), FooSource, check);

        Assert.That(result.IsRefusal, Is.True);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(check.Refusal!.ErrorCode));
        Assert.That(result.Changes, Is.Null);
    }

    [Test]
    public async Task ACancelledToken_Throws_InsteadOfReturningAResult()
    {
        var solution = CreateSolution(GoodCaller);
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, Abs(SourceRelative), Abs(DestinationRelative));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.CatchAsync<OperationCanceledException>(async () => await CopyAndObsoleteMigration.RunAsync(solution, Abs(SourceRelative), Abs(DestinationRelative), FooSource, check, cancellation.Token));
    }
}
