# Internal WhitespaceTolerance contract and SnippetResolver for snippet-in-text matching

**Status:** PROPOSED 2026-10-13. Direction agreed with the user 2026-10-13 (internal-only enum, one dispatcher in `Common`, three-outcome result, staged migration); sections A-G below record that design as settled, except where marked open. All ten open questions were decided 2026-10-13 (see "Decisions"), and a candidates-in-errors requirement was added (section E, "Candidates in every failure"). Slicing and file counts are still estimates for the plan.

This revision replaces the first draft (committed at 412ae839). The motivation, evidence, prior art and `MatchStrictness` comparison are carried over; the design is rewritten.

## Motivation

Callers and helpers disagree about whitespace handling, and the only signal a caller gets is a "not found" error that does not say whitespace was the cause. A weak model then spends its turn budget on the wrong hypothesis.

### Evidence: Qwen run 20261013-191017-805-3769e300

Run folder: `RoslynSentinel-TestRuns/SubAgent/20261013-191017-805-3769e300/subagent/Logs/` (`agent.log`, `turn-N-01-ReplaceSnippet-result.json`). The task was a four-branch edit in `MemberRefactoringEngine.cs`. The run ended `RepeatedToolFailure` after 11 turns (5m06s, `agent.log:164`). Seven `ReplaceSnippet` calls (turns 2, 4, 5, 7, 9, 10, 11) all returned `NotFound`; the breaker tripped on `[ReplaceSnippet|NotFound|<file>]` across turns 9-11 (`agent.log:163`).

Cause, verified from the logged request and the worktree file bytes (the call was not re-executed): the file has `        }` and `        var trailingIssue = ...` at 8 spaces (worktree `MemberRefactoringEngine.cs` lines 2992-2994). The turn 2 `oldContent` (`agent.log:44`) has those two lines at column 0 while keeping the other lines' indentation. `ReplaceSnippet` uses the exact matcher (`ContextHelper.FindExactSnippetPosition`, `ContextHelper.cs:301`), which runs only the ordinal `IndexOf` plus the CRLF/LF regex (`:248-287`), so it cannot match.

What the error told the model (turn 2 result, quoted):

- "`5 of 5 lines in contextSnippet were located individually ... but not in the arrangement you supplied`" -- the call was `oldContent`; the label reads `contextSnippet`.
- "`snippet line 9 was located at source line 2994`" and, in the same message, "`Closest window: ... snippet line 8 ("var trailingIssue ...") differs from source line 2993 ("")`". The same line is numbered 9 and 8 in one message, and the hint blames a blank source line.
- No mention of indentation.

Why the diagnostics were blind (traced to source, re-read 2026-10-13):

- `GatherNoMatchEvidence` (`ContextHelper.cs:811`) compares `.Trim()`-ed lines, so leading indentation cannot differ. Its whitespace-only flag (`IsWhitespaceOnly`) is set only when the interior-collapsed comparison matches, so `FormatAllMatchedDiagnosis` took the generic "not in the arrangement" branch (`:617-628`).
- `FormatClosestWindowHint` (`:896`) builds its needle with `snippetLines.Where(l => l.Length > 0)` (`:900`), dropping the snippet's own blank line, while the source array keeps blanks. The needle's last line is therefore compared against the file's blank line, which produces both the off-by-one numbering and the blank-line blame. `LineBlockAligner` also compares trimmed text.
- The breaker key is `tool|errorCode|file` (`ModelAgentRunner.BuildFailureSignature`, `Common/AgentLoop/ModelAgentRunner.cs:344`), so seven different failed attempts look like one failure. That is deliberate (its comment: a model that fixes one argument and hits a different error is not looping), which matters for follow-up H1.

Model-side friction, recorded as observed (not resolved into a tidy story):

- Turn 5 (`agent.log:106`): "use `contextSnippet` with lineBefore/lineAfter parameters in ReplaceSnippet ... That's more robust according to the tool docs." `lineBefore`/`lineAfter` select among matches; they do not loosen matching. Which description led to this belief is untraced (hypothesis: the shared `ToolParams.ContextSnippet` text, `ToolParams.cs:42`).
- Turn 6 (`agent.log:113`): blamed "a blank line". This matches the misleading `source line 2993 ("")` hint (hypothesis, not confirmed).
- Turn 8 (`agent.log:124`): blamed Windows line endings. No message text read supports this; why is untraced.
- Turn 10 (`agent.log:151`): "not providing the exact verbatim text".
- Turn 11 sent malformed arguments (parameter tags inside `reason`, `agent.log:161`).

Four different hypotheses across five turns, none of them indentation, because no message named it. Per the failure doctrine this is an environment defect: the tool had the information (it compared trimmed lines and knew they matched) and discarded it.

