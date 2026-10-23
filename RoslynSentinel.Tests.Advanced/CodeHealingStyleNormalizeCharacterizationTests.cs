using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C6-5 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins (LF, CRLF and Mixed output) for the eleven <c>CodeHealingEngine</c>, <c>CodeStyleEngine</c> and <c>IDEStyleEngine</c> methods
/// that now end in the EOL-aware <c>RoslynFormattingHelper</c> helpers (formerly a whole-subtree whitespace pass). Each input has an untouched, badly
/// spaced member (<c>UntouchedLine</c>) that the rewrite does not change. "Mixed" is LF except the untouched line,
/// which ends in a stray CRLF. The pins encode the migrated contract (annotated-span formatting): the untouched line
/// keeps its text, blank lines between members survive, changed lines take the document's dominant line ending, and a
/// stray CRLF on an untouched line survives. The pre-migration whole-subtree pass emitted LF for every input, split
/// the untouched member over three lines, dropped the blank line after a field, wrote "catch (Exception)when" and
/// spread the object initializer over five lines.
/// </summary>
[TestFixture]
[Category("CodeHealingEngine")] // sentinel:auto-category
[Category("CodeStyleEngine")] // sentinel:auto-category
[Category("IDEStyleEngine")] // sentinel:auto-category
public class CodeHealingStyleNormalizeCharacterizationTests
{
    private const string UntouchedLine = "    public void  Untouched( )  { }";

    private IWorkspaceManager _workspaceManager;
    private CodeHealingEngine _healing;
    private CodeStyleEngine _style;
    private IDEStyleEngine _ide;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var config = new SentinelConfiguration();
        _healing = new CodeHealingEngine(_workspaceManager, config);
        _style = new CodeStyleEngine(_workspaceManager, config);
        _ide = new IDEStyleEngine(_workspaceManager);
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
    public async Task FixThreadSleep_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "",
            "public class Worker",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        Thread.Sleep(100);",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _healing.FixThreadSleepAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "",
            "public class Worker",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        await Task.Delay(100);",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task AddRetryPolicy_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "using System;",
            "",
            "public class Worker",
            "{",
            "    public void Run()",
            "    {",
            "        Console.WriteLine();",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _healing.AddRetryPolicyAsync("Test.cs", 0, 0, 3);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System;",
            "",
            "public class Worker",
            "{",
            "    public void Run()",
            "    {",
            "        int _retryCount = 0;",
            "        while (true)",
            "        {",
            "            try",
            "            {",
            "                Console.WriteLine();",
            "                break;",
            "            }",
            "            catch (Exception) when (_retryCount < 3)",
            "            {",
            "                _retryCount++;",
            "            }",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ModernizeExceptions_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Worker",
            "{",
            "    public void Run()",
            "    {",
            "        throw new System.Exception(\"bad\");",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var changes = await _healing.ModernizeExceptionsAsync([new CodeHealingEngine.ExceptionTarget("Test.cs", 5, "WorkerException")]);

        AssertPinned(changes["Test.cs"], Build(eol,
            "public class Worker",
            "{",
            "    public void Run()",
            "    {",
            "        throw new WorkerException(\"bad\");",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task FixDangerousLock_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "using System;",
            "",
            "public class Counter",
            "{",
            "    private int _n;",
            "",
            "    public void Inc()",
            "    {",
            "        lock (this)",
            "        {",
            "            _n++;",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _style.FixDangerousLockAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System;",
            "using System.Threading;",
            "",
            "public class Counter",
            "{",
            "    private readonly Lock _lockObj = new Lock();",
            "    private int _n;",
            "",
            "    public void Inc()",
            "    {",
            "        lock (_lockObj)",
            "        {",
            "            _n++;",
            "        }",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task SimplifyVerbosity_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "using System.Collections.Generic;",
            "",
            "public class Holder",
            "{",
            "    private List<int> _cache;",
            "",
            "    public void Init()",
            "    {",
            "        List<int> items = new List<int>();",
            "        if (_cache == null) _cache = new List<int>();",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _style.SimplifyVerbosityAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System.Collections.Generic;",
            "",
            "public class Holder",
            "{",
            "    private List<int> _cache;",
            "",
            "    public void Init()",
            "    {",
            "        List<int> items = new();",
            "        _cache ??= new();",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task UseCollectionExpressions_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "using System.Collections.Generic;",
            "",
            "public class Holder",
            "{",
            "    public int[] Values()",
            "    {",
            "        int[] a = new int[] { 1, 2, 3 };",
            "        List<int> b = new List<int> { 4, 5 };",
            "        return a;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _style.UseCollectionExpressionsAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System.Collections.Generic;",
            "",
            "public class Holder",
            "{",
            "    public int[] Values()",
            "    {",
            "        int[] a = [1, 2, 3];",
            "        List<int> b = [4, 5];",
            "        return a;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task UseTimeProvider_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "using System;",
            "",
            "public class Clock",
            "{",
            "    public DateTimeOffset Stamp()",
            "    {",
            "        return DateTimeOffset.UtcNow;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _style.UseTimeProviderAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System;",
            "",
            "public class Clock",
            "{",
            "    private readonly TimeProvider _timeProvider;",
            "    public DateTimeOffset Stamp()",
            "    {",
            "        return _timeProvider.GetUtcNow();",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task SimplifyAllNames_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Names",
            "{",
            "    public string Get()",
            "    {",
            "        global::System.Console.WriteLine();",
            "        return global::System.String.Empty;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _style.SimplifyAllNamesAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Names",
            "{",
            "    public string Get()",
            "    {",
            "        System.Console.WriteLine();",
            "        return System.String.Empty;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task UseIndexFromEnd_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Arrays",
            "{",
            "    public int Last(int[] arr)",
            "    {",
            "        return arr[arr.Length - 1] + 0;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _style.UseIndexFromEndAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Arrays",
            "{",
            "    public int Last(int[] arr)",
            "    {",
            "        return arr[^1] + 0;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task UseObjectInitializers_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Person",
            "{",
            "    public string Name;",
            "    public int Age;",
            "}",
            "",
            "public class Factory",
            "{",
            "    public Person Make()",
            "    {",
            "        var p = new Person();",
            "        p.Name = \"a\";",
            "        p.Age = 1;",
            "        return p;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _ide.UseObjectInitializersAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Person",
            "{",
            "    public string Name;",
            "    public int Age;",
            "}",
            "",
            "public class Factory",
            "{",
            "    public Person Make()",
            "    {",
            // The pre-migration whole-subtree pass spread the initializer over five lines; the formatter keeps the
            // synthesized initializer on the declaration's line.
            "        var p = new Person() { Name = \"a\", Age = 1 };",
            "        return p;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task UseNullPropagation_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class Notifier",
            "{",
            "    private Notifier _next;",
            "",
            "    public void Ping()",
            "    {",
            "        if (_next != null) _next.Ping();",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _ide.UseNullPropagationAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Notifier",
            "{",
            "    private Notifier _next;",
            "",
            "    public void Ping()",
            "    {",
            "        _next?.Ping();",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [Test]
    public async Task FixDangerousLock_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _style.FixDangerousLockAsync("Missing.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task SimplifyVerbosity_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _style.SimplifyVerbosityAsync("Missing.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UseTimeProvider_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _style.UseTimeProviderAsync("Missing.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UseIndexFromEnd_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _style.UseIndexFromEndAsync("Missing.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}
