<!-- spec-eval-defect-remediation-v2.md -->
# Spec — Eval Defect Remediation (Build Truthfulness, Trivia/EOL Preservation, Finding Routing)

**Version:** v2
**Date:** 2026-09-08
**Status:** Ready for implementation, with corrections below
**Changes from v1:** Added the Evidence Corpus index and file+line citations for every observed defect. No design changes.
**Corrections (2026-09-08, post-review):** This spec was authored from eval logs/transcripts only, without direct access to the RoslynSentinel source. Its diagnosis of *what the model saw* is accurate — every transcript claim checked out verbatim against the corpus. Its assumptions about *internal implementation* were guesses made without reading the code, and several are wrong in ways that would block a literal implementation. See the "Verify against the codebase" section below, now filled in with actual answers, and the note on Phase 2 in the Observed section.

---

## Evidence Corpus

Server version `15.0.0.0`, build `2026-08-13T14:54:26Z`. 46 tools exposed. Model: `qwen/qwen3.6-35b-a3b` via LM Studio at `http://192.168.1.113:1234/v1/responses`, GTX 1080. Captured 2026-09-08.

The uploaded filenames are munged by the upload path. The **Ref** column is the short name used throughout this spec; rename the archived copies to match before an agent works from it.

| Ref | Test leg | Log file (lines) | Transcript file | Turns |
|---|---|---|---|---|
| `MIN` | `WholeFileRewriteAgentTests.Model_FixesWholeFileRewriteBug_MinimalGuidanceDisambiguated` | `agent.log` (1686) | `transcript.json` | 13 |
| `PLAN` | `PlanImplementVerifyAgentTests…_PlanImplementVerify` — *plan* leg | `1788897809428_agent__2_.log` (1267) | `1788897809426_transcript.json` | 7 |
| `IMPL` | same test — *implement* leg | `1788897809428_agent__1_.log` (641) | `1788897809428_transcript__2_.json` | 6 |
| `VERIFY` | same test — *verify* leg | `1788897809427_agent.log` (626) | `1788897809427_transcript__1_.json` | 3 |

Fixture roots differ per leg — `MIN` uses `RoslynSentinelTests_f29caa63-…` (**LF line endings**), the `PlanImplementVerify` legs use `RoslynSentinelTests_3510bfab-…` (**CRLF line endings**). That difference is itself load-bearing; see 2.5.

