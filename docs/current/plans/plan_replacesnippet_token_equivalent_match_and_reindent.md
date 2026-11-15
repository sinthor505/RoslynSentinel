# Plan: ReplaceSnippet accepts token-equivalent oldContent and re-indents newContent to the file

**Status:** APPROVED 2026-11-14. Owner settled R1-R4, lanes, step order, Step 10 scope and the eval set (see "Owner decisions (2026-11-14) on the plan review"); Batched mode, owner present.

## Problem

Source of truth: [proposal_replacesnippet_token_equivalent_match_and_reindent.md](../proposals/proposal_replacesnippet_token_equivalent_match_and_reindent.md) (committed 4177207c). In short: 94 of 2,003 `ReplaceSnippet` calls in the 2026-11-14 runs were rejected as `MatchedLooserThanRequested`, almost all for indentation; and `newContent` is spliced verbatim, so model tabs and wrong widths are written even by exact matches.

Verified against the source on 2026-11-14 (line numbers drift; a peer session was editing the solution while this plan was written, workspaceVersion 4 -> 12):

- `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs:991`
  `private static (SnippetMatch Match, string? Note) ResolveEditMatch(SourceText sourceText, string oldContent, string? lineBefore, string? lineAfter, Func<int, string?>? memberNameAt, string? withinMember, int? matchLine)`.
  It calls `SnippetResolver.Resolve(..., WhitespaceTolerance.IgnoreLineEndings, SnippetCaseMode.CaseSensitive, new SnippetSelectors(...), memberNameAt)`, returns on `Matched`, retries once without `matchLine` on `SelectorMismatch` with one pre-filter match, and otherwise throws `SnippetErrorFormatter.Build(result, sourceText, oldContent, "oldContent", memberNameAt, selectorsSupported: true, adjacentLinesSupported: false)`. Blast radius: private, 2 call sites, both in this file: single path `:1316` (`root` from `document.GetSyntaxRootAsync` at `:1310`), batch path `:1636` (root only inside `MemberNameResolverFor(...)` at `:1617`).
- Splice + EOL: single `:1346` `oldText.ToString().Remove(match.Start, match.Length).Insert(match.Start, newContent)`, then `EolUtilities.NormalizeEol(newFileContent, targetEol)` `:1349`; `liftSpans` uses `new TextChange(new TextSpan(match.Start, match.Length), newContent)`. Batch: highest-offset-first loop `:1691`, `NormalizeEol` `:1695`, `liftSpans[canonicalPath]` `:1706`. Notes: the single path prefixes `selectorNote` to the apply summary; the batch path collects `selectorNotes` (`batchEdits[i]: ...`) and prefixes them. The validate action returns before the summary on both paths (no notes there, by existing design).
- `AppliedEditMemory` (nested, private, `:209`): `Remember(string path, string? oldContent, string? newContent, string changeId)` (1 call, `:1451`) and `FindDuplicate(string path, string? oldContent, string? newContent, string fileText)` (2 calls, in the duplicate catch filter and its body near `:1323`). `FindDuplicate` requires the file to contain `newContent` verbatim (EOL-normalized), so a re-indented write would break the duplicate-apply no-op.
- `RoslynSentinel.Common/SnippetMatching/SnippetResolver.cs`: `public static SnippetMatchResult Resolve(SourceText sourceText, string snippet, WhitespaceTolerance tolerance, SnippetCaseMode caseMode, SnippetSelectors? selectors, Func<int, string?>? memberNameAt)`. The looser probe keeps only `SnippetNearMatch(LowestTier, StartLine, ActualLines, Differences)` (no span, capped at `MaxNearMatches = 5`, first looser tier only), so the token step cannot use the result's near-matches; it must re-run the matchers. The matchers `IndentationMatcher` and `CollapsedWhitespaceMatcher` (`SnippetMatchers.cs:164`, `:123`), `SnippetMatchResult.Create` (`internal`, `SnippetMatchResult.cs:258`) and `SnippetSelectorFilter.Apply(SourceText, string, SnippetMatchResult, SnippetSelectors, Func<int, string?>?)` (`internal`, `SnippetSelectors.cs:36`) are internal to `RoslynSentinel.Common`. `SnippetMatchResult.WithAmbiguity(SnippetMatchResult source, SnippetMatchOutcome outcome, IReadOnlyList<SnippetMatch> matches)` is public.
- `public sealed record SnippetMatch(int Start, int Line, int Length, WhitespaceTolerance Tier, string MatcherName)`; its doc says Tier is "never looser than the requested tier". `WhitespaceTolerance.cs` summary says near-matches "are never edit candidates"; the `IgnoreIndentation` doc says its span excludes the first line's indentation, the `IgnoreAllWhitespace` window stage starts at the line start.
- Descriptions: `McpToolSchemaPatcher.ApplyReplaceSnippetLimits` (`RoslynSentinel.Common/McpToolSchemaPatcher.cs:493` oldContent "Verbatim text to find and replace using literal substring match. ...", `:497` newContent "Verbatim replacement text for oldContent. ..."); the constants they override, `ToolParams.OldContent` (`ToolParams.cs:75`) and `ToolParams.NewContent` (`:83`), used by `WorkspaceTools.ReplaceSnippetTool` (`WorkspaceTools.cs:159-160`); batch item fields `SnippetEdit.OldContent` / `NewContent` `[BatchItemField(...)]` (`BatchTypes.cs:569`, `:572`, "matched exactly against the file's original content"). No test pins any of these strings.
- Success DTO: `public record ReplaceSnippetResult(ApplyChangesResult? ReplacementResult, DiagnosticReport? ValidationReport, string? DiffContent, string? ChangeId = null)` (`RoslynSentinel.Common/ReplaceSnippetResult.cs`). Not changed by this plan.
- References: `RoslynSentinel.Common.csproj` references `Microsoft.CodeAnalysis.CSharp.Workspaces` 5.9.0 (so `SyntaxFactory.ParseTokens`, `Formatter`, `Document`, `OptionSet` are all available in Common); `Engines.Basic` and `Tools.Basic` get them through Common. `Formatter.GetFormattedTextChanges(SyntaxNode, IEnumerable<TextSpan>, Workspace, OptionSet, CancellationToken)`, `Document.GetOptionsAsync(CancellationToken)` and `FormattingOptions.UseTabs/IndentationSize/TabSize` exist in the 5.9.0 package docs (`Microsoft.CodeAnalysis.Workspaces.xml`); whether any is `[Obsolete]` was not verifiable (see Risks).
- `WhitespaceRatchetTests.IsRawFormatterCall` (`RoslynSentinel.Tests.Basic/WhitespaceRatchetTests.cs:191`) counts only `NormalizeWhitespace`, `Formatter.FormatAsync` and `Formatter.Format`; `GetFormattedTextChanges` is not counted.
- Existing tests that flip: `ReplaceSnippetLooserTierTests.ReplaceSnippet_Single_OldContentDiffersOnlyByIndentation_FailsAsLooserTierShowsFileLinesAndWritesNothingAsync` and `..._Batch_...` (`RoslynSentinel.Tests.Tools.Basic`), and `ReplaceSnippetErrorCodeTests.ReplaceSnippet_Batch_LooserTierAndNotFoundTogether_ReportsAmbiguousCodeAndWritesNothingAsync` (its `MemberDupLooserBlock` becomes token-equivalent). Resolver-level `MatchedLooserThanRequested` tests (SnippetResolverTests, SnippetCandidateTests, SnippetErrorFormatterTests, ContextHelper*, SnippetResolverCrossAssemblyTests) are unaffected: the resolver does not change. The always-on indent pass can change the written text of other tests: 123 `ReplaceSnippet` call sites in 19 test files across `RoslynSentinel.Tests` and `RoslynSentinel.Tests.Tools.Basic`.