### Structural cause: helpers silently encode different whitespace policies

Callers re-counted 2026-10-13 with `Search(mode: references)` (production code, excluding `ContextHelper`'s own internal use and doc-comment crefs). `FindNodeAtSnippet`: 0 callers including tests.

| Family | Helper (file:line) | Matching | Production callers |
| --- | --- | --- | --- |
| Exact | `FindExactSnippetPosition` (`ContextHelper.cs:301`), `FindAllExactSnippetMatches` (`:248`) | ordinal, plus `\r?\n` regex for multi-line; also hosts the `withinMember`/`matchLine` selectors | 2: `WorkspaceFileEditImpl.cs:641` (ReplaceSnippet), `:825` (ReplaceSnippetBatch). 48 test refs |
| Tolerant | `FindAllSnippetMatchesWithLength` (`:45`) | 4 stages, each run only if every earlier stage found nothing: literal; EOL regex; single-line `\s+` collapse + Trim + OrdinalIgnoreCase substring; N-line window with per-line Trim + collapse + OrdinalIgnoreCase | via the wrappers below |
| Tolerant | `FindAllSnippetMatches` (`:32`) | wrapper, drops length | 1: `SyntaxTargetResolver.cs:305` |
| Tolerant | `FindSnippetPosition` x2, `FindSnippetPositionWithLength` x2 (`:178-234`) | wrappers | 16 sites in 9 files (`SourceText` overload; 8 test refs) |
| Tolerant | `TryFindSnippetPosition` x2 (`:490`, `:507`) | non-throwing wrappers | 8 sites in 4 files |
| Tolerant | `FindSymbolAtSnippetAsync` (`:1161`) | wrapper plus Roslyn symbol lookup | 6 sites in 2 files (`SymbolNavigationEngine` 5, `ImpactAnalyzer` 1) |
| Tolerant | `SyntaxTargetResolver.ResolveBySnippetOrThrow` (`Engines.Basic/SyntaxTargetResolver.cs:292`) | candidate disambiguation | 35 sites in 4 files (`MemberRefactoringEngine` 27, `SymbolNavigationEngine` 4, `BasicRefactoringEngine` 3, `AdvancedStructuralEngine` 1) |
| Dead | `FindNodeAtSnippet` (`:1138`) | -- | 0 |
| Other | `DiffEngine.ReanchorHunk` (`Common/DiffEngine.cs:294`) | per-line Trim, case-sensitive, no interior collapse, +/-60 lines; shares `LineBlockAligner` | -- |
| Other | `Search(mode: text)` | per line, case-insensitive; multi-line patterns never match | -- |

`ContextHelper.cs` is 1669 lines, not the ~1200 first assumed. Lines 13-1182 are snippet matching, selectors, diagnostics and `FindSymbolAtSnippetAsync`; lines 1187-1667 (`GetUniqueVariableName`, XML-doc generation helpers) have nothing to do with snippets.

No helper reports which stage matched. Defects that follow from that:

1. `ReplaceSnippet` is exact, but every `contextSnippet` tool is tolerant and case-insensitive, while the shared `ToolParams.ContextSnippet` text says "copied exactly" (`ToolParams.cs:42`).
2. `ResolveBySnippetOrThrow` consults the snippet only when there is more than one name candidate (`SyntaxTargetResolver.cs:300`, guard `contextSnippet == null || candidates.Count <= 1`). A wrong snippet is silently ignored with one candidate. The XML comment says this is deliberate, but nothing outside it does. It also uses only each match's `Start`, via the tolerant `FindAllSnippetMatches`.
3. The diagnostics (`GatherNoMatchEvidence`, `MatchLine` `:1077`, `LineBlockAligner`) are case-sensitive; the tolerant matcher is case-insensitive. A near-match the matcher would accept can be reported as a miss.
4. `FormatAllMatchedDiagnosis` hardcodes "ReplaceSnippet requires a byte-exact match" (`ContextHelper.cs:620-621`) though `DiagnoseNoMatch` is shared. It is not literally true either: the exact matcher accepts CRLF/LF interchange.
5. A tolerant-path failure reads "An exact match could not be located for the provided contextSnippet" (`ContextErrorBuilder.cs:55`); that path was not exact. The generic branch also hardcodes the label `contextSnippet`, and the ambiguity text hardcodes "withinMember where listed" (`ContextErrorBuilder.cs`, `BuildAmbiguous`) whether or not that selector was offered.
6. Span conventions differ per stage and are undocumented as a set. Exact: start at the snippet's first character. Single-line fallback: start at the mapped fragment (after the line's indentation). Window fallback: start at the first line's start, so the span includes that line's real indentation, and ends at the last line's end without its line break (`ContextHelper.cs:87-165`; window end at `:159`). Replacing a window span with caller text drops the file's indentation unless the caller supplied it. Today this is harmless because no edit tool uses the tolerant path; it becomes a hazard the moment a tier above `IgnoreLineEndings` is used for an edit.
7. The `withinMember`/`matchLine` selectors exist only on the exact path (`ContextHelper.cs:306-388`); the tolerant path has only `lineBefore`/`lineAfter`.