**`BlockEditHelpers.cs` is eval-fixture content, not a RoslynSentinel source file.** It does not exist anywhere in this repository. It is a string constant (`HelperFileContent`, `RoslynSentinel.Tests.ModelEval/Fixtures/WholeFileRewriteReproducer.cs:20-34`) that the eval harness materializes at runtime into a disposable temp workspace at `ContosoOrders.Core/FixtureHelpers/BlockEditHelpers.cs`, standing in for a generic production file (per that fixture's own doc comment, lines 14-18). Anyone implementing Phase 2 should not go looking for this file in `RoslynSentinel.*` — the 28→22 line-count evidence is real and reproducible from the logs, but the file itself only exists inside a given eval run's temp directory.

**Citation format used below:**
- Log: `MIN:1178` = line 1178 of that leg's log file.
- Transcript: `IMPL T5 · Build · ResultJson` = `Turns[4].ToolCalls[0].ResultJson` (`TurnNumber` is 1-based; array index is `TurnNumber - 1`).

A note on reading this corpus: `qwen3.6-35b-a3b` is materially stronger than the Gemma-4 baseline this project targets. Two of the defects below (1, 3) were **survived by model competence, not handled by the substrate**. Treat any transcript moment where the model recovered on its own as an unhandled defect, not a pass.

---

## Purpose

Three defects surfaced by the 2026-09-08 eval corpus, all in the same class: **the substrate produced a verdict the model could not verify, and in two cases actively contradicted evidence the model had already noticed.**

| Phase | Defect | Severity |
|---|---|---|
| 1 | `Build` reports `buildSucceeded: true` without compiling anything | P0 — invalidates every eval result |
| 2 | `ChangeAccessibility` destroys leading trivia (XML doc comments) and injects foreign EOL | P1 — reproduced 2/2 runs |
| 3 | Orientation breaker and `DiffHunkAnalyzer` findings are logged server-side and dropped from the wire | P2 — two working detectors, zero model-visible output |

All three are substrate-side. None are prompt problems.

---

## Do NOT

- **Do not** change the `Build` tool's *parameter* surface (`level`, `scope`, `scopeName`, `reason`). Only the result envelope changes.
- **Do not** patch trivia preservation per-tool. If `ChangeAccessibility` and `ModifyModifier` share a member-rewrite path, fix the shared path once. Per-tool patches recreate the defect in the next tool added.
- **Do not** silently coerce `buildSucceeded` semantics. Removing the field is correct; redefining it in place is a silent wire break that the model cannot detect.
- **Do not** add a `skipValidation`-style bypass on any of the new gates. Prior evidence (`skipPrecheck` on `RemoveMember`) shows bypass flags on safety gates get exploited.
- **Do not** implement Phase 3's finding plumbing as a second envelope. It routes through `EngineResultWrapper<T>` → `ToToolResponse()` (see Verify §2 — this means *adding* a `Findings` slot and a real `ToToolResponse()` to that type, since neither exists today) or it does not ship.
- **Do not** touch engine method signatures outside the ones named here — engine consolidation is still pending and signatures should not be edited twice.

---

## Verify against the codebase before starting

Resolved 2026-09-08 by reading the actual source. Original questions kept for traceability; answers below each.

1. **What `level: "quickBuild"` actually invokes** — in-memory `Compilation.GetDiagnostics()`, MSBuild, or a stub.
   **Answer: in-memory only, confirmed.** `Build` (`SentinelWorkspaceTools.cs:1093`, `[McpServerTool(Name = "Build")]`) routes `quickBuild` to `RefactoringEngine`'s sibling `BuildEngine.RunQuickBuildAsync` (`RoslynSentinel.Basic/BuildEngine.cs:18-77`), which calls `_diagnosticEngine.Get{File,Project,Solution}DiagnosticsAsync` — no `Process` object anywhere in the method. `ExitCode: -1` (`BuildEngine.cs:66`) is a **hardcoded literal**, not read from anything — it is `-1` on every quickBuild call regardless of outcome, which is why it never varies in the corpus. `BuildSucceeded: summary.Errors == 0` (`BuildEngine.cs:64`) never checks whether any project was actually examined — confirming Phase 1.5's gate targets a real, currently-absent check. Contrast: `RunFullBuildAsync` (`BuildEngine.cs:83-202`, the `fullBuild`/msbuild level) genuinely spawns `dotnet build` and reads its real `process.ExitCode` (line 190) — so `fullBuild` is unaffected by this defect; only `quickBuild` fabricates.
2. **Whether `EngineResult<T>` already has a `Findings` slot.**
   **Answer: that type name doesn't exist.** The real type is `EngineResultWrapper<T>` (`RoslynSentinel.Common/EngineResultWrapper.cs:13`), with exactly three members: `Outcome` (`EngineOutcome`), `Error` (`EngineError?`), `Data`. No `Findings` slot, no list-of-anything. Also: `ToToolResponse()` — the method Phase 3.2 wants to extend — currently exists only on `EngineError` (`EngineResultWrapper.cs:95-103`, a fixed 3-field anonymous object), not on the wrapper itself. Phase 3 needs to **add** a `Findings` property to `EngineResultWrapper<T>` and **build** a real `ToToolResponse()` on it — this is new surface, not an extension point waiting to be used. Update every code reference below from `EngineResult<T>` to `EngineResultWrapper<T>`.
3. **Whether `Directive` / `ToolHint` / `FailureRouter` from `spec-operation-outcome-routing-v1.md` are implemented** or still spec-only.
   **Answer: implemented, in commit `5dbeae071`** ("Implement spec: Operation Outcome Classification + ToolGraph-Routed Failure Hints (v1)", 2026-06-25) — **but scoped to the `UpliftCallers`/Asyncify tool family only**, not generalized. That commit also moved the spec file itself to `docs/obsolete/spec-operation-outcome-routing-v1.md` (it is no longer in `docs/current/`). Confirmed present and real:
   - `FailureRouter` (`RoslynSentinel.Common/FailureRouter.cs:10-212`) — implemented, tested (`RoslynSentinel.Tests.Asyncify/OperationOutcomeRoutingTests.cs`, 12 tests covering all 7 spec §6 requirements).
   - `ToolGraph` (`RoslynSentinel.Common/ToolGraph.cs:19-110`) — implemented. Note: companion `ToolHints.OnSuccess`/`OnMissingInput` (`ToolGraph.cs:112-125`) are still `TODO` stubs returning `null` — the graph exists but not every planned consumer does.
   - `ToolHint` — real record, constructed at `FailureRouter.cs:45-51`.
   - `OperationOutcome` enum (`RoslynSentinel.Common/OperationOutcome.cs:30-37`) — implemented, matching Phase 1 §1.7's forward reference exactly (`CompletedFully`/`CompletedWithNoOps`/`PartialProgress`/`NoProgress`/`NothingToDo`). **§1.7 applies as written: `BuildOutcome` should route through this enum rather than as a parallel one, if that composition is straightforward — otherwise keep `BuildOutcome` separate and note the divergence.**
   - **`Directive` is a plain `string`** (`RoslynSentinel.Common/OperationSummary.cs:26`, also `BatchTypes.cs:89`, `BreakerStatusReport.cs:7`), not an enum or comparable typed value. **Phase 3.2/3.4's code sketches (`Directive != Proceed`, "Suggested value: `ReviewRequired`") assume a type that does not exist.** Before Phase 3 ships, either promote `Directive` to a real enum (touching all three existing call sites) or have Phase 3 compare against string constants explicitly — pick one and say so; do not leave it implicit.
   - **Net effect on Sequencing §3**: Phase 3 is not blocked on landing `spec-operation-outcome-routing-v1.md` — it already landed. What Phase 3 actually depends on is *generalizing* this Asyncify-scoped machinery to `EngineResultWrapper<T>` and to `Build`/`ChangeAccessibility`/`ApplyUnifiedDiff`. Treat that generalization as in-scope Phase 3 work, not a prerequisite spec.
4. **Whether `ChangeAccessibility` and `ModifyModifier` share a rewrite path.**
   **Answer: yes, confirmed** — both call `ReplaceNodeFormattedAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs:62-81`): `ChangeAccessibilityAsync` at line 2953, `AddModifierAsync` at line 3036, `RemoveModifierAsync` at line 3113. The helper has 23 call sites total (also `Member`, `ModifyAttribute`, `ModifyEnum`, etc.) — Phase 2's "fix the shared path once" premise is architecturally correct and its blast radius is larger than the two pilot tools, which the Pilot Scope section already anticipates. **See the amended Observed note below — this method already contains trivia-preservation logic, and the bug is very likely a regression in that logic from one day before the eval, not an absent feature.**
