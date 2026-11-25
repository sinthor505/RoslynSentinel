using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for <see cref="ChangeNamespaceEngine"/> (plan Step 31, rows T31.1-T31.16): one row per reference form of the namespace change's motivation list,
/// the hazards found in planning (dangling usings, shadowing, partial types, multi-namespace files, overlapping edits) and the refusals. In-memory only:
/// the paths sit under the scratch root but nothing is written there. Applying a plan means writing each changed file's new text into the committed
/// solution and compiling the project.
/// </summary>
[TestFixture]
[Category("ChangeNamespaceEngine")]
public class ChangeNamespaceEngineTests
{
    private static readonly string ProjectDirectory = Path.Combine(ScratchRoot.Resolve(), "ChangeNamespaceEngineTests", "Demo");

    private const string FooRelative = "Models/Foo.cs";

    private static readonly string FooSource = Lf("namespace Demo.Models;", "", "public class Foo { public static int Make() => 1; }");

    // A project-relative path written with '/' becomes an absolute path with the platform's separators.
    private static string Abs(string relativePath) => Path.Combine(ProjectDirectory, Path.Combine(relativePath.Split('/')));

    // The tests spell file text with LF endings, so the expectations do not depend on this file's own line endings.
    private static string Lf(params string[] lines) => string.Join("\n", lines) + "\n";

    private static Solution CreateSolution(params (string Relative, string Content)[] files)
    {
        var documents = files
            .Select(file => (name: Path.GetFileName(file.Relative), content: file.Content, filePath: Abs(file.Relative)))
            .ToArray();
        return TestSolutionBuilder.CreateSolutionWithProject("Demo", Path.Combine(ProjectDirectory, "Demo.csproj"), documents);
    }

    private static NamespaceMove Move(string relative, string oldNamespace, string newNamespace)
        => new(new FilePathWrapper(Abs(relative)), oldNamespace, newNamespace);

    private static Task<CallerRewriteResult> PlanAsync(Solution solution, params NamespaceMove[] moves)
        => ChangeNamespaceEngine.PlanAsync(new ChangeNamespaceRequest(moves), solution, CancellationToken.None);

    private static string NewTextOf(CallerRewriteResult result, string relative)
        => result.Files.Single(file => file.FilePath == Abs(relative)).NewText;

    // The mode the plan applied to one changed caller file.
    private static CallerRewriteMode ModeOf(CallerRewriteResult result, string relative)
        => result.Files.Single(file => file.FilePath == Abs(relative)).Mode;

    // Only these files are changed by the plan; every other document keeps its committed bytes.
    private static void AssertOnlyFiles(CallerRewriteResult result, params string[] relatives)
        => Assert.That(result.Files.Select(file => file.FilePath.Absolute), Is.EquivalentTo(relatives.Select(Abs)).IgnoreCase);

