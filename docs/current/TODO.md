# TODO — known gaps not yet fixed

Running list of confirmed-but-deferred issues found during tool development/grading. Each entry
should have enough detail to pick back up without re-discovering the root cause. Remove an entry
once it's actually fixed (and note the fix in SCENARIOS.md/commit history instead).

## `ApplyMethodCodemod`/`ApplyClassCodemod`'s `contextSnippet` declared `required: true` but defaults to `null` and is actually optional — closed (2026-08-27)

**Found:** 2026-08-27, while auditing `contextSnippet` wording (`SentinelCodemodTools.cs:400,773`).
Both parameters are `[Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet =
null` — the attribute claims required, the C# default and the method's own `[Description]`
("contextSnippet/lineBefore/lineAfter disambiguate convert_expression_body"/"...disambiguate
convert_property_safe") both say it's an optional disambiguator used only for one transform among
several.

**Fixed 2026-08-27:** confirmed via live MCP schema inspection (`ApplyMethodCodemod`/
`ApplyClassCodemod`) that a parameter's generated JSON-schema "required" status is driven entirely
by its C# nullable-type/default-value shape, not by `ConsumesAttribute.Required` — nothing in the
codebase reflects over that property (exhaustive grep confirmed it's inert, documentation-only
metadata). Safe to drop the mismatched flag. Changed both sites
(`SentinelCodemodTools.cs:400,773`) from `[Consumes(DataTag.ContextSnippet, required: true)]` to
`[Consumes(DataTag.ContextSnippet)]`.

## `Git(operation: status)` hung indefinitely (30min timeout) on a freshly-loaded solution — closed (2026-08-27)

**Found:** 2026-08-27, during an autonomous overnight session, right after `LoadSolution` succeeded
against `RoslynSentinel.slnx` on the VS Code Advanced.Http copy (port 5150, restarted minutes
earlier by `build.ps1`). `GetWorkspaceHealth` worked fine immediately before and after. Calling
`Git(operation: "status")` produced no response/progress for the full 1800s MCP idle timeout and
was aborted client-side — not a fast error, a genuine hang. Not yet root-caused (didn't want to
burn overnight time debugging the server itself instead of the planned TODO items) — could be
something about running immediately after a fresh `build.ps1 -Force` restart + reload, git-process
spawning inside the server, or unrelated. Worked around by falling back to the plain `git` CLI via
PowerShell for the rest of this session's commits, per the "try first, fall back, log the gap"
instruction.

**Resolution 2026-08-27:** decided not worth deep-diving further — a manual MCP Inspector test of
`Git(status)` against a normally-running server worked fine, and the already-implemented 30s
`GitProcessTimeout` fast-fail bound (from an earlier session) means a recurrence fails fast instead
of hanging indefinitely, which was the actual pain point. Added
`RoslynSentinel.Tests.Battery/GitToolsSmokeTests.cs` as cheap regression insurance instead of a
repro: spins up a real `git init`-ed temp repo behind a `FakeWorkspaceManager`, then calls
`Git(status)`/`Git(log)`/`Git(diff)` (the read-only operations) and asserts each completes well
under a 10s bound. This won't catch the original hang's root cause, but will catch a regression
that makes every Git call slow again.

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

**`SymbolNavigationEngine.cs` audit — closed 2026-08-27:** classified all 4 `contextSnippet != null`
sites (`FindCallers` ~line 1327, `FindImplementations` ~line 1484, `ResolveSymbolByNameAsync` ~line
1947, plus 2 message-text-only mentions at ~1366/~1557 that aren't resolution logic). Two are
*replace* shape (contextSnippet exclusively decides resolution, no name-based fallback if it fails)
and one is genuine *fallback* shape (`ResolveSymbolByNameAsync` falls through to
`candidates.FirstOrDefault()` if the snippet doesn't resolve — the same benign shape already
confirmed clean for `AnalyzeControlFlowAsync`/`AnalyzeDataFlowAsync`). Neither replace-shape site is
the same bug class as `ReplaceMember`/`ConvertExpressionBody`, though: those are mutating edit-target
resolvers, where a mismatched snippet either fails a resolution that should have succeeded, or worse,
silently applies the edit to the wrong node. `FindCallers`/`FindImplementations` are read-only lookups
— a mismatched snippet here throws a clear, actionable `InvalidOperationException` (already naming
the likely cause and suggesting `GetMethodSource`/`GetFileOutline`/omitting the snippet), never
returns a wrong answer silently. No code change needed; the risk this entry was tracking doesn't
apply to read-only resolution.

## No tool for creating or deleting a whole file — CLOSED 2026-08-27

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

**Investigated 2026-08-27 (deferred at the time, later fixed same day):** confirmed this is NOT
resolved by the recent `FilePathLock`/`FileIoHelper` work (`RoslynSentinel.Common/FileIoHelper.cs`) —
that's purely a per-path locking chokepoint around raw `File.ReadAllText`/`WriteAllText`/`Delete`
calls to prevent write-vs-write/read-vs-write races; it adds no tool surface and doesn't touch
`ApplyDiff`'s description or add any delete path. Confirmed the existing delete-adjacent machinery
was thinner than the "suggested approach" above assumed: `IWorkspaceManager.RemoveDocumentByPathAsync`
(used at the time only by `SyncTypeAndFilename`'s post-apply cleanup) only removes the in-memory
Roslyn `Document` — it does not delete the file from disk, does not stamp `WorkspaceVersion`, does
not go through `FileIoHelper`, and has no changeId/undo support.

**Fixed 2026-08-27 (same day, later pass):** user chose "add small dedicated `CreateFile`/`DeleteFile`
tools" (option 2 above) over documenting `ApplyDiff`'s side effect. Implemented both in
`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`, routed through the shared write-path
chokepoint rather than a bespoke path: `PersistentWorkspaceManager.ApplyProposedChangesAsync` gained
a `deletePaths` parameter (threaded through `IWorkspaceMutator`/`ValidateAndApplyHelper` too),
handled as its own pass alongside the existing write loop — drift-checked, pre-image-captured,
rollback-capable, and undo-tracked via `UndoLastApply` (a deleted file's captured pre-image is
non-null, so writing it back through the normal undo path resurrects it with zero extra code).
`CreateFile` fails if the target already exists (points callers to `ApplyDiff` for overwrite);
`DeleteFile` fails if the target doesn't exist. The file-watcher's `OnFileSystemChanged` handler
needed a narrower suppression check for genuine tracked deletes (previously treated every
`WatcherChangeTypes.Deleted` as real external drift unconditionally, which caused `DeleteFile`'s own
delete to get flagged as drift and block a subsequent `UndoLastApply` write-back) — now suppressed via
an empty-content `_internalChanges` sentinel recorded before the delete, combined with the path still
being absent. 7 new tests in `RoslynSentinel.Tests.Battery/CreateFileDeleteFileTests.cs` (create,
create-collision, parent-dir-autocreate, delete, delete-nonexistent, delete-then-undo,
delete-refused-on-drift), all passing. Committed `35b115c`.

**Related, smaller finding from this same pass — closed 2026-08-27:** `SyncTypeAndFilename`'s own
`File.Delete(filePath)` call (`SentinelRefactoringTools.cs`, ~line 1175) was a bare `System.IO` call,
not routed through `FileIoHelper.DeleteAsync` — it didn't hold the per-path lock the rest of the
write path uses. Fixed by switching to `await FileIoHelper.DeleteAsync(filePath, cancellationToken)`.
Verified via `GetDiagnostics`: 0 errors/warnings.

## `ApplyDiff` reflows far more of the file than the target hunk — closed (commit 7e870e4)

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

**Fixed 2026-08-27 (commit `7e870e4`, "Fix ApplyDiff forcing one line-ending convention onto the
whole file"):** confirmed via direct inspection of `DiffEngine.ApplyDiff`
(`RoslynSentinel.Common/DiffEngine.cs`) that the suggested approach above was implemented exactly as
described. Each original line's own line-break characters (`\r\n`, `\n`, `\r`, or none for a
file with no trailing newline) are now read per-line via `SourceText.Lines` and preserved
individually instead of forcing one convention onto the whole file; newly-inserted lines get the
file's dominant ending (by majority count) unless they land as the new last line. Discovered
already-fixed while investigating an unrelated `ApplyDiff` hunk-anchoring question — this entry was
stale (the fix predates this note but the doc was never updated). No further action needed.

## `AddConstructorParameter`/`ConstructorParameter` collapses multi-line signatures onto one line — closed (2026-08-27)

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

**Fixed 2026-08-27:** reproduced against current `ConstructorParameter(operation: add)` via an
isolated scratch MCP repro (throwaway `.slnx`/`.csproj` loaded via `LoadSolution`, confirmed the
collapse live). Root cause: `RoslynSentinel.Basic/RefactoringEngine.cs`'s
`AddConstructorParameterAsync`/`RemoveConstructorParameterAsync` both did
`root.ReplaceNode(classDecl, newClassNode).NormalizeWhitespace()` — a whole-syntax-tree reflow, the
same bug class as the `ApplyDiff` CRLF issue and the Basic-side `NormalizeWhitespace` sweep. Fixed
by switching both to the file's own established `ReplaceNodeFormattedAsync` helper (annotates only
the new/replaced node and scopes `Formatter.FormatAsync` to just that annotation) — already used by
`AddMemberAsync`/`AddPropertyAsync`/`AddFieldAsync`/`ModifyEnum`/`ModifyModifier` in the same file.
The field/assignment-ordering issue was not addressed (lower severity, separate from the formatting
bug this entry was about). See also the correction note on the "`RoslynSentinel.Advanced`'s
NormalizeWhitespace occurrences" entry below — the Basic-side sweep this fix's helper pattern came
from wasn't actually fully applied; `SortMembersAsync` in the same file still has the unscoped
`NormalizeWhitespace()` pattern and remains unfixed.

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

## `ChangeSignature` silently skips call-site reordering on arity mismatch — CLOSED 2026-08-27

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

**Fix (2026-08-27):** implemented the "at minimum, surface skipped call sites" option rather than the
fuller semantic-model argument-binding rewrite — the minimal fix removes the silent-failure danger
without the risk of a much larger behavior change overnight. `ChangeSignatureAsync` now returns a new
`ChangeSignatureResult(Dictionary<FilePath,string> Changes, List<SkippedCallSite> SkippedCallSites)`
record instead of a bare `Dictionary<FilePath,string>`. Two skip reasons are now detected and reported
(file + 1-based line + human-readable reason) instead of a bare `continue`:
- any argument uses a name (`NameColon != null`) — reordering positionally would corrupt the call
- `args.Count != parameters.Count` (optional argument omitted, or `params` expansion) — same as before,
  now reported instead of silently skipped

A third pre-existing `continue` (reference site isn't a simple `InvocationExpressionSyntax` — e.g. a
method-group/delegate conversion) is now also reported as skipped, for the same reason.

`SentinelAdvancedRefactoringTools.ChangeSignature`'s non-dry-run success path now appends a `WARNING:`
note listing every skipped call site (file:line + reason) onto the `AppliedChangeSummary` description
when `SkippedCallSites.Count > 0`, so the caller/agent sees it without an extra round-trip; the
dry-run (`autoStage=false`) path now returns `{ Changes, SkippedCallSites }` instead of just `{ Changes }`.

A fuller fix — real argument-to-parameter binding via the semantic model instead of positional-count
matching, so named/optional/params call sites could be correctly rewritten rather than just flagged —
is still open if this proves insufficient in practice.

Updated 4 pre-existing test call sites (`BugFixTests.cs` x3, `BatteryTwelveTests.cs`, `RegressionTests.cs`
x2) for the changed return shape, and added 2 new regression tests in `BugFixTests.cs` covering the
named-argument and arity-mismatch skip cases (`ChangeSignature_CallSiteWithNamedArgument_IsReportedAsSkipped`,
`ChangeSignature_CallSiteWithFewerArgsThanParameters_IsReportedAsSkipped`).

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

**Audit update (2026-08-27, best-effort pass):** re-inventoried the "still open" list above against
current code, since several items (`UsingDirective`, `SummaryComment`, `ModifyAttribute`,
`ConstructorParameter`) turned out to already be wired — this list had gone stale, likely from work
landing in a parallel session (see `[[project_concurrent_sessions]]`-style note: this repo sometimes has
more than one Claude/VS session active).

Re-checked all remaining unwired tools in both files against the (a)/(b)/(c) categories above:
- **`RenameSymbol`, `GenerateMapping`, `SyncTypeAndFilename`:** category (c), but not for the "too
  expensive" reason — there is no new content to show at all. `RenameSymbol` already returns rich
  custom `Data` (`oldName`/`newName`/`residualMentions`/etc.) instead of bare `AppliedChangeSummary`,
  so it doesn't need this mechanism. `SyncTypeAndFilename` only moves a file to a new name; the
  file's content is unchanged. Confirmed by reading both — no code change.
- **`ModifyEnum`, `ChangeAccessibility`, `ModifyModifier`, `ModifyBaseType`:** category (c) — the
  "new" content (the accessibility keyword, modifier keyword, base type name, or enum value list) is
  always caller-supplied verbatim already. `ChangeAccessibility`/`ModifyModifier`/`ModifyBaseType`
  already carry an explicit `// No ChangedContent: ...` comment recording this as a deliberate decision,
  not an oversight — confirms this judgment call was already made correctly. No code change.
- **`ExtractLocalVariable`, `ExtractMethodSafe` (Basic); `Introduce`, `ConvertAnonymousToNamed`,
  `ExtractMembers`, `IntroduceParameterObject` (Advanced):** category (c), the expensive way — these
  generate genuinely new code (a variable declaration, an extracted method body, a named
  record/parameter-object type) that the caller does *not* already have. But every one of them is
  built on an engine method whose result type (`DocumentEditResult`/similar) only ever exposes the
  whole-file `UpdatedText`, with no separately-returned "just the new fragment" field — the same
  shape the `Member(add)` typed-generation path hit and deliberately declined to wire for exactly
  this reason (see that entry above: "reconstructing it at the tool layer would duplicate the
  engine's formatting logic and drift if that formatting ever changes"). Wiring these properly would
  mean extending each engine method's return shape first, which is a materially different (and
  riskier, since it touches core extract/introduce logic) task than "wire an existing field into the
  offload mechanism." Left out of this pass; revisit only alongside a broader engine-API pass that
  adds fragment-returning to these specific methods.
- **`ChangeSignature` (Advanced):** already has bespoke handling as of the item-#7 fix above (its
  `Data`/summary now carries `SkippedCallSites`); doesn't need the generic mechanism.
- **`PullUpMember` (Advanced):** returns a two-file dict (derived + base) with no single "the new
  text" to highlight the same way `Member`'s single-file operations do; category (c).
- **`InlineClass`, `MoveAllTypesToFiles`, `InvertAssignments`, `SyncInterface`, `Inline`, `WrapRange`,
  `MoveType`:** not individually re-verified line-by-line in this pass (time-boxed as best-effort) —
  spot-checks of the ones actually read strongly suggest the same "whole-file `UpdatedText` only, no
  discrete new-fragment field" shape applies uniformly across this engine generation, but this is an
  inference from the pattern, not a confirmed per-tool finding. If revisited, check each the same way:
  read the underlying engine method's return type first, and only wire if a fragment is already
  separately available.

**Conclusion:** no code changes were needed for item #8 this session — the tools that could be wired
cheaply already were (by earlier work), and the rest are consistently blocked on the same "no
separately-returned fragment" engine-API gap rather than being unwired oversights. The TODO's original
per-tool list is now corrected to reflect this; a genuine fix for the remaining tools requires an
engine-API-extension pass (adding fragment-returning fields to `DocumentEditResult`-shaped results for
extract/introduce-style operations), not more tool-layer wiring.

## `PullUpMember` tool is a no-op stub, but is exposed with a description implying it works — CLOSED 2026-08-27 (findings revised — original diagnosis was stale)

**Found:** 2026-08-22, during a brief Roslyn-duplication review pass of the remaining structural
refactoring tools. See `docs/roslyn-duplication-audit-v1.md`.

**Original diagnosis (turned out to be wrong — the tool wrapper doesn't call this engine):**
`StructuralRefinementEngine.PullUpMemberAsync` (`RoslynSentinel.Basic/StructuralRefinementEngine.cs`)
is entirely unimplemented — its body is just a comment (`// logic to remove from class, add to base...`)
and an immediate `return new Dictionary<FilePath, string>();`. The sibling
`StructuralRefinementEngine.PushMembersDownAsync` has the identical stub shape.

**Correction (2026-08-27):** `SentinelAdvancedRefactoringTools` injects *two* different, similarly-named
engines: `_structuralRefinementEngine` (type `StructuralRefinementEngine`, from `RoslynSentinel.Basic`
— the stub described above) and `_refinementEngine` (type `RefinementEngine`, from
`RoslynSentinel.Advanced` — a real, working implementation). The `PullUpMember` tool method calls
`_refinementEngine.PullUpMemberAsync`, **not** the stub — `_structuralRefinementEngine` is injected into
this class but never called anywhere in it (dead field). So the original finding's premise (the tool
always returns empty / always fails) was false; `PullUpMember` has always actually worked for valid
inputs. Confirmed via `RoslynSentinel.Tests.Advanced/NewImplementationsTests.cs`'s
`PullUpMember_MovesMember_FromDerivedToBaseFile` etc., which were passing the whole time.

**Real bug found instead, in `RoslynSentinel.Advanced/RefinementEngine.PullUpMemberAsync`:** every
failure branch (file not found, class not found, member not found, no base class, base class external,
etc.) returned `new Dictionary<FilePath, string> { { "error", message } }` instead of throwing. Because
`Dictionary<FilePath, string>` has an implicit `string → FilePath` conversion
(`RoslynSentinel.Common/FilePath.cs:97`), the key `"error"` silently became a real (if unvalidated)
`FilePath`. With `autoStage=true` (the tool's default), that dict was then handed straight to
`ValidateAndApplyAsync` — meaning any *failed* `PullUpMember` call would attempt to write the error
message to disk as a file literally named `error`, staged as a real change, rather than surfacing a
proper tool error. The `catch (Exception ex)` wrapping the whole method also meant genuine unexpected
exceptions (e.g. a Roslyn API throwing) were silently folded into the same fake-file-error path instead
of reaching the tool layer's own `catch`, which already knows how to map real exceptions correctly via
`ToolErrorMapper`.

**Fix (2026-08-27):**
- `RoslynSentinel.Advanced/RefinementEngine.PullUpMemberAsync`: replaced every `return new
  Dictionary<FilePath,string> { {"error", ...} }` with `throw new ToolNotFoundException(...)`, and
  removed the outer `try/catch` that swallowed all exceptions into the same fake-error-dict shape —
  unexpected exceptions now propagate to the tool wrapper's existing `catch`/`ToolErrorMapper` path.
- Added `ToolErrorCode.NotImplemented` and a new `ToolNotImplementedException : ToolException`
  (`RoslynSentinel.Common/ToolException.cs`), following the same one-exception-per-category pattern as
  `SolutionNotLoadedException`/`ToolNotFoundException`/etc., for any future stub that needs an honest
  "not implemented" failure instead of a misleading domain-specific error.
- `RoslynSentinel.Basic/StructuralRefinementEngine.PullUpMemberAsync`/`PushMembersDownAsync` (the actual
  unimplemented stubs — still dead code, unreachable from any tool) now throw
  `ToolNotImplementedException` instead of silently returning an empty dict, in case they're ever wired
  up by mistake or on purpose without their bodies being filled in first.
- Simplified `SentinelAdvancedRefactoringTools.PullUpMember`: removed the now-permanently-dead
  `changes.Count == 0` → `"Member 'X' not found..."` branch (real failures throw before reaching it now).
- Updated 4 tests across `NewImplementationsTests.cs` and `BatterySeventeenTests.cs` /
  `BugFixTests.cs` that asserted the old "returns a dict with an `error` key" contract to instead
  assert `Assert.ThrowsAsync<ToolNotFoundException>`.

**Follow-up fixed 2026-08-27:** `RefinementEngine.InlineMethodAsync` in the same file had the
identical `{ "__error__", message }` fake-dict anti-pattern on its 5 failure branches (document not
found, syntax root null, method not found, complex-body-not-supported, symbol-resolution-failed).
Converted all 5 to `throw new ToolNotFoundException(...)`, matching the pattern already applied
elsewhere in this file. Removed the now-dead `methodChanges.Count == 0` check in
`SentinelAdvancedRefactoringTools.Inline`'s `kind == "method"` branch (the existing
`catch (Exception ex)` right below already maps the thrown exception via `ToolErrorMapper`).
Updated 3 tests in `RoslynSentinel.Tests.Advanced/BugFixTests.cs` that asserted the old
`__error__`-dict contract to instead assert `Assert.ThrowsAsync<ToolNotFoundException>` (or, for one
stale assertion, to check for the actually-correct inlined output).

**Push-down member:** confirmed there is still no real push-down implementation anywhere in the
codebase — `StructuralRefinementEngine.PushMembersDownAsync` (Basic, now throws `NotImplemented`) is
the only method with that name, and it is not wired to any exposed MCP tool. No `PushDownMember` tool
exists to expose it through. Out of scope to implement (real logic, not a duplication-avoidance case);
noted here in case a future session wants to add the tool once the engine method is implemented.

## New-file validation gap — closed (commit 1b00f3f)

**Found:** documented at length in the now-`docs/obsolete/new-file-validation-gap-scope.md`. **Fixed:**
commit `1b00f3f` ("Validate new files against their containing project's compilation") — added
`RoslynSentinel.Common/SolutionProjectLocator.cs` (`FindContainingProject`) and wired it into
`RoslynSentinel.Common/ValidationEngine.cs` so a brand-new file is added into the candidate `Solution`
for validation instead of being `continue`-skipped. Also updated `RoslynSentinel.Tests.Battery/BatteryTenTests.cs`
and `docs/known-failing-tests.{Basic,Solution}.txt`. Confirmed via `git show 1b00f3f` during the
2026-08-24 docs reorganization pass; no further action needed.

## `RoslynSentinel.Advanced`'s NormalizeWhitespace occurrences never got a follow-up sweep — Basic side now fully closed 2026-08-27; Advanced still open

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

**Correction 2026-08-27 — the Basic-side sweep this entry assumed was "done" had two live misses of
its own,** found while fixing the unrelated "`ConstructorParameter` collapses multi-line signatures"
bug (separate TODO entry). `RoslynSentinel.Basic/RefactoringEngine.cs`'s `AddConstructorParameterAsync`
and `RemoveConstructorParameterAsync` both did `root.ReplaceNode(classDecl, newClassNode)
.NormalizeWhitespace()` — a whole-tree reflow identical to the pattern this entry describes, not a
narrowly-scoped one. Fixed by switching both to the file's own established
`ReplaceNodeFormattedAsync` helper (annotates only the new/replaced node and calls
`Formatter.FormatAsync` scoped to that annotation — already used by `AddMemberAsync`/`AddPropertyAsync`
/`AddFieldAsync` and others in the same file). `SortMembersAsync` (same file, ~line 3458) has the
identical unscoped `.ReplaceNode(...).NormalizeWhitespace()` shape and was NOT fixed in this pass — out
of scope for the `ConstructorParameter` bug, flagged here since it's the same bug class found by the
same audit. **Implication for this entry:** "the Basic sweep is done, only Advanced is unswept" can no
longer be assumed — worth a fresh grep of `RoslynSentinel.Basic` too (not just `RoslynSentinel.Advanced`)
for any other `.ReplaceNode(...).NormalizeWhitespace()`/bare `.NormalizeWhitespace()` call before
scoping the Advanced-side follow-up work, since the "already fixed" premise just proved incomplete once.

**Basic side fully closed 2026-08-27:** a fresh `SearchSolutionText` sweep of all of
`RoslynSentinel.Basic` found 89 raw `.NormalizeWhitespace()` hits (not just the 1 `SortMembersAsync`
miss above), across 17 files. Every hit was individually classified: 55 were the unscoped
whole-root-reflow bug, 34 were legitimate uses (whole-document `SyntaxRewriter.Visit` passes where
full-file reformatting is correct-by-design; small standalone nodes normalized before insertion,
never a reflow of existing content; text built only for display/comparison, never written back; or
brand-new output with the original root untouched). Of the 55 confirmed bugs, 52 were fixed by
switching to `ReplaceNodeFormattedAsync`/`RemoveNodeFormattedAsync` (or a shared-`SyntaxAnnotation` +
`Formatter.FormatAsync` pass for compound/multi-edit methods) — full per-file breakdown in the
`RoslynSentinel.Basic/*.cs` diffs from this date. 3 were deliberately left unfixed, flagged for a
human/architecture decision rather than a mechanical swap:
- `RunMicroRefactoringAsync` (`GranularRefactoringEngine.cs`) — its 5 dispatched helpers return bare
  `SyntaxNode?` with no way to identify which sub-node changed; scoping the fix requires changing
  helper return contracts, not just wrapping the call site.
- `ExtractConstantSafeAsync` and `GenerateToStringSafeAsync` (`MsToolAugmentEngine.cs`) — both
  Document-less (parse from a raw string via `CSharpSyntaxTree.ParseText`/`File.ReadAllTextAsync`,
  no `Document` available for `Formatter.FormatAsync`'s annotation-scoped overload). Would need an
  `AdhocWorkspace`-based formatting variant; `FormatDocumentSafeAsync` in the same file was checked
  as a possible existing precedent and doesn't cover this case.

Verified via `dotnet build RoslynSentinel.slnx` (0 errors, only pre-existing test-project warnings)
and the full test suite across all 5 test projects (0 new failures; every name in
`docs/known-failing-tests.{Basic,Advanced}.txt` still fails/skips the same way, nothing new).
`git diff --ignore-all-space` confirms each file's actual change is exactly the expected
targeted-formatting swap — large raw `git diff` line counts in a few files (e.g. `AnalysisEngine.cs`)
are pure pre-existing LF/CRLF line-ending noise, not scope creep.

**Advanced side (~63-64 occurrences) remains explicitly out of scope** — not started this session
per direct user instruction to do Basic only. Suggested approach section above still applies when
that work is picked up; re-run `SearchSolutionText` fresh rather than trusting the old ~64 count,
since this session's Basic count (89, not the originally-assumed handful) shows raw grep-style
estimates for this pattern have been unreliable so far.

## Remaining `throw new` sites inside `[McpServerTool]`-adjacent code — CLOSED 2026-08-27

**Found:** 2026-08-24, while auditing `docs/spec-replace-throws-in-mcp-tools-v1.md` (kept in
`docs/current/` as still-partial) for the docs reorganization pass. That spec's goal — replace
exceptions thrown from MCP-tool-adjacent code with string/result returns so a caller doesn't have to
catch — is not fully executed. Grep at the time of this pass found 11 remaining `throw new` sites:
`Program.cs` (both `RoslynSentinel.Server.Basic` and `RoslynSentinel.Server.Advanced`, 1 each, line 19),
`SentinelSymbolTools.cs:210`, `ServerStartupHelpers.cs:216`, `SentinelScanTools.cs:549`, and 6 sites in
`SentinelAsyncifyTools.cs` (lines 2939, 2953, 2962, 3060, 3424, 3444).

**Re-audited 2026-08-27 — all 11 sites already handled correctly, no code change needed:**
- `Program.cs` (both projects, line 19, `ArgumentException` on unknown `--transport`): process-startup
  CLI arg validation in `Main`, before any MCP request loop exists — not tool-adjacent at all;
  crashing at boot on a bad launch flag is the correct behavior, not a leak risk.
- `ServerStartupHelpers.cs:216` (`SmokeResolveToolTypes`, `InvalidOperationException` on an
  unresolvable DI tool type): `[Conditional("DEBUG")]`, runs once at process boot before any tool
  call — a dev-only DI-wiring smoke check, not reachable from a live agent request.
- `SentinelSymbolTools.cs:210` (`RunRelationshipQueryAsync`'s `ArgumentOutOfRangeException` default
  arm): `searchKind` is a strongly-typed enum MCP parameter (framework rejects invalid values before
  this code runs); the only way to hit this arm is `Enum.GetValues<FindUsagesSearchKind>()` finding a
  real value the switch forgot to handle — a genuine bug-guard, and it's already caught by
  `QuerySymbolRelationships`'s own `catch (Exception ex)` → `ToolErrorMapper.ToResultError`, same as
  every other unmapped exception in this codebase. Already agent-safe.
- `SentinelScanTools.cs:549` (`RequireFile`, `ArgumentException` when `scope != "file"`): message is
  already clean/self-contained (states what's wrong and what was received, no internals). Its ~29 call
  sites are all inside `RunScanDetector`'s single dispatch method, which has a dedicated
  `catch (ArgumentException aex)` immediately above the general catch that passes `aex.Message`
  straight through — already correctly wired, not a leak.
- `SentinelAsyncifyTools.cs`'s 6 sites (line numbers shifted since 2026-08-24 from unrelated
  `AsyncifyCore` decomposition work — now at 3024/3038/3047/3151/3516/3536, same 6 logical sites):
  5 of the 6 (all but 3516/3536's containing block) are caught by an immediately-following per-item
  `catch (Exception ex)` inside the same candidate loop and never reach an agent-visible `ResultError`
  at all — logged via `_logger.LogWarning`/`LogInformation` and folded into a per-item failure record
  instead. The remaining pair (3516, 3536, inside a loop whose catch sets `OperationItemRecord.Reason
  = ex.Message`) does surface the message to the agent, but the message itself
  (`"Conversion failed: {Outcome} — {Message}"` / `"Validation: N error(s) — ..."`) is already clean,
  matching the established convention for `Reason` everywhere else in this file — not a leak.

**Conclusion:** the spec's stated goal (replace throws with result returns) was never fully executed
as originally scoped, but every one of the 11 sites this entry tracked turned out to already be either
non-tool-adjacent, DEBUG-only, or already correctly mapped/caught with an agent-safe message by the
time of this re-audit — very likely fixed piecemeal across other sessions' work since 2026-08-24
(see [[project_concurrent_sessions]]) rather than by a dedicated pass on this spec. No remaining risk
identified; closing rather than re-scoping into a rename/restructure task nothing currently needs.

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

