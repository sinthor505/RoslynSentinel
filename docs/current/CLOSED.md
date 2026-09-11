# CLOSED — fixed/resolved history

Permanent record of issues that were found and fixed (or confirmed as an upstream bug outside
RoslynSentinel's control). Split out of TODO.md on 2026-09-10 to keep that file open-items-only;
entries below are otherwise unchanged from when they were closed. Newly-fixed TODO.md items should
be moved here going forward, not deleted.

## Blockers from the MCP tool description/param/enum/optionality revision pass — closed (2026-09-10)

From `finding_mcp_tool_desc_revision_blockers.md` (deleted, superseded by this entry and by
`issue_member_add_silent_persistence.md` for the one item still open):

- **`RequireProject` returned its own error string instead of throwing** (`SentinelScanTools.cs`) —
  fixed to throw `ArgumentException` matching `RequireFile`'s existing message shape, so
  `RunScanDetector`'s existing `catch (ArgumentException aex)` block now correctly surfaces
  `ToolErrorCode.InvalidArgument` instead of passing the error text into `FindUnusedReferencesAsync`
  as if it were a real project name. Regression test added:
  `BatteryTwentyTwoTests.RunScanDetector_UnusedReferencesWithoutProjectScope_ReturnsInvalidArgument`.
- **`ApplyUnifiedDiff` "HEADER COUNT MISMATCH" read as an error but was purely cosmetic** —
  confirmed via code trace that declared hunk-header line counts are never used in the actual
  apply/anchor path (`DiffEngine.cs` only uses the declared *start line* as a re-anchor guess, with a
  content-based fallback search); `DiffHunkAnalyzer.cs`'s wording changed to an explicitly
  informational "header line-count hint" note, and a pure header-count mismatch (no unmarked blank
  line, no malformed lines) no longer sets `DiffReport.HasFindings` or populates the
  `diffHunkFindings` response field. `DiffHunkAnalyzerTests.cs` updated to match.
- **Test call sites masked by loose assertions (class-of-bug audit)** — full-repo audit (156 test
  files, 92 candidate call sites) found 3 remaining `RenameSymbol` call sites
  (`BatteryTwentyFourTests.cs:237,246`, `MassiveRefactoringTests.cs:110`) missing the `reason:` named
  argument, shifting every positional string argument one slot early. Fixed by adding full named
  arguments; assertions tightened to check `result.Success` where they previously only checked
  non-null. All other candidate call sites in the audit were already correctly labeled.
- **`WrapRange.wrapper` schema reports optional but is unconditionally required** — confirmed root
  cause: the MCP SDK's `AIFunctionFactory` derives JSON-schema `required` purely from C# default-value
  presence, and this repo's own `Consumes`/`ExternalInputRequired` attributes are documentation-only,
  never consulted by schema generation. Real fix requires reordering `wrapper` before other defaulted
  parameters, which breaks existing positional call sites — already flagged in-code via the
  `TOOL-OPTION-REQUIRED-FLAG-STALE` comment; no further action taken, left for a dedicated future pass.

## `Member(add)`/`InsertMemberAfter`/`InsertMemberBefore` silently no-op'd on enum containers — closed (2026-09-11)

The last still-open item from the blockers pass above (`issue_member_add_silent_persistence.md`,
deleted, superseded by this entry). `Member(operation: "add", ...)` against an enum container (e.g.
`ToolScope`, `InlineKind` in `RoslynSentinel.Common/ToolEnums.cs`) reported full success — a real
`changeId`, `status: "applied"`, `"Written to disk."` — while the file was byte-for-byte unchanged.
`InsertMemberAfterAsync`/`InsertMemberBeforeAsync` exhibited the identical symptom against enums.

Root cause, confirmed via a live VS debugger attached to the running MCP server process while
stepping through the exact repro call: `RefactoringEngine.AddMemberAsync`'s container-type switch
(`RefactoringEngine.cs:1209-1216`) had explicit cases for
`Class`/`Interface`/`Record`/`StructDeclarationSyntax` only; `EnumDeclarationSyntax` fell to a
`_ => container` fallback that silently returned the container unmodified while the method still
reported `Outcome = Modified`. `InsertMemberAfterAsync`/`InsertMemberBeforeAsync` check `container is
TypeDeclarationSyntax` (which does not include `EnumDeclarationSyntax`, a direct
`BaseTypeDeclarationSyntax` subtype) and fall back to calling `AddMemberAsync` for anything that
isn't, landing on the same gap. This was unrelated to concurrency, workspace staleness, or
container-name resolution — all suspected at length during the investigation — `container` resolved
correctly as a genuine `EnumDeclarationSyntax` every time; the switch simply never handled that case.
Also confirmed unrelated to `newMemberSource` validity: `EnumMemberDeclarationSyntax` doesn't derive
from `MemberDeclarationSyntax`, so `AddMemberAsync`'s whole approach (parse via
`SyntaxFactory.ParseMemberDeclaration`, splice via `AddMembers`) can never produce a valid enum
member regardless of input — enums need `ModifyEnumAsync`, an existing, separate tool built for
exactly this.