## Proposal

### Prior art and how this differs

- `CLOSED.md` "`ReplaceSnippet` silently corrupted adjacent lines sharing a trailing substring" (2026-09-10, `CLOSED.md:688-703`): the tolerant path's span length differed from the snippet's, so `ReplaceSnippet` was moved to the exact matcher on purpose; `ApplyDiff` was left loose ("two separate anchoring strictness levels, by design"). This proposal keeps that decision and does not make `ReplaceSnippet` tolerant. It makes the levels explicit and named, and makes the span a checked invariant.
- `docs/current/plans/plan_replacesnippet_whitespace_tolerant_match.md`: a plan to make `ReplaceSnippet` tolerant. Never applied; rejected here (Alternatives, c).
- `docs/current/proposal_universal_symbol_resolver.md`: candidate resolution, not text normalization.
- `docs/current/proposals/proposal_snippet_match_selectors_withinmember_matchline.md`: the selectors. Orthogonal; they filter matches after any tier has produced them (section C keeps them as a separate stage).
- `docs/current/finding_matchstrictness_internal_param_evaluation.md` (2026-09-28): evaluated an internal `MatchStrictness` (`Strict`/`BestEffort`) parameter for `FindCallersAsync`/`FindImplementationsForMemberAsync` and did not implement it. It concluded the idea clears the premature-abstraction bar because two callers (`SymbolRelationshipImpl.FindReferences`, read-only; `RefactoringStructuralImpl.Member`, mutating) have opposite correctness needs, and that `Member` should always be `Strict`. Differences, from re-reading it 2026-10-13:
  - **Different axis.** It is about candidate ambiguity: pick the closest of several symbol candidates, or throw. This proposal is about text normalization: which whitespace differences a snippet comparison forgives. A `Strict` candidate policy can sit on any whitespace tier.
  - **No default here.** It proposed `Strict` as the default. Here the tolerance is a required argument at every adapter call site (memory `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`).
  - **Existing evidence.** Both policies already exist as hardcoded paths (exact and tolerant families above), chosen implicitly by method name at 50+ call sites; that evaluation weighed a split that did not yet exist.
  - **Overlap, unresolved.** The single-candidate rule in `ResolveBySnippetOrThrow` is partly a candidate-ambiguity question and so is in that finding's territory. Whether the two parameters should share a vocabulary or type is open. The finding does not say whether its author expects `MatchStrictness` to be revived, and I could not tell.

### A. Scope (settled)

In scope: snippet-in-text matching, meaning text plus snippet in, matches out. Tolerant family: `FindSnippetPosition` x2, `FindSnippetPositionWithLength` x2, `TryFindSnippetPosition` x2, `FindAllSnippetMatches`, `FindAllSnippetMatchesWithLength`, and `ResolveBySnippetOrThrow`'s snippet use. Exact family: `FindExactSnippetPosition`, `FindAllExactSnippetMatches`, and `ReplaceSnippet`'s two sites.

Out of scope: `DiffEngine` hunk anchoring (`ReanchorHunk`), name-based symbol lookup (`ResolveCandidates`), `Search(mode: text)` (per-line, case-insensitive, regex plus literal), `GetMethodSource`/`ReadFile`. These may adopt the enum later; otherwise they keep a simple guard (section C).

### B. The enum (settled)

`WhitespaceTolerance`, internal use only, in `RoslynSentinel.Common`. An ordered ladder; each tier accepts everything the one before accepts:

| Tier | Accepts additionally |
| --- | --- |
| `Exact` | nothing: ordinal comparison, no EOL regex |
| `IgnoreLineEndings` | CR, LF and CRLF interchangeable between snippet and file |
| `IgnoreIndentation` | differing per-line leading and trailing whitespace; interior text still compared as written |
| `IgnoreAllWhitespace` | per-line trim plus interior whitespace-run collapse |

LF/CRLF as its own tier (decided). Reason: today's "exact" matcher already accepts it (`ContextHelper.cs:269-277`), so a tier named `Exact` that also did so would be untrue, and the enum exists to stop names promising something untrue. It also matters in practice: the repo's own source files are CRLF while a model's multi-line snippet is typically LF, so an ordinal `Exact` `ReplaceSnippet` would fail every multi-line edit. `ReplaceSnippet` therefore passes `IgnoreLineEndings`, preserving its behaviour exactly. For a single-line snippet the two lowest tiers behave identically (the EOL regex runs only when the snippet contains `\n`).

