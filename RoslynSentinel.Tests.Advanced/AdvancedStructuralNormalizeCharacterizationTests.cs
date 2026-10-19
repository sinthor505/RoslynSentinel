using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Slice C7a characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for the <c>StructuralRefactoringEngine</c> sites that ended in
/// <c>RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace</c>: ConvertAbstractClassToInterface,
/// ReplaceConstructorWithFactory, ExtractSuperclass, InlineClass (same file and cross file), UpdateTypeReferences
/// (through a cross-file InlineClass) and RunMicroRefactoring. Each input has untouched, irregularly spaced code
/// (<c>UntouchedLine</c>). A line with a trailing "~" ends in a stray CRLF in the "Mixed" (otherwise LF) variant.
/// The pins encode the migrated contract (annotated-span formatting): untouched lines keep their text and terminator,
/// blank lines and comments survive, new nodes (the interface, the factory, the inlined members, the constant, the
/// braces) are formatted whole, and changed lines take the document's dominant line ending. The pre-migration
/// whole-subtree pass emitted LF for every input, re-spaced and split every untouched member, dropped blank lines between
/// members, lost the constructor's leading comment and the interface members' comments, and split
/// <c>if (x > 0) DoWork();</c> lines that no refactoring touched.
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

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, MicroExpected(line == 2 ? "none" : refactoringId, eol));
    }
}