## Decision

Two pure helpers in `RoslynSentinel.Common/SnippetMatching/` (layering: they need the internal matchers, `SnippetMatchResult.Create` and `SnippetSelectorFilter`, all internal to Common, and Common already references the Roslyn workspace packages; no MCP types are used), wired into `WorkspaceFileEditImpl` in `Tools.Basic`. No new parameter, no new tool, no change to `SnippetResolver`, `ReplaceSnippetResult` or any other `SnippetResolver` caller.

1. **`SnippetTokenEquivalence` (new).** Runs only when the `IgnoreLineEndings` resolve returns `MatchedLooserThanRequested`. Finds candidates with the `IndentationMatcher` and `CollapsedWhitespaceMatcher` (union, all spans, uncapped), accepts a candidate when its tokens equal `oldContent`'s, normalizes its span, applies the selectors, then the tie-break. Accepted matches are `SnippetMatch` with `MatcherName = SnippetTokenEquivalence.MatcherName` and `Tier` = the tier of the matcher that found the candidate. The resulting `SnippetMatchResult` goes through the existing `ResolveEditMatch` logic (Matched / matchLine retry / `SnippetErrorFormatter.Build`), so `Ambiguous` errors keep today's wording.
2. **`NewContentIndenter` (new).** Splices the edits itself (`SourceText.WithChanges`) and re-indents only the leading whitespace of the inserted lines: formatter path when the edited text parses, delta-shift fallback otherwise. All changes stay inside the inserted spans, so each edit's final inserted text is well defined and is used for `liftSpans`. Runs on every successful `ReplaceSnippet` match (exact ones included), before `NormalizeEol`, for apply and validate alike.
3. **Facts in the success summary**, through the existing summary-prefix mechanism (the `Note` returned by `ResolveEditMatch` and the batch `selectorNotes` list): a token-equivalent match and a re-indent each add one fact sentence. No DTO change.
4. **Descriptions** say indentation in `oldContent` need not match and `newContent` is re-indented to the file.

### Owner decisions (2026-11-14) on the proposal's open questions

- **Span convention:** when the candidate's first token (or first comment) is the first non-whitespace text on its line, the accepted span starts at that line's start (whole lines; the span owns the indentation). Otherwise (mid-line start) it starts at the first token and the file's text before it on that line stays. The span ends at the end of the last token; if `oldContent` ends with a line break, the span extends through the end of that file line including its line break.
- **Both "added while verifying" refinements accepted:** the comparison is checked against the file's own syntax tree (a candidate whose span starts or ends inside a token, string literal tokens included, is rejected), and the indent pass skips lines whose start lies inside a string literal token.
- **Comments and preprocessor directives** are trivia that must match: their text is compared with leading and trailing whitespace trimmed; only whitespace and end-of-line trivia is forgiven. (Plan detail: a multi-line comment or doc comment trivia is compared line by line, each line trimmed; see Risks R4.)
- **Block-comment continuation lines:** the formatter path leaves them as written; the fallback shift applies the same delta to them.
- **`#region` and other directive lines:** treated like code lines by the indent pass (the formatter decides).
- **Other snippet-anchored tools and `ApplyDiff`:** out of scope.
- **Tie-break:** among several token-equivalent candidates (after selectors), if exactly one has leading whitespace identical to `oldContent` on every line, take it; otherwise `Ambiguous` (or `StillAmbiguous` after selectors), as today.

### Owner decisions (2026-11-14) on the plan review

- **R1:** option (b): override `UseTabs`, `IndentationSize` and `TabSize` with `InferIndentUnit(originalText)` whenever the file has indented lines; document options (`.editorconfig`) only when inference is inconclusive.
- **R3:** option (a): leave the ratchet, comment at the new call.
- **R4:** per-line trim inside multi-line comments and doc comments (as planned).
- **R5, R6, R7:** plan recommendations accepted (union of looser tiers; no `IOperationResult`; heuristic kept, measured by eval class 4).
- **Lanes:** Steps 2-6 go to `implementer-senior` (algorithmic bodies); the rest stay `implementer` D1 / `test-writer` as written.
- **Order:** Step 8 (indent pass) lands before Step 7 (token step), so no window writes unindented token-matched text. Step 8 depends on 6 only; its `TokenMatchWithUnindentedNewContent` test moves to Step 7. Step 7 depends on 3 and 8.
- **Step 10 scope:** all description sites, split into 10a (Common) and 10b (Tools.Basic); wording says indentation and the amount of spacing need not match (not "whitespace between tokens", which overstates what the candidate finders locate).
- **Step 13:** ShippingKind aliases run with `-Step 5`; V13 (same-code V7 control) is added.
- **Mode:** Batched (one orchestrator per batch), owner present: mid-run decisions are asked with a recommendation.

### Algorithms (the contract the body slices implement)

**Token comparison** (`AreTokenEquivalent(string snippet, string candidateText, CSharpParseOptions options)`): lex both with `SyntaxFactory.ParseTokens(text, options: options)` including the end-of-file token. Equivalent when the token counts match and each pair has the same `Kind()` and `Text`, and the non-whitespace trivia of each pair (every trivia except `WhitespaceTrivia` and `EndOfLineTrivia`, leading and trailing, in order) has the same count and kinds, with text equal after splitting each trivia's `ToFullString()` on line breaks and trimming each line. Options come from the file tree (`(CSharpParseOptions)fileRoot.SyntaxTree.Options`) so raw strings and newer syntax lex the same way.

**Tree alignment** (`TokensAlignWithTree(SyntaxNode fileRoot, int candidateTextStart, string candidateText, CSharpParseOptions options)`): every token of `ParseTokens(candidateText)` (except the zero-width end-of-file token), mapped to file positions by adding `candidateTextStart`, must start at the `Span.Start` of a real tree token (`fileRoot.FindToken(pos).Span.Start == pos`) and end at the `Span.End` of a real tree token (`fileRoot.FindToken(end - 1).Span.End == end`). This rejects a candidate that starts or ends inside a token, inside a string literal (including interpolated string text and raw strings) or inside comment trivia, while still accepting a whole interpolated string (one lexer token spanning several tree tokens).

