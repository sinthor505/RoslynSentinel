using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for <see cref="CopyAndObsoleteCallerRewrite"/>: the two rewrite modes (a using directive, or qualifying each reference), the reference kinds a
/// symbol search finds (qualified, attribute, typeof, nameof, generic argument, base list, cref, alias directive), other projects, global usings, name
/// conflicts, byte preservation (BOM, line endings), and an end-to-end validation of copy + marked original + caller rewrites. In-memory only: the paths
/// sit under the scratch root but nothing is written there.
/// </summary>
[TestFixture]
[Category("CopyAndObsoleteCallerRewrite")] // sentinel:auto-category
public class CopyAndObsoleteCallerRewriteTests
{
    private static readonly string ProjectDirectory = Path.Combine(ScratchRoot.Resolve(), "CopyAndObsoleteCallerRewriteTests", "Demo");

    private static readonly string AppDirectory = Path.Combine(ScratchRoot.Resolve(), "CopyAndObsoleteCallerRewriteTests", "App");

    private const string SourceRelative = "Models/Foo.cs";

    // A project-relative path written with '/' becomes an absolute path with the platform's separators.
    private static string Abs(string relativePath) => Path.Combine(ProjectDirectory, Path.Combine(relativePath.Split('/')));

    private static string AppAbs(string relativePath) => Path.Combine(AppDirectory, Path.Combine(relativePath.Split('/')));

    // Lines joined with the given line ending, plus a final one: the tests spell file text this way so it does not depend on this file's own line endings.
    private static string Join(string eol, params string[] lines) => string.Join(eol, lines) + eol;

    private static string Lf(params string[] lines) => Join("\n", lines);

    private static readonly string FooSource = Lf("namespace Demo.Models;", "", "public class Foo { public static int Make() => 1; }");

    private static readonly (string Relative, string Content) HelperFile = ("Models/Helper.cs", Lf("namespace Demo.Models;", "public class Helper { }"));

    private static Dictionary<FilePathWrapper, CallerRewriteMode> Forced(string relative, CallerRewriteMode mode) => new() { [Abs(relative)] = mode };

    private static Solution CreateSolution(string sourceText, params (string Relative, string Content)[] others)
    {
        var documents = new[] { (Relative: SourceRelative, Content: sourceText) }.Concat(others)
            .Select(file => (name: Path.GetFileName(file.Relative), content: file.Content, filePath: Abs(file.Relative)))
            .ToArray();
        return TestSolutionBuilder.CreateSolutionWithProject("Demo", Path.Combine(ProjectDirectory, "Demo.csproj"), documents);
    }