    private static void AssertRefused(CallerRewriteResult result, string errorCode)
    {
        Assert.That(result.IsRefusal, Is.True, "The plan was expected to be refused.");
        Assert.That(result.Files, Is.Empty);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(errorCode));
    }

    // Writes every changed file into the committed solution and returns the error diagnostics of the project.
    private static async Task<string[]> CompileErrorsAfterAsync(Solution committed, CallerRewriteResult result)
    {
        var applied = committed;
        foreach (var file in result.Files)
        {
            applied = applied.WithDocumentText(committed.GetDocumentIdsWithFilePath(file.FilePath.Absolute).Single(), SourceText.From(file.NewText));
        }

        var compilation = await applied.Projects.Single().GetCompilationAsync();
        return compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.ToString()).ToArray();
    }

    [Test]
    public async Task Change_UsingForm_AddsTheNewUsingAndRemovesTheOldOneWhenUnused()
    {
        var caller = ("Consumers/User.cs", Lf("using System;", "using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class User { public Foo F = new Foo(); }"));
        var solution = CreateSolution((FooRelative, FooSource), caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("using Demo.Other;").And.Not.Contain("using Demo.Models;"));
        AssertOnlyFiles(result, FooRelative, "Consumers/User.cs");
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_FullyQualifiedForm_IsRewritten()
    {
        var caller = ("Consumers/User.cs", Lf("namespace Demo.Consumers;", "", "public class User { public Demo.Models.Foo F = new Demo.Models.Foo(); }"));
        var solution = CreateSolution((FooRelative, FooSource), caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("Demo.Other.Foo").And.Not.Contain("Demo.Models.Foo"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_PartiallyQualifiedForm_IsRewritten()
    {
        var caller = ("Consumers/User.cs", Lf("namespace Demo.Consumers;", "", "public class User { public Models.Foo F; }"));
        var solution = CreateSolution((FooRelative, FooSource), caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("Other.Foo").And.Not.Contain("Models.Foo"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_GlobalQualifiedForm_IsRewritten()
    {
        var caller = ("Consumers/User.cs", Lf("namespace Demo.Consumers;", "", "public class User { public global::Demo.Models.Foo F; }"));
        var solution = CreateSolution((FooRelative, FooSource), caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("global::Demo.Other.Foo"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_AliasDirectiveForm_IsWrittenOut()
    {
        var caller = ("Consumers/User.cs", Lf("using F = Demo.Models.Foo;", "", "namespace Demo.Consumers;", "", "public class User { public F Field; }"));
        var solution = CreateSolution((FooRelative, FooSource), caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("using F = Demo.Other.Foo;"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_CrefForm_FullyQualifiedCrefIsRewritten()
    {
        var caller = ("Consumers/User.cs", Lf(
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "/// <summary>See <see cref=\"Demo.Models.Foo\"/>.</summary>",
            "public class User { public Foo F; }"));
        var solution = CreateSolution((FooRelative, FooSource), caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("cref=\"Demo.Other.Foo\""));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_NameofAndTypeofForms_KeepTheSimpleNameAndGetTheNewUsing()
    {
        var caller = ("Consumers/User.cs", Lf(
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public System.Type T = typeof(Foo); public string N = nameof(Foo); }"));
        var solution = CreateSolution((FooRelative, FooSource), caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("using Demo.Other;").And.Contain("typeof(Foo)").And.Contain("nameof(Foo)"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_AttributeBaseListAndGenericArgumentForms_GetTheNewUsing()
    {
        var shapes = ("Consumers/Shapes.cs", Lf(
            "namespace Demo.Consumers;",
            "",
            "public class Base<T> { }",
            "",
            "public class MarkerAttribute : System.Attribute { public MarkerAttribute(System.Type type) { } }"));
        var caller = ("Consumers/User.cs", Lf(
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "[Marker(typeof(Foo))]",
            "public class User : Base<Foo> { }"));
        var solution = CreateSolution((FooRelative, FooSource), shapes, caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("using Demo.Other;").And.Not.Contain("using Demo.Models;"));
        AssertOnlyFiles(result, FooRelative, "Consumers/User.cs");
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_GlobalUsingOfTheOldNamespace_NoDanglingUsing_AndCompiles()
    {
        var helper = ("Models/Helper.cs", Lf("namespace Demo.Models;", "", "public class Helper { }"));
        var globals = ("Consumers/Globals.cs", Lf("global using Demo.Models;"));
        var caller = ("Consumers/User.cs", Lf("namespace Demo.Consumers;", "", "public class User { public Foo F = new Foo(); }"));
        var solution = CreateSolution((FooRelative, FooSource), helper, globals, caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.DanglingUsings ?? Enumerable.Empty<DanglingUsing>(), Is.Empty, "Demo.Models still holds Helper, so the global using still resolves.");
        Assert.That(ModeOf(result, "Consumers/User.cs"), Is.EqualTo(CallerRewriteMode.Using), "The old types are gone, so a using of the new namespace is safe beside the global using.");
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    // Moved mode: a global using of the old namespace explains the missing 'using Old;', so the caller gets 'using New;' instead of qualification and the global using stays.
    [Test]
    public async Task Change_GlobalUsingOfTheOldNamespace_CallerGetsTheNewUsing_NotQualification_GlobalUsingUntouched()
    {
        var helper = ("Models/Helper.cs", Lf("namespace Demo.Models;", "", "public class Helper { }"));
        var globals = ("Consumers/Globals.cs", Lf("global using Demo.Models;"));
        var caller = ("Consumers/User.cs", Lf("namespace Demo.Consumers;", "", "public class User { public Foo F = new Foo(); public Helper H = new Helper(); }"));
        var solution = CreateSolution((FooRelative, FooSource), helper, globals, caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(ModeOf(result, "Consumers/User.cs"), Is.EqualTo(CallerRewriteMode.Using));
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("using Demo.Other;").And.Not.Contain("Demo.Other.Foo"));
        AssertOnlyFiles(result, FooRelative, "Consumers/User.cs");
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    // Moved mode: other types of the old namespace are still used, so 'using Old;' stays and 'using New;' is added beside it (the old types are gone, so no CS0104).
    [Test]
    public async Task Change_CallerAlsoUsesAnotherTypeOfTheOldNamespace_KeepsBothUsings_NoQualification()
    {
        var helper = ("Models/Helper.cs", Lf("namespace Demo.Models;", "", "public class Helper { }"));
        var caller = ("Consumers/User.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class User { public Foo F = new Foo(); public Helper H = new Helper(); }"));
        var solution = CreateSolution((FooRelative, FooSource), helper, caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(ModeOf(result, "Consumers/User.cs"), Is.EqualTo(CallerRewriteMode.Using));
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("using Demo.Models;").And.Contain("using Demo.Other;").And.Not.Contain("Demo.Other.Foo"));
        AssertOnlyFiles(result, FooRelative, "Consumers/User.cs");
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    // Moved mode still qualifies when a used type shares its name with a different type of the new namespace (the added using would make it ambiguous).
    [Test]
    public async Task Change_SimpleNameConflictInTheNewNamespace_StillQualifies()
    {
        var keep = ("Models/Keep.cs", Lf("namespace Demo.Models;", "", "public class Keep { }"));
        var lib = ("Lib/Helper.cs", Lf("namespace Demo.Lib;", "", "public class Helper { }"));
        var clash = ("Other/Helper.cs", Lf("namespace Demo.Other;", "", "public class Helper { }"));
        var caller = ("Consumers/User.cs", Lf("using Demo.Lib;", "using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class User { public Foo F = new Foo(); public Helper H = new Helper(); }"));
        var solution = CreateSolution((FooRelative, FooSource), keep, lib, clash, caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(ModeOf(result, "Consumers/User.cs"), Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("public Demo.Other.Foo F = new Demo.Other.Foo();").And.Not.Contain("using Demo.Other;"));
        Assert.That(result.Facts, Has.Some.Contain("'Helper'").And.Contain("ambiguous"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    // Moved mode still qualifies when the old using appears in two scopes: which one binds the simple name is not decided here.
    [Test]
    public async Task Change_OldUsingInTwoScopes_StillQualifies()
    {
        var keep = ("Models/Keep.cs", Lf("namespace Demo.Models;", "", "public class Keep { }"));
        var caller = ("Consumers/User.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers", "{", "    using Demo.Models;", "", "    public class User { public Foo F = new Foo(); }", "}"));
        var solution = CreateSolution((FooRelative, FooSource), keep, caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(ModeOf(result, "Consumers/User.cs"), Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("public Demo.Other.Foo F = new Demo.Other.Foo();").And.Not.Contain("using Demo.Other;"));
        Assert.That(result.Facts, Has.Some.Contain("more than one scope"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_PartialTypeInThreeFiles_AllPartsInSet_Works()
    {
        var part1 = ("Models/Big1.cs", Lf("namespace Demo.Models;", "", "public partial class Big { public int A; }"));
        var part2 = ("Models/Big2.cs", Lf("namespace Demo.Models;", "", "public partial class Big { public int B; }"));
        var part3 = ("Models/Big3.cs", Lf("namespace Demo.Models;", "", "public partial class Big { public int C; }"));
        var solution = CreateSolution(part1, part2, part3);

        var result = await PlanAsync(solution,
            Move("Models/Big1.cs", "Demo.Models", "Demo.Other"),
            Move("Models/Big2.cs", "Demo.Models", "Demo.Other"),
            Move("Models/Big3.cs", "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_PartialTypeInThreeFiles_PartOutsideSet_Refuses()
    {
        var part1 = ("Models/Big1.cs", Lf("namespace Demo.Models;", "", "public partial class Big { public int A; }"));
        var part2 = ("Models/Big2.cs", Lf("namespace Demo.Models;", "", "public partial class Big { public int B; }"));
        var part3 = ("Models/Big3.cs", Lf("namespace Demo.Models;", "", "public partial class Big { public int C; }"));
        var solution = CreateSolution(part1, part2, part3);

        var result = await PlanAsync(solution,
            Move("Models/Big1.cs", "Demo.Models", "Demo.Other"),
            Move("Models/Big2.cs", "Demo.Models", "Demo.Other"));

        AssertRefused(result, ToolErrorCode.TargetIneligible);
    }

    [Test]
    public async Task Change_CallerInsideOldNamespace_GetsTheUsing_NotQualification()
    {
        var uses = ("Models/Uses.cs", Lf("namespace Demo.Models;", "", "public class Uses { public Foo F = new Foo(); }"));
        var solution = CreateSolution((FooRelative, FooSource), uses);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Models/Uses.cs"), Does.Contain("using Demo.Other;").And.Not.Contain("Demo.Other.Foo"));
        AssertOnlyFiles(result, FooRelative, "Models/Uses.cs");
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_SetMembersReferenceEachOther_NoUsingBetweenThem()
    {
        var foo = ("Models/Foo.cs", Lf("namespace Demo.Models;", "", "public class Foo { public Bar B; }"));
        var bar = ("Models/Bar.cs", Lf("namespace Demo.Models;", "", "public class Bar { public Foo F; }"));
        var solution = CreateSolution(foo, bar);

        var result = await PlanAsync(solution,
            Move("Models/Foo.cs", "Demo.Models", "Demo.Other"),
            Move("Models/Bar.cs", "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Models/Foo.cs"), Does.Not.Contain("using Demo.Other;"));
        Assert.That(NewTextOf(result, "Models/Bar.cs"), Does.Not.Contain("using Demo.Other;"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_OldNamespaceEmptied_DanglingUsingIsRemoved()
    {
        // The dangling-using sweep only reports a using that nothing else touched: a file that uses a moved type has its 'using Old;' replaced by the caller pass,
        // so it never dangles (see the next test). The directive that dangles belongs to a file that uses no moved type at all and sits outside the reference sites.
        var unused = ("Consumers/Unused.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class Unused { }"));
        var solution = CreateSolution((FooRelative, FooSource), unused);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.DanglingUsings, Has.Some.Property(nameof(DanglingUsing.Resolution)).EqualTo(DanglingUsingResolution.Removed));
        Assert.That(NewTextOf(result, "Consumers/Unused.cs"), Does.Not.Contain("using Demo.Models;"));
        AssertOnlyFiles(result, FooRelative, "Consumers/Unused.cs");
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_OldNamespaceEmptied_UsingOfAFileThatUsesAMovedTypeIsSwitchedNotReportedDangling()
    {
        var caller = ("Consumers/User.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class User { public Foo F = new Foo(); }"));
        var solution = CreateSolution((FooRelative, FooSource), caller);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.DanglingUsings ?? Enumerable.Empty<DanglingUsing>(), Is.Empty, "The caller pass already replaced 'using Demo.Models;', so nothing is left to dangle.");
        Assert.That(NewTextOf(result, "Consumers/User.cs"), Does.Contain("using Demo.Other;").And.Not.Contain("using Demo.Models;"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }

    [Test]
    public async Task Change_NameShadowing_FactReported()
    {
        // A type Demo.Other sits where the new namespace Demo.Other would also be declared: the namespace segment would shadow it.
        var shadow = ("Shadow/Other.cs", Lf("namespace Demo;", "", "public class Other { }"));
        var solution = CreateSolution((FooRelative, FooSource), shadow);

        var result = await PlanAsync(solution, Move(FooRelative, "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.Facts, Has.Some.StartWith("Name shadowing:"));
    }

    [Test]
    public async Task Change_MultiNamespaceFile_Refuses()
    {
        var two = ("Models/Two.cs", Lf("namespace Demo.A", "{", "    public class X { }", "}", "namespace Demo.B", "{", "    public class Y { }", "}"));
        var solution = CreateSolution(two);

        var result = await PlanAsync(solution, Move("Models/Two.cs", "Demo.A", "Demo.Other"));

        AssertRefused(result, ToolErrorCode.TargetIneligible);
    }

    [Test]
    public async Task Change_OverlappingEdits_RefusesWithEditOverlap_NotSkipped()
    {
        // Through ChangeNamespaceEngine this cannot happen: one call has one target namespace (D7), so a set member's simple names for other members are never edited
        // (they land in the same namespace) and a qualified name only has its qualifier replaced, a span the namespace swap and the using edits never touch (see the next
        // test). The overlap-refusing path of the shared core is reachable with two targets, so it is driven through the set overload of the core directly:
        // Foo carries a redundant 'using Demo.Models;' and moves to Alpha while Bar moves to Beta. As a caller Foo drops that using (nothing else of Demo.Models is used)
        // and adds 'using Beta;'; as a declaration owner it needs 'using Demo;' for Base, which Demo.Models saw implicitly and Alpha does not. Both roles anchor at the
        // start of the 'using Demo.Models;' line, and the caller role's removal is ordered ahead of the owner role's insertion, which then falls inside the removed span.
        var foo = ("Models/Foo.cs", Lf("using Demo.Models;", "", "namespace Demo.Models;", "", "public class Foo { public Bar B; public Base X; }"));
        var bar = ("Models/Bar.cs", Lf("namespace Demo.Models;", "", "public class Bar { }"));
        var baseType = ("Base.cs", Lf("namespace Demo;", "", "public class Base { }"));
        var solution = CreateSolution(foo, bar, baseType);

        var result = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution,
            [Move("Models/Foo.cs", "Demo.Models", "Alpha"), Move("Models/Bar.cs", "Demo.Models", "Beta")],
            cancellationToken: CancellationToken.None);

        AssertRefused(result, ToolErrorCode.PreconditionNotMet);
        Assert.That(result.Refusal!.StructuredDetail, Has.Some.InstanceOf<EditOverlap>());
    }

    [Test]
    public void Change_ApplyStrict_RefusesAnOverlapWhereApplySkipsIt()
    {
        // The shared Apply keeps skipping an edit that starts inside an earlier one (the live copyAndObsolete path relies on it); ApplyStrict reports the pair instead.
        const string text = "0123456789";
        var edits = new List<(int Position, int Length, string Text)> { (2, 4, "X"), (4, 0, "Y") };
        var overlaps = new List<EditOverlap>();

        Assert.That(CopyAndObsoleteEdits.Apply(text, edits), Is.EqualTo("01X6789"));
        Assert.That(CopyAndObsoleteEdits.ApplyStrict("file.cs", text, edits, overlaps), Is.Null);
        Assert.That(overlaps, Is.EqualTo(new[] { new EditOverlap("file.cs", 2, 4, 4, 0) }));
        Assert.That(CopyAndObsoleteEdits.OverlapRefusal(overlaps).ErrorCode, Is.EqualTo(ToolErrorCode.PreconditionNotMet));
    }

    [Test]
    public async Task Change_SetMemberNamedByItsQualifiedName_IsRewrittenOnceWithoutOverlap()
    {
        // Foo is moved and names Bar, which is moved too, by its qualified name: the qualifier edit and Foo's own namespace swap are disjoint spans, so there is no overlap.
        var foo = ("Models/Foo.cs", Lf("namespace Demo.Models;", "", "public class Foo { public Demo.Models.Bar B; }"));
        var bar = ("Models/Bar.cs", Lf("namespace Demo.Models;", "", "public class Bar { }"));
        var solution = CreateSolution(foo, bar);

        var result = await PlanAsync(solution,
            Move("Models/Foo.cs", "Demo.Models", "Demo.Other"),
            Move("Models/Bar.cs", "Demo.Models", "Demo.Other"));

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(NewTextOf(result, "Models/Foo.cs"), Does.Contain("namespace Demo.Other;").And.Contain("Demo.Other.Bar").And.Not.Contain("Demo.Models"));
        Assert.That(await CompileErrorsAfterAsync(solution, result), Is.Empty);
    }
}
