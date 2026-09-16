# Finding: "not found" / "does not exist" wording is model-confusing across every ContextHelper-backed locator

**Status (2026-09-15/16):** Category A (ContextHelper.cs's two throw sites + exception taxonomy) and
the Category B compiler-diagnostic labeling item are FIXED, via
`docs/current/plans/plan_contexterrorbuilder_orienting_guidance.md` Steps 1-4 - see that plan doc
for what shipped (`ContextErrorBuilder`, `SnippetMatchOutcome`, `DiagnoseNoMatch`, and `[COMPILER
ERROR]` labels on all 5 `CompilerErrorLookupHelper.DescribeAsync` dump sites in
`SentinelWorkspaceTools.cs` - one more site than originally scoped, found via `SearchSolutionText`
during implementation: `ReplaceSnippetBatch` ~966 in addition to the 3 originally cited, plus
`CreateFile` ~1077 found during this pass, for 5 total).

**Still open (deferred, not started):** the plan's Step 5 - "Summary of distinct labeling targets
(Category B)" item 2 above, the "position resolved but wrong node kind" family repeated across
`GranularRefactoringEngine.cs`, `MappingEngine.cs`, `SemanticRefactoringLibrary.cs`,
`MsToolAugmentEngine.cs`, `CodeGenerationEngine.cs`, plus the related raw-`ex.Message`-propagation
sites in `SymbolNavigationEngine.cs`/`ImpactAnalyzer.cs`/`RefactoringEngine.cs`. Explicitly lower
priority per the plan; deferred as a separately-scoped follow-up sweep (~7 files, a dozen-plus
distinct sites) rather than folded into this pass. Full raw findings below remain the source
inventory for that follow-up.

## What's broken

Every snippet/locator tool built on `ContextHelper`'s literal-text matching (`FindExactSnippetPosition`,
`FindSnippetPosition(WithLength)`, and the many engines that call them) reports a failed match using
"not found" / "does not exist" wording. That phrasing reads to a model as "the content itself is
gone" (deleted, renamed, never existed), when the real condition is almost always "the literal text
you supplied did not align to anything in the file" — a matching problem, not an existence problem.
A weak model reading only the error text can wrongly conclude it should stop editing or that a
prior edit already landed, when what it actually needs to do is re-read the file and retry with a
corrected snippet.

A second, structurally distinct problem was found alongside it: several failure paths that are
**not** matching failures at all (a compiler diagnostic from a hypothetical edit; a malformed
`lineBefore`/`lineAfter` argument; a resolved position with the wrong syntax-node kind at it) share
the same "not found"/"does not exist" message shape as genuine no-match errors, with no label to
tell the two apart. `ReplaceSnippet`'s `action=validate` path is the case that originally surfaced
this: `"ReplaceSnippet validate failed: The name 'entries' does not exist in the current context"`
looks identical in shape to a snippet-not-found error, but is actually raw Roslyn compiler-diagnostic
text describing what the *hypothetical edit* would break — the snippet matched fine.

## Root causes (highest leverage first)

