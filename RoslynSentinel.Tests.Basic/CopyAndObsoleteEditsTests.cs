using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for <see cref="CopyAndObsoleteEdits.BuildAsync(Solution, FilePathWrapper, string, CopyAndObsoleteCheckResult, CancellationToken)"/>: the namespace swap
/// (file-scoped, block, global), the using directives the copy needs, the [Obsolete] marks on the original, byte preservation (BOM, line endings) and an
/// end-to-end compile of both texts. In-memory only: the paths sit under the scratch root but nothing is written there.
/// </summary>
[TestFixture]
[Category("CopyAndObsoleteEdits")] // sentinel:auto-category
public class CopyAndObsoleteEditsTests
{
    private static readonly string ProjectDirectory = Path.Combine(ScratchRoot.Resolve(), "CopyAndObsoleteEditsTests", "Demo");

    // A project-relative path written with '/' becomes an absolute path with the platform's separators.
    private static string Abs(string relativePath) => Path.Combine(ProjectDirectory, Path.Combine(relativePath.Split('/')));

    // Lines joined with the given line ending, plus a final one: the tests spell file text this way so it does not depend on this file's own line endings.
    private static string Join(string eol, params string[] lines) => string.Join(eol, lines) + eol;

    private static string Lf(params string[] lines) => Join("\n", lines);

    private static string Attribute(string typeName) => $"[System.Obsolete(\"Use Demo.Other.{typeName}\")]";

    private static string Norm(string? text) => (text ?? string.Empty).Replace("\r\n", "\n");

    private static Solution CreateSolution(string sourceRelative, string sourceText, params (string Relative, string Content)[] others)
    {
        var documents = new[] { (Relative: sourceRelative, Content: sourceText) }.Concat(others)
            .Select(file => (name: Path.GetFileName(file.Relative), content: file.Content, filePath: Abs(file.Relative)))
            .ToArray();
        return TestSolutionBuilder.CreateSolutionWithProject("Demo", Path.Combine(ProjectDirectory, "Demo.csproj"), documents);
    }

