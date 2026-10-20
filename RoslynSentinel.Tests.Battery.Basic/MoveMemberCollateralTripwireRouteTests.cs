using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Slice B3 of docs/current/plans/plan_movemember_collateral_change_tripwire.md: every MoveMember route reports the
/// TextChanges it applied (<see cref="MoveMemberResult.AppliedEdits"/>), and the REAL apply (dryRun: false) through the
/// write chokepoint, with those edits as the expected set, passes the collateral-change guard and writes exactly the
/// engine's text. One test per route.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
[Category("MoveMemberResult")]
public class MoveMemberCollateralTripwireRouteTests
{
    private const string Eol = "\r\n";

    [Test]
    public async Task StaticMethodToExistingClass_RealApplyPassesTheCollateralGuardAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", Lines("namespace Example;", "", "public static class A", "{",
                "    public static string Norm(string input) => input.Trim();", "",
                "    /// <summary>Other, see <see cref=\"A.Norm\"/>.</summary>",
                "    public static int Process(string x) => x.Length;", "}")),
            ("Target.cs", Lines("namespace Example;", "", "public static class B", "{",
                "    public static int Count() => 0;", "}")),
            ("Caller.cs", Lines("using Example;", "", "class Caller", "{",
                "    void Run() => A.Norm(\"x\");", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"), "A", new[] { "Norm" }, "B", workspace.PathOf("Target.cs"), CancellationToken.None);

        Assert.That(EditedFileNames(result), Is.EquivalentTo(new[] { "Source.cs", "Target.cs", "Caller.cs" }),
            "The source, the target and the rewritten caller must each report their edits");
        await AssertRealApplyPassesGuardAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task StaticMethodToNewClass_RealApplyPassesTheCollateralGuardAndLeavesTheNewFileUncheckedAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", Lines("namespace Example;", "", "public static class A", "{",
                "    public static string Norm(string input) => input.Trim();", "",
                "    public static int Process(string x) => x.Length;", "}")),
            ("Caller.cs", Lines("using Example;", "", "class Caller", "{",
                "    void Run() => A.Norm(\"x\");", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"), "A", new[] { "Norm" }, "NewHelper", null, CancellationToken.None);

        Assert.That(result.Changes.Keys.Select(FileName), Does.Contain("NewHelper.cs"), "The new class file is part of the change set");
        // The new file has no pre-image, so it carries no edits and the chokepoint leaves it unchecked by design.
        Assert.That(EditedFileNames(result), Is.EquivalentTo(new[] { "Source.cs", "Caller.cs" }));
        await AssertRealApplyPassesGuardAsync(workspace, validation, files, result);
        Assert.That(workspace.ReadText("NewHelper.cs"), Is.EqualTo(result.Changes.Single(c => FileName(c.Key) == "NewHelper.cs").Value));
    }

    [Test]
    public async Task InstanceMethodWithNoExternalCallersToNewClass_RealApplyPassesTheCollateralGuardAndLeavesTheNewFileUncheckedAsync()
    {
        // With a caller that holds the instance in a local, the engine cannot resolve the receiver to the new class,
        // leaves the call site skipped, and the compile gate refuses the stale a.Count call. This fixture has no external
        // callers, so only the moved-from file is edited.
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", Lines("namespace Example;", "", "public class A", "{",
                "    public int Count(string input, bool trim = false)", "    {",
                "        var text = trim ? input.Trim() : input;", "        return text.Length;", "    }", "",
                "    /// <summary>Other, see <see cref=\"A.Count\"/>.</summary>",
                "    public int Process(string x) => x.Length;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"), "A", new[] { "Count" }, "NewHelper", null, CancellationToken.None);

        Assert.That(result.Changes.Keys.Select(FileName), Does.Contain("NewHelper.cs"), "The new class file is part of the change set");
        // The new file has no pre-image, so it carries no edits and the chokepoint leaves it unchecked by design.
        Assert.That(EditedFileNames(result), Is.EquivalentTo(new[] { "Source.cs" }));
        await AssertRealApplyPassesGuardAsync(workspace, validation, files, result);
        Assert.That(workspace.ReadText("NewHelper.cs"), Is.EqualTo(result.Changes.Single(c => FileName(c.Key) == "NewHelper.cs").Value));
    }

    [Test]
    public async Task MethodToBaseType_RealApplyPassesTheCollateralGuardAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", Lines("namespace Example;", "", "public class Derived : Base", "{",
                "    public string Normalize(string input) => input.Trim();", "",
                "    public int Process(string x) => x.Length;", "}")),
            ("Base.cs", Lines("namespace Example;", "", "public class Base", "{",
                "    public int Count() => 0;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"), "Derived", new[] { "Normalize" }, "Base", workspace.PathOf("Base.cs"), CancellationToken.None);

        Assert.That(EditedFileNames(result), Is.EquivalentTo(new[] { "Source.cs", "Base.cs" }),
            "The member is removed from the derived file and inserted into the base file");
        await AssertRealApplyPassesGuardAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task InstanceMethodToExistingClass_RealApplyPassesTheCollateralGuardAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", Lines("namespace Example;", "", "public class A", "{",
                "    public int Count(string input, bool trim = false)", "    {",
                "        var text = trim ? input.Trim() : input;", "        return text.Length;", "    }", "",
                "    /// <summary>Other, see <see cref=\"A.Count\"/>.</summary>",
                "    public int Process(string x) => x.Length;", "}")),
            ("Target.cs", Lines("namespace Example;", "", "public class B", "{",
                "    public int Size() => 0;", "}")),
            ("Caller.cs", Lines("using Example;", "", "public class Caller", "{",
                "    private readonly B _b = new B();", "",
                "    public int Run()", "    {", "        var a = new A();", "        return a.Count(\"test\", true);", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"), "A", new[] { "Count" }, "B", null, CancellationToken.None);

        Assert.That(result.SkippedCallSites, Is.Empty, "The single B field makes the call site resolvable automatically");
        Assert.That(EditedFileNames(result), Is.EquivalentTo(new[] { "Source.cs", "Target.cs", "Caller.cs" }),
            "Source, target and the caller receiver rewrite must each report their edits");
        await AssertRealApplyPassesGuardAsync(workspace, validation, files, result);
    }

    /// <summary>
    /// Proves the recorded edits are exactly the ones that produced the text (guard-side replay equals the engine text),
    /// then runs the real apply with them as the expected set and checks nothing is refused and the engine text is written.
    /// </summary>
    private static async Task AssertRealApplyPassesGuardAsync(
        InMemoryWorkspace workspace,
        ValidationEngine validation,
        (string Name, string Content)[] originals,
        MoveMemberResult result)
    {
        Assert.That(result.AppliedEdits, Is.Not.Null, "The route must report the edits it applied");

        foreach (var (path, edits) in result.AppliedEdits!)
        {
            var name = FileName(path);
            var original = originals.Single(f => f.Name == name).Content;
            Assert.That(CollateralChangeGuard.ApplyEdits(original, edits), Is.EqualTo(result.Changes[path]),
                $"Replaying the recorded edits on the original {name} must reproduce the engine's text");
        }

        var outcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            validation, workspace.Manager, NullLogger.Instance, result.Changes, "MoveMember",
            dryRun: false, expectedEdits: result.AppliedEdits);

        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
        foreach (var (path, text) in result.Changes)
        {
            Assert.That(workspace.ReadText(FileName(path)), Is.EqualTo(text), $"{FileName(path)} must be written exactly as the engine produced it");
        }
    }

    private static string[] EditedFileNames(MoveMemberResult result) =>
        (result.AppliedEdits?.Keys ?? Enumerable.Empty<FilePathWrapper>()).Select(FileName).ToArray();

    private static string FileName(FilePathWrapper path) => Path.GetFileName(path.ToString());

    private static string Lines(params string[] lines) => string.Join(Eol, lines) + Eol;

    private static (MemberRefactoringEngine Engine, ValidationEngine Validation) CreateEngine(InMemoryWorkspace workspace)
    {
        var validation = new ValidationEngine(workspace.Manager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var symbolNav = new SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        return (new MemberRefactoringEngine(workspace.Manager, symbolNav, validation), validation);
    }
}
