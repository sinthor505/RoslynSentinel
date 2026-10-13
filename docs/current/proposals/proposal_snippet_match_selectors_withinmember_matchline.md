# Snippet match selectors: fix adjacent-line anchoring, add withinMember/matchLine

**Status:** Phases 1-3 IMPLEMENTED (2026-10-13). Phase 4 is OPEN and is a deliberate decision, not a to-do.

Phase 3 landed in five commits: 5cbc02fc (EnclosingMemberResolver extracted), f185a613 + 2e14c83d (member names in
Ambiguous/StillAmbiguous errors; StillAmbiguous now lists candidates, capped at 5), 3d718ea4 (selector logic in
`ContextHelper.FindExactSnippetPosition`), 09b38030 (parameters on both ReplaceSnippet tool classes, impl and
`SnippetEdit`), 2ff95c44 (errors hand back copyable `matchLine`/`withinMember`; own wording for selector mismatch).

Phase 3 decisions (resolving the open questions below):
- `withinMember` is name-only, resolved at the tool layer (`WorkspaceFileEditImpl`, which holds the Document) and passed
  to Common as a `Func<int,string?>`; it is paired with `matchLine` as the tie-break for overloads, constructors and
  same-named members. The error lists `withinMember` only when the name is unique among the matches.
- `matchLine` is the 1-based line of the match's first non-whitespace character, against the pre-call text. Batch edits
  already match against the original snapshot, so no shift logic is needed. Staleness across calls is handled by the
  self-verifying error that lists the current matches.
- Precedence: `matchLine`/`withinMember` first, then `lineBefore`/`lineAfter` on the survivors. Blank `withinMember` and
  `matchLine <= 0` count as unset.
- Exact matcher only. The tolerant matcher and the ~24 engine callers are unchanged.
- Top-level selectors combined with `batchEdits` are rejected, not silently dropped.
- `lineBefore`/`lineAfter` are kept and reworded as an extra filter (phase 4 stays open).

Known leftovers: the "lineBefore/lineAfter filtered out all of them" error still opens with the misleading
"verbatim required ... Re-read the file" sentence (same defect as the selector-mismatch case, fixed there only);
no live-model verification of phase 3 yet (server binary is stale; restart before verifying).

## Motivation

A 9B model (Qwen3.5 on LM Studio, `SubAgent` run `20261012-085159-072-f711c4a4`) was asked to relabel
two textually identical 10-line branches in `MemberRefactoringEngine.cs`. The environment steered it
wrong twice and then returned an error too large to read. Full trace:
`docs/current/blockers/blocking_error_replacesnippet_notfound_diagnosis_uncapped_94kb.md`. Summary of
the verified causes:

