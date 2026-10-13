# Internal WhitespaceTolerance contract for snippet resolution/lookup helpers

**Status:** PROPOSED 2026-10-13. Direction (internal-only, required parameter, no tool-schema change) was approved by the user 2026-10-13; the design, tier definitions and slicing below are awaiting review and are NOT approved for implementation.

## Motivation

Callers and helpers disagree about whitespace handling, and the only signal a caller gets is a "not found" error that does not say whitespace was the cause. A weak model then spends its turn budget on the wrong hypothesis.

### Evidence: Qwen run 20261013-191017-805-3769e300

Run folder: `RoslynSentinel-TestRuns/SubAgent/20261013-191017-805-3769e300/subagent/Logs/` (`agent.log`, `turn-N-01-ReplaceSnippet-result.json`). The task was a four-branch edit in `MemberRefactoringEngine.cs`. The run ended `RepeatedToolFailure` after 11 turns (5m06s, `agent.log:164`). Seven `ReplaceSnippet` calls (turns 2, 4, 5, 7, 9, 10, 11) all returned `NotFound`; the breaker tripped on `[ReplaceSnippet|NotFound|<file>]` across turns 9-11 (`agent.log:163`).

Cause, verified from the logged request and the worktree file bytes (I did not re-execute the call): the file has `        }` and `        var trailingIssue = ...` at 8 spaces (worktree `MemberRefactoringEngine.cs` lines 2992-2994). The turn 2 `oldContent` (`agent.log:44`) has those two lines at column 0 while keeping the other lines' indentation. `ReplaceSnippet` uses the strict matcher (`ContextHelper.FindExactSnippetPosition`, `ContextHelper.cs:301`, which runs only the ordinal `IndexOf` plus the CRLF/LF regex, `:248-287`), so it cannot match.

What the error told the model (turn 2 result, quoted):

- "`5 of 5 lines in contextSnippet were located individually ... but not in the arrangement you supplied`" -- the call was `oldContent`; the label reads `contextSnippet`.
- "`snippet line 9 was located at source line 2994`" and, in the same message, "`Closest window: ... snippet line 8 ("var trailingIssue ...") differs from source line 2993 ("")`". The same line is numbered 9 and 8 in one message, and the hint blames a blank source line.
- No mention of indentation.

Why the diagnostics were blind (traced to source):

- `GatherNoMatchEvidence` (`ContextHelper.cs:811`) compares `.Trim()`-ed lines, so leading indentation cannot differ. Its whitespace-only flag (`IsWhitespaceOnly`, consumed at `:618`) is set only when the interior-collapsed comparison matches, so `FormatAllMatchedDiagnosis` took the generic "not in the arrangement" branch (`:618-628`).
- `FormatClosestWindowHint` (`:896`) builds its needle with `snippetLines.Where(l => l.Length > 0)` (`:900`), dropping the snippet's own blank line, while the source array keeps blanks. The needle's last line is therefore compared against the file's blank line, which produces both the off-by-one numbering and the blank-line blame. `LineBlockAligner` also compares trimmed text.
- The breaker key is `tool|errorCode|file`, so seven different failed attempts look like one failure to it.

Model-side friction, recorded as observed (not resolved into a tidy story):

- Turn 5 (`agent.log:106`): "use `contextSnippet` with lineBefore/lineAfter parameters in ReplaceSnippet ... That's more robust according to the tool docs." `lineBefore`/`lineAfter` select among matches; they do not loosen matching. Which description led to this belief is untraced (hypothesis: the shared `ToolParams.ContextSnippet` text, below).
- Turn 6 (`agent.log:113`): blamed "a blank line". This matches the misleading `source line 2993 ("")` hint (hypothesis, not confirmed).
- Turn 8 (`agent.log:124`): blamed Windows line endings. No message text I read supports this; why the model arrived there is untraced.
- Turn 10 (`agent.log:151`): "not providing the exact verbatim text".
- Turn 11 sent malformed arguments (parameter tags inside `reason`, `agent.log:161`).

Four different hypotheses across five turns, none of them indentation, because no message named it. Per the failure doctrine this is an environment defect: the tool had the information (it compared trimmed lines and knew they matched) and discarded it.

### Structural cause: helpers silently encode different whitespace policies

Surveyed 2026-10-13 (callers re-counted with `Search(mode: references)` unless marked):