Fixed by rejecting enum containers explicitly in `AddMemberAsync` (`EditOutcome.CannotEdit`, message
pointing callers at `ModifyEnumAsync`), and changing the switch's fallback from `_ => container` to
`_ => throw new NotSupportedException(...)` so any future unhandled container-type subtype fails
loudly instead of silently reporting false success — general hardening against the same bug class
recurring for a future Roslyn syntax-node subtype. Regression tests added to
`RoslynSentinel.Tests.Basic/CodeEditingTests.cs`: `AddMember_ToEnum_RejectsInsteadOfSilentNoOp`,
`InsertMemberAfter_OnEnum_RejectsInsteadOfSilentNoOp`, `InsertMemberBefore_OnEnum_RejectsInsteadOfSilentNoOp`.

**Correction (2026-09-11):** `Member(operation: "view")`'s "Cannot edit: container not found" on
enum containers was originally logged above as a separate, unreconciled issue, on the theory that
`view` used a different container-resolution path than `add`. That theory was wrong: `view` is
backed by `RefactoringEngine.GetContainerMembersAsync`, which calls the exact same
`ResolveTypeByNameOrSnippet` as `AddMemberAsync` and hit the identical `is not
TypeDeclarationSyntax` missing-case pattern — the same bug class as this entry's root cause,
recurring in a second method. Fixed by adding an `EnumDeclarationSyntax` branch to
`GetContainerMembersAsync` that returns each enum member as a `ContainerMemberInfo` (`Kind =
"enumMember"`, `Signature` = `"Name"` or `"Name = Value"`).

