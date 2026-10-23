using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Slice 5a characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins (LF, CRLF and Mixed output) for the twelve <c>LogicSimplificationEngine</c> edit paths that now end in
/// the EOL-aware <c>RoslynFormattingHelper</c> helpers (formerly a whole-subtree whitespace pass). Each input has an untouched, badly spaced member
/// (<c>UntouchedLine</c>) that the rewrite does not change. "Mixed" is LF except the untouched line, which ends in a
/// stray CRLF. The pins encode the migrated contract (annotated-span formatting): the untouched line keeps its text,
/// changed lines take the document's dominant line ending, and a stray CRLF on an untouched line survives. The
/// pre-migration whole-subtree pass emitted LF for every input and split the untouched member over three lines; the
/// rewritten code itself came out identical.
/// </summary>
[TestFixture]
[Category("LogicSimplificationEngine")] // sentinel:auto-category
public class LogicOptimizationNormalizeCharacterizationTests
{
    private const string UntouchedLine = "    public void  Untouched( )  { }";

    private IWorkspaceManager _workspaceManager;
    private LogicSimplificationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new LogicSimplificationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>Joins lines without a trailing newline: LF, CRLF, or "Mixed" (LF, CRLF after <c>UntouchedLine</c> only).</summary>
    private static string Build(string eol, params string[] lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i == lines.Length - 1)
            {
                break;
            }

            var crlf = eol == "CRLF" || (eol == "Mixed" && lines[i] == UntouchedLine);
            sb.Append(crlf ? "\r\n" : "\n");
        }

        return sb.ToString();
    }

    private static void AssertPinned(string? actual, string expected) =>
        Assert.That(actual, Is.EqualTo(expected).NoClip);

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task SimplifyBooleanExpressions_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public bool Check(bool flag)",
            "    {",
            "        var a = flag != true;",
            "        return flag == true;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.SimplifyBooleanExpressionsAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public bool Check(bool flag)",
            "    {",
            "        var a = !(flag);",
            "        return flag;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task AddGuardClauses_Modified_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public void Run(string name, int count)",
            "    {",
            "        System.Console.WriteLine(name);",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.AddGuardClausesAsync("Test.cs", "Run");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public void Run(string name, int count)",
            "    {",
            "        ArgumentNullException.ThrowIfNull(name);",
            "        System.Console.WriteLine(name);",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task AddGuardClauses_NoCandidates_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public void Run(int count)",
            "    {",
            "        System.Console.WriteLine(count);",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.AddGuardClausesAsync("Test.cs", "Run");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.NoCandidatesFound));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public void Run(int count)",
            "    {",
            "        System.Console.WriteLine(count);",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertToNullCoalescing_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public string Pick(string s, string t)",
            "    {",
            "        if (t == null)",
            "        {",
            "            t = \"a\";",
            "        }",
            "        return s != null ? s : t;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertToNullCoalescingAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public string Pick(string s, string t)",
            "    {",
            "        t ??= \"a\";",
            "        return s ?? t;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertToSwitch_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public void Run(int x)",
            "    {",
            "        if (x == 1)",
            "        {",
            "            System.Console.WriteLine(\"a\");",
            "        }",
            "        else if (x == 2)",
            "        {",
            "            System.Console.WriteLine(\"b\");",
            "        }",
            "        else",
            "        {",
            "            System.Console.WriteLine(\"c\");",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertToSwitchAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public void Run(int x)",
            "    {",
            "        switch (x)",
            "        {",
            "            case 1:",
            "                System.Console.WriteLine(\"a\");",
            "                break;",
            "            case 2:",
            "                System.Console.WriteLine(\"b\");",
            "                break;",
            "            default:",
            "                System.Console.WriteLine(\"c\");",
            "                break;",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertIfToSwitchExpression_ByteExact(string eol)
    {
        SetSource(Build(eol, IfChainSource(UntouchedLine)));

        var result = await _engine.ConvertIfToSwitchExpressionAsync("Test.cs", "Name");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public string Name(int x)",
            "    {",
            "        return x switch",
            "        {",
            "            1 => \"a\",",
            "            2 => \"b\",",
            "            _ => \"c\"",
            "        };",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertIfToSwitchStatement_ByteExact(string eol)
    {
        SetSource(Build(eol, IfChainSource(UntouchedLine)));

        var result = await _engine.ConvertIfToSwitchStatementAsync("Test.cs", "Name");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public string Name(int x)",
            "    {",
            "        switch (x)",
            "        {",
            "            case 1:",
            "                return \"a\";",
            "            case 2:",
            "                return \"b\";",
            "            default:",
            "                return \"c\";",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    private static string[] IfChainSource(string untouched) =>
    [
        "public class Sample",
        "{",
        "    public string Name(int x)",
        "    {",
        "        if (x == 1)",
        "            return \"a\";",
        "        else if (x == 2)",
        "            return \"b\";",
        "        else",
        "            return \"c\";",
        "    }",
        "",
        untouched,
        "}",
    ];

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ExtensionToStatic_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public static class Ext",
            "{",
            "    public static int Twice(this int value)",
            "    {",
            "        return value * 2;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ExtensionToStaticAsync("Test.cs", "Twice");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public static class Ext",
            "{",
            "    public static int Twice(int value)",
            "    {",
            "        return value * 2;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertStaticToExtension_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Ext",
            "{",
            "    public static int Twice(int value)",
            "    {",
            "        return value * 2;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertStaticToExtensionAsync("Test.cs", "Twice");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public static class Ext",
            "{",
            "    public static int Twice(this int value)",
            "    {",
            "        return value * 2;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertForEachToFor_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public void Run(int[] items)",
            "    {",
            "        foreach (var item in items)",
            "        {",
            "            System.Console.WriteLine(item);",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertForEachToForAsync("Test.cs", 5);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public void Run(int[] items)",
            "    {",
            "        for (int i = 0; i < items.Length; i++)",
            "        {",
            "            var item = items[i];",
            "            System.Console.WriteLine(item);",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertForToForEach_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public void Run(int[] items)",
            "    {",
            "        for (int i = 0; i < items.Length; i++)",
            "        {",
            "            System.Console.WriteLine(items[i]);",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertForToForEachAsync("Test.cs", 5);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public void Run(int[] items)",
            "    {",
            "        foreach (var item in items)",
            "        {",
            "            System.Console.WriteLine(item);",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertWhileToFor_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public void Run()",
            "    {",
            "        int i = 0;",
            "        while (i < 3)",
            "        {",
            "            System.Console.WriteLine(i);",
            "            i++;",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertWhileToForAsync("Test.cs", 6);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public void Run()",
            "    {",
            "        for (int i = 0; i < 3; i++)",
            "        {",
            "            System.Console.WriteLine(i);",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [Test]
    public async Task SimplifyBooleanExpressions_MissingFile_ThrowsNotFound()
    {
        SetSource("public class Sample { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.SimplifyBooleanExpressionsAsync("Missing.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ExtensionToStatic_MissingFile_ThrowsNotFound()
    {
        SetSource("public static class Ext { public static int Twice(this int x) => x * 2; }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ExtensionToStaticAsync("Missing.cs", "Twice"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ConvertForEachToFor_MissingFile_ThrowsNotFound()
    {
        SetSource("public class Sample { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ConvertForEachToForAsync("Missing.cs", 5));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ReduceBlockDepth_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ReduceBlockDepthAsync("Missing.cs", "Foo"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}
