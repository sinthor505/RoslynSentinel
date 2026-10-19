using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Slice 6 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for the <c>AsyncOptimizationEngine</c> sites that ended in
/// <c>RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace</c>. Each input has an untouched, badly spaced member
/// (<c>UntouchedLine</c>) that the rewrite does not change. "Mixed" is LF except the untouched line, which ends in a
/// stray CRLF. The pins encode the migrated contract (annotated-span formatting): the untouched line keeps its text,
/// blank lines between members survive, changed lines take the document's dominant line ending, and a stray CRLF on an
/// untouched line survives. The pre-migration whole-subtree pass emitted LF for every input, split the untouched member
/// over three lines, dropped the blank line after an expression-bodied member, and put the first scaffold comment of
/// GenerateAsyncOverload on the brace line. ConvertToAsyncBridge is the exception: it still runs the whole-subtree pass,
/// so its pin is the pre-migration output.
/// </summary>
[TestFixture]
[Category("AsyncOptimizationEngine")] // sentinel:auto-category
public class AsyncOptimizationNormalizeCharacterizationTests
{
    private const string UntouchedLine = "    public void  Untouched( )  { }";

    private IWorkspaceManager _workspaceManager;
    private AsyncOptimizationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new AsyncOptimizationEngine(_workspaceManager);
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
    public async Task OptimizeIndependentAwaits_WhenAll_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        await A();",
            "        await B();",
            "        Done();",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.OptimizeIndependentAwaitsAsync("Test.cs", "RunAsync");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        await Task.WhenAll(A(), B());",
            "        Done();",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task OptimizeIndependentAwaits_Hoisted_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        var a = await GetAAsync();",
            "        var b = await GetBAsync();",
            "        Use(a, b);",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.OptimizeIndependentAwaitsAsync("Test.cs", "RunAsync");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        var aTask = GetAAsync();",
            "        var bTask = GetBAsync();",
            "        var a = await aTask;",
            "        var b = await bTask;",
            "        Use(a, b);",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task GenerateAsyncOverload_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class C",
            "{",
            "    public int Compute(int x)",
            "    {",
            "        return x;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.GenerateAsyncOverloadAsync("Test.cs", "Compute");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class C",
            "{",
            "    public int Compute(int x)",
            "    {",
            "        return x;",
            "    }",
            "",
            "    public async Task<int> ComputeAsync(int x, CancellationToken cancellationToken = default)",
            "    {",
            "        // TODO: Replace synchronous operations with their async equivalents",
            "        // e.g., File.ReadAllText -> File.ReadAllTextAsync, DbCommand.ExecuteReader -> ExecuteReaderAsync",
            "        // placeholder - remove and implement properly",
            "        await Task.CompletedTask;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertToAsyncBridge_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class C",
            "{",
            "    [MigrationCandidate(\"AsyncBridgeCandidate\")]",
            "    public int Compute(int x)",
            "    {",
            "        return x;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertToAsyncBridgeAsync("Test.cs", "Compute");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        // Still the pre-migration pin: this site deliberately keeps the whole-subtree pass (see the engine comment).
        AssertPinned(result.UpdatedText, Build("LF",
            "public class C",
            "{",
            "    [Obsolete(\"Asyncify-bridge: call ComputeAsync instead.\", false)]",
            "    public int Compute(int x)",
            "    {",
            "        // Asyncify-bridge: synchronous wrapper over ComputeAsync.",
            "        return ComputeAsync(x).GetAwaiter().GetResult();",
            "    }",
            "",
            "    public async Task<int> ComputeAsync(int x, CancellationToken cancellationToken = default)",
            "    {",
            "        return x;",
            "    }",
            "",
            "    public void Untouched()",
            "    {",
            "    }",
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertEventHandlerCallerToAsyncVoid_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "using System;",
            "",
            "public class C",
            "{",
            "    [Obsolete(\"Asyncify-bridge: call LoadAsync instead.\", false)]",
            "    public int Load() => 1;",
            "",
            "    public System.Threading.Tasks.Task<int> LoadAsync() => System.Threading.Tasks.Task.FromResult(1);",
            "",
            "    public void OnClick(object sender, EventArgs e)",
            "    {",
            "        var n = Load();",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertEventHandlerCallerToAsyncVoidAsync("Test.cs", "OnClick");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System;",
            "",
            "public class C",
            "{",
            "    [Obsolete(\"Asyncify-bridge: call LoadAsync instead.\", false)]",
            "    public int Load() => 1;",
            "",
            "    public System.Threading.Tasks.Task<int> LoadAsync() => System.Threading.Tasks.Task.FromResult(1);",
            "",
            "    public async void OnClick(object sender, EventArgs e)",
            "    {",
            "        var n = await LoadAsync();",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task RewriteObsoleteCallsInAsyncMethod_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "using System;",
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "",
            "public class C",
            "{",
            "    [Obsolete(\"Asyncify-bridge: call LoadAsync instead.\", false)]",
            "    public int Load(int x) => x;",
            "",
            "    public Task<int> LoadAsync(int x, CancellationToken cancellationToken = default) => Task.FromResult(x);",
            "",
            "    public async Task<int> RunAsync(CancellationToken cancellationToken)",
            "    {",
            "        return Load(1);",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.RewriteObsoleteCallsInAsyncMethodAsync("Test.cs", "RunAsync");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System;",
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "",
            "public class C",
            "{",
            "    [Obsolete(\"Asyncify-bridge: call LoadAsync instead.\", false)]",
            "    public int Load(int x) => x;",
            "",
            "    public Task<int> LoadAsync(int x, CancellationToken cancellationToken = default) => Task.FromResult(x);",
            "",
            "    public async Task<int> RunAsync(CancellationToken cancellationToken)",
            "    {",
            "        return await LoadAsync(1, cancellationToken);",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task AddConfigureAwaitFalse_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        await A();",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.AddConfigureAwaitFalseAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        await A().ConfigureAwait(false);",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task RemoveConfigureAwaitFalse_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        await A().ConfigureAwait(false);",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.RemoveConfigureAwaitFalseAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync()",
            "    {",
            "        await A();",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertToAsyncEnumerable_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class C",
            "{",
            "    public List<int> GetAll()",
            "    {",
            "        var results = new List<int>();",
            "        results.Add(1);",
            "        return results;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertToAsyncEnumerableAsync("Test.cs", "GetAll");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class C",
            "{",
            "    public async IAsyncEnumerable<int> GetAll(CancellationToken ct = default)",
            "    {",
            "        yield return 1;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task AddCancellationTokenToMethod_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync(int x)",
            "    {",
            "        await Task.Delay(x);",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.AddCancellationTokenToMethodAsync("Test.cs", "RunAsync");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class C",
            "{",
            "    public async Task RunAsync(int x, CancellationToken cancellationToken = default)",
            "    {",
            "        await Task.Delay(x, cancellationToken);",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task FlagMigrationCandidate_ByteExact(string eol)
    {
        // Re-flagging keeps the existing FlaggedDate, which keeps the pin independent of today's date.
        SetSource(Build(eol,
            "public class C",
            "{",
            "    [MigrationCandidate(\"AsyncBridgeCandidate\", FlaggedDate = \"2026-01-02\")]",
            "    public int Compute(int x)",
            "    {",
            "        return x;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.FlagMigrationCandidateAsync("Test.cs", "Compute", "AsyncBridgeCandidate", 5, "io");

        AssertPinned(result.Changes.First().Value, Build(eol,
            "public class C",
            "{",
            "    [MigrationCandidate(\"AsyncBridgeCandidate\", Score = 5, Reason = \"io\", FlaggedDate = \"2026-01-02\")]",
            "    public int Compute(int x)",
            "    {",
            "        return x;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task FlagMultipleMigrationCandidates_ByteExact(string eol)
    {
        // Re-flagging keeps the existing FlaggedDate, which keeps the pin independent of today's date.
        SetSource(Build(eol,
            "public class C",
            "{",
            "    [MigrationCandidate(\"AsyncBridgeCandidate\", FlaggedDate = \"2026-01-02\")]",
            "    public int First(int x)",
            "    {",
            "        return x;",
            "    }",
            "",
            "    [MigrationCandidate(\"AsyncBridgeCandidate\", FlaggedDate = \"2026-01-03\")]",
            "    public int Second(int x)",
            "    {",
            "        return x;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var items = new List<(FilePathWrapper FilePath, string MethodName, string Pattern, int Score, string? Reason)>
        {
            ("Test.cs", "First", "AsyncBridgeCandidate", 5, "io"),
            ("Test.cs", "Second", "AsyncBridgeCandidate", 7, null),
        };
        var (results, errors) = await _engine.FlagMultipleMigrationCandidatesAsync(items);

        Assert.That(errors, Is.Empty);
        AssertPinned(results[0].Changes.First().Value, Build(eol,
            "public class C",
            "{",
            "    [MigrationCandidate(\"AsyncBridgeCandidate\", Score = 5, Reason = \"io\", FlaggedDate = \"2026-01-02\")]",
            "    public int First(int x)",
            "    {",
            "        return x;",
            "    }",
            "",
            "    [MigrationCandidate(\"AsyncBridgeCandidate\", Score = 7, FlaggedDate = \"2026-01-03\")]",
            "    public int Second(int x)",
            "    {",
            "        return x;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }
}