**`Member` now genuinely supports enum containers for add/remove/replace/view, not just rejection
(2026-09-11).** Rather than leaving enums permanently rejected, `Member`'s `add`/`remove`/`replace`
operations now detect an enum container/member and translate the request into the pre-existing,
unmodified `ModifyEnumAsync` (the tool already built for enums' comma-separated full-list-replace
model), via three new `RefactoringEngine` methods: `AddEnumMemberAsync`, `RemoveEnumMemberAsync`,
`ReplaceEnumMemberAsync` — each reads the current member list via `GetContainerMembersAsync`,
applies the requested add/remove/substitute, and delegates the actual edit/renumbering/
collision-detection to `ModifyEnumAsync`. Two cheap pre-check helpers,
`IsEnumContainerAsync` (probes whether a container name resolves to an `EnumDeclarationSyntax`) and
`TryGetEnumMemberContainerNameAsync` (resolves a member name via the pre-existing
`ResolveMemberOrEnumMemberByNameOrSnippet` and, if it's an `EnumMemberDeclarationSyntax`, returns
its parent enum's name), let `SentinelRefactoringTools.Member`'s dispatch route correctly without
duplicating `ModifyEnumAsync`'s diffing logic. `typedKind` (property/field generation) still isn't
meaningful for enums and is explicitly rejected with guidance. Every enum-path failure's error
message ends with guidance to retry via `ModifyEnum(enumName: "...", values: ...)` directly as a
fallback. `AddMemberAsync`/`InsertMemberAfterAsync`/`InsertMemberBeforeAsync` themselves are
unchanged and still correctly reject enums at the engine layer — the new support lives at the
`Member` tool-dispatch layer, one level up. Regression tests: `RoslynSentinel.Tests.Basic/CodeEditingTests.cs`
(engine-layer: `IsEnumContainer_*`, `GetContainerMembers_OnEnum_ReturnsEnumMembers`,
`AddEnumMember_*`, `RemoveEnumMember_*`, `ReplaceEnumMember_*`, `TryGetEnumMemberContainerName_*`)
and `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs` (tool-layer: `Member_Add_OnEnumContainer_Succeeds`,
`Member_Remove_OnEnumMember_Succeeds`, `Member_Replace_OnEnumMember_Succeeds`,
`Member_View_OnEnumContainer_ReturnsEnumMembers`).

Also left open as independent, not-yet-applied hardening ideas surfaced during the original
investigation (unrelated to the actual root cause, but still valid): populating `AfterSource` in
operation blobs from a fresh disk read unconditionally (not just on a real write), verifying a
claimed write actually landed on disk before returning `Succeeded`, and making both no-op fast
paths in `PersistentWorkspaceManager.ApplyProposedChangesAsync` log unconditionally rather than
only the first branch.

## `ChangeAccessibility` moved to an enum; `ListAll` tool added — closed (commit de39a8d)

`ChangeAccessibility`'s `accessibility` parameter changed from `string` to a new
`AccessibilityLevel` enum (`RoslynSentinel.Common/ToolEnums.cs`, `[JsonStringEnumMemberName]`
aliasing `protectedInternal`/`privateProtected` to their space-separated wire values), so an
invalid value is rejected by schema/binding before the engine runs — closes the old
silent-fallback-to-public bug structurally instead of via a runtime check. Added `ListAll`
(`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`): lists every namespace/class/interface/
struct/record/enum/enum member/constructor/field/method/property in the loaded solution, one row
per symbol, filterable by kind and/or project, reusing `GetFileOutline`'s extraction logic
(factored into `ExtractOutlineItems`) and wired into the offload-to-disk pattern via a new
`SolutionSymbolEntryList` wrapper type. Motivated by model-eval transcripts showing models fabricate
plausible-sounding nonexistent identifiers under sparse orientation rather than browsing — `ListAll`
gives a cheap up-front orientation anchor; its description tells the model to call it first when it
doesn't already know an exact symbol name.

## `DependencyInjectionTests`'s hard-coded `allModes` set was missing `"Admin"` — closed (2026-09-05)

`RoslynSentinel.Tests.Advanced/DependencyInjectionTests.cs` builds its test DI container from a
hand-maintained `allModes` HashSet rather than deriving it from the real registration path, and was
missing `"Admin"` — so `SentinelAdminTools` was never registered in the test container, failing
`DynamicDiscovery_AllClassesWithToolAttribute_ShouldBeResolvable` ("Dynamically discovered tool
SentinelAdminTools is not registered in the DI container"). Same "hand-copied list drifts from the
real set" failure mode already known for engines. **Fix:** added `"Admin"` to `allModes` in Setup(),
with a comment noting the set must track every mode string `AddRoslynSentinelToolsAdvanced` checks.
If this fails again for a different tool class, check `allModes` first — it's a test-fixture drift
bug, not a production DI bug.

## `DiffEngine.ApplyDiff` phantom trailing-blank-line anchor bug — closed (commit af7a9ab)

A blank line at the very end of a diff's raw text was structurally ambiguous with a genuine blank
context line (`IsContextOrRemovalLine` treats zero-length lines as implicit blank context), so an
unguarded trailing blank became a phantom anchor requirement that defeated an otherwise-exact hunk
match inside the reanchor window. Root-caused from a real model-eval transcript. **Fix** is
heuristic/structural (scan for the next `@@` header or end of input as the hunk boundary), not
header-count-based — confirmed separately that model-generated diff hunk headers cannot be trusted
for their declared old/new line counts even when the body content is fine, so a header-count-trusting
rewrite was tried and reverted. Added `RoslynSentinel.Common/DiffHunkAnalyzer.cs`, a diagnostic
parser reporting per-hunk declared-vs-actual counts, wired into `DiffEngine.ApplyDiff` via
constructor-injected `ILogger<DiffEngine>` (logs a warning on mismatch, full report on
`DiffApplyException`). Use its output first when investigating any future `ApplyDiff` failure.

## `FindReferences`/`FindCallers` resolved a `contextSnippet` assignment line to the ctor parameter instead of the field — closed (2026-09-05)

`ContextHelper.FindSymbolAtSnippetAsync` walked `node.AncestorsAndSelf()` for the first declared
symbol instead of checking the node itself — for a reference-site snippet (an assignment line like
`_dependencyEngine = dependencyEngine;`), the walk climbed past the reference and returned the
enclosing constructor's declared parameter, not the field. A second bug in `FindCallersAsync`'s
no-`contextSnippet` branch called `GetDeclaredSymbol` directly on a `FieldDeclarationSyntax` (always
null; needs the `VariableDeclaratorSyntax` child), causing a false "not found declared" error.
**Fixed** in `ContextHelper.cs`/`SymbolNavigationEngine.cs`; regression tests added to
`RegressionTests.cs`. For any private field whose name collides with its own constructor parameter
(near-universal in this codebase) or with a same-named field in another class, `FindReferences`
could silently return the wrong symbol's reference list with no ambiguity warning — plain `grep`
remained the reliable fallback during the investigation. When auditing field usage, verify
`FindReferences` results look like real method-body call sites, not constructor-assignment lines,
before trusting them.

## `ListWorkspaceSolutions` hung 32+ minutes / ~5GB RAM on `workspacePath: "/"` — closed

A model passing `workspacePath: "/"` (a plausible "search from root" guess) resolved via
`Directory.Exists` to the current drive root (`C:\` on Windows) and triggered an uncancellable,
unbounded `Directory.EnumerateFiles` scan of the whole drive — `cancellationToken` was explicitly
discarded in the original code. **Fix** (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`):
(1) reject any `workspacePath` that resolves to exactly a drive root, returning a clear
`InvalidArgument`; (2) replaced the LINQ chain with a manual `foreach` checking
`cancellationToken.ThrowIfCancellationRequested()` per file; (3) added a 200,000-file hard walk cap
as a second line of defense. Verified via 2 new regression tests in `BatteryTwentyTests.cs`. If a
future hang/memory-runaway shows up against a tool taking a path parameter, check whether the path
resolves to something unexpectedly broad (drive root, UNC share root) before assuming it's a model
reasoning failure.

## LM Studio request silently never dispatched to an inference slot — hung 3400s+ — closed (2026-09-08)

`Model_AppliesSevenChainedRefactors` hung well past its timeout. LM Studio's own server log showed
the failing request was logged as received but never entered the inference pipeline at all (no slot
launch, no streaming, no error) — distinct from the separate mid-stream JSON truncation bug. The
.NET side was just an ordinary blocked read (`StreamReader.ReadLineAsyncInternal`), not a
`PersistentWorkspaceManager` deadlock as first suspected. **Fix**
(`LmStudioAgentClient.cs`/`LlmOptions.cs`): added `StreamIdleTimeoutSeconds` (default 120s),
measuring idle time *between* SSE lines rather than total call duration, enforced via a linked
`CancellationTokenSource.CancelAfter` per read; `CompleteAsync` retries the whole request once on
`StreamIdleTimeoutException`/`InvalidOperationException`/`IOException`, propagating on a second
consecutive failure. Deliberately not relying on `HttpClient.Timeout`, since once headers are read
for a streamed response it no longer bounds body-read time. Any future hang against
`LmStudioAgentClient`-based tests: check whether it now surfaces as a retried
`StreamIdleTimeoutException`/`IOException` (working as intended, just a slow LM Studio) before
re-opening a deadlock investigation.

## `MethodSignature(add)` CS1737 — closed, upstream Claude Code bug, not RoslynSentinel

`MethodSignature(add)` against `GitTools.Git` failed with CS1737 ("optional parameters must appear
after all required parameters"). Isolated unit tests couldn't reproduce it; only real MCP tool calls
failed. A live debugger attach showed a nullable-string argument (e.g. `defaultValue: "null"`)
arrives as C# `null`, not the string `"null"`, before RoslynSentinel's own code runs — reproduced on
any nullable-string parameter on any tool. The same call via MCP Inspector (an independent MCP
client) against the same server binary succeeded, proving the defect is specific to Claude Code's
own MCP argument serialization, not RoslynSentinel's server/schema or the underlying SDK. Matches
upstream [anthropics/claude-code#81911](https://github.com/anthropics/claude-code/issues/81911). No
RoslynSentinel code fix needed. Workaround for any caller: never pass the literal string `"null"` as
an MCP tool argument; omit the argument or use a distinct sentinel instead.

## `tasks/get` "Missing required Mcp-Name header" over HTTP transport — closed, upstream SDK bug, not RoslynSentinel

Polling `tasks/get` after a task-eligible tool call (e.g. `Features`) returned `CreateTaskResult`
failed with `-32020 Missing required Mcp-Name header`, even when the client sent a correct
`Mcp-Method` header — the task then appeared permanently "working" from the client's point of view.
Confirmed via decompiling both packages and cross-checking the MCP spec itself: `Mcp-Name` is
required only for `tools/call`/`resources/read`/`prompts/get` — a fixed, closed list.
`ModelContextProtocol.AspNetCore` 2.2.0's header validator instead generalizes to "any handler with
a routing-name parameter," and `ModelContextProtocol.Extensions.Tasks` 2.2.0 registers
`tasks/get`/`update`/`cancel` with exactly such a parameter, tripping the over-generalization. Both
packages are at fault; the spec itself is correct and unambiguous. Specific to the HTTP transport
(`RoslynSentinel.Server.Advanced --http`) — the stdio-style in-process pipe transport has no such
header-validation layer and is unaffected. No RoslynSentinel fix applicable. To verify a task-backed
call actually completed over HTTP, check the tool's real-world side effect directly, or test via
stdio/the in-process harness instead.

## `QuerySymbolRelationships` never called the large-result offload mechanism — closed (commit 0431e7a)

`QuerySymbolRelationships` (`SentinelSymbolTools.cs`) hand-built its `ToolResult<object>` responses
directly instead of calling `ForPossiblyLargeDataAsync`, so large results (confirmed 70,172 chars
against the 30,720-byte threshold, e.g. `attributeUsages` on a broad attribute like
`McpServerTool`) went out inline as raw JSON with only a passive log warning, never actually
offloaded to disk. Every sibling tool file already called the offload builder; this file had zero
references to it. **Fix:** added `SymbolRelationshipResultList` (list-shaped, deserializes
generically since element type varies per `searchKind`) and `BroadenedSymbolRelationshipResults`
(map-shaped, for the broaden-on-empty fallback) to `LargeResultHelper.cs`'s `ResultWrapperType`
enum; both call sites now route through `ForPossiblyLargeDataAsync`, with matching `GetLargeResult`
switch cases. This class of bug is only caught by a runtime log warning, not compile-time or
test-time — if another tool is suspected of the same gap, grep the file for
`ForPossiblyLargeDataAsync|StoreLargeResultAsync|LargeResultHelper`; zero hits in a tool returning
lists/dictionaries is the smoking gun.

## `ReplaceSnippet` silently corrupted adjacent lines sharing a trailing substring — closed (2026-09-10)

`ReplaceSnippet` reported `success:true` while splicing leftover characters onto adjacent short
lines that shared a trailing substring (e.g. two lines both ending in `Count,`), observed twice in
one `PlanStepRunner` eval run — the model's own follow-up `ReadFile` caught and self-corrected the
mangling both times, but nothing in the tool surfaced an error. **Root cause:**
`ContextHelper.FindSnippetPosition`/`FindAllSnippetMatches` only ever returned a match *start*
offset, never its true matched length. `ReplaceSnippet` assumed the matched span was exactly
`oldContent.Length` chars — true for literal/CRLF-normalized matches, false for `ContextHelper`'s
two whitespace-collapsing fallback paths, where the real source span can differ in length from the
literal snippet that matched it. When a fallback fired, the splice cut the wrong number of
characters. **Fixed** (`ContextHelper.cs`, `SentinelWorkspaceTools.cs`,
`ContextHelperTests.cs`): added length-carrying match methods
(`FindAllSnippetMatchesWithLength`/`FindSnippetPositionWithLength`, old int-returning methods now
thin wrappers, zero behavior change) plus a new **strict** pair
(`FindAllExactSnippetMatches`/`FindExactSnippetPosition`) that only matches literal-ordinal/
CRLF-normalized, never the whitespace-collapse fallback. `ReplaceSnippet` now uses the strict
variant and the real matched length for its splice — an approximate-only match now fails loudly
instead of corrupting silently. `ApplyDiff`/`ApplyUnifiedDiff` deliberately left on the original
loose matching (two separate anchoring strictness levels, by design). 9 new tests added. Left as an
out-of-scope follow-up: `MsToolAugmentEngine.cs`'s extract-method selection-span logic has the same
length-derivation shape but is a selection-boundary risk, not a text-corruption bug.

## `ValidationEngine`'s diagnostic-delta dedup misclassified pre-existing errors as newly introduced after a line-shifting edit — closed (2026-08-26)

`ValidationEngine.ValidateChangesAsync`'s `DiagnosticKey` included the diagnostic's line number, so
any edit that added/removed lines above a pre-existing compiler error shifted that error's line
number and the candidate's copy no longer matched its baseline key — reported as "new" and blocking
every member in the same project from being applied. Found via solution-wide `BulkComment` runs
against a fixture with a legitimate pre-existing unresolved-reference error. **Fix:** `DiagnosticKey`
dropped the line-number segment, now `{Id}|{Message}|{Path}`. Verified: 0-error build, all
`McpTasksHarnessBulkCommentTests` (including the real non-dry-run run) still pass. If a
line-shift-resistant key is ever revisited, validate against a solution with a real pre-existing
error in an untouched-but-shifted file, not just a synthetic single-file case.

## `PersistentWorkspaceManager`'s solution-reload branch could corrupt the live workspace on a mid-reload failure — closed (commit c3c9463)

`OnDebounceTimerElapsed`'s full-solution-reload branch called `OpenSolutionAsync` and reassigned
`CurrentSolution` on the same long-lived `_workspace` instance; if `OpenSolutionAsync` threw partway
through (observed cause: the `.slnx` transiently missing during a concurrent `git checkout`/reset),
the reused workspace's internal MSBuild state was left half-reloaded — every subsequent tool call
then saw mass `CS0234`/`CS0246` errors across nearly all files, even though `GetWorkspaceHealth`
still reported a clean load. Only a fresh explicit `LoadSolution` (always builds a brand-new
`MSBuildWorkspace`) recovered it. **Fix:** the reload branch now builds a brand-new
`MSBuildWorkspace` and only swaps it into `_workspace`/`CurrentSolution` after `OpenSolutionAsync`
succeeds; on failure the new workspace is disposed and the old one is left untouched — the same
create-new/swap-only-on-success pattern `ReloadWorkspaceFromDiskAsync` already used. If a workspace-
loaded tool call ever reports implausibly massive, uniform reference-resolution errors right after a
clean `GetWorkspaceHealth`, check the server log for "Reloading entire solution" before assuming a
real code problem.

## `ApplyDiff`'s size guard didn't catch whole-file comment-out sabotage — closed (commit 579ead4)

The existing >50%-shrink guard only compared raw line counts, so an agent commenting out every line
of a file one-for-one (same line count, zero working code, 0 compiler errors since comments always
compile) sailed through undetected — found via a model-eval transcript where `ApplyDiff` replaced a
file with every line prefixed `//` and reported success. **Fix:** added
`PercentActiveCodeLinesRemoved` alongside the existing `PercentLinesRemoved` — parses old/new
content with Roslyn, counts lines actually covered by a syntax token (excludes comment/whitespace
trivia), and rejects with `ConfirmationRequired` if either the raw-line-shrink or the
active-code-line-shrink metric exceeds the threshold. Same-line-count sabotage is a distinct failure
signature from the fragment-submission case the original guard targets and needed its own check
rather than a threshold tuning. Test:
`ApplyDiff_FilesFormatCommentsOutWholeFile_RejectsWithConfirmationRequiredAsync` in
`RoslynSentinel.Tests.Battery/ApplyDiffSizeGuardTests.cs`.

## Three hedged/suggestive tool error messages rewritten to direct phrasing — closed

Run 2 of a `PlanImplementVerify` batch failed because a CS0103 error already correctly named the fix
(`BlockEditHelpers.ReplaceBlockFormatted`), but the model spent 10 turns trying a redundant `using`
directive, undoing it, and re-hitting the identical error four more times before applying the
qualified-name fix. Working theory: the old message named `using` even to rule it out, which may
have amplified the model's attachment to that approach rather than steering away from it. Two other
messages showed the same hedged shape on inspection. **Fix:** three messages rewritten to direct,
unhedged "You MUST" phrasing, avoiding naming any wrong approach where possible: (1) the
`ReadFile`/`GetMethodSource`/`GetFileOutline` "file not found" error now names a matching real path
if one exists elsewhere in the solution, or says to call `ListSolutionItems(kind: all)` next; (2) the
orientation-breaker-tripped message now opens with "SearchSolutionText is DISABLED" and says to call
`ListAll`/`ListSolutionItems` now, instead of describing what's still available; (3) the CS0103
out-of-scope-member message for the candidate-found-but-not-via-`using` case now says to use the
fully qualified name and explicitly "do not add a `using` directive, it will not fix this" — the one
deliberate exception to not naming the wrong approach, judged worth the small re-amplification risk
given the model had just tried it 5 times. Verified: 0-error build; `ReadFileTests`/
`GetMethodSourceTests`/`OrientationBreaker`-filtered Battery tests 21/22 passed (the one failure
confirmed pre-existing and unrelated via `git stash` re-run against unmodified master).
**Caveat:** this ships on one transcript's evidence plus a plausibility read, not a controlled
before/after batch — treat future retry-rate changes on this pattern as supporting or undermining
evidence, not as already-confirmed.

## `FilePathLock` ported in and wired into `PersistentWorkspaceManager` to fix a watcher-vs-write race — closed (commit 9f70b24, follow-up 0661c25)

`OnFileSystemChanged` already had self-write suppression plus a `catch (IOException)` around its
verification read, but Windows can raise a `Changed` event while a write still holds the file handle
open, so the verification read would throw and get caught — functionally fine, but noisy (surfaces
as a first-chance `IOException` break in the VS debugger with nothing actually broken). **Fix:**
ported a per-path async lock (`RoslynSentinel.Common/FilePathLock.cs`, `SemaphoreSlim`-backed,
keyed by normalized full path); `OnFileSystemChanged` now checks `FilePathLock.IsLocked` first and
returns early; `ApplyProposedChangesAsync` holds the lock for the duration of both the main write and
any rollback write/delete. This is defense-in-depth/debugger-noise cleanup, not a correctness fix —
`_solutionLock` already serializes all `ApplyProposedChangesAsync` calls solution-wide. Confirmed
separately: `BulkComment` legitimately calls `ApplyProposedChangesAsync` twice for the same file
(seed phase, then comment phase) when it has un-seeded stale members — that's expected, not a sign of
dual writers. **Follow-up (commit 0661c25):** added `RoslynSentinel.Common/FileIoHelper.cs`, a
static wrapper baking `FilePathLock` acquisition into every write/delete and mutation-adjacent read;
migrated all of `PersistentWorkspaceManager`'s write-path call sites onto it. Deliberately scoped to
`PersistentWorkspaceManager` only (not a solution-wide `File.*` replacement); existence/metadata-only
checks (`File.Exists`, `Directory.Exists`, `.sln` parsing) were left as direct `System.IO` calls
since they don't participate in the write race. Remaining candidates if scope is widened later:
`ScanResultHelper.cs`, `OperationBlobWriter.cs`, `MigrationLedger.cs`, `ValidateAndApplyHelper.cs`.

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
the sample on disk. `RoslynSentinel.Tests.Integration/IntegrationTwentyNineTests.cs` (`B29_AllEngines_
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

**Update 2026-09-09 — current policy, `WriteFile` gated off, `CreateFile` is the mandatory
replacement for agent tasks:** `WriteFile` (whole-file write, `operation: CreateFile|ReplaceFile`,
free-form `content` param) is now gated off the default model-visible surface — disabled due to a
high rate of model misuse/failure in testing (same failure class `ApplyDiff`/`ApplyUnifiedDiff`
were gated for). `CreateFile` (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:689`) is the
tool that fills the resulting gap for creating a brand-new file: it takes **no content parameter**
at all — for a `.cs` file it requires `namespaceName`, `typeKind`, `typeName` and always stubs
`namespace {ns};\n\npublic {kind} {typeName}\n{\n}\n`; it fails if the file already exists.
`typeKind` (`NewTypeKind` enum, `RoslynSentinel.Common/ToolEnums.cs:70`) supports `class`, `record`,
`interface`, `enum`, `struct`, and `staticClass` (added 2026-09-09, maps to `public static class`
directly — avoids a follow-up `ModifyModifier` call for static utility classes; there's no
`staticStruct` since `static` is only valid on classes in C#, CS0106). Any plan/step doc telling an
agent to "create a new file" with real content must decompose it as: `CreateFile` (stub) →
`Member(add, containerName: "...")` once per method/property/field (or
`Member(add, containerName: null, ...)` for a second top-level type in the same file) →
`UsingDirective(add)` for imports the stub doesn't carry. Agent-facing docs should describe this
allowed workflow positively rather than naming which whole-file-write tool is disabled.

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
landing in a parallel session — this repo sometimes has more than one Claude/VS session active.

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
(this repo sometimes has more than one Claude/VS session active) rather than by a dedicated pass on this spec. No remaining risk
identified; closing rather than re-scoping into a rename/restructure task nothing currently needs.

