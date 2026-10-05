using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// ModifyModifier(add static) and MoveMember into a static class: the member becomes static and every
/// instance-qualified caller is rewritten to the type name, with ONLY the receiver spans (and the inserted
/// 'static' keyword) changing. See docs/current/blockers/blocking_error_no_tool_converts_instance_member_to_static_with_caller_rewrite.md.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")] // sentinel:auto-category
[Category("StaticConversionResult")] // sentinel:auto-category
public class MakeStaticRewritesCallersTests
{
    private static string[] HelperLines() =>
    [
        "namespace Example;",
        "",
        "public class Helper",
        "{",
        "    public string Normalize(string input) => input.Trim().ToLower();",
        "",
        "    public string Describe(string input) => Normalize(input) + \"!\";",
        "",
        "    public int Length(string s) => s.Length;",
        "}"
    ];

    private static string[] ConsumerLines(params string[] bodyLines) =>
    [
        "namespace Example;",
        "",
        "public class Consumer",
        "{",
        "    private readonly Helper _helper;",
        "",
        "    public Consumer(Helper helper)",
        "    {",
        "        _helper = helper;",
        "    }",
        "",
        .. bodyLines,
        "}"
    ];

    [TestCase("\n")]
    [TestCase("\r\n")]
    public async Task AddStatic_RewritesInjectedFieldCallersInAnotherFile_OnlyReceiverSpansChange(string eol)
    {
        var helper = Lines(eol, HelperLines());
        var consumer = Lines(eol, ConsumerLines(
            "    public string Run(string x)",
            "    {",
            "        return _helper.Normalize(x);",
            "    }"));
        var (workspace, engine) = CreateInMemoryTestFixture(("Helper.cs", helper), ("Consumer.cs", consumer));

        var result = await engine.ConvertMembersToStaticAsync([Request(workspace, 0, "Helper.cs", "Normalize")], CancellationToken.None);

        Assert.That(result.HandledIndexes, Is.EquivalentTo(new[] { 0 }));
        Assert.That(result.Changes.Count, Is.EqualTo(2), "Helper (modifier) and Consumer (receiver)");
        var helperNew = TextOf(result, "Helper.cs");
        var consumerNew = TextOf(result, "Consumer.cs");
        AssertOnlySpansChanged(helper, helperNew, new ContentSpan("public string Normalize", "public static string Normalize"));
        AssertOnlySpansChanged(consumer, consumerNew, new ContentSpan("_helper.Normalize(x)", "Helper.Normalize(x)"));
        Assert.That(helperNew, Does.Contain("Normalize(input) + \"!\""), "The unqualified call inside the type needs no change");
    }

    [Test]
    public async Task AddStatic_ReportsReceiverFieldThatIsNoLongerRead()
    {
        const string eol = "\n";
        var consumer = Lines(eol, ConsumerLines("    public string Run(string x) => _helper.Normalize(x);"));
        var (workspace, engine) = CreateInMemoryTestFixture(("Helper.cs", Lines(eol, HelperLines())), ("Consumer.cs", consumer));

        var result = await engine.ConvertMembersToStaticAsync([Request(workspace, 0, "Helper.cs", "Normalize")], CancellationToken.None);

        Assert.That(result.Notes, Has.Some.Contain("_helper").And.Contain("no longer read"));
        Assert.That(TextOf(result, "Consumer.cs"), Does.Contain("private readonly Helper _helper;"), "The unused field is reported, never deleted");
    }

    [Test]
    public async Task AddStatic_DoesNotReportReceiverFieldThatIsStillRead()
    {
        const string eol = "\n";
        var consumer = Lines(eol, ConsumerLines(
            "    public string Run(string x) => _helper.Normalize(x);",
            "",
            "    public int Size(string x) => _helper.Length(x);"));
        var (workspace, engine) = CreateInMemoryTestFixture(("Helper.cs", Lines(eol, HelperLines())), ("Consumer.cs", consumer));

        var result = await engine.ConvertMembersToStaticAsync([Request(workspace, 0, "Helper.cs", "Normalize")], CancellationToken.None);

        Assert.That(result.Notes, Has.None.Contain("no longer read"));
    }

