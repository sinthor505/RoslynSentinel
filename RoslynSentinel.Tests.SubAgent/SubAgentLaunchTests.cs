using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Basic;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.SubAgent;

/// <summary>
/// Run naming, argument validation, and the recursion guard on the child server's launch arguments.
/// Pure unit tests: nothing here spawns a process.
/// </summary>
[TestFixture]
public class SubAgentLaunchTests
{
    // ── Run naming ──────────────────────────────────────────────────────────────

    [Test]
    [Category("SubAgentRunNaming")] // sentinel:auto-category
    public void NewRunId_CalledManyTimesAtTheSameInstant_NeverCollides()
    {
        // The same millisecond on purpose: a live server can start several calls inside one tick, and
        // the timestamp alone would collide. Only the GUID suffix keeps them apart.
        var instant = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 5000).Select(_ => SubAgentRunNaming.NewRunId(instant)).ToList();

        Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
    }

    [Test]
    [Category("SubAgentRunNaming")] // sentinel:auto-category
    public void NewRunId_HasSortableTimestampPrefixAndShortSuffix()
    {
        var id = SubAgentRunNaming.NewRunId(new DateTimeOffset(2026, 10, 1, 13, 5, 9, 42, TimeSpan.Zero));

        Assert.That(id, Does.Match(@"^20261001-130509-042-[0-9a-f]{8}$"));
    }

    [Test]
    [Category("SubAgentRunNaming")] // sentinel:auto-category
    public void RunDirectory_IsASiblingOfTheRepo_NotInsideIt()
    {
        var repo = Path.Combine(Path.GetTempPath(), "somewhere", "RoslynSentinel");
        var runDirectory = SubAgentRunNaming.RunDirectory(repo, "run-1");

        Assert.Multiple(() =>
        {
            Assert.That(runDirectory, Does.Not.StartWith(repo + Path.DirectorySeparatorChar),
                "a run folder inside the repo would appear as untracked content in the live session's own git status");
            Assert.That(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runDirectory))),
                Is.EqualTo(Path.GetDirectoryName(repo)));
            Assert.That(Path.GetFileName(runDirectory), Is.EqualTo("run-1"));
        });
    }

    [Test]
    [Category("SubAgentRunNaming")] // sentinel:auto-category
    public void RunDirectory_ToleratesATrailingSeparator()
    {
        var repo = Path.Combine(Path.GetTempPath(), "somewhere", "RoslynSentinel") + Path.DirectorySeparatorChar;

        Assert.That(
            SubAgentRunNaming.RunDirectory(repo, "run-1"),
            Is.EqualTo(SubAgentRunNaming.RunDirectory(repo.TrimEnd(Path.DirectorySeparatorChar), "run-1")));
    }

    [Test]
    [Category("SubAgentRunNaming")] // sentinel:auto-category
    public void BranchName_IsNamespacedUnderSubagent()
    {
        Assert.That(SubAgentRunNaming.BranchName("run-1"), Is.EqualTo("subagent/run-1"));
    }

    // ── Validation ──────────────────────────────────────────────────────────────

    [Test]
    [Category("SubAgentModelRun")] // sentinel:auto-category
    public void Validate_AcceptsAWellFormedCall()
    {
        Assert.That(SubAgentModelRun.Validate("do a thing", "some-model", 4096, null, null), Is.Null);
    }

    [TestCase(null, "m", 1, "prompt")]
    [TestCase("  ", "m", 1, "prompt")]
    [TestCase("p", null, 1, "model")]
    [TestCase("p", "", 1, "model")]
    [TestCase("p", "m", 0, "maxTokensPerTurn")]
    [TestCase("p", "m", -5, "maxTokensPerTurn")]
    [Category("SubAgentModelRun")] // sentinel:auto-category
    public void Validate_RejectsBadArguments_NamingTheParameter(string? prompt, string? model, int maxTokens, string expectedParameter)
    {
        var error = SubAgentModelRun.Validate(prompt, model, maxTokens, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Not.Null);
            Assert.That(error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(error.Message, Does.Contain(expectedParameter));
        });
    }

    [Test]
    [Category("SubAgentModelRun")] // sentinel:auto-category
    public void Validate_RejectsNonPositiveCaps()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SubAgentModelRun.Validate("p", "m", 1, 0, null)?.Message, Does.Contain("turnCap"));
            Assert.That(SubAgentModelRun.Validate("p", "m", 1, null, 0)?.Message, Does.Contain("wallClockCapMinutes"));
        });
    }

    [Test]
    [Category("SubAgentImpl")] // sentinel:auto-category
    public void ApplyResponseFormat_JsonAddsTheInstruction_TextLeavesThePromptAlone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SubAgentImpl.ApplyResponseFormat("task", SubAgentResponseFormat.Text), Is.EqualTo("task"));
            Assert.That(SubAgentImpl.ApplyResponseFormat("task", SubAgentResponseFormat.Json),
                Is.EqualTo("task" + SubAgentImpl.JsonInstruction));
        });
    }

    // ── Recursion guard ─────────────────────────────────────────────────────────

    private static HashSet<string> ChildToolClasses(params string[] extraArgs)
    {
        // An extra --mode replaces the launcher's own (the parser takes the first match, so a plain
        // append would be ignored and the test would prove nothing).
        var overridesMode = extraArgs.Any(a => a.StartsWith("--mode=", StringComparison.Ordinal));
        var args = SubAgentChildServerLauncher
            .BuildServerArguments(@"C:\wt", "some-model", @"C:\logs", "run-1")
            .Where(a => !(overridesMode && a.StartsWith("--mode=", StringComparison.Ordinal)))
            .Concat(extraArgs)
            .ToArray();
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);
        ServerStartupHelpers.ParseArgs(
            args, allModes,
            out _, out var activeModes, out _, out _, out var includeTools, out var excludeTools, out _);

        return ServerStartupHelpers.ResolveActiveToolClasses(
            activeModes, ToolClassRegistry.AdvancedModeToToolClasses, includeTools, excludeTools);
    }

    [Test]
    public void ChildServerArguments_ExposeTheClaudeSurface_WithoutEitherSubAgentTool()
    {
        var active = ChildToolClasses();

        Assert.Multiple(() =>
        {
            Assert.That(active, Does.Contain("WorkspaceTools"), "the dispatched model needs Build/RunTest/LoadSolution");
            Assert.That(active, Does.Contain("WholeFileWriteTools"));
            Assert.That(active, Does.Not.Contain("SubAgentTools"));
            Assert.That(active, Does.Not.Contain("SubAgentEvalTools"));
        });
    }

    [TestCase("SubAgentTools")]
    [TestCase("SubAgentEvalTools")]
    [TestCase("SubAgentTools,SubAgentEvalTools")]
    [TestCase("SubAgent,SubAgentEval")]
    public void ChildServerArguments_ExcludeWinsEvenIfSomethingTriesToIncludeTheTool(string includeValue)
    {
        var active = ChildToolClasses("--include-tools=" + includeValue, "--mode=Claude,SubAgent,SubAgentEval");

        Assert.Multiple(() =>
        {
            Assert.That(active, Does.Not.Contain("SubAgentTools"));
            Assert.That(active, Does.Not.Contain("SubAgentEvalTools"));
        });
    }

    [Test]
    [Category("SubAgentChildServerLauncher")] // sentinel:auto-category
    public void ChildServerArguments_CarryTheModelAndRunIdentity()
    {
        var args = SubAgentChildServerLauncher.BuildServerArguments(@"C:\wt", "some-model", @"C:\logs", "run-1");

        Assert.Multiple(() =>
        {
            Assert.That(args, Does.Contain("--llm-model=some-model"));
            Assert.That(args, Does.Contain("--run-id=run-1"));
            Assert.That(args, Does.Contain("--base-repo-dir=" + @"C:\wt"));
            Assert.That(args, Does.Contain("--log-dir=" + @"C:\logs"));
            Assert.That(args, Does.Contain("--testing"));
            Assert.That(args, Has.None.StartsWith("--solution"), "auto-load is fire-and-forget; LoadSolution is called explicitly");
        });
    }

    // ── Reading a child tool result ─────────────────────────────────────────────

    private static CallToolResult Result(string text, bool? isError = null) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = isError };

    [Test]
    [Category("ChildToolResult")] // sentinel:auto-category
    public void ChildToolResult_SuccessEnvelope_IsSuccess()
    {
        Assert.That(ChildToolResult.IsSuccess(Result("""{"isError":false,"successData":"ok"}"""), out var failure), Is.True);
        Assert.That(failure, Is.Null);
    }

    [Test]
    [Category("ChildToolResult")] // sentinel:auto-category
    public void ChildToolResult_FailureEnvelope_IsFailure_EvenWithoutTheMcpErrorFlag()
    {
        // RoslynSentinel reports failure inside the JSON envelope; MCP-level IsError is not set.
        var ok = ChildToolResult.IsSuccess(Result("""{"isError":true,"errorData":{"errorCode":"NotFound","message":"nope"}}"""), out var failure);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(failure, Does.Contain("NotFound"));
        });
    }

    [Test]
    [Category("ChildToolResult")] // sentinel:auto-category
    public void ChildToolResult_McpErrorFlag_IsFailure_EvenForNonJsonText()
    {
        Assert.That(ChildToolResult.IsSuccess(Result("An error occurred invoking 'LoadSolution'.", isError: true), out _), Is.False);
    }
}