5. **Exact `DataTag` member names** (`DataTag.ProjectName`, `DataTag.SearchPattern`).
   **Answer:** `DataTag` is a real 60-member enum (`RoslynSentinel.Common/DataTag.cs:3-70`). `DataTag.ProjectName` exists exactly as named (line 7). `DataTag.SearchPattern` **does not exist** and must be added — no existing member is a natural fit (`ContextSnippet` is the closest but means something else). This was already flagged as a placeholder in the original spec; confirming it's a real gap, not a naming slip.

---

## Phase 1 — `Build` must not fabricate a verdict

### Observed

Identical payload in every leg that called `Build` (the `PLAN` leg is planning-only and has no `Build` call):

| Ref | Log line | Transcript | Duration | `exitCode` | `workspaceVersion` |
|---|---|---|---|---|---|
| `MIN` | `MIN:1176–1178` | `MIN T12 · Build · ResultJson` | `00:00:00.0674430` | `-1` | 4 |
| `IMPL` | `IMPL:277–279` | `IMPL T4 · Build · ResultJson` | `00:00:00.0512156` | `-1` | 2 |
| `VERIFY` | `VERIFY:99` | `VERIFY T2 · Build · ResultJson` | `00:00:00.1073165` | `-1` | **0** |

```json
{"buildSucceeded":true,"level":"quickBuild","exitCode":-1,
 "errorCount":0,"warningCount":0,"errors":[],"warnings":[],
 "errorSummary":[],"warningSummary":[],"duration":"00:00:00.0281372"}
```

A `quickBuild` of `ContosoOrders.Core` completing in 51 ms with a process exit code of `-1` is not a compile. `VERIFY` reporting `workspaceVersion: 0` while claiming a successful build of a workspace that had been mutated twice is a second, independent tell.

**The model caught it and the envelope overrode it** — `IMPL:316`, reasoning field, verbatim:

> *"Wait, exitCode is -1 but errorCount is 0 and buildSucceeded is true. That's a bit odd, but the build succeeded with no errors or warnings."*

It then reported "Build succeeded (0 errors, 0 warnings)" to the user (`IMPL:317`), and carried that into its final summary (`IMPL:620`, `IMPL:636`). This is the failure mode the whole architecture exists to prevent: a correct model inference discarded because a boolean outranked the field contradicting it.

Note also `MIN T12 · Build · ArgumentsJson` (`MIN:1174`) and `VERIFY T2` — the `VERIFY` task text explicitly instructed the agent to *"Verify this yourself with an MCP build tool… do not take it on faith."* It complied, and the tool lied to it.

### Changes

**1.1 — Introduce a three-state outcome.** A boolean cannot represent "nothing was verified".

```csharp
// v1
public enum BuildOutcome
{
    Succeeded,
    Failed,
    NotRun
}
```

**1.2 — Replace `buildSucceeded` with `outcome`.** Remove the boolean entirely. A model that has learned to read `buildSucceeded` will get a missing-field error rather than a stale-semantics true.

**1.3 — Remove `exitCode` unless a process was launched.** A process exit code emitted by an in-memory Roslyn compilation is a lie by field name. If `method` is not `"msbuild"`, the field must be absent, not `-1`.

**1.4 — Add provenance and a positive-completeness stamp.**

```csharp
// v1
public sealed record BuildResult
{
    public required BuildOutcome Outcome { get; init; }
    public required string Level { get; init; }
    public required string Method { get; init; }          // "roslynCompilation" | "msbuild"
    public required List<string> ProjectsCompiled { get; init; }
    public required bool DiagnosticsComplete { get; init; }
    public required int ErrorCount { get; init; }
    public required int WarningCount { get; init; }
    public required List<BuildDiagnostic> Errors { get; init; }
    public required List<BuildDiagnostic> Warnings { get; init; }
    public required TimeSpan Duration { get; init; }
    public int? ExitCode { get; init; }                   // null unless Method == "msbuild"
}
```

`ProjectsCompiled` is the load-bearing field: it is the substrate stating what it actually looked at, rather than the model inferring it from an absence of errors.

**1.5 — Gate on empty compilation set.** This is the actual fix; everything above is shape.

