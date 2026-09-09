# Eval Defect Remediation v2 — Step Index (qwen3.6-35b-a3b split, runner variant)

Source plan: `docs/current/plans/plan-eval-defect-remediation-v2.md`. This directory splits it
into self-contained steps, each run by an automated runner as its own fresh, isolated model
conversation with no memory of any other step. Each step file restates whatever prior-step
context it depends on and explicitly names the file(s) it implements — the model must not read,
open, or act on any other step file (via `ProjectDoc` or otherwise), and must stop after its own
gate rather than continuing on to the next step itself.

Run steps **strictly in order**. Each step ends with its own local build/test gate; do not start
the next step until the current one's gate passes. Phase-level gates (bigger regression sweeps)
are called out at the phase boundaries below.

Per `feedback_dogfood_mcp_blocking_errors.md`: every read/write goes through the RoslynSentinel
MCP tools (`ReadFile`, `ApplyDiff`/`ApplyUnifiedDiff`/`WriteFile`, `Member`, `ModifyModifier`,
etc.) — never plain filesystem edits. Any MCP tool failure or gap encountered while executing a
step is itself a blocking finding: stop immediately, write
`docs/current/blockers/blocking_error_<slug>.md` describing it, and end the turn rather than
routing around it.

## Steps

| # | File | What it does |
|---|---|---|
| 0 | [01-baseline.md](01-baseline.md) | Run full test suite once, record clean pass count |
| 1.1 | [02-phase1-types-and-engine-fix.md](02-phase1-types-and-engine-fix.md) | Reshape `BuildResult`, add `EngineErrorCode.BuildNotRun` + `Failure` helper, fix `RunQuickBuildAsync` zero-project fabrication, adapt `RunFullBuildAsync` field names, call-site sweep |
| 1.2 | [03-phase1-tests.md](03-phase1-tests.md) | Update existing Battery tests + add 5 new tests for the `Build` fix |
| — | **Phase 1 gate** | `RoslynSentinel.Tests.Battery` + `RoslynSentinel.Tests.Basic` green, solution build clean |
| 2.1 | [04-phase2-repro-and-trivia-intent.md](04-phase2-repro-and-trivia-intent.md) | Live-repro the `ChangeAccessibility` doc-comment-loss bug, then add `TriviaEditIntent` enum, change `ReplaceNodeFormattedAsync` signature, fix `RemoveSummaryCommentAsync` call site |
| 2.2 | [05-phase2-eol.md](05-phase2-eol.md) | Extract `EolUtilities`, wire EOL normalization into `ReplaceNodeFormattedAsync`/`RemoveNodeFormattedAsync`, add post-write invariant check |
| 2.3 | [06-phase2-fixture-tests.md](06-phase2-fixture-tests.md) | Eval prompt/fixture updates + all Phase 2 tests |
| — | **Phase 2 gate** | `RoslynSentinel.Tests.Basic` + `RoslynSentinel.Tests.Battery` green, solution build clean |
| 3.1 | [07-phase3-directivekind.md](07-phase3-directivekind.md) | Additive `DirectiveKind` enum on `OperationSummary`/`BatchResultSummary`/`BreakerStatusReport` |
| 3.2 | [08-phase3-finding-type.md](08-phase3-finding-type.md) | `Finding`/`FindingSeverity` types, add `Findings` to `EngineResultWrapper<T>`/`ToolResult<T>`, bridge-site sweep, `DataTag.SearchPattern` |
| 3.3 | [09-phase3-orientation-breaker.md](09-phase3-orientation-breaker.md) | Wire the orientation breaker's trip-check into `WorkspaceReadNavigationImpl`'s search path itself |
| 3.4 | [10-phase3-diffhunkanalyzer.md](10-phase3-diffhunkanalyzer.md) | Wire `DiffHunkAnalyzer`'s report out of `DiffEngine.ApplyDiff` through to `ApplyUnifiedDiff`'s `ToolResult` |
| — | **Phase 3 gate** | `RoslynSentinel.Tests.Basic` + `RoslynSentinel.Tests.Battery` + `RoslynSentinel.Tests.Asyncify` green, solution build clean |
| final | [11-final-verification.md](11-final-verification.md) | Manual MCP-driven verification of all three fixes end to end |

## Why this split

The original plan's numbered sub-sections (1.1–1.7, 2.0–2.6, 3.0–3.4) already group naturally
into independently-completable units of work. This split does not change any technical content,
decision, or file/line reference from the source plan — it only:

- Groups adjacent sub-sections into one step where they're one continuous edit, or where a later
  sub-section can't meaningfully start without the previous one's output already in hand (e.g.
  1.1+1.2's type-reshape-then-engine-fix is one continuous edit that leaves the solution building
  clean at the end; 2.1+2.2's live-repro-then-fix is one step because the fix cannot be written
  correctly without the repro's findings, and a fresh, isolated conversation has no way to inherit
  those findings from a separate prior run the way a human moving to a new chat, with the answer
  still in their own head, effectively can).
- Repeats necessary context (file paths, prior-step type definitions, corrected-vs-spec notes)
  in each step file so it stands alone.
- Adds an explicit "Prior state" section to each step so the model doesn't need to re-derive
  what earlier steps already changed — it can also just read the live code, which is
  authoritative if this note and the code disagree.
- Each step's own text names exactly the file(s) it implements and instructs the model not to
  read or act on any other step file, since each step runs as a fully separate, memory-less
  conversation — nothing carries forward except what actually landed in the repo.

If a step still proves too large for a single turn in practice, split it further along its
existing internal numbered items rather than re-deriving new boundaries. Conversely, if two
adjacent steps turn out to have the same hard producer/consumer dependency 2.1+2.2 had, merge them
the same way rather than trying to paper over it with more "re-derive this" wording.