**Candidate acceptance** (`TryAccept`): `candidateText` = the file text from the first to the last non-whitespace character inside the candidate span. Accept when `TokensAlignWithTree` and `AreTokenEquivalent(oldContent, candidateText)` both hold; then build the accepted span per the owner span convention. Accepted spans are de-duplicated by (Start, Length).

**Resolve**: candidates -> accepted (none: return null, caller throws the original looser-tier error) -> `SnippetMatchResult.Create(IgnoreLineEndings, CaseSensitive, Matched|Ambiguous, accepted, [], 0, [], n, n, n, tiersProbed: [IgnoreLineEndings, IgnoreIndentation, IgnoreAllWhitespace])` -> `SnippetSelectorFilter.Apply` when selectors are non-null -> on `Ambiguous`/`StillAmbiguous`, tie-break over `Matches`; exactly one winner -> `SnippetMatchResult.WithAmbiguity(result, SnippetMatchOutcome.Matched, [winner])`.

**Tie-break** (`HasIdenticalLeadingWhitespace`): for each `oldContent` line i (after dropping fully blank leading and trailing lines, as the matchers do), its leading whitespace equals that of file line (accepted start line + i); line 1 is skipped when the accepted span starts mid-line.

**Indent pass, eligible lines** (`EligibleLines`): the lines of the spliced text that intersect an inserted span, excluding blank (whitespace-only) lines, lines whose start lies strictly inside a token's span (string literals of every kind; any multi-line token), and, when `skipBlockCommentLines` is true (formatter path), lines whose start lies strictly inside a multi-line comment trivia. Line 1 of an insertion is eligible only if the text before the insertion on that line is whitespace-only (possibly empty). Directive lines are eligible like code lines.

**Formatter path**: used when the spliced tree has no more syntax errors than the original tree and no syntax error intersects an inserted span. Changes = `Formatter.GetFormattedTextChanges(splicedRoot, eligibleLineSpans, document.Project.Solution.Workspace, options, ct)` with `options` = `await splicedDocument.GetOptionsAsync(ct)` (see Risks R1/R2 for the indentation-option override). `IndentsFromFormatterChanges` keeps a change only if it ends exactly at an eligible line's first non-whitespace character and either (a) its new text has no line break and its span starts at or after that line's start, or (b) its old and new text contain the same number of line breaks; the line's new indentation is the new text's segment after its last line break, which must be spaces/tabs only. Every other change, and every other part of a kept change, is discarded. Any exception from the formatter -> fallback.

**Fallback shift** (`IndentsFromShift`): indent unit from `InferIndentUnit(originalText)`: tabs if most indented lines start with a tab, else spaces; space width = the most common positive difference between consecutive non-blank lines' indentation widths, clamped 1..8, default 4 (tab width 4 for arithmetic). Each leading tab of a `newContent` line counts as one unit. Per edit: `fileBase` = width of the leading whitespace of the original line containing `edit.Span.Start`. If the text before `edit.Span.Start` on that line is whitespace-only: `snippetBase` = width of the leading whitespace of `newContent`'s first non-blank line; `delta = fileBase - snippetBase`; that first line's target is `fileBase`; every later eligible line's target is its own width + `delta` (clamped at 0). Otherwise (mid-line start): line 1 is not touched; if the smallest width among non-blank later lines is >= `fileBase` the lines are taken as already absolute (delta 0), else `delta = fileBase`. Block-comment continuation lines are shifted too (eligible lines computed with `skipBlockCommentLines: false`). Targets are written in the file's style (tabs: width / unit tabs plus remaining spaces).

**Applying** (`ApplyIndents`): replaces each eligible line's leading whitespace with its target. For line 1 of an insertion that starts after a non-empty whitespace prefix P, only the inserted part may change: the new inserted leading whitespace is target minus P when target starts with P; otherwise line 1 is left as written. Returns the full text, each edit's final inserted text (input order), the count of lines whose whitespace changed, and whether the fallback ran.

## Execution rules

- All C# reads and writes through the RoslynSentinel MCP tools (CLAUDE.md dog-fooding table). `Search` each file for `TODO` before editing it.
- Every step ends compile-green: `Build` 0 errors (level `solution` unless stated). Implementers never run tests; the dispatcher runs the named tests after each step with `RunTest`, one at a time. Test baseline is 0 failures; any failure is new.
- Tier: depth `implementer` at tier D1 (files <= 3, edits <= 15, tool calls <= 35), per `.claude/implementer-tiers.json`; Steps 2-6 go to `implementer-senior` (owner decision), whatever their own Lane line says.
- Run order (owner decision): 1, {2 + 4}, {3 + 5}, 6, {8 + 10a}, 10b, 7, 9, 11, 12, 13.
- New test methods follow NUnit 5 conventions (`await` async asserts); fixtures `[TestFixture]`, `[Parallelizable(ParallelScope.All)]` like `ReplaceSnippetLooserTierTests`.
- Concurrency: at most two implementing agents at once; pairs are marked per step. A peer session may be editing `WorkspaceFileEditImpl.cs`: check `Git(status)` / `ExternalFileDrift` before Steps 7-9 and follow the `peer-coordination` skill on a conflict.
- Commit each step with a `Plan: plan_replacesnippet_token_equivalent_match_and_reindent` trailer; the last code step carries `Closes:` only after Step 12 is reported.

## Steps

