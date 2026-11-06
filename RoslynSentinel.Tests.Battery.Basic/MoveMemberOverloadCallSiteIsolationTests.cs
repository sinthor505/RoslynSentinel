using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("MemberRefactoringEngine")]
public class MoveMemberOverloadCallSiteIsolationTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static (InMemoryWorkspace workspace, MemberRefactoringEngine engine) CreateFixture(params (string relativePath, string content)[] files)
    {
        var workspace = InMemoryWorkspace.Create(files);
        var diffEngine = TestEngines.DiffEngine();
        var validationEngine = TestEngines.ValidationEngine(workspace.Manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNav = TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var engine = TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, validationEngine);
        return (workspace, engine);
    }

    private static Task<MoveMemberResult> MoveAsync(
    InMemoryWorkspace workspace,
    MemberRefactoringEngine engine,
    string className,
    string[] memberNames,
    string targetClassName,
    MoveMemberOldMember oldMember = MoveMemberOldMember.remove,
    string? forwarderReceiver = null) =>
    engine.MoveMemberAsync(
        workspace.PathOf("Source.cs"),
        className,
        memberNames,
        targetClassName,
        null,
        CancellationToken.None,
        oldMember: oldMember,
        forwarderReceiver: forwarderReceiver);

    private static string TextOf(MoveMemberResult result, string fileName, string original)
    {
        var keys = result.Changes.Keys.Where(k => Path.GetFileName(k.ToString()) == fileName).ToList();
        return keys.Count == 0 ? original : result.Changes[keys[0]];
    }

    private static string StaticSource() => Lines(
    "namespace Example;",
    "",
    "public static class Source",
    "{",
    "    public static int Save(int a) => a;",
    "    public static int Save(string s) => s.Length;",
    "}");

    private static string StaticTarget() => Lines(
    "namespace Example;",
    "",
    "public static class Target",
    "{",
    "}");

    private static string StaticCaller(params string[] memberLines)
    {
        var lines = new List<string> { "namespace Example;", "", "public static class Caller", "{" };
        lines.AddRange(memberLines);
        lines.Add("}");
        return Lines(lines.ToArray());
    }

    private static string InstanceSource() => Lines(
    "namespace Example;",
    "",
    "public class Source",
    "{",
    "    public int Save(int a) => a;",
    "    public int Save(string s) => s.Length;",
    "}");

    private static string InstanceSourceWithImpl() => Lines(
    "namespace Example;",
    "",
    "public class Source",
    "{",
    "    private readonly Target _impl = new Target();",
    "",
    "    public int Save(int a) => a;",
    "    public int Save(string s) => s.Length;",
    "}");

    private static string InstanceTarget() => Lines(
    "namespace Example;",
    "",
    "public class Target",
    "{",
    "}");

    private static string InstanceCaller(params string[] memberLines)
    {
        var lines = new List<string>
    {
        "namespace Example;",
        "",
        "public class Caller",
        "{",
        "    private readonly Source a = new Source();",
        "    private readonly Target _target = new Target();",
        "",
    };
        lines.AddRange(memberLines);
        lines.Add("}");
        return Lines(lines.ToArray());
    }

    [Test]
    public async Task StaticRemove_SeparateLineCalls_OnlySaveIntCallIsRewritten()
    {
        var callerText = StaticCaller("    public static void Run()", "    {", "        Source.Save(1);", "        Source.Save(\"x\");", "    }");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", StaticSource()),
            ("Target.cs", StaticTarget()),
            ("Caller.cs", callerText));

        var result = await MoveAsync(workspace, engine, "Source", ["Save(int)"], "Target");

        Assert.That(TextOf(result, "Source.cs", StaticSource()), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public static class Source",
            "{",
            "    public static int Save(string s) => s.Length;",
            "}")));
        Assert.That(TextOf(result, "Target.cs", StaticTarget()), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public static class Target",
            "{",
            "    public static int Save(int a) => a;",
            "}")));
        Assert.That(TextOf(result, "Caller.cs", callerText), Is.EqualTo(StaticCaller(
            "    public static void Run()",
            "    {",
            "        Target.Save(1);",
            "        Source.Save(\"x\");",
            "    }")));
    }

    [Test]
    public async Task StaticKeepDelegator_SourceKeepsForwarderForSaveInt_CallSitesUnchanged()
    {
        var callerText = StaticCaller("    public static void Run()", "    {", "        Source.Save(1);", "        Source.Save(\"x\");", "    }");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", StaticSource()),
            ("Target.cs", StaticTarget()),
            ("Caller.cs", callerText));

        var result = await MoveAsync(workspace, engine, "Source", ["Save(int)"], "Target", MoveMemberOldMember.keepDelegator);

        Assert.That(TextOf(result, "Source.cs", StaticSource()), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public static class Source",
            "{",
            "    public static int Save(int a) => global::Example.Target.Save(a);",
            "    public static int Save(string s) => s.Length;",
            "}")));
        Assert.That(TextOf(result, "Target.cs", StaticTarget()), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public static class Target",
            "{",
            "    public static int Save(int a) => a;",
            "}")));
        Assert.That(TextOf(result, "Caller.cs", callerText), Is.EqualTo(callerText), "Keep modes do not rewrite callers.");
    }

    [Test]
    public async Task InstanceRemove_SeparateAndSameLineCalls_OnlySaveIntReceiversAreRewritten()
    {
        var callerText = InstanceCaller(
            "    public void RunSeparate()",
            "    {",
            "        a.Save(1);",
            "        a.Save(\"x\");",
            "    }",
            "",
            "    public void RunSameLine()",
            "    {",
            "        a.Save(1); a.Save(\"x\");",
            "    }");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", InstanceSource()),
            ("Target.cs", InstanceTarget()),
            ("Caller.cs", callerText));

        var result = await MoveAsync(workspace, engine, "Source", ["Save(int)"], "Target");

        Assert.That(TextOf(result, "Source.cs", InstanceSource()), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public class Source",
            "{",
            "    public int Save(string s) => s.Length;",
            "}")));
        Assert.That(TextOf(result, "Target.cs", InstanceTarget()), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public class Target",
            "{",
            "    public int Save(int a) => a;",
            "}")));
        Assert.That(TextOf(result, "Caller.cs", callerText), Is.EqualTo(InstanceCaller(
            "    public void RunSeparate()",
            "    {",
            "        _target.Save(1);",
            "        a.Save(\"x\");",
            "    }",
            "",
            "    public void RunSameLine()",
            "    {",
            "        _target.Save(1); a.Save(\"x\");",
            "    }")));
    }

    [Test]
    public async Task InstanceRemove_SameExpressionCalls_OnlySaveIntReceiverIsRewritten()
    {
        var callerText = InstanceCaller(
            "    public int Sum() => a.Save(1) + a.Save(\"x\");");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", InstanceSource()),
            ("Target.cs", InstanceTarget()),
            ("Caller.cs", callerText));

        var result = await MoveAsync(workspace, engine, "Source", ["Save(int)"], "Target");

        Assert.That(TextOf(result, "Caller.cs", callerText), Is.EqualTo(InstanceCaller(
            "    public int Sum() => _target.Save(1) + a.Save(\"x\");")));
    }

    [Test]
    public async Task InstanceKeepDelegator_SourceKeepsForwarderForSaveInt_CallSitesUnchanged()
    {
        var callerText = InstanceCaller(
            "    public void RunSeparate()",
            "    {",
            "        a.Save(1);",
            "        a.Save(\"x\");",
            "    }");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", InstanceSourceWithImpl()),
            ("Target.cs", InstanceTarget()),
            ("Caller.cs", callerText));

        var result = await MoveAsync(workspace, engine, "Source", ["Save(int)"], "Target", MoveMemberOldMember.keepDelegator, "_impl");

        Assert.That(TextOf(result, "Source.cs", InstanceSourceWithImpl()), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public class Source",
            "{",
            "    private readonly Target _impl = new Target();",
            "",
            "    public int Save(int a) => this._impl.Save(a);",
            "    public int Save(string s) => s.Length;",
            "}")));
        Assert.That(TextOf(result, "Caller.cs", callerText), Is.EqualTo(callerText), "Keep modes do not rewrite callers.");
    }

    [Test]
    public async Task SaveIntCallingStayingPrivateSaveString_IsRefusedAndWritesNothing()
    {
        var sourceText = Lines(
            "namespace Example;",
            "",
            "public static class Source",
            "{",
            "    public static int Save(int a) => Save(\"x\") + a;",
            "    private static int Save(string s) => s.Length;",
            "}");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", sourceText),
            ("Target.cs", StaticTarget()));

        var refusal = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => MoveAsync(workspace, engine, "Source", ["Save(int)"], "Target"));

        Assert.That(refusal!.Message, Does.Contain("reference private/internal member(s) [Save]"));
        Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(sourceText), "A refused move must not write.");
        Assert.That(workspace.ReadText("Target.cs"), Is.EqualTo(StaticTarget()), "A refused move must not write.");
    }

    [Test]
    public async Task StaticRemove_BothSignaturesListed_MovesBothAndRewritesBothCalls()
    {
        var callerText = StaticCaller("    public static void Run()", "    {", "        Source.Save(1);", "        Source.Save(\"x\");", "    }");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", StaticSource()),
            ("Target.cs", StaticTarget()),
            ("Caller.cs", callerText));

        var result = await MoveAsync(workspace, engine, "Source", ["Save(int)", "Save(string)"], "Target");

        Assert.That(TextOf(result, "Target.cs", StaticTarget()), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public static class Target",
            "{",
            "    public static int Save(int a) => a;",
            "",
        "    public static int Save(string s) => s.Length;",
            "}")));
        Assert.That(TextOf(result, "Caller.cs", callerText), Is.EqualTo(StaticCaller(
            "    public static void Run()",
            "    {",
            "        Target.Save(1);",
            "        Target.Save(\"x\");",
            "    }")));
    }

    [Test]
    public async Task StaticRemove_SameClassCallers_OnlySaveIntCallIsRewritten()
    {
        var sourceText = Lines(
            "namespace Example;",
            "",
            "public static class Source",
            "{",
            "    public static int Save(int a) => a;",
            "    public static int Save(string s) => s.Length;",
            "    public static int Run() => Save(1) + Save(\"x\");",
            "}");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", sourceText),
            ("Target.cs", StaticTarget()));

        var result = await MoveAsync(workspace, engine, "Source", ["Save(int)"], "Target");

        Assert.That(TextOf(result, "Source.cs", sourceText), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public static class Source",
            "{",
            "    public static int Save(string s) => s.Length;",
            "    public static int Run() => Target.Save(1) + Save(\"x\");",
            "}")));
        Assert.That(TextOf(result, "Target.cs", StaticTarget()), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public static class Target",
            "{",
            "    public static int Save(int a) => a;",
            "}")));
    }

    [Test]
    public async Task StaticRemove_SameLineCalls_OnlySaveIntCallIsRewritten()
    {
        var callerText = StaticCaller("    public static void Run()", "    {", "        Source.Save(1); Source.Save(\"x\");", "    }");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", StaticSource()),
            ("Target.cs", StaticTarget()),
            ("Caller.cs", callerText));

        var result = await MoveAsync(workspace, engine, "Source", ["Save(int)"], "Target");

        Assert.That(TextOf(result, "Caller.cs", callerText), Is.EqualTo(StaticCaller(
            "    public static void Run()",
            "    {",
            "        Target.Save(1); Source.Save(\"x\");",
            "    }")));
    }
}
