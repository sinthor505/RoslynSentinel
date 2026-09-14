# PlanStepRunner run 20260910-013550-398 — Category B: Harness / runner

**Status: RESOLVED 2026-09-10.** B1 (inline step content by value), B3 (frontmatter-driven
`readOnly`/`buildOptional`, enforced before commit; scope-violation check added as warn-only),
and B4 (in-loop repeated-consecutive-failure breaker, `AgentStopReason.RepeatedToolFailure`,
auto-written blocker doc) are all implemented and covered by unit tests
(`RoslynSentinel.Tests.PlanStepRunner`, `RepeatedToolFailureBreakerTests`). B2 needed no code
change — `WriteToolAdviceHelper.cs` already derives its advice text from the live tool-class
set rather than naming a gated-off tool (option 1 below, already in place). Also fixed in the
same pass: a stale `docs\tests\` path in `roslynsentinel-planstep.ps1` left over from the
`docs\testing\` directory move in `e3d32b8`, which otherwise made the runner unrunnable via
its front door. Live end-to-end verification against a real LM Studio host is still pending;
the fixes below are otherwise complete and build/test-clean.

**Source run:** `PlanStepRunner/20260910-013550-398/01-baseline/`
**Outcome:** `TurnCapExceeded` after 60 turns / 24m18s; worktree non-compiling (190 errors),
uncommitted, `01-baseline` never completed.

**Scope:** four harness/runner defects (B1–B4) in
`RoslynSentinel.Tools.PlanStepRunner`. Tool defects are in
`finding_planstep_run_398_A_tool_defects.md`; plan content in
`finding_planstep_run_398_C_plan_content.md`.

**Central finding:** step 01 is a **read-only, no-code-changes** step. The model performed
step 02's and step 3.2's work instead, producing 9 mutation operation blobs and 3 modified
source files. Nothing in the harness noticed, prevented, or flagged this.

---

## The scope violation, in evidence

`docs/tests/plan-eval-defect-remediation-v2-steps-runner/01-baseline.md` opens:

> **This step implements only this file.** Do not read, open, or act on any other plan step
> file (e.g. via `ProjectDoc`) … **Do not make any code changes in this step.**

and closes:

> No gate — this step only records a number. Report the baseline counts and stop — do not
> proceed to any other step.

What the worktree actually contains:

```bash
$ cd PlanStepRunner/20260910-013550-398/01-baseline/Worktree && git status --short
 M RoslynSentinel.Basic/BuildEngine.cs
 M RoslynSentinel.Common/BuildResult.cs
 M RoslynSentinel.Common/EngineResultWrapper.cs
?? bin-runner/

$ ls .roslynsentinel/operations/ | wc -l
9        # 1 Member_*, 8 replace_snippet_*
```

The diff is step 1.1's spec (`BuildOutcome` enum, `BuildResult` reshape,
`EngineErrorCode.BuildNotRun`, `EngineResultWrapper<T>.Failure`) left half-applied — only
one of the two `BuildResult` construction sites was updated, hence 190 build errors.

**The model was not disobeying.** It never received the text above; `ProjectDoc` served a
different file whose gate said *"Proceed to 02-phase1-types.md"* (see doc A, A1). B1–B4 are
about the harness having **no independent enforcement** of a constraint it states only in prose.

---

## B1 — Step content is passed by path, not by value (CRITICAL)

**File:** [Program.cs:167-179](RoslynSentinel.Tools.PlanStepRunner/Program.cs#L167-L179)

```csharp
var relativeStepPath = Path.GetRelativePath(options.SourceRepo, step.FilePath);
var worktreeStepPath = Path.Combine(worktreePath, relativeStepPath);

var userPrompt =
    "The solution is already loaded — do not call LoadSolution or ListWorkspaceSolutions, " +
    "go straight to reading/editing.\n" +
    $"Review the planning doc `{worktreeStepPath}`.\n" +
    "Implement the plan.";