### Step 1 - Contract skeleton for both helpers
- Files: `RoslynSentinel.Common/SnippetMatching/SnippetTokenEquivalence.cs` (new), `RoslynSentinel.Common/SnippetMatching/NewContentIndenter.cs` (new)
- Depends on: none
- Effect: text-neutral (no callers)
- Lane: `implementer`, D1. Two `CreateFile` calls, one per file.
- Change: create both files with final signatures and bodies `throw new NotImplementedException(); // TODO implement (Step n)`, XML doc summaries taken from the Algorithms section above.
  ```csharp
  // SnippetTokenEquivalence.cs  -- usings: Microsoft.CodeAnalysis, Microsoft.CodeAnalysis.CSharp, Microsoft.CodeAnalysis.Text
  namespace RoslynSentinel.Common.SnippetMatching;
  public static class SnippetTokenEquivalence
  {
      public const string MatcherName = "TokenEquivalence";
      public static SnippetMatchResult? Resolve(SourceText sourceText, SyntaxNode fileRoot, string snippet,
          SnippetSelectors? selectors, Func<int, string?>? memberNameAt);                                   // Step 3
      internal static IReadOnlyList<SnippetMatch> FindLooserCandidates(SourceText sourceText, string snippet);  // Step 3
      internal static SnippetMatch? TryAccept(SourceText sourceText, SyntaxNode fileRoot, string snippet,
          SnippetMatch candidate);                                                                          // Step 3
      internal static bool AreTokenEquivalent(string snippet, string candidateText, CSharpParseOptions options); // Step 2
      internal static bool TokensAlignWithTree(SyntaxNode fileRoot, int candidateTextStart, string candidateText,
          CSharpParseOptions options);                                                                       // Step 2
      internal static bool HasIdenticalLeadingWhitespace(SourceText sourceText, SnippetMatch accepted, string snippet); // Step 3
  }

  // NewContentIndenter.cs  -- usings: Microsoft.CodeAnalysis, Microsoft.CodeAnalysis.Formatting, Microsoft.CodeAnalysis.Text
  namespace RoslynSentinel.Common.SnippetMatching;
  public sealed record NewContentIndentResult(string Text, IReadOnlyList<string> InsertedTexts, int LinesAdjusted, bool UsedFallback);
  public static class NewContentIndenter
  {
      public static Task<NewContentIndentResult> ReindentAsync(Document document, IReadOnlyList<TextChange> edits,
          CancellationToken cancellationToken);                                                             // Step 6
      internal static (bool UseTabs, int Width) InferIndentUnit(SourceText originalText);                   // Step 4
      internal static IReadOnlyList<int> EligibleLines(SourceText splicedText, SyntaxNode splicedRoot,
          IReadOnlyList<TextSpan> insertedSpans, bool skipBlockCommentLines);                               // Step 4 (0-based line numbers)
      internal static NewContentIndentResult ApplyIndents(SourceText splicedText, IReadOnlyList<TextSpan> insertedSpans,
          IReadOnlyDictionary<int, string> indents, bool usedFallback);                                     // Step 4
      internal static IReadOnlyDictionary<int, string> IndentsFromFormatterChanges(SourceText splicedText,
          IReadOnlyList<int> eligibleLines, IEnumerable<TextChange> formatterChanges);                      // Step 5
      internal static IReadOnlyDictionary<int, string> IndentsFromShift(SourceText originalText, SourceText splicedText,
          IReadOnlyList<TextChange> edits, IReadOnlyList<TextSpan> insertedSpans, IReadOnlyList<int> eligibleLines,
          (bool UseTabs, int Width) unit);                                                                  // Step 5
  }
  ```
  `ReindentAsync`'s contract: `document` is the pre-edit document; `edits` are in original-text coordinates, non-overlapping, any order (sorted inside); the result `Text` is the spliced and re-indented text with the edits' own line endings (the caller normalizes EOL afterwards).
  The `CreateFile` for `NewContentIndenter.cs` carries this in its `reason`:
  ```
  NEW-DTO: NewContentIndentResult (Result, Common.SnippetMatching)
  Evaluated: SnippetMatchResult - a match outcome with selectors and near-matches, wrong role; ApplyChangesResult - a write outcome (files, validation), wrong role; RoslynFormattingHelper returns only Document/string, no per-edit inserted texts or adjusted-line count
  Wire impact: none (internal helper result, never serialized)
  ```
- Done when: `Build` 0 errors.

### Step 2 - Token comparison and tree alignment bodies (pair with Step 4)
- Files: `RoslynSentinel.Common/SnippetMatching/SnippetTokenEquivalence.cs`, `RoslynSentinel.Tests/SnippetTokenEquivalenceTests.cs` (new), `RoslynSentinel.Common/SnippetMatching/SnippetMatchResult.cs` (doc only)
- Depends on: 1
- Effect: text-neutral
- Lane: `implementer`, D1 (2 body replacements, 1 doc edit, 1 `CreateFile`).
- Change: implement `AreTokenEquivalent` and `TokensAlignWithTree` per Algorithms. In `SnippetMatchResult.cs`, extend the `SnippetMatch` `Tier` param doc: "never looser than the requested tier, except for a match whose MatcherName is SnippetTokenEquivalence.MatcherName: that one was found at the looser Tier and accepted because its C# tokens equal the snippet's." Tests (`RoslynSentinel.Tests`, class `SnippetTokenEquivalenceTests`):
  - `AreTokenEquivalent_TabsVersusSpacesIndentation_ReturnsTrue`
  - `AreTokenEquivalent_DifferenceInsideVerbatimString_ReturnsFalse` (proposal case: difference inside a multi-line verbatim string)
  - `AreTokenEquivalent_DifferenceInsideRawString_ReturnsFalse` (multi-line raw string)
  - `AreTokenEquivalent_CommentTextDiffers_ReturnsFalse` and `AreTokenEquivalent_CommentIndentationDiffers_ReturnsTrue`
  - `AreTokenEquivalent_RegionDirectiveIndentationDiffers_ReturnsTrue`
  - `AreTokenEquivalent_InterpolatedStringSameText_ReturnsTrue`
  - `TokensAlignWithTree_CandidateStartsInsideStringLiteral_ReturnsFalse` (proposal case: candidate inside a string literal in the file)
  - `TokensAlignWithTree_CandidateStartsMidToken_ReturnsFalse`
  - `TokensAlignWithTree_WholeStatements_ReturnsTrue`
- Done when: `Build` 0 errors; dispatcher runs `SnippetTokenEquivalenceTests` (all pass).