```csharp
// v1 — first thing after resolving scope, before assembling the result
if (projectsCompiled.Count == 0)
{
    return EngineResultWrapper<BuildResult>.Failure(
        new EngineError(
            EngineErrorCode.BuildNotRun,
            $"Build did not compile anything. Scope '{scope}' with scopeName '{scopeName}' " +
            $"resolved to zero projects. No compile verdict is available — do not treat this as success.",
            DataTag.ProjectName),
        payload: new BuildResult
        {
            Outcome = BuildOutcome.NotRun,
            // ... remaining fields ...
        });
}
```

The payload is still returned on failure so the model can see `ProjectsCompiled: []` rather than only reading prose.

**1.6 — `NotRun` routes.** Register `BuildNotRun` in the `FailureRouter` with a `ToolHint` for `ListSolutionItems` (or whichever tool enumerates project names) with `kind` pre-filled, so the model is handed the way to discover the correct `scopeName` rather than guessing.

**1.7 — Structural note.** `NotRun` is the same argument as `AlreadySatisfied` in `spec-operation-outcome-routing-v1.md`: an outcome that is neither success nor an actionable failure, and which must be structurally distinct rather than collapsed into a boolean. If the `OperationOutcome` enum has landed, `BuildOutcome` should be expressed through it instead of as a parallel enum.

---

## Phase 2 — Trivia and EOL preservation in the shared member-rewrite path

### Observed

**Doc comment destruction, reproduced 2/2 legs that called `ChangeAccessibility`.** `BlockEditHelpers.cs` went 28 → 22 lines in both. Only the accessibility modifier was requested.

| Ref | Call | Before (`totalLines`) | After (`totalLines`) |
|---|---|---|---|
| `MIN` | `MIN:529–531` / `MIN T4 · ChangeAccessibility` | 28 — `MIN:229` / `MIN T3 · ReadFile · ResultJson` | 22 — `MIN:1147` / `MIN T11 · ReadFile[1] · ResultJson` |
| `IMPL` | `IMPL:199–201` / `IMPL T2 · ChangeAccessibility` | 28 — `IMPL:144` / `IMPL T1 · ReadFile[0] · ResultJson` | 22 — `IMPL:329` / `IMPL T5 · ReadFile[1] · ResultJson` |

Diff the `data.source` strings at those four transcript locations to reproduce:

```
before:
    /// <summary>
    /// Replaces <paramref name="oldBlock"/> with <paramref name="newBlock"/> inside
    /// <paramref name="fileText"/>, re-indenting only the replacement block to match the
    /// surrounding indentation — everything else in fileText is returned byte-for-byte
    /// unchanged.
    /// </summary>
    private static string ReplaceBlockFormatted(string fileText, string oldBlock, string newBlock)

after:
    internal static string ReplaceBlockFormatted(string fileText, string oldBlock, string newBlock)
```

**EOL injection — `MIN` only, because it is the only LF fixture.**

| Read | Citation | CRLF | bare LF |
|---|---|---|---|
| `BlockEditHelpers.cs` before | `MIN:229` / `MIN T3 · ReadFile · ResultJson` | 0 | 27 |
| `BlockEditHelpers.cs` after | `MIN:1147` / `MIN T11 · ReadFile[1] · ResultJson` | **1** | 20 |

The single injected CRLF lands on the `{` line immediately preceding the edited member. The `PlanImplementVerify` fixture is CRLF throughout (`IMPL T1 · ReadFile[0]` → 27 CRLF, 0 bare LF), which masked the defect entirely across three of the four legs. Consistent with Roslyn `SyntaxFactory` emitting `Environment.NewLine` for synthesized trivia on Windows.

Reproduce by counting `\r\n` versus bare `\n` in the `data.source` field at the two citations above.

**Both defects were then excused by the model, on the strength of the system prompt.** `IMPL:610` (turn 6 reasoning): *"I see the file has an extra blank line where `ReformatWholeFile` was deleted. That's just a tool artifact — no logic changed."* And `VERIFY T3 · Content` answered "yes, only the accessibility changed" to review criterion 3 while reading a file with six lines of API documentation missing.

**Root cause, identified post-hoc (2026-09-08 review) — read before writing 2.1.** `ChangeAccessibility` and `ModifyModifier` already route through a shared helper, `ReplaceNodeFormattedAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs:62-81`), and that helper **already contains trivia-preservation logic**:

```csharp
// current code, RefactoringEngine.cs:69-77
var newNodeLeadingTrivia = newNode.GetLeadingTrivia();
var newNodeTrailingTrivia = newNode.GetTrailingTrivia();
var oldNodeLeadingTrivia = oldNode.GetLeadingTrivia();
var oldNodeTrailingTrivia = oldNode.GetTrailingTrivia();

var leadingTrivia = newNodeLeadingTrivia.Count > 0 ? newNodeLeadingTrivia : oldNodeLeadingTrivia;
var trailingTrivia = newNodeTrailingTrivia.Count > 0 ? newNodeTrailingTrivia : oldNodeTrailingTrivia;
```