```

The runner computes the correct absolute path and then hands the model a *string* it must
re-resolve through `ProjectDoc`. That indirection is where A1 substituted a different
document. The runner already holds `step.FilePath` and could simply read it.

Note the model was given an **absolute path** and called `ProjectDoc` with a
**docs-relative name** — it translated, correctly, but into a lookup that then misresolved.

### Fix

Inline the step text into the prompt:

```csharp
var stepText = File.ReadAllText(worktreeStepPath);
var userPrompt =
    "The solution is already loaded — do not call LoadSolution or ListWorkspaceSolutions, " +
    "go straight to reading/editing.\n" +
    "Implement the plan below. It is reproduced in full; do not look for it on disk, and do " +
    "not read any other plan step file.\n\n" +
    "=== BEGIN PLAN STEP: " + step.FileName + " ===\n" + stepText +
    "\n=== END PLAN STEP ===";
```

Single highest-value change in this document — it makes A1 unable to affect the runner at
all, independent of whether A1 is fixed.

---

## B2 — `--include-tools` default omits `WriteFile`, which error text tells the model to use

**File:** [RunnerOptions.cs:41-42](RoslynSentinel.Tools.PlanStepRunner/RunnerOptions.cs#L41-L42)

```csharp
var includeTools = GetArg(args, "--include-tools")
    ?? "SentinelWorkspaceTools,SentinelSymbolTools,SentinelRefactoringTools," +
       "SentinelDocumentationTools,SentinelCommentingTools,SentinelAdvancedRefactoringTools";
```

60 tools exposed; `WriteFile` is not among them (verified against `agent.log` line 1).
`ReplaceSnippet`'s size-limit error names `WriteFile(operation=ReplaceFile)` as the
prescribed escape — see doc A, A2. Result: 24 identical failures, turns 38–60.

### Decision required

`WriteFile` is deliberately gated. Memory `project_wholefilewrite_gating_overnight_result_2026_09_08`
records that removing whole-file write from the default surface took a fixture from **47% to
26/26**, so re-adding it wholesale would regress a well-evidenced win. Options:

1. **Fix the error text instead** (doc A, A2 option 1) — keep gating, stop advertising an
   absent tool. Preserves the gating result. **Recommended.**
2. Raise `MaxContentChars` so mid-size edits are expressible without `WriteFile`.
3. Expose `WriteFile` only for runner use — contradicts the gating evidence; not recommended.

Also relevant: `feedback_dont_name_gated_tools_in_agent_docs` — state the allowed workflow
positively rather than pointing at something unavailable.

---

## B3 — No enforcement that a read-only step stays read-only

**File:** [Program.cs:92-104](RoslynSentinel.Tools.PlanStepRunner/Program.cs#L92-L104)

```csharp
var buildOptional = KnownBuildOptionalSteps.Contains(step.FileName);
var buildOk = outcome.BuildErrorCount == 0;
var shouldAdvance = outcome.Converged && !outcome.LooksBlocked && (buildOk || buildOptional);

if (!shouldAdvance) { /* halt, leave worktree */ return 1; }