### Step 3 - Candidates, acceptance, tie-break and Resolve bodies (pair with Step 5)
- Files: `RoslynSentinel.Common/SnippetMatching/SnippetTokenEquivalence.cs`, `RoslynSentinel.Tests/SnippetTokenEquivalenceTests.cs`, `RoslynSentinel.Common/SnippetMatching/WhitespaceTolerance.cs` (doc only)
- Depends on: 2
- Effect: text-neutral (still no production caller)
- Lane: `implementer`, D1 (4 body replacements, 1 doc edit, 1 batch of appended tests).
- Change: implement `FindLooserCandidates` (`new IndentationMatcher().FindAll(...)` and `new CollapsedWhitespaceMatcher().FindAll(...)` with `SnippetCaseMode.CaseSensitive`, union), `TryAccept` (span convention), `HasIdenticalLeadingWhitespace`, `Resolve` per Algorithms. In `WhitespaceTolerance.cs`, add to the enum summary's contract paragraph: near-matches are never edit candidates, with one exception: `ReplaceSnippet` re-checks looser-tier candidates with `SnippetTokenEquivalence` and edits one only when its C# tokens equal the snippet's (whitespace and end-of-line trivia are the only differences forgiven). Tests appended to `SnippetTokenEquivalenceTests`:
  - `Resolve_TabsWhereFileHasSpaces_AcceptsWholeLineSpan` (proposal: tabs accepted)
  - `Resolve_DroppedIndentation_Accepts` (proposal: dropped indentation accepted)
  - `Resolve_WrongIndentWidth_Accepts` (proposal: wrong width accepted)
  - `Resolve_IndentDifferenceInsideMultiLineRawString_ReturnsNull` (proposal: difference inside raw/verbatim string rejected)
  - `Resolve_CandidateOnlyInsideStringLiteral_ReturnsNull` (proposal: candidate inside a string literal not accepted)
  - `Resolve_TwoTokenEquivalentCandidates_IsAmbiguous` (proposal: two candidates Ambiguous)
  - `Resolve_TwoCandidatesOneWithIdenticalLeadingWhitespace_TakesIt` (proposal: tie-break; the winner block has the snippet's exact indentation but a trailing-space difference so the strict tier missed it)
  - `Resolve_MidLineStart_SpanStartsAtFirstToken`
  - `Resolve_SnippetEndsWithLineBreak_SpanIncludesLineBreak`
  - `Resolve_WithMatchLineSelector_PicksThatCandidate`
- Done when: `Build` 0 errors; dispatcher runs `SnippetTokenEquivalenceTests`.

### Step 4 - Indenter: unit inference, eligible lines, applying (pair with Step 2)
- Files: `RoslynSentinel.Common/SnippetMatching/NewContentIndenter.cs`, `RoslynSentinel.Tests/NewContentIndenterTests.cs` (new)
- Depends on: 1
- Effect: text-neutral
- Lane: `implementer`, D1 (3 body replacements, 1 `CreateFile`).
- Change: implement `InferIndentUnit`, `EligibleLines`, `ApplyIndents` per Algorithms. Tests (`RoslynSentinel.Tests`, class `NewContentIndenterTests`, trees from `CSharpSyntaxTree.ParseText`):
  - `InferIndentUnit_FourSpaceFile_ReturnsSpacesFour`, `InferIndentUnit_TabFile_ReturnsTabs`, `InferIndentUnit_NoIndentedLines_DefaultsToFourSpaces`
  - `EligibleLines_LinesInsideMultiLineVerbatimString_AreSkipped` (proposal: newContent lines inside a multi-line string not re-indented)
  - `EligibleLines_LinesInsideRawString_AreSkipped`
  - `EligibleLines_BlockCommentContinuation_SkippedOnlyWhenRequested`
  - `EligibleLines_MidLineInsertion_FirstLineNotEligible`
  - `ApplyIndents_WhitespacePrefixOnFirstLine_ChangesOnlyInsertedPart`
  - `ApplyIndents_ReturnsInsertedTextsInInputOrder`
- Done when: `Build` 0 errors; dispatcher runs `NewContentIndenterTests`.

### Step 5 - Indenter: formatter-change filter and fallback shift (pair with Step 3)
- Files: `RoslynSentinel.Common/SnippetMatching/NewContentIndenter.cs`, `RoslynSentinel.Tests/NewContentIndenterTests.cs`
- Depends on: 4
- Effect: text-neutral
- Lane: `implementer`, D1 (2 body replacements, 1 appended test batch).
- Change: implement `IndentsFromFormatterChanges` and `IndentsFromShift` per Algorithms. Tests appended (formatter changes are hand-built `TextChange` lists, no workspace):
  - `IndentsFromFormatterChanges_PureIndentChange_IsKept`
  - `IndentsFromFormatterChanges_ChangeSpanningLineBreakAndIndent_KeepsTrailingIndent`
  - `IndentsFromFormatterChanges_IntraLineSpacingChange_IsDiscarded`
  - `IndentsFromFormatterChanges_ChangeThatJoinsLines_IsDiscarded`
  - `IndentsFromShift_LineStartSpan_ShiftsByBaseDifference` (proposal: fallback path)
  - `IndentsFromShift_TabsConvertedToFileUnit`
  - `IndentsFromShift_MidLineStartWithAbsoluteIndent_LeavesLaterLines` (proposal: mid-line start, no double indent, fallback)
  - `IndentsFromShift_MidLineStartWithRelativeIndent_AddsFileBase`
  - `IndentsFromShift_BlockCommentContinuation_GetsSameDelta`
- Done when: `Build` 0 errors; dispatcher runs `NewContentIndenterTests`.

### Step 6 - Indenter: ReindentAsync orchestration (pair with Step 7)
- Files: `RoslynSentinel.Common/SnippetMatching/NewContentIndenter.cs`, `RoslynSentinel.Tests/NewContentIndenterTests.cs`
- Depends on: 5
- Effect: text-neutral (still no production caller)
- Lane: `implementer`, D1 (1 body replacement, 1 appended test batch).
- Change: implement `ReindentAsync`: return the plain splice unchanged when `!document.SupportsSyntaxTree` or there are no edits; sort edits; `spliced = originalText.WithChanges(sorted)`; inserted spans in spliced coordinates (running offset); `splicedDocument = document.WithText(spliced)`; parse check; formatter path or fallback per Algorithms; catch formatter exceptions into the fallback (never rethrow); `ApplyIndents`. Add the comment at the `GetFormattedTextChanges` call: indentation-only use of the formatter's text changes; nothing else from them is written (whitespace ratchet does not count this call, see the plan's Risks R3). Tests appended (documents from an `AdhocWorkspace`, as in `RoslynFormattingHelper.FormatAnnotatedTextAsync`):
  - `ReindentAsync_ExactMatchWithTabIndentedNewContent_ConvertsToFileIndent` (proposal: indent pass on an exact match with tab-indented newContent)
  - `ReindentAsync_RegionDoesNotParse_UsesShiftFallback` (proposal: fallback on a region that does not parse; asserts `UsedFallback`)
  - `ReindentAsync_MidLineStart_DoesNotDoubleIndentLaterLines` (proposal: mid-line start)
  - `ReindentAsync_NewContentWithVerbatimStringLines_LeavesThemAsWritten` (proposal: string lines not re-indented)
  - `ReindentAsync_IntraLineSpacingAndBraces_LeftAsWritten`
  - `ReindentAsync_CorrectlyIndentedNewContent_ReportsZeroLinesAdjusted`
  - `ReindentAsync_TwoEdits_InsertedTextsMatchInputOrder`
- Done when: `Build` 0 errors; dispatcher runs `NewContentIndenterTests`.