This is **not the original code**. Before commit `1d2d0bf86` (2026-09-07, one day before this corpus was captured), the method unconditionally transplanted `oldNode`'s trivia onto `newNode`. The 09-07 commit changed it to the `Count > 0` conditional above, explicitly to let `RemoveSummaryCommentAsync` intentionally strip doc comments without them being re-added by this helper (commit message: *"Improve trivia preservation in ReplaceNodeFormattedAsync"*). `Count > 0` is too coarse a signal for "this trivia was intentionally set" versus "this trivia happens to be non-empty for incidental reasons" — e.g. `ChangeAccessibilityAsync` (`RefactoringEngine.cs:2951`) builds each new modifier token via `SyntaxFactory.Token(k).WithTrailingTrivia(SyntaxFactory.Space)`, which can leave `newNode`'s trivia non-empty for reasons unrelated to doc-comment intent, tripping the wrong branch and silently discarding `oldNode`'s doc comment.

**This means the doc-comment-loss bug reproduced in this corpus is very likely a regression introduced by the 09-07 fix, one day before capture — not a from-scratch absence of trivia handling.** Do not write `PreserveSurroundingTrivia` (2.1) as new, additive logic next to this method; it will sit unused beside the real bug. Instead: fix or replace the `Count > 0` heuristic in `ReplaceNodeFormattedAsync` itself — e.g. an explicit `TriviaEditIntent` flag passed by call sites that genuinely mean to change trivia (like `RemoveSummaryCommentAsync`), defaulting to "preserve old trivia" for every other caller including `ChangeAccessibility`/`ModifyModifier`. Confirm with a live repro (a member with a doc comment, through `ChangeAccessibility`) before and after any fix — static reading alone did not fully resolve why the specific single-accessibility-keyword case in the corpus trips the bad branch, since the new first token's *leading* trivia should normally be empty; the trailing-trivia branch or a multi-modifier ordering interaction is the more likely trigger and needs a debugger/test, not further static analysis.

Also prior art exists for 2.2's EOL-detection mechanism: `DiffEngine.cs:85-93` (the `ApplyDiff`/`ApplyUnifiedDiff` write path) already does dominant-EOL detection and preservation. It's just not applied to `ReplaceNodeFormattedAsync`. Reuse rather than reinvent.

### Changes

**2.1 — Single trivia-preserving replacement helper.** One site, not per-tool.

```csharp
// v1
internal static TNode PreserveSurroundingTrivia<TNode>(TNode original, TNode replacement)
    where TNode : SyntaxNode
{
    TNode withLeading = replacement.WithLeadingTrivia(original.GetLeadingTrivia());
    TNode withBoth = withLeading.WithTrailingTrivia(original.GetTrailingTrivia());
    return withBoth;
}
```

Modifier-only edits must go through this. Rewriting a modifier list must never reconstruct the member declaration from scratch.

**2.2 — Detect the document's dominant EOL and normalize at the write point.**

```csharp
// v1
internal static string DetectDominantEol(string documentText)
{
    int crlf = 0;
    int index = documentText.IndexOf("\r\n", StringComparison.Ordinal);
    while (index >= 0)
    {
        crlf++;
        index = documentText.IndexOf("\r\n", index + 2, StringComparison.Ordinal);
    }

    int totalLf = 0;
    foreach (char c in documentText)
    {
        if (c == '\n')
        {
            totalLf++;
        }
    }

    int bareLf = totalLf - crlf;
    if (bareLf > crlf)
    {
        return "\n";
    }

    return "\r\n";
}
```

Apply normalization once, at the single write site, after all node rewriting — not inside individual engines.

**2.3 — Post-write invariant check, emitted as a finding rather than swallowed.** Two assertions on every mutating write:

- **EOL homogeneity:** if the document had a single EOL style before the write and has mixed styles after, emit a finding.
- **Leading-trivia retention:** if the target member had leading trivia before the write and has strictly less after, and the operation was not an explicit trivia edit, emit a finding.

These findings route via Phase 3. They must **not** be logged and dropped, which is exactly the defect Phase 3 fixes.

**2.4 — Narrow the eval system prompt's normalization clause.** Current text:

> *"the tool pipeline may normalize incidental whitespace/indentation or add its own short attribution comment on lines it writes — that is not something you caused and not something to fix"*

This clause is currently laundering substrate damage. The judge leg answered "yes, that is the only change" to criterion 3 while reading a file with six lines of API documentation missing. Replace with wording scoped to **trailing whitespace and indentation depth only**, and state explicitly that removal of comments, doc comments, or blank-line separators is a defect to be reported, not excused.

**2.5 — Convert one eval fixture to LF.** `PlanImplementVerify` is CRLF-only and cannot detect 2.2. Either parameterise the fixture over both EOL styles or convert one leg to LF permanently.

### Pilot scope

`ChangeAccessibility` and `ModifyModifier`. Extend to `ConstructorParameter` and `Member` only after the pilot's tests are green — `ConstructorParameter` has a known independent trivia defect (misplaced constructor) that should not be conflated with this one.

---

## Phase 3 — Route suppressed findings to the model

### Observed

