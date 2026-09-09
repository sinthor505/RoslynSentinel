# Plan: Sub-agent-wrapped `ApplyDiff` (Option A — single-turn, no tool access)

## Background

Overnight model-eval sweeps (`docs/current/blockers/finding_applydiff_size_threshold_local_model.md`,
36+ real runs against `qwen3.5-9b-coder` via LM Studio) found that a small local model driving
`ApplyDiff` directly from the orchestrating agent's own context degrades non-monotonically as the
target file grows past ~3-5KB (20-33 "unrelated" padding methods in the test fixture): mistakes
(calling a helper before copying it in), constraint violations (illegally publicizing a private
helper as a shortcut), and outright abandonment all become likelier, though never as a clean
threshold.

Two additional, more precise root causes were found on later investigation of the raw LM Studio
server logs (`lmstudio_logs/*.log`, format: `n_tokens = N, truncated = 0/1` on `slot release` lines;
`"finish_reason": "length"` in the response JSON):

- **The context window (64K, `n_ctx_slot = 65536`) was never actually hit** — max observed context
  across all batches was ~41K tokens. The abandonment pattern is not context-window truncation.
- **The test harness's own `maxTokensPerTurn` cap (2048, hardcoded default in
  `RoslynSentinel.Tests.ModelEval/AgentLoop/ModelAgentRunner.cs:29`) was hit repeatedly** — 10 of
  ~112 completions across batches 3-4 (~9%) show `finish_reason: "length"`. In the clearest example
  (`lmstudio_logs/2026-08-29.2.log:78812`), the model spent its entire 2048-token budget on visible
  `reasoning_content` working out the correct fix step-by-step, then got cut off mid-emission of the
  `tool_calls` JSON itself (`"Unterminated string in JSON"` — a truncated tool call, not a malformed
  one). This is a harness bug (an unrealistically low per-turn output cap for a model that "thinks
  out loud" before acting), not a RoslynSentinel product issue, and not the context-window
  hypothesis — but it is a real, previously-uncounted contributor to some of the "gives up" results,
  since a cut-off tool call is functionally indistinguishable from the model choosing to abandon.
- Separately, reading a batch-4 (two-step-prompt) transcript in full
  (`n20/20260829-110908-188/transcript.json`) found the model **twice inventing plausible-looking
  but entirely fabricated tool parameters** — a `confirmationCode: "0"` for `ApplyDiff`'s
  whole-file-rewrite size-guard confirmation flow (`SentinelWorkspaceTools.cs:439-458` — this flow
  only ever triggers after a real `ConfirmationRequired` rejection on >50% line-shrinkage; nothing
  in that run's actual server responses ever offered a code, the model manufactured the whole
  interaction from training-data familiarity with similar protocols) and a
  `changeId: "workspaceVersion:5"` for `UndoLastApply` that didn't correspond to any real operation
  blob — followed by 8 turns of redundant re-verification after the fix had already landed
  correctly. This "protocol hallucination" pattern was not seen in any single-step-prompt run and
  is the strongest evidence that **tool-protocol surface area itself, not just file size, is a
  hallucination risk for small models** — every additional round-trip through real tool responses
  is another chance for the model to misremember or invent the shape of what should come back.

**Design conclusion drawn from this**: the fix should shrink the tool-protocol surface the small
model has to reason about, not just shrink the file. A sub-agent that never sees `ApplyDiff`'s
confirmation-code flow, `UndoLastApply`, or any other tool response at all — because it isn't
calling tools, just transforming text once per attempt — structurally cannot hallucinate a
confirmation code, because it never has one to hallucinate about.

## Existing patterns being reused (not reinvented)

Mirrors `docs/current/spec-bulk-comment-orchestrator-v1.md` closely — `BulkComment` is the direct
precedent for "a tool that makes stateless, single-turn LLM completions with no further tool access,
where the server (not the model) owns the retry loop and progress tracking." That spec's own
background section documents *why* the alternative (an agentic loop with real tool access driving
multi-step work from its own context) was tried first and rejected: a weak local model given a
multi-level plan and tool access lost track of its own progress, hallucinated a checklist, and
falsely declared completion. The `ApplyDiff` overnight findings are an independent, second data
point for the same conclusion, from a different tool and a different task shape.

Reused as-is:

- **`ILlmClient` / `LmStudioClient`** (`RoslynSentinel.Common/ILlmClient.cs`,
  `LmStudioClient.cs`) — the existing single-turn completion client, already used by
  `CommentingEngine`. No new HTTP client needed; `ApplyDiffAgentEngine` (new, see below) takes
  `ILlmClient` as a constructor dependency the same way `CommentingEngine` does.
- **`LlmOptions`** (`RoslynSentinel.Common/LlmOptions.cs`) — `ROSLYNSENTINEL_LLM_BASE_URL` /
  `_MODEL` / `_TIMEOUT_SECONDS` / `_PARALLELISM`, unchanged. A sub-agent-wrapped `ApplyDiff` call
  processes one file per invocation, so `LlmOptions.Parallelism` is not relevant here the way it is
  for `BulkComment`'s per-file member fan-out — each `ApplyDiff` call makes at most `maxAttempts`
  *sequential* LLM calls (see below), never concurrent ones.
- **`ApplyProposedChangesAsync`** (`PersistentWorkspaceManager`) — the actual write/validate/
  rollback path is completely unchanged. The sub-agent only ever produces candidate file text; every
  attempt is validated and applied through the exact same chokepoint every other mutating tool uses
  (per `[[project_write_path_chokepoint_unified]]`), so drift-detection, undo-tracking, and the
  circuit breaker all keep working exactly as they do today.
- **`ICircuitBreaker`** (`CheckBreaker()` / `RecordBatchOutcome`) — checked once per `ApplyDiff`
  call, same as every other batch-mutating tool.
- **`ToolResult<T>` / `ResultError`** — same envelope shape as every other tool.
- **MCP Tasks eligibility** — per `RoslynSentinelTaskTools.cs`, adding a tool name to the `Names`
  frozen set is a one-line change with no other task-aware code needed in the tool method itself
  (confirmed by researching `BulkComment`'s integration). Since a 3-attempt sub-agent loop can
  plausibly take 1-5+ minutes on a slow local GPU (matching what the overnight sweep observed for
  multi-retry runs), `ApplyDiffAgentic` should be added to `RoslynSentinelTaskTools.Names` from day
  one rather than retrofitted later.

## New pieces

### 1. New tool: `ApplyDiffAgentic` (does not replace `ApplyDiff`)

This is an **additive** tool, not a change to `ApplyDiff` itself. `ApplyDiff` stays exactly as it
is today — a purely mechanical, deterministic diff/patch applier with zero LLM involvement, callable
directly by any agent (including a strong frontier model) that can already produce a correct diff
or full-file rewrite itself. `ApplyDiffAgentic` is a new tool for the specific case an orchestrating
agent wants to *describe* an edit in natural language and delegate the mechanics — most valuable
exactly when the orchestrator is itself a small/local model that struggles to hold a large file
plus a multi-hunk diff in its own context reliably (the overnight findings' whole subject).

```csharp
[McpServerTool(Name = "ApplyDiffAgentic")]
[Description("""
    Delegates a described code change to a single-purpose sub-agent that reads the target file,
    generates a fix, and applies it — internally retrying up to maxAttempts times without
    involving the caller's own context. Use this instead of ApplyDiff when you want to describe
    *what* should change rather than construct the exact diff/file content yourself; use ApplyDiff
    directly when you already have the exact new content or a unified diff in hand.

    filepath: the single file to change (required).
    instruction: natural-language description of the desired change — be as specific as ApplyDiff's
      own docs recommend for a diff (what exact text/method/block changes, and what must NOT
      change).
    maxAttempts: internal retry cap (default 3) — each attempt is a fresh, stateless generation
      from the current file content and the same instruction; a failed attempt's compiler errors
      are fed back into the next attempt's prompt, but the sub-agent has no memory across attempts
      beyond that and never sees or calls any MCP tool itself.

    Returns ApplyDiffAgenticResult: success, attemptsUsed, the final ApplyChangesResult (same shape
    ApplyDiff itself returns) on success, or the last attempt's validation diagnostics on failure
    after exhausting maxAttempts. This return value is the completion signal — never trust or act
    on sub-agent prose; only the structured result and the file's actual post-call content are
    authoritative.
    """)]
public async Task<ToolResult<ApplyDiffAgenticResult>> ApplyDiffAgentic(
    [Consumes(DataTag.SourceFilepath)] string filepath,
    [Description("Natural-language description of the desired change.")] string instruction,
    int maxAttempts = 3,
    bool dryRun = false,
    RequestContext<CallToolRequestParams>? requestParams = null,
    CancellationToken cancellationToken = default)
```

### 2. `ApplyDiffAgentEngine` (new, `RoslynSentinel.Advanced`)

Mirrors `CommentingEngine`'s shape (one engine, `ILlmClient` dependency, mechanical control flow
around single-turn completions) but with a **retry loop that regenerates from scratch each attempt**
rather than one-shot-per-item:

```csharp
public async Task<ApplyDiffAgenticResult> ApplyAsync(
    string filepath, string instruction, int maxAttempts, bool dryRun,
    CancellationToken cancellationToken)
{
    var originalText = await ReadFileTextAsync(filepath, cancellationToken);
    string? lastDiagnostics = null;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        var userPrompt = BuildPrompt(originalText, instruction, lastDiagnostics);
        var newFileText = await _llmClient.CompleteAsync(
            SystemPrompt, userPrompt, MaxTokensForFileSize(originalText.Length), cancellationToken);
        var extracted = ExtractFileContent(newFileText); // strip any fenced-code-block wrapper etc.

        if (dryRun)
        {
            return new ApplyDiffAgenticResult(Success: true, AttemptsUsed: attempt,
                DryRun: true, ProposedText: extracted);
        }

        var applyResult = await _workspaceManager.ApplyProposedChangesAsync(
            new Dictionary<FilePath, string> { [filepath] = extracted },
            retryCount: 1, validateChanges: true);

        if (applyResult.Success)
        {
            return new ApplyDiffAgenticResult(Success: true, AttemptsUsed: attempt,
                ApplyResult: applyResult);
        }

        lastDiagnostics = applyResult.ValidationResult?.Diagnostics.ToJson();
    }

    return new ApplyDiffAgenticResult(Success: false, AttemptsUsed: maxAttempts,
        LastDiagnostics: lastDiagnostics);
}
```

Key properties, each a direct response to a specific overnight finding:

- **Stateless per attempt, not a conversation.** Attempt 2 is built from `(originalText,
  instruction, attempt-1's compiler diagnostics)` — never from attempt 1's own generated text or
  any prior LLM response. This is the single biggest structural difference from what an agentic
  tool-calling sub-loop (Option B) would do, and it's deliberate: it means there is no accumulating
  context for the model to lose coherence in, and no tool response for it to hallucinate around,
  because *there are no tool responses in the prompt at all* — only source code and, on retry, a
  compiler diagnostic string.
- **No tool schema is ever passed to the LLM call.** `CompleteAsync`'s system prompt asks for
  "the complete new file content" as plain text (optionally fenced), not a tool call. This directly
  closes off the specific hallucination pattern found overnight (fabricated `confirmationCode`,
  fabricated `UndoLastApply` changeId) — there is no tool-call JSON shape for the model to invent
  a plausible-looking-but-wrong instance of, because it's never asked to produce one.
  `ExtractFileContent` is a thin, mechanical unwrap (strip a ```` ```csharp ```` fence if present),
  not a parser for anything the model could get subtly wrong in a way that needs its own retry
  logic — if extraction produces something that isn't valid enough to compile, that failure is
  caught by the *existing* `ApplyProposedChangesAsync(validateChanges: true)` path exactly like any
  other bad edit, and counted as a normal attempt failure.
- **Retry happens at the server/engine level, not by asking the model to "try again."** Each
  attempt calls `ApplyProposedChangesAsync` for real (never a preview-only check) — a failed attempt
  leaves the file unchanged (validation happens pre-write, same guarantee `ApplyDiff` itself already
  gives) and the loop simply tries again with the diagnostic appended to the prompt. This mirrors
  `BulkComment`'s "failure just means the item stays stale, re-invocation via idempotent state is
  the retry mechanism" philosophy, adapted to a bounded in-call loop instead of cross-call
  statelessness, because unlike `BulkComment`'s many-small-items shape, a single `ApplyDiff`-style
  edit has no natural "resume where the last call left off" unit smaller than "attempt again."
- **`MaxTokensForFileSize` must not repeat the harness's 2048-token bug.** The `finish_reason:
  "length"` finding above is a direct, concrete warning: a fixed low token cap silently truncates a
  "thinking" model's response mid-tool-call/mid-file. `ApplyDiffAgentEngine`'s completion budget
  must scale with the target file's size (e.g. `Math.Max(4096, originalText.Length / 3)`, tuned
  during verification) plus headroom for reasoning content, not reuse any fixed constant blindly
  copied from the test harness.
- **`maxAttempts` default of 3** matches the user's original ask and the overnight data: nearly
  every observed CS0103 self-correction resolved within 1-2 retries; only the pathological
  (protocol-hallucination) runs blew past that, and those are exactly the runs where failing fast
  after 3 clean, hallucination-free attempts is strictly better than what happened last night (a
  26-27 turn spiral visible in the *orchestrator's own* context).

### 3. `ApplyDiffAgenticResult` (new record, `RoslynSentinel.Common`)

```csharp
public sealed record ApplyDiffAgenticResult(
    bool Success,
    int AttemptsUsed,
    bool DryRun = false,
    string? ProposedText = null,          // dry-run only
    ApplyChangesResult? ApplyResult = null, // success only — same type ApplyDiff itself returns
    string? LastDiagnostics = null);       // failure only — last attempt's compiler diagnostics
```

Deliberately thin and structured, matching the "return value is the completion signal, not agent
prose" philosophy already enforced for `CommentingResult`. The orchestrating agent (however weak)
never has to parse or trust any model-generated prose to know whether the edit landed — only
`Success`/`ApplyResult`/`LastDiagnostics`.

### 4. System prompt (fixed constant, mirrors `CommentingEngine.CommentSystemPrompt`)

```
You are given the complete current contents of one C# file and an instruction describing a
change to make. Reply with ONLY the complete new file content (the whole file, not a diff or
fragment) — no explanation, no markdown fences, no partial excerpts. Preserve every part of the
file not covered by the instruction byte-for-byte, including exact formatting and comments.
If a prior attempt's compiler errors are included below, fix exactly those errors without
introducing new unrelated changes.
```

The "no markdown fences" instruction is a request, not a guarantee — `ExtractFileContent` strips a
fence if the model adds one anyway, same defensive posture as any prompt-based instruction to a
small model.

## What this does *not* attempt to fix

- **The size-correlated reliability curve itself** (the four-pattern degradation as file size
  grows) is not eliminated by this design — a large file is still a large single-turn prompt/
  completion for the sub-agent. What changes is *where* the resulting mess lands: inside one bounded
  `ApplyDiffAgentic` call and its own up-to-`maxAttempts` retries, never leaking into the
  orchestrator's own multi-hundred-turn context the way the 26-27-turn spirals did overnight. This
  is the efficiency argument from the user's own framing: even if the sub-agent internally "wastes"
  2-3 generations on a hard file, that cost is paid once, locally, without repeatedly re-feeding the
  *entire* accumulated orchestrator conversation through the model on every attempt — which is what
  was actually happening when the mistake-and-retry cycle played out inside the primary agent loop.
- **Diff-format edits are out of scope for v1.** The sub-agent always regenerates and submits the
  complete new file content (`changesetFormat: files` semantics internally), never a unified diff —
  simpler to validate, and sidesteps `ApplyDiff`'s diff-format subtleties (hunk re-anchoring, line
  drift) entirely for the sub-agent's own output. `ApplyDiffAgentic`'s existing whole-file-rewrite
  size guard (the same >50%-shrinkage `ConfirmationRequired` check `ApplyDiff` already has) should
  still apply on the internal `ApplyProposedChangesAsync` call — no special-casing needed, it's the
  same code path.
- **Multi-file changes are out of scope for v1** — `filepath` is a single file, matching the
  overnight fixture's own shape (one buggy file, one reference file to *read* but not touch). A
  future version could accept multiple files the same way `ApplyDiff`'s `changesetFormat: files`
  does, but that reintroduces cross-file coordination complexity this design deliberately avoids for
  a first version.

## Option B — noted for future development, not designed here

An agentic sub-loop (the sub-agent gets real MCP tool access — `ReadFile`, `ApplyDiff`, `Build` —
across up to `maxAttempts` turns, self-correcting from actual tool responses rather than a
server-constructed diagnostic string) is a plausible alternative that could handle harder cases
better, since the model would see its own real compiler output interactively rather than a single
injected diagnostic. `ModelAgentRunner`
(`RoslynSentinel.Tests.ModelEval/AgentLoop/ModelAgentRunner.cs`) is the only existing precedent for
this shape in the codebase, but it is test/eval-only infrastructure today, not wired for any
production tool.

The overnight findings are a specific, concrete argument *against* choosing this first: the
protocol-hallucination failure mode (fabricated confirmation codes, fabricated undo IDs) is a direct
consequence of giving the model real tool responses to reason about and imitate. Option B
reintroduces that exact surface area, just contained inside a sub-agent instead of the primary
orchestrator — better than leaking into the orchestrator's context, but not free of the risk Option
A avoids structurally. If Option A's 3-attempt cap turns out to fail often enough in practice
(tracked via `AttemptsUsed`/`Success` rates once this ships) to justify the added complexity, Option
B becomes worth prototyping — likely gated behind measuring how often Option A's non-agentic retry
actually exhausts `maxAttempts` on genuinely hard cases versus how often 3 stateless regenerations
are sufficient.

## Files touched

- New: `RoslynSentinel.Common/ApplyDiffAgenticResult.cs`
- New: `RoslynSentinel.Advanced/ApplyDiffAgentEngine.cs`
- New: `ApplyDiffAgentic` tool method in `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`
  (co-located with `ApplyDiff` itself, not a new tool-class file, since it's a sibling operation on
  the same surface)
- Modified: `RoslynSentinel.Server.Advanced/RoslynSentinelTaskTools.cs` (add `"ApplyDiffAgentic"` to
  `Names`)
- Modified: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` (register
  `ApplyDiffAgentEngine`; `ILlmClient`/`LmStudioClient` registration already exists from
  `BulkComment`, no change needed there)
- Reused as-is: `ILlmClient`, `LmStudioClient`, `LlmOptions`, `ApplyProposedChangesAsync`,
  `ICircuitBreaker`, `ToolResult<T>`, `ToolErrorMapper`

## Verification

1. Build — 0 errors.
2. Point `ROSLYNSENTINEL_LLM_BASE_URL`/`ROSLYNSENTINEL_LLM_MODEL` at a running LM Studio instance;
   call `ApplyDiffAgentic(filepath: ..., instruction: ..., dryRun: true)` against a small test file
   — confirm it reports proposed text with zero disk writes.
3. Reproduce the exact overnight fixture scenario
   (`RoslynSentinel.Tests.ModelEval/Fixtures/SizeGraduatedReproducer.cs`, e.g.
   `BuildBuggyFileContent(20)`) via `ApplyDiffAgentic` instead of the orchestrator driving `ApplyDiff`
   turn-by-turn — confirm the fix lands correctly and measure `AttemptsUsed` against the overnight
   `applyDiffErrorCount` data for the same file size as a rough apples-to-apples comparison.
4. Deliberately trigger a compile failure on attempt 1 (e.g. by giving an instruction that's
   slightly ambiguous) — confirm attempt 2's prompt includes attempt 1's real diagnostics and that
   the loop terminates with `Success: false, AttemptsUsed: 3` (not an infinite loop, not a silent
   partial write) when all attempts fail.
5. Confirm `UndoLastApply` works normally against a successful `ApplyDiffAgentic` call's resulting
   `ApplyResult.UndoChangeId` — the internal mechanism should be fully transparent to every existing
   forensic/undo tool, since it's the same `ApplyProposedChangesAsync` chokepoint.
6. Confirm task-backed execution: call `ApplyDiffAgentic` through the MCP Tasks polling path (per
   the pattern in `RoslynSentinel.Tests.Advanced/McpTasksHarnessBulkCommentTests.cs`) and verify a
   multi-attempt (slow) call can be polled and, if needed, cancelled mid-flight via
   `CancellationToken`.
7. Once working, consider re-running a slice of the overnight sweep
   (`RoslynSentinel.Tests.ModelEval/SizeThresholdAgentTests.cs`-style harness, adapted to call
   `ApplyDiffAgentic` once instead of driving raw `ApplyDiff` across the orchestrator's own turns) at
   the noisy 20/33-method boundary sizes, to get a real before/after comparison rather than the
   rough estimate in step 3.