### Step 7 - Wire the token step into ResolveEditMatch (runs after Step 8; owner decision)
- Files: `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`, `RoslynSentinel.Tests.Tools.Basic/ReplaceSnippetLooserTierTests.cs`, `RoslynSentinel.Tests.Tools.Basic/ReplaceSnippetErrorCodeTests.cs`
- Depends on: 3, 8
- Also: add `ReplaceSnippet_Single_TokenMatchWithUnindentedNewContent_WritesFileIndentationAsync` (moved from Step 8) to `ReplaceSnippetLooserTierTests`. If that makes the dispatch exceed D1, hand the new tests to `test-writer` after the code lands.
- Effect: behaviour-changing: an `oldContent` that differs from the file only in whitespace trivia now applies instead of failing `MatchedLooserThanRequested`; the apply summary gains one fact sentence.
- Lane: `implementer`, D1. Blast radius of the changed member: `ResolveEditMatch`, private, 2 call sites in the same file.
- Change:
  - `ResolveEditMatch`: add parameter `SyntaxNode? fileRoot` after `sourceText` (via `MethodSignature` or a `Member` replace that also updates both calls). Wrap the resolve in a local function `ResolveWithTokenStep(SnippetSelectors selectors)`: `SnippetResolver.Resolve(...)` as today; if the outcome is `MatchedLooserThanRequested` and `fileRoot` is not null and `SnippetTokenEquivalence.Resolve(sourceText, fileRoot, oldContent, selectors, memberNameAt)` returns non-null, use that result. Use it for the first resolve and for the matchLine retry. When the returned match's `MatcherName == SnippetTokenEquivalence.MatcherName`, append to the note: `$"oldContent matched line {line} by its C# tokens; its whitespace differs from the file's, and the file's lines {first}-{last} were replaced."` (join with the matchLine note by a space when both exist). Update the method's XML summary (the "near-matches are never edit candidates" sentence gets the token exception).
  - Single call (`:1316`): pass `root`. Batch (`:1617`/`:1636`): capture `var fileRoot = await document.GetSyntaxRootAsync(cancellationToken);`, pass it to `MemberNameResolverFor` and `ResolveEditMatch`.
  - `ReplaceSnippetLooserTierTests`: rename and flip the two looser-tier tests to `ReplaceSnippet_Single_OldContentDiffersOnlyByIndentation_AppliesAndNotesTokenMatchAsync` and `ReplaceSnippet_Batch_OldContentDiffersOnlyByIndentation_AppliesAsync` (expect `IsError` false and `h.Text == Source.Replace(FileBlock, Replacement)`; single also asserts the summary contains "by its C# tokens"); add `ReplaceSnippet_Single_OldContentWithTabsWhereFileHasSpaces_AppliesAsync`; add `ReplaceSnippet_Single_LooserMatchStartingMidToken_StillFailsAsLooserTierAsync` (`"otal = Compute();\nLog(total);"`, expects `MatchedLooserThanRequested` and an unchanged file); add `ReplaceSnippet_Single_IndentDifferenceInsideVerbatimString_StillFailsAsLooserTierAsync` (second fixture file with a multi-line verbatim string; proposal: string difference rejected, end to end). Update the file header comment.
  - `ReplaceSnippetErrorCodeTests`: change `MemberDupLooserBlock` to `"nt Compute() => 1;\n\nvoid Log(int x) { }"` (starts mid-token, so it stays looser-tier only) and its comment; the test keeps asserting `Ambiguous`.
- Done when: `Build` 0 errors; dispatcher runs `ReplaceSnippetLooserTierTests`, `ReplaceSnippetErrorCodeTests`, `SnippetResolverCrossAssemblyTests`.

### Step 8 - Wire the indent pass into the single and batch paths (pair with Step 10a; runs before Step 7)
- Files: `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`, `RoslynSentinel.Tests.Tools.Basic/ReplaceSnippetReindentTests.cs` (new)
- Depends on: 6
- Effect: behaviour-changing: every successful `ReplaceSnippet` (apply and validate) writes `newContent` with the file's indentation; the apply summary gains a fact sentence when lines were adjusted.
- Lane: `implementer`, D1 (about 6 edits in one file + 1 `CreateFile`).
- Change:
  - Single path: replace the splice at `:1346` with `var reindent = await NewContentIndenter.ReindentAsync(document, [new TextChange(new TextSpan(match.Start, match.Length), newContent)], cancellationToken); var newFileContent = reindent.Text;` (`NormalizeEol` stays after it). `liftSpans` uses `reindent.InsertedTexts[0]` instead of `newContent`. When `reindent.LinesAdjusted > 0`, prefix the apply summary (beside `selectorNote`) with `$"newContent indentation was adjusted to the file's on {reindent.LinesAdjusted} line(s); returnDiff shows the written text."`.
  - Batch path: replace the highest-offset-first splice loop (`:1688-1692`) with one `ReindentAsync(document, resolved.Select(r => new TextChange(new TextSpan(r.match.Start, r.match.Length), r.newContent)).ToList(), cancellationToken)` call; `spliced = reindent.Text`; `liftSpans[canonicalPath]` uses `reindent.InsertedTexts[i]` in `resolved` order; add `$"{DisplayPath(filePathResolved)}: newContent indentation was adjusted to the file's on {n} line(s); returnDiff shows the written text."` to `selectorNotes` when `n > 0`. Keep the overlap check before the call (`SourceText.WithChanges` throws on overlap). `newContentsByFile`, `_rejectedEdits` and `_inFlightEdits` keep the model's own `newContent`.
  - Tests (`RoslynSentinel.Tests.Tools.Basic`, class `ReplaceSnippetReindentTests`, `InMemoryWorkspace` harness as in `ReplaceSnippetLooserTierTests`):
    - `ReplaceSnippet_Single_ExactMatchTabIndentedNewContent_WritesFileIndentationAsync` (proposal: indent pass on an exact match, end to end)
    - `ReplaceSnippet_Batch_ExactMatchTabIndentedNewContent_WritesFileIndentationAsync`
    - `ReplaceSnippet_Single_MidLineOldContent_SecondLineNotDoubleIndentedAsync` (proposal: mid-line start, end to end; the 2026-11-09 journal case)
    - (moved to Step 7: `ReplaceSnippet_Single_TokenMatchWithUnindentedNewContent_WritesFileIndentationAsync`)
    - `ReplaceSnippet_Single_IndentAdjusted_SummaryStatesLineCountAsync`
    - `ReplaceSnippet_Single_CorrectlyIndentedNewContent_WritesVerbatimAndNoNoteAsync`
- Done when: `Build` 0 errors; dispatcher runs `ReplaceSnippetReindentTests`, then every test in the 19 files that call `ReplaceSnippet` (`ReplaceSnippetErrorCodeTests`, `ReplaceSnippetRenameLiftTests`, `EditImpactSpanAttributionTests`, `ReplaceSnippetBatchTests`, `ReplaceSnippetCrossFileRejectionTests`, `ReplaceSnippetSizeGuardTests`, `ReplaceSnippetLooserTierTests`, `StaleReadWriteToolsTests`, `RejectedSnippetEditTests`, `ReplaceSnippetNoTypeLeftGuardTests`, `FileNotFoundConsumerPathTests`, `PathCaseLookupRegressionTests`, `ReplaceSnippetDuplicateApplyTests`, `EolInheritanceTests`, `StaleReadEngineToolsTests`, `WholeFileApplyResultShapeTests`, `ReplaceSnippetValidateRejectionTests`, `SnippetMissFactsAndOptionsTests`, `UndoLastApplyConcurrentEditTests`). Failures go to Step 11.

