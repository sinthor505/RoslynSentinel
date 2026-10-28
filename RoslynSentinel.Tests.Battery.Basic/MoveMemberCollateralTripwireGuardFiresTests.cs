using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Slice B1t of docs/current/plans/plan_movemember_collateral_change_tripwire.md: the collateral-change guard fires on
/// real MoveMember output. A whole-file normalizer is simulated by removing a blank line that the move did not touch
/// (the one after the namespace line) from the engine's text for Source.cs. The guard must refuse it with
/// CollateralChangeRefused, name the file and the first differing line, and write nothing. Control: the unaltered
/// engine text passes with the same edits. Mutation: without expectedEdits the altered text is not refused.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
[Category("MoveMemberResult")]
public class MoveMemberCollateralTripwireGuardFiresTests
{
    private const string Eol = "\r\n";

    [Test]
    public async Task StaticMethodToExistingClass_NormalizedSourceIsRefusedAndNothingIsWrittenAsync()
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

        await AssertGuardFiresOnNormalizedSourceAsync(workspace, validation, result, files);
    }

    [Test]
    public async Task InstanceMethodToExistingClass_NormalizedSourceIsRefusedAndNothingIsWrittenAsync()
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
                "    public int Run()", "    {", "        var a = new A();",
                "        return a.Count(\"test\", true);", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"), "A", new[] { "Count" }, "B", null, CancellationToken.None);

        await AssertGuardFiresOnNormalizedSourceAsync(workspace, validation, result, files);
    }

    [Test]
    public async Task MethodToBaseType_NormalizedSourceIsRefusedAndNothingIsWrittenAsync()
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

        await AssertGuardFiresOnNormalizedSourceAsync(workspace, validation, result, files);
    }

    [Test]
    public async Task StaticMethodToNewClass_NormalizedSourceIsRefusedByAnExistingFileAsync()
    {
        // The new class file (NewHelper.cs) has no pre-image and is unchecked by design, so the refusal must come from
        // Source.cs, which does have one.
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

        await AssertGuardFiresOnNormalizedSourceAsync(workspace, validation, result, files);
    }

    /// <summary>
    /// Refusal first (nothing on disk changes), then the mutation check (the same altered text is not refused once
    /// expectedEdits is omitted, so the refusal is the guard's doing), then the control (the engine's own text passes
    /// with the same edits and is written).
    /// </summary>
    private static async Task AssertGuardFiresOnNormalizedSourceAsync(
        InMemoryWorkspace workspace,
        ValidationEngine validation,
        MoveMemberResult result,
        (string Name, string Content)[] originals)
    {
        Assert.That(result.AppliedEdits, Is.Not.Null, "The route must report the edits it applied");

        var sourcePath = result.Changes.Keys.Single(k => FileName(k) == "Source.cs");
        var engineText = result.Changes[sourcePath];
        var normalizedText = StripBlankLineAfterNamespace(engineText);
        Assert.That(normalizedText, Is.Not.EqualTo(engineText), "The simulated normalizer must alter the engine's text");

        var normalized = new Dictionary<FilePathWrapper, string>(result.Changes) { [sourcePath] = normalizedText };

        var refused = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            validation, workspace.Manager, NullLogger.Instance, normalized, "MoveMember",
            dryRun: false, expectedEdits: result.AppliedEdits);

        Assert.That(refused.Error, Is.Not.Null, "The guard must refuse text that the declared edits do not explain");
        Assert.That(refused.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.CollateralChangeRefused));
        Assert.That(refused.Error.Message, Does.Contain("Source.cs").And.Contain("first difference at line 2"),
            "The refusal must name the file and the first differing line");
        foreach (var (name, content) in originals)
        {
            Assert.That(workspace.ReadText(name), Is.EqualTo(content), $"{name} must be unchanged on disk after a refusal");
        }

        var unguarded = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            validation, workspace.Manager, NullLogger.Instance, normalized, "MoveMember",
            dryRun: true, expectedEdits: null);

        Assert.That(unguarded.Error?.ErrorCode, Is.Not.EqualTo(ToolErrorCode.CollateralChangeRefused),
            "Without expectedEdits the same altered text is not refused by the guard, so the refusal above is the guard's doing");

        var control = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            validation, workspace.Manager, NullLogger.Instance, result.Changes, "MoveMember",
            dryRun: false, expectedEdits: result.AppliedEdits);

        Assert.That(control.Error, Is.Null, control.Error?.Message);
        Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(engineText),
            "The unaltered engine text must pass the guard with the same edits and be written");
    }

    /// <summary>Removes the first blank line after the namespace line: a whole-file normalizer effect outside the moved member.</summary>
    private static string StripBlankLineAfterNamespace(string text)
    {
        const string Marker = "namespace Example;" + Eol + Eol;
        Assert.That(text, Does.Contain(Marker), "The engine text must keep the blank line after the namespace line");
        var index = text.IndexOf(Marker, StringComparison.Ordinal);
        return text.Substring(0, index) + "namespace Example;" + Eol + text.Substring(index + Marker.Length);
    }

    private static string FileName(FilePathWrapper path) => Path.GetFileName(path.ToString());

    private static string Lines(params string[] lines) => string.Join(Eol, lines) + Eol;

    private static (MemberRefactoringEngine Engine, ValidationEngine Validation) CreateEngine(InMemoryWorkspace workspace)
    {
        var validation = TestEngines.ValidationEngine(workspace.Manager, TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var symbolNav = TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        return (TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, validation), validation);
    }
}