    [Test]
    public void AddStatic_RefusesMemberUsingInstanceField_NamingTheField()
    {
        const string eol = "\n";
        var source = Lines(eol, "namespace Example;", "", "public class Counter", "{", "    private int _count;", "", "    public int Next() => _count + 1;", "}");
        var (workspace, engine) = CreateInMemoryTestFixture(("Counter.cs", source));

        var ex = Assert.ThrowsAsync<ToolTargetIneligibleException>(() => engine.ConvertMembersToStaticAsync([Request(workspace, 0, "Counter.cs", "Next")], CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("instance field '_count'").And.Contain("Counter.cs:7"));
    }

    [Test]
    public void AddStatic_RefusesMemberCallingAnotherInstanceMemberThroughImplicitThis()
    {
        const string eol = "\n";
        var (workspace, engine) = CreateInMemoryTestFixture(("Helper.cs", Lines(eol, HelperLines())));

        var ex = Assert.ThrowsAsync<ToolTargetIneligibleException>(() => engine.ConvertMembersToStaticAsync([Request(workspace, 0, "Helper.cs", "Describe")], CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("instance method (implicit this) 'Normalize'").And.Contain("Helper.cs:7"));
    }

    [Test]
    public async Task AddStatic_BatchOfTwo_WhereOneCallsTheOther_ConvertsBothAndRewritesBothCallers()
    {
        const string eol = "\n";
        var helper = Lines(eol, HelperLines());
        var consumer = Lines(eol, ConsumerLines(
            "    public string Run(string x) => _helper.Normalize(x) + _helper.Describe(x);"));
        var (workspace, engine) = CreateInMemoryTestFixture(("Helper.cs", helper), ("Consumer.cs", consumer));

        var result = await engine.ConvertMembersToStaticAsync([Request(workspace, 0, "Helper.cs", "Normalize"), Request(workspace, 1, "Helper.cs", "Describe")], CancellationToken.None);

        Assert.That(result.HandledIndexes, Is.EquivalentTo(new[] { 0, 1 }));
        AssertOnlySpansChanged(helper, TextOf(result, "Helper.cs"),
            new ContentSpan("public string Normalize", "public static string Normalize"),
            new ContentSpan("public string Describe", "public static string Describe"));
        AssertOnlySpansChanged(consumer, TextOf(result, "Consumer.cs"),
            new ContentSpan("_helper.Normalize(x)", "Helper.Normalize(x)"),
            new ContentSpan("_helper.Describe(x)", "Helper.Describe(x)"));
    }

    [Test]
    public async Task AddStatic_NullConditionalOnSimpleField_BecomesPlainStaticCall()
    {
        const string eol = "\n";
        var consumer = Lines(eol, ConsumerLines("    public string? Run(string x) => _helper?.Normalize(x);"));
        var (workspace, engine) = CreateInMemoryTestFixture(("Helper.cs", Lines(eol, HelperLines())), ("Consumer.cs", consumer));

        var result = await engine.ConvertMembersToStaticAsync([Request(workspace, 0, "Helper.cs", "Normalize")], CancellationToken.None);

        AssertOnlySpansChanged(consumer, TextOf(result, "Consumer.cs"), new ContentSpan("_helper?.Normalize(x)", "Helper.Normalize(x)"));
        Assert.That(result.Notes, Has.Some.Contain("null-conditional call"));
    }

    [Test]
    public void AddStatic_NullConditionalOnComplexReceiver_IsRefused()
    {
        const string eol = "\n";
        var consumer = Lines(eol, ConsumerLines(
            "    private Helper GetHelper() => _helper;",
            "",
            "    public string? Run(string x) => GetHelper()?.Normalize(x);"));
        var (workspace, engine) = CreateInMemoryTestFixture(("Helper.cs", Lines(eol, HelperLines())), ("Consumer.cs", consumer));

        var ex = Assert.ThrowsAsync<ToolTargetIneligibleException>(() => engine.ConvertMembersToStaticAsync([Request(workspace, 0, "Helper.cs", "Normalize")], CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("null-conditional receiver").And.Contain("Consumer.cs:"));
    }

    [Test]
    public async Task AddStatic_NotAMethodOrAlreadyStatic_IsLeftToThePlainModifierPath()
    {
        const string eol = "\n";
        var source = Lines(eol, "namespace Example;", "", "public class Box", "{", "    public int Value;", "", "    public static int Twice(int n) => n * 2;", "}");
        var (workspace, engine) = CreateInMemoryTestFixture(("Box.cs", source));

        var result = await engine.ConvertMembersToStaticAsync([Request(workspace, 0, "Box.cs", "Value"), Request(workspace, 1, "Box.cs", "Twice")], CancellationToken.None);

        Assert.That(result.HandledIndexes, Is.Empty);
        Assert.That(result.Changes, Is.Empty);
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    [Category("MoveMemberResult")] // sentinel:auto-category
    public async Task MoveInstanceMemberIntoStaticClass_MakesItStaticAndRewritesCaller(string eol)
    {
        var source = Lines(eol, "namespace Example;", "", "public class Helper", "{", "    public int Length(string s) => s.Length;", "", "    public string Normalize(string input) => input.Trim().ToLower();", "}");
        var target = Lines(eol, "namespace Example;", "", "public static class Utils", "{", "    public static int Count() => 0;", "}");
        var caller = Lines(eol, ConsumerLines(
            "    public string Run(string x)",
            "    {",
            "        return _helper.Normalize(x);",
            "    }"));
        var (workspace, engine) = CreateInMemoryTestFixture(("Source.cs", source), ("Target.cs", target), ("Caller.cs", caller));

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "Helper", ["Normalize"], "Utils", workspace.PathOf("Target.cs"), CancellationToken.None);

        Assert.That(result.SkippedCallSites, Is.Empty);
        Assert.That(result.Changes.Count, Is.EqualTo(3));
        var moved = "    public static string Normalize(string input) => input.Trim().ToLower();" + eol;
        AssertOnlySpansChanged(source, result.Changes[KeyOf(result.Changes, "Source.cs")], new ContentSpan(eol + "    public string Normalize(string input) => input.Trim().ToLower();" + eol, string.Empty));
        AssertOnlySpansChanged(target, result.Changes[KeyOf(result.Changes, "Target.cs")], new ContentSpan("    public static int Count() => 0;" + eol, "    public static int Count() => 0;" + eol + eol + moved));
        AssertOnlySpansChanged(caller, result.Changes[KeyOf(result.Changes, "Caller.cs")], new ContentSpan("_helper.Normalize(x)", "Utils.Normalize(x)"));
        Assert.That(result.Notes, Has.Some.Contain("_helper").And.Contain("no longer read"));
    }

    [Test]
    public void MoveInstanceMemberIntoStaticClass_UsingInstanceState_IsRefusedNamingTheState()
    {
        const string eol = "\n";
        var source = Lines(eol, "namespace Example;", "", "public class Counter", "{", "    private int _count;", "", "    public int Next() => _count + 1;", "}");
        var target = Lines(eol, "namespace Example;", "", "public static class Utils", "{", "    public static int Count() => 0;", "}");
        var (workspace, engine) = CreateInMemoryTestFixture(("Source.cs", source), ("Target.cs", target));

        var ex = Assert.ThrowsAsync<ToolTargetIneligibleException>(() => engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "Counter", ["Next"], "Utils", workspace.PathOf("Target.cs"), CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("static class 'Utils'").And.Contain("instance field '_count'"));
    }

    private static StaticConversionRequest Request(InMemoryWorkspace workspace, int index, string file, string target) => new(index, workspace.PathOf(file), target, null, null, null);

    private static FilePathWrapper KeyOf(Dictionary<FilePathWrapper, string> changes, string fileName) => changes.Keys.Single(k => Path.GetFileName(k.ToString()) == fileName);

    private static string TextOf(StaticConversionResult result, string fileName) => result.Changes[KeyOf(result.Changes, fileName)];

    /// <summary>Joins lines with <paramref name="eol"/> and terminates the last line with it too.</summary>
    private static string Lines(string eol, params string[] lines) => string.Join(eol, lines) + eol;

    /// <summary>Strips the allowed edits from both texts and asserts the remainder is byte-identical.</summary>
    private static void AssertOnlySpansChanged(string before, string after, params ContentSpan[] allowedEdits)
    {
        var workingBefore = before;
        var workingAfter = after;
        foreach (var edit in allowedEdits)
        {
            var beforeIdx = workingBefore.IndexOf(edit.OldContent, StringComparison.Ordinal);
            Assert.That(beforeIdx, Is.GreaterThanOrEqualTo(0), $"Expected span not found in the original text: {edit.OldContent}");
            var afterIdx = workingAfter.IndexOf(edit.NewContent, StringComparison.Ordinal);
            Assert.That(afterIdx, Is.GreaterThanOrEqualTo(0), $"Expected replacement not found in the new text: {edit.NewContent}");
            workingBefore = workingBefore.Remove(beforeIdx, edit.OldContent.Length);
            workingAfter = workingAfter.Remove(afterIdx, edit.NewContent.Length);
        }

        Assert.That(workingAfter, Is.EqualTo(workingBefore), "Everything outside the allowed edits must be byte-identical");
    }

    private static (InMemoryWorkspace workspace, MemberRefactoringEngine engine) CreateInMemoryTestFixture(params (string relativePath, string content)[] files)
    {
        var workspace = InMemoryWorkspace.Create(files);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspace.Manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNav = new SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        return (workspace, new MemberRefactoringEngine(workspace.Manager, symbolNav, validationEngine));
    }

    private readonly record struct ContentSpan(string OldContent, string NewContent);
}