### Step 9 - Duplicate-apply memory knows the written text
- Files: `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`, `RoslynSentinel.Tests/ReplaceSnippetDuplicateApplyTests.cs`
- Depends on: 8
- Effect: behaviour-changing (restores today's no-op): an identical `ReplaceSnippet` sent twice is still a no-op success when the first write was re-indented.
- Lane: `implementer`, D1 (3 edits in `AppliedEditMemory` + call site, 1 test).
- Change: `AppliedEditMemory` entries gain `WrittenContent`; `Remember(string path, string? oldContent, string? newContent, string changeId, string? writtenContent = null)` stores `writtenContent ?? newContent`; `FindDuplicate` keeps its signature but checks, per matching (oldContent, newContent) entry, that the file contains that entry's written content (move the file-contains check inside the loop). Call site `:1451` passes `reindent.InsertedTexts[0]`. Blast radius: `Remember` 1 call, `FindDuplicate` 2 calls (unchanged), all in this file. Test: `ReplaceSnippet_SameReindentedEditSentTwice_SecondIsNoOpSuccessAsync`.
- Done when: `Build` 0 errors; dispatcher runs `ReplaceSnippetDuplicateApplyTests`.

### Step 10a - Descriptions in Common (pair with Step 8)
- Files: `RoslynSentinel.Common/McpToolSchemaPatcher.cs`, `RoslynSentinel.Common/ToolParams.cs`, `RoslynSentinel.Common/BatchTypes.cs`
- Depends on: none (text only); schedule with Step 8 so the wire text and the behaviour land in the same run.
- Effect: behaviour-changing (tool schema text)
- Lane: `implementer`, D1 (about 7 `ReplaceSnippet` edits).
- Change (facts only, per `reference_tool_error_text_facts_options_policy.md`; keep the limit sentences as they are). Wording rule (owner decision): say indentation and the amount of spacing need not match; never claim arbitrary whitespace between tokens is forgiven (`x=1` against `x = 1` and different line breaks still do not match):
  - `McpToolSchemaPatcher.cs:493` oldContent: `"Text to find and replace. Indentation and the amount of spacing need not match the file; identifiers, operators, comments, string contents and line structure must. Limit: ..."`.
  - `:497` newContent: `"Replacement text for oldContent. Its indentation is adjusted to the file's; spacing within lines is kept as written. Limit: ..."`.
  - `ToolParams.OldContent` / `ToolParams.NewContent` (`:75`, `:83`): the same first sentences with their existing "Default limit of 2000 characters." tail.
  - `ToolParams.SnippetEdits`: if its text says the edits match exactly or verbatim, align it with the same facts (read it first; leave it if it says nothing about matching).
  - `SnippetEdit.OldContent` / `NewContent` `[BatchItemField]` text and `<summary>` (`BatchTypes.cs:568-572`): the same first sentences (NewContent keeps "May be empty (pure deletion) or longer than OldContent (net insertion).").
- Done when: `Build` 0 errors; `Search(mode: text, query: "literal substring match|matched exactly against the file")` over `RoslynSentinel.Common` returns only `TextSearchResult.cs`.

### Step 10b - Tool descriptions in Tools.Basic
- Files: `RoslynSentinel.Tools.Basic/WorkspaceTools.cs`, `RoslynSentinel.Tools.Basic/EditSnippetsTools.cs`
- Depends on: 10a (same wording); run after Step 8 commits (both are in Tools.Basic, so not as a pair with 8).
- Effect: behaviour-changing (tool description text)
- Lane: `implementer`, D1 (2 edits).
- Change: `ReplaceSnippetTool` `[Description]` (`WorkspaceTools.cs:152`, "Replaces one exact text block with another") and `EditSnippetsTool` `[Description]` (`EditSnippetsTools.cs:23`, "Replaces exact text blocks"): replace "exact" with the facts of 10a in one short clause (indentation and the amount of spacing need not match; newContent is re-indented to the file). Keep the rest of each description.
- Done when: `Build` 0 errors; `Search(mode: text, query: "Replaces one exact text block|Replaces exact text blocks")` returns 0 matches.

### Step 11 - Existing test drift from the always-on indent pass (conditional)
- Files: only the test files that failed in Step 8's run (at most 3 per dispatch; split by file otherwise)
- Depends on: 8
- Effect: text-neutral (tests only)
- Lane: `test-writer`.
- Change: for each failure, decide from the diff whether the new text is the intended re-indent (update the expected text and add a one-line comment naming this plan's behaviour) or a defect (stop, leave the test red, report it with the input and the written text; a defect goes back to Step 5/6 as an `implementer-senior` fix).
- Done when: the Step 8 test list passes, or every remaining failure is reported as a defect.

### Step 12 - Stub search, full build, live restart, baselines
- Files: none
- Depends on: 2-11
- Effect: none
- Lane: main session (plain calls, no implementer).
- Change: `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Common/SnippetMatching/*.cs")` returns 0 matches (fail otherwise). `Build(level: fullBuild)`; run `RunTest` for `SnippetTokenEquivalenceTests`, `NewContentIndenterTests`, `WhitespaceRatchetTests`, `SnippetResolverTests`, `SnippetErrorFormatterTests` and the Step 8 list; compare with the baselines (0 failures). Restart the server (`McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`, `LoadSolution`) and dog-food one token-equivalent and one re-indent `ReplaceSnippet` on a scratch edit (undo it with `UndoLastApply`); journal the result.
- Done when: no stubs, 0 build errors, 0 new test failures, both live calls behave as specified.

### Step 13 - Eval run (proposal slice e)
- Files: none (results under `ModelTestingResults/113/`)
- Depends on: 12, with the plan's commits on master
- Effect: none (measurement)
- Lane: main session (queues and reads; log analysis may be delegated to `model-eval-log-analyst`). Never `roslynsentinel-modeleval.ps1` directly on the shared host.
- Change: queue on host 113 through `scripts/roslynsentinel-evalqueue.ps1 submit -HostAddress 113 -Model <key> -Session <name> -Purpose 'plan_replacesnippet_token_equivalent_match_and_reindent eval'`, for each model key of the 2026-11-14 baseline (qwen3.5-4b and qwen3.5-9b; `-Model` is mandatory): `-Test ShippingKindStepV7`, `V8`, `V9`, `V10`, `V11` and `V13` (the same-code V7 control), each with `-Step 5` (aliases from fa7be962, `scripts/roslynsentinel-modeleval.ps1:396-401`; owner decision) and `-Test OrderPricingRefactorChain10` (the ten-chain eval, `Model_AppliesTenChainedRefactors`). Use the same repeat counts as the baseline runs. Wait with `evalqueue wait -Id ...` in the background.
- Outcome classes reported per run set (not pass/fail only): (1) `ReplaceSnippet` calls rejected `MatchedLooserThanRequested`, per difference class, against 94 / 2,003 baseline (target near zero); (2) calls accepted by tokens (summary contains "by its C# tokens"); (3) calls with indentation adjusted; (4) `ValidationFailed` attributable to re-indentation (target zero; each one traced to a turn and the written diff); (5) task pass rate versus the 2026-11-14 baseline; (6) any formatter-style surprise (lambda bodies, initializers, switch expressions: the proposal's unmeasured hypothesis), with the written text.
- Done when: an eval doc under `docs/current/evals/` (template `finding.md`) reports the six classes with run ids; the plan's Status line is updated.

## Pairs

| Pair | Files | Projects |
| --- | --- | --- |
| Step 2 + Step 4 | SnippetTokenEquivalence.cs, SnippetMatchResult.cs, SnippetTokenEquivalenceTests.cs / NewContentIndenter.cs, NewContentIndenterTests.cs | Common + Tests (same projects, disjoint files) |
| Step 3 + Step 5 | SnippetTokenEquivalence.cs, WhitespaceTolerance.cs, SnippetTokenEquivalenceTests.cs / NewContentIndenter.cs, NewContentIndenterTests.cs | Common + Tests (same projects, disjoint files) |
| Step 8 + Step 10a | WorkspaceFileEditImpl.cs, ReplaceSnippetReindentTests.cs / McpToolSchemaPatcher.cs, ToolParams.cs, BatchTypes.cs | Tools.Basic + Tests.Tools.Basic / Common |

Sequential only (owner order 2026-11-14): 1 -> {2,4}; 2 -> 3; 4 -> 5 -> 6; 6 -> 8; {3,8} -> 7; 10a -> 10b (after 8 commits); 8 -> 9; 8 -> 11; all -> 12 -> 13. The former 6 + 7 pair is gone (7 now follows 8).

## Out of scope

- Other snippet-anchored tools (`Member` `contextSnippet` lookups and similar) and `ApplyDiff` / `ApplyUnifiedDiff` (owner decision 2026-11-14).
- `SnippetResolver`, its ladder and its other callers; the looser-tier message wording (`SnippetErrorFormatter.BuildLooser`, dispatched separately).
- `validateOnApply` behaviour.
- A full-format pass of the edited region (only leading whitespace of inserted lines changes).
- `ReplaceSnippetResult` shape: the new facts ride in the existing summary text.

## Risks and open decisions

- **R1 - Indentation options source (open, recommendation given).** The proposal says "the document's options, including .editorconfig". Without an `.editorconfig`, Roslyn defaults to 4 spaces, so in a 2-space or tab file the formatter would indent inserted lines inconsistently with the file. Options: (a) document options only (proposal text); (b) override `FormattingOptions.UseTabs`, `IndentationSize` and `TabSize` with `InferIndentUnit(originalText)` whenever the file has indented lines; (c) override only when the document has no `.editorconfig` value. Recommendation: (b), because the proposal's premise is that the file's own text is the authority on indentation; it is one `WithChangedOption` chain in Step 6. Step 6 implements (b) unless the owner picks otherwise before it runs.
- **R2 - Obsolete API check (unverified).** `Document.GetOptionsAsync`, `OptionSet.WithChangedOption` and `FormattingOptions.*` exist in the 5.9.0 docs; whether they carry `[Obsolete]` could not be checked (the package assembly could not be loaded beside pwsh's own Roslyn). If Step 6's build shows CS0618 on them, keep the call, leave `// TODO replace obsolete formatting options API` and report it; no other step depends on the answer.
- **R3 - Whitespace ratchet scope.** `WhitespaceRatchetTests` does not count `Formatter.GetFormattedTextChanges`, so the new call passes the ratchet silently although the ratchet's comment says new formatting goes through `RoslynFormattingHelper`. Options: (a) leave it, with the Step 6 comment (only indentation is taken from the changes and nothing else is written); (b) move the call into a new `RoslynFormattingHelper` member and extend the ratchet to count `GetFormattedTextChanges` with an allowlist entry (the allowlist "only shrinks" rule makes this an owner call). Recommendation: (a) now, (b) as a TODO.md item if the owner wants the ratchet airtight.
- **R4 - Comment comparison granularity (interpretation).** The owner decision says comments are compared with leading/trailing whitespace trimmed. The plan trims per line inside a multi-line comment or doc comment trivia, so a doc comment re-indented as a whole still matches; a whole-text trim would reject every indentation difference inside multi-line comments. Confirm or switch to whole-text trim (one line in `AreTokenEquivalent`).
- **R5 - Union of looser tiers.** The plan collects candidates from both looser matchers (not only the lowest tier that matches, as the resolver's probe does), so a token-equivalent candidate found only by the collapsing matcher is not missed. Side effect: in rare files a block may become `Ambiguous` where a lowest-tier-only search would have found one. Recommendation: keep the union (ambiguity is then real by tokens).
- **R6 - NewContentIndentResult and IOperationResult.** `dto-conventions` section 4 asks operation results to implement `IOperationResult`. This helper has no failure state (formatter failures fall back internally, nothing is reported as an error), so the plan does not implement it. Owner may prefer `IOperationResult` for uniformity; it would add a `Status` with only one value in use.
- **R7 - Fallback heuristic for mid-line starts.** "Absolute if the smallest later-line width >= fileBase, else relative to column 0" is a heuristic; a newContent whose later lines are all nested deeper than line 1 but written relative to column 0 with a large indent could be misread. Only the fallback (non-parsing region) uses it; the formatter path does not. Covered by the two Step 5 tests; measured by eval class (4).
- **R8 - Formatter style mismatch (proposal risk, unmeasured).** Lambda bodies, initializers and switch expressions may be indented differently from the author's style, now also on exact matches. Mitigations: indentation-only filter, the summary fact, `returnDiff`. Measured by Step 13 class (6); if it shows up, the fallback-only alternative for such lines is a follow-up decision.
- **R9 - Always-on blast radius.** The indent pass runs on every successful `ReplaceSnippet`; Step 8's run covers 123 call sites in 19 test files (an upper bound: error-path tests never splice and most fixtures are single-line or already correctly indented, so a handful of expected-text changes is the likely drift), and Step 11 exists for it. A defect found there blocks Step 12.
- **R10 - Concurrent edits.** A peer session was editing the solution during planning; `WorkspaceFileEditImpl.cs` line numbers above may shift. Steps 7-9 locate code by member and snippet, not by line number.
- **Cross-plan baseline:** Step 13 compares against the 2026-11-14 eval numbers in the proposal; the separately dispatched looser-message rewording does not affect the counted outcome classes.
