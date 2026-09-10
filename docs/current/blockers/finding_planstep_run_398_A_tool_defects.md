# PlanStepRunner run 20260910-013550-398 — Category A: Tool defects

**Source run:** `PlanStepRunner/20260910-013550-398/01-baseline/`
(`Logs/agent.log`, `Logs/transcript.json`, `Worktree/`)

**Outcome:** `TurnCapExceeded` after 60 turns / 24m18s. Worktree left non-compiling
(190 errors). 34 of 60 turns were tool failures.

**Scope of this doc:** the six product-level tool defects (A1–A6). Harness/runner issues
are in `finding_planstep_run_398_B_harness.md`; plan-content issues in
`finding_planstep_run_398_C_plan_content.md`.

**Reproduce the log slices below with:**
```bash
cd PlanStepRunner/20260910-013550-398/01-baseline/Logs
grep -oE 'Turn [0-9]+: [A-Za-z]+ FAILED' agent.log | awk '{print $3}' | sort | uniq -c | sort -rn
grep -oE '"message":"[^"]{0,320}' agent.log | sort | uniq -c | sort -rn
```

## Failure tally (run 398)

| Tool | Fails | Turns |
|---|---|---|
| `ReplaceSnippet` | 26 | 9,10,12,29,30,31,34,38,40–45,47–54,57–60 |
| `SearchSolutionText` | 4 | 17,18,24,25 |
| `ReadFile` | 1 | 6 |
| `Member` | 1 | 19 |
| `UndoLastApply` | 1 | 36 |
| `CreateFile` | 1 | 56 |

---

## A1 — `ProjectDoc` read silently returns a *different* document (CRITICAL)

