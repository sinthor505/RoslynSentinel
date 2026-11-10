using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MoveMemberCompanionRefusalTests
{
    private static string Lines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private static string TargetPart(string text) => text[text.IndexOf("public static class Target", StringComparison.Ordinal)..];

    private static string SourcePart(string text) => text[..text.IndexOf("public static class Target", StringComparison.Ordinal)];

    private static string ChainFixture() => Lines(
        "public static class Source",
        "{",
        "    public static int Run() => A();",
        "    private static int A() => B();",
        "    private static int B() => 1;",
        "}",
        "",
        "public static class Target",
        "{",
        "}");

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task TwoLevelHelperChain_RefusalListsWholeChainInArray_AndWritesNothing()
    {
        var original = ChainFixture();
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Run"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("reference private/internal member(s) [A, B]"));
        Assert.That(result.ErrorData?.Message, Does.Contain("[\"A\",\"B\"]"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task RetryWithExactlyListedNames_Succeeds()
    {
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", ChainFixture()));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var path = workspace.PathOf("Refactor.cs");
        var refused = await tools.MoveMemberTool(reason: "test message", path, "Source", ["Run"], "Target");
        Assert.That(refused.IsError, Is.True);

        var result = await tools.MoveMemberTool(reason: "test message", path, "Source", ["Run", "A", "B"], "Target");
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);

        var text = workspace.ReadText("Refactor.cs");
        Assert.That(TargetPart(text), Does.Contain("int Run()"));
        Assert.That(TargetPart(text), Does.Contain("int A()"));
        Assert.That(TargetPart(text), Does.Contain("int B()"));
        Assert.That(SourcePart(text), Does.Not.Contain("int Run()"));
        Assert.That(SourcePart(text), Does.Not.Contain("int A()"));
        Assert.That(SourcePart(text), Does.Not.Contain("int B()"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task SharedHelper_IsNamedAsSharedAndKeptOutOfArray()
    {
        var original = Lines(
            "public static class Source",
            "{",
            "    public static int Run() => A();",
            "    private static int A() => B();",
            "    private static int B() => 1;",
            "    public static int Other() => B();",
            "}",
            "",
            "public static class Target",
            "{",
            "}");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Run"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("[\"A\"]"));
        Assert.That(result.ErrorData?.Message, Does.Not.Contain("\"B\""));
        Assert.That(result.ErrorData?.Message, Does.Contain("also used by members that stay in 'Source'"));
        Assert.That(result.ErrorData?.Message, Does.Contain("move the staying users too: [B]"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task OverloadedCompanion_PrintsSignatureForm()
    {
        var original = Lines(
            "public static class Source",
            "{",
            "    public static int Run() => Helper(1);",
            "    private static int Helper(int x) => x;",
            "    private static int Helper(string s) => 0;",
            "}",
            "",
            "public static class Target",
            "{",
            "}");
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Run"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("[\"Helper(int)\"]"));
        Assert.That(result.ErrorData?.Message, Does.Not.Contain("Helper(string)"));
    }
}