Mapping from today's code:

- `Exact`, `IgnoreLineEndings`: the ordinal and EOL-regex stages (`:60-85`).
- `IgnoreAllWhitespace`: the two collapse stages (`:87-165`).
- `IgnoreIndentation`: **no stage today**. Nothing ignores only leading whitespace while preserving interior whitespace; the single-line stage collapses interior runs too, and the window stage trims and collapses every line. This tier needs a new matcher (`IndentationMatcher`). That, and the cap in section C, are the only behaviour additions in the plan, and are a named, separate step (G, slice 2d).
- Decided: `IgnoreIndentation` ignores leading **and** trailing whitespace per line (models reproduce trailing whitespace unreliably and it is invisible). The span must exclude the first line's indentation, consistent with the exact matcher, so a replacement does not eat the file's indentation. Still for the plan: whole-line versus fragment semantics per tier (the single-line stage is a substring match, the window stage is whole-line equality). A tier above `Exact` must return the real source span, never `contextSnippet.Length`.

Case-insensitivity is a **separate axis**, not a rung. It is not whitespace, and C# is case-sensitive, so a case-insensitive hit can resolve to a different identifier. The tolerant stages are OrdinalIgnoreCase today (the first two stages are ordinal) and the diagnostics are case-sensitive; migrating callers must keep today's behaviour explicit until each states otherwise. Decided: a second required enum, no default; lookup callers stay case-insensitive and edit callers are case-sensitive.

No tool schema, `ToolParams`, Lean-profile or schema-test change.

### C. Dispatcher, matchers and guard (settled)

New folder `RoslynSentinel.Common/SnippetMatching/`, class `SnippetResolver`. Not an engine: `Common` stays text-only (no Roslyn semantic model) and is shared with `DiffEngine` and the diagnostics.

- **Public:** the dispatcher, the result types (E), and only those adapters with a distinct return shape (`...OrThrow`, `Try...`).
- **Internal sealed:** `ExactMatcher`, `LineEndingMatcher`, `IndentationMatcher`, `CollapsedWhitespaceMatcher`; the selector post-filters (`withinMember`, `matchLine`, `lineBefore`, `lineAfter`), a stage separate from and independent of the tier; `LineBlockAligner` (already `internal static`, shared with `DiffEngine`, so moving it is a namespace change only).
- **`internal interface ISnippetMatcher { WhitespaceTolerance Tier; FindAll(...) }`**, existing for the matrix test only.
- `InternalsVisibleTo` from `Common` exists only for `RoslynSentinel.Tests` (`RoslynSentinel.Common.csproj:25`), the project that already tests `LineBlockAligner`. Matcher tests must live there; other test projects see only the public surface.

**The cap.** The dispatcher runs matchers in ladder order **up to** the requested tier and stops at the first tier that finds a match (as the stages do today). An `Exact` request can never silently match at a looser tier. This is a small behaviour addition for the exact family (which today has no looser stages to leak into) and none for callers that request the loosest tier.

**Migration shape.** Old public helpers become thin `[Obsolete]` adapters delegating to the dispatcher, are migrated in batches, then made internal and deleted, so the compiler lists every missed caller. `FindNodeAtSnippet` (0 callers) is deleted. A search of repo-root props, csproj and editorconfig found no `TreatWarningsAsErrors`/`WarningsAsErrors`; confirm before slice 2, because `[Obsolete]` on a widely used helper must warn, not fail the build.

**Guard (out-of-scope helpers only).** A shared `WhitespaceToleranceGuard`: each helper declares its supported tier set, and a table-driven matrix test checks every helper against every tier. An unsupported tier is a programming error: a dedicated internal exception, caught at the tool boundary and mapped to a structured error, never a raw exception in `ResultError` (CLAUDE.md working conventions). It applies only to helpers that keep their own implementation and accept a tolerance; in-scope helpers never need it, because the dispatcher either implements the tier or does not exist for them. Rejected: a copy-pasted `switch` in every helper (drifts, and each copy needs its own test).

### D. Splitting `ContextHelper` (settled; separate commits from behaviour changes)

