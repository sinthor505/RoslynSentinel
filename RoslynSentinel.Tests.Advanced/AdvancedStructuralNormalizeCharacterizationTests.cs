using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Slice C7a characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins (LF, CRLF and Mixed output) for the <c>StructuralRefactoringEngine</c> edit paths that now end in
/// the EOL-aware <c>RoslynFormattingHelper</c> helpers (formerly a whole-subtree whitespace pass): ConvertAbstractClassToInterface,
/// ReplaceConstructorWithFactory, ExtractSuperclass, InlineClass (same file and cross file), UpdateTypeReferences
/// (through a cross-file InlineClass) and RunMicroRefactoring. Each input has untouched, irregularly spaced code
/// (<c>UntouchedLine</c>). A line with a trailing "~" ends in a stray CRLF in the "Mixed" (otherwise LF) variant.
/// The pins encode the migrated contract (annotated-span formatting): untouched lines keep their text and terminator,
/// blank lines and comments survive, new nodes (the interface, the factory, the inlined members, the constant, the
/// braces) are formatted whole, and changed lines take the document's dominant line ending. The pre-migration
/// whole-subtree pass emitted LF for every input, re-spaced and split every untouched member, dropped blank lines between
/// members, lost the constructor's leading comment and the interface members' comments, and split
/// <c>if (x > 0) DoWork();</c> lines that no refactoring touched.
/// <para>
/// Slice C7b adds ExtractMembersToPartial, InlineMethod (with and without callers), ConvertTupleToClass and
/// ConvertAnonymousToNamed. New files (FormatNewRootKeepingTriviaAsync) keep the embedded usings, members and element
/// types byte-exact, format only the synthesized structure, follow the source's namespace kind and end with a line
/// break. Before C7b they were LF-only (a CRLF doc comment kept its CRLF), always used a block namespace, re-spaced the
/// moved members and ended without a line break; InlineMethod and ConvertTupleToClass re-spaced and split every
/// untouched member of the files they edited, and the anonymous-type class had no trailing line break.
/// </para>
/// </summary>
[TestFixture]
[Category("StructuralRefactoringEngine")] // sentinel:auto-category
public class AdvancedStructuralNormalizeCharacterizationTests
{
    private const string UntouchedLine = "    public void  Untouched( )  { }~";

