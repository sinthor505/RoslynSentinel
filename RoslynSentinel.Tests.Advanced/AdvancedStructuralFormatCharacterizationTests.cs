using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C5b-2a characterization tests (docs/current/proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for <c>StructuralRefactoringEngine.IntroduceParameterObjectAsync</c>: the main path (rewritten method
/// plus appended record) and the interface-warning path (rewritten method only, returned with a warning message), the
/// two annotated-container sites routed onto <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c>. The source
/// carries irregular spacing, comments and blank lines in code the engine does not touch, so a regression that formats
/// the whole document shows up as a diff. Written against the unmodified call (plain
/// <c>Formatter.FormatAsync(doc, annotation)</c>). The CRLF tests are byte-identical to the pre-routing behaviour; the
/// LF and mixed-EOL tests encode the corrected output (the old code leaked formatter-inserted CRLF).
/// </summary>
[TestFixture]
[Category("StructuralRefactoringEngine")] // sentinel:auto-category
public class AdvancedStructuralFormatCharacterizationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private StructuralRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Advanced.TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // The text the engine returned for one file of a multi-file change (the target file included).
    private static string ChangedText(DocumentEditResult result, string fileName)
    {
        var matches = result.Changes.Where(kv => kv.Key.Absolute.EndsWith(fileName, StringComparison.Ordinal)).ToList();
        Assert.That(matches, Has.Count.EqualTo(1), $"Precondition: exactly one changed file ends with {fileName}; changed: {string.Join(", ", result.Changes.Keys.Select(k => k.Absolute))}");
        return matches[0].Value;
    }

    private void SetSources(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>Joins lines with LF (a trailing newline only if the last line is empty), then converts to CRLF when requested.</summary>
    private static string Src(bool crlf, params string[] lines)
    {
        var text = string.Join("\n", lines);
        return crlf ? text.Replace("\n", "\r\n") : text;
    }

    // An LF document with exactly one CRLF terminator, the last character of the anchor. Two placements: the opening
    // brace of the method the engine rewrites (inside the annotated node), and the `_count` field line right before
    // the rewritten method (the line break there is formatter-owned: the formatter may re-space the whitespace in
    // front of an annotated node). The expected bytes are the LF expectation with that same one terminator kept.
    private const string MethodBraceAnchor = "\n    {\n";
    private const string PreMethodLineAnchor = "    private   int _count;\n";

    private static string WithStrayCrlf(string lfText, string anchor)
    {
        Assert.That(lfText, Does.Contain(anchor), "Precondition: the anchor line is present.");
        var mixed = lfText.Replace(anchor, anchor.Substring(0, anchor.Length - 1) + "\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        return mixed;
    }

    // ---- main path: no interface involved; the method is rewritten and the record appended ----

    private static string[] MainInput() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void  Run( int  a, string b )",
        "    {",
        "        // inner comment",
        "        _count += a;",
        "",
        "        Console.WriteLine( b );",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
    ];

    private static string[] MainExpected() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void Run(RunParameters request)",
        "    {",
        "        // TODO: Update call sites to use RunParameters",
        "        // inner comment",
        "        _count += request.A;",
        "",
        "        Console.WriteLine(request.B);",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
        "public record RunParameters(int A, string B);",
        "",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task IntroduceParameterObject_MainPath_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, MainInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.Message, Is.EqualTo("Introduced parameter object 'RunParameters'."));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, MainExpected())));
    }

    [Test]
    public async Task IntroduceParameterObject_MainPath_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, MainInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task IntroduceParameterObject_MainPath_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, MainInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.UpdatedText.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [TestCase(MethodBraceAnchor)]
    [TestCase(PreMethodLineAnchor)]
    public async Task IntroduceParameterObject_MainPath_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        SetSource(WithStrayCrlf(Src(false, MainInput()), anchor));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.UpdatedText, Is.EqualTo(WithStrayCrlf(Src(false, MainExpected()), anchor)));
    }

    // ---- empty body: the engine puts the TODO comment on the method's closing brace trivia ----

    private static string[] EmptyBodyInput() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    private   int _count;",
        "",
        "    public void  Run( int  a, string b )",
        "    {",
        "    }",
        "}",
    ];

    private static string[] EmptyBodyExpected() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    private   int _count;",
        "",
        "    public void Run(RunParameters request)",
        "    {",
        "        // TODO: Update call sites to use RunParameters",
        "    }",
        "}",
        "",
        "public record RunParameters(int A, string B);",
        "",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task IntroduceParameterObject_EmptyBody_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, EmptyBodyInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, EmptyBodyExpected())));
    }

    // ---- warning path: the method implements an interface, so only the method is rewritten ----
    // The interface is declared after the class so that the by-name lookup finds the class method first.

    private static string[] WarnInput() =>
    [
        "using System;",
        "",
        "public class Worker : IWorker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void  Run( int  a, string b )",
        "    {",
        "        // inner comment",
        "        _count += a;",
        "",
        "        Console.WriteLine( b );",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
        "public interface IWorker",
        "{",
        "    void Run(int a, string b);",
        "}",
    ];

    private static string[] WarnExpected() =>
    [
        "using System;",
        "",
        "public class Worker : IWorker",
        "{",
        "    // keep   this   comment",
        "    private   int _count;",
        "",
        "",
        "    public void Run(RunParameters request)",
        "    {",
        "        // TODO: Update call sites to use RunParameters",
        "        // inner comment",
        "        _count += request.A;",
        "",
        "        Console.WriteLine(request.B);",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
        "public interface IWorker",
        "{",
        "    void Run(int a, string b);",
        "}",
        "",
        "public record RunParameters(int A, string B);",
        "",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task IntroduceParameterObject_WarningPath_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, WarnInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.Message, Does.Contain("WARNING: This method implements an interface."));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, WarnExpected())));
    }

    [Test]
    public async Task IntroduceParameterObject_WarningPath_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, WarnInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Message, Does.Contain("WARNING: This method implements an interface."));
        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task IntroduceParameterObject_WarningPath_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, WarnInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Message, Does.Contain("WARNING: This method implements an interface."));
        Assert.That(result.UpdatedText.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [TestCase(MethodBraceAnchor)]
    [TestCase(PreMethodLineAnchor)]
    public async Task IntroduceParameterObject_WarningPath_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        SetSource(WithStrayCrlf(Src(false, WarnInput()), anchor));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        Assert.That(result.Message, Does.Contain("WARNING: This method implements an interface."));
        Assert.That(result.UpdatedText, Is.EqualTo(WithStrayCrlf(Src(false, WarnExpected()), anchor)));
    }

    // ---- overloads: two methods named Run in one class (D1: never take the first match silently) ----

    private static string[] OverloadInput() =>
    [
        "using System;",
        "",
        "public class Worker",
        "{",
        "    private int _count;",
        "",
        "    public void Run(int a, int b)",
        "    {",
        "        Console.WriteLine(a + b);",
        "    }",
        "",
        "    public void Run(string s, string t)",
        "    {",
        "        Console.WriteLine(s + t);",
        "    }",
        "}",
    ];

    [Test]
    public async Task IntroduceParameterObject_TwoOverloadsNoSnippet_ThrowsAmbiguousMatch()
    {
        SetSource(Src(false, OverloadInput()));

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _engine.IntroduceParameterObjectAsync("Test.cs", "Run"));

        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(ex.Message, Does.Contain("Test.cs:7"));
        Assert.That(ex.Message, Does.Contain("Test.cs:12"));
        Assert.That(ex.Message, Does.Contain("IntroduceParameterObject"));
        Assert.That(ex.Message, Does.Contain("contextSnippet"));
    }

    [TestCase("    public void Run(string s, string t)")]
    [TestCase("public void Run(string s, string t)")]
    public async Task IntroduceParameterObject_TwoOverloadsWithContextSnippet_ChangesOnlyTheMatchedOverload(string contextSnippet)
    {
        SetSource(Src(false, OverloadInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run", contextSnippet: contextSnippet);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("    public void Run(int a, int b)\n    {\n        Console.WriteLine(a + b);\n    }"));
        Assert.That(result.UpdatedText, Does.Contain("    public void Run(RunParameters request)"));
        Assert.That(result.UpdatedText, Does.Contain("request.S + request.T"));
        Assert.That(result.UpdatedText, Does.Not.Contain("public void Run(string s, string t)"));
    }

    [Test]
    public async Task IntroduceParameterObject_ContextSnippetOutsideEveryOverload_ThrowsNotFound()
    {
        SetSource(Src(false, OverloadInput()));

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(
            async () => await _engine.IntroduceParameterObjectAsync("Test.cs", "Run",
                contextSnippet: "    private int _count;"));

        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(ex.Message, Does.Contain("not inside any method named 'Run'"));
    }

    // One file, interface declared BEFORE its implementing class: a bare name edits the class (never the first declaration).
    private static string[] InterfaceFirstInput() =>
    [
        "public interface IWorker",
        "{",
        "    void Run(int a, string b);",
        "}",
        "",
        "public class Worker : IWorker",
        "{",
        "    public void Run(int x, string y)",
        "    {",
        "        System.Console.WriteLine(y);",
        "    }",
        "}",
    ];

    [Test]
    public async Task IntroduceParameterObject_SnippetOnInterfaceMethodInSharedFile_UpdatesInterfaceAndImplementer_ByteExact()
    {
        SetSource(Src(false, InterfaceFirstInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run", contextSnippet: "void Run(int a, string b);");

        Assert.That(result.Message, Is.EqualTo("Introduced parameter object 'RunParameters'. Updated 1 implementation(s) in 1 file(s)."));
        Assert.That(result.Changes, Is.Empty, "Only the target file changed, so the result carries just UpdatedText.");
        Assert.That(result.UpdatedText, Is.EqualTo(Src(false,
            "public interface IWorker",
            "{",
            "    void Run(RunParameters request);",
            "}",
            "",
            "public class Worker : IWorker",
            "{",
            "    public void Run(RunParameters request)",
            "    {",
            "        // TODO: Update call sites to use RunParameters",
            "        System.Console.WriteLine(request.B);",
            "    }",
            "}",
            "",
            "public record RunParameters(int A, string B);",
            "")));
    }

    [Test]
    public async Task IntroduceParameterObject_InterfaceDeclaredBeforeClass_EditsTheClassAndKeepsTheTodoAndMessage_ByteExact()
    {
        SetSource(Src(false, InterfaceFirstInput()));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run");

        // The exact success wording (it once read "// Local variable introduced.") plus the interface warning.
        Assert.That(result.Message, Is.EqualTo("Introduced parameter object 'RunParameters'. WARNING: This method implements an interface. Update the interface signature in the corresponding interface file."));
        Assert.That(result.Changes, Is.Empty);
        // The class (not the interface declared first) is edited, and its own parameter names (x, y) shape the record.
        Assert.That(result.UpdatedText, Is.EqualTo(Src(false,
            "public interface IWorker",
            "{",
            "    void Run(int a, string b);",
            "}",
            "",
            "public class Worker : IWorker",
            "{",
            "    public void Run(RunParameters request)",
            "    {",
            "        // TODO: Update call sites to use RunParameters",
            "        System.Console.WriteLine(request.Y);",
            "    }",
            "}",
            "",
            "public record RunParameters(int X, string Y);",
            "")));
    }

    [Test]
    public async Task IntroduceParameterObject_InterfaceWithoutImplementers_WarnsAndChangesOnlyTheInterfaceFile()
    {
        SetSource(Src(false,
            "public interface IProcessor",
            "{",
            "    void Process(string name, int age);",
            "}",
            ""));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Process");

        Assert.That(result.Message, Is.EqualTo("Introduced parameter object 'ProcessParameters'. WARNING: The interface method was updated, but no implementations of it were found in the solution. Update any implementations outside the solution manually."));
        Assert.That(result.Changes, Is.Empty);
        Assert.That(result.UpdatedText, Is.EqualTo(Src(false,
            "public interface IProcessor",
            "{",
            "    void Process(ProcessParameters request);",
            "}",
            "",
            "public record ProcessParameters(string Name, int Age);",
            "")));
    }

    [Test]
    public async Task IntroduceParameterObject_PartialClassImplementer_OnlyThePartHoldingTheMethodChanges()
    {
        SetSources(
            ("A.cs", Src(false,
                "namespace Contracts;",
                "",
                "public interface IProcessor",
                "{",
                "    void Process(string name, int age);",
                "}",
                "")),
            // The part that names the interface does not hold the method.
            ("B.cs", Src(false,
                "namespace Contracts.Impl;",
                "",
                "public partial class Worker : IProcessor",
                "{",
                "    public int Other() => 1;",
                "}",
                "")),
            ("C.cs", Src(false,
                "namespace Contracts.Impl;",
                "",
                "public partial class Worker",
                "{",
                "    public void Process(string name, int age)",
                "    {",
                "        Other();",
                "    }",
                "}",
                "")));

        var result = await _engine.IntroduceParameterObjectAsync("A.cs", "Process");

        Assert.That(result.Changes, Has.Count.EqualTo(2));
        // Contracts.Impl is nested inside the record's namespace (Contracts): no using needed.
        Assert.That(ChangedText(result, "C.cs"), Is.EqualTo(Src(false,
            "namespace Contracts.Impl;",
            "",
            "public partial class Worker",
            "{",
            "    public void Process(ProcessParameters request)",
            "    {",
            "        // TODO: Update call sites to use ProcessParameters",
            "        Other();",
            "    }",
            "}",
            "")));
    }

    [Test]
    public async Task IntroduceParameterObject_TwoImplementersInOneFile_ExpressionBodyAndExistingUsing_ByteExact()
    {
        SetSources(
            ("A.cs", Src(false,
                "namespace Contracts;",
                "",
                "public interface IProcessor",
                "{",
                "    int Process(string name, int age);",
                "}",
                "")),
            ("B.cs", Src(false,
                "using Contracts;",
                "",
                "namespace Impl;",
                "",
                "public class First : IProcessor",
                "{",
                "    public int Process(string name, int age) => name.Length + age;",
                "}",
                "",
                "public class Second : IProcessor",
                "{",
                "    public int Process(string text, int count)",
                "    {",
                "        return text.Length * count;",
                "    }",
                "}",
                "")));

        var result = await _engine.IntroduceParameterObjectAsync("A.cs", "Process");

        Assert.That(result.Message, Is.EqualTo("Introduced parameter object 'ProcessParameters'. Updated 2 implementation(s) in 1 file(s)."));
        // Both implementers are edited in one pass over B.cs; the using it already has is not duplicated. The expression-bodied
        // method has no statement to carry the TODO, so it is its own line above the declaration.
        Assert.That(ChangedText(result, "B.cs"), Is.EqualTo(Src(false,
            "using Contracts;",
            "",
            "namespace Impl;",
            "",
            "public class First : IProcessor",
            "{",
            "    // TODO: Update call sites to use ProcessParameters",
            "    public int Process(ProcessParameters request) => request.Name.Length + request.Age;",
            "}",
            "",
            "public class Second : IProcessor",
            "{",
            "    public int Process(ProcessParameters request)",
            "    {",
            "        // TODO: Update call sites to use ProcessParameters",
            "        return request.Name.Length * request.Age;",
            "    }",
            "}",
            "")));
    }

    // ---- interface-targeted: the interface and every implementer take the record (call sites stay manual) ----

    [Test]
    public async Task IntroduceParameterObject_InterfaceInOtherFile_UpdatesImplementerAndExplicitImplementer_ByteExact()
    {
        SetSources(
            ("A.cs", Src(false,
                "namespace Contracts;",
                "",
                "public interface IProcessor",
                "{",
                "    void Process(string name, int age);",
                "}",
                "")),
            ("B.cs", Src(false,
                "using System;",
                "",
                "namespace Impl;",
                "",
                "public class Processor : Contracts.IProcessor",
                "{",
                "    public void Process(string fullName, int years)",
                "    {",
                "        Console.WriteLine(fullName + years);",
                "    }",
                "}",
                "")),
            ("C.cs", Src(false,
                "namespace Impl;",
                "",
                "public class ExplicitProcessor : Contracts.IProcessor",
                "{",
                "    void Contracts.IProcessor.Process(string name, int age)",
                "    {",
                "        System.Console.WriteLine(name);",
                "    }",
                "}",
                "")));

        var result = await _engine.IntroduceParameterObjectAsync("A.cs", "Process");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.Message, Is.EqualTo("Introduced parameter object 'ProcessParameters'. Updated 2 implementation(s) in 2 file(s)."));
        Assert.That(result.Changes, Has.Count.EqualTo(3));
        var expectedA = Src(false,
            "namespace Contracts;",
            "",
            "public interface IProcessor",
            "{",
            "    void Process(ProcessParameters request);",
            "}",
            "",
            "public record ProcessParameters(string Name, int Age);",
            "");
        Assert.That(result.UpdatedText, Is.EqualTo(expectedA));
        Assert.That(ChangedText(result, "A.cs"), Is.EqualTo(expectedA));
        // Implementer parameters are mapped by position: fullName/years land on the record's Name/Age. B.cs is in another namespace: using added.
        Assert.That(ChangedText(result, "B.cs"), Is.EqualTo(Src(false,
            "using System;",
            "using Contracts;",
            "",
            "namespace Impl;",
            "",
            "public class Processor : Contracts.IProcessor",
            "{",
            "    public void Process(ProcessParameters request)",
            "    {",
            "        // TODO: Update call sites to use ProcessParameters",
            "        Console.WriteLine(request.Name + request.Age);",
            "    }",
            "}",
            "")));
        // C.cs had no usings at all: the new one is followed by a blank line.
        Assert.That(ChangedText(result, "C.cs"), Is.EqualTo(Src(false,
            "using Contracts;",
            "",
            "namespace Impl;",
            "",
            "public class ExplicitProcessor : Contracts.IProcessor",
            "{",
            "    void Contracts.IProcessor.Process(ProcessParameters request)",
            "    {",
            "        // TODO: Update call sites to use ProcessParameters",
            "        System.Console.WriteLine(request.Name);",
            "    }",
            "}",
            "")));
    }

    [Test]
    public void IntroduceParameterObject_NullSyntaxRoot_ErrorNamesThePath()
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(
            () => StructuralRefactoringEngine.RequireSyntaxRoot(null, "Some/Dir/Missing.cs"));

        Assert.That(ex!.Message, Does.Contain("Some/Dir/Missing.cs"));
    }

    [Test]
    public async Task IntroduceParameterObject_MemberNameLabelAndInitializerTarget_LeftUnchanged_ValueUsesRewritten()
    {
        SetSource(Src(false,
            "using System;",
            "",
            "public class Other { public string name; }",
            "public class Foo { public string name { get; set; } }",
            "",
            "public class Worker",
            "{",
            "    public void Run(string name, int count)",
            "    {",
            "        var other = new Other();",
            "        Console.WriteLine(other.name);",
            "        Log(name: name);",
            "        var foo = new Foo { name = name };",
            "        Console.WriteLine(name);",
            "    }",
            "",
            "    private void Log(string name) { }",
            "}"));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run", parameterNames: new[] { "name" });

        Assert.That(result.UpdatedText, Does.Contain("public void Run(RunParameters request, int count)"));
        Assert.That(result.UpdatedText, Does.Contain("Console.WriteLine(other.name);"), "A member access on another object is not a parameter use.");
        Assert.That(result.UpdatedText, Does.Contain("Log(name: request.Name);"), "The named-argument label stays; only its value is a use.");
        Assert.That(result.UpdatedText, Does.Contain("new Foo { name = request.Name }"), "The object-initializer member target stays; the value is a use.");
        Assert.That(result.UpdatedText, Does.Contain("Console.WriteLine(request.Name);"));
        Assert.That(result.UpdatedText, Does.Not.Contain("Log(name: name)"));
    }

    [Test]
    public async Task IntroduceParameterObject_InterfaceTargetRenamedImplementer_BindsByPosition_OtherMemberLeftUnchanged()
    {
        SetSource(Src(false,
            "using System;",
            "",
            "public interface IWorker",
            "{",
            "    void Run(string name, int count);",
            "}",
            "",
            "public class Other { public string n; }",
            "",
            "public class Worker : IWorker",
            "{",
            "    public void Run(string n, int c)",
            "    {",
            "        var other = new Other();",
            "        Console.WriteLine(other.n);",
            "        Console.WriteLine(n);",
            "    }",
            "}"));

        var result = await _engine.IntroduceParameterObjectAsync("Test.cs", "Run", parameterNames: new[] { "name" }, contextSnippet: "void Run(string name, int count);");

        Assert.That(result.UpdatedText, Does.Contain("void Run(RunParameters request, int count);"));
        Assert.That(result.UpdatedText, Does.Contain("public void Run(RunParameters request, int c)"), "The implementer's renamed parameter is rewritten by position.");
        Assert.That(result.UpdatedText, Does.Contain("Console.WriteLine(other.n);"), "other.n is a member access on another object, not the parameter.");
        Assert.That(result.UpdatedText, Does.Contain("Console.WriteLine(request.Name);"));
        Assert.That(result.UpdatedText, Does.Not.Contain("Console.WriteLine(n);"));
    }
}
