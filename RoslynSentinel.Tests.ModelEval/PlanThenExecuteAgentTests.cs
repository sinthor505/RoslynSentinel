using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// Same fixture, prompt ambiguity, and disambiguating sentence as
/// <see cref="WholeFileRewriteAgentTests"/>'s MinimalGuidanceDisambiguated test, but additionally
/// instructs the model to write out its complete plan in prose, with no tool calls, before making its
/// first tool call -> tests the "on-the-fly decisions fork 50/50, a committed-up-front plan anchors the
/// model" hypothesis raised alongside project_minimalguidance_reasoning_pattern_analysis. Nothing
/// enforces the instruction server-side (a request filter can't see whether the model's prior turn
/// was prose-only, only individual tool calls in isolation -> see PlanOnlyAgentTests's doc comment for
/// why a true two-phase plan-then-execute would need a second full model round-trip instead) -> this is
/// a prompt-only nudge, and <see cref="AssertFixApplied"/> additionally records whether the model
/// actually complied (turn 1 had zero tool calls) so compliant vs. non-compliant runs can be compared
/// post-hoc, the same technique used for the reasoning-pattern analysis this test follows up on.
/// </summary>
// TODO duplicated [TestFixture] attribute (pre-existing): drop one, checking first that the Explicit test is not discovered twice.
[TestFixture]
[TestFixture]
public class PlanThenExecuteAgentTests
{
    // Identical task/ambiguity-closing text to WholeFileRewriteAgentTests's
    // DisambiguatedMinimalGuidanceUserPromptTemplate, with one added instruction: state the full plan
    // before touching any tool. Keeping the rest of the prompt byte-for-byte identical to that test
    // isolates the "plan first" instruction as the only variable between the two.
    private const string PlanThenExecuteUserPromptTemplate = """
        # Task: Fix a bug in FixtureHelpers/BlockConverter.cs

        Users report that editing shapes via `{0}/FixtureHelpers/BlockConverter.cs` sometimes
        changes unrelated formatting elsewhere in the same file, even though they only asked for
        one class to be converted.

        Investigate `BlockConverter.cs`, find the root cause, and fix it. A similar bug was
        already fixed elsewhere in this codebase using a reusable pattern - look for it and reuse
        that same approach rather than inventing a new one.

        If the existing fix lives in a private method in another file, call it directly rather
        than copying its body into your own fix - raise its accessibility (e.g. to `internal`) so
        it can be called cross-file, but don't duplicate its logic, and don't modify anything else
        in that file. Once you switch the buggy method's call site to the shared method, delete
        only that one now-unused old method (the one the bug report is about) instead of leaving
        it behind - do not delete, rename, or otherwise modify any other method, field, or class,
        even ones that look unused, unrelated, or like dead code to you.

        Before making any tool call that edits a file, first write out your complete plan as plain
        text: the root cause, exactly which method(s)/file(s) you will modify, and the specific
        content you will place in `BlockConverter.cs`. Only after stating that plan in full should
        you begin making edit tool calls - do not interleave planning and editing.

        Verify your fix compiles, using an MCP tool (you have no terminal access). Scope the build
        to just the `ContosoOrders.Core` project rather than the whole solution.

        Do not modify any code unrelated to this specific bug, including code that looks unused —
        leave it exactly as you found it. Report what you changed and the verification result.
        """;

    // Tool surface: ModelEvalToolSurfaces.Basic (the "Refactor" and "Workspace" modes), same as
    // WholeFileRewriteAgentTests -> nothing blocked here; only the prompt differs from that test's
    // MinimalGuidanceDisambiguated variant.
    private ModelEvalMcpEnvironment? _environment;

    // Aliases of _environment's members, assigned in SetUp so the test bodies keep their field names; _environment owns disposal.
    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;
    private LmStudioAgentClient _agentClient = null!;
    private string _runDirectory = null!;