- Matching and selectors move to `Common/SnippetMatching` (pure move).
- NoMatch diagnostics (`DiagnoseNoMatch`, `GatherNoMatchEvidence`, `FormatClosestWindowHint`, `FormatAllMatchedDiagnosis`, and their interplay with `ContextErrorBuilder`) move to a sibling `SnippetDiagnostics` that knows the tier.
- `FindSymbolAtSnippetAsync` is Roslyn-bound (it takes a `Document`) and moves up into an engine that calls the dispatcher. Which engine is a slice detail.
- The unrelated generation helpers (`:1187-1667`) stay put; moving them is not part of this effort.
- Wording lives in one place, `ContextErrorBuilder`/`SnippetDiagnostics`, which ends the drift in defects 4 and 5.

### E. Result DTO (settled; the dispatcher never throws)

`sealed record SnippetMatchResult`:

- `RequestedTolerance`, and the case mode (echoed back).
- `Outcome`: the existing `SnippetMatchOutcome` (`ContextErrorBuilder.cs:149`) is **extended**, not duplicated. It has `NoMatch`, `Ambiguous`, `StillAmbiguous`, `TrueTie`, `InvalidDisambiguator`, `SelectorMismatch`, `AdjacentLineMismatch`; add `Matched` and `MatchedLooserThanRequested`. Its doc comment ("why a lookup failed to resolve to exactly one match") and `ContextErrorBuilder.Build`'s throwing `default` branch must be updated for a non-error member. The dispatcher uses `InvalidDisambiguator` instead of `ThrowIfMultiLine` throwing. It reports `Ambiguous` when no adjacent-line or selector filter was supplied and `StillAmbiguous` otherwise; `TrueTie` stays a diagnostics-stage refinement (to be confirmed in the plan).
- `Matches`: `IReadOnlyList<SnippetMatch>` at or stricter than the requested tier. Each `SnippetMatch` has start, line, **true span length**, the tier that matched and the matcher name. This is the only type an edit adapter accepts. The new record collides in name with the legacy nested `ContextHelper.SnippetMatch(Start, Length)` (`:24`); slice 2 must resolve that, and the nested one is deleted in the final slice.
- `NearMatches`: `IReadOnlyList<SnippetNearMatch>`, matches that exist only at a looser tier. Each has the lowest tier needed, the file's actual lines verbatim and copy-ready, and a short **structured** difference (for example: leading whitespace, line 3, expected 8, got 0). `SnippetNearMatch` is a **separate type** from `SnippetMatch`, so a near-match cannot be passed to an edit path: a compile-time guard for the splice-corruption rule.
- Invariants: `NearMatches` is populated only when `Matches` is empty and only when the snippet matched nothing before selectors; a result has accepted matches, near-matches or neither, never both. When the text matched but a selector removed everything, that is `SelectorMismatch`/`AdjacentLineMismatch`, not a near-match.
- Surface: `IsSingleMatch` (`Matches.Count == 1`, the only check an editing caller needs); `Single()` and `TryGetSingle(out SnippetMatch)` on accepted matches only; `HasNearMatches` (convenience, never part of a success check); `Outcome` for error-code mapping (`Ambiguous` = several accepted matches; `MatchedLooserThanRequested` = near-matches only).
- Filter trail: candidate counts before and after selectors and after adjacent-line filtering, so `AdjacentLineMismatch` can say how many candidates were removed.
- **No prose in the DTO.** Wording stays in one place, which stops the drift in defects 4 and 5.

Why the name `IsSingleMatch`, and why not an equality test. It is deliberately not `IsMatch` or `IsExactMatch`: `Exact` is a tier name, and an accepted `IgnoreIndentation` hit is not an `Exact` match. A caller must not test "matched tier equals requested tier": a stricter match is acceptable (an `IgnoreIndentation` request against a file that matches verbatim is fine). Because of the cap, accepted matches are never looser than requested, so "at or stricter" holds by construction and callers need no tier comparison.

Near-match probing is **lazy**: it runs only on failure, as a read-only probe of looser tiers reporting the lowest tier that matches. The list is capped at 3 to 5 entries with a total count (decided; the exact number is a plan detail). A near-match is never used to edit.

#### Candidates in every failure (requirement, decided 2026-10-13)

Every matcher and lookup returns its candidates wherever they exist, and every tool error built from a failed result **lists them**. A failure that has candidates is never reported as prose alone.

**Why.** Whitespace handling is a frequent weak-model struggle: models weigh it differently from code, miscount it, and the text passes through several serialization layers. The repeated identical tool calls seen in failed runs likely come from the model not understanding the message, or believing its revised call is already correct. Messages such as "no matches found, try an exact snippet" or "a match was found from lines x through y but differed at line z" make the model do bookkeeping and translation across several layers. A candidate list removes that: the model copies the right text on its next turn and recovers unambiguously. Compare the Qwen run, where seven NotFound results named no concrete text to copy (Evidence above).

**What a candidate is.**