git.CommitWorktree(worktreePath, $"Plan step {step.FileName}");
git.RemoveWorktree(worktreePath);
```

The advance gate checks convergence, blocked-phrases, and build errors. It does **not**
check whether the step was permitted to modify code, or whether the files touched relate to
the step at all.

### The near-miss

Here the gate did the right thing by accident: the model hit the turn cap and left 190 build
errors, so `shouldAdvance` was false and it halted. **Had the model finished step 1.1
cleanly**, the runner would have:

1. committed step 02's work under the message `"Plan step 01-baseline.md"`;
2. removed the worktree, discarding the evidence;
3. started step 02 against a tree where step 02 was already (partly) done —
   whose "Prior state" section asserts none of it has happened yet.

That silent-corruption path is still live and is the real reason to fix this.

### Fix

Add a per-step `readOnly` flag (parsed from the step file, or from a manifest) and enforce it:

```csharp
if (step.ReadOnly)
{
    var dirty = git.GetDirtyPaths(worktreePath)
        .Where(p => !p.StartsWith("bin-runner/")).ToList();
    if (dirty.Count > 0)
    {
        Console.WriteLine($"HALTING — read-only step {step.FileName} modified: {string.Join(", ", dirty)}");
        return 1;
    }
}
```

Even without a `readOnly` flag, a **general** scope check is worth adding: warn (or halt)
when a step's committed diff touches files no part of that step's text mentions.

Note `KnownBuildOptionalSteps` ([Program.cs:228](RoslynSentinel.Tools.PlanStepRunner/Program.cs#L228))
is currently an empty set, with a long comment explaining every step must build clean. That
comment's reasoning — "the next step's worktree is built from this one's committed tip" — is
exactly why an unscoped commit is dangerous.

---

## B4 — No repeated-failure circuit breaker; turn cap absorbs the loop

**File:** [Program.cs:161-165](RoslynSentinel.Tools.PlanStepRunner/Program.cs#L161-L165)
(`ModelAgentRunner`, `turnCap: options.TurnCap`, default 40 —
[RunnerOptions.cs:39](RoslynSentinel.Tools.PlanStepRunner/RunnerOptions.cs#L39); this run used 60)

The last 23 turns were the same tool, same file, same `errorCode`, same message. The only
thing that stopped it was exhausting the turn budget, ~13 minutes later.

The plan's own index and `feedback_dogfood_mcp_blocking_errors` both say a tool failure is a
**blocking** finding: stop, write `docs/current/blockers/blocking_error_<slug>.md`, end the
turn. The model did not do this — and the harness has no mechanism to make it.

### Fix

1. **Breaker in `ModelAgentRunner`:** N consecutive failures (suggest N=3) with the same
   `(toolName, errorCode, filePath)` → terminate with a distinct stop reason
   (`RepeatedToolFailure`), so it is distinguishable from `TurnCapExceeded` in results.
   A per-tool error budget already exists (`project_per_tool_error_budget_added`, 3a66d65) —
   check whether it is wired into this runner's path, since it plainly did not fire here.
2. **Auto-write the blocker doc** on trip, capturing tool, args, error, and turn range.
   Removes the dependence on the model choosing to comply.
3. **Surface it in `StepOutcome`** ([Program.cs:274-276](RoslynSentinel.Tools.PlanStepRunner/Program.cs#L274-L276))
   so the summary line distinguishes "ran out of turns making progress" from "looped on one
   error for 23 turns."

### Secondary: `LooksBlocked` detection is fragile

[Program.cs:211-215](RoslynSentinel.Tools.PlanStepRunner/Program.cs#L211-L215) matches
substrings against the final message only:

```csharp
private static readonly string[] BlockedPhrases =
[
    "cannot complete", "can't complete", "unable to complete", "i am blocked", "i'm blocked",
    "cannot find", "could not find", "unable to proceed", "cannot proceed",
];
```

Two weaknesses: (a) on `TurnCapExceeded` the last message is a tool call, not prose, so
there is nothing to match; (b) `"cannot find"` / `"could not find"` are common in ordinary
narration and will produce false positives. Structural signals (failure counts, breaker
trips) are more reliable than phrase-matching.

---

## Secondary observations (no action proposed)

- **Sibling step files are present in the worktree.** All 11 runner-copy steps plus all 13
  old-copy steps ship in every worktree, so isolation is asserted in prose only. Covered as
  a plan-content issue in doc C (C2), but the harness could equally enforce it.
- **The runner-copy plan directory is unreachable via `ProjectDoc`** (doc A, A1). Until A1
  or B1 lands, the runner variant's scope-lock wording has no effect on any run.
- **The three test projects were run correctly.** Turns 2–4 captured
  `Tests.Basic` 228/228, `Tests.Battery` 902 total / 898 passed (4 pre-existing failures,
  consistent with `reference_known_failing_tests`), `Tests.Asyncify` 92/92. **The actual
  deliverable of step 01 was complete by turn 4** — everything after was out of scope.

## Suggested priority

| # | Issue | Severity | Effort |
|---|---|---|---|
| B1 | Pass step content by value | Critical | Low |
| B3 | Enforce read-only / scope on advance | High | Low–Medium |
| B4 | Repeated-failure breaker | High | Medium |
| B2 | `WriteFile` / error-text mismatch | Medium | Low (decision, not code) |

**B1 alone would have prevented this run's failure.** B3 prevents the worse, silent variant
where a scope-violating step commits cleanly and corrupts every subsequent step.
