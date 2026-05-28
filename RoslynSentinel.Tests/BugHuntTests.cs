// BugHuntTests.cs — Deep bug hunt across RoslynSentinel engines
// Each test documents a confirmed bug with:
//   - What triggers it (false positive / false negative)
//   - What SHOULD happen
//   - What DOES happen (the bug)
//
// Tests are written to assert CORRECT behavior → they FAIL on the current code,
// confirming the bug. They will PASS once fixed.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BugHuntTests
{
    private PersistentWorkspaceManager _workspaceManager;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ==========================================================================
    // BUG 1: AsyncSafetyEngine.FindBlockingCallsInAsyncAsync
    //
    // TYPE:     False positive
    // ROOT CAUSE: The .Result check only guards against InvocationExpressionSyntax
    //             parents, but does NOT guard against AssignmentExpressionSyntax.
    //             Compare with AntiPatternEngine.DetectBlockingTaskWait which has:
    //               if (name == "Result" && ma.Parent is AssignmentExpressionSyntax
    //                   assign && assign.Left == ma) continue;
    //             FindBlockingCallsInAsyncAsync lacks that guard entirely.
    //
    // TRIGGERS: Any  obj.Result = value  write inside an async method — e.g.
    //           ASP.NET action filters writing to context.Result, or any domain
    //           object whose property happens to be named Result.
    //
    // EXPECTED: 0 reports (it is a property set, not a Task.Result blocking read)
    // ACTUAL:   1 report ("Line N: .Result accessed in async method")
    // SEVERITY: Major — very common in ASP.NET controller / filter code
    // ==========================================================================

    [Test]
    public async Task BUG1_FindBlockingCallsInAsync_ResultPropertySet_ShouldNotFlag()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        const string source = """
            class ActionContext { public string Result { get; set; } = ""; }
            class MyFilter
            {
                private readonly ActionContext _ctx = new ActionContext();
                public async System.Threading.Tasks.Task ExecuteAsync()
                {
                    // This is a property SETTER, not reading Task.Result.
                    // It must NOT be flagged as a blocking call.
                    _ctx.Result = "error";
                    await System.Threading.Tasks.Task.Delay(1);
                }
            }
            """;
        SetSource(source);

        var result = await engine.FindBlockingCallsInAsyncAsync("Test.cs");

        Assert.That(
            result.Where(r => r.Reason.Contains(".Result accessed")),
            Is.Empty,
            "BUG: _ctx.Result = 'error' is a property SET (left-hand side of assignment). " +
            "It must not be reported as '.Result accessed in async method'. " +
            "Fix: add the same assignment-target guard that AntiPatternEngine.DetectBlockingTaskWait uses.");
    }

    // Confirm the true positive still fires (reading Task.Result in async method)
    [Test]
    public async Task BUG1_FindBlockingCallsInAsync_TaskResultRead_ShouldFlag()
    {
        var engine = new AsyncSafetyEngine(_workspaceManager);
        const string source = """
            class Worker
            {
                public async System.Threading.Tasks.Task RunAsync()
                {
                    var t = System.Threading.Tasks.Task.FromResult(42);
                    var v = t.Result;   // genuine blocking read
                    await System.Threading.Tasks.Task.Delay(0);
                }
            }
            """;
        SetSource(source);

        var result = await engine.FindBlockingCallsInAsyncAsync("Test.cs");

        Assert.That(
            result.Where(r => r.Reason.Contains(".Result accessed")),
            Is.Not.Empty,
            "Genuine .Result read inside an async method should still be flagged.");
    }

    // ==========================================================================
    // BUG 2: AntiPatternEngine.DetectAsyncVoidMethod
    //
    // TYPE:     False positive
    // ROOT CAUSE: AntiPatternEngine.IsEventHandlerSignature compares the first
    //             parameter type with the literal string "object" or "Object".
    //             In nullable-reference-type-enabled code the type text is
    //             "object?" (with trailing ?).  The check
    //               firstType == "object" || firstType == "Object"
    //             fails for "object?" so the method is NOT recognised as an
    //             event handler and is incorrectly flagged as AsyncVoidMethod.
    //
    //             The fixed version in AsyncSafetyEngine.IsEventHandlerSignature
    //             uses  .TrimEnd('?')  before comparing — that fix was never
    //             back-ported to AntiPatternEngine.
    //
    // TRIGGERS: Any #nullable enable project where event handlers are written as
    //             async void OnClick(object? sender, SomeEventArgs e)
    //
    // EXPECTED: 0 AsyncVoidMethod findings (it IS a valid event handler)
    // ACTUAL:   1 finding (wrongly flagged)
    // SEVERITY: Minor-to-Major depending on project — ubiquitous in WinForms/WPF/MAUI
    // ==========================================================================

    [Test]
    public async Task BUG2_AsyncVoidEventHandler_NullableSender_ShouldNotFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            class MyForm
            {
                // Nullable-aware event handler — the only legitimate use of async void.
                // Should be excluded from the AsyncVoidMethod anti-pattern.
                private async void OnButtonClick(object? sender, System.EventArgs e)
                {
                    await System.Threading.Tasks.Task.Delay(100);
                }
            }
            """;
        SetSource(source);

        var result = await engine.DetectAntiPatternsAsync(
            "Test.cs", patternFilter: ["AsyncVoidMethod"]);

        Assert.That(
            result.Where(f => f.Pattern == "AsyncVoidMethod"),
            Is.Empty,
            "BUG: async void with (object? sender, EventArgs e) is the canonical event-handler " +
            "signature in nullable-aware code. AntiPatternEngine.IsEventHandlerSignature does not " +
            "strip the '?' from the first parameter type before comparing, so 'object?' != 'object'. " +
            "Fix: apply .TrimEnd('?') to firstType before the string comparison (as AsyncSafetyEngine already does).");
    }

    // Control: non-nullable sender with EventArgs → NOT flagged (already works)
    [Test]
    public async Task BUG2_Control_AsyncVoidEventHandler_NonNullableSender_ShouldNotFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            class MyForm
            {
                private async void OnButtonClick(object sender, System.EventArgs e)
                {
                    await System.Threading.Tasks.Task.Delay(100);
                }
            }
            """;
        SetSource(source);

        var result = await engine.DetectAntiPatternsAsync(
            "Test.cs", patternFilter: ["AsyncVoidMethod"]);

        Assert.That(
            result.Where(f => f.Pattern == "AsyncVoidMethod"),
            Is.Empty,
            "async void (object sender, EventArgs e) — non-nullable — should not be flagged (control).");
    }

    // Control: non-event async void IS flagged (still works after fix)
    [Test]
    public async Task BUG2_Control_AsyncVoidNonEventHandler_ShouldFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            class MyService
            {
                public async void DoWork()
                {
                    await System.Threading.Tasks.Task.Delay(1);
                }
            }
            """;
        SetSource(source);

        var result = await engine.DetectAntiPatternsAsync(
            "Test.cs", patternFilter: ["AsyncVoidMethod"]);

        Assert.That(
            result.Where(f => f.Pattern == "AsyncVoidMethod"),
            Is.Not.Empty,
            "async void without event-handler signature SHOULD be flagged.");
    }

    // ==========================================================================
    // BUG 3: AntiPatternEngine.FindMutablePublicPropertiesAsync
    //
    // TYPE:     False positive
    // ROOT CAUSE: The method retrieves the first SetAccessorDeclaration found on
    //             the property, but does NOT check the accessor's modifiers.
    //             A "private set" or "protected set" accessor is still a
    //             SetAccessorDeclaration and passes the null-check, so the
    //             property is flagged as having "a public setter" even though
    //             the setter is restricted.
    //
    //             The description in the emitted finding even says "with a public
    //             setter" — which is factually wrong for private/protected set.
    //
    // EXPECTED: 0 MutablePublicApi findings (private/protected set is NOT public)
    // ACTUAL:   2 findings (Name and Count both falsely flagged)
    // SEVERITY: Major — every domain entity using the common "public get, private set"
    //           encapsulation pattern is incorrectly reported
    // ==========================================================================

    [Test]
    public async Task BUG3_FindMutablePublicProperties_PrivateSetter_ShouldNotFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            public class UserAccount
            {
                // private set is NOT a public setter — must not be flagged.
                public string Name { get; private set; } = "";
                // protected set is also NOT a public setter — must not be flagged.
                public int LoginCount { get; protected set; }
            }
            """;
        SetSource(source);

        var result = await engine.FindMutablePublicPropertiesAsync("Test.cs");

        Assert.That(
            result.Where(f => f.Pattern == "MutablePublicApi"),
            Is.Empty,
            "BUG: { get; private set; } and { get; protected set; } are NOT mutable public APIs. " +
            "The set accessor must be checked for PrivateKeyword / ProtectedKeyword modifiers " +
            "before reporting. Fix: skip the setAccessor when it carries private/protected modifier.");
    }

    // Control: a genuinely public setter IS flagged (regression guard)
    [Test]
    public async Task BUG3_Control_FindMutablePublicProperties_PublicSetter_ShouldFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            public class UserAccount
            {
                public string Name { get; set; } = "";
            }
            """;
        SetSource(source);

        var result = await engine.FindMutablePublicPropertiesAsync("Test.cs");

        Assert.That(
            result.Where(f => f.Pattern == "MutablePublicApi"),
            Is.Not.Empty,
            "A truly public { get; set; } SHOULD be flagged as mutable public API (control).");
    }

    // Edge: init-only setter must NOT be flagged (init ≠ set, already handled correctly — regression guard)
    [Test]
    public async Task BUG3_Control_FindMutablePublicProperties_InitSetter_ShouldNotFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            public class ImmutableRecord
            {
                public string Name { get; init; } = "";
            }
            """;
        SetSource(source);

        var result = await engine.FindMutablePublicPropertiesAsync("Test.cs");

        Assert.That(
            result.Where(f => f.Pattern == "MutablePublicApi"),
            Is.Empty,
            "{ get; init; } is init-only — it should NOT be flagged as mutable public API.");
    }

    // ==========================================================================
    // BUG 4: PerformanceEngine.AnalyzePerformanceAsync
    //
    // TYPE:     False negative (both detection paths)
    // ROOT CAUSE: Both the BinaryExpression-+ path and the AddAssignment-+= path
    //             check ancestors for:
    //               a is ForStatementSyntax || a is ForEachStatementSyntax || a is WhileStatementSyntax
    //             Neither path includes  DoStatementSyntax.
    //             String concatenation inside a  do { ... } while (...)  loop is
    //             therefore silently ignored.
    //             Note: AntiPatternEngine.DetectStringConcatInLoop DOES include
    //             DoStatementSyntax — the omission is specific to PerformanceEngine.
    //
    // EXPECTED: At least 1 StringConcatenationInLoop report
    // ACTUAL:   0 reports
    // SEVERITY: Minor — do-while loops are less common, but the inconsistency with
    //           AntiPatternEngine is surprising and creates a gap in coverage.
    // ==========================================================================

    [Test]
    public async Task BUG4_PerformanceEngine_StringConcatBinaryInDoWhile_ShouldFlag()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        const string source = """
            class Builder
            {
                public string Build(string[] items)
                {
                    string result = "";
                    int i = 0;
                    do
                    {
                        // string + literal inside do-while — identical risk to for/foreach/while
                        result = result + "item";
                        i++;
                    } while (i < items.Length);
                    return result;
                }
            }
            """;
        SetSource(source);

        var result = await engine.AnalyzePerformanceAsync("Test.cs");

        Assert.That(
            result.Where(r => r.IssueType == "StringConcatenationInLoop"),
            Is.Not.Empty,
            "BUG: string concatenation with '+' inside a do-while loop should be flagged as " +
            "StringConcatenationInLoop. DoStatementSyntax is missing from both ancestor checks " +
            "in AnalyzePerformanceAsync. Fix: add  || a is DoStatementSyntax  to each inLoop check.");
    }

    [Test]
    public async Task BUG4_PerformanceEngine_StringConcatPlusAssignInDoWhile_ShouldFlag()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        const string source = """
            class Builder
            {
                public string Build(string[] items)
                {
                    string htmlStr = "";
                    int i = 0;
                    do
                    {
                        // += inside do-while — same O(n²) risk
                        htmlStr += "item";
                        i++;
                    } while (i < items.Length);
                    return htmlStr;
                }
            }
            """;
        SetSource(source);

        var result = await engine.AnalyzePerformanceAsync("Test.cs");

        Assert.That(
            result.Where(r => r.IssueType == "StringConcatenationInLoop"),
            Is.Not.Empty,
            "BUG: string += inside a do-while loop should be flagged. " +
            "The AddAssignmentExpression path also missing DoStatementSyntax.");
    }

    // Control: same += inside a foreach IS detected (regression guard)
    [Test]
    public async Task BUG4_Control_PerformanceEngine_StringConcatInForeach_ShouldFlag()
    {
        var engine = new PerformanceEngine(_workspaceManager);
        const string source = """
            class Builder
            {
                public string Build(string[] items)
                {
                    string htmlStr = "";
                    foreach (var item in items)
                        htmlStr += "x";
                    return htmlStr;
                }
            }
            """;
        SetSource(source);

        var result = await engine.AnalyzePerformanceAsync("Test.cs");

        Assert.That(
            result.Where(r => r.IssueType == "StringConcatenationInLoop"),
            Is.Not.Empty,
            "string += inside foreach SHOULD be flagged (control).");
    }

    // ==========================================================================
    // BUG 5: AntiPatternEngine.DetectStringConcatInLoop (StringConcatInLoop)
    //
    // TYPE:     False negative
    // ROOT CAUSE: DetectStringConcatInLoop only inspects AssignmentExpressionSyntax
    //             nodes whose kind is AddAssignmentExpression (the  +=  operator).
    //             The equivalent pattern  str = str + val  uses a
    //             SimpleAssignmentExpression whose Right is a BinaryAddExpression.
    //             This form is completely missed because the entry condition
    //               if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)) continue;
    //             skips it immediately.
    //
    // EXPECTED: At least 1 StringConcatInLoop finding
    // ACTUAL:   0 findings
    // SEVERITY: Minor — +=  is more idiomatic in C#, but  = x + y  is a real
    //           pattern (particularly in code ported from other languages or
    //           generated by LLMs), and the inconsistency between the two forms
    //           is a genuine detection gap.
    // ==========================================================================

    [Test]
    public async Task BUG5_AntiPattern_StringConcatWithAddOperator_ShouldFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            class Builder
            {
                public string Build(string[] parts)
                {
                    string htmlStr = "";
                    foreach (var part in parts)
                    {
                        // Semantically identical to htmlStr += part, but expressed
                        // as a simple assignment with a binary-add right-hand side.
                        htmlStr = htmlStr + part;
                    }
                    return htmlStr;
                }
            }
            """;
        SetSource(source);

        var result = await engine.DetectAntiPatternsAsync(
            "Test.cs", patternFilter: ["StringConcatInLoop"]);

        Assert.That(
            result.Where(f => f.Pattern == "StringConcatInLoop"),
            Is.Not.Empty,
            "BUG: 'htmlStr = htmlStr + part' inside a foreach loop is O(n²) string " +
            "concatenation and should be flagged as StringConcatInLoop. The current " +
            "detection only matches AddAssignmentExpression (+=), missing the " +
            "SimpleAssignment + BinaryAdd form. " +
            "Fix: add a second pass that inspects SimpleAssignment whose Right " +
            "is a BinaryAddExpression containing the same LHS identifier.");
    }

    // Control: the += form IS detected (regression guard)
    [Test]
    public async Task BUG5_Control_AntiPattern_PlusAssignInLoop_ShouldFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            class Builder
            {
                public string Build(string[] parts)
                {
                    string htmlStr = "";
                    foreach (var part in parts)
                        htmlStr += part;
                    return htmlStr;
                }
            }
            """;
        SetSource(source);

        var result = await engine.DetectAntiPatternsAsync(
            "Test.cs", patternFilter: ["StringConcatInLoop"]);

        Assert.That(
            result.Where(f => f.Pattern == "StringConcatInLoop"),
            Is.Not.Empty,
            "htmlStr += part inside foreach SHOULD be flagged as StringConcatInLoop (control).");
    }

    // Also confirm it works in a while loop (not just foreach)
    [Test]
    public async Task BUG5_Control_AntiPattern_PlusAssignInWhile_ShouldFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            class Builder
            {
                public string Build()
                {
                    string htmlStr = "";
                    int i = 0;
                    while (i++ < 10)
                        htmlStr += "x";
                    return htmlStr;
                }
            }
            """;
        SetSource(source);

        var result = await engine.DetectAntiPatternsAsync(
            "Test.cs", patternFilter: ["StringConcatInLoop"]);

        Assert.That(
            result.Where(f => f.Pattern == "StringConcatInLoop"),
            Is.Not.Empty,
            "htmlStr += 'x' inside while SHOULD be flagged (control).");
    }

    // ==========================================================================
    // BONUS BUG: AntiPatternEngine.FindMutablePublicPropertiesAsync
    //            does not analyse record types (RecordDeclarationSyntax).
    //            It only iterates  OfType<ClassDeclarationSyntax>(), so a
    //            mutable  record  with  { get; set; }  properties is silently
    //            skipped even though it has the same mutability risk.
    //
    // TYPE:     False negative
    // SEVERITY: Minor — records with mutable state are unusual but valid.
    // ==========================================================================

    [Test]
    public async Task BonusBug_FindMutablePublicProperties_RecordWithPublicSetter_ShouldFlag()
    {
        var engine = new AntiPatternEngine(_workspaceManager);
        const string source = """
            public record class MutableOrder
            {
                public string Item { get; set; } = "";
                public int Quantity { get; set; }
            }
            """;
        SetSource(source);

        var result = await engine.FindMutablePublicPropertiesAsync("Test.cs");

        Assert.That(
            result.Where(f => f.Pattern == "MutablePublicApi"),
            Is.Not.Empty,
            "BONUS BUG: a 'record class' with { get; set; } has the same mutability risk as " +
            "a regular class and should be flagged. The engine currently only iterates " +
            "ClassDeclarationSyntax, silently skipping RecordDeclarationSyntax nodes. " +
            "Fix: also iterate OfType<RecordDeclarationSyntax>() (or use TypeDeclarationSyntax).");
    }
}
