using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Tests.ModelEval.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Drives a real LM Studio model through a real in-process MCP server (same harness construction as
/// RoslynSentinel.Tests.Advanced's McpTasksHarness* tests) against the
/// <see cref="WholeFileRewriteReproducer"/> fixture, replacing the manual copy/paste-into-LM-Studio
/// workflow previously used for plan-9b-model-test-step2.md. Requires an LM Studio server reachable
/// at ROSLYNSENTINEL_LLM_BASE_URL (default http://localhost:1234/v1) with ROSLYNSENTINEL_LLM_MODEL
/// set to a loaded, tool-calling-capable model — tests are skipped (not failed) if LM Studio isn't
/// reachable, since this project exercises a real external model, not a mocked one.
/// </summary>
[TestFixture]
public class WholeFileRewriteAgentTests
{
    private const string UserPromptTemplate = """
        # Task: Fix a whole-file-rewrite bug in FixtureHelpers/BlockConverter.cs (level 2)

        This gives you less detail than a fully-scripted plan. You are told *what* to do and
        *which tool* to use, but not the exact parameters or exact code — work those out yourself
        from what the tools return. If a tool call fails, stop and report the exact error message
        rather than guessing around it.

        ## Background

        `{0}/FixtureHelpers/BlockConverter.cs` has a bug: `ConvertAbstractClassToInterface` builds
        its replacement text and then calls a private `ReformatWholeFile` helper that rewrites the
        ENTIRE file's text — not just the part that changed. This silently reformats unrelated code
        in the same file.

        This exact bug was already fixed, using the same fix pattern, in the sibling file
        `{0}/FixtureHelpers/BlockEditHelpers.cs` — but the helper there is private, so
        `BlockConverter.cs` can't call it directly yet. Your job is to apply that same fix pattern
        to `ConvertAbstractClassToInterface` in `BlockConverter.cs` by reusing the existing helper,
        not by writing a second copy of it.

        ## Steps

        1. Read `ConvertAbstractClassToInterface` in `{0}/FixtureHelpers/BlockConverter.cs`.
           Identify the exact call responsible for the whole-file-rewrite bug described above.

        2. Find the existing fix pattern already used elsewhere in this codebase for this same bug.
           Look at `{0}/FixtureHelpers/BlockEditHelpers.cs` — there is a private helper method
           there that solves exactly this problem by rewriting only the changed block instead of
           the whole file. Locate it and read its full source.

        3. Apply the same fix to `ConvertAbstractClassToInterface`:
           - Raise `ReplaceBlockFormatted`'s accessibility in `BlockEditHelpers.cs` (e.g. to
             `internal`) so `BlockConverter.cs` can call it directly — don't copy its body.
           - Add whatever `using` directive the call site needs to compile.
           - Update `ConvertAbstractClassToInterface` so it calls the shared helper instead of
             `ReformatWholeFile`, producing the same edit (still renaming
             `public abstract class {{className}}` to `public interface I{{className}}`), but
             formatting only that change.

        4. Verify your change compiles, using an MCP tool (you have no terminal). Scope the build
           to just the `ContosoOrders.Core` project rather than the whole solution.

        5. Confirm the fix: re-read `ConvertAbstractClassToInterface` and check that the
           whole-file-rewrite call is gone and the shared helper is being called instead.

        6. Report what you changed and the verification result.

        ## Constraints

        - Don't modify `UnrelatedMethodBefore` or `UnrelatedMethodAfter` in the file — they are
          explicitly out of scope for this task.
        - Don't invent a new helper method or a different fix approach, and don't duplicate
          `ReplaceBlockFormatted`'s body into `BlockConverter.cs` — call the existing method as-is;
          don't rename it or change its behavior.
        - Don't change anything in `BlockEditHelpers.cs` other than `ReplaceBlockFormatted`'s
          accessibility modifier.
        - Preserve the original method's behavior (same inputs/outputs) — only the rewrite
          mechanism should change.
        """;

    // Level 3: no method/file names, no fix mechanism, no step list — just the observable symptom.
    // The model has to locate the bug by reading BlockConverter.cs, discover BlockEditHelpers.cs's
    // existing fix pattern itself (e.g. via SearchSolutionText/ListSolutionItems), and decide how to
    // reuse it. Reuses the same fixture and AssertFixApplied as the level-2 test above — only the
    // prompt differs, so a pass/fail delta between the two tests isolates how much the scripted
    // guidance in the level-2 prompt was doing versus the model's own reasoning.
    private const string MinimalGuidanceUserPromptTemplate = """
        # Task: Fix a bug in FixtureHelpers/BlockConverter.cs

        Users report that editing shapes via `{0}/FixtureHelpers/BlockConverter.cs` sometimes
        changes unrelated formatting elsewhere in the same file, even though they only asked for
        one class to be converted.

        Investigate `BlockConverter.cs`, find the root cause, and fix it. A similar bug was
        already fixed elsewhere in this codebase using a reusable pattern — look for it and reuse
        that same approach rather than inventing a new one.

        Verify your fix compiles, using an MCP tool (you have no terminal access). Scope the build
        to just the `ContosoOrders.Core` project rather than the whole solution.

        Do not modify any code unrelated to this specific bug. Report what you changed and the
        verification result.
        """;

    // Level 3.5: same symptom-only framing as MinimalGuidanceUserPromptTemplate above (no method/
    // file names, no step list) but closes one specific ambiguity that a 50-run reasoning-level
    // analysis (see project_minimalguidance_reasoning_pattern_analysis memory, 2026-08-31) showed
    // was a major fork between pass and fail under the ORIGINAL (since-flipped) assertion: "reuse
    // that same approach" says nothing about *how* to reuse a private method, and the model's own
    // next thought decides everything. That original analysis scored "call it directly, raising
    // its accessibility" as a failure — but per the real-world precedent this fixture is modeled
    // on (commit 8a8963d: consolidate 52 duplicate call sites onto one shared helper, not grow more
    // copies), calling the shared helper directly is actually the correct fix, and AssertFixApplied
    // was flipped to match. This variant's disambiguating sentence now points at that same correct
    // outcome instead of away from it — see AssertFixApplied's comment for the full rationale.
    //
    // Plan-before-edit paragraph added 2026-09-02 (see docs/current/project_own_copy_helper_dominant_failure.md
    // and docs/current/model_eval_pattern_analysis_2026_09_02.md §4.4): a 165-run excavation found
    // this exact disambiguating text, on its own, did NOT reduce the dominant failure (the model
    // finds BlockEditHelpers.cs but still pastes a second copy of the method's body instead of
    // calling it — 45-55% of runs) — wording alone wasn't the lever. PlanThenExecute uses this
    // identical disambiguating text plus only one added instruction (state a complete plan before
    // any edit tool call) and reaches 80% mechanical correctness on the same ambiguity. Porting
    // that instruction here directly, rather than trying yet another wording of the ambiguity
    // itself, since that's the variable the excavation found actually correlates with success.
    private const string DisambiguatedMinimalGuidanceUserPromptTemplate = """
        # Task: Fix a bug in FixtureHelpers/BlockConverter.cs

        Users report that editing shapes via `{0}/FixtureHelpers/BlockConverter.cs` sometimes
        changes unrelated formatting elsewhere in the same file, even though they only asked for
        one class to be converted.

        Investigate `BlockConverter.cs`, find the root cause, and fix it. A similar bug was
        already fixed elsewhere in this codebase using a reusable pattern — look for it and reuse
        that same approach rather than inventing a new one.

        If the existing fix lives in a private method in another file, call it directly rather
        than copying its body into your own fix — raise its accessibility (e.g. to `internal`) so
        it can be called cross-file, but don't duplicate its logic, and don't modify anything else
        in that file. Once you switch the buggy method's call site to the shared method, delete
        only that one now-unused old method (the one the bug report is about) instead of leaving
        it behind — do not delete, rename, or otherwise modify any other method, field, or class,
        even ones that look unused, unrelated, or like dead code to you.

        If the reusable pattern is a "find and replace a block, re-indenting only that block"
        helper: locate that helper and call it directly, exactly once, passing it the original,
        unmodified text plus the old and new block content. Leave the input text itself unmodified
        before calling the helper — the helper is the only thing that should ever change it. It
        needs the old block content to still be present in the text you pass it, so it can find
        and replace it.

        Before making any tool call that edits a file, first write out your complete plan as plain
        text: the root cause, exactly which method(s)/file(s) you will modify, and the specific
        content you will place in `BlockConverter.cs`. Only after stating that plan in full should
        you begin making edit tool calls — do not interleave planning and editing.

        Verify your fix compiles, using an MCP tool (you have no terminal access). Scope the build
        to just the `ContosoOrders.Core` project rather than the whole solution.

        Do not modify any code unrelated to this specific bug, including code that looks unused —
        leave it exactly as you found it. Report what you changed and the verification result.
        """;

    // Scripted-plan variant: same symptom-only framing as DisambiguatedMinimalGuidanceUserPromptTemplate,
    // but instead of asking the model to locate the bug and derive a plan itself, the plan is handed
    // to it verbatim — lifted from a real model's own successful PlanThenExecute run (transcript
    // 20260901-005448-159, see project_seed_investigation_result's sibling analysis memories) so the
    // steps are exactly what a 9B model already proved it can conceive on its own. This isolates
    // execution fidelity from planning/bug-location: if pass rate here is much higher than
    // MinimalGuidanceDisambiguated's, the bottleneck is planning, not mechanical tool use.
    private const string ScriptedPlanUserPromptTemplate = """
        # Task: Fix a bug in FixtureHelpers/BlockConverter.cs

        Users report that editing shapes via `{0}/FixtureHelpers/BlockConverter.cs` sometimes
        changes unrelated formatting elsewhere in the same file, even though they only asked for
        one class to be converted.

        The root cause and fix have already been worked out for you. Apply exactly this plan:

        1. In `{0}/FixtureHelpers/BlockEditHelpers.cs`, raise `ReplaceBlockFormatted`'s
           accessibility from `private` to `internal` (it stays `static`). Don't change anything
           else in that file.
        2. In `{0}/FixtureHelpers/BlockConverter.cs`, in `ConvertAbstractClassToInterface`,
           replace the line `return ReformatWholeFile(rewritten);` with a call to the now-internal
           `BlockEditHelpers.ReplaceBlockFormatted(rewritten, oldHeader, newHeader)`, returning its
           result instead.
        3. Delete the now-unused `ReformatWholeFile` method from `BlockConverter.cs` entirely.

        Do not modify any other method, field, or class in either file, even ones that look
        unused or unrelated — leave everything else exactly as you found it.

        Verify your fix compiles, using an MCP tool (you have no terminal access). Scope the build
        to just the `ContosoOrders.Core` project rather than the whole solution.

        Report what you changed and the verification result.
        """;

    // "Refactor" (not "Refactoring") and "Workspace" are the exact mode strings
    // AddRoslynSentinelToolsBasic checks — these two together register everything the prompts in
    // this file need (ApplyDiff, Build, ReadFile, SearchSolutionText, ListSolutionItems via
    // SentinelWorkspaceTools; SentinelRefactoringTools/SentinelAugmentTools for edits) without
    // pulling in Advanced's much larger scanner/analyzer/asyncify tool catalog, which only adds
    // context bloat and slows the model down for tasks that never call those tools.
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Refactor", "Workspace",
    };

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
                "ROSLYNSENTINEL_LLM_MODEL is not set — model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        _fixture = new RoslynSentinel.Tests.TestSolutionFixture();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = RoslynSentinelTaskTools.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        _runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddHttpClient<LmStudioAgentClient>(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            // A slow local GPU (e.g. a GTX 1080) can take several minutes per completion on
            // larger prompts/diffs — floor well above LlmOptions.TimeoutSeconds's default-30s*4
            // so per-turn latency alone never trips the HTTP timeout ahead of the runner's own
            // wall-clock cap.
            client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
        });
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        // dotnet test's console logger block-buffers stdout when it's redirected to a file, so
        // ModelAgentRunner's per-turn logging is invisible until the whole test process exits —
        // this file sink writes+flushes independently so a run can be tailed live.
        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(_runDirectory, "agent.log")));

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
        await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, TestContext.CurrentContext.CancellationToken);

        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs"),
            WholeFileRewriteReproducer.HelperFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs"),
            WholeFileRewriteReproducer.BuggyFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "Shape.cs"),
            WholeFileRewriteReproducer.TargetAbstractClassFileContent,
            reloadSolution: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _mcpClient = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
        _agentClient = _host.Services.GetRequiredService<LmStudioAgentClient>();
    }

    [TearDown]
    public async Task TearDown()
    {
        // SetUp calls Assert.Ignore (throwing) before these are assigned when LM Studio isn't
        // configured — TearDown still runs in that case, so guard rather than NRE.
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _fixture?.Dispose();

        if (_runDirectory is not null)
        {
            ModelTestingResultsArchiver.ArchiveRunDirectory(_runDirectory);
        }
    }

    [Test]
    public async Task Model_FixesWholeFileRewriteBug_UsingExistingHelperPattern()
    {
        var result = await RunOnceAsync(UserPromptTemplate, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        await AssertFixApplied(result);
    }

    /// <summary>
    /// Harder variant of <see cref="Model_FixesWholeFileRewriteBug_UsingExistingHelperPattern"/>:
    /// same bug, same fixture, same assertions, but the prompt gives only the observable symptom —
    /// no method name, no sibling-file pointer, no step list. The model must locate the bug and
    /// discover the existing BlockEditHelpers.cs fix pattern on its own.
    /// </summary>
    [Test]
    public async Task Model_FixesWholeFileRewriteBug_MinimalGuidance()
    {
        var result = await RunOnceAsync(MinimalGuidanceUserPromptTemplate, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        await AssertFixApplied(result);
    }

    /// <summary>
    /// Same as <see cref="Model_FixesWholeFileRewriteBug_MinimalGuidance"/> but with the
    /// disambiguated prompt (see <see cref="DisambiguatedMinimalGuidanceUserPromptTemplate"/>) —
    /// compare pass rates between the two over N repeats to check whether closing the "reuse the
    /// approach" ambiguity actually raises the pass rate, per
    /// project_minimalguidance_reasoning_pattern_analysis's finding that this ambiguity is the
    /// dominant fork between pass and fail on the plain MinimalGuidance prompt.
    /// </summary>
    [Test]
    public async Task Model_FixesWholeFileRewriteBug_MinimalGuidanceDisambiguated()
    {
        var result = await RunOnceAsync(DisambiguatedMinimalGuidanceUserPromptTemplate, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        await AssertFixApplied(result);
    }

    /// <summary>
    /// Same fixture/assertions as <see cref="Model_FixesWholeFileRewriteBug_MinimalGuidanceDisambiguated"/>,
    /// but the prompt hands the model an exact, already-correct plan (see
    /// <see cref="ScriptedPlanUserPromptTemplate"/>) instead of asking it to find the bug and derive
    /// one. Isolates whether failures on the disambiguated prompt come from planning/bug-location or
    /// from mechanical execution — a model that still fails here despite a scripted correct plan
    /// points at execution fidelity, not reasoning, as the bottleneck.
    /// </summary>
    [Test]
    public async Task Model_FixesWholeFileRewriteBug_ScriptedPlan()
    {
        var result = await RunOnceAsync(ScriptedPlanUserPromptTemplate, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Converged, Is.True,
            $"Agent did not converge (stopped: {result.StopReason}) within {result.TurnCount} turns. See transcript: {result.TranscriptPath}");

        await AssertFixApplied(result);
    }

    /// <summary>
    /// Runs the same prompt N times against a fresh fixture each time to check how consistently the
    /// model reproduces a correct fix — the "loop for consistency" use case the harness exists for.
    /// Reports a pass-rate summary; do not assert 100% since small local models are not fully
    /// deterministic even at low temperature.
    /// </summary>
    [Test]
    [Explicit("Slow (N real model runs); run manually via `dotnet test --filter ConsistencyCheck`.")]
    public async Task Model_FixesWholeFileRewriteBug_ConsistencyCheck()
    {
        const int runs = 5;
        var passCount = 0;
        var turnCounts = new List<int>();

        for (var i = 0; i < runs; i++)
        {
            if (i > 0)
            {
                await TearDown();
                await SetUp();
            }

            var result = await RunOnceAsync(UserPromptTemplate, TestContext.CurrentContext.CancellationToken);
            turnCounts.Add(result.TurnCount);

            if (result.Converged)
            {
                try
                {
                    await AssertFixApplied(result);
                    passCount++;
                }
                catch (Exception ex) when (ex is AssertionException or InvalidOperationException or IOException)
                {
                    // Text-check failure (AssertionException) or a functional-check failure from
                    // FunctionalFixVerifier (build/reflection failure) — both mean this run failed;
                    // counted as a failed run below, transcript already on disk for inspection.
                }
            }
        }

        TestContext.Out.WriteLine($"Pass rate: {passCount}/{runs}. Turn counts: [{string.Join(", ", turnCounts)}]");
        Assert.That(passCount, Is.GreaterThan(0), $"Model never succeeded across {runs} runs — see per-run transcripts under {_runDirectory}/../");
    }

    private async Task<AgentRunResult> RunOnceAsync(string userPromptTemplate, CancellationToken cancellationToken)
    {
        // Generous caps for a slow local GPU (e.g. a GTX 1080): individual turns have been
        // observed to take 1-2 minutes, so a 10-minute cap can cut off a run mid-recovery
        // before the model genuinely gets stuck. 40 turns / 30 minutes gives real room to
        // either converge or fail on its own rather than on an artificial clock.
        var runner = new ModelAgentRunner(
            _agentClient, _mcpClient, turnCap: 40, wallClockCap: TimeSpan.FromMinutes(30),
            logger: _host.Services.GetRequiredService<ILogger<ModelAgentRunner>>());
        var userPrompt = string.Format(userPromptTemplate, Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core"));
        return await runner.RunAsync(AgentSystemPrompts.CodingAgent, userPrompt, _runDirectory, cancellationToken);
    }

    private Task AssertFixApplied(AgentRunResult result) => AssertFixApplied(_fixture, result, TestContext.CurrentContext.CancellationToken);

    /// <summary>
    /// Shared with <see cref="PlanImplementVerifyAgentTests"/>, which runs this same fixture/bug
    /// through its own separate <see cref="RoslynSentinel.Tests.TestSolutionFixture"/> instance —
    /// static + explicit fixture parameter instead of an instance method so both test classes can
    /// call it without one depending on the other's private state.
    /// </summary>
    internal static async Task AssertFixApplied(RoslynSentinel.Tests.TestSolutionFixture fixture, AgentRunResult result, CancellationToken cancellationToken)
    {
        var fixedPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs");
        Assert.That(File.Exists(fixedPath), Is.True, "BlockConverter.cs should still exist after the model's edits.");

        var fixedText = File.ReadAllText(fixedPath);

        // Only the *call* to ReformatWholeFile needs to be gone — the prompt never asks the
        // model to delete the now-unused method definition (matching plan-9b-step2.md step 4,
        // which only asks to stop calling the whole-file rewrite), so leaving
        // "private static string ReformatWholeFile(...)" as dead code is a valid fix, not a
        // failure. Checking for the bare substring here previously false-failed several runs
        // that fixed the bug correctly but left the dead method in place.
        Assert.That(fixedText, Does.Not.Contain("return ReformatWholeFile("),
            $"ConvertAbstractClassToInterface should no longer call ReformatWholeFile. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("ReplaceBlockFormatted"),
            $"The fix should call ReplaceBlockFormatted (directly or qualified) — bringing it into " +
            $"scope, not duplicating its body. Transcript: {result.TranscriptPath}");

        // The real-world incident this fixture is modeled on (commit 8a8963d, "Fix
        // NormalizeWhitespace whole-file reflow bug") was 52 call sites each reimplementing the
        // same fix independently — the actual fix was to consolidate onto ONE shared helper, not
        // to keep growing copies of it. So the correct model behavior here is to expose
        // ReplaceBlockFormatted (raise its accessibility) and call the existing method from
        // BlockConverter.cs — leaving it private and duplicating its body into BlockConverter.cs
        // is the failure mode this assertion now catches, matching the precedent instead of
        // fighting it.
        var helperPath = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs");
        var helperText = File.ReadAllText(helperPath);
        Assert.That(helperText, Does.Not.Contain("private static string ReplaceBlockFormatted"),
            $"ReplaceBlockFormatted should have its accessibility raised (internal/public) so " +
            $"BlockConverter.cs can call it directly instead of duplicating it. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Not.Match(@"(?:private\s+)?static\s+string\s+ReplaceBlockFormatted\s*\("),
            $"BlockConverter.cs should call the shared ReplaceBlockFormatted, not define its own " +
            $"copy of it. Transcript: {result.TranscriptPath}");

        // Unrelated methods must be byte-for-byte untouched — this is the actual bug signature
        // (whole-file reformat silently reindents code the model never meant to touch).
        Assert.That(fixedText, Does.Contain("public string UnrelatedMethodBefore( int    x , int y )"),
            $"UnrelatedMethodBefore's original (oddly-spaced) formatting should be untouched. Transcript: {result.TranscriptPath}");
        Assert.That(fixedText, Does.Contain("public string UnrelatedMethodAfter(  string   s  )"),
            $"UnrelatedMethodAfter's original (oddly-spaced) formatting should be untouched. Transcript: {result.TranscriptPath}");

        // Total cap raised 2 -> 8 (2026-09-02, see docs/current/project_modifymodifier_accessibility_footgun.md):
        // the 2026-09-02 165-run excavation found ~27 runs (mostly MinimalGuidance) produced a
        // byte-perfect, functionally correct fix and still failed here, because a legitimate
        // self-correction sequence spanning two DIFFERENT tools (e.g. a real compiler error on
        // ApplyDiff, then a ModifyModifier/ChangeAccessibility retry) never trips the per-tool cap
        // but does trip a tight total cap the moment any third, unrelated benign hiccup occurs
        // (almost always a zero-match SearchSolutionText). The total cap needs to be loose enough
        // to tolerate several independent single-retry corrections across a longer agentic run.
        // maxPerTool was raised from 2 to 4 on 2026-09-04: a PlanImplementVerify base-qwen3.5-9b
        // run made a wrong-then-right accessibility call on ONE tool (ApplyDiff: unqualified call
        // -> CS0103 -> added a using directive, still wrong -> fully-qualified call, correct),
        // 3 failed calls on the single tool for a legitimate multi-step diagnosis, not thrashing.
        // 4 still catches true thrashing (5+ retries on one unresolved root cause).
        AgentToolErrorAssertions.AssertWithinBudget(result, maxTotal: 8, maxPerTool: 4);

        // Text-scan checks above only prove the edited source LOOKS right — they can't catch
        // code that compiles but is functionally broken (e.g. the whole file replaced with every
        // line commented out, which still builds with 0 errors and can coincidentally satisfy
        // substring checks). Actually build the project and invoke the real method to confirm the
        // fixed code still produces the correct transformation, not just plausible-looking text.
        // See project_planimplementverify_5run_result_postfix_verify.md's run 3 for the incident
        // that motivated this.
        var coreProjectDirectory = Path.Combine(fixture.SolutionDirectory, "ContosoOrders.Core");
        var convertedOutput = await FunctionalFixVerifier.InvokeConvertAbstractClassToInterfaceAsync(
            coreProjectDirectory, WholeFileRewriteReproducer.TargetAbstractClassFileContent, "Shape", cancellationToken);

        Assert.That(convertedOutput, Does.Contain("public interface IShape"),
            $"ConvertAbstractClassToInterface should still convert 'public abstract class Shape' to " +
            $"'public interface IShape' after the fix — the code compiles but produced the wrong " +
            $"output. Transcript: {result.TranscriptPath}");
        Assert.That(convertedOutput, Does.Not.Contain("public abstract class Shape"),
            $"The original abstract class declaration should be gone from the real converted output. " +
            $"Transcript: {result.TranscriptPath}");
        Assert.That(convertedOutput, Does.Contain("double GetArea()"),
            $"The converted output should still retain the abstract methods (as interface members). " +
            $"Transcript: {result.TranscriptPath}");
    }
}