Two detectors fired correctly server-side. Neither reached the wire.

**Orientation breaker — `PLAN:123`:**

```
05:26:07.449 [Warning] RoslynSentinel.Common.IWorkspaceManager:
  Orientation breaker TRIPPED after 3 consecutive zero-match SearchSolutionText calls.
```

The three triggering calls, all in turn 2 (`PLAN T2 · ToolCalls[0..2]`), with fabricated names:

| Pattern | Log |
|---|---|
| `ReformatOnlyChangedBlock` | `PLAN:104–109` |
| `ReformatOnly` | `PLAN:112–117` |
| `ReindentOnlyChanged` | `PLAN:120–126` |

The breaker fires at `PLAN:123`, between the exception (`PLAN:120`) and the MCP completion (`PLAN:124`). The result actually delivered to the model is at `PLAN:126` / `PLAN T2 · ToolCalls[2].ResultJson` — **byte-identical to the first two**, generic `NoMatches` prose from `NoSearchMatchesException`, thrown at `WorkspaceReadNavigationImpl.cs:510` (stack trace at `PLAN:106`, `PLAN:114`, `PLAN:122`). No breaker signal on the wire at all.

The model recovered by independently reaching for `ListAll(kind: "method")` (`PLAN:137` / `PLAN T3`), a tool the `NoMatches` hint text does not mention. That is model strength, not architecture — the same sequence in `MIN T2` (`MIN:209`, `MIN:217`) produced two fabricated patterns after one lucky hit.

**DiffHunkAnalyzer — `IMPL:246`:**

```
05:29:18.580 [Warning] RoslynSentinel.Common.DiffEngine:
  ApplyDiff succeeded but its diff has findings: DiffHunkAnalyzer: 1 hunk(s).
```

Fires between the diff application and the MCP completion at `IMPL:251`. The result delivered to the model (`IMPL:253` / `IMPL T3 · ApplyUnifiedDiff · ResultJson`) is:

```json
{"success":true,"succeededFiles":["…BlockConverter.cs"],"failedFiles":{},
 "summary":"Applied 1 changes successfully (0 delete(s)). 0 failures.",
 "workspaceInSync":true,"workspaceVersion":2,
 "validationResult":{"success":true,"diagnostics":[]}}
```

The analyzer's finding is nowhere in it. It corresponds to the stray double blank line left where `ReformatWholeFile` was deleted — visible in `IMPL T5 · ReadFile[0] · ResultJson` (`IMPL:324`), and rationalised away by the model at `IMPL:610`.

### Changes

**3.1 — `OperationFinding` type, carried on `EngineResultWrapper<T>`.**

```csharp
// v1
public sealed record OperationFinding
{
    public required string Code { get; init; }        // "OrientationBreakerTripped", "DiffHunkAnomaly", "MixedLineEndings", "TriviaLoss"
    public required string Message { get; init; }
    public required FindingSeverity Severity { get; init; }
    public DataTag? Tag { get; init; }
}

public enum FindingSeverity
{
    Informational,
    Warning
}
```

`EngineResultWrapper<T>` gains `public List<OperationFinding> Findings { get; init; }`, defaulting to empty. Note this type has no `ToToolResponse()` today (see Verify §2) — it needs to be added, not extended; `EngineError.ToToolResponse()` is a separate, narrower existing method and is not the extension point.

**3.2 — `ToToolResponse()` surfaces findings.** Single wire-assembly point, as always. When `Findings` is non-empty:

- Emit a `findings` array in the payload.
- Emit a substrate-computed `directive`. A success carrying warning-severity findings is **not** a clean success; the directive states the verdict so the model is not left inferring one. Suggested value: `"ReviewRequired"` with the specific next action, distinct from the `"Proceed"` a clean success emits. **`Directive` is currently a bare `string`** (`OperationSummary.cs:26` and two other call sites, see Verify §3) — decide explicitly whether to promote it to an enum (touching those three sites) or compare against string constants here, and record the decision; don't leave the type ambiguous between this spec's `!= Proceed` phrasing and the field's actual type.

**3.3 — Wire the orientation breaker.** When the breaker trips, the failing `SearchSolutionText` result must carry a finding plus a `ToolHint`, replacing the generic prose:

```csharp
// v1 — shape only; resolve the ToolHint through ToolGraph, do not hand-author it
findings.Add(new OperationFinding
{
    Code = "OrientationBreakerTripped",
    Message = "Three consecutive SearchSolutionText calls returned zero matches. " +
              "The names being searched for do not exist in this solution. " +
              "Stop guessing names — enumerate instead.",
    Severity = FindingSeverity.Warning,
    Tag = DataTag.SearchPattern
});
```

The accompanying `ToolHint` must point at `ListAll` with `kind: "method"` pre-filled. Note that the existing `NoSearchMatchesException` prose recommends `LocateSymbol` (requires the exact name) and `GetFileOutline` (requires the file) — **neither is usable by an agent that does not know the name.** `ListAll` is what actually worked in the plan leg and is absent from the hint. Add it to the base `NoMatches` message as well, independently of the breaker.

