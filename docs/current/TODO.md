# TODO — known gaps not yet fixed

Running list of confirmed-but-deferred issues found during tool development/grading. Each entry
should have enough detail to pick back up without re-discovering the root cause. Remove an entry
once it's actually fixed (and note the fix in SCENARIOS.md/commit history instead).

## `ApplyMethodCodemod`/`ApplyClassCodemod`'s `contextSnippet` declared `required: true` but defaults to `null` and is actually optional

**Found:** 2026-08-27, while auditing `contextSnippet` wording (`SentinelCodemodTools.cs:400,773`).
Both parameters are `[Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet =
null` — the attribute claims required, the C# default and the method's own `[Description]`
("contextSnippet/lineBefore/lineAfter disambiguate convert_expression_body"/"...disambiguate
convert_property_safe") both say it's an optional disambiguator used only for one transform among
several. Not fixed in the same pass since it's a `required`-flag/attribute-contract question, not a
wording one, and unclear whether anything downstream (schema generation, validation) actually trusts
the `required` flag in a way changing it could break — needs checking before flipping it to `false`.

## `Git(operation: status)` hung indefinitely (30min timeout) on a freshly-loaded solution

**Found:** 2026-08-27, during an autonomous overnight session, right after `LoadSolution` succeeded
against `RoslynSentinel.slnx` on the VS Code Advanced.Http copy (port 5150, restarted minutes
earlier by `build.ps1`). `GetWorkspaceHealth` worked fine immediately before and after. Calling
`Git(operation: "status")` produced no response/progress for the full 1800s MCP idle timeout and
was aborted client-side — not a fast error, a genuine hang. Not yet root-caused (didn't want to
burn overnight time debugging the server itself instead of the planned TODO items) — could be
something about running immediately after a fresh `build.ps1 -Force` restart + reload, git-process
spawning inside the server, or unrelated. Worked around by falling back to the plain `git` CLI via
PowerShell for the rest of this session's commits, per the "try first, fall back, log the gap"
instruction. Re-verify against a normally-running (not just-restarted) server before assuming this
repros generally.

## `SyncTypeAndFilename` validation always sees old+new documents coexisting — closed (2026-08-27)

**Found:** 2026-08-25, while wiring `dryRun`/`returnDiff` params onto `SyncTypeAndFilename`
(`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`) and writing a regression test for the
new `dryRun` behavior.

**Fixed 2026-08-27:** confirmed not resolved by the recent `FilePathLock`/`FileIoHelper` work
(those address write-path file-locking races, unrelated to Roslyn solution/document validation).
Added an optional `removePaths` parameter threaded through `ValidationEngine.ValidateChangesAsync`
(both the instance wrapper and the static core, `RoslynSentinel.Common/ValidationEngine.cs`) and
`ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs`)
and the two tool-layer private wrappers (`SentinelRefactoringTools.cs`,
`SentinelAdvancedRefactoringTools.cs`). The static core now removes each path's existing `Document`
(if any) from the candidate solution before processing `fileChanges`, and adds its project to
`affectedProjectIds` so the removal's own compile impact is still checked. `SyncTypeAndFilename`
now passes `removePaths: [filePath]` so the old path's document is excluded from validation instead
of coexisting with the new path's — the duplicate-declaration false failure is gone. Verified with a
new real success-path test, `SyncTypeAndFilename_RealRename_SucceedsAndRemovesOldDocument`
(`RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`), which renames a mismatched-filename file
through the actual `ValidateAndApplyAsync` path (not the short-circuit the older
`SyncTypeAndFilename_ValidFile_ReturnsString` test takes) and asserts both success and that the old
path's `Document` is gone from `CurrentSolution` afterward. Full-suite `build.ps1 -Flavor Solution
-Mode Test` shows 0 new failures. The post-apply `File.Delete`/`RemoveDocumentByPathAsync` sequence
already in `SyncTypeAndFilename` (for cleaning up on-disk/in-memory state *after* a successful
apply) was untouched — this fix only concerns the pre-apply validation gate.

**What:** `ValidationEngine.ValidateChangesAsync`'s static core (`RoslynSentinel.Common/
ValidationEngine.cs`, ~line 102-116) treats a change whose path has no existing document as brand
new, and *adds* it into the candidate solution alongside everything already there. But
`SyncTypeAndFilename`'s change dictionary is keyed on the *new* path (`changes = { [newPath] =
content }`) while the *old* document (same content, same type) is still present under the old path
— `RemoveDocumentByPathAsync` for the old path only runs after a successful, non-dryRun apply, in
the tool method itself (`SentinelRefactoringTools.cs`, after `ValidateAndApplyAsync` returns). So
pre-apply validation sees the same type declared in two documents simultaneously and fails with a
duplicate-declaration compiler error (e.g. `CS0229 Ambiguity between 'X.Member' and 'X.Member'`) —
for what looks like every real invocation where the type's declaration is otherwise unique, which is
the normal case (that's the whole reason the file needs renaming).

**Why this went unnoticed:** no existing test exercised this tool's success path through
`ValidateAndApplyAsync` at all. The one pre-existing test
(`SyncTypeAndFilename_ValidFile_ReturnsString` in `BatteryTwentyFourTests.cs`) renames `"Order.cs"`
containing `class Order` — filename already matches the type, so `SyncTypeAndFilenameAsync` returns
`EditOutcome.CannotEdit` before ever reaching `ValidateAndApplyAsync`, and the test only asserts
`result is not null`. Found by writing a real rename scenario (mismatched filename vs. type, real
temp-dir files) for `SyncTypeAndFilename_DryRun_NeverDeletesOriginalFileAsync`
(`BatteryTwentyFourTests.cs`) — that test asserts the dryRun invariant regardless of validation
outcome, so it stayed green, but the underlying validation failure is documented inline there.

Checked two other candidates that might have already covered this and confirmed neither does:
`Samples/ContosoOrders` scenario 8 (`SCENARIOS.md`) describes `SyncTypeAndFilename` renaming
`OrderProcessor.cs` → `Order.cs`, but the sample's `OrderProcessor.cs` still contains `class Order`
today with a comment noting the mismatch is planted intentionally — i.e. that scenario documents an
*intended* agent task, not a recorded successful run, and the rename was never actually applied to
the sample on disk. `RoslynSentinel.Tests.Battery/BatteryTwentyNineTests.cs` (`B29_AllEngines_
RealSolution_SmokeTests`) does load a real on-disk solution (via `ROSLYN_SENTINEL_TEST_SLN`), but
its own file header states all its tests are read-only ("nothing is written to disk") and it never
calls `SyncTypeAndFilename` — it wouldn't hit this bug either way.

**Suggested approach:** either exclude the old document from the candidate solution when validating
a rename-shaped change (the tool layer already knows both paths), or extend `ValidateChangesAsync`
to accept an explicit "remove these paths first" list so rename tools can request it without a
special case per tool. Whichever approach, add a real success-path test for `SyncTypeAndFilename`
through `ValidateAndApplyAsync` (not just the engine method) once fixed.

## `ApplyDiff` hunk-anchoring and error-wrapper fixes — closed

**Found:** 2026-08-20/21, while migrating tests off a dead `RefactoringEngine.SafeDeleteSymbolAsync`
copy (see commit "Merge SafeDeleteSymbolAsync's reflection-risk check..."). Fixed in two passes:
the anchor-search bug on 2026-08-21 (commit "Fix DiffEngine's ReanchorHunk desyncing on unmarked
blank hunk lines"), and the outer error-wrapper message on 2026-08-21 (same day, follow-up commit).

**Fix 1 — the hunk-anchor search itself had a real defect**, now fixed in
`RoslynSentinel.Common/DiffEngine.cs`. `ReanchorHunk`'s `anchorLines` list only kept hunk-body lines
starting with `" "` or `"-"` — a hunk-body line representing a blank file line but written with no
leading space marker at all (a bare empty string, distinct from `" "`) was silently dropped instead
of being treated as an implicit blank context line. Since the hunk's declared line number assumes
every body line (including blank ones) is present and counted, dropping one desynchronizes the
anchor list from the declared position by exactly one line per dropped line — causing the search to
look one line off from where the real content is, and (if no coincidental match exists within the
60-line window) throw "content wasn't found there or within 60 lines" even though the content was
exactly where declared. Root-caused via byte-level replay of the actual failing hunk from this
session against the actual file content at the time (see `DiffEngineTests.cs`'s new
`ApplyDiff_HunkWithUnmarkedBlankContextLine_StillAnchorsCorrectly` test, which reproduces it exactly
and confirms the fix). Both `ReanchorHunk`/`MatchesAt` (the search) and the main `ApplyDiff` hunk-body
processing loop (the actual apply) were updated in tandem — a bare empty line is now treated as an
implicit blank context line by both, consistently.

**Fix 2 — the outer tool-layer wrapper's misleading message (first pass).** All three `catch`
blocks in `SentinelWorkspaceTools.cs`'s `ApplyDiff` method previously appended a fixed *"Check that
the solution is loaded and the file path is valid"* sentence to every exception unconditionally,
including `DiffEngine`'s own `InvalidOperationException` — which already names the real cause (a
stale/mismatched hunk) and has nothing to do with the solution or file path. That fixed phrase
actively misled callers into checking the wrong thing, exactly as flagged live during this session.
First fix: a local `BuildApplyDiffError(ex, context)` helper that checked
`_workspaceManager.CurrentSolution == null` directly and trusted `InvalidOperationException`
messages instead of guessing. **Superseded the same day** by the broader fix below once an audit
found the identical anti-pattern in ~89 catch blocks across 11 files, not just `ApplyDiff`.

**Related, separate observation:** applying this fix's own edit to `DiffEngineTests.cs` (via the
IDE's direct file-edit tool, not `ApplyDiff`) silently normalized that file's line endings from
mixed CRLF/LF to pure LF — the same class of unrequested whole-file line-ending rewrite as
`ApplyDiff`'s CRLF-forcing bug (see the "reflows far more of the file" entry, now fixed), just in
the opposite direction and via a different tool. Not investigated further here since it didn't
break anything (LF end-to-end is arguably the more consistent state) and reverting file-by-file
would risk introducing a real mistake, but worth knowing this isn't purely an `ApplyDiff`-specific
problem — something in the write path generally doesn't preserve mixed line endings verbatim.

## Guessed-cause error messages, exception-hierarchy fix — closed (Server.Basic + Server.Advanced)

**Found:** 2026-08-21, auditing the codebase for other instances of `ApplyDiff`'s "Check that the
solution is loaded and the file path is valid" pattern after fixing it there specifically. Found the
exact same unconditional, often-wrong guessed-cause message hardcoded into ~89 catch blocks across
11 files (`SentinelWorkspaceTools.cs`, `SentinelRefactoringTools.cs`, `SentinelSymbolTools.cs` in
Server.Basic; `SentinelScanTools.cs`, `SentinelAsyncifyTools.cs`, `SentinelIntelligenceTools.cs`,
`SentinelAdvancedRefactoringTools.cs`, `SentinelCodemodTools.cs`, `SentinelQualityTools.cs`,
`SentinelModernizationTools.cs`, `SentinelGenerationTools.cs` in Server.Advanced) — every one of
these asserted "solution not loaded or path invalid" regardless of the exception's actual type or
cause. One correctly-tailored variant already existed (`Build`'s "...and dotnet is on PATH"),
showing the pattern was hand-copied per-tool with no shared helper.

**Root design decision:** rather than guess a category from a caught exception's runtime type (most
domain failures across the engine layer are stock `InvalidOperationException`/`ArgumentException`/
`FileNotFoundException` with the real specifics only in message text — confirmed via an engine-wide
survey; no custom exception types existed before this fix, and two pre-existing half-adopted
taxonomies, `ToolErrorCode` and `EngineOutcome`/`EngineErrorCode`, were both largely bypassed in
favor of a catch-all `Exception` code), the fix makes the **throw site** self-report its category.
Added a small exception hierarchy in `RoslynSentinel.Common/ToolException.cs`:
`SolutionNotLoadedException`, `ToolNotFoundException`, `ToolAmbiguousMatchException`,
`DiffApplyException` (all `: ToolException : Exception`, each exposing its own `ErrorCode`). Added
matching `ToolErrorCode.NotFound`/`Ambiguous`/`DiffApplyFailed` constants (`ToolResult.cs`). Added a
single shared `ToolErrorMapper.ToResultError(ex, workspaceManager, context)` (also in
`ToolException.cs`) that every catch block calls directly: `ToolException` subclasses pass their
`ErrorCode`/`Message` straight through with no guessing; otherwise it checks
`workspaceManager.CurrentSolution == null` directly (the one thing cheap to verify) before falling
back to a generic, honest "failed unexpectedly" message with no asserted cause.

**Done this pass (Server.Basic + the shared throw-site migration it depends on):**
- `RoslynSentinel.Common/PersistentWorkspaceManager.cs`: `GetBranchedSolutionAsync` and
  `ApplyProposedChangesAsync`'s "Solution not loaded" check now throw `SolutionNotLoadedException`;
  `ResolveSolutionPath` now throws `ToolNotFoundException` instead of `FileNotFoundException`.
- `RoslynSentinel.Common/DiffEngine.cs`: all of `ApplyDiff`'s throw sites and `ReanchorHunk`'s
  "not found" throw now throw `DiffApplyException` (this also retired the original one-off
  `BuildApplyDiffError` helper above — it's now just a call to the shared mapper).
- `RoslynSentinel.Common/ContextHelper.cs`: `FindSnippetPosition`'s not-found/ambiguous throws now
  throw `ToolNotFoundException`/`ToolAmbiguousMatchException`. `TryFindSnippetPosition`'s own catch
  updated to match.
- `RoslynSentinel.Basic/StructuralRefinementEngine.cs`: `SyncTypeAndFilenameAsync`'s "file not
  found" now throws `ToolNotFoundException`.
- All 89 generic-phrase catch sites in `SentinelWorkspaceTools.cs`, `SentinelRefactoringTools.cs`,
  `SentinelSymbolTools.cs` (Server.Basic's 3 files with the pattern) replaced with
  `ToolErrorMapper.ToResultError(ex, _workspaceManager, context)` calls. `LoadSolution`'s own catch
  block deliberately does NOT go through the mapper — its `SolutionNotLoaded` branch would say "call
  LoadSolution first" from inside `LoadSolution` itself, which is circular; it special-cases
  `ToolException` vs. generic `Exception` inline instead. `Build`'s already-correct
  "...dotnet is on PATH" message was left untouched.
- Every downstream `catch (InvalidOperationException`/`catch (FileNotFoundException` site that
  wrapped one of the migrated throw sites was found and updated to `catch (ToolException` (or the
  specific subclass) instead, across both source and tests: `RoslynSentinel.Basic/RefactoringEngine.cs`
  (2 sites, `WrapInTryCatchAsync`/`WrapInRegionAsync`), `SymbolNavigationEngine.cs` (3 sites in/around
  `GetSymbolInfoAsync` and `ResolveSymbolByNameAsync`), `MsToolAugmentEngine.cs` (5 sites — the
  broadest miss on the first sweep, see below), `SemanticRefactoringLibrary.cs` (`WrapInUsingAsync`),
  `MappingEngine.cs` (`InvertAssignmentsAsync`), `ServiceRegistrationExtensionsBasic.cs` (the MCP
  request filter that turns "no solution loaded" into a friendly non-error response — this one would
  have silently stopped working, since its old filter matched on `InvalidOperationException` +
  message-prefix rather than type); test-side: `RoslynSentinel.Tests/ContextHelperTests.cs`,
  `RegressionTests.cs`, `LoadSolutionPathSanitizationTests.cs`, `RoslynSentinel.Tests.Basic/DiffEngineTests.cs`,
  `RoslynSentinel.Tests.Advanced/BugFixTests.cs`, `RoslynSentinel.Tests.Battery/BatteryNineTests.cs`
  (`SyncTypeAndFilename_UnknownFile_ThrowsFileNotFound`).
- Verified via full-suite runs (`Tests`, `Tests.Basic`, `Tests.Advanced`, `Tests.Battery`) that the
  post-fix failure sets are byte-for-byte identical to the pre-fix (stashed) baseline — no new
  failures, confirmed by diffing sorted test-name lists, not just failure counts.

**How the migration was actually caught being incomplete (worth remembering):** an initial
targeted-file sweep (checking only `RefactoringEngine.cs`) missed 5 sites in
`MsToolAugmentEngine.cs` that also call `ContextHelper.FindSnippetPosition` directly — caught only
because running the full `Tests.Basic` suite surfaced a genuine new failure
(`ExtractMethodSafe_SnippetNotFound_ReturnsFail`) that wasn't in the pre-existing-failure set. A
second, deliberately paranoid whole-solution sweep (tracing actual call chains, not just grepping
for exception-type names near each other) then found 2 more source sites
(`SemanticRefactoringLibrary.cs`, `MappingEngine.cs`) and 1 more test site (`BugFixTests.cs`) that
the first "careful" pass had also missed, plus a `Tests.Battery` site
(`SyncTypeAndFilename_UnknownFile_ThrowsFileNotFound`) that a narrower grep pattern failed to catch
initially. **Lesson: after any exception-type migration, always run the full test suite (not just
the file you think is affected) and diff the failure set against a real baseline before trusting a
"nothing else references this" grep-based audit** — several real misses here only surfaced through
that final full-suite diff, not through code review.

**Done — Server.Advanced (2026-08-20, second pass):** all ~55 remaining generic-phrase sites across
`SentinelScanTools.cs`, `SentinelIntelligenceTools.cs`, `SentinelAdvancedRefactoringTools.cs`,
`SentinelCodemodTools.cs`, `SentinelQualityTools.cs`, `SentinelModernizationTools.cs`,
`SentinelGenerationTools.cs` (via `ToErrorMessage` for its 4 bare-`string`-returning methods) migrated
to `ToolErrorMapper`, mirroring Server.Basic. Along the way, fixed 2 pre-existing `ToolResult<T>`
contract violations in `SentinelScanTools.cs` (`DescribeScanDetectors`/`AnalyzeMethod` set
`Success = false` but put the error text in `Data` instead of `Error`).

`SentinelAsyncifyTools.cs` needed more than a mechanical swap: its ~12 outer `catch (Exception ex)`
blocks all hardcoded `MigrationErrorCode.Exception` + `"An unexpected error occurred."` regardless of
cause, and 6 inner `*Core` helpers (`PropagateCancellationTokenCore`, `UpliftCallersCore`,
`FlagMigrationCandidatesCore`, `AsyncifyCore`, `HandlerToAsyncCore`) additionally caught their engine
call's real exception and **rethrew** a new `InvalidOperationException` carrying the guessed-cause
sentence, discarding the original exception's type — so the outer catch had nothing accurate to map
even after being fixed. Both layers were migrated: the inner helpers now
`catch (Exception ex) when (ex is not ToolException) { log; throw; }` (let the real exception
propagate) instead of wrapping it, and every outer catch calls `ToolErrorMapper.ToResultError`/
`ToErrorMessage`. `MigrationErrorCode`'s constants (`SolutionNotLoaded`/`FeatureDisabled`/
`InvalidArgument`/`Exception`, in `MigrationEnvelope.cs`) were left as-is rather than replaced with
`ToolErrorCode` — `ResultError.ErrorCode` is a plain `string`, and the two enums' values are
identical text, so there's no wire-format change from routing through the shared mapper. Left
untouched (confirmed genuinely per-item, not the guessed-cause pattern): `AddCancellationTokenCore`'s
loop-internal catch (already uses real `ex.Message`), `BridgeAsyncMethodsCore`'s retry-on-
"already exists" catch, and 3 `throw new InvalidOperationException($"Validation: ...")` sites inside
`AsyncifyCore`'s per-candidate loops (all caught locally by an immediately-following per-item catch
that already uses `ex.Message`, never reaching the outer catch).

**Bug found via this migration, fixed in the same pass:** `ToolErrorMapper.ToResultError` built
`new ResultError(code, message)` — a 2-arg call, leaving the record's `Detail` field at its default
`null` — even though `Message` already embeds `ex.Message` as inline text ("...Details: {ex.Message}").
`RoslynSentinel.Tests.Asyncify/MigrationScanResultTests.cs`'s
`T9_GetAsyncMigrationProgress_ForcedException_ReturnsException_DetailNonEmpty` asserts
`result.Error.Detail` is non-empty — caught as a new full-suite test failure after the
`SentinelAsyncifyTools.cs` migration (this test predates the migration; the old code path happened to
pass `ex.Message` as a 3rd positional arg to `ResultError`, which the mapper's 2-arg call dropped).
Fixed by passing `ex.Message` as `ToResultError`'s `Detail` argument — `new ResultError(code, message,
ex.Message)` — restoring the structured field instead of relying solely on the inline text. Verified
via `build.ps1 -Flavor Solution -Mode Test`: 0 new failures against the 87-line baseline both before
and after this fix (the `Detail` fix was the only change needed; re-run confirmed 84 pre-existing
failures, none new).

Also confirmed unaudited and explicitly out of scope for this pass: engine-layer throw-site
migration for Server.Advanced's own engines (e.g. `ApiIntegrationEngine.AddValidationToPocoAsync`'s
"class not found" `InvalidOperationException`, still caught by name-specific `catch (InvalidOperationException ioe)`
blocks in `SentinelCodemodTools.cs` for `add_validation_to_poco`/`convert_abstract_to_interface`) —
these are already genuine, specific-cause exceptions, just not yet elevated to the `ToolException`
hierarchy the way `StructuralRefinementEngine.cs` was for Basic. Left as future work, not a bug.

## Future feature: `UsingDirective(operation: add, simplifyAllCallers: true)` — solution-wide simplification

**Found:** 2026-08-19, while reviewing whether `UsingDirective` needed a `simplifySingleFile`/
`simplifyAllCallers` split. `simplifySingleFile` already effectively exists as the current
`simplifyExisting` bool (add-only, runs `Simplifier.ReduceAsync` scoped to just the edited
document) — no new work needed there. `simplifyAllCallers` does not exist and would be new,
larger-scope work, not a boolean flag on the existing method.

**Not `FindReferences` + a loop.** The obvious-looking shortcut — call `FindReferences` to get a
file list, then loop `UsingDirective(simplifySingleFile)` over each — doesn't work: `FindReferences`
resolves references to one specific *symbol* (a method/type/member via `docCommentId`), but this
feature is scoped to a *namespace*, which can contain many independent symbols
(`ContosoOrders.Core.Discounts` might have `DiscountCalculator`, `TaxCalculator`, etc.). There's no
single symbol representing "the namespace" to feed `FindReferences`, running it once per symbol in
the namespace would still miss files that don't reference that particular symbol, and a
`FindReferences` hit doesn't distinguish "referenced via existing using directive" (nothing to
simplify) from "referenced via a fully-qualified name" (the actual target).

**What it would actually need to do (namespace-scoped solution sweep, not symbol-based):**
1. Enumerate every document in the solution — not a filtered subset, since there's no cheap way to
   know in advance which documents contain a fully-qualified reference into the target namespace.
2. Per document: get the semantic model and look for `QualifiedNameSyntax`/
   `MemberAccessExpressionSyntax` nodes whose resolved symbol's containing namespace matches the
   target (a syntax/semantic scan, not a reference lookup) — cheaply skip documents with no such
   node before doing anything else, since most of the solution won't reference the namespace at all.
3. For each document that does have matches: ensure the `using` directive is present (add if
   missing, matching `AddUsingDirectiveAsync`'s existing idempotency check), then run
   `Simplifier.ReduceAsync` scoped to that document — same mechanism `simplifyExisting` already uses
   per-file, just applied across every matching document instead of one.
4. Only report/return documents that actually changed.

**Why not built now:** this changes the tool's blast radius from "one file" to "the whole
solution" — every document touched needs its own using-directive-presence check (not just the one
file the caller named), its own simplify pass, and its own change entry in the result. That's a
meaningfully different feature (a bespoke semantic-model sweep across the whole solution) than the
current single-document flag, and deserves a deliberate design pass (e.g. should it also report
which files it touched? cap how many files it'll touch in one call? require a dry-run first?)
rather than being bolted on as a same-shaped bool.

## `contextSnippet` wording audit across tool descriptions — closed (2026-08-27)

**Found:** 2026-08-19, while fixing `ReplaceMember`'s single-candidate `contextSnippet` bug (see
SCENARIOS.md Scenario 4 / "Fixed" list).

**What:** every `contextSnippet`-accepting tool's `[Description]` calls it "a distinctive substring
from the target member" (or near-identical wording) without clarifying what "distinctive" actually
requires — that it still needs to match the file's real text (now tolerant of whitespace/indentation
differences, but not genuine content differences). Across the 7 recorded ContosoOrders agent runs,
real agents have passed, for the exact same kind of call: a full member body, a signature-only
one-liner, a comment-only fragment, and a from-memory reconstruction that introduced a genuine content
difference (see `ContextHelperTests.FindSnippetPosition_SafeDelete_AgentFabricatedInterpolation_StillFailsToMatch`).
Nothing in the current wording steers an agent toward the safest choice (shortest unique substring
that's still copied verbatim) or away from the riskiest one (reconstructing a whole member from
memory).

**Findings 2026-08-27:** re-audited via `grep -i contextSnippet` across every `.cs` file. The shared
`ToolParams.ContextSnippet` constant (`RoslynSentinel.Common/ToolParams.cs`) already contains exactly
the wording this entry asked for — "only needed when ambiguous," "prefer the shortest unique
fragment," "do NOT paste the whole body," "copy verbatim, not from memory" — and turned out to
already be applied consistently at the parameter level everywhere it's the right fit (every
optional/disambiguation-only `contextSnippet` parameter in `SentinelRefactoringTools.cs` and
`SentinelAdvancedRefactoringTools.cs` already carries `[Description(ToolParams.ContextSnippet)]`).
Unclear from history whether this was fixed in an earlier unlogged pass or was never as inconsistent
as this entry assumed — either way, no wording change was needed on that front.

**What was actually missing:** 6 `contextSnippet` parameters across `SentinelQualityTools.cs` (x2),
`SentinelGenerationTools.cs`, `SentinelAdvancedRefactoringTools.cs` (`Introduce`), and
`SentinelCodemodTools.cs` (x2) had no `[Description]` at all on the parameter. Of these, 4
(`SentinelQualityTools.cs` x2, `SentinelGenerationTools.cs`, `Introduce`) are a genuinely different
shape than `ToolParams.ContextSnippet` assumes: `contextSnippet` is `required: true` there and is the
*sole* locator for the target (no separate `symbolName`/`memberName` parameter exists), not an
optional disambiguator layered on top of a name — applying the generic "Optional. Only needed when
ambiguous..." wording to these would be actively wrong. Each already has an adequate tool-specific
explanation in its method-level `[Description]` (e.g. "contextSnippet: short foreach snippet (e.g.
\"foreach (var item in\")"), so left alone. The remaining 2
(`SentinelCodemodTools.cs`'s `ApplyMethodCodemod`/`ApplyClassCodemod`) are the genuine optional-
disambiguator shape (default `null`, used only to disambiguate one transform among several,
alongside a real name-based locator) but were missing `[Description(ToolParams.ContextSnippet)]`
entirely — fixed by adding it to both.

**Related, unfixed, logged separately below:** both `ApplyMethodCodemod`/`ApplyClassCodemod`'s
`contextSnippet` parameters are declared `[Consumes(DataTag.ContextSnippet, required: true)]` while
defaulting to `null` and being genuinely optional per the method's own description — an
attribute/actual-optionality mismatch, not a wording problem. Not fixed here (out of scope for this
pass; may affect other tooling/validation that trusts the `required` flag) — see the new entry below.

Verified via `build.ps1 -Flavor Solution -Mode Test`: 0 new failures.

## Deferred: `contextSnippet` deprecation tracking, and `NearMissList`'s 3-candidate cap

**Found:** 2026-08-19, closing out `docs/plan-tool-disambiguation-remediation-v1.md` Task I/J
(hint-strategy evaluation + raw-`ContextHelper` error-message enrichment).

**What (two related, deliberately-unresolved questions):**
1. Every tool touched by that plan keeps `contextSnippet` fully optional, silently first-matching
   by name when omitted — including when the name is genuinely ambiguous. Whether to eventually
   require `contextSnippet` (or `symbolName`+`contextSnippet`) once ambiguity is detected, or at
   least emit a non-fatal warning on a silent first-match against 2+ candidates, was explicitly
   raised as a Risks-section question in that plan and never decided — it's a product/reliability
   trade-off (breaking today's default-argument-free call shape vs. catching silent wrong-guesses
   proactively), not something to decide unilaterally while fixing the hint text.
2. The `NearMissList` hint strategy (now the sole implementation in `RefactoringEngine.BuildMemberHint`/
   `BuildTypeHint`) caps its candidate list at 3, with a "+N more" suffix beyond that. No fixture in
   the current test suite has more than 3 real same-named candidates, so this was left at the plan's
   originally-specified cap rather than speculatively widened or made configurable.

**Why not resolved now:** both are explicitly flagged in the plan doc's Task J addendum as
recommendations for the user to decide, not gaps this session's work left broken — the additive,
non-breaking behavior is working as designed today.

## `ConvertExpressionBodyAsync` has the same contextSnippet bug class as `ReplaceMember` — closed (2026-08-27)

**Found:** 2026-08-19, while fixing `ReplaceMember`'s `ResolveMemberByNameOrSnippet`/
`ResolveTypeByNameOrSnippet` single-candidate bug (see SCENARIOS.md Scenario 4 / "Fixed" list).

**What:** `RefactoringEngine.ConvertExpressionBodyAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs`,
~line 1643) resolves its target with an `if (contextSnippet != null) { position-based } else {
name-based candidates }` branch — structurally different from `ResolveMemberByNameOrSnippet`'s
"compute name-based candidates first, only consult the snippet if 2+" shape. This means a supplied
`contextSnippet` bypasses name-based candidate computation entirely rather than being ignored when
unnecessary, so the same failure mode (a defensive/mismatched snippet blocking an otherwise-unambiguous
resolution) is still possible here, just via a different code path.

**Fixed 2026-08-27:** rather than restructure the method's own resolution logic, replaced the whole
branch with a direct call to the existing shared `ResolveMemberByNameOrSnippet` helper (already used
by `ReplaceMember` and ~19 other call sites) — it already has the "compute name-based candidates
first, only consult contextSnippet if 2+" shape this method was missing, and its return type
(`MemberDeclarationSyntax?`) matches what `ConvertExpressionBodyAsync` needs directly. Passed an
`extraFilter` restricting candidates to `MethodDeclarationSyntax`/`PropertyDeclarationSyntax`/
`ConstructorDeclarationSyntax`, matching the original inline name-based branch's member-kind filter.
Wrapped in the same `try/catch (InvalidOperationException)` → `EditOutcome.CannotEdit` pattern every
other `ResolveMemberByNameOrSnippet` caller already uses (e.g. `ReplaceMemberAsync`). Verified with a
new regression test, `ConvertExpressionBody_UnambiguousMemberWithMismatchedContextSnippet_StillSucceeds`
(`RoslynSentinel.Tests.Advanced/BugFixTests.cs`) — a single non-overloaded method converts
successfully even when passed a `contextSnippet` that doesn't match the file's real text at all
(previously failed with a snippet-not-found error). `build.ps1 -Flavor Solution -Mode Test`: 0 new
failures.

**Audit while fixing this (corrected — see below):** `grep -n "contextSnippet != null"` across
`RoslynSentinel.Basic` found 8 hits total, not just `ConvertExpressionBodyAsync`. Triaged each:
- `RefactoringEngine.AnalyzeControlFlowAsync`/`AnalyzeDataFlowAsync` (lines ~1818, ~1862): use
  `method ??= <name-based lookup>` — snippet is tried first but *falls back* to name-based search
  if the snippet doesn't match, rather than replacing it. Different, more benign shape (a mismatched
  snippet is silently ignored rather than causing failure) and these are read-only analysis methods,
  not mutating tools. Left alone — different bug (if any), different severity, out of scope here.
- `SymbolNavigationEngine.cs` (4 hits, ~lines 1327/1366/1484/1557/1947): read-only symbol
  lookup/reference-finding code, not a mutating edit-target resolver — not the same "silently wrong
  edit" risk profile as `ReplaceMember`/`ConvertExpressionBodyAsync`. Not triaged in detail this pass;
  flagged as unaudited rather than confirmed-clean.
- `CodeGenerationEngine.ConvertPropertySafeAsync` (line ~1043): **was the same bug class** — a
  mismatched `contextSnippet` returned `TargetNotFound` before ever trying name-based resolution,
  identical failure mode to `ConvertExpressionBodyAsync`'s pre-fix behavior, exposed via
  `ApplyClassCodemod`'s `convert_property_safe` transform. Fixed in the same pass: since
  `CodeGenerationEngine` is a separate class from `RefactoringEngine` and can't call its private
  `ResolveMemberByNameOrSnippet`, wrote an equivalent local candidates-first/snippet-only-if-2+ block
  directly in `ConvertPropertySafeAsync` instead of extracting a cross-class shared helper (judged
  smaller/less risky than a refactor touching `ResolveMemberByNameOrSnippet`'s 20 existing call
  sites). Updated the stale test that had encoded the old buggy behavior as expected
  (`ConvertPropertySafe_WithBadContextSnippet_ReturnsErrorString` renamed to
  `ConvertPropertySafe_UnambiguousPropertyWithBadContextSnippet_StillSucceeds`, now asserting
  success) and added a new `ConvertPropertySafe_AmbiguousPropertyWithBadContextSnippet_
  ReturnsErrorString` test with two same-named properties on sibling types, preserving the original
  test's intent (a snippet that matches nothing should still fail when disambiguation is genuinely
  needed). `build.ps1 -Flavor Solution -Mode Test`: 0 new failures.

**Still open:** `SymbolNavigationEngine.cs`'s 4 `contextSnippet != null` sites are unaudited — worth
a follow-up pass to classify their shape (replace vs. fallback vs. something else) before assuming
they're clean.

## No tool for creating or deleting a whole file

**Found:** 2026-08-19/20, while implementing the `Build` tool (`docs/plan-build-verification-tool-v1.md`)
using the MCP tools on their own source as a dogfooding exercise. Needed to create a brand-new
`BuildEngine.cs` file and, after placing it in the wrong project, delete it.

**What:** no tool is named or described for "create a new file" or "delete a file." `ApplyDiff`
(`changesetFormat: files`) happens to work for creation — passing a path that doesn't exist yet in
the `changes` dict creates it (confirmed: `preImages` reports `null` for that path on success) — but
nothing in its `[Description]` mentions this, so an agent has no reason to expect it. There is no
equivalent for deletion; the fallback was a raw filesystem `rm`, entirely outside the MCP tool
surface and its validation/versioning/drift-tracking.

**Why this matters:** any task that needs a new top-level type in a new file (a new engine class, a
new tool class) or needs to remove one (abandoning a wrong placement, deleting a whole obsolete
file) currently has no first-class tool path. Silent reliance on `ApplyDiff`'s undocumented
file-creation side effect is fragile — nothing guarantees that behavior is intentional/stable rather
than incidental to how it resolves a target path.

**Suggested approach:** either (1) document `ApplyDiff`'s file-creation behavior explicitly in its
`[Description]` and add a symmetric `deleteFile`/`action: delete` path that goes through the same
validation/`workspaceVersion`/undo machinery as every other write, or (2) add small dedicated
`CreateFile`/`DeleteFile` tools. Whichever direction, the delete side should update the in-memory
workspace and stamp `WorkspaceVersion` like other mutating tools, not bypass it.

## `ApplyDiff` reflows far more of the file than the target hunk — root cause found: whole-file CRLF normalization

**Found:** 2026-08-19/20, while implementing the `Build` tool (original repro, unconfirmed root
cause). **Root cause isolated:** 2026-08-20/21, while merging `SafeDeleteSymbolAsync`'s
reflection-risk check (see commit "Merge SafeDeleteSymbolAsync's reflection-risk check..."). A
handful of small, targeted `ApplyDiff` calls (1-11 real changed lines each) against 5 different
files produced a combined git diff of thousands of lines — `BugFixTests.cs` alone showed 8188
changed lines for what should have been ~6 real lines. All behavior-preserving (`Build` showed 0
new errors after every edit), so this is a formatting/reflow issue, not a correctness one — but a
severe code-review/diff-noise cost, and the previous entry's "collapsing multi-line signatures"
hypothesis turned out to be the wrong mechanism.

**Confirmed root cause:** `ApplyDiff`'s write path normalizes **every line ending in the whole
file to CRLF** on every apply, regardless of the file's original predominant convention and
regardless of hunk size. Verified by byte-level comparison (`od -tx1`) of the git blob before/after
each of 5 `ApplyDiff` calls in the session that produced the commit above:
- `FinalRegressionTests.cs`: 1/285 lines CRLF before → 285/285 after.
- `BugFixTests.cs`: 1/4092 lines CRLF before → 4098/4098 after (edited via both `ApplyDiff` and
  direct `Edit` calls — the CRLF flip happened on the `ApplyDiff`-touched portions).
- `MassiveRefactoringTests.cs`: 2/134 CRLF before → 134/134 after (touched by exactly one
  small `ApplyDiff` call).
- `BatteryThirtyOneTests.cs`: 2/422 CRLF before → 422/422 after (one `ApplyDiff` call).
- `BatteryNineTests.cs`: 1/273 CRLF before → 273/273 after (one `ApplyDiff` call).
- Control case: `StructuralRefinementEngine.cs`, edited by 4 separate `ApplyDiff` calls in the same
  session, was **already 100% CRLF** beforehand and stayed 100% CRLF after — no reflow-sized diff
  resulted. This is the key data point: `ApplyDiff` normalizing to CRLF is a no-op (and produces a
  minimal, correct-sized diff) on a file that's already all-CRLF, but reflows *every line* of a
  file that has mixed or predominantly-LF line endings, because git then sees every line as
  changed (the trailing `\r` becomes part of each line's content once endings are inconsistent
  within the blob).
- The lone stray CRLF line present in each "before" snapshot (1-2 out of hundreds) is itself
  suspicious — likely a remnant of this exact bug firing on some earlier single-line edit to that
  file in a prior session, never noticed because a 1-line diff doesn't look like reflow.

**Why this matters:** this is a distinct root cause from (but the same symptom class as) the
whole-file `NormalizeWhitespace()` bug `docs/plan-symbol-tool-hardening-v1.md` documents as fixed —
that one shifted line numbers via re-indentation; this one is purely a line-ending write-time
normalization with no semantic effect, but it inflates every git diff touching a non-uniformly-
CRLF file to look like a full-file rewrite, defeating code review.

**Suggested approach:** find wherever `ApplyDiff`'s write path re-serializes the document (likely a
`.ToFullString()` write or a `File.WriteAllText`/`SourceText` round-trip that doesn't preserve the
original `SourceText.ChecksumAlgorithm`/newline metadata) and make it preserve each line's existing
ending — or at minimum detect the file's dominant line ending once and normalize consistently
*to that*, rather than unconditionally forcing CRLF. Roslyn's `SourceText` already tracks per-file
line-ending info; the fix likely means writing back through that instead of a raw string write that
loses it.

## `AddConstructorParameter`/`ConstructorParameter` collapses multi-line signatures onto one line

**Found:** 2026-08-19, while implementing the `Build` tool, using the (since renamed/consolidated)
`AddConstructorParameter` tool to add a `BuildEngine` parameter to `SentinelWorkspaceTools`'s
constructor. Note: this tool has since been renamed to `ConstructorParameter(operation: add)` by a
concurrent session (confirmed live — `AddConstructorParameter` no longer resolves, `ConstructorParameter`
does) — re-verify this repros on the current tool before fixing, since the consolidation may have
touched the same code path.

**What:** the target constructor's parameter list was originally formatted one parameter per line
(a 10-parameter DI constructor). After the tool added the 11th parameter, the entire parameter list
and the constructor's opening line were collapsed onto a single very long line. The field
declaration and body-assignment ordering were also not inserted in the same relative position as
the other fields/assignments (appended at the end rather than matching declaration order) — lower
severity, but worth fixing in the same pass if the formatting fix touches that code anyway.

**Why this matters:** same class of code-review/diff-noise cost as the `ApplyDiff` reflow issue above,
though possibly a different code path (constructor-parameter insertion, not a generic diff apply) —
don't assume they share a root cause without checking.

**Suggested approach:** repro against current `ConstructorParameter(operation: add)` on a
multi-line constructor before diagnosing; if confirmed, the fix likely belongs in whatever formatting
step runs after the new parameter/field/assignment are inserted (preserve existing line-break style
rather than normalizing to single-line).

## Mutating tools don't return the resulting content, forcing a separate `ReadFile` to see the outcome

**Found:** 2026-08-19/20, raised by Andrew while reviewing the `Build` tool implementation session.

**What:** per Andrew, mutating tools originally returned the entire new file content on every write,
which bloated agent context (especially on large files) — this was since changed so mutating tools
return only a `changeId`/success flag, not the resulting text. The consequence: an agent that wants
to confirm what its edit actually produced (e.g. see the replaced member's new text, confirm a
generated signature looks right) must make a *second* tool call (`ReadFile`/`GetMethodSource`) to see
it — which defeats the original goal of reducing tool-call count and context bloat, just shifts the
cost from "one bloated response" to "two calls, one of which re-fetches what was just written."
`docs/plan-symbol-tool-hardening-v1.md`'s own review guidance ("flag it if the agent never re-reads
the file/method afterward to confirm... actually look correct") implicitly assumes agents *should* be
re-reading after every write, which is exactly the extra round-trip this behavior forces.

## `ChangeSignature` silently skips call-site reordering on arity mismatch

**Found:** 2026-08-22, during a Roslyn-duplication audit of `ChangeSignatureAsync`
(`RoslynSentinel.Basic/RefactoringEngine.cs`, currently around line 205). See
`docs/roslyn-duplication-audit-v1.md` finding #3.

**What:** after reordering the target method's declared parameter list, the tool walks all
references via `SymbolFinder.FindReferencesAsync` and reorders each call site's arguments to match.
But the reorder is only applied when `args.Count == parameters.Count` exactly
(`RefactoringEngine.cs:205`) — any call site using named arguments, an omitted optional argument, or
`params` array expansion has a different effective/textual arg count and is silently `continue`d
past, with no error or warning surfaced anywhere in the result.

**Why this matters:** the declaration is still reordered even when some call sites are skipped, so
those skipped call sites are left passing arguments positionally to the *old* parameter order against
the *new* declaration — a silent semantic break (wrong values going to wrong parameters, or a type
mismatch if types differ) that compiles cleanly in many cases and is easy to miss in review.

**Suggested approach:** at minimum, surface which call sites were skipped (file/line) in the result so
the caller/agent knows to handle them manually. A fuller fix would need real argument-to-parameter
binding (via the semantic model's `SemanticModel.GetSymbolInfo`/argument-matching, not just positional
count) to correctly handle named/optional/params call sites instead of skipping them.

**Why this matters:** the large-result offload pattern is the mechanism that should resolve this
tension: return the actual result (e.g. the new member's text, or a small tail/diff of the change)
inline when it's small, and only offload to disk when it's genuinely large — rather than
unconditionally omitting it. Right now the tool families that used the working offload mechanism
(`SentinelIntelligenceTools`/`SentinelScanTools`/`SentinelAsyncifyTools`) aren't the ones doing small
in-place edits (`Member`/`ReplaceMember`/`ConstructorParameter`/etc.), so the tools that would most
benefit from "return the small result inline, offload only if large" don't have that machinery wired
at all.

**Update 2026-08-20/21:** the blocker this entry originally named — the `ToolResult<T>.Data`
offload stub — is now finished. `ToolResult<T>.ForPossiblyLargeDataAsync(data, solutionRoot,
resultType, wrapperType, ...)` (`RoslynSentinel.Common/ToolResult.cs`) is the new factory: small
results go inline, large ones offload via `ScanResultHelper.StoreScanResultAsync` and populate
`LargeResult`. `ApplyDiff` itself was also separately fixed in the same pass (no longer inlines
`PreImages` by default; added `returnDiff` — see the removed "`ApplyDiff` response size..." entry
this superseded).

**Update 2026-08-20/21 (second pass):** `Member` (`add`/`remove`/`replace`) and `ConstructorParameter`
(`add`/`remove`) in `SentinelRefactoringTools.cs` are now wired — this was the mechanism's first
real caller (it had zero call sites before this). Added `ScanWrapperType.MemberChangedContent` +
`MemberChangedContentResult` (`RoslynSentinel.Common/ScanResultHelper.cs`) and a matching
`GetScanResult` switch case, mirroring `MethodSource`/`FileSource`/`MigrationScanSummary`. Notes on
what "changed content" means per operation, since it isn't uniform:
- `Member(replace)`: `newMemberSource` is already known verbatim from the caller — echoed back with
  zero extra work.
- `Member(add)`, raw-source path (`newMemberSource` supplied): same, echoed back verbatim.
- `Member(add)`, typed-generation path (`typedKind`+`typedName`+`typedType`, no `newMemberSource`):
  **`ChangedContent` is left empty.** The actual generated source is built inside
  `RefactoringEngine.AddPropertyAsync`/`AddFieldAsync` and never returned separately from the
  whole-file `UpdatedText` — reconstructing it at the tool layer would duplicate the engine's
  formatting logic and drift if that formatting ever changes. Revisit if `AddPropertyAsync`/
  `AddFieldAsync` are ever changed to return the generated fragment alongside `UpdatedText`.
- `Member(remove)`: unchanged, still bare `AppliedChangeSummary`. There's no new content to show
  for a removal — the `Description` field ("Removes 'X' from Y.cs") already says what happened, and
  forcing a `ChangedContent` field onto this operation would just be an empty string.
- `ConstructorParameter(add)`: `ChangedContent` is `"{paramType} {paramName}"`, reconstructed at the
  tool layer (both values are caller-supplied, so this isn't duplicating engine logic the way the
  `Member(add)` typed path would).
- `ConstructorParameter(remove)`: same reasoning as `Member(remove)` — bare `AppliedChangeSummary`.

**Still open — remaining ~24 tools not yet wired,** all in the same "return bare `AppliedChangeSummary`,
built on `ValidateAndApplyAsync`" shape, inventoried during this pass:
- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`: `RenameSymbol` (has richer `Data` already,
  worth checking if it needs `ChangedContent` too), `GenerateMapping`, `UsingDirective` (add/remove),
  `ModifyEnum`, `ChangeAccessibility`, `SummaryComment` (add/remove), `ExtractLocalVariable`,
  `ExtractMethodSafe`, `ModifyAttribute` (add/replace/remove), `ModifyModifier` (add/remove),
  `ModifyBaseType` (add/remove), `SyncTypeAndFilename`.
- `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs`: `ChangeSignature`,
  `ConvertAnonymousToNamed`, `InlineClass`, `MoveAllTypesToFiles`, `InvertAssignments`, `PullUpMember`,
  `IntroduceParameterObject`, `Introduce`, `ExtractMembers`, `SyncInterface`, `Inline`, `WrapRange`,
  `MoveType`.

**Suggested approach for the rest:** same pattern as `Member`/`ConstructorParameter` above — for each
tool, decide per-operation whether the changed content is (a) already known verbatim from a caller
parameter (cheapest, prefer this), (b) cheaply reconstructable from caller-supplied parts without
duplicating engine formatting logic, or (c) not available without either engine changes or accepting
the whole-file `UpdatedText`/diff (in which case, leave it out rather than duplicating engine logic
or reintroducing the original context-bloat problem this mechanism was built to avoid).

## `PullUpMember` tool is a no-op stub, but is exposed with a description implying it works

**Found:** 2026-08-22, during a brief Roslyn-duplication review pass of the remaining structural
refactoring tools. See `docs/roslyn-duplication-audit-v1.md`.

**What:** `StructuralRefinementEngine.PullUpMemberAsync` (`RoslynSentinel.Basic/StructuralRefinementEngine.cs:351`)
is entirely unimplemented — its body is just a comment (`// logic to remove from class, add to base...`)
and an immediate `return new Dictionary<FilePath, string>();`. No syntax tree is touched, nothing is
awaited. It is nonetheless wired up to a fully-described, user-facing MCP tool
(`SentinelAdvancedRefactoringTools.PullUpMember`, `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs:390`)
whose `[Description]` reads: "Pulls a method or property from a derived class into its base class.
Removes override, adds virtual (if not already abstract/virtual), and moves the declaration." None of
that happens. Because the engine call always returns an empty dict, the tool wrapper's
`changes.Count == 0` check (line 414) always fires, so every call fails with the misleading message
`"Member 'X' not found or no accessible base class available."` — indistinguishable from a genuine
"class/member doesn't exist" error, even when both exist and are eligible.

The sibling method `StructuralRefinementEngine.PushMembersDownAsync` (line 360, "push member down to
derived classes") has the identical stub shape, but is **not** wired to any exposed MCP tool — lower
priority since no caller can reach it today, but should be fixed alongside `PullUpMemberAsync` if that
one gets implemented, or removed if push-down is out of scope.

**Why this matters:** an agent (or user) calling `PullUpMember` gets a plausible-sounding "not found"
error and will reasonably conclude their class/member names are wrong or the base class is
inaccessible, and may spend real effort double-checking names, symbol resolution, etc. — when the
actual cause is that the tool does nothing at all. This is worse than an honest
`NotImplementedException` or a `FeatureDisabled` result, both of which exist elsewhere in this codebase
as an established pattern for gating incomplete tools.

**Suggested approach:** short-term, make the failure honest — either gate `PullUpMember` behind
`_config.IsFeatureEnabled(...)` returning `FeatureDisabled` (the pattern already used elsewhere, e.g.
`ExtractLocalVariableAsync`), or have the engine method throw/return a distinct "not implemented"
outcome instead of a silently-empty dict that collapses into the generic not-found path. Longer-term,
implement the real logic: locate `className`'s base class via `INamedTypeSymbol.BaseType`, find
`memberName`'s declaration, clone it into the base class syntax tree with `override` removed and
`virtual` added (if not already `abstract`/`virtual`), remove the original from the derived class, and
replace both documents — this is genuinely new logic (Roslyn has no public or internal "pull member
up" refactoring service to delegate to), not a duplication-avoidance case.

## New-file validation gap — closed (commit 1b00f3f)

**Found:** documented at length in the now-`docs/obsolete/new-file-validation-gap-scope.md`. **Fixed:**
commit `1b00f3f` ("Validate new files against their containing project's compilation") — added
`RoslynSentinel.Common/SolutionProjectLocator.cs` (`FindContainingProject`) and wired it into
`RoslynSentinel.Common/ValidationEngine.cs` so a brand-new file is added into the candidate `Solution`
for validation instead of being `continue`-skipped. Also updated `RoslynSentinel.Tests.Battery/BatteryTenTests.cs`
and `docs/known-failing-tests.{Basic,Solution}.txt`. Confirmed via `git show 1b00f3f` during the
2026-08-24 docs reorganization pass; no further action needed.

## `RoslynSentinel.Advanced`'s NormalizeWhitespace occurrences never got a follow-up sweep

**Found:** 2026-08-24, while auditing `docs/plan-normalize-whitespace-full-sweep-v1.md` (now filed
`docs/obsolete/`) for the docs reorganization pass. That plan completed a sweep of `RoslynSentinel.Basic`
but explicitly scoped out `RoslynSentinel.Advanced`'s ~64 occurrences of the same whole-file
`NormalizeWhitespace()` pattern as deferred/out-of-scope, and no follow-up plan doc for the Advanced
side exists anywhere in `docs/`.

**Why this matters:** the Basic-side version of this bug caused real line-shift/re-indentation damage
(see the plan doc's own root-cause writeup) before being fixed. If the same call pattern is still
present ~64 times in `RoslynSentinel.Advanced` (not re-verified count-wise in this pass — worth a fresh
grep for `NormalizeWhitespace()` before scoping work), those call sites carry the same latent risk and
have simply not been hit by a repro yet.

**Suggested approach:** grep `RoslynSentinel.Advanced` for `.NormalizeWhitespace()` calls that
re-serialize a whole document (vs. a narrowly-scoped single-node call, which is fine), cross-check each
against the Basic-side fix's shape (targeted formatting vs. whole-tree re-indent), and either confirm
they're already narrow/safe or port the same fix pattern across.

## Remaining `throw new` sites inside `[McpServerTool]`-adjacent code

**Found:** 2026-08-24, while auditing `docs/spec-replace-throws-in-mcp-tools-v1.md` (kept in
`docs/current/` as still-partial) for the docs reorganization pass. That spec's goal — replace
exceptions thrown from MCP-tool-adjacent code with string/result returns so a caller doesn't have to
catch — is not fully executed. Grep at the time of this pass found 11 remaining `throw new` sites:
`Program.cs` (both `RoslynSentinel.Server.Basic` and `RoslynSentinel.Server.Advanced`, 1 each, line 19),
`SentinelSymbolTools.cs:210`, `ServerStartupHelpers.cs:216`, `SentinelScanTools.cs:549`, and 6 sites in
`SentinelAsyncifyTools.cs` (lines 2939, 2953, 2962, 3060, 3424, 3444).

**Suggested approach:** re-grep `throw new` across both Server projects to get a current count (this
list may already be smaller if fixed piecemeal since), then work through the remainder using the same
result-shape conversion the rest of the spec already applied elsewhere.

## Read-tool metadata envelope (`isComplete`/truncation flag) — entirely unimplemented

**Found:** 2026-08-24, while auditing `docs/spec-read-tool-metadata-envelope-v1.md` (kept in
`docs/current/`) for the docs reorganization pass. Zero `isComplete`/`IsTruncated`-style fields exist
anywhere in the codebase — the spec's proposed metadata envelope for read tools (so a caller can tell
whether a returned excerpt is the whole thing or was cut short) has not been started.

**Suggested approach:** see the spec doc itself for the proposed shape; flagging here mainly so this
doesn't get lost now that the originating spec lives outside `docs/TODO.md`'s usual discovery path.

## Tool terminology/naming backlog — open, unactioned

**Found:** 2026-08-24, while auditing `docs/tool-terminology-refinement-reference-v1.md` (kept in
`docs/current/`) for the docs reorganization pass. That reference catalogs weak/ambiguous tool and
parameter names (`GetBreakerStatus`, `GetMigrationLedger`, `ClearExternalDrift` vs. its sibling
`ListExternalDiskChanges`'s inconsistent metaphor, the `Apply*Codemod`/`Generate`/`Introduce`/`Inline`
untyped-string-discriminator family) and recommends, in order: (1) convert small/stable discriminator
params to real enums; (2) standardize the "call `DescribeAdvancedToolOptions` first" wording across all
wide dispatchers; (3) only then revisit the specific rename table. None of the three steps has been
started as of this pass.

**Suggested approach:** see the reference doc itself for the full weak-term table and confusable-group
list; this entry exists so the backlog is discoverable from `TODO.md` without having to know the
reference doc exists.

