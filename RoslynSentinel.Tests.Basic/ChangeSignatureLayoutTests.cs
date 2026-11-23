using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// ChangeSignature keeps the multi-line layout of argument lists and parameter lists: a kept item keeps its own line and
/// indentation, a new item follows its neighbours, a removed item takes its line with it. Each case asserts the exact rewritten
/// text and that the rewritten file compiles. Sources are built with explicit CRLF joins so they match the repo's line endings.
/// Method bodies are "=> 0" so that removing a parameter never leaves a body that names it.
/// </summary>
[TestFixture]
public class ChangeSignatureLayoutTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private BasicRefactoringEngine _refactoringEngine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        _refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private Task<ChangeSignatureResult> Change(string filePath, string methodName, params SignatureParameterSpec[] parameters) =>
        _refactoringEngine.ChangeSignatureAsync(filePath, methodName, parameters);

    private Task<ChangeSignatureResult> ChangeAt(string filePath, string methodName, string contextSnippet, params SignatureParameterSpec[] parameters) =>
        _refactoringEngine.ChangeSignatureAsync(filePath, methodName, parameters, contextSnippet: contextSnippet);

    private Task<ChangeSignatureResult> KeepWrapper(string filePath, string methodName, params SignatureParameterSpec[] parameters) =>
        _refactoringEngine.ChangeSignatureAsync(filePath, methodName, parameters, oldSignature: ChangeSignatureOldSignature.keepAsDeprecatedWrapper);

    private void SetFile(string name, string content)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { (name, content) });
        _workspaceManager.SetTestSolution(solution);
    }

    // Joins lines with CRLF, the line ending the rewritten sources must keep.
    private static string Lines(params string[] lines) => string.Join("\r\n", lines);

    private static void AssertCompiles(string text)
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" }
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(frameworkDir, assembly)))
            .ToList();
        var compilation = CSharpCompilation.Create("ChangeSignatureLayoutProof", new[] { CSharpSyntaxTree.ParseText(text, path: "Layout.cs") }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.That(errors, Is.Empty, "The rewritten code must compile with zero errors: " + string.Join(Environment.NewLine, errors));
    }

    // Every line break in the rewritten text must be CRLF: no LF that is not preceded by CR.
    private static void AssertNoBareLineFeed(string text) =>
        Assert.That(text.Replace("\r\n", string.Empty).Contains('\n'), Is.False, "Every line break must stay CRLF; a bare LF was introduced");

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineCall_Reorder_KeepsOneArgumentPerLine()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(int a, int b, int c) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        3);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(2), new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        3,",
            "        1,",
            "        2);")), "The reordered arguments each keep their own line at the neighbours' indentation");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineCallWithClosingParenOnOwnLine_Reorder_KeepsParenPlacement()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(int a, int b, int c) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        3",
            "    );",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(2), new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        3,",
            "        1,",
            "        2",
            "    );")), "The closing parenthesis stays on its own line after the reorder");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineCallWithTrailingComment_Reorder_CommentFollowsItsArgument()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(int a, int b, int c) => 0;",
            "    public int Run() => Foo(",
            "        1, // one",
            "        2,",
            "        3);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(2), new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        3,",
            "        1, // one",
            "        2);")), "The // one comment moves with the argument it followed, to the argument's new line");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineCall_RemoveMiddleParameter_LeavesNoBlankLine()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(int a, int b, int c) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        3);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(0), new ExistingParameterSpec(2));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        1,",
            "        3);")), "The removed argument takes its line with it; no blank line is left behind");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineCallWithClosingParenOnOwnLine_RemoveMiddleParameter_KeepsParenPlacement()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(int a, int b, int c) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        3",
            "    );",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(0), new ExistingParameterSpec(2));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        1,",
            "        3",
            "    );")), "The closing parenthesis stays on its own line after the removal");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineCall_AddParameterAtEnd_NewArgumentFollowsNeighbours()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(int a, int b) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("z", "int", "9"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        9);")), "The new argument goes on its own line with the neighbours' indentation");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineCall_AddParameterInFront_NewArgumentFollowsNeighbours()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(int a, int b) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new NewParameterSpec("z", "int", "9"), new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        9,",
            "        1,",
            "        2);")), "The new argument goes in front on its own line with the neighbours' indentation");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineDeclaration_ReorderWithComments_CommentsStayWithParameters()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(",
            "        int a, // first",
            "        int b,",
            "        int c // last",
            "    ) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        3);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(2), new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Foo(",
            "        int c, // last",
            "        int a, // first",
            "        int b",
            "    ) => 0;")), "Each parameter keeps its own line; // first and // last stay with a and c");
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        3,",
            "        1,",
            "        2);")), "The multi-line call keeps one argument per line after the reorder");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineDeclaration_AddAtEnd_KeepsCommentsAndLayout()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(",
            "        int a, // first",
            "        int b,",
            "        int c // last",
            "    ) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        3);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new ExistingParameterSpec(2), new NewParameterSpec("d", "int", "4"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Foo(",
            "        int a, // first",
            "        int b,",
            "        int c, // last",
            "        int d = 4",
            "    ) => 0;")), "The new parameter goes on its own line; // last moves with c");
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        3,",
            "        4);")), "The new argument goes on its own line in the call");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_MultiLineDeclaration_RemoveMiddle_KeepsCommentsAndLayout()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(",
            "        int a, // first",
            "        int b,",
            "        int c // last",
            "    ) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        3);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(0), new ExistingParameterSpec(2));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Foo(",
            "        int a, // first",
            "        int c // last",
            "    ) => 0;")), "The removed parameter takes its line; no blank line; // last stays with c");
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Foo(",
            "        1,",
            "        3);")), "The removed argument takes its line in the multi-line call");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_SingleLineCall_ReorderAndAdd_StaysSingleLine()
    {
        const string source = "public class Svc\r\n{\r\n    public int Foo(int a, int b, int c) => 0;\r\n    public int Run() => Foo(1, 2, 3);\r\n}";
        SetFile("Svc.cs", source);
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(2), new ExistingParameterSpec(0), new NewParameterSpec("z", "int", "9"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain("    public int Run() => Foo(3, 1, 9);"), "A single-line call keeps its single-line style");
        Assert.That(after.Split("\r\n").Length, Is.EqualTo(source.Split("\r\n").Length), "No line break is introduced into a single-line call");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_CrlfSources_ReorderAddAndRemove_NoBareLineFeedIntroduced()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(",
            "        int a,",
            "        int b,",
            "        int c",
            "    ) => 0;",
            "    public int Run() => Foo(",
            "        1,",
            "        2,",
            "        3);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(2), new ExistingParameterSpec(0), new NewParameterSpec("z", "int", "9"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain("\r\n"), "The rewritten file keeps its CRLF line breaks");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_Constructors_ReorderKeepsLayoutOfNewTargetTypedAndThisCalls()
    {
        SetFile("C.cs", Lines(
            "public class C",
            "{",
            "    public C(int a, int b) { }",
            "    public C(int a) : this(",
            "        a,",
            "        2) { }",
            "    public C(string s) { }",
            "    public static void Run()",
            "    {",
            "        var x = new C(",
            "            1,",
            "            2);",
            "        C c = new(",
            "            1,",
            "            2);",
            "    }",
            "}"));
        var result = await ChangeAt("C.cs", "C", "public C(int a, int b) { }", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["C.cs"];
        Assert.That(after, Does.Contain("    public C(int b, int a) { }"), "The reordered constructor declaration");
        Assert.That(after, Does.Contain(Lines(
            "    public C(int a) : this(",
            "        2,",
            "        a) { }")), "The this(...) call keeps one argument per line after the reorder, and the body brace stays after the closing parenthesis");
        Assert.That(after, Does.Contain(Lines(
            "        var x = new C(",
            "            2,",
            "            1);")), "The new C(...) call keeps one argument per line after the reorder");
        Assert.That(after, Does.Contain(Lines(
            "        C c = new(",
            "            2,",
            "            1);")), "The target-typed new(...) call keeps one argument per line after the reorder");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_Constructors_MultiLineDeclarationWithThisCall_ReorderAndAdd_BodyBraceStaysOnClosingLine()
    {
        SetFile("C.cs", Lines(
            "public class C",
            "{",
            "    public C(int a) { }",
            "    public C(",
            "        int a,",
            "        int b) : this(a) { }",
            "}"));
        var reordered = await ChangeAt("C.cs", "C", "int b) : this(a) { }", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(reordered.Error, Is.Null, reordered.Error);
        Assert.That(reordered.Changes["C.cs"], Does.Contain(Lines(
            "    public C(",
            "        int b,",
            "        int a) : this(a) { }")), "The reordered parameters keep one per line and the initializer and body brace stay on the closing line");
        AssertCompiles(reordered.Changes["C.cs"]);

        var added = await ChangeAt("C.cs", "C", "int b) : this(a) { }", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("c", "int", "3"));

        Assert.That(added.Error, Is.Null, added.Error);
        Assert.That(added.Changes["C.cs"], Does.Contain(Lines(
            "    public C(",
            "        int a,",
            "        int b,",
            "        int c = 3) : this(a) { }")), "The added parameter goes on its own line with correct spacing and the initializer and body brace stay on the closing line");
        AssertNoBareLineFeed(added.Changes["C.cs"]);
        AssertCompiles(added.Changes["C.cs"]);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_Constructors_SingleArgumentMultiLineThisCall_AddParameter_BodyBraceStaysOnClosingLine()
    {
        SetFile("C.cs", Lines(
            "public class C",
            "{",
            "    public C(int a) { }",
            "    public C(string s) : this(",
            "        1) { }",
            "}"));
        var result = await ChangeAt("C.cs", "C", "public C(int a) { }", new ExistingParameterSpec(0), new NewParameterSpec("z", "int", "0"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["C.cs"];
        Assert.That(after, Does.Contain("    public C(int a, int z = 0) { }"), "The changed constructor declaration");
        Assert.That(after, Does.Contain(Lines(
            "    public C(string s) : this(",
            "        1, 0) { }")), "The one argument stays on its own line, the added argument follows it, and the body brace stays after the closing parenthesis");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_KeepAsDeprecatedWrapper_MultiLineDeclarationAndCall_KeepLayoutAndSingleLineWrapper()
    {
        SetFile("Saver.cs", Lines(
            "public class Saver",
            "{",
            "    public int Save(",
            "        string name,",
            "        int id) => 0;",
            "    public int Run() => Save(",
            "        \"x\",",
            "        1);",
            "}"));
        var result = await KeepWrapper("Saver.cs", "Save", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Saver.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public int Save(",
            "        int id,",
            "        string name) => 0;")), "The new declaration keeps its multi-line parameter list");
        Assert.That(after, Does.Contain(Lines(
            "    public int Run() => Save(",
            "        1,",
            "        \"x\");")), "The migrated call keeps one argument per line");

        var tree = CSharpSyntaxTree.ParseText(after, path: "Saver.cs");
        var wrapper = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString().Contains("Obsolete")));
        // The attribute list sits on its own line, as the engine always emits it; the declaration (return type through body) must be single-line.
        var wrapperText = tree.GetText().ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(wrapper.ReturnType.SpanStart, wrapper.Span.End));
        Assert.That(wrapperText.Contains('\n'), Is.False, "The generated wrapper declaration is single-line: " + wrapperText);
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_LambdaBodyCall_ReorderKeepsLayout()
    {
        SetFile("Svc.cs", Lines(
            "public class Svc",
            "{",
            "    public int Foo(int a, int b) => 0;",
            "    public System.Func<int> Make() => () => Foo(",
            "        3,",
            "        4);",
            "}"));
        var result = await Change("Svc.cs", "Foo", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Svc.cs"];
        Assert.That(after, Does.Contain(Lines(
            "    public System.Func<int> Make() => () => Foo(",
            "        4,",
            "        3);")), "The lambda-body call keeps one argument per line after the reorder");
        AssertNoBareLineFeed(after);
        AssertCompiles(after);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_CrlfConstructor_AddParameterWithDefault_OnlyDeclarationAndCallLinesChange()
    {
        var original = Lines(
            "using System;",
            "#nullable enable",
            "",
            "// Unrelated header comment.",
            "public class Widget",
            "{",
            "    private readonly string _name;",
            "",
            "    public Widget(string name) { _name = name; }",
            "",
            "    // Factories.",
            "    public static Widget A() => new Widget(\"a\");",
            "    public static Widget B() => new Widget(\"b\");",
            "    public static Widget C() => new Widget(",
            "        \"c\");",
            "",
            "    public string Describe() => \"Widget \" + _name;",
            "    public int Other(int x) => x + 1;",
            "}");
        // Zero-based indices of the lines the edit must change: the declaration and one line per call site.
        int[] expectedChanged = [8, 11, 12, 14];
        SetFile("Widget.cs", original);
        var result = await ChangeAt("Widget.cs", "Widget", "public Widget(string name)", new ExistingParameterSpec(0), new NewParameterSpec("hint", "string?", "null"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Widget.cs"];
        AssertNoBareLineFeed(after);
        AssertCompiles(after);

        var beforeLines = original.Split("\r\n");
        var afterLines = after.Split("\r\n");
        Assert.That(afterLines, Has.Length.EqualTo(beforeLines.Length), "The edit adds no lines");
        var changed = Enumerable.Range(0, beforeLines.Length).Where(i => beforeLines[i] != afterLines[i]).ToArray();
        Assert.That(changed, Is.EqualTo(expectedChanged), "Only the declaration and the call lines may differ; every other line stays byte-identical");

        var change = FileLineChange.Compute("Widget.cs", original, after);
        Assert.That(change.LinesAdded, Is.EqualTo(expectedChanged.Length));
        Assert.That(change.LinesRemoved, Is.EqualTo(expectedChanged.Length));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignatureLayout_CrlfConstructor_LargeFileWithDistantCallSites_ReportsOnlyTheEditedLines()
    {
        // A ~6000 line file with call sites near both ends: the differing middle is thousands of lines wide, which the
        // line counter once reported in full (the whole span as removed and re-added).
        var lines = new List<string> { "public class Widget", "{", "    public Widget(string name) { }", "    public static Widget First() => new Widget(\"first\");" };
        for (int i = 0; i < 2900; i++)
        {
            lines.Add($"    public int Filler{i}() => {i};");
            lines.Add(string.Empty);
        }

        lines.Add("    public static Widget Last() => new Widget(\"last\");");
        lines.Add("}");
        var original = Lines(lines.ToArray());
        SetFile("Widget.cs", original);
        var result = await ChangeAt("Widget.cs", "Widget", "public Widget(string name)", new ExistingParameterSpec(0), new NewParameterSpec("hint", "string?", "null"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Widget.cs"];
        AssertNoBareLineFeed(after);

        var beforeLines = original.Split("\r\n");
        var afterLines = after.Split("\r\n");
        Assert.That(afterLines, Has.Length.EqualTo(beforeLines.Length), "The edit adds no lines");
        var changed = Enumerable.Range(0, beforeLines.Length).Where(i => beforeLines[i] != afterLines[i]).ToArray();
        Assert.That(changed, Is.EqualTo(new[] { 2, 3, beforeLines.Length - 2 }), "Only the declaration and the two call lines may differ");

        var change = FileLineChange.Compute("Widget.cs", original, after);
        Assert.That(change.LinesAdded, Is.EqualTo(3));
        Assert.That(change.LinesRemoved, Is.EqualTo(3));
    }
}