- **Ambiguous:** each accepted match, as today (line, enclosing member, ready-to-use selectors), plus the file's actual text for that match so the model can see what differs between candidates.
- **Looser-tier only:** each `SnippetNearMatch` carries the file's actual lines verbatim and copy-ready (the text the model should send as `oldContent`), the lowest tier needed, and the structured difference.
- **No match at any tier:** the closest windows, each as actual file lines verbatim, ranked, rather than a description of where lines differed. Having no candidate to show is a legitimate outcome only when the probe finds nothing close, and the message says so.
- **Selector and adjacent-line failures:** the candidates the filter removed, with the values that would select each one.

**Rules.**

- Candidate text is the file's text, not a rendering of the snippet. Where the model sends it back, it must match at the requested tier without further editing.
- Caps keep errors bounded (3 to 5 candidates, a total count, and the existing character cap `MaxAllMatchedDiagnosisChars`). A cap must not drop the most likely candidate: rank before truncating, and say how many were omitted. This replaces the 5-candidate cut in H2, which can hide the intended match.
- Prose stays secondary: one line naming the outcome, then the candidates. Slice 1's per-line difference notes remain as the annotation on each candidate, not as the only information.
- The DTO carries the candidates as data (`Matches`, `NearMatches`, and a new closest-windows list for the no-match case); `ContextErrorBuilder`/`SnippetDiagnostics` formats them. Every lookup helper that adopts the dispatcher inherits this, including the lookup callers (`ResolveBySnippetOrThrow` and friends), not only the edit tools.
- Test: for each failure outcome, a test asserts the error text contains the candidate's literal lines, and that sending a candidate back succeeds. That second check is the guard for "recovers on the next turn".

Open for the plan: the closest-window ranking for the no-match case (whether to reuse `LineBlockAligner` scoring as is), and whether a lookup tool (not an edit) should return the single best near-match as a result with a warning rather than an error.

Every matcher must return the real span, and each needs a **splice-invariant test**: replacing the reported span with the matched text reproduces the file. This is the direct regression guard for the 2026-09-10 corruption.

### F. Three-outcome error model (settled)

Replaces `NotFound` for these paths:

1. **No match at any tier.** `NotFound`; the message names what was tried and lists the closest windows as actual file lines (see "Candidates in every failure").
2. **Matched only at a looser tier than the caller accepts** (`MatchedLooserThanRequested`). Its own error code in `ToolErrorCode` (`Common/SentinelCallToolResult.cs:42`; the name and placement are aligned with `project_tool_error_code_taxonomy_proposal` later) and a `ToolException` subclass, not `NotFound`. The message names the tier and lists each near-match as copy-ready file lines. This is what would have turned the Qwen run's turns 2-11 into one retry.
3. **Ambiguous.** Existing `ToolAmbiguousMatchException` behaviour, with each candidate's file text included.

Edit tools adopt the policy "`Exact`-class tier, but report the near-match". Read and lookup tools accept the loosest tier, which makes outcome 2 moot for them (decided); outcomes 1 and 3 still list candidates.

`ResolveBySnippetOrThrow` validates the snippet even when there is one name candidate (decided). A currently silent wrong snippet becomes an error at up to 35 call sites, so slice 4 for that helper must expect new failures in existing tests and treat each as a finding, not as noise.

### G. Staged migration (every step compiles; implementer slices of 3 or fewer `.cs` files; file counts are estimates)