1. **`RoslynSentinel.Common/ContextHelper.cs:216`** (`FindSnippetPositionWithLength`) and **:322**
   (`FindExactSnippetPosition`) throw `ToolNotFoundException` with `"contextSnippet not found: ..."`
   / `"contextSnippet not found verbatim: ..."`. Nearly every other Category-A finding below is this
   same message, propagated or lightly re-wrapped by a caller — fixing these two throw sites (and
   giving them a distinct exception/error code, see #2) fixes the wording almost everywhere by
   inheritance.
2. **`RoslynSentinel.Common/ToolException.cs:39-51`** — `ToolNotFoundException`'s doc comment
   itself conflates two different failure shapes under one exception type and one error code
   (`ToolErrorCode.NotFound`): real absence (file/symbol/type genuinely doesn't exist) vs. a
   context-snippet literal-text match that failed. `ToolErrorMapper.ToCodeAndMessage`
   (`ToolException.cs:141-188`) only prefixes the tool/action name, never the failure category, so
   both come out with identical `ErrorCode = "NotFound"` and no way to distinguish them short of
   parsing message text. Recommend a new exception/code (e.g. `SnippetNotMatchedException` /
   `ToolErrorCode.SnippetNotMatched`) so the distinction is structural, not just prose.

## Category legend (used throughout the raw findings)

- **(A)** Genuine no-match (literal/fuzzy text search failed) phrased ambiguously — needs
  "match not located" style rewording.
- **(B)** A different kind of failure (compiler diagnostic, real symbol/file absence, IO error,
  malformed argument, wrong-node-kind-at-resolved-position) that's unframed and could be confused
  with (A) — needs a distinguishing prefix/label, not a reword of the underlying message.

## Confirmed out of scope (reviewed, correctly excluded)

- `NoSearchMatchesException` / `SearchSolutionText` (`WorkspaceReadNavigationImpl.cs:505-511`) —
  already frames itself correctly as a text-search outcome, distinct from symbol lookup.
- `BuildFileNotFoundError` (`SentinelWorkspaceTools.cs:1832-1854`) — genuine file-path existence
  check.
- `SentinelSymbolTools.cs`, `SentinelDocumentationTools.cs`, `SentinelAdvancedRefactoringTools.cs` —
  their "not found" wording is backed by exact identifier-text or file-path lookups
  (`.Identifier.Text == name`, `File.Exists`, semantic `GetTypeHierarchyAsync`), never
  `contextSnippet` fuzzy matching. Verified down to each call site's underlying engine method
  signature.

## Raw audit findings (full Explore-agent report, unedited)

### RoslynSentinel.Common/ContextHelper.cs (the root cause - highest priority)

- **Line 216** - `FindSnippetPositionWithLength`: `throw new ToolNotFoundException($"contextSnippet not found: \"{contextSnippet.Trim()}\"");`
  **(A)** - "not found" reads as "this text/thing doesn't exist in the file," when the real condition is "no substring/whitespace-collapsed/CRLF-normalized match for the literal you supplied." A model reading only this could conclude the code it's trying to edit was already changed/deleted.

- **Line 322-324** - `FindExactSnippetPosition`: `throw new ToolNotFoundException($"contextSnippet not found verbatim: \"{contextSnippet.Trim()}\". Re-read the file and copy oldContent exactly (including whitespace) from the current content - approximate/retyped text is not accepted here.");`
  **(A)** - Better than line 216 (does explain the remediation), but still leads with "not found verbatim," which a weak model can anchor on as "content is gone" rather than "your string didn't match." This is the exact exception that reaches ReplaceSnippet's core anchoring path (`FindExactSnippetPosition` calls, used by both single-edit and batch ReplaceSnippet).

- **Lines 229-236 / 337-344** - `ToolAmbiguousMatchException`: `"contextSnippet is ambiguous ({matches.Count} matches): ..."` and `"contextSnippet is still ambiguous ({matches.Count} matches remain): ..."`
  Not itself a no-match wording problem (ambiguous != not found, and the text is already clear that the content DOES exist, just non-uniquely) - no fix needed here, but worth preserving this framing as a template for the reworded not-found messages.

- **Lines 455-459** - `ThrowIfMultiLine`: `throw new ToolNotFoundException($"{parameterName} must be a single line, but the supplied value spans multiple lines: ...");`
  **(B)** - This is a genuine caller-input-shape validation error (lineBefore/lineAfter contains embedded newline), not a matching failure at all, yet it's thrown as `ToolNotFoundException` (the same exception type used for real no-match). A model catching/reading only the exception type or a generic "not found" bucket could misclassify this as "the adjacent line doesn't exist" rather than "you passed a malformed value." Needs at minimum a distinguishing prefix (e.g. "Invalid argument:") since the message body itself is already fine.

### RoslynSentinel.Common/ToolException.cs (exception taxonomy)

- **Line 39-51** - `ToolNotFoundException` class doc: *"A named file, symbol, type, member, project, or context snippet does not exist where the caller said it would."*
  **(B)** - The doc comment itself conflates two very different failure shapes under one exception type/error code (`ToolErrorCode.NotFound`): (1) real absence (file/symbol/type truly doesn't exist) and (2) a context-snippet literal-text match that failed. Both surface through `ToolErrorMapper` with the identical `ErrorCode = "NotFound"` and no distinguishing label. This is the structural root of the confusion: the taxonomy has no separate code/exception for "snippet text didn't match" vs "name doesn't exist." Recommend a new exception type/error code (e.g. `SnippetNotMatchedException` / `ToolErrorCode.SnippetNotMatched`) so downstream mapping can attach a distinct prefix instead of only rewording free text.

- **Lines 141-188** - `ToolErrorMapper.ToCodeAndMessage`: `if (ex is ToolException toolEx) { return (toolEx.ErrorCode, $"{context} failed: {toolEx.Message}"); }`
  **(B)** - The mapper prefixes only the tool/action name (e.g. "ReplaceSnippet apply failed: ..."), never the failure *category*. A `ToolNotFoundException` from a real absent symbol and one from a failed snippet match both come out as `"{context} failed: {message}"` with `ErrorCode = "NotFound"` - nothing here lets a model (or even code) tell them apart short of parsing the message body. This is the central place a category-distinguishing prefix should be added once ContextHelper's exceptions are split out or tagged.

### RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs (ReplaceSnippet, ReplaceSnippetBatch, ApplyDiff)

- **Line 698, 887** - `ContextHelper.FindExactSnippetPosition(oldText, oldContent, lineBefore, lineAfter)` - unguarded call whose `ToolNotFoundException`/`ToolAmbiguousMatchException` (from ContextHelper lines 216/322/337) propagates to the outer `catch (Exception ex)` (line 745/762), which maps via `ToolErrorMapper.ToResultError(ex, ..., "ReplaceSnippet {action} for '{filePathResolved}'")`. **(A)** - Model-facing output becomes e.g. `"ReplaceSnippet apply for 'Foo.cs' failed: contextSnippet not found verbatim: \"...\""`. Same root wording problem as ContextHelper, unchanged by the wrapper.

- **Line 727-728** - genuine compiler-diagnostic path, `action=validate`/on-apply-validation-failure: `"ReplaceSnippet: the edit matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n" + await CompilerErrorLookupHelper.DescribeAsync(...)`.
  **(B) - this is the confirmed core "mislabeled/unframed non-match failure" case that prompted this audit.** The prose *before* the colon already explicitly disclaims a match failure ("the edit matched the target file") - good - but the diagnostics dump that follows (raw Roslyn diagnostic text, e.g. `"The name 'entries' does not exist in the current context"`) has no visual/structural separation or label (like `[COMPILER ERROR]`) distinguishing it as compiler output rather than a ContextHelper snippet-match message. Because both this path and the snippet-not-found path can appear from the same tool (`ReplaceSnippet`) with similarly shaped free text containing "does not exist," a model skimming only the tail of the message could conflate the two. Needs a distinguishing prefix/label on the diagnostics block itself, not a reword of the diagnostic text.

- **Line 890-893** (ReplaceSnippetBatch): `catch (ToolException toolEx) { perEditErrors.Add($"edits[{index}] ({filePathResolved}): {toolEx.Message}"); }` - propagates ContextHelper's raw message per-edit. **(A)** - inherits the same wording issue, multiplied across batch edits.

- **Line 1211** (ApplyDiff, files-changeset path, mirrors line 727): `"ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors - change not applied. Fix the issue(s) below and retry:\n" + ...` **(B)** - same finding as line 727-728, same fix needed (distinguishing label on the diagnostics block).

- **Line 1319** - identical pattern for the `changesetFormat: diff` path. **(B)** - same as above.

- **Lines 1832-1854** - `BuildFileNotFoundError`: `"'{requestedFileName}' does not exist at '{normalizedPath}'..."` / `"'{requestedFileName}' does not exist anywhere in the solution..."`
  Out of scope (genuine file-path existence check, not a snippet/text match) - confirmed correctly worded and not part of this bug class. Listed here only to document that it was reviewed and excluded.

### RoslynSentinel.Common/DiffEngine.cs (ApplyDiff's hunk anchoring - backs ApplyDiff, not ContextHelper, but same wording-confusion class)

- **Lines 125-128** - `DiffApplyException`: `"Malformed hunk header \"{line}\": expected the form ... Regenerate the diff with a well-formed header."` **(B)** - genuine structural/shape validation of the diff text itself (not a content-matching failure); already well-labeled ("Malformed hunk header"), low risk, listed for completeness.

- **Line 171** - `throw new DiffApplyException($"Line {currentLine + 1} out of bounds.");` **(A/B boundary)** - terse; doesn't say whether the file changed structurally or the diff is stale. Low-risk but could be clearer that this is a diff-vs-file mismatch, not "this line doesn't exist in the abstract."

- **Lines 177-182** - `throw new DiffApplyException($"hunk '{match.Value}' expected to remove \"{expected}\" at line {currentLine + 1}, but found \"{actual}\". The hunk's line numbers may be stale relative to hunks applied earlier in this same diff - regenerate the diff against the file's current content, or use a whole-member/whole-file replacement tool instead.");` **(A)** - reasonably well-framed already (explains stale line numbers, gives remediation), but "expected to remove X, but found Y" is adjacent to the same class: a model could still read "found Y instead" as "X doesn't exist" rather than "the diff's anchor is stale." Lower priority than ContextHelper's bare "not found" but worth aligning wording.

- **Lines 197, 204-209** - same pattern for context lines (`"Context line {currentLine + 1} out of bounds."` and `"hunk '{match.Value}' expected context \"{expected}\" at line {currentLine + 1}, but found \"{actual}\"..."`). Same classification as above.

- **Lines 360-364** - `ReanchorHunk` final failure: `throw new DiffApplyException($"hunk '{hunkHeader}' declares line {declaredLine + 1}, but its content wasn't found there or within {HunkReanchorWindow} lines in either direction. {mismatchDetail} Regenerate the diff against the file's current content, or use a whole-member/whole-file replacement tool instead.");` **(A)** - "content wasn't found there or within N lines" is the DiffEngine analogue of ContextHelper's "not found": a model could read this as "the target code is gone" rather than "the diff's anchor/context didn't match anywhere nearby." Already includes good remediation guidance, but the core "wasn't found" phrase should be aligned with whatever replacement wording is chosen for ContextHelper (e.g. "no matching anchor position located").

### RoslynSentinel.Basic/RefactoringEngine.cs (ContextHelper-backed member/type/expression locators)

- **Line 1668** (`ExtractConstantAsync`): `Message = $"// Error: {snippetError}"` - propagates `TryFindSnippetPosition`'s ContextHelper-originated error verbatim. **(A)**.

- **Line 1774** (`ExtractLocalVariableAsync`): `Message = $"// Error: {snippetError} Re-check the snippet against GetMethodSource/GetFileOutline output, or add lineBefore/lineAfter ... to disambiguate."` **(A)** - better than 1668 (adds actionable follow-up), but still leads with the raw ContextHelper "not found"/"ambiguous" text before the added guidance.

- **Line 4300** (`WrapInTryCatchAsync` catch block): `Message = $"// ContextSnippet error: {ex.Message}"` **(A)** - the `"ContextSnippet error:"` prefix is actually a good pattern (labels the category) but still surfaces ContextHelper's raw "not found" wording inside it.

- **Line 5075** (`WrapInRegionAsync` catch block): same pattern, `Message = $"// ContextSnippet error: {ex.Message}"`. **(A)**.

- **Lines 5148-5169** (`ResolveMemberByNameOrSnippet`), **5212-5232** (`ResolveMemberOrEnumMemberByNameOrSnippet`), **5431-5450** (`ResolveTypeByNameOrSnippet`): all three build `BuildMemberHint`/`BuildTypeHint` with `failureMode` string `"not found"` when `ContextHelper.FindAllSnippetMatches` returns zero matches, e.g.:
  `throw new InvalidOperationException(BuildMemberHint(candidates, matches, "not found"));` -> renders as `"contextSnippet not found (N candidates): line X \`...\`, line Y \`...\`. Provide a more specific contextSnippet or use lineBefore/lineAfter."`
  **(A)** - "contextSnippet not found" here is especially confusing because *named candidates with that name DO exist* (the message lists them) - the failure is purely that the literal snippet text didn't align to any of those already-known candidates. This is a strong instance of the bug: a model could read "not found" and think the member itself is missing, even though the same message enumerates the member's declaration sites.

- **Lines 5456, 5512** - `BuildMemberHint`/`BuildTypeHint` when `candidates.Count == 0`: `"contextSnippet {failureMode}: no candidates found."` **(B)** - this branch is a genuine real-absence case (no declaration with that name at all), correctly distinct in meaning from the snippet-mismatch branch above, but shares the exact same message shape/prefix (`"contextSnippet {failureMode}: ..."`) as the snippet-mismatch branch - needs a distinguishing prefix so the two don't read identically.

### RoslynSentinel.Basic/GranularRefactoringEngine.cs

- **Line 452** (`IntroduceFieldAsync`, unguarded `FindSnippetPosition` call) - exception propagates to caller uncaught within this method; ultimately surfaces via the MCP tool wrapper's generic catch -> `ToolErrorMapper`. **(A)**.
- **Line 461** - `Message = "// Expression not found."` (when `expression == null` after a *successful* position resolution) **(B)** - this is a distinct, correctly-scoped failure ("position resolved, but no ExpressionSyntax there") but phrased identically to a not-found-snippet message; a model can't tell this apart from "the snippet text itself wasn't found." Needs distinguishing wording/prefix (compare to RefactoringEngine.cs line 1804's much better framing of the same shape of failure).
- **Line 583** (`IntroduceParameterAsync`) - `TryFindSnippetPosition` guarded; **line 590**: `Message = $"// Error: {paramSnippetError}"`. **(A)**.
- **Line 602** - `Message = "// Expression not found."` - same **(B)** issue as line 461.
- **Line 696** (`IntroduceVariableAsync`, unguarded `FindSnippetPosition`). **(A)**.
- **Line 713** - `Message = "// Expression not found."` - same **(B)** issue as line 461/602 (three duplicate instances of this exact string in this file).

### RoslynSentinel.Basic/CodeGenerationEngine.cs

- **Line 1057, 1064** (property auto/full conversion): `var pos = ContextHelper.TryFindSnippetPosition(...); ... Message = $"// Error: {snippetError}"`. **(A)**.
- **Line 1077** - `Message = $"// Error: Property '{propertyName}' not found."` **(B)** - this is the *name-based* candidate resolution failing (real absence of a property with that name in this file), separate from the contextSnippet-disambiguation failure just above it (line 1064) - correctly distinct condition, but uses the same bare "not found" wording as the snippet-mismatch case one branch earlier, risking conflation between "no property named X" and "contextSnippet didn't match."
- **Line 1270, 1277** (string.Format extraction): same `TryFindSnippetPosition`/`"// Error: {snippetError}"` pattern. **(A)**.
- **Line 1292** - `Message = "// Error: No string.Format call found at the given context snippet."` **(B)** - position resolved successfully, but no matching invocation there; distinct condition from the snippet-match failure just above, needs its own framing (currently reads close enough to "not found" wording to blur together, though it does say "at the given context snippet" which somewhat anchors it - moderate priority).

### RoslynSentinel.Basic/MsToolAugmentEngine.cs

- **Lines 250-256** (`AnalyzeSwitchForPatternConversionAsync`): `catch (ToolException ex) { return new SwitchConversionAnalysis(false, 0, [], ex.Message); }` - raw ContextHelper message surfaced directly into `BlockingReason`. **(A)**.
- **Line 261** - `"No switch statement found at contextSnippet location."` **(B)** - position resolved, but wrong node kind there; distinct condition, needs its own framing (currently borderline acceptable since it says "at contextSnippet location," but still uses "not found"-adjacent "No ... found" phrasing that groups it visually with the true no-match case).
- **Line 313** (`ConvertSwitchToPatternSafeAsync`) - unguarded `FindSnippetPosition` call (relies on the earlier `AnalyzeSwitchForPatternConversionAsync` call at line 302 having already validated, but if called independently this would throw uncaught). **(A)**.
- **Lines 452-453, 471** (string.Format-related tool): `catch (ToolException ex) { return MsAugmentResult.Fail(ex.Message); }` **(A)**; and `"No string.Format() call found at contextSnippet location."` **(B)** (same shape as CodeGenerationEngine.cs line 1292).
- **Lines 752-756, 763-764** (foreach->LINQ analysis): same pair - `ex.Message` propagated raw **(A)**; `"No foreach statement found at contextSnippet location."` **(B)**.
- **Lines 1046-1047, 1056-1058** (`ExtractConstantSafeAsync`): `ex.Message` propagated raw **(A)**; `"No literal expression found at contextSnippet location. Provide a contextSnippet that directly contains or is adjacent to the literal."` **(B)** (position resolved, wrong node kind - reasonably explained with remediation, lower priority than the bare "not found" cases).
- **Lines 1455-1456** (extract-method-family tool): `catch (ToolException ex) { return MsAugmentResult.Fail(ex.Message); }` **(A)**.

### RoslynSentinel.Basic/MappingEngine.cs

- **Line 175** (`InvertAssignmentsAsync`), unguarded within a `try` whose **catch at lines 222-228** does: `catch (ToolException ex) { ... Message = $"// ContextSnippet error: {ex.Message}" ... }` **(A)** - same "labeled prefix but raw ContextHelper text inside" pattern as RefactoringEngine.cs's WrapInTryCatch/WrapInRegion.
- **Line 204** - `Message = $"// No assignment expressions found at the snippet location"` **(B)** - position resolved, but no matching node kind there; distinct condition using "not found"-adjacent wording ("No ... found").

### RoslynSentinel.Basic/SemanticRefactoringLibrary.cs

- **Line 300** (`WrapInUsingAsync(contextSnippet,...)`), guarded by **catch at 347-350**: `catch (ToolException ex) { ... Message = $"// ContextSnippet error: {ex.Message}" ... }` **(A)** - same pattern.
- **Line 320** - `Message = "// Error: No statements found at the snippet location."` **(B)** - position resolved but no statement node there; distinct condition, "not found"-adjacent wording.

### RoslynSentinel.Basic/ImpactAnalyzer.cs

- **Line 42** (`AnalyzeImpactAsync`) - unguarded `ContextHelper.FindSnippetPosition` call inside a broad **`catch (Exception ex)` at line 103-106**: `return new ImpactReport("", "", [], 0, 0, Error: ex.Message);` **(A)** - raw ContextHelper exception message (e.g. "contextSnippet not found: ...") surfaces directly as the report's `Error` field, completely unlabeled - not even a "ContextSnippet error:" prefix like the other engines use. Lowest-context example in the audit; highest risk of a model reading "not found" as "the symbol doesn't exist" since there's no surrounding text at all.
- **Line 61** - `Error: "No symbol found at the specified position."` **(B)** - position resolved (via ContextHelper successfully), but Roslyn's `SymbolFinder`/semantic model returned no symbol there; a genuinely distinct condition, reasonably worded already ("at the specified position" anchors it correctly), lower priority.

### RoslynSentinel.Basic/SyntaxUpgradeEngine.cs

- **Line 192, 199** (`UseNameofExpressionAsync`): `TryFindSnippetPosition(...); ... Message = $"// Error: {snippetError}"`. **(A)**.

### RoslynSentinel.Basic/SymbolNavigationEngine.cs

- **Line 343-348** (`GetSymbolInfoAsync`): `catch (ToolException ex) when (ex.Message.Contains("matched") || ex.Message.Contains("No match")) { return ErrorHoverInfo($"Snippet not found: {ex.Message}"); }` **(A)** - the added `"Snippet not found: "` prefix reintroduces "not found" wording on top of ContextHelper's own message; double negative-framing risk (both the prefix and the embedded message say "not found"/"no match").
- **Line 361-365** - `catch (ToolException ex) { return ErrorHoverInfo(ex.Message); }` - raw propagation, no prefix at all. **(A)**.
- **Line 386-387** - `"No symbol found at snippet '{contextSnippet}'. Try a snippet that includes the identifier directly (e.g. the method name or property name)."` **(B)** - position resolved successfully (ContextHelper matched, Roslyn's SymbolFinder found nothing there); genuinely distinct condition, already reasonably framed and includes remediation - low priority.
- **Lines 1443-1448, 1621-1622** - `FindCallers`-family: `"FindCallers: contextSnippet did not resolve to a symbol in '{filePath}'. This is NOT a confirmed zero-references result for '{symbolName}' - the lookup never ran. " + DescribeNameOnlyCandidates(...) + "Re-check the snippet against GetMethodSource/GetFileOutline output, or omit contextSnippet if the symbolName is unambiguous in this file."` - **Already well-framed** (explicitly disclaims "not found"/zero-results conflation, names the real cause). Listed as a positive reference example, not a finding requiring a fix.
- **Line 2098-2100** - `catch (ToolException) { // snippet not found in this document -> continue }` - internal control flow only, never surfaced to the model as text. Not a finding.

### RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs (SearchSolutionText)

- **Lines 505-511** - `NoSearchMatchesException`: `"No matches were found for '{pattern}' as either a literal substring or a regex pattern. Try adjusting the search pattern. If you were searching for a known symbol by name, use LocateSymbol instead (semantic lookup, not text matching)..."`
  Out of scope - this *is* a genuine "search ran, zero results" signal (distinct `NoSearchMatchesException`/`ToolErrorCode.NoMatches`), and the message already explicitly frames it as a text-search outcome, distinguishes it from symbol lookup, and suggests alternatives. No fix needed; confirmed reviewed and excluded per the audit's own carve-out for `NoSearchMatchesException`.

### RoslynSentinel.Server.Basic/SentinelSymbolTools.cs, SentinelDocumentationTools.cs, SentinelAdvancedRefactoringTools.cs - locator-style tools reviewed, found out of scope

All "not found"/"does not exist" wording found in these three files (LocateSymbol's `"Symbol '{symbolName}' not found in the solution"`; QuerySymbolRelationships' `"nothing found under any kind... trustworthy 'not found anywhere' signal"`; GetTypeInfo's type-hierarchy-not-found passthrough; ProjectDoc's `"'{filename}' was not found under..."`; InlineClass/IntroduceParameterObject/Inline(variable/field/parameter)/MoveType's `"'{name}' not found in '{file}'"`) are **genuine name/path/symbol existence checks** backed by exact identifier-text or file-path lookups (`.Identifier.Text == name`, `File.Exists`, semantic `GetTypeHierarchyAsync`), not literal-text/contextSnippet fuzzy matching. These are correctly out of scope and were verified by reading each call site down to the underlying engine method signature (`variableName`, `className`, `methodName`, `typeName` parameters, never `contextSnippet`). No findings from these three files.

### Summary of distinct rewording targets (Category A)

The core recurring phrase to replace across ContextHelper.cs (lines 216, 322) and every call site that either propagates `ex.Message` raw or builds its own `"not found"`/`"ambiguous"`-prefixed string from `ContextHelper`'s output (RefactoringEngine.cs 5151/5164/5168/5215/5227/5231/5434/5446/5449; GranularRefactoringEngine.cs; CodeGenerationEngine.cs; MsToolAugmentEngine.cs; MappingEngine.cs; SemanticRefactoringLibrary.cs; ImpactAnalyzer.cs; SyntaxUpgradeEngine.cs; SymbolNavigationEngine.cs) is the single ContextHelper wording problem, propagated by inheritance through every one of these call sites - fixing ContextHelper's two throw sites (and giving them a distinct exception type/error code per the ToolException.cs finding) is the highest-leverage single change.

### Summary of distinct labeling targets (Category B)

1. ReplaceSnippet/ApplyDiff's compiler-diagnostic block (SentinelWorkspaceTools.cs lines 727-728, 1211, 1319) needs a `[COMPILER ERROR]`-style prefix distinguishing it from a snippet-match failure.
2. The "position resolved but wrong node kind there" family (`"Expression not found."`, `"No statements found..."`, `"No switch statement found..."`, `"No string.Format() call found..."`, `"No foreach statement found..."`, `"No literal expression found..."`, `"No assignment expressions found..."`) - repeated across GranularRefactoringEngine.cs, MappingEngine.cs, SemanticRefactoringLibrary.cs, MsToolAugmentEngine.cs, CodeGenerationEngine.cs - is a distinct failure mode from "snippet text didn't match" (the position *did* resolve) and should get its own consistent prefix (e.g. "Snippet matched, but ...").
3. `ThrowIfMultiLine`'s `ToolNotFoundException` (ContextHelper.cs 455-459) is an input-validation error mislabeled under the no-match exception type.
4. `BuildMemberHint`/`BuildTypeHint`'s zero-candidates branch ("no candidates found") vs. its snippet-mismatch branch ("not found (N candidates): ...") share an identical message prefix despite being genuinely different failure categories.
5. ToolException.cs's `ToolNotFoundException` doc/type conflates real-absence and snippet-mismatch under one error code with no sub-classification, which is the structural reason (1) is possible at every call site above.

## How to apply

Use this as the source document for an implementation plan (`docs/current/plans/plan_*.md`).
Suggested order, per the agent's leverage ranking:

1. Reword `ContextHelper.cs`'s two throw sites (lines 216, 322) and introduce a distinct
   exception/error code for "snippet text did not match" vs. real absence.
2. Label the compiler-diagnostic block in `ReplaceSnippet`/`ApplyDiff`
   (`SentinelWorkspaceTools.cs:727-728, 1211, 1319`) so it can't be confused with a match failure.
3. Fix `ThrowIfMultiLine`'s exception type/label (`ContextHelper.cs:455-459`).
4. Align the "position resolved but wrong node kind" family's wording across the ~6 affected files.
5. Re-run `SearchSolutionText` for "not found"/"does not exist" after the fix to confirm no
   contradictory wording remains, per this repo's established verification pattern.