**3.4 — Wire `DiffHunkAnalyzer`.** `ApplyUnifiedDiff` currently returns `validationResult: {"success":true,"diagnostics":[]}` while the analyzer has findings. Either the analyzer's findings populate `validationResult.diagnostics`, or they populate `Findings` — not neither. Recommended: `Findings`, with `Status = Success` and `Directive = ReviewRequired`, so the diff still applies but the model is told to re-read rather than to trust.

**3.5 — Audit for other log-only detectors.** Grep for `[Warning]`-level emissions in `RoslynSentinel.Common` and `RoslynSentinel.Server.Basic` that have no corresponding wire field. Every one is a substrate-computed verdict being withheld from the model. The two above were found by reading four transcripts; assume there are more.

---

## Sequencing and build gates

1. **Phase 1 first, alone.** Until `Build` compiles for real, every eval result — including the ones validating Phases 2 and 3 — is unfalsifiable. Build and run the full ModelEval suite after Phase 1 and record which tests go red. Expect regressions; that is the deliverable.
2. **Phase 2 second.** Requires Phase 1 green so that a genuine compile failure is distinguishable from a fabricated pass. Ship 2.1–2.3 and 2.5 together; 2.4 is a prompt edit and can land independently.
3. **Phase 3 last.** `spec-operation-outcome-routing-v1.md` already landed (commit `5dbeae071`, 2026-06-25; the spec itself now lives in `docs/obsolete/`) — `ToolHint`/`FailureRouter`/`ToolGraph`/`OperationOutcome` all exist and are tested. It is scoped only to `UpliftCallers`/Asyncify, though, and `Directive` there is a bare `string`, not the enum this spec's code implies. Phase 3's real dependency is generalizing that existing machinery to `EngineResultWrapper<T>` and to `Build`/`ChangeAccessibility`/`ApplyUnifiedDiff` — treat that as Phase 3's own first step, not a separate spec to wait on.
4. Build must be clean after each phase before starting the next. Do not batch.

---

## Minimum test cases

### Phase 1

| # | Test | Expectation |
|---|---|---|
| 1.1 | `Build(scope: "project", scopeName: "ContosoOrders.Core")` on a valid project | `Outcome == Succeeded`, `ProjectsCompiled` contains `ContosoOrders.Core`, `DiagnosticsComplete == true` |
| 1.2 | `Build` with a `scopeName` that matches no project | `Outcome == NotRun`, `EngineResultWrapper.Outcome == Failure`, `ProjectsCompiled` empty, `ToolHint` present |
| 1.3 | `Build` after introducing a deliberate `CS0103` | `Outcome == Failed`, `ErrorCount >= 1`, error present in `Errors` |
| 1.4 | `Build` with `Method == "roslynCompilation"` | `ExitCode` is absent from the serialized payload |
| 1.5 | Serialization test | `buildSucceeded` does not appear in any `Build` payload |

### Phase 2

**Before writing 2.1, add a 2.0: reproduce the doc-comment loss live** (`ChangeAccessibility` on a member with an XML doc comment, single accessibility modifier, through the actual tool) and step through `ReplaceNodeFormattedAsync`'s `Count > 0` branches with a debugger to confirm which branch fires and why — see the root-cause note above. Do this before deciding whether 2.1 is a new helper or a fix to the existing one; the two are different amounts of work and touch a method with 23 call sites.

**Commit history behind these helpers, for context on tests 2.6–2.9 below.** Two commits, back to back, both to `RefactoringEngine.cs`, both shipped with zero new tests:
- `208a1c4` (2026-09-07 00:21, "Fix Member replace/remove dropping blank lines and reformatting siblings") — made `ReplaceNodeFormattedAsync` unconditionally transplant `oldNode`'s trivia onto `newNode`, and rewrote `RemoveNodeFormattedAsync` to use `KeepExteriorTrivia` with explicit boundary-token restoration instead of reformatting the whole container/siblings. Directionally correct, and the general approach (format only the touched region) matches this spec's own preference for scoped writes — but shipped with no regression test for the blank-line/sibling-preservation behavior it claims to fix, for either of its two call sites (`Member` remove, `ConstructorParameter` remove).
- `1d2d0bf86` (2026-09-07 23:24, ~23 hours later) — `208a1c4`'s unconditional trivia transplant in `ReplaceNodeFormattedAsync` immediately broke `RemoveSummaryCommentAsync` (`RefactoringEngine.cs:3367`), which relies on that same helper to *intentionally* strip doc-comment trivia. This commit added the `newNodeLeadingTrivia.Count > 0 ? new : old` conditional to fix that — which is very likely the direct cause of the `ChangeAccessibility` doc-comment-loss regression this eval corpus caught the next day (`Count > 0` can't distinguish "trivia intentionally set" from "trivia incidentally non-empty," e.g. from `SyntaxFactory.Token(k).WithTrailingTrivia(SyntaxFactory.Space)`).

Net effect: two same-file, same-helper-family, untested commits in one day, the second one fixing a regression the first one caused, and the second commit's own fix is now the eval-caught bug this spec's Phase 2 addresses. Tests 2.6–2.9 close both original gaps so a third pass through this code doesn't repeat the pattern.