1. **Diagnostics-only fix, independent of everything else.** (a) Indentation-aware NoMatch message: when trimmed lines match but raw lines do not, say "line N matches except leading whitespace: expected X spaces, got Y" and emit the file's actual lines verbatim. (b) Fix `FormatClosestWindowHint` numbering: the needle drops blank lines (`:900`), so "snippet line N" is off by one and blames a blank source line. (c) Remove the hardcoded "byte-exact" and `contextSnippet` wording by threading the label through. Files: `ContextHelper.cs`, `ContextErrorBuilder.cs`, one test file. Tests in `RoslynSentinel.Tests` (`ContextHelperTests.cs`, `ContextHelperSelectorTests.cs`, `ContextHelperLineAfterTests.cs`); `ContextHelperDisambiguationTests` in `Tests.Tools.Basic/RegressionTests.cs` also touches the area. Shared `LineBlockAligner` callers (`DiffEngine`) must be re-run.
2. **Enum, `SnippetResolver`, matchers and DTO, delegating to existing code, no behaviour change.** Likely over 3 files, so split: 2a enum, DTO, `ISnippetMatcher`, extended outcome; 2b matchers and dispatcher; 2c pure-move of matching logic out of `ContextHelper` and inversion so old helpers delegate to the dispatcher (the two delegation directions cannot coexist; ordering is for the plan to settle); 2d the `IndentationMatcher` and the cap, the only behaviour additions. Tests: the matrix (every tier against every matcher) and a splice-invariant test per matcher.
3. **`SnippetDiagnostics` split and tier-aware diagnostics** (defect 3, case handling, the structured near-match difference, outcome 2's message, and the candidate listings required by "Candidates in every failure" for all three outcomes, including the closest-windows list for no-match).
4. **Callers, in batches**, each with that batch's `[Obsolete]` warnings cleared: `ReplaceSnippet`/`ReplaceSnippetBatch` (2 sites, `WorkspaceFileEditImpl.cs`) onto `IgnoreLineEndings` with outcome 2; `FindSnippetPosition` (16 sites, 9 files); `TryFindSnippetPosition` (8 sites, 4 files); `FindSymbolAtSnippetAsync` (6 sites, 2 files); `ResolveBySnippetOrThrow` (35 sites, 4 files) in 2-3 slices, with `MemberRefactoringEngine.cs` (27) split by method group. I did not re-read the dispatch skill's call-site limit; batching is a planning decision.
5. **Final slice:** make the old helpers internal, delete them, `FindNodeAtSnippet` and the legacy nested `SnippetMatch`; rename leftovers whose names overpromise (for example `FindExactSnippetPosition`).

Test impact: 48 references to `FindExactSnippetPosition` and 8 to `FindSnippetPosition` move to the dispatcher or internal matchers. `ReplaceSnippetSchemaTests` (`Tests.Server`) pins the `lineBefore`/`lineAfter` description text (`ExpectedAnchorText`); no slice above touches that, so it is unaffected unless a tool-text change (H3) alters those descriptions. I read lines 1-70 of the file only.

### H. Adjacent follow-ups (not core; each is its own item)

1. **Breaker signature.** `tool|errorCode|file` ignores arguments (`ModelAgentRunner.cs:344`). Adding an `oldContent` hash conflicts with that function's stated intent (a model changing an argument is not looping). In the Qwen run every attempt had different `oldContent`, so a hash (hypothesis) would have kept the breaker from tripping and cost more turns. Revisit only after outcome 2 ships and shows whether repeated identical attempts are the real failure. Distinct error codes for `AdjacentLineMismatch` and `SelectorMismatch` (both surface as `ToolNotFoundException` today, `ContextErrorBuilder.cs`) is the less contentious half.
2. **Ambiguity listing caps.** `StillAmbiguous`, `SelectorMismatch` and `AdjacentLineMismatch` list at most 5 candidates (`ContextErrorBuilder.MaxStillAmbiguousCandidatesListed`, `:8`); the first-time `Ambiguous` listing is uncapped (`DescribeAmbiguousCandidates` default `int.MaxValue`). The brief says the Qwen target was match 8 of 11, so a cap of 5 would hide it once the model's text matches; I did not verify the 8-of-11 figure (the logged run never got past NoMatch). The static "withinMember where listed" wording is defect 5. The candidates-in-errors requirement (section E) supersedes a fixed cap of 5: rank, then truncate, and report how many were omitted.
3. **ReplaceSnippet description.** Add a line that indentation and blank lines must match exactly. The `oldContent` text is assembled in `McpToolSchemaPatcher.cs:375` (`ApplyReplaceSnippetLimits`); I saw only a preview of it.

## Alternatives considered, not pursued

- **(a) Rename helpers only.** Does not reach the model, does not fix wording drift or the single-candidate case, and gives callers no way to state what they accept.
- **(b) Tool-level `WhitespaceTolerance` parameter.** About 22 tool signatures, schema tests such as `ReplaceSnippetSchemaTests`, Lean-profile hiding, and a weak model would have to choose a value it cannot judge. Rejected in favour of internal-only; the environment should choose and explain, not the model.
- **(c) Make `ReplaceSnippet` tolerant** (`plan_replacesnippet_whitespace_tolerant_match.md`). Rejected: the 2026-09-10 splice-corruption history. A tolerant edit match is safe only with a checked real span; this design provides one, but loosening the edit path is a separate decision the evidence does not force. Outcome 2 gets the model to the right text in one retry without that risk.
- **A `switch` in every helper.** Rejected: drifts, and each copy needs its own test. The guard and the dispatcher exist for this.
- **Interface-based result** (an `ISnippetMatch` that near-matches also implement). Rejected: a near-match could then be passed to an edit path. Separate types make that a compile error.
- **Revive `MatchStrictness` and fold whitespace into it.** Would conflate candidate policy with text normalization; see the comparison above.

## Decisions (2026-10-13)

The ten questions listed in the previous draft were decided as follows.

| # | Decision |
| --- | --- |
| 1 | Case is a second required enum, no default. Lookup callers stay case-insensitive; edit callers are case-sensitive. |
| 2 | `DiffEngine` anchoring (`ReanchorHunk`) and `Search(mode: text)` are deferred until after slice 4. |
| 3 | Edit tools are `Exact`-class with near-match reporting. Read and lookup tools are tolerant. |
| 4 | LF/CRLF is its own tier (`IgnoreLineEndings`). |
| 5 | `ResolveBySnippetOrThrow` validates the snippet even with one name candidate. |
| 6 | Tolerance is hard-wired per engine call site; no MCP types below `Tools.*`. |
| 7 | Near-match cap of 3 to 5 entries, with a total count. |
| 8 | `IgnoreIndentation` ignores leading and trailing whitespace. Spans exclude the first line's indentation, consistent with the exact matcher. |
| 9 | A new distinct code in `ToolErrorCode`; name and placement aligned with `project_tool_error_code_taxonomy_proposal` later. |
| 10 | Dispatcher first, delegating to the existing code (2b), then move the logic (2c). |

Added requirement: every matcher and lookup returns its candidates, and tool errors list them (section E, "Candidates in every failure").

## Still open (for the plan, not blocking)

- Whole-line versus fragment semantics per tier (the single-line stage is a substring match, the window stage is whole-line equality).
- Closest-window ranking for the no-match case: reuse `LineBlockAligner` scoring as is, or rank differently.
- Whether a lookup tool returns its single best near-match with a warning instead of an error.
- Whether `TrueTie` stays a diagnostics-stage refinement (section E).

## Cost / risk

- **Touches:** `Common` (`ContextHelper`, `ContextErrorBuilder`, `SentinelCallToolResult.cs`'s `ToolErrorCode`, new `SnippetMatching/` folder), then call sites in `Engines.Basic`, `Engines.Advanced` and `Tools.Basic`. Tool schemas, `ToolParams` and profiles are unchanged. Layering (`Common` <- `Engines.*` <- `Tools.*`) holds because the enum and dispatcher live in `Common`.
- **Tests:** 48 and 8 references move at the cut-over slices (above).
- **Coverage gaps the plan must close.** Planner: define what each tier must prove. Reviewer: confirm the tests cover it. Existing tests exercise the exact path only; for example `FindExactSnippetPosition_ApproximateIndentation_ThrowsNotFound` (`ContextHelperTests.cs:310`) covers exact-path rejection of an indentation difference, and `..._AdjacentSimilarLines_NoSpliceFragmentOnReplace` (`:435`) covers the exact path's span. Neither can exercise tier reporting, the cap, near-matches or outcome 2, which do not exist yet. While old helpers delegate (slice 2), a green suite proves delegation only, not the new paths. Required, per changed path: a splice-invariant test per matcher; a cap test (an `Exact` request never returns a looser-tier match, including when a looser match exists elsewhere in the file); an `IgnoreIndentation` test distinct from the collapse stages; a wrapper pass-through test per adapter; the invariant tests (near-matches only when matches are empty); and a regression using the Qwen snippet (`}` and `var trailingIssue` at column 0 against 8 spaces). An implementer whose given tests pass is correct from its vantage point; a gap that survives a green run belongs to the planner and reviewer.
- **Makes harder:** every new snippet helper must go through the dispatcher or declare tiers in the guard; a new tier touches every matcher and the matrix test.
- **Risks:** outcome-2 messages echo real file lines into a tool error, so they must respect the existing length cap (`MaxAllMatchedDiagnosisChars`, `ContextHelper.cs:587`) and not leak paths. `LineBlockAligner` is shared with `DiffEngine`, so slice 1 and the namespace move need `DiffEngine` tests re-run. 35 `ResolveBySnippetOrThrow` sites make slice 4 the widest edit; the compiler-led `[Obsolete]` route bounds it but does not shrink it.
- **Closes in `TODO.md`:** nothing tracked there (searched 2026-10-13; only unrelated `NormalizeWhitespace` items matched). Add a line when this is approved.

## Related

- `docs/current/finding_matchstrictness_internal_param_evaluation.md`
- `docs/current/CLOSED.md` (splice-corruption entry, 2026-09-10, `:688-703`)
- `docs/current/plans/plan_replacesnippet_whitespace_tolerant_match.md`
- `docs/current/proposal_universal_symbol_resolver.md`
- `docs/current/proposals/proposal_snippet_match_selectors_withinmember_matchline.md`
- Memory: `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`, `project_tool_error_code_taxonomy_proposal`, `project_replacesnippet_tuple_generic_transcription_failure` (a second weak-model `ReplaceSnippet` transcription failure)