**Files:** [SentinelDocumentationTools.cs:109-186](RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs#L109-L186)
(`ReadFile`), [SentinelDocumentationTools.cs:193-211](RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs#L193-L211)
(`FindByBasename`)
**Introduced by:** commit `91a0a5e` "Fix ProjectDoc read to fall back across all of docs/, not just the requested docType"
**Severity:** Critical — this single defect caused the whole run to execute the wrong work
(see doc B for the downstream consequence).

### What happened

Turn 1 requested the runner copy and got the *other* plan directory's file back:

```
Turn 1: calling ProjectDoc with args:
  {"action":"read","docType":"plan",
   "name":"tests/plan-eval-defect-remediation-v2-steps-runner/01-baseline.md"}

Turn 1: ProjectDoc succeeded. Result:
  {"found":true,
   "filename":"plan-eval-defect-remediation-v2-steps/01-baseline.md",   <-- WRONG DIR
   "content":"# Step 0 ... ## Gate\r\n\r\nNo gate ... Proceed to\r\n[02-phase1-types.md]..."}
```

`found:true`. No warning. The `filename` field is the only evidence, and it is *not* the
path that was asked for.

### Root cause (verified)

`FindByBasename` discards the directory component at
[line 200](RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs#L200):

```csharp
string wantStem = Path.GetFileNameWithoutExtension(Path.GetFileName(name));
```

`Path.GetFileName("tests/plan-…-runner/01-baseline.md")` → `"01-baseline.md"` → stem
`"01-baseline"`. The caller's directory is thrown away, then
[line 206-210](RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs#L206-L210)
matches that stem against **every** file under `subdir`, recursively.

`docs/current/` exists, so `GetDocTypeSubdirRoot`
([line 103-107](RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs#L103-L107))
makes `subdir` = `docs/current/plans`. Verified contents:

```bash
$ find docs/current/plans -iname "01-baseline.*"
docs/current/plans/plan-eval-defect-remediation-v2-steps/01-baseline.md   # exactly 1

$ find docs -iname "01-baseline.*"
docs/current/plans/plan-eval-defect-remediation-v2-steps/01-baseline.md
docs/tests/plan-eval-defect-remediation-v2-steps-runner/01-baseline.md    # the one wanted
```

Fallback 1 ([line 125-134](RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs#L125-L134))
finds `matches.Count == 1` and returns it as an unambiguous hit. Because Fallback 1
succeeds, **Fallback 2 (line 149-179) — the `docs/`-wide search that 91a0a5e added, and
the only branch that could reach `docs/tests/` — is never executed.** The requested file
is unreachable via `ProjectDoc` under any `docType`.

The `Count > 1` ambiguity guards at
[136-144](RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs#L136-L144) and
[171-179](RoslynSentinel.Server.Basic/SentinelDocumentationTools.cs#L171-L179) do not fire:
the two candidates live in *different* search scopes, so neither scope ever sees a count of 2.

Same substitution recurred at turn 5 (`02-phase1-types.md`) and turn 22
(`03-phase1-engine-fix.md`) — filenames that exist **only** in the old directory.

### Fix options

1. **Honour an explicit path.** If `name` contains `/` or `\`, treat it as a real relative
   path: on miss, error out naming what was searched. Only apply basename fallback to bare
   names. (Preferred — the fallback exists for bare/mis-extensioned names, not to
   override a caller who supplied a full path.)
2. **Search both scopes before deciding.** Union Fallback 1 + Fallback 2 candidates, then
   apply the ambiguity check once. Would have produced the correct "did you mean" error here.
3. **Minimum viable:** when `result.Filename != requested name`, add a `warning` field
   (`"resolved 'X' to 'Y' by basename fallback"`). Cheap, and makes every future
   substitution visible in the transcript.

**Test to add:** two files with the same basename in different `docs/` subtrees; assert a
path-qualified read returns the requested one or errors — never the other. Existing tests
live in `RoslynSentinel.Tests.*/SentinelDocumentationToolsTests.cs` (also touched by 91a0a5e).

---

## A2 — `ReplaceSnippet` size limit points at a tool that is not exposed (CRITICAL)

**File:** [SentinelWorkspaceTools.cs:585-596](RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L585-L596)
**Constants:** `MaxOldContentLines = 20`, `MaxContentChars = 200`
([lines 537-538](RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L537-L538))
**Severity:** Critical — terminal livelock; consumed turns 38–60 (~23 of 60 turns).

### What happened

24 identical failures with this message:

```
ReplaceSnippet: oldContent/newContent exceeds the size limit for a small localized edit
(max 20 lines / 200 chars each). For a whole-file rewrite, use WriteFile(operation=ReplaceFile).
For a structural change (rename, signature, extract), use the matching Roslyn tool
(RenameSymbol, ChangeSignature, ExtractMethodSafe, Member, etc.).
For multiple small edits in the same file, call ReplaceSnippet once per edit.
```

**`WriteFile` is not in the runner's exposed toolset.** Verified against the 60 tools listed
in `agent.log` line 1:

```bash
$ head -2 agent.log | grep -o 'WriteFile' || echo "NOT exposed"
NOT exposed
```

The model needed a ~25-line insertion into `BuildEngine.cs`. It was told to use a tool it
did not have, tried `CreateFile` as a substitute (turn 56, rejected — see A6), then spent
every remaining turn reshaping the same oversized edit. The final two turns:

```
Turn 59: ReplaceSnippet FAILED ... exceeds the size limit
Turn 60 reasoning: "ReplaceSnippet has max 20 lines oldContent and 20 lines newContent.
  Let me use ExtractMethodSafe to insert the gate code, or use a smaller ReplaceSnippet."
Turn 60: ReplaceSnippet FAILED ... exceeds the size limit
Agent run finished: TurnCapExceeded after 60 turn(s)
```

Note the 200-**char** ceiling is the tighter constraint: a 6-line C# insertion with normal
indentation exceeds 200 chars long before it hits 20 lines. The message leads with
"max 20 lines / 200 chars", which reads as far more permissive than it is.

### Fix options

1. **Gate error advice on tools actually exposed.** The error text should be built from the
   live tool registry — never name a tool absent from the current `--include-tools` set.
   Applies beyond this message; audit all "use X instead" strings.
2. **Add `WriteFile` to the runner's include-tools** (see doc B, B2) — necessary but not
   sufficient; the general defect in (1) remains.
3. **Report which limit tripped** and by how much: `"newContent is 412 chars (limit 200)"`.
   The model repeatedly guessed wrong about which bound it was hitting.
4. Consider whether 200 chars is the right ceiling for `newContent`. The rationale (keep
   edits small/anchored) applies to `oldContent` matching; a larger `newContent` bound
   would not weaken anchoring.

**Cross-ref:** `finding_applydiff_size_threshold_local_model.md` in this directory — same
class of problem (size threshold pushing a local model into a loop).

---

## A3 — `UndoLastApply` cannot revert a change `WrapRange` reported as applied

**Files:** [SentinelWorkspaceTools.cs:1693-1712](RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1693-L1712)
(`UndoLastApply` / `OperationBlobWriter.FindBlobPath`),
[SentinelAdvancedRefactoringTools.cs:850-996](RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs#L850-L996)
(`WrapRange`; region path at
[line 924](RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs#L924))
**Severity:** High — the model could not undo its own bad edit and had to work around it.

### What happened

Turn 35, `WrapRange` succeeded and *explicitly advertised* the undo:

```json
{"success":true,"data":{
  "changeId":"9f12909c",
  "affectedFiles":["...\\RoslynSentinel.Basic\\BuildEngine.cs"],
  "description":"Wrapped snippet in #region 'ZeroProjectsGate' in BuildEngine.cs.",
  "dryRun":false,
  "status":"applied",
  "note":"Written to disk. Call UndoLastApply(changeId: \"9f12909c\") to revert if needed."}}
```

Turn 36 took it at its word and failed:

```
UndoLastApply({"changeId":"9f12909c"})
-> {"errorCode":"NoOperationBlobFound",
    "message":"No operation blob found for changeId '9f12909c'.
               Ensure the apply completed successfully and a solution is loaded."}
```

No blob was ever written — verified directly:

```bash
$ ls Worktree/.roslynsentinel/operations/ | grep -i 9f12909c
(no output)
```

The directory holds 9 blobs (1 `Member_*`, 8 `replace_snippet_*`) — none from `WrapRange`.
So `WrapRange` writes to disk on the `#region` path without emitting an operation blob,
while returning a `changeId` and instructing the caller to undo with it. The error message
also misdiagnoses ("ensure the apply completed successfully") — the apply *did* complete;
the blob was simply never written.

### Why it matters beyond this run

This is the third recorded instance of an `UndoLastApply` gap
(cf. `blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md`
and the `project_synctypeandfilename_undolastapply_blocker` memory). The pattern is
consistent: **a mutating tool succeeds and returns a `changeId` that undo cannot resolve.**

### Fix options

1. **Route `WrapRange` through the shared write chokepoint** so a blob is always written.
   Per the `project_write_path_chokepoint_unified` note (cb70952), all `.cs` writes should
   go through `ApplyProposedChangesAsync` — verify whether `WrapRange`'s
   `ValidateAndApplyAsync` path actually reaches it for `wrapper: region`.
2. **Invariant + test:** any tool result carrying `status:"applied"` and a `changeId` must
   have a resolvable blob. Worth asserting generically across the mutating-tool battery
   rather than per tool.
3. **Do not emit the "Call UndoLastApply(...)" note** unless a blob was confirmed written.

---

## A4 — `SearchSolutionText` literal mode defeats regex patterns (4 wasted turns)

**File:** [WorkspaceReadNavigationImpl.cs:429](RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs#L429)
**Known issue:** memory `project_searchmode_literal_override_bug`; existing test at
[BatteryTwentyTests.cs:207](RoslynSentinel.Tests.Battery/BatteryTwentyTests.cs#L207)
**Severity:** Medium — pure turn waste, model always recovered.

Three failures, all the same shape (turns 17, 18, 25):

```
Pattern 'Failure.*EngineResultWrapper' contains regex metacharacters ([\^\$\.\*\+\?\(\)\[\]\{\}\|\\])
but searchMode is literal - searched for the literal substring as requested.
Pass searchMode: regex if you meant to search as a regex. No matches were found...
```

Patterns: `Failure.*EngineResultWrapper` (t17), `public static.*Failure` (t18),
`McpServerTool.*Build` (t25). Turn 24 was a plain no-match on
`class DiagnosticSummary`.

The tool **detects** the metacharacters and still runs the literal search, guaranteeing
zero results. It never once retried with `searchMode: regex` — it rephrased the pattern
instead, so the same failure recurred three times.

### Fix options

1. Make `searchMode` a **required** parameter. Directly supported by the
   `feedback_prefer_mandatory_params_to_close_footgun_roundtrips` working agreement: an
   optional param whose default causes a known failure mode just relocates the failure.
2. Or auto-promote to regex when metacharacters are detected, returning results plus a
   note. (Weaker — silent mode-switching is its own hazard.)
3. Minimum: reword so the corrective action is the first clause, not the third sentence.

---

## A5 — `Member` fails on a generic container name

**Severity:** Medium — one wasted turn; recurrence of a known gap.

Turn 19 vs turn 20, identical except `containerName`:

```
Turn 19: containerName "EngineResultWrapper<T>"  -> FAILED
  {"errorCode":"Exception",
   "message":"Member: no change produced for '...EngineResultWrapper.cs' (TargetNotFound).
              // Container not found."}

Turn 20: containerName "EngineResultWrapper"     -> succeeded (changeId 56c0becb)
```

The model wrote the type's real declared name, including its type parameter. That was
rejected; the arity-stripped name worked. This is the same `containerName` gap recorded in
memory `project_qwen36_35b_smoketest_and_member_containername_gap` (noted as hit twice
there — this is a third).

Secondary defects in the same message:
- `errorCode` is `"Exception"` for what is an ordinary "target not found" condition, not a
  crash. Contradicts the `feedback_agent_friendly_error_messages` agreement.
- `// Container not found.` is appended comment-style with no indication of *which*
  containers **were** found — the single most useful thing to return here.

### Fix

Accept `Foo<T>`, `Foo<TKey,TValue>`, and backtick-arity `Foo\`1` by normalizing to the
metadata name before lookup. On miss, list the container names actually present in the file.

---

## A6 — `CreateFile` blocks the documented `ReplaceSnippet` workaround

**Severity:** Medium (compounds A2).

Turn 56, after the A2 deadlock, the model tried staging a corrected copy:

```
CreateFile({"filepath":"...\\RoslynSentinel.Basic\\BuildEngine_new.cs", ...})
-> "CreateFile: this content would introduce new compiler errors — not written to disk.
    Fix the issue(s) below and retry:
    CS0101 at ...\\BuildEngine_new.cs:3: The namespace 'RoslynSentinel.Basic' already
    contains a definition for ..."
```

The compile-gate is behaving as designed, and the model's approach was poor. But combined
with A2 it means: no `WriteFile`, `ReplaceSnippet` refuses the edit, and the scratch-file
route is refused too — **no available path to a >200-char edit.** That is what turned a
recoverable situation into a terminal one.

Worth noting the gate cannot distinguish "temporary scratch file" from "intended source".
Not obviously fixable in isolation; resolving A2 removes the need for the workaround.

---

## Related known issues touched by this run

- `ReplaceSnippet` ambiguous `contextSnippet` (1 failure): *"contextSnippet is ambiguous
  (2 matches): `return new EngineResultWrapper<BuildResult>(EngineOutcome.Succes…`"* —
  the sibling-near-identical-anchor problem from
  `project_diffengine_anchor_collision_duplicate_trivia`.
- `ReadFile` guessed `EngineErrorCode.cs`; the enum actually lives in
  `EngineResultWrapper.cs`. The error correctly redirected to `ListSolutionItems`. Not a
  defect — recording it because it argues for `LocateSymbol`-first guidance
  (cf. `project_docCommentId_description_gap`).
- One `ReplaceSnippet` rejected for introducing `CS1739` (`BuildResult` no longer has a
  `BuildSucceeded` parameter) — the compile-gate working correctly, mid-refactor.

## Suggested priority

| # | Issue | Severity | Effort |
|---|---|---|---|
| A1 | `ProjectDoc` wrong-document | Critical | Low |
| A2 | Size-limit → unexposed `WriteFile` | Critical | Low |
| A3 | `WrapRange`/`UndoLastApply` blob gap | High | Medium |
| A4 | `searchMode` literal override | Medium | Low |
| A5 | `Member` generic `containerName` | Medium | Low |
| A6 | `CreateFile` scratch-file gate | Medium | — (mooted by A2) |

A1 and A2 together account for the run's failure. Both are low-effort.