    [SetUp]
    public async Task SetUp()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        _environment = await ModelEvalMcpEnvironment.CreateAsync(
            _runDirectory,
            restorePackages: false,
            beforeLoad: null,
            afterLoad: StageFilesAsync,
            TestContext.CurrentContext.CancellationToken,
            configureToolSurface: ModelEvalToolSurfaces.Basic);
        _host = _environment.Host;
        _mcpClient = _environment.Client;
        _fixture = _environment.Fixture;
        _agentClient = _environment.AgentClient;
    }

    private static async Task StageFilesAsync(
        RoslynSentinel.Tests.TestSolutionFixture fixture,
        IWorkspaceManager workspaceManager,
        CancellationToken cancellationToken)
    {
        await fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs"),
            WholeFileRewriteReproducer.HelperFileContent,
            reloadSolution: false,
            cancellationToken: cancellationToken);
        await fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs"),
            WholeFileRewriteReproducer.BuggyFileContent,
            reloadSolution: false,
            cancellationToken: cancellationToken);
        await fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "Shape.cs"),
            WholeFileRewriteReproducer.TargetAbstractClassFileContent,
            reloadSolution: true,
            cancellationToken: cancellationToken);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_environment is not null)
        {
            await _environment.DisposeAsync();
            _environment = null;
        }

        if (_runDirectory is not null)
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(_runDirectory);
        }
    }

    [Test]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1")]
    public async Task Model_FixesWholeFileRewriteBug_PlanThenExecute()
    {
        var runner = new ModelAgentRunner(
            _agentClient, _mcpClient, repeatedFailureLimit: 10, maxTokensPerTurn: 16384, turnCap: 40, wallClockCap: TimeSpan.FromMinutes(30),
            logger: _host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var userPrompt = string.Format(PlanThenExecuteUserPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        var result = await runner.RunAsync(ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent), userPrompt, _runDirectory, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        // Not a pass/fail gate -> recorded so compliant vs. non-compliant runs can be compared
        // post-hoc against AssertFixApplied's outcome (same technique as
        // project_minimalguidance_reasoning_pattern_analysis), since nothing server-side stops the
        // model from ignoring the "plan before editing" instruction.
        var firstTurn = result.Transcript.Turns.FirstOrDefault();
        var compliedWithPlanFirst = firstTurn is not null && firstTurn.ToolCalls.Count == 0;
        TestContext.Out.WriteLine(compliedWithPlanFirst
            ? "PLAN-FIRST: model's turn 1 had zero tool calls (stated a plan before acting)."
            : "NO PLAN-FIRST: model made a tool call on turn 1 (interleaved planning and editing, or skipped planning).");

        AssertFixApplied(result);
    }

    private void AssertFixApplied(AgentRunResult result)
    {
        var fixedPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs");
        Assert.That(File.Exists(fixedPath), Is.True, "BlockConverter.cs should still exist after the model's edits.");

        var fixedText = File.ReadAllText(fixedPath);

        Assert.That(fixedText, Does.Not.Contain("return ReformatWholeFile("),
            $"ConvertAbstractClassToInterface should no longer call ReformatWholeFile. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Not.Match(@"static\s+string\s+ReformatWholeFile\s*\("),
            $"The old ReformatWholeFile method should be deleted once its call site is replaced, " +
            $"not left behind unused. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("ReplaceBlockFormatted"),
            $"The fix should call ReplaceBlockFormatted (directly or qualified) - bringing it into " +
            $"scope, not duplicating its body. Transcript: {result.TranscriptPath}");

        // See WholeFileRewriteAgentTests.AssertFixApplied for the full rationale: the real-world
        // precedent (commit 8a8963d) was consolidating 52 duplicate call sites onto one shared
        // helper, so exposing and calling ReplaceBlockFormatted is the correct fix here, not
        // duplicating it.
        var helperPath = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs");
        var helperText = File.ReadAllText(helperPath);
        Assert.That(helperText, Does.Not.Contain("private static string ReplaceBlockFormatted"),
            $"ReplaceBlockFormatted should have its accessibility raised (internal/public) so " +
            $"BlockConverter.cs can call it directly instead of duplicating it. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Not.Match(@"(?:private\s+)?static\s+string\s+ReplaceBlockFormatted\s*\("),
            $"BlockConverter.cs should call the shared ReplaceBlockFormatted, not define its own " +
            $"copy of it. Transcript: {result.TranscriptPath}");

        // Trivia-insensitive: the tool pipeline may legitimately normalize incidental whitespace
        // (see AgentSystemPrompts.CodingAgent's note on this) -> what matters is that the model
        // didn't change either method's LOGIC.
        UnrelatedCodeEquivalenceAssert.AssertMemberUnchanged(fixedPath, "UnrelatedMethodBefore",
            "public string UnrelatedMethodBefore( int    x , int y )\n{\n        return (x+y).ToString();\n}");
        UnrelatedCodeEquivalenceAssert.AssertMemberUnchanged(fixedPath, "UnrelatedMethodAfter",
            "public string UnrelatedMethodAfter(  string   s  )\n{\n        return s?.Trim() ?? \"\";\n}");

        // Threshold is 2, not 1: CompilerErrorLookupHelper's guidance (see RoslynSentinel.Engines.Basic
        // CompilerErrorLookupHelper.cs) is designed to be *read after* a failed ApplyDiffTool, so a
        // model that mis-qualifies a call (e.g. CS0103 on a static member of another class) and
        // then correctly follows the guidance on retry legitimately costs 2 error tool calls, not
        // 1 - observed directly in a 2026-08-31 PlanThenExecute run that fully fixed the bug in 2
        // ApplyDiffTool attempts but tripped a <=1 gate. 3+ on the same tool is real thrashing, not a
        // single guided recovery -> see AgentToolErrorAssertions for why the cap is per-tool, not
        // just total.
        //
        // Total cap raised 2 -> 8 (2026-09-02), matching WholeFileRewriteAgentTests.AssertFixApplied
        // -> see docs/current/project_modifymodifier_accessibility_footgun.md: 9/45 (20%) of this
        // variant's runs hit the same cross-tool self-correction false negative (a real error on one
        // tool, a guided retry on a DIFFERENT tool, then any third unrelated benign hiccup tripping
        // a tight total cap even though no single tool ever thrashed).
        // Per-tool cap raised 2 -> 4 (2026-09-04), matching AssertFixApplied -> a legitimate
        // multi-step diagnosis (wrong fix -> compiler error -> adjusted fix -> still wrong ->
        // correct fix) can land 3 failed calls on ONE tool without being thrashing.
        AgentToolErrorAssertions.AssertWithinBudget(result, maxTotal: 8, maxPerTool: 4);
    }
}