| Family | Helper (file:line) | Matching | Production callers |
| --- | --- | --- | --- |
| Exact | `FindExactSnippetPosition` (`ContextHelper.cs:301`), `FindAllExactSnippetMatches` (`:248`) | ordinal, plus `\r?\n` regex for multi-line | 2: `WorkspaceFileEditImpl.cs:641` (ReplaceSnippet), `:825` (ReplaceSnippetBatch). 48 test refs |
| Tolerant | `FindAllSnippetMatchesWithLength` (`:45`) | 4 stages: literal; EOL regex; single-line `\s+` collapse, Trim, OrdinalIgnoreCase, substring; N-line window, per-line Trim and collapse, OrdinalIgnoreCase | via the wrappers below |
| Tolerant | `FindAllSnippetMatches` (`:32`) | wrapper, drops length | 1: `SyntaxTargetResolver.cs:305` |
| Tolerant | `FindSnippetPosition` x2 / `FindSnippetPositionWithLength` x2 (`:178-231`) | wrappers | 16 sites of the `SourceText` overload in 9 files (the string overload and the WithLength variants were not separately counted) |
| Tolerant | `TryFindSnippetPosition` x2 (`:490`, `:507`) | non-throwing wrappers | 8 sites in 4 files (brief's count, not re-counted) |
| Tolerant | `FindSymbolAtSnippetAsync` (`:1161`) | wrapper plus symbol lookup | 6 sites in 2 files (`SymbolNavigationEngine` 5, `ImpactAnalyzer` 1) |
| Tolerant | `SyntaxTargetResolver.ResolveBySnippetOrThrow` (`SyntaxTargetResolver.cs:292`) | candidate disambiguation | 35 sites in 4 files (`MemberRefactoringEngine` 27, `SymbolNavigationEngine` 4, `BasicRefactoringEngine` 3, `AdvancedStructuralEngine` 1) |
| Dead | `FindNodeAtSnippet` (`:1138`) | -- | 0 callers, tests included |
| Other | `DiffEngine.ReanchorHunk` (`Common/DiffEngine.cs:294`) | per-line Trim, case-sensitive, no interior collapse, searches +/-60 lines (`HunkReanchorWindow`, `:32`) | -- |
| Other | `Search(mode: text)` | per line, case-insensitive; multi-line patterns never match | -- |

No helper reports which stage matched. Five inconsistencies follow from that:

1. `ReplaceSnippet` is strict, but every `contextSnippet` tool is tolerant and case-insensitive, while the shared `ToolParams.ContextSnippet` text says "copied exactly" (`ToolParams.cs:42`).
2. `ResolveBySnippetOrThrow` consults the snippet only when there is more than one name candidate (`SyntaxTargetResolver.cs:300`, guard `contextSnippet == null || candidates.Count <= 1`). A wrong snippet is silently ignored with one candidate. Its XML comment says this is deliberate ("a defensively-supplied snippet must never fail a call that didn't actually need disambiguating"), but nothing outside that comment says so.
3. The diagnostics (`GatherNoMatchEvidence`, `MatchLine` `:1077`, `LineBlockAligner`) are case-sensitive; the tolerant matcher is case-insensitive. A near-match the matcher would accept can be reported as a miss.
4. `FormatAllMatchedDiagnosis` hardcodes "ReplaceSnippet requires a byte-exact match" (`ContextHelper.cs:621`), though `DiagnoseNoMatch` is shared. It is also not literally true: the exact matcher accepts CRLF/LF interchange.
5. A tolerant-path failure reads "An exact match could not be located for the provided contextSnippet" (`ContextErrorBuilder.cs:55`), which is wrong because that path was not exact.

## Proposal

### Prior art and how this differs

- `CLOSED.md` "`ReplaceSnippet` silently corrupted adjacent lines sharing a trailing substring" (2026-09-10, heading at line 682): the tolerant path's span length differed from the snippet's, so `ReplaceSnippet` was moved to the exact matcher on purpose; `ApplyDiff` was deliberately left loose ("two separate anchoring strictness levels, by design"). This proposal keeps that decision. It does not make `ReplaceSnippet` tolerant. It makes the two levels explicit and named instead of implied by which method was called.
- `docs/current/plans/plan_replacesnippet_whitespace_tolerant_match.md`: a plan to make `ReplaceSnippet` tolerant. Never applied; rejected here (Alternatives, c).
- `docs/current/proposal_universal_symbol_resolver.md`: about candidate resolution, not text normalisation.
- `docs/current/proposals/proposal_snippet_match_selectors_withinmember_matchline.md`: the `withinMember`/`matchLine` selectors. Orthogonal; they filter matches after any tier has produced them.
- `docs/current/finding_matchstrictness_internal_param_evaluation.md` (2026-09-28): evaluated an internal `MatchStrictness` (`Strict`/`BestEffort`) parameter and did not implement it. Concluded the idea clears the premature-abstraction bar because two callers (`FindReferences`, read-only; `Member`, mutating) have opposite correctness needs, and that `Member` should always be `Strict`. Differences, stated as accurately as I can from reading it:
  - **Different axis.** That evaluation is about candidate ambiguity: pick the closest of several symbol candidates, or throw. This proposal is about text normalisation: which whitespace differences a snippet comparison forgives. The two are independent; a `Strict` candidate policy can sit on any whitespace tier.
  - **No default here.** That evaluation proposed `Strict` as the default. This proposal requires an explicit argument at every call site (memory `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`).
  - **Larger existing evidence.** Both policies already exist as hardcoded code paths today (the exact and tolerant families above), with 50+ production call sites choosing between them implicitly by method name. That evaluation was weighing a split that did not yet exist.
  - **Overlap, unresolved.** The single-candidate rule in `ResolveBySnippetOrThrow` (item 8) is partly a candidate-ambiguity question and so is in that evaluation's territory. Whether the two parameters should share a vocabulary or a type is open. I could not tell from the finding whether its author expects `MatchStrictness` to be revived.

### 1. The enum (decided: shape; tier boundaries open where marked)

`WhitespaceTolerance` in `RoslynSentinel.Common`, internal use only. An ordered ladder; each tier accepts everything the one before accepts:

| Tier | Accepts additionally |
| --- | --- |
| `Exact` | nothing: ordinal comparison, no EOL regex |
| `IgnoreLineEndings` | CR, LF and CRLF interchangeable between snippet and file |
| `IgnoreIndentation` | differing per-line leading whitespace |
| `IgnoreAllWhitespace` | per-line trim plus interior whitespace-run collapse |

LF/CRLF is a separate tier, not folded into `Exact`. Reason: today's "exact" matcher already allows it (`ContextHelper.cs:269-277`), so `Exact` as a name would be untrue, and the enum exists to stop names promising something untrue. `ReplaceSnippet` then passes `IgnoreLineEndings`, which preserves its current behaviour exactly. For a single-line snippet the two lowest tiers behave identically (the EOL regex runs only when the snippet contains `\n`).

Open: tier boundaries not fixed by the existing code.
- Whether `IgnoreIndentation` ignores trailing whitespace too. Models reproduce trailing spaces unreliably and they are invisible, which argues for yes; the brief specified leading only.
- Granularity differs by stage today: stage 3 is a substring match inside one line, stage 4 is whole-line equality across a window. The ladder must say which each tier means, and tiers above `Exact` need a defined span for a fragment match (the real span, never `contextSnippet.Length`).

Case-insensitivity: recommended as a **separate axis**, not a rung. It is not whitespace; C# is case-sensitive, so a case-insensitive hit can resolve to a different identifier. The tolerant matcher is currently OrdinalIgnoreCase in stages 3 and 4, so migrating callers must keep that behaviour explicit (old overloads delegate with ignore-case on) until each caller states otherwise. Mechanism (second required enum vs a small `SnippetMatchPolicy` record) is open.

### 2. Required parameter, no default

Every resolution/lookup helper takes a `WhitespaceTolerance` argument with no default, so the compiler enumerates every call site. Wrappers (`...OrThrow`, `...OrNull`, `Try...`) take the same parameter and pass it through; a wrapper cannot supply one itself. Each caller states the loosest tier it accepts.

### 3. No silent downgrade

A helper that does not implement a requested tier returns or throws a structured error. It never quietly runs a stricter or looser tier.

### 4. Three-outcome error model

Replaces "NotFound" for every helper:

- **(a) No match at any tier.** `NotFound`, message names the tiers tried.
- **(b) Matched only at a looser tier than the caller accepts.** A new error code and exception (a `ToolException` subclass plus a `ToolErrorCode` constant, `ToolException.cs`; name open, e.g. `WhitespaceMismatch`), distinct from `NotFound`. The message names the tier that matched and shows the file's actual lines verbatim, ready to copy. This is what would have turned the Qwen run's turns 2-11 into one retry.
- **(c) Ambiguous.** Existing `Ambiguous` behaviour.

Proposed policy for edit tools, "Exact, but report a looser near-match": on failure, every helper probes upward from the accepted tier to `IgnoreAllWhitespace` to decide between (a) and (b). The probe runs only on the failure path, so there is no cost on success. No extra parameter; the only per-caller choice is the accepted tier. Open whether this is right for read/lookup callers (which usually accept the loosest tier, making (b) moot).

### 5. Results carry the tier and the real span

The match result carries the tier that matched and the matched span length. The splice-corruption history makes span length a hard requirement for any edit caller. Tolerant tiers must return the real source span. Today `SnippetMatch` is `(Start, Length)` (`ContextHelper.cs:24`); how the tier is added without breaking its construction sites is a slice 2 detail (construction sites in tests not surveyed).

### 6. Diagnostics derive from the tier

- `FormatClosestWindowHint`: number snippet lines against the real snippet (keep blank lines, or map needle indexes back), and stop blaming a blank source line.
- `DiagnoseNoMatch` / `GatherNoMatchEvidence`: compare leading whitespace, so an indentation-only difference is reported as such, with the file's real line quoted.
- Remove the hardcoded "ReplaceSnippet requires a byte-exact match" (`:621`); the text comes from the caller's accepted tier.
- Fix the `contextSnippet` vs `oldContent` label in the generic branch of `FormatAllMatchedDiagnosis` (and check `FormatPartialMatchDiagnosis`, which I did not read).
- Make the case handling of the diagnostics match the matcher's (inconsistency 3).

### 7. Adjacent follow-ups (not core)

- Separate error codes for `AdjacentLineMismatch` and `SelectorMismatch` (today both surface through `ToolNotFoundException`; `ContextErrorBuilder.cs:34-38, :106, :122`).
- Include an `oldContent` hash in the breaker signature. Caveat (hypothesis): in this run every attempt had different `oldContent`, so a hash would have kept the breaker from tripping and burned more turns. It helps only if the new error (b) makes repeated identical attempts the real failure case. Decide after slice 1 ships.

### 8. Single-candidate `ResolveBySnippetOrThrow`

Proposed: keep the behaviour (it is intentional per its XML comment) but make it an explicit, documented part of the helper's contract, and have it report whether the snippet was consulted so a tool can warn if it wants to. Open: whether any tool should warn.

### Migration (each step compiles; at most 3 `.cs` files per implementer slice)

1. **Diagnostics only**, independent of the enum: indentation-aware NoMatch message, `FormatClosestWindowHint` numbering/blame, wording and label fixes. Files: `ContextHelper.cs`, `ContextErrorBuilder.cs`, one test file. Ships on its own and fixes the evidence case's message.
2. **Add the enum and new overloads beside the old ones** in `Common`, with tier reporting. Old overloads delegate with their current behaviour (`Exact`-family -> `IgnoreLineEndings`; tolerant family -> `IgnoreAllWhitespace`, ignore-case on).
3. **`ReplaceSnippet`/`ReplaceSnippetBatch`** (2 sites, `WorkspaceFileEditImpl.cs`) to `IgnoreLineEndings`, using error (b).
4. **`FindSnippetPosition`** (16 sites, 9 files), in batches.
5. **`TryFindSnippetPosition`** (8 sites, 4 files) and **`FindSymbolAtSnippetAsync`** (6 sites, 2 files).
6. **`ResolveBySnippetOrThrow`** (35 sites, 4 files) in 2-3 slices; 27 of the sites are in `MemberRefactoringEngine.cs`, so that file is split by method group. I did not re-read the dispatch skill's call-site limit; the batching is a planning decision.
7. **Cleanup**: delete the old overloads and dead `FindNodeAtSnippet`; rename helpers whose names promise something untrue (e.g. `FindExactSnippetPosition`) only now that the parameter exists.

Out of scope for now: `Search(mode: text)` (a user-visible tool behaviour change, contradicting "internal only", and not a position-resolving helper) and `DiffEngine.ReanchorHunk` (a hunk-repositioning failure model; shares `LineBlockAligner` with `ContextHelper`, so slice 1 touches shared code that DiffEngine also uses). Both are open questions below.

## Alternatives considered, not pursued

- **(a) Rename helpers only.** Does not reach the model, does not fix the wording drift or the single-candidate case, and gives callers no way to state what they accept.
- **(b) Tool-level `WhitespaceTolerance` parameter.** About 22 tool signatures, schema tests such as `ReplaceSnippetSchemaTests` (`RoslynSentinel.Tests.Server/ReplaceSnippetSchemaTests.cs`, which hardcodes a literal), Lean-profile hiding, and a weak model would have to choose a value it cannot judge. Rejected in favour of internal-only; the environment should choose and explain, not the model.
- **(c) Make `ReplaceSnippet` tolerant** (`plan_replacesnippet_whitespace_tolerant_match.md`). Rejected: the 2026-09-10 splice-corruption history. A tolerant edit match is safe only with a real span; this proposal gives tolerant tiers one, but loosening the edit path is a separate decision the evidence does not force. Error (b) gets the model to the right text in one retry without that risk.
- **Revive `MatchStrictness` and fold whitespace into it.** Would conflate candidate policy with text normalisation; see the comparison above.

## Open questions

1. Case axis: second required enum or a policy record; and whether lookup callers keep today's case-insensitive behaviour.
2. Whether `DiffEngine` anchoring joins the ladder.
3. Whether `Search(mode: text)` joins, given it is tool-visible.
4. Default accepted tier per caller category (proposed: edit tools `IgnoreLineEndings` with probing; read/lookup tools `IgnoreAllWhitespace`). Not decided.
5. How engines receive a tolerance without MCP types (the tier is chosen in `Tools.*` or hard-wired per engine call; layering forbids MCP types below `Tools.*`).
6. Trailing-whitespace and fragment-vs-whole-line semantics per tier (section 1).
7. Name and placement of the new error code; relationship to `proposal_tool_error_code_taxonomy` (memory `project_tool_error_code_taxonomy_proposal`).
8. Whether `MatchStrictness` shares a type or vocabulary with this.

## Cost / risk

- **Touches:** `Common` (`ContextHelper`, `ContextErrorBuilder`, `ToolException`/`ToolErrorCode`, new enum file), then call sites in `Engines.Basic`, `Engines.Advanced` and `Tools.Basic`. Tools' public schemas, `ToolParams` and profiles are unchanged. Layering (`Common` <- `Engines.*` <- `Tools.*`) holds because the enum lives in `Common`.
- **Tests:** 48 references to `FindExactSnippetPosition` and 8 to `FindSnippetPosition` need updating at the cut-over slices.
- **Coverage gaps the plan must close (planner's responsibility to specify; reviewer's to confirm).** Existing tests exercise the exact path (for example `FindExactSnippetPosition_ApproximateIndentation_ThrowsNotFound`, `ContextHelperTests.cs:310`, and `..._AdjacentSimilarLines_NoSpliceFragmentOnReplace`, `:435`). None can exercise tier reporting or error (b), because they do not exist yet. While old overloads delegate (slice 2), a green suite proves delegation only. Each tier needs its own test on the changed path, including a span-length assertion per tolerant tier, a wrapper pass-through test per wrapper, and a regression using the Qwen run's snippet (`}` and `var trailingIssue` at column 0 against 8 spaces). The implementer is correct if tests it was given pass; it cannot be expected to cover what the plan did not specify.
- **Makes harder:** every new snippet helper must declare a tier; a new tier means touching every helper that implements the ladder.
- **Risk:** the error-(b) message echoes real file lines into a tool error, so it must respect the existing length caps (`MaxAllMatchedDiagnosisChars`, `ContextHelper.cs:587`) and not leak paths; and `LineBlockAligner` is shared with `DiffEngine`, so slice 1 changes must be checked against `DiffEngine` tests.
- **Closes in `TODO.md`:** nothing tracked there (searched 2026-10-13 for whitespace/FindSnippetPosition entries; only unrelated `NormalizeWhitespace` items matched). Add a line when the doc is approved.

## Related

- `docs/current/finding_matchstrictness_internal_param_evaluation.md`
- `docs/current/CLOSED.md` (splice-corruption entry, 2026-09-10)
- `docs/current/plans/plan_replacesnippet_whitespace_tolerant_match.md`
- `docs/current/proposal_universal_symbol_resolver.md`
- `docs/current/proposals/proposal_snippet_match_selectors_withinmember_matchline.md`
- Memory: `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`, `project_tool_error_code_taxonomy_proposal`, `project_replacesnippet_tuple_generic_transcription_failure` (a second weak-model `ReplaceSnippet` transcription failure)