    private IWorkspaceManager _workspaceManager;
    private StructuralRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new StructuralRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSources(params (string Name, string Content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>
    /// Joins lines without a trailing newline: LF, CRLF, or "Mixed" (LF, except that a line written with a trailing "~"
    /// ends in CRLF). The "~" marker is stripped in every variant.
    /// </summary>
    private static string Build(string eol, params string[] lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var odd = line.EndsWith('~');
            sb.Append(odd ? line[..^1] : line);
            if (i == lines.Length - 1)
            {
                break;
            }

            var crlf = eol == "CRLF" || (eol == "Mixed" && odd);
            sb.Append(crlf ? "\r\n" : "\n");
        }

        return sb.ToString();
    }

    /// <summary>Writes the actual text with visible line endings (NUnit clips long diffs), then compares byte for byte.</summary>
    private static void AssertPinned(string? actual, string expected)
    {
        TestContext.Out.WriteLine((actual ?? "<null>").Replace("\r", "\\r").Replace("\n", "\\n\n"));
        Assert.That(actual, Is.EqualTo(expected).NoClip);
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertAbstractClassToInterface_ByteExact(string eol)
    {
        SetSources(("Test.cs", Build(eol,
            "public abstract class Animal",
            "{",
            "    public abstract void Speak();",
            "",
            "    // legs",
            "    public abstract int  Legs( );",
            "}",
            "",
            "public class Other",
            "{",
            UntouchedLine,
            "}")));

        var result = await _engine.ConvertAbstractClassToInterfaceAsync("Test.cs", "Animal");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public interface IAnimal",
            "{",
            "    void Speak();",
            "",
            "    // legs",
            "    int Legs();",
            "}",
            "",
            "public class Other",
            "{",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ReplaceConstructorWithFactory_ByteExact(string eol)
    {
        SetSources(("Test.cs", Build(eol,
            "public class Widget",
            "{",
            "    private int _id;",
            "",
            "    // the constructor",
            "    public Widget(int id)",
            "    {",
            "        _id = id;",
            "    }",
            "",
            UntouchedLine,
            "}")));

        var result = await _engine.ReplaceConstructorWithFactoryAsync("Test.cs", "Widget");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Widget",
            "{",
            "    private int _id;",
            "",
            "    // the constructor",
            "    private Widget(int id)",
            "    {",
            "        _id = id;",
            "    }",
            "",
            UntouchedLine,
            "",
            "    public static Widget Create(int id)",
            "    {",
            "        return new Widget(id);",
            "    }",
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ExtractSuperclass_NewFile_ByteExact(string eol)
    {
        SetSources(("Test.cs", Build(eol,
            "using System;",
            "",
            "namespace Zoo;",
            "",
            "public class Dog",
            "{",
            "    // the name",
            "    public string Name { get; set; }",
            "    public   int   Age { get;  set; }~",
            UntouchedLine,
            "}")));

        var result = await _engine.ExtractSuperclassAsync(["Test.cs"], ["Dog"], "AnimalBase");

        Assert.That(result, Has.Count.EqualTo(1));
        AssertPinned(result.Values.Single(), Build(eol,
            "using System;",
            "",
            "namespace Zoo;",
            "",
            "public abstract class AnimalBase",
            "{",
            "    // the name",
            "    public string Name { get; set; }",
            "    public   int   Age { get;  set; }~",
            "}",
            ""));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task InlineClass_SameFile_ByteExact(string eol)
    {
        SetSources(("Test.cs", Build(eol,
            "public class Owner",
            "{",
            "    public int Existing;",
            "}",
            "",
            "public class Helper",
            "{",
            "    public int Value = 42;",
            "",
            "    public void  Go( ) { }",
            "}",
            "",
            "public class Other",
            "{",
            UntouchedLine,
            "}")));

        var result = await _engine.InlineClassAsync("Test.cs", "Test.cs", "Helper");

        Assert.That(result.Keys.Select(k => k.ToString()), Is.EquivalentTo(new[] { "Test.cs" }));
        AssertPinned(result["Test.cs"], Build(eol,
            "public class Owner",
            "{",
            "    public int Existing;",
            "",
            "    public int Value = 42;",
            "",
            "    public void Go() { }",
            "}",
            "",
            "public class Other",
            "{",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task InlineClass_CrossFile_WithTypeReferenceUpdate_ByteExact(string eol)
    {
        SetSources(
            ("Source.cs", Build(eol,
                "namespace App;",
                "",
                "public class Helper",
                "{",
                "    public int Value;",
                "}",
                "",
                "public class Keep",
                "{",
                UntouchedLine,
                "}")),
            ("Target.cs", Build(eol,
                "namespace App;",
                "",
                "public class Owner",
                "{",
                UntouchedLine,
                "}")),
            ("Consumer.cs", Build(eol,
                "namespace App;",
                "",
                "public class Consumer",
                "{",
                "    public Helper  Instance;",
                UntouchedLine,
                "}")));

        var result = await _engine.InlineClassAsync("Source.cs", "Target.cs", "Helper");

        Assert.That(result.Keys.Select(k => k.ToString()), Is.EquivalentTo(new[] { "Source.cs", "Target.cs", "Consumer.cs" }));
        Assert.Multiple(() =>
        {
            AssertPinned(result["Target.cs"], Build(eol,
                "namespace App;",
                "",
                "public class Owner",
                "{",
                UntouchedLine,
                "",
                "    public int Value;",
                "}"));
            AssertPinned(result["Source.cs"], Build(eol,
                "namespace App;",
                "",
                "public class Keep",
                "{",
                UntouchedLine,
                "}"));
            AssertPinned(result["Consumer.cs"], Build(eol,
                "namespace App;",
                "",
                "public class Consumer",
                "{",
                "    public Owner  Instance;",
                UntouchedLine,
                "}"));
        });
    }

    // Lines: 5 = typed local with a string literal, 6 = unused local, 7 = braceless if, 8 = if with a one-statement
    // block, 2 = no candidate (the micro-refactoring finds nothing and returns the root unchanged).
    private static string[] MicroInput() =>
    [
        "public class C",
        "{",
        "    public void M(int x)",
        "    {",
        "        string name = \"hello\";",
        "        int unused = 0;",
        "        if (x > 0) DoWork();",
        "        if (x > 1)",
        "        {",
        "            DoWork();",
        "        }",
        "",
        "        DoWork();",
        "    }",
        "",
        UntouchedLine,
        "    void DoWork() { }",
        "}",
    ];

    private static string MicroExpected(string refactoringId, string eol)
    {
        var lines = MicroInput().ToList();
        switch (refactoringId)
        {
            case "type-to-var":
                lines[4] = "        var name = \"hello\";";
                break;
            case "remove-unused-local":
                lines.RemoveAt(5);
                break;
            case "add-braces":
                lines.RemoveAt(6);
                lines.InsertRange(6, ["        if (x > 0)", "        {", "            DoWork();", "        }"]);
                break;
            case "remove-braces":
                lines.RemoveRange(7, 4);
                lines.InsertRange(7, ["        if (x > 1)", "            DoWork();"]);
                break;
            case "extract-constant":
                lines[4] = "        string name = ExtractedConstant;";
                lines.InsertRange(2, ["    private const string ExtractedConstant = \"hello\";", ""]);
                break;
        }

        return Build(eol, [.. lines]);
    }

    [TestCase("type-to-var", 5, "LF")]
    [TestCase("type-to-var", 5, "CRLF")]
    [TestCase("type-to-var", 5, "Mixed")]
    [TestCase("remove-unused-local", 6, "LF")]
    [TestCase("remove-unused-local", 6, "CRLF")]
    [TestCase("remove-unused-local", 6, "Mixed")]
    [TestCase("add-braces", 7, "LF")]
    [TestCase("add-braces", 7, "CRLF")]
    [TestCase("add-braces", 7, "Mixed")]
    [TestCase("remove-braces", 8, "LF")]
    [TestCase("remove-braces", 8, "CRLF")]
    [TestCase("remove-braces", 8, "Mixed")]
    [TestCase("extract-constant", 5, "LF")]
    [TestCase("extract-constant", 5, "CRLF")]
    [TestCase("extract-constant", 5, "Mixed")]
    [TestCase("type-to-var", 2, "LF")]
    [TestCase("type-to-var", 2, "CRLF")]
    [TestCase("type-to-var", 2, "Mixed")]
    public async Task RunMicroRefactoring_ByteExact(string refactoringId, int line, string eol)
    {
        SetSources(("Test.cs", Build(eol, MicroInput())));

        var result = await _engine.RunMicroRefactoringAsync("Test.cs", refactoringId, line);

        // Line 2 is a genuine no-op for type-to-var, so the outcome must be AlreadyInTargetState, not Modified.
        if (line == 2)
        {
            Assert.That(result.Outcome, Is.EqualTo(EditOutcome.AlreadyInTargetState));
            return;
        }

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, MicroExpected(refactoringId, eol));
    }

    /// <summary>
    /// Wraps a type whose member lines are written at file-scoped indentation in namespace Demo: file-scoped as given, or a
    /// block namespace with every non-empty type line indented one level deeper.
    /// </summary>
    private static string[] InNamespace(bool fileScoped, string[] header, string typeDeclaration, string[] body) =>
        fileScoped
            ? [.. header, "namespace Demo;", "", typeDeclaration, "{", .. body, "}"]
            : [.. header, "namespace Demo", "{", "    " + typeDeclaration, "    {", .. body.Select(l => l.Length == 0 ? l : "    " + l), "    }", "}"];

    [TestCase("LF", false)]
    [TestCase("CRLF", false)]
    [TestCase("Mixed", false)]
    [TestCase("LF", true)]
    [TestCase("CRLF", true)]
    [TestCase("Mixed", true)]
    public async Task ExtractMembersToPartial_NewFile_ByteExact(string eol, bool fileScoped)
    {
        string[] moved =
        [
            "    // the name~",
            "    public string Name { get; set; }",
            "",
            "    /// <summary>Total.</summary>",
            "    public   int Total(int a)",
            "    {",
            "        return  a; // keep",
            "    }",
        ];
        string[] header = ["using System; // for Console", "using System.Linq;", ""];
        SetSources(("Test.cs", Build(eol, InNamespace(fileScoped, header, "public partial class Customer", [.. moved, "", UntouchedLine]))));

        var result = await _engine.ExtractMembersToPartialAsync("Test.cs", "Customer", ["Name", "Total"]);

        Assert.That(result.Keys.Select(k => k.ToString()), Is.EquivalentTo(new[] { "Customer.Partial.cs" }));
        // The new file mirrors the source's namespace kind; usings and moved members (comments, spacing, line endings)
        // are byte-exact, the synthesized structure takes the dominant ending and the file ends with a line break.
        AssertPinned(result.Values.Single(), Build(eol, [.. InNamespace(fileScoped, header, "public partial class Customer", moved), ""]));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task InlineMethod_WithCallers_ByteExact(string eol)
    {
        SetSources(
            ("Calc.cs", Build(eol,
                "using System;",
                "",
                "namespace Demo",
                "{",
                "    public class Calc",
                "    {",
                "        // the answer~",
                "        public static int   Answer( ) => 40 + 2;",
                "",
                "        public int Use()",
                "        {",
                "            return  Answer() + 1; // keep",
                "        }",
                "",
                "    " + UntouchedLine,
                "    }",
                "}")),
            ("Other.cs", Build(eol,
                "namespace Demo",
                "{",
                "    public class Other",
                "    {",
                "        public int Get() =>  Calc.Answer( ) * 2;~",
                "    " + UntouchedLine,
                "    }",
                "}")));

        var result = await _engine.InlineMethodAsync("Calc.cs", "Answer");

        Assert.That(result.Keys.Select(k => k.ToString()), Is.EquivalentTo(new[] { "Calc.cs", "Other.cs" }));
        Assert.Multiple(() =>
        {
            // Only the inlined expression is formatted (including the space in front of it); the removed method takes
            // its comment along and leaves no blank line after the opening brace; everything else is byte-exact.
            AssertPinned(result["Calc.cs"], Build(eol,
                "using System;",
                "",
                "namespace Demo",
                "{",
                "    public class Calc",
                "    {",
                "        public int Use()",
                "        {",
                "            return 40 + 2 + 1; // keep",
                "        }",
                "",
                "    " + UntouchedLine,
                "    }",
                "}"));
            AssertPinned(result["Other.cs"], Build(eol,
                "namespace Demo",
                "{",
                "    public class Other",
                "    {",
                "        public int Get() => (40 + 2) * 2;",
                "    " + UntouchedLine,
                "    }",
                "}"));
        });
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task InlineMethod_NoCallers_ByteExact(string eol)
    {
        SetSources(("Calc.cs", Build(eol,
            "namespace Demo;",
            "",
            "public class Calc",
            "{",
            "    // unused~",
            "    private int   Seed( )",
            "    {",
            "        return 7;",
            "    }",
            "",
            UntouchedLine,
            "}")));

        var result = await _engine.InlineMethodAsync("Calc.cs", "Seed");

        Assert.That(result.Keys.Select(k => k.ToString()), Is.EquivalentTo(new[] { "Calc.cs" }));
        AssertPinned(result["Calc.cs"], Build(eol,
            "namespace Demo;",
            "",
            "public class Calc",
            "{",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF", false)]
    [TestCase("CRLF", false)]
    [TestCase("Mixed", false)]
    [TestCase("LF", true)]
    [TestCase("CRLF", true)]
    [TestCase("Mixed", true)]
    public async Task ConvertTupleToClass_ByteExact(string eol, bool fileScoped)
    {
        string[] header = ["using System; // for Console", ""];
        SetSources(("Repo.cs", Build(eol, InNamespace(fileScoped, header, "public class Repo",
        [
            "    // returns a pair~",
            "    public (int /* id */ id, string  name) GetPair( ) => (1, \"a\");",
            "",
            UntouchedLine,
        ]))));

        var result = await _engine.ConvertTupleToClassAsync("Repo.cs", "GetPair", "Pair");

        Assert.That(result.Keys.Select(k => k.ToString()), Is.EquivalentTo(new[] { "Repo.cs", "Pair.cs" }));
        Assert.Multiple(() =>
        {
            // Only the replaced return type is formatted; the rest of the method and file is byte-exact.
            AssertPinned(result["Repo.cs"], Build(eol, InNamespace(fileScoped, header, "public class Repo",
            [
                "    // returns a pair~",
                "    public Pair GetPair( ) => (1, \"a\");",
                "",
                UntouchedLine,
            ])));
            // New file: the using (with its comment) and the element types (with theirs) are embedded byte-exact; the
            // synthesized structure takes the dominant ending and the file ends with a line break.
            AssertPinned(result["Pair.cs"], Build(eol, [.. InNamespace(fileScoped, header, "public class Pair",
            [
                "    public int /* id */ Id { get; set; }",
                "    public string Name { get; set; }",
            ]), ""]));
        });
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertAnonymousToNamed_NewFile_ByteExact(string eol)
    {
        SetSources(("Maker.cs", Build(eol,
            "namespace Demo;",
            "",
            "public class Maker",
            "{",
            "    public object Make() => new { Id = 1, name = \"a\" };~",
            UntouchedLine,
            "}")));

        var result = await _engine.ConvertAnonymousToNamedAsync("Maker.cs", "Item");

        Assert.That(result.Keys.Select(k => k.ToString()), Is.EquivalentTo(new[] { "Maker.cs", "Item.cs" }));
        Assert.Multiple(() =>
        {
            // Only the replaced expression is changed; the rest of the file keeps its text and line endings. The changed
            // line takes the document's dominant ending (no "~" here, so it is LF in the Mixed variant), like every other
            // changed line pinned in this fixture.
            AssertPinned(result["Maker.cs"], Build(eol,
                "namespace Demo;",
                "",
                "public class Maker",
                "{",
                "    public object Make() => new Item { Id = 1, Name = \"a\" };",
                UntouchedLine,
                "}"));
            // Every node is new: the class (in the source's namespace, properties typed from the initializers) takes the
            // dominant line ending throughout and the file ends with a line break.
            AssertPinned(result["Item.cs"], Build(eol == "CRLF" ? "CRLF" : "LF",
                "namespace Demo;",
                "",
                "public class Item",
                "{",
                "    public int Id { get; set; }",
                "    public string Name { get; set; }",
                "}",
                ""));
        });
    }
}