1. **The adjacent-line filter measures `lineAfter` from the wrong line (false-negative NotFound).**
   `ContextHelper.FilterByAdjacentLinesPredicate` (`RoslynSentinel.Common/ContextHelper.cs:717-749`)
   computes one `lineIndex` from `GetLinePosition(match.Start).Line` (`:721`) and uses it for both
   selectors: `lineBefore` against `Lines[lineIndex - 1]`, `lineAfter` against `Lines[lineIndex + 1]`
   (`:733`). For a multi-line snippet, `lineAfter` is therefore tested against the snippet's own
   second line, not the line after the snippet. The behaviour has been latent since
   `38a867568931c047605d5eb67769979b4ca6302a` (2026-05-02). The documented contract
   (`docs/current/plans/plan_linebefore_lineafter_single_line_contract.md`: "immediately after
   contextSnippet") holds only for single-line snippets.
2. **The Ambiguous message reports the same wrong lines.** `DescribeAmbiguousCandidates`
   (`ContextHelper.cs:655`) reports `lineBefore`/`lineAfter` the same way, so two identical multi-line
   matches always look like they have "identical surrounding lines" (observed: both matches reported
   `lineAfter="if (newMember == null)"`, which is snippet line 2). The message then says "use a
   different tool or a longer oldContent", which is advice the tool surface cannot back up.
3. **When the filter removes every exact match, the failure is misdiagnosed and uncapped.** The result
   falls into the no-match path (`DiagnoseNoMatch`, `ContextHelper.cs:629-647` ->
   `FormatAllMatchedDiagnosis`, `:439-457`), which says the lines were "located individually in the
   file, but not in the arrangement you supplied" (false: the arrangement was correct) and dumps every
   per-line hit with no cap. Observed size: 94,408 bytes, over the 15,360-byte offload threshold, so
   the model saw only `offloaded: true` and a `resultId`, not the `errorCode` or the instruction. The
   dump was added uncapped in `b48971fe62d645d568b08d1ffa287b941d5d9aa1` (2026-09-15). There is no
   closest-contiguous-window suggestion on this path; `DiffEngine.ReanchorHunk`
   (`RoslynSentinel.Common/DiffEngine.cs:294`) already does best-contiguous-prefix diagnosis
   and is the model to copy.
4. **Even with correct semantics, `lineBefore`/`lineAfter` are weak selectors.** Each inspects exactly
   one adjacent line, so they cannot separate matches whose neighbourhoods are also identical (common:
   boilerplate `return new DocumentEditResult {...}` branches), and they are equivalent to widening
   `oldContent` by one line. Widening has its own costs: `newContent` must repeat the added context,
   and `oldContent` is capped at 2000 chars (the model hit that on turn 13: "oldContent is 2598
   chars excluding leading indentation (limit 2000)").

This is a tool-surface defect, not a model fault: the model followed the error text ("a longer
oldContent") and was punished for it. The environment change that fixes it is correct selector
semantics, a bounded diagnosis, and a selector the model can copy straight out of the Ambiguous message.

### Scope of the shared filter (verified)

- Two matchers share the filter and `ContextErrorBuilder`:
  - exact: `ContextHelper.FindExactSnippetPosition` (`:298`), used by ReplaceSnippet
    (`WorkspaceFileEditImpl.cs:606`) and ReplaceSnippetBatch (`:787`);
  - tolerant: `FindSnippetPosition`/`TryFindSnippetPosition`, with ~25 engine call sites, mostly
    single-line `contextSnippet`.
- `SyntaxTargetResolver.ResolveBySnippetOrThrow` (`SyntaxTargetResolver.cs:292`/`:305`) uses
  `ContextHelper.FindAllSnippetMatches` when 2+ name candidates exist.
- `lineBefore`/`lineAfter` are declared on ~27 tools (46 declarations across 21 files).
  - Basic: ReplaceSnippet, Search, SafeDeleteUnusedSymbol, InspectSymbol, FindReferences,
    PreviewRenameImpact, Declaration, ParameterEdit, SummaryComment, ExtractLocalVariable,
    ExtractMethodSafe, MethodSignature, ChangeAccessibility, ConstructorParameter, Member, ModifyEnum,
    ModifyAttribute, ModifyModifier, ModifyBaseType.
  - Advanced: InvertAssignments, Introduce, WrapRange, InterpolateStringSafe,
    AnalyzeForeachForLinqConversion, AnalyzeSwitchForPatternConversion.
  - Experimental: ApplyMethodCodeTransform, ApplyClassCodeTransform.
- The earlier "unification" work (`docs/current/proposal_universal_symbol_resolver.md`; commits `50378ea`
  and `b95e857cb4447b02eec59c1c6876f31897765585`, resolved 2026-10-03, `CLOSED.md:26-41`) covered
  symbol-name resolution and hint formatting only. Text-snippet matching and the adjacent-line filter
  were never unified.

## Proposal

Four phases, each its own slice, in order. Phases 1 and 2 are independently shippable and fix the
reported failure on their own.

### Phase 1 - Fix the shared filter (decided)

- `lineBefore` is checked against the line before the match's **first** line (unchanged).
  `lineAfter` is checked against the line after the match's **last** line, i.e. the line containing the
  last character of the match (`match.Start + match.Length - 1`, not `match.End`, so a match that
  includes its trailing newline does not shift the anchor one line too far). Edge case to pin in a
  test: a match that ends exactly at end-of-file has no following line, so `lineAfter` fails (as the
  existing `lineIndex >= Count - 1` guard does today).
- Single-line snippets: behaviour is unchanged, so the ~25 single-line engine callers and the other
  single-line `contextSnippet` tools are unaffected. Multi-line callers get the documented semantics.
- `DescribeAmbiguousCandidates` reports the same two lines (the line before the first line, the line
  after the last line).
- When the filter eliminates every exact match, return a distinct error instead of falling into the
  no-match diagnosis, in the shape: "oldContent matched N times at lines ...; none had lineAfter = ...;
  the actual adjacent lines are ...". The NotFound code is reserved for text that really is absent.
- Regression tests (each must exercise the changed path, not just a shared helper):
  1. multi-line `oldContent` + `lineAfter` set to the line after the snippet selects the intended
     match through `FindExactSnippetPosition` (the ReplaceSnippet path);
  2. multi-line `oldContent` + a `lineAfter` that matches no site returns the new distinct error, not
     the "not in the arrangement you supplied" diagnosis;
  3. Ambiguous message for identical multi-line matches reports the lines after each match's last
     line (and so can differ between matches);
  4. a single-line snippet case asserting unchanged behaviour on both matchers.
  The tolerant matcher shares the filter, so at least one test should go through
  `FindSnippetPosition` as well, otherwise the "single-line callers unaffected" claim is unchecked.
- **Do not split into single-line and multi-line helpers.**
  - The defect is that the filter ignores the match's end, not that the two cases need different
    logic. Once anchoring is span-based (first line / last line), a single-line match is just the
    case where they coincide, and one code path covers both.
  - A split would duplicate the filter, `DescribeAmbiguousCandidates` and the no-match diagnosis. That
    is the drift this proposal exists to remove.
  - The meaningful split already exists and stays: exact (`oldContent`, a span to replace) vs.
    tolerant (`contextSnippet`, a position to find). They genuinely differ in tolerance.
  - Line count matters in exactly one place, the no-match diagnosis (phase 2): one line gets the
    closest-line suggestion, several lines get the best contiguous run. That is a branch inside one
    function, not a separate helper.

### Phase 2 - Cap and improve the all-matched diagnosis (decided in outline)

- Cap `FormatAllMatchedDiagnosis`: at most ~5 hits per snippet line, ~2 KB in total (well under the
  15,360-byte offload threshold), and drop snippet lines that match more than N source lines
  (boilerplate such as `{`, `};`, `return new DocumentEditResult`; N is open, see below).
- Order the message so `errorCode` and the actionable instruction come first and the summary ("7 of 7
  lines located individually") second; any truncation then loses the least useful content.
- Add a closest-contiguous-window candidate: the best run of snippet lines appearing in order in the
  source, reporting the source line range and the first snippet line where it diverges, with the
  divergent source line quoted for verbatim copy. Do not write a second copy of the matching logic.
- **Share it with `DiffEngine.ReanchorHunk`** (`RoslynSentinel.Common/DiffEngine.cs:294`), which
  already does best-leading-prefix alignment with a divergence message. That method mixes three
  concerns:
  1. **Alignment (generic).** It tries an ordered list of start positions, scores each with
     `MatchLeadingCount` (trimmed line compare), stops at the first full match and otherwise keeps
     the best partial match.
  2. **Diff-specific input.** Anchor lines come from the hunk's context/removal lines, and the
     candidates are the declared line +/- `HunkReanchorWindow`, nearest first.
  3. **Divergence wording.** "Matched the first N line(s) starting at line L, but then expected X at
     line M - found Y". This fits `ContextHelper` almost verbatim.
- **Extract (1) and the wording core of (3)** into a shared `Common` helper, e.g.
  `LineBlockAligner.FindBestPrefixAlignment(lines, needleLines, orderedCandidates)`. It returns
  `(Position, MatchedCount, IsFullMatch)`, plus a formatter for the divergence sentence.
  - `DiffEngine` keeps (2) and passes its nearest-first window.
  - `ContextHelper` passes the lines where needle line 1 is located, or every line if none.
  - Both are already in `Common`, so there is no layering issue.
- **Constraints:**
  - **Candidate order.** The helper must honour the caller's candidate order and never sort it.
    `DiffEngine` depends on nearest-first, so it returns the closest full match rather than the
    first in the file. The existing `DiffEngineTests` must stay green unchanged, in particular
    `ApplyDiff_Size60ModelEvalTranscript_ReplaysRecordedFailingDiff`. Do the extraction as its own
    refactor slice, with no behaviour change, before `ContextHelper` adopts the helper.
  - **Leading-prefix blind spot.** Alignment counts matching lines from the needle's first line, so
    a snippet whose first line is wrong but whose later lines are right scores 0. A
    longest-contiguous-run-at-any-offset mode would cover that. Add it only if a real failure needs
    it; the turn-3 case is fully covered by prefix alignment once phase 1 lands.
- Review the caps on `FormatPartialMatchDiagnosis` and `FormatZeroMatchedDiagnosis` for the same
  failure mode.
- Test: a boilerplate-heavy snippet against a file with hundreds of per-line hits produces an error
  under the cap that still starts with the errorCode and instruction.
- Test: a multi-line snippet with one stale middle line reports the closest window and the divergent
  line, through the shared aligner.

### Phase 3 - Selectors on ReplaceSnippet that the error message hands back (design, open questions below)

Add two optional parameters to ReplaceSnippet (exact matcher only; the first cut does not touch the
tolerant matcher):

- `withinMember`: the enclosing member name. Survives line drift between the Ambiguous response and
  the retry, and matches how the model thinks about the edit ("the one in `InsertMemberAfterAsync`").
- `matchLine`: the 1-based start line of the intended match. Self-verifying: if the line is stale, the
  call fails and lists the current match lines; it never silently edits a different match.

The Ambiguous message then lists, per match: line, enclosing member, and the ready-to-copy selector
values, e.g. "Match 1 (line 2458, in InsertMemberAfterAsync): withinMember="InsertMemberAfterAsync"
matchLine=2458". This replaces "use a different tool or a longer oldContent" with options the tool
actually implements, consistent with the rule that copy-verbatim values come from the tool, not from
the model's memory. Relates to the memory idea "line-number fallback anchor" (not designed;
`project_linenumber_fallback_anchor_idea.md`), which `matchLine` would supersede for ReplaceSnippet.

### Phase 4 - Deprecate lineBefore/lineAfter on ReplaceSnippet (open)

Once phase 3 ships, deprecate `lineBefore`/`lineAfter` on ReplaceSnippet (keep accepted but drop from
the schema/description, or remove; open question). Keep them on the single-line `contextSnippet` tools
for now. Whether to roll `withinMember`/`matchLine` out to the other ~26 tools and retire the
adjacent-line filter altogether is folded into the deferred "`contextSnippet` deprecation tracking"
item (`docs/current/TODO.md:256`); this proposal does not decide it.

## Alternatives considered, not pursued

- **Only widen `oldContent` (the advice the tool gives today).** Loses on three counts: `newContent`
  must repeat the added context, `oldContent` is capped at 2000 chars (the model exceeded it on turn
  13), and it cannot separate matches whose neighbourhoods are also identical. It also was the path
  that triggered the 94 KB error, because widening exposed the filter bug. Widening remains valid; it
  just must not be the only documented option.
- **Fix the filter only (phase 1) and stop.** Phase 1 would have made the turn-3 call succeed, but one
  adjacent line still cannot separate identical neighbourhoods, and it keeps the selector burden on the
  model (it must read the neighbouring text and retype it). Kept as phase 1 because it is cheap and
  corrects a documented contract; not sufficient alone.
- **`occurrence: N` index.** Unstable (the Nth match changes as soon as another edit adds or removes a
  matching site) and invisible to the model unless the error prints it. `matchLine` carries the same
  information, is printed by the Ambiguous message, and verifies itself against the current file.
- **Name `ApplyUnifiedDiff` as the fallback in the message.** It is in the on-demand `moveExtract`
  toolset, so naming it without a way to enable it strands the model (memory
  `feedback_dont_name_gated_tools_in_agent_docs`). Selectors on the tool the model is already using
  avoid the tool switch altogether.

## Open questions

- **Overloads, nested types, local functions:** is `withinMember: "Name"` enough, or must it be paired
  with `matchLine` as a tie-break, or accept a signature form (`Name(int,string)`)? Open.
- **`matchLine` in `ReplaceSnippetBatch`:** line numbers shift after earlier edits in the same batch.
  Should `matchLine` always resolve against the pre-batch snapshot? That needs checking against how the
  batch applies edits (`WorkspaceFileEditImpl.cs:787`); not verified.
- **Tolerant matcher / engine tools:** should the tolerant matcher get `withinMember` too, or does
  `SyntaxTargetResolver`'s name resolution already cover it? Open.
- **Remove vs. hide `lineBefore`/`lineAfter` on ReplaceSnippet:** schema token cost saved versus
  breaking existing callers, prompts and model-eval assertions. Open.
- **Phase 2 boilerplate threshold N** (drop snippet lines matching more than N source lines) and the
  exact byte/hit caps are starting values, not tuned against real runs.

## Cost / risk

- **Phase 1** touches `ContextHelper` (Common), so every caller of the shared filter is in the blast
  radius, though single-line behaviour is intended to be bit-identical. Risk: a multi-line caller that
  accidentally depended on the first-line anchoring now selects differently. Mitigation: check the ~25
  tolerant-matcher call sites and `SyntaxTargetResolver` for multi-line snippets before landing; add a
  tolerant-path test.
- **Phase 2** changes error text. Any test or model-eval assertion that matches the old "located
  individually" wording must be updated. Capping loses some candidates; the closest-window suggestion
  is meant to compensate.
- **Phase 3** adds two parameters to a high-traffic tool's schema (token cost for every session in
  which ReplaceSnippet is active) and needs enclosing-member resolution inside the exact matcher,
  which today is text-only and has no syntax-tree dependency. That is the main design cost.
- **Phase 4** is a schema/contract change; sequencing it after phase 3 keeps a working selector
  available at every step.
- It makes harder: nothing structural, but two selector families (adjacent-line and member/line) will
  coexist across the tool surface until the TODO.md:256 deprecation item is decided.
- Verification note for the implementer/reviewer split: the observed failure was reproduced from a
  transcript, not from a test. The planner owns specifying the phase 1 tests above (multi-line
  `lineAfter` on the exact path, the distinct error, the Ambiguous message); the reviewer confirms
  they exercise `FilterByAdjacentLinesPredicate` with a multi-line match rather than only the
  single-line cases that already pass.

## Related

- Source blocker: `docs/current/blockers/blocking_error_replacesnippet_notfound_diagnosis_uncapped_94kb.md`
  (its "Suggested direction" items 0-3 are covered here; items 4-5 - `Member(replace)` rejection
  wording and the SubAgent identical-call loop break - are not).
- `docs/current/plans/plan_linebefore_lineafter_single_line_contract.md` - the contract whose
  "immediately after contextSnippet" wording holds only for single-line snippets.
- `docs/current/proposal_universal_symbol_resolver.md` - prior unification (symbol names and hints
  only).
- `docs/current/TODO.md:256` - deferred "contextSnippet deprecation tracking".
- Memory: `project_linenumber_fallback_anchor_idea.md`, `feedback_dont_name_gated_tools_in_agent_docs.md`.