    // Checks source -> destination (an accepted check is asserted), then builds the edits from the text actually written (writtenText, default the loaded text).
    private static async Task<CopyAndObsoleteEditResult> BuildAsync(string sourceRelative, string sourceText, string destination, (string Relative, string Content)[] others, string? writtenText = null)
    {
        var solution = CreateSolution(sourceRelative, sourceText, others);
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, Abs(sourceRelative), Abs(destination));
        Assert.That(check.IsRefusal, Is.False, check.Refusal?.Message);
        return await CopyAndObsoleteEdits.BuildAsync(solution, Abs(sourceRelative), writtenText ?? sourceText, check);
    }

    private static Task<CopyAndObsoleteEditResult> BuildAsync(string sourceRelative, string sourceText, string destination) => BuildAsync(sourceRelative, sourceText, destination, []);

    private static void AssertFactsAndOptions(CopyAndObsoleteEditResult result, string errorCode)
    {
        Assert.That(result.IsRefusal, Is.True);
        Assert.That(result.NewText, Is.Null);
        Assert.That(result.OldText, Is.Null);
        Assert.That(result.Refusal!.ErrorCode, Is.EqualTo(errorCode));
        Assert.That(result.Refusal.Message, Does.Contain("Options:"));
        Assert.That(result.Refusal.Message, Does.Not.Contain("must").IgnoreCase.And.Not.Contain("stop").IgnoreCase.And.Not.Contain("do not").IgnoreCase);
    }

    private static readonly (string Relative, string Content) HelperFile = ("Models/Helper.cs", Lf("namespace Demo.Models;", "public class Helper { }"));

    private static readonly (string Relative, string Content) ExtensionFile = ("Models/Ext.cs", Lf("namespace Demo.Models;", "public static class Ext { public static int Twice(this int value) => value * 2; }"));

    [Test]
    public async Task FileScopedNamespace_IsSwappedInTheCopy_AndTheOldTypeIsMarked()
    {
        var result = await BuildAsync("Models/Foo.cs", Lf("namespace Demo.Models;", "", "public class Foo { }"), "Other/Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.NewText, Is.EqualTo(Lf("namespace Demo.Other;", "", "public class Foo { }")), "The copy has the new namespace and carries no [Obsolete].");
        Assert.That(result.OldText, Is.EqualTo(Lf("namespace Demo.Models;", "", Attribute("Foo"), "public class Foo { }")));
        Assert.That(result.AddedUsings, Is.Empty);
    }

    [Test]
    public async Task BlockNamespace_IsSwappedInTheCopy_AndTheAttributeKeepsTheIndent()
    {
        var result = await BuildAsync("Models/Foo.cs", Lf("namespace Demo.Models", "{", "    public class Foo { }", "}"), "Other/Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.NewText, Is.EqualTo(Lf("namespace Demo.Other", "{", "    public class Foo { }", "}")));
        Assert.That(result.OldText, Is.EqualTo(Lf("namespace Demo.Models", "{", "    " + Attribute("Foo"), "    public class Foo { }", "}")));
    }

    [Test]
    public async Task GlobalNamespaceSource_GetsAFileScopedNamespaceAfterTheUsings()
    {
        var result = await BuildAsync("Foo.cs", Lf("using System;", "", "public class Foo { }"), "Other/Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.NewText, Is.EqualTo(Lf("using System;", "", "namespace Demo.Other;", "", "public class Foo { }")));
        Assert.That(result.OldText, Is.EqualTo(Lf("using System;", "", Attribute("Foo"), "public class Foo { }")));
    }

    [Test]
    public async Task GlobalNamespaceSource_WithoutUsings_KeepsTheHeaderCommentOnTopAndTheDocCommentOnTheType()
    {
        var result = await BuildAsync("Foo.cs", Lf("// header", "", "/// <summary>The foo.</summary>", "public class Foo { }"), "Other/Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.NewText, Is.EqualTo(Lf("// header", "", "namespace Demo.Other;", "", "/// <summary>The foo.</summary>", "public class Foo { }")));
        Assert.That(result.OldText, Is.EqualTo(Lf("// header", "", "/// <summary>The foo.</summary>", Attribute("Foo"), "public class Foo { }")));
    }

    [Test]
    public async Task GlobalNamespaceSource_WithUsingsDirectlyAboveTheType_GetsABlankLineBeforeTheNamespace()
    {
        var result = await BuildAsync("Foo.cs", Lf("using System;", "public class Foo { }"), "Other/Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.NewText, Is.EqualTo(Lf("using System;", "", "namespace Demo.Other;", "", "public class Foo { }")));
    }

    [Test]
    public async Task ImplicitAncestorAndOldNamespaceTypes_GetUsings_ButANamespaceTheNewOneStillSeesDoesNot()
    {
        var source = Lf(
            "using System;",
            "",
            "namespace Demo.Models.Sub;",
            "",
            "public class Widget : Helper",
            "{",
            "    private Sibling _sibling = new Sibling();",
            "    public int N => Convert.ToInt32(Root.Value);",
            "}");
        var others = new[]
        {
            HelperFile,
            ("Models/Sub/Sibling.cs", Lf("namespace Demo.Models.Sub;", "public class Sibling { }")),
            ("Root.cs", Lf("namespace Demo;", "public class Root { public const int Value = 1; }")),
        };

        var result = await BuildAsync("Models/Sub/Widget.cs", source, "Other/Widget.cs", others);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.AddedUsings, Is.EqualTo(new[] { "Demo.Models", "Demo.Models.Sub" }), "Helper sits in an ancestor of the old namespace, Sibling in the old namespace; Root sits in Demo, which Demo.Other also sees.");
        Assert.That(result.NewText, Does.Contain(Lf("using System;", "using Demo.Models;", "using Demo.Models.Sub;", "", "namespace Demo.Other;")));
        Assert.That(result.NewText, Does.Not.Contain("using Demo;"));
    }

    [Test]
    public async Task ExistingUsing_IsKeptOnce_AndNewUsingsLandInSortedPosition()
    {
        var source = Lf(
            "using System;",
            "using Zeta.Stuff;",
            "",
            "namespace Demo.Models;",
            "",
            "public class User : Helper",
            "{",
            "    public int Run() => 3.Twice();",
            "}");
        var withExistingUsing = source.Replace("using Zeta.Stuff;", "using Demo.Models;");

        var sorted = await BuildAsync("Models/User.cs", source, "Other/User.cs", [HelperFile, ExtensionFile]);
        var duplicate = await BuildAsync("Models/User.cs", withExistingUsing, "Other/User.cs", [HelperFile, ExtensionFile]);

        Assert.That(sorted.IsRefusal, Is.False, sorted.Refusal?.Message);
        Assert.That(sorted.AddedUsings, Is.EqualTo(new[] { "Demo.Models" }), "Helper and the extension method are both in Demo.Models: one using.");
        Assert.That(sorted.NewText, Does.Contain(Lf("using System;", "using Demo.Models;", "using Zeta.Stuff;")), "System sorts first, then ordinal: Demo.Models goes after System and before Zeta.Stuff.");
        Assert.That(duplicate.IsRefusal, Is.False, duplicate.Refusal?.Message);
        Assert.That(duplicate.AddedUsings, Is.Empty);
        Assert.That(Regex.Matches(duplicate.NewText!, "using Demo\\.Models;").Count, Is.EqualTo(1));
        Assert.That(duplicate.NewText, Does.Contain(Lf("using System;", "using Demo.Models;", "", "namespace Demo.Other;")), "Existing usings stay where they were.");
    }

    [Test]
    public async Task ExtensionMethodFromTheOldNamespace_GetsAUsing()
    {
        var source = Lf("namespace Demo.Models;", "", "public class User", "{", "    public int Run() => 3.Twice();", "}");

        var result = await BuildAsync("Models/User.cs", source, "Other/User.cs", [ExtensionFile]);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.AddedUsings, Is.EqualTo(new[] { "Demo.Models" }));
        Assert.That(result.NewText, Is.EqualTo(Lf("using Demo.Models;", "", "namespace Demo.Other;", "", "public class User", "{", "    public int Run() => 3.Twice();", "}")));
    }

    private static readonly (string Relative, string Content) EnumeratorExtensionFile = ("Models/CountExt.cs", Lf(
        "namespace Demo.Models;",
        "public static class CountExt",
        "{",
        "    public static System.Collections.Generic.IEnumerator<int> GetEnumerator(this int count) { for (var i = 0; i < count; i++) { yield return i; } }",
        "}"));

    [Test]
    public async Task ForeachOverAnExtensionGetEnumerator_GetsAUsing()
    {
        var source = Lf("namespace Demo.Models;", "", "public class User", "{", "    public int Run() { var sum = 0; foreach (var i in 3) { sum += i; } return sum; }", "}");

        var result = await BuildAsync("Models/User.cs", source, "Other/User.cs", [EnumeratorExtensionFile]);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.AddedUsings, Is.EqualTo(new[] { "Demo.Models" }), "The foreach names no type, but its GetEnumerator is an extension method of Demo.Models.CountExt.");
        Assert.That(result.NewText, Does.StartWith(Lf("using Demo.Models;", "", "namespace Demo.Other;")));
    }

    [Test]
    public async Task CollectionInitializerWithAnExtensionAdd_GetsAUsing()
    {
        var bag = ("Bag.cs", Lf("namespace Demo;", "public class Bag : System.Collections.IEnumerable { public System.Collections.IEnumerator GetEnumerator() => throw new System.NotSupportedException(); }"));
        var bagExtension = ("Models/BagExt.cs", Lf("namespace Demo.Models;", "public static class BagExt { public static void Add(this Bag bag, int value) { } }"));
        var source = Lf("namespace Demo.Models;", "", "public class User", "{", "    public object Run() => new Bag { 1, 2 };", "}");

        var result = await BuildAsync("Models/User.cs", source, "Other/User.cs", [bag, bagExtension]);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.AddedUsings, Is.EqualTo(new[] { "Demo.Models" }), "Bag sits in Demo, which Demo.Other also sees; only the extension Add needs the old namespace.");
    }

    [Test]
    public async Task DeconstructionWithAnExtensionDeconstruct_GetsAUsing()
    {
        var pair = ("Pair.cs", Lf("namespace Demo;", "public class Pair { }"));
        var pairExtension = ("Models/PairExt.cs", Lf("namespace Demo.Models;", "public static class PairExt { public static void Deconstruct(this Pair pair, out int a, out int b) { a = 1; b = 2; } }"));
        var source = Lf("namespace Demo.Models;", "", "public class User", "{", "    public int Run() { var (a, b) = new Pair(); return a + b; }", "}");

        var result = await BuildAsync("Models/User.cs", source, "Other/User.cs", [pair, pairExtension]);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.AddedUsings, Is.EqualTo(new[] { "Demo.Models" }));
    }

    [Test]
    public async Task TypeNamedOnlyInADocCommentCref_GetsAUsing()
    {
        var source = Lf("namespace Demo.Models;", "", "/// <summary>Works with <see cref=\"Helper\"/>.</summary>", "public class User { }");

        var result = await BuildAsync("Models/User.cs", source, "Other/User.cs", [HelperFile]);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.AddedUsings, Is.EqualTo(new[] { "Demo.Models" }), "The workspace parses documents with DocumentationMode.Parse, so the cref name binds like any other name.");
    }

    [Test]
    public async Task AttributeFromTheOldNamespace_GetsAUsing_AndTheMarkGoesAboveIt()
    {
        var source = Lf("namespace Demo.Models;", "", "[Mark]", "public class User { }");
        var attributeFile = ("Models/MarkAttribute.cs", Lf("namespace Demo.Models;", "public class MarkAttribute : System.Attribute { }"));

        var result = await BuildAsync("Models/User.cs", source, "Other/User.cs", [attributeFile]);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.AddedUsings, Is.EqualTo(new[] { "Demo.Models" }));
        Assert.That(result.OldText, Is.EqualTo(Lf("namespace Demo.Models;", "", Attribute("User"), "[Mark]", "public class User { }")));
    }

    [Test]
    public async Task TypesDeclaredInTheFile_AndTheFullyQualifiedOldNamespaceTypes_NeedNoUsing()
    {
        var source = Lf("namespace Demo.Models;", "", "public class A : B { }", "public class B { public Demo.Models.Helper H = new Demo.Models.Helper(); }");

        var result = await BuildAsync("Models/AB.cs", source, "Other/AB.cs", [HelperFile]);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.AddedUsings, Is.Empty);
        Assert.That(result.NewText, Does.Not.Contain("using"));
    }

    [Test]
    public async Task Bom_IsPreservedInBothTexts()
    {
        var plain = Lf("namespace Demo.Models;", "", "public class Foo { }");

        var result = await BuildAsync("Models/Foo.cs", plain, "Other/Foo.cs", [], writtenText: "﻿" + plain);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.NewText, Is.EqualTo("﻿" + Lf("namespace Demo.Other;", "", "public class Foo { }")));
        Assert.That(result.OldText, Is.EqualTo("﻿" + Lf("namespace Demo.Models;", "", Attribute("Foo"), "public class Foo { }")));
    }

    [Test]
    public async Task CrlfFile_StaysCrlf_AndLfFile_StaysLf()
    {
        string[] lines = ["namespace Demo.Models;", "", "/// <summary>The user.</summary>", "public class User", "{", "    public int Run() => 3.Twice();", "}"];

        var crlf = await BuildAsync("Models/User.cs", Join("\r\n", lines), "Other/User.cs", [ExtensionFile]);
        var lf = await BuildAsync("Models/User.cs", Join("\n", lines), "Other/User.cs", [ExtensionFile]);

        Assert.That(crlf.IsRefusal, Is.False, crlf.Refusal?.Message);
        Assert.That(lf.IsRefusal, Is.False, lf.Refusal?.Message);
        Assert.That(crlf.AddedUsings, Is.EqualTo(new[] { "Demo.Models" }));
        foreach (var text in new[] { crlf.NewText!, crlf.OldText! })
        {
            Assert.That(Regex.IsMatch(text, "(?<!\r)\n"), Is.False, "A CRLF file gets no bare LF.");
        }

        foreach (var text in new[] { lf.NewText!, lf.OldText! })
        {
            Assert.That(text, Does.Not.Contain("\r"), "An LF file gets no CR.");
        }

        Assert.That(Norm(crlf.NewText), Is.EqualTo(lf.NewText));
        Assert.That(Norm(crlf.OldText), Is.EqualTo(lf.OldText));
    }

    [Test]
    public async Task EveryTopLevelTypeKind_GetsItsOwnObsoleteWithTheRightText()
    {
        string[] names = ["A", "B", "C", "D", "E", "F", "G"];
        string[] declarations =
        [
            "public class A { }",
            "public enum B { X }",
            "public record C(int V);",
            "public struct D { }",
            "public interface E { }",
            "public delegate void F();",
            "public record struct G(int V);",
        ];
        var source = Lf(["namespace Demo.Models;", "", .. declarations]);
        var expectedOld = Lf(["namespace Demo.Models;", "", .. declarations.Select((declaration, index) => Attribute(names[index]) + "\n" + declaration)]);

        var result = await BuildAsync("Models/Types.cs", source, "Other/Types.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.OldText, Is.EqualTo(expectedOld));
        Assert.That(result.NewText, Is.EqualTo(source.Replace("Demo.Models", "Demo.Other")));
        Assert.That(result.AlreadyObsoleteTypeNames, Is.Empty);
    }

    [Test]
    public async Task TypeThatAlreadyCarriesObsolete_IsLeftUnchanged_AndNamed()
    {
        var source = Lf(
            "using System;",
            "",
            "namespace Demo.Models;",
            "",
            "[Obsolete(\"old\")]",
            "public class A { }",
            "",
            "[System.ObsoleteAttribute]",
            "public class B { }",
            "",
            "public class C { }");
        var expectedOld = source.Replace("public class C { }", Attribute("C") + "\npublic class C { }");

        var result = await BuildAsync("Models/Types.cs", source, "Other/Types.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.OldText, Is.EqualTo(expectedOld));
        Assert.That(result.AlreadyObsoleteTypeNames, Is.EqualTo(new[] { "A", "B" }));
    }

    [Test]
    public async Task DocComment_StaysAboveTheAttribute_AndAnExistingAttributeListStaysBelowIt()
    {
        var source = Lf("using System;", "", "namespace Demo.Models;", "", "/// <summary>The foo.</summary>", "[Serializable]", "public sealed class Foo { }");

        var result = await BuildAsync("Models/Foo.cs", source, "Other/Foo.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.OldText, Does.Contain(Lf("/// <summary>The foo.</summary>", Attribute("Foo"), "[Serializable]", "public sealed class Foo { }")));
        Assert.That(result.NewText, Does.Contain(Lf("/// <summary>The foo.</summary>", "[Serializable]", "public sealed class Foo { }")));
    }

    [Test]
    public async Task StaticFieldTypes_AreReportedFromTheCheckedTypes()
    {
        var source = Lf("namespace Demo.Models;", "", "public class Counter { public static int Total; }", "public class Plain { }");

        var result = await BuildAsync("Models/Counter.cs", source, "Other/Counter.cs");

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.StaticFieldTypeNames, Is.EqualTo(new[] { "Counter" }));
    }

    [Test]
    public async Task EditsBindTheTextToBeWritten_NotTheLoadedText()
    {
        // The workspace still holds the text without the Helper reference; the text to be written has it.
        var loaded = Lf("namespace Demo.Models;", "", "public class Foo { }");
        var written = Lf("namespace Demo.Models;", "", "public class Foo : Helper { }");

        var result = await BuildAsync("Models/Foo.cs", loaded, "Other/Foo.cs", [HelperFile], writtenText: written);

        Assert.That(result.IsRefusal, Is.False, result.Refusal?.Message);
        Assert.That(result.AddedUsings, Is.EqualTo(new[] { "Demo.Models" }));
        Assert.That(result.NewText, Does.Contain("public class Foo : Helper { }"));
    }

    [Test]
    public async Task Refused_NestedNamespaceBlocks_AreNamed()
    {
        var result = await BuildAsync("Models/Foo.cs", "namespace Demo { namespace Models { public class Foo { } } }", "Other/Foo.cs");

        AssertFactsAndOptions(result, ToolErrorCode.TargetIneligible);
        Assert.That(result.Refusal!.Message, Does.Contain("nested namespace blocks").And.Contain("'Demo.Models'"));
    }

    [Test]
    public async Task Refused_PartiallyQualifiedNamespaceName_IsNamed()
    {
        var source = Lf("namespace Demo.Models.Sub;", "", "public class Widget { private Sub.Sibling _sibling = new Sub.Sibling(); }");
        var sibling = ("Models/Sub/Sibling.cs", Lf("namespace Demo.Models.Sub;", "public class Sibling { }"));

        var result = await BuildAsync("Models/Sub/Widget.cs", source, "Other/Widget.cs", [sibling]);

        AssertFactsAndOptions(result, ToolErrorCode.TargetIneligible);
        Assert.That(result.Refusal!.Message, Does.Contain("'Sub'").And.Contain("partial qualification").And.Contain("'Demo.Other'"));
    }

    [Test]
    public async Task Refused_OwnTypeQualifiedByTheOldNamespace_IsNamed()
    {
        var source = Lf("namespace Demo.Models;", "", "public class Foo { public static Demo.Models.Foo Create() => new Demo.Models.Foo(); }");

        var result = await BuildAsync("Models/Foo.cs", source, "Other/Foo.cs");

        AssertFactsAndOptions(result, ToolErrorCode.TargetIneligible);
        Assert.That(result.Refusal!.Message, Does.Contain("'Foo'").And.Contain("namespace qualification"));
    }

    [Test]
    public async Task Refused_GlobalNamespaceFileWithTopLevelStatements_IsNamed()
    {
        var source = Lf("System.Console.WriteLine(1);", "", "public class Foo { }");

        var result = await BuildAsync("Foo.cs", source, "Other/Foo.cs");

        AssertFactsAndOptions(result, ToolErrorCode.TargetIneligible);
        Assert.That(result.Refusal!.Message, Does.Contain("GlobalStatement"));
    }

    [Test]
    public async Task Refused_EmptyNewNamespace_IsNamed()
    {
        var text = Lf("namespace Demo.Models;", "", "public class Foo { }");
        var solution = CreateSolution("Models/Foo.cs", text);

        var result = await CopyAndObsoleteEdits.BuildAsync(solution, Abs("Models/Foo.cs"), text, "Demo.Models", string.Empty, [new TopLevelType("class", "Foo", false)]);

        AssertFactsAndOptions(result, ToolErrorCode.InvalidArgument);
        Assert.That(result.Refusal!.Message, Does.Contain("global namespace"));
    }

    [Test]
    public async Task Refused_RefusedCheck_PassesItsRefusalThrough()
    {
        var solution = CreateSolution("Models/Foo.cs", Lf("namespace Demo.Models;", "public class Foo { }"));
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, Abs("Models/Foo.cs"), Abs("Models/Foo2.cs"));

        var result = await CopyAndObsoleteEdits.BuildAsync(solution, Abs("Models/Foo.cs"), "namespace Demo.Models;", check);

        Assert.That(result.IsRefusal, Is.True);
        Assert.That(result.Refusal, Is.SameAs(check.Refusal));
    }

    [Test]
    public async Task EndToEnd_BothTextsCompile_AndOnlyReferencesFromOtherFilesWarnObsolete()
    {
        var source = Lf(
            "namespace Demo.Models;",
            "",
            "public class Foo",
            "{",
            "    public static int Count;",
            "    public Helper Make() => new Helper();",
            "}",
            "",
            "public class Bar : Foo { }");
        var consumer = ("Consumers/User.cs", Lf("using Demo.Models;", "", "namespace Demo.Consumers;", "", "public class User { public Foo F = new Foo(); }"));
        var solution = CreateSolution("Models/Foo.cs", source, HelperFile, consumer);
        var check = await CopyAndObsoleteChecks.CheckAsync(solution, Abs("Models/Foo.cs"), Abs("Other/Foo.cs"));
        Assert.That(check.IsRefusal, Is.False, check.Refusal?.Message);

        var edits = await CopyAndObsoleteEdits.BuildAsync(solution, Abs("Models/Foo.cs"), source, check);

        Assert.That(edits.IsRefusal, Is.False, edits.Refusal?.Message);
        Assert.That(edits.StaticFieldTypeNames, Is.EqualTo(new[] { "Foo" }));
        var project = solution.Projects.Single();
        var oldId = solution.GetDocumentIdsWithFilePath(Abs("Models/Foo.cs")).Single();
        var applied = solution
            .WithDocumentText(oldId, SourceText.From(edits.OldText!))
            .AddDocument(DocumentId.CreateNewId(project.Id), "Foo.cs", SourceText.From(edits.NewText!), filePath: Abs("Other/Foo.cs"));
        var compilation = await applied.GetProject(project.Id)!.GetCompilationAsync();
        var diagnostics = compilation!.GetDiagnostics();

        Assert.That(diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.ToString()), Is.Empty, "Both files compile.");
        var obsoleteWarnings = diagnostics.Where(diagnostic => diagnostic.Id == "CS0618").ToList();
        Assert.That(obsoleteWarnings, Is.Not.Empty, "The consumer still binds the old Foo.");
        Assert.That(obsoleteWarnings.Select(diagnostic => diagnostic.Location.SourceTree?.FilePath), Is.All.EqualTo(Abs("Consumers/User.cs")).IgnoreCase, "Only the other file warns; the old file's own references do not.");
    }
}