| # | Test | Expectation |
|---|---|---|
| 2.1 | `ChangeAccessibility` on a member with an XML doc comment | Doc comment present and byte-identical after the write |
| 2.2 | `ChangeAccessibility` on an LF-only document | Zero CRLF sequences in the resulting file |
| 2.3 | `ChangeAccessibility` on a CRLF-only document | Zero bare LF sequences in the resulting file |
| 2.4 | `ModifyModifier` on a member with leading blank line + doc comment | Both preserved |
| 2.5 | Invariant check fires | Force a trivia-losing rewrite; assert an `OperationFinding` with `Code == "TriviaLoss"` is produced |
| 2.6 | `RemoveSummaryCommentAsync` on a member whose remaining leading trivia (indentation/blank line) is non-empty after stripping the doc comment | Doc comment removed; blank line/indentation preserved — this is the case the `Count > 0` heuristic was built for in commit `1d2d0bf86` and must still pass once that heuristic is replaced |
| 2.7 | `Member(operation: "remove")` on a member with a blank line separating it from its neighbors, with untouched sibling members before/after | Removed member's gap collapses correctly; sibling members' own internal spacing/formatting is byte-identical to before the removal — regression test for commit `208a1c4`, which fixed this but shipped with no test |
| 2.8 | `Member(operation: "remove")` on the first member and, separately, the last member in a type body | No exception from missing before/after tokens; container's opening/closing brace trivia preserved — the edge case `RemoveNodeFormattedAsync`'s `hasTokenBefore`/`hasTokenAfter` guards exist for, also untested since `208a1c4` |
| 2.9 | `ConstructorParameter` removal exercising `RemoveNodeFormattedAsync` (line 2065 call site) | Same blank-line/sibling-preservation behavior as 2.7 — second call site, not just `Member` remove |

### Phase 3

| # | Test | Expectation |
|---|---|---|
| 3.1 | Three consecutive zero-match `SearchSolutionText` calls | Third result carries a `finding` with `Code == "OrientationBreakerTripped"` and a `ToolHint` naming `ListAll` |
| 3.2 | First and second zero-match calls | No breaker finding — the breaker must not fire early |
| 3.3 | `ApplyUnifiedDiff` producing a hunk anomaly | Result carries a `finding`, `Directive != Proceed` |
| 3.4 | `ApplyUnifiedDiff` with a clean diff | `Findings` empty, `Directive == Proceed` |
| 3.5 | Base `NoMatches` message | Mentions `ListAll` as a discovery path |

---

## Out of scope for this spec

Carried forward, not addressed here:

- **Result-shape standardization.** Evidence:
  - `FindReferences(kind: "callers")` → `data: []` (`MIN:1024` / `MIN T7`); `kind: "all"` → `data: {callers:[], implementations:[]}` (`MIN:1067` / `MIN T8`). Same tool, two envelopes.
  - `Member(operation: "replace")` → `{summary:{…}, changedContent}` (`MIN:980` / `MIN T6`); `operation: "remove"` → flat `{changeId, affectedFiles, description, dryRun, workspaceVersion, status, note}` (`MIN:1112` / `MIN T10`).
  - `workspaceVersion` present at `MIN T10`, absent at `MIN T6`.
  - **Measured cost: `MIN` turns 7–9** (`MIN:1024` → `MIN:1079`). Bare `[]` with no completeness stamp → model distrusted it → re-called with `kind: "all"` → still distrusted → full `ReadFile` to confirm. Three turns to establish a fact the first call answered correctly. Fold into the engine consolidation pass.
- **Positive-completeness stamps on `FindReferences`.** Same root cause as the turn burn above; already in the prioritized fix queue.
- **Concept → symbol discovery.** No fuzzy or substring symbol-name search exists. `PLAN T2` was 3/3 fabricated patterns; `MIN T2` was 2/3 (`MIN:209`, `MIN:217`) after one lucky hit on `ReplaceBlock`. 3.3's hint change is a mitigation, not the fix.
- **Harness gaps.**
  - Model identity, temperature, and context window appear nowhere in any log or transcript — grep of all four logs for model-name patterns returns nothing; only the endpoint (`http://192.168.1.113:1234/v1/responses`) is recorded. A transcript that cannot be attributed to a model is not a regression datapoint. With a capability ladder planned (see below) this becomes blocking, not cosmetic.
  - Reasoning parser leak: `VERIFY:596` / `VERIFY T3 · ModelMessage.ReasoningContent` is the literal string `"<think>\n\n"`.
  - Empty user turns injected: `MIN:1134` / `MIN T11` reasoning — *"The user is sending an empty message."*
- **Capability-ladder baseline.** This corpus is a single model (`qwen/qwen3.6-35b-a3b`). Re-running all four legs against `gemma-4-e4b` on identical fixtures, *before* any fix in this spec lands, gives the floor measurement. The delta between the two on the same fixtures is the real measure of substrate quality — a single strong model cannot produce that signal, and defects 1 and 3 above are precisely the ones it masked.