    // Checks and builds the copy and the marked original (both asserted accepted), runs the caller pass, applies all of it and compiles the project.
    private static async Task<(CallerRewriteResult Rewrite, string[] Errors)> MoveAsync(string sourceText, (string Relative, string Content)[] others, Dictionary<FilePathWrapper, CallerRewriteMode>? forced = null)
    {
        var solution = CreateSolution(sourceText, others);
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, Abs(SourceRelative), Abs("Other/Foo.cs"));
        Assert.That(check.IsRefusal, Is.False, check.Refusal?.Message);
        var edits = await CopyAndObsoleteEdits.BuildAsync(solution, Abs(SourceRelative), sourceText, check);
        Assert.That(edits.IsRefusal, Is.False, edits.Refusal?.Message);
        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), check, forced);
        Assert.That(rewrite.IsRefusal, Is.False, rewrite.Refusal?.Message);

        var project = solution.Projects.Single();
        var applied = solution
            .WithDocumentText(solution.GetDocumentIdsWithFilePath(Abs(SourceRelative)).Single(), SourceText.From(edits.OldText!))
            .AddDocument(DocumentId.CreateNewId(project.Id), "Foo.cs", SourceText.From(edits.NewText!), filePath: Abs("Other/Foo.cs"));
        foreach (var file in rewrite.Files)
        {
            applied = applied.WithDocumentText(solution.GetDocumentIdsWithFilePath(file.FilePath.Absolute).Single(), SourceText.From(file.NewText));
        }

        var compilation = await applied.GetProject(project.Id)!.GetCompilationAsync();
        var errors = compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.ToString()).ToArray();
        return (rewrite, errors);
    }

    private static CallerFileRewrite FileOf(CallerRewriteResult rewrite, string relative) => rewrite.Files.Single(file => file.FilePath == Abs(relative));

    // One fact of the pass holds every fragment.
    private static void AssertFact(CallerRewriteResult rewrite, params string[] fragments)
        => Assert.That(rewrite.Facts.Any(fact => fragments.All(fragment => fact.Contains(fragment, StringComparison.Ordinal))), Is.True, $"No fact holds {string.Join(" and ", fragments)}. Facts: {string.Join(" | ", rewrite.Facts)}");

    [Test]
    public async Task UsingMode_ReplacesTheOldUsing_WhenItWasOnlyNeededForTheMovedType()
    {
        var consumer = ("Consumers/User.cs", Lf(
            "using System;",
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public Foo F = new Foo(); public Func<int> M = Foo.Make; }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [consumer]);

        var file = FileOf(rewrite, "Consumers/User.cs");
        Assert.That(file.Mode, Is.EqualTo(CallerRewriteMode.Using));
        Assert.That(file.NewText, Is.EqualTo(Lf(
            "using System;",
            "using Demo.Other;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public Foo F = new Foo(); public Func<int> M = Foo.Make; }")));
        Assert.That(file.ReferencesRewritten, Is.EqualTo(3));
        Assert.That(rewrite.Files, Has.Count.EqualTo(1), "Neither the source file nor a file without a reference is changed.");
        Assert.That(errors, Is.Empty, "No CS0104: the old using is gone, so the simple name has one meaning.");
    }

    [Test]
    public async Task UsingMode_AlsoMigrates_ATypeThatWasAlreadyObsolete()
    {
        var source = Lf("namespace Demo.Models;", "", "[System.Obsolete(\"old\")]", "public class Foo { }");
        var consumer = ("Consumers/User.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class User { public Foo F; }"));

        var (rewrite, errors) = await MoveAsync(source, [consumer]);

        Assert.That(FileOf(rewrite, "Consumers/User.cs").NewText, Does.Contain("using Demo.Other;").And.Not.Contain("using Demo.Models;"));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task QualifyMode_IsUsed_WhenTheFileAlsoNeedsAnotherTypeOfTheOldNamespace()
    {
        var consumer = ("Consumers/User.cs", Lf(
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public Foo F = new Foo(); public Helper H = new Helper(); }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [HelperFile, consumer]);

        var file = FileOf(rewrite, "Consumers/User.cs");
        Assert.That(file.Mode, Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(file.NewText, Is.EqualTo(Lf(
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public Demo.Other.Foo F = new Demo.Other.Foo(); public Helper H = new Helper(); }")));
        AssertFact(rewrite, "'Helper'", "qualified");
        Assert.That(errors, Is.Empty, "No CS0104: the old using stays and no using for the new namespace is added.");
    }

    private static readonly (string Relative, string Content) EnumeratorExtensionFile = ("Models/CountExt.cs", Lf(
        "namespace Demo.Models;",
        "public static class CountExt",
        "{",
        "    public static System.Collections.Generic.IEnumerator<int> GetEnumerator(this int count) { for (var i = 0; i < count; i++) { yield return i; } }",
        "}"));

    [Test]
    public async Task QualifyMode_IsUsed_WhenTheFileReliesOnTheOldUsingForAnExtensionGetEnumeratorOfAForeach()
    {
        var consumer = ("Consumers/User.cs", Lf(
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public Foo F = new Foo(); public int Sum() { var sum = 0; foreach (var i in 3) { sum += i; } return sum; } }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [EnumeratorExtensionFile, consumer]);

        var file = FileOf(rewrite, "Consumers/User.cs");
        Assert.That(file.Mode, Is.EqualTo(CallerRewriteMode.Qualify), "The foreach names no type, but the using of Demo.Models is what makes its GetEnumerator extension resolve.");
        Assert.That(file.NewText, Does.Contain("using Demo.Models;").And.Contain("public Demo.Other.Foo F = new Demo.Other.Foo();"));
        Assert.That(errors, Is.Empty, "The old using is kept, so the foreach still compiles.");
    }

    [Test]
    public async Task QualifyMode_IsUsed_WhenTheFileReliesOnTheOldUsingForAnExtensionAddOfACollectionInitializer()
    {
        var bag = ("Bag.cs", Lf("namespace Demo;", "public class Bag : System.Collections.IEnumerable { public System.Collections.IEnumerator GetEnumerator() => throw new System.NotSupportedException(); }"));
        var bagExtension = ("Models/BagExt.cs", Lf("namespace Demo.Models;", "public static class BagExt { public static void Add(this Bag bag, int value) { } }"));
        var consumer = ("Consumers/User.cs", Lf(
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public Foo F = new Foo(); public Demo.Bag B = new Demo.Bag { 1, 2 }; }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [bag, bagExtension, consumer]);

        Assert.That(FileOf(rewrite, "Consumers/User.cs").Mode, Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task QualifyMode_IsUsed_ForFilesInsideTheOldNamespaceOrNestedInIt()
    {
        var sibling = ("Models/Sibling.cs", Lf("namespace Demo.Models;", "", "public class Sibling { public Foo F = new Foo(); }"));
        var deep = ("Models/Sub/Deep.cs", Lf("namespace Demo.Models.Sub;", "", "public class Deep { public Foo F; }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [sibling, deep]);

        var siblingFile = FileOf(rewrite, "Models/Sibling.cs");
        Assert.That(siblingFile.Mode, Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(siblingFile.NewText, Is.EqualTo(Lf("namespace Demo.Models;", "", "public class Sibling { public Demo.Other.Foo F = new Demo.Other.Foo(); }")), "Members of the enclosing namespace win over usings, so a using would keep the old type.");
        var deepFile = FileOf(rewrite, "Models/Sub/Deep.cs");
        Assert.That(deepFile.Mode, Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(deepFile.NewText, Is.EqualTo(Lf("namespace Demo.Models.Sub;", "", "public class Deep { public Demo.Other.Foo F; }")));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task QualifyMode_IsUsed_WhenTheNewNamespaceHoldsADifferentTypeOfAUsedName()
    {
        var lib = ("Lib/Util.cs", Lf("namespace Demo.Lib;", "public class Helper { }"));
        var clash = ("Other/Helper.cs", Lf("namespace Demo.Other;", "public class Helper { }"));
        var consumer = ("Consumers/User.cs", Lf(
            "using Demo.Lib;",
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public Foo F; public Helper H; }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [lib, clash, consumer]);

        var file = FileOf(rewrite, "Consumers/User.cs");
        Assert.That(file.Mode, Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(file.NewText, Does.Contain("public Demo.Other.Foo F; public Helper H;").And.Contain("using Demo.Models;"));
        AssertFact(rewrite, "'Helper'", "ambiguous");
        Assert.That(errors, Is.Empty, "A using for Demo.Other would have made Helper ambiguous (CS0104).");
    }

    [Test]
    public async Task QualifiedReferences_GetTheirNamespacePartReplaced_InTypeAndExpressionPositions()
    {
        var consumer = ("Consumers/User.cs", Lf(
            "namespace Demo.Consumers;",
            "",
            "public class User",
            "{",
            "    public Demo.Models.Foo A = new Demo.Models.Foo();",
            "    public global::Demo.Models.Foo B = new global::Demo.Models.Foo();",
            "    public int C = Demo.Models.Foo.Make();",
            "}"));

        var (rewrite, errors) = await MoveAsync(FooSource, [consumer]);

        var file = FileOf(rewrite, "Consumers/User.cs");
        Assert.That(file.Mode, Is.EqualTo(CallerRewriteMode.Using), "No simple-name reference, so no using directive is touched.");
        Assert.That(file.NewText, Is.EqualTo(Lf(
            "namespace Demo.Consumers;",
            "",
            "public class User",
            "{",
            "    public Demo.Other.Foo A = new Demo.Other.Foo();",
            "    public global::Demo.Other.Foo B = new global::Demo.Other.Foo();",
            "    public int C = Demo.Other.Foo.Make();",
            "}")));
        Assert.That(file.ReferencesRewritten, Is.EqualTo(5));
        Assert.That(errors, Is.Empty);
    }

    private static readonly string KindsSource = Lf(
        "namespace Demo.Models;",
        "",
        "public class Foo { public static int Make() => 1; }",
        "",
        "[System.AttributeUsage(System.AttributeTargets.All)]",
        "public class MarkAttribute : System.Attribute { }",
        "",
        "public class Box<T> { }");

    private static readonly (string Relative, string Content) KindsConsumer = ("Consumers/User.cs", Lf(
        "using System.Collections.Generic;",
        "using Demo.Models;",
        "",
        "namespace Demo.Consumers;",
        "",
        "[Mark]",
        "public class User : Box<Foo>",
        "{",
        "    public System.Type T = typeof(Foo);",
        "    public string N = nameof(Foo);",
        "    public List<Foo> L = new();",
        "    public int M = Foo.Make();",
        "}"));

    [Test]
    public async Task EveryReferenceKind_IsQualified_WhenQualifyIsForced()
    {
        var (rewrite, errors) = await MoveAsync(KindsSource, [KindsConsumer], Forced("Consumers/User.cs", CallerRewriteMode.Qualify));

        var file = FileOf(rewrite, "Consumers/User.cs");
        Assert.That(file.Mode, Is.EqualTo(CallerRewriteMode.Qualify), "The forced mode wins over the default.");
        Assert.That(file.NewText, Is.EqualTo(Lf(
            "using System.Collections.Generic;",
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "[Demo.Other.Mark]",
            "public class User : Demo.Other.Box<Demo.Other.Foo>",
            "{",
            "    public System.Type T = typeof(Demo.Other.Foo);",
            "    public string N = nameof(Demo.Other.Foo);",
            "    public List<Demo.Other.Foo> L = new();",
            "    public int M = Demo.Other.Foo.Make();",
            "}")), "Attribute (written without the suffix), base list, typeof, nameof, generic argument and a static member access are all found.");
        Assert.That(file.ReferencesRewritten, Is.EqualTo(7));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task EveryReferenceKind_NeedsNoTextEdit_InUsingMode_ExceptTheUsings()
    {
        var (rewrite, errors) = await MoveAsync(KindsSource, [KindsConsumer]);

        var file = FileOf(rewrite, "Consumers/User.cs");
        Assert.That(file.Mode, Is.EqualTo(CallerRewriteMode.Using));
        Assert.That(file.NewText, Is.EqualTo(KindsConsumer.Content.Replace("using Demo.Models;", "using Demo.Other;")));
        Assert.That(file.ReferencesRewritten, Is.EqualTo(7));
        Assert.That(errors, Is.Empty, "[Mark], Box, Foo and the static call all bind the new types through the new using.");
    }

    [Test]
    public async Task CrefReferences_AreFound_SimpleAndQualified()
    {
        var consumer = ("Consumers/User.cs", Lf(
            "namespace Demo.Consumers;",
            "",
            "/// <summary>See <see cref=\"Demo.Models.Foo\"/> and <see cref=\"Demo.Models.Foo.Make\"/>.</summary>",
            "public class User { }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [consumer]);

        Assert.That(FileOf(rewrite, "Consumers/User.cs").NewText, Is.EqualTo(Lf(
            "namespace Demo.Consumers;",
            "",
            "/// <summary>See <see cref=\"Demo.Other.Foo\"/> and <see cref=\"Demo.Other.Foo.Make\"/>.</summary>",
            "public class User { }")));
        Assert.That(errors, Is.Empty);

        var simple = ("Consumers/Simple.cs", Lf(
            "using Demo.Models;",
            "",
            "namespace Demo.Consumers;",
            "",
            "/// <summary>See <see cref=\"Foo\"/>.</summary>",
            "public class Simple { }"));
        var (simpleRewrite, simpleErrors) = await MoveAsync(FooSource, [simple], Forced("Consumers/Simple.cs", CallerRewriteMode.Qualify));

        Assert.That(FileOf(simpleRewrite, "Consumers/Simple.cs").NewText, Does.Contain("<see cref=\"Demo.Other.Foo\"/>"));
        Assert.That(simpleErrors, Is.Empty);
    }

    [Test]
    public async Task AliasDirective_IsWrittenOut_AndUsesThroughTheAliasAreLeftAlone()
    {
        var consumer = ("Consumers/User.cs", Lf(
            "using F = Demo.Models.Foo;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public F Value = new F(); }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [consumer]);

        var file = FileOf(rewrite, "Consumers/User.cs");
        Assert.That(file.NewText, Is.EqualTo(Lf(
            "using F = Demo.Other.Foo;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public F Value = new F(); }")));
        Assert.That(file.ReferencesRewritten, Is.EqualTo(1));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task AliasDirective_IsWrittenOut_EvenWhenTheFileUsesTheOldNamespaceForSimpleNames()
    {
        var consumer = ("Consumers/User.cs", Lf(
            "using Demo.Models;",
            "using F = Demo.Models.Foo;",
            "",
            "namespace Demo.Consumers;",
            "",
            "public class User { public F A; public Foo B; }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [consumer]);

        var file = FileOf(rewrite, "Consumers/User.cs");
        Assert.That(file.NewText, Does.Contain("using F = Demo.Other.Foo;").And.Contain("using Demo.Other;").And.Not.Contain("using Demo.Models;"));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task CallerInASecondProject_IsRewritten()
    {
        var upstream = new[] { (Abs(SourceRelative), FooSource) };
        var downstream = new[]
        {
            (AppAbs("User.cs"), Lf("using Demo.Models;", "", "namespace App;", "", "public class User { public Foo F = new Foo(); }")),
        };
        var solution = TestSolutionBuilder.CreateTwoProjectSolution("Demo", upstream, "App", downstream);

        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), "Demo.Models", "Demo.Other", ["Foo"]);

        Assert.That(rewrite.IsRefusal, Is.False, rewrite.Refusal?.Message);
        var file = rewrite.Files.Single();
        Assert.That(file.FilePath, Is.EqualTo((FilePathWrapper)AppAbs("User.cs")));
        Assert.That(file.Mode, Is.EqualTo(CallerRewriteMode.Using));
        Assert.That(file.NewText, Is.EqualTo(Lf("using Demo.Other;", "", "namespace App;", "", "public class User { public Foo F = new Foo(); }")));

        var demo = solution.Projects.Single(project => project.Name == "Demo");
        var app = solution.Projects.Single(project => project.Name == "App");
        var applied = solution
            .AddDocument(DocumentId.CreateNewId(demo.Id), "NewFoo.cs", SourceText.From(Lf("namespace Demo.Other;", "", "public class Foo { }")), filePath: Abs("Other/Foo.cs"))
            .WithDocumentText(solution.GetDocumentIdsWithFilePath(AppAbs("User.cs")).Single(), SourceText.From(file.NewText));
        var compilation = await applied.GetProject(app.Id)!.GetCompilationAsync();
        Assert.That(compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.ToString()), Is.Empty);
    }

    [Test]
    public async Task GlobalUsingOfTheOldNamespace_QualifiesEveryCaller_AndIsLeftAlone()
    {
        var globalUsing = ("GlobalUsings.cs", Lf("global using Demo.Models;"));
        var implicitCaller = ("Consumers/A.cs", Lf("namespace Demo.Consumers;", "", "public class A { public Foo F; }"));
        var explicitCaller = ("Consumers/B.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class B { public Foo F; }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [globalUsing, implicitCaller, explicitCaller]);

        Assert.That(FileOf(rewrite, "Consumers/A.cs").Mode, Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(FileOf(rewrite, "Consumers/A.cs").NewText, Is.EqualTo(Lf("namespace Demo.Consumers;", "", "public class A { public Demo.Other.Foo F; }")));
        Assert.That(FileOf(rewrite, "Consumers/B.cs").Mode, Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(FileOf(rewrite, "Consumers/B.cs").NewText, Is.EqualTo(Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class B { public Demo.Other.Foo F; }")));
        Assert.That(rewrite.Files.Any(file => file.FilePath == Abs("GlobalUsings.cs")), Is.False, "The global using file itself is not edited.");
        AssertFact(rewrite, "global using", "GlobalUsings.cs");
        Assert.That(errors, Is.Empty);
    }

    // A global using only reaches the files of its own project: here only the upstream project has one, so its caller qualifies for that reason while the caller of the
    // downstream project (which has its own 'using Demo.Models;') gets the using directive.
    [Test]
    public async Task GlobalUsingInOneProject_QualifiesThatProjectsCallers_NotTheOtherProjects()
    {
        var upstream = new[]
        {
            (Abs(SourceRelative), FooSource),
            (Abs("GlobalUsings.cs"), Lf("global using Demo.Models;")),
            (Abs("Consumers/A.cs"), Lf("namespace Demo.Consumers;", "", "public class A { public Foo F; }")),
        };
        var downstream = new[]
        {
            (AppAbs("User.cs"), Lf("using Demo.Models;", "", "namespace App;", "", "public class User { public Foo F = new Foo(); }")),
        };
        var solution = TestSolutionBuilder.CreateTwoProjectSolution("Demo", upstream, "App", downstream);

        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), "Demo.Models", "Demo.Other", ["Foo"]);

        Assert.That(rewrite.IsRefusal, Is.False, rewrite.Refusal?.Message);
        var inGlobalUsingProject = rewrite.Files.Single(file => file.FilePath == Abs("Consumers/A.cs"));
        Assert.That(inGlobalUsingProject.Mode, Is.EqualTo(CallerRewriteMode.Qualify));
        Assert.That(inGlobalUsingProject.NewText, Is.EqualTo(Lf("namespace Demo.Consumers;", "", "public class A { public Demo.Other.Foo F; }")));
        var inOtherProject = rewrite.Files.Single(file => file.FilePath == AppAbs("User.cs"));
        Assert.That(inOtherProject.Mode, Is.EqualTo(CallerRewriteMode.Using));
        Assert.That(inOtherProject.NewText, Is.EqualTo(Lf("using Demo.Other;", "", "namespace App;", "", "public class User { public Foo F = new Foo(); }")));
        AssertFact(rewrite, "global using", Abs("GlobalUsings.cs"), "in its project");
        Assert.That(rewrite.Facts.Any(fact => fact.Contains(AppAbs("User.cs"), StringComparison.Ordinal) && fact.Contains("global using", StringComparison.Ordinal)), Is.False, "The downstream caller is not qualified for the global-using reason.");
    }

    [Test]
    public async Task FilesWithoutAReference_AndGeneratedFiles_AreNotTouched()
    {
        var unrelated = ("Consumers/Unrelated.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class Unrelated { public Helper H; }"));
        var generated = ("Gen/Api.g.cs", Lf("namespace Demo.Gen;", "", "public static class Api { public static void Take(Demo.Models.Foo foo) { } }"));
        var headerGenerated = ("Gen/Other.cs", Lf("// <auto-generated>", "// by a tool", "// </auto-generated>", "namespace Demo.Gen;", "", "public static class Other { public static void Take(Demo.Models.Foo foo) { } }"));
        var caller = ("Consumers/User.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class User { public Foo F; }"));

        var (rewrite, errors) = await MoveAsync(FooSource, [HelperFile, unrelated, generated, headerGenerated, caller]);

        Assert.That(rewrite.Files.Select(file => file.FilePath.Absolute), Is.EqualTo(new[] { Abs("Consumers/User.cs") }).IgnoreCase, "Only the real caller changes; the source, the unrelated file and both generated files do not.");
        AssertFact(rewrite, "Api.g.cs", "generated");
        AssertFact(rewrite, "Other.cs", "generated");
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task ExcludedPaths_AreNotTouched()
    {
        var caller = ("Consumers/User.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class User { public Foo F; }"));
        var solution = CreateSolution(FooSource, caller);

        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), "Demo.Models", "Demo.Other", ["Foo"], excludedPaths: [Abs("Consumers/User.cs")]);

        Assert.That(rewrite.IsRefusal, Is.False, rewrite.Refusal?.Message);
        Assert.That(rewrite.Files, Is.Empty);
    }

    [Test]
    public async Task BomAndLineEndings_ArePreserved_AndTheNewUsingUsesTheFilesLineEnding()
    {
        const string bom = "﻿";
        var crlf = ("Consumers/Crlf.cs", bom + Join("\r\n", "using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class Crlf { public Foo F; }"));
        var lf = ("Consumers/Lf.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class Lf { public Foo F; }"));
        var qualified = ("Consumers/Qualified.cs", bom + Join("\r\n", "using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class Qualified { public Foo F; public Helper H; }"));
        var solution = CreateSolution(FooSource, HelperFile, crlf, lf, qualified);

        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), "Demo.Models", "Demo.Other", ["Foo"]);

        Assert.That(rewrite.IsRefusal, Is.False, rewrite.Refusal?.Message);
        Assert.That(FileOf(rewrite, "Consumers/Crlf.cs").NewText, Is.EqualTo(bom + Join("\r\n", "using Demo.Other;", "", "namespace Demo.Consumers;", "", "public class Crlf { public Foo F; }")), "BOM kept in front, CRLF used for the new line.");
        Assert.That(FileOf(rewrite, "Consumers/Lf.cs").NewText, Is.EqualTo(Lf("using Demo.Other;", "", "namespace Demo.Consumers;", "", "public class Lf { public Foo F; }")), "LF file stays LF.");
        Assert.That(FileOf(rewrite, "Consumers/Qualified.cs").NewText, Is.EqualTo(bom + Join("\r\n", "using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class Qualified { public Demo.Other.Foo F; public Helper H; }")), "Qualify mode splices in place and keeps the BOM and CRLF.");
    }

    [Test]
    public async Task UnknownTypeName_IsAFact_WhenAnotherListedTypeIsFound()
    {
        var caller = ("Consumers/User.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class User { public Foo F; }"));
        var solution = CreateSolution(FooSource, caller);

        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), "Demo.Models", "Demo.Other", ["Foo", "Nope"]);

        Assert.That(rewrite.IsRefusal, Is.False, rewrite.Refusal?.Message);
        Assert.That(rewrite.Files, Has.Count.EqualTo(1));
        AssertFact(rewrite, "'Nope'");
    }

    private static void AssertFactsAndOptions(CallerRewriteResult result, string errorCode)
    {
        Assert.That(result.IsRefusal, Is.True);
        Assert.That(result.Files, Is.Empty);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(errorCode));
        Assert.That(result.Refusal.Message, Does.Contain("Options:"));
        Assert.That(result.Refusal.Message, Does.Not.Contain("must").IgnoreCase.And.Not.Contain("stop").IgnoreCase.And.Not.Contain("do not").IgnoreCase);
    }

    [Test]
    public async Task Refused_EmptyNewNamespace_IsNamed()
    {
        var solution = CreateSolution(FooSource);

        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), "Demo.Models", string.Empty, ["Foo"]);

        AssertFactsAndOptions(rewrite, ToolErrorCode.InvalidArgument);
        Assert.That(rewrite.Refusal!.Message, Does.Contain("global namespace"));
    }

    [Test]
    public async Task Refused_NoListedTypeIsDeclared_IsNamed()
    {
        var solution = CreateSolution(FooSource);

        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), "Demo.Models", "Demo.Other", ["Nope"]);

        AssertFactsAndOptions(rewrite, ToolErrorCode.PreconditionNotMet);
        Assert.That(rewrite.Refusal!.Message, Does.Contain("'Nope'"));
    }

    [Test]
    public async Task Refused_SourceNotInTheSolution_PassesTheLookupErrorThrough()
    {
        var solution = CreateSolution(FooSource);

        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs("Models/Missing.cs"), "Demo.Models", "Demo.Other", ["Foo"]);

        Assert.That(rewrite.IsRefusal, Is.True);
        Assert.That(rewrite.Refusal!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task Refused_RefusedCheck_PassesItsRefusalThrough()
    {
        var solution = CreateSolution(FooSource);
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, Abs(SourceRelative), Abs("Models/Foo2.cs"));

        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), check);

        Assert.That(rewrite.IsRefusal, Is.True);
        Assert.That(rewrite.Refusal, Is.SameAs(check.Refusal));
    }

    [Test]
    public async Task EndToEnd_HappyCallersValidateClean_AndACallerThatCannotMigrateGainsAnAttributedError()
    {
        var happy = ("Consumers/Happy.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class Happy { public Foo F = new Foo(); }"));
        var sibling = ("Models/Sibling.cs", Lf("namespace Demo.Models;", "", "public class Sibling { public Foo F = new Foo(); }"));

        // Api.g.cs is generated, so the pass leaves it taking the old Foo; Bad passes it a Foo, which after the rewrite is the new one.
        var api = ("Gen/Api.g.cs", Lf("namespace Demo.Gen;", "", "public static class Api { public static void Take(Demo.Models.Foo foo) { } }"));
        var bad = ("Consumers/Bad.cs", Lf("using Demo.Gen;", "using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class Bad { public void Run() { Api.Take(new Foo()); } }"));

        var solution = CreateSolution(FooSource, happy, sibling, api, bad);
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, Abs(SourceRelative), Abs("Other/Foo.cs"));
        Assert.That(check.IsRefusal, Is.False, check.Refusal?.Message);
        var edits = await CopyAndObsoleteEdits.BuildAsync(solution, Abs(SourceRelative), FooSource, check);
        Assert.That(edits.IsRefusal, Is.False, edits.Refusal?.Message);
        var rewrite = await CopyAndObsoleteCallerRewrite.RewriteAsync(solution, Abs(SourceRelative), check);
        Assert.That(rewrite.IsRefusal, Is.False, rewrite.Refusal?.Message);

        Assert.That(rewrite.Files.Select(file => file.FilePath.Absolute), Is.EquivalentTo(new[] { Abs("Consumers/Happy.cs"), Abs("Models/Sibling.cs"), Abs("Consumers/Bad.cs") }).IgnoreCase);
        var changes = new Dictionary<FilePathWrapper, string>
        {
            [Abs(SourceRelative)] = edits.OldText!,
            [Abs("Other/Foo.cs")] = edits.NewText!,
        };
        foreach (var file in rewrite.Files.Where(file => file.FilePath != Abs("Consumers/Bad.cs")))
        {
            changes[file.FilePath] = file.NewText;
        }

        var happyOnly = await ValidationEngine.ValidateChangesAsync(solution, changes);

        Assert.That(happyOnly.IsError, Is.False, string.Join("; ", happyOnly.Diagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.FilePath} {diagnostic.Message}")));

        changes[Abs("Consumers/Bad.cs")] = rewrite.Files.Single(file => file.FilePath == Abs("Consumers/Bad.cs")).NewText;
        var withBad = await ValidationEngine.ValidateChangesAsync(solution, changes);

        Assert.That(withBad.IsError, Is.True, "Bad now passes the new Foo to an API that still takes the old one.");
        Assert.That(withBad.Diagnostics, Is.Not.Empty);
        Assert.That(withBad.Diagnostics.Select(diagnostic => diagnostic.FilePath.Absolute), Is.All.EqualTo(Abs("Consumers/Bad.cs")).IgnoreCase, "Every introduced error is attributed to the one caller the loop would revert.");
        Assert.That(withBad.Diagnostics.Select(diagnostic => diagnostic.Id), Does.Contain("CS1503"));
    }
}
