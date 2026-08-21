# TODO — known gaps not yet fixed

Running list of confirmed-but-deferred issues found during tool development/grading. Each entry
should have enough detail to pick back up without re-discovering the root cause. Remove an entry
once it's actually fixed (and note the fix in SCENARIOS.md/commit history instead).

## `ApplyDiff`'s outer error wrapper still says "check the solution/path" for non-workspace failures

**Found:** 2026-08-20/21, while migrating tests off a dead `RefactoringEngine.SafeDeleteSymbolAsync`
copy (see commit "Merge SafeDeleteSymbolAsync's reflection-risk check..."); root-caused and the
underlying anchor-search bug fixed 2026-08-21 (see commit "Fix DiffEngine's ReanchorHunk desyncing
on unmarked blank hunk lines").

**What was fixed:** the hunk-anchor search itself had a real defect, now fixed in
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

**Still open (smaller, not fixed in this pass):** the *outer* tool-layer wrapper
(`SentinelWorkspaceTools.cs`'s `ApplyDiff` method) still catches every exception from
`DiffEngine.ApplyDiff` — including the inner `InvalidOperationException` this bug produced — with a
generic *"ApplyDiff diff apply for '\<path\>' failed unexpectedly (InvalidOperationException). Check
that the solution is loaded and the file path is valid."* message, even when the solution was loaded
and the path was valid the whole time and the real cause is a hunk-content mismatch. This didn't
need fixing to resolve the underlying bug (the inner message was accurate and specific enough to
diagnose it), but it's still misleading on its own whenever *any* `DiffEngine.ApplyDiff` exception
propagates up — worth a follow-up that either catches `InvalidOperationException` specifically and
passes its message through unwrapped, or drops the generic "check the solution/path" sentence when
an inner message already explains the real cause.

**Related, separate observation:** applying this fix's own edit to `DiffEngineTests.cs` (via the
IDE's direct file-edit tool, not `ApplyDiff`) silently normalized that file's line endings from
mixed CRLF/LF to pure LF — the same class of unrequested whole-file line-ending rewrite as
`ApplyDiff`'s CRLF-forcing bug (see the "reflows far more of the file" entry, now fixed), just in
the opposite direction and via a different tool. Not investigated further here since it didn't
break anything (LF end-to-end is arguably the more consistent state) and reverting file-by-file
would risk introducing a real mistake, but worth knowing this isn't purely an `ApplyDiff`-specific
problem — something in the write path generally doesn't preserve mixed line endings verbatim.

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

## `contextSnippet` wording audit across tool descriptions

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

**Why this matters:** with the 2026-08-19 fix, `contextSnippet` is now genuinely optional for any
non-overloaded target — but agents don't know that from the description alone, and will likely keep
supplying one defensively "just in case," reintroducing exactly the kind of avoidable mismatch this
session fixed for `ReplaceMember` specifically, on some other tool or some other snippet shape not yet
seen in a live run.

**Suggested approach:** a single pass across every `[Description]` mentioning `contextSnippet` (grep
for `ToolParams.ContextSnippet` and inline duplicated wording — some tools use the shared constant,
others still inline their own text) to state consistently: (1) only needed when the name is ambiguous
(2+ same-named declarations); (2) prefer the shortest substring that's still unique — a signature line
is usually enough, a full body is rarely necessary and is more failure-prone to reproduce verbatim; (3)
copy it verbatim from a prior tool result rather than retyping from memory. Not done as part of the
`ReplaceMember` fix, which addressed the resolution *logic* but not the *wording* that leads callers to
over-supply a snippet in the first place.

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

## `ConvertExpressionBodyAsync` has the same contextSnippet bug class as `ReplaceMember`, different code shape

**Found:** 2026-08-19, while fixing `ReplaceMember`'s `ResolveMemberByNameOrSnippet`/
`ResolveTypeByNameOrSnippet` single-candidate bug (see SCENARIOS.md Scenario 4 / "Fixed" list).

**What:** `RefactoringEngine.ConvertExpressionBodyAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs`,
~line 1643) resolves its target with an `if (contextSnippet != null) { position-based } else {
name-based candidates }` branch — structurally different from `ResolveMemberByNameOrSnippet`'s
"compute name-based candidates first, only consult the snippet if 2+" shape. This means a supplied
`contextSnippet` bypasses name-based candidate computation entirely rather than being ignored when
unnecessary, so the same failure mode (a defensive/mismatched snippet blocking an otherwise-unambiguous
resolution) is still possible here, just via a different code path.

**Why not fixed alongside `ReplaceMember`:** the one-line "skip if `candidates.Count <= 1`" guard used
for the two shared helpers doesn't directly apply — this method would need restructuring to compute
name-based candidates unconditionally first, then decide whether to also honor a snippet-based
position, which is a larger, more careful change than the guards applied elsewhere. Worth checking
whether any other `RefactoringEngine`/`StructuralRefinementEngine` methods share this same
`if (contextSnippet != null) { position } else { name }` shape (not yet audited) before fixing, so all
affected methods get the same treatment in one pass rather than one at a time as each is found live.

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

