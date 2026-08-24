# Plan — Tool Disambiguation Remediation

## Title
Close the 15 name-only-resolution gaps found in `docs/tool-disambiguation-survey-v1.md` via
`contextSnippet`-based disambiguation, migrate the remaining raw line/column tools to the same
pattern, and — carried by the same resolution helpers, evaluated only once everything is wired up —
add a self-correcting failure hint so a rejected disambiguation attempt tells the calling agent how
to fix its next call instead of just erroring.

## Background
Two prior documents set up this plan:
- `docs/plan-symbol-tool-hardening-v1.md` (already implemented) fixed whole-file reformatting and
  added `contextSnippet`-based disambiguation to `FindReferences`/`QuerySymbolRelationships`.
- `docs/plan-tool-disambiguation-survey-v1.md` (the survey plan) and its output,
  `docs/tool-disambiguation-survey-v1.md` (the survey results), found that `RefactoringEngine`'s
  name-only resolution — `GetMemberName(member) == targetName` via `FirstOrDefault`, or
  `Identifier.Text == typeName` for type-level targets — is shared by 15 mutating tools. When two
  members share a name (overloads, a field/method collision, same-named members in sibling nested
  types), these tools silently mutate whichever candidate `DescendantNodes()`'s document-order walk
  hits first — no error, no warning, no way to target a different one. `ReplaceMember` and
  `RemoveMember` are CRITICAL because a wrong match means undetectable code corruption or code loss.

While designing the remediation, a further, orthogonal question came up: when a supplied
`contextSnippet` or line/column *doesn't* match — either not found at all, or ambiguous — can the
tool suggest a corrected value instead of just erroring, so the calling agent can self-correct in
one more call instead of re-reading the file or blindly retrying with stale information?

This plan builds the hint capability early (as 3 switchable candidate strategies, wired into the
shared resolution helpers every migrated tool uses) but **defers picking a winner to the very last
task**, after every tool in this plan is migrated and has real, varied failure cases to evaluate
against. Evaluating with only synthetic fixtures (as an earlier draft of this plan proposed) risks
picking a strategy that looks good on toy cases but doesn't hold up across the actual variety of
tools this plan touches — waiting until everything is wired up gives Task (the final one) real
material to judge against.

**Why the switch lives in `RefactoringEngine`, not in `ContextHelper`:** `ContextHelper.
FindSnippetPosition` only sees raw text offsets — it has no access to symbol names, signatures, or
declarations. A genuinely useful hint ("the nearest candidate is `Add(string a, string b)` at line
42") needs the resolved member/type declarations, which only exist at the `RefactoringEngine`
resolution-helper layer. So `ContextHelper` stays a **hint-agnostic, pure position-finder** — it
gains one new *additive* method that returns structured ambiguity data (match count + candidate
positions) instead of a baked message, used only by the new resolution helpers; its existing
`FindSnippetPosition`/`FindNodeAtSnippet`/`FindSymbolAtSnippetAsync` are untouched, so every existing
caller (`FindReferences`, `InspectSymbol`, `ExtractMethodSafe`, etc.) keeps working exactly as today
with zero risk of regression from this plan. The new `ResolveMemberByNameOrSnippet`/
`ResolveTypeByNameOrSnippet` helpers (Task B) own the `HintStrategy` enum and switch, and build the
actual hint text from the candidate declarations after calling `ContextHelper`'s new structured-data
method.

## Assumptions
- You are editing `RoslynSentinel.Basic`/`RoslynSentinel.Common`/`RoslynSentinel.Server.Basic`/
  `RoslynSentinel.Server.Advanced` engine/tool code directly with normal file tools (Read/Edit/Bash),
  not by calling the RoslynSentinel MCP tools on themselves.
- Verified by `RoslynSentinel.Tests`, not by round-tripping through a live MCP session. Kill/
  rebuild/restart is only needed once at the end, for an optional live smoke test.
- Line numbers cited below were correct as of writing but **will have drifted** — re-locate every
  target with Grep first, every task, no exceptions. Two prior plans in this repo made the same
  point; take it as seriously as they did.
- Build and test per task (0 errors before moving on). Diff the failing-test list against
  `docs/known-failing-tests.txt` after every task rather than eyeballing all ~75 pre-existing
  failures. If that baseline file doesn't match a fresh run when you start, regenerate it first and
  say so explicitly — don't silently diff against a stale baseline.
- Commit each task separately with a focused message; do not bundle unrelated tasks into one commit.
- **Additive, not breaking**, throughout: every `contextSnippet` parameter is optional (nullable,
  defaults to `null`), preserving today's name-only call signature as the default path. This mirrors
  existing precedent already in this codebase — `RefactoringEngine.ConvertExpressionBodyAsync`
  (~line 1545) and `ExtractConstantAsync` (~line 1708) already take a required name/snippet
  parameter *and* optional `lineBefore`/`lineAfter` side by side. When `contextSnippet` is supplied,
  it narrows/replaces the `FirstOrDefault`-by-name lookup; when omitted, existing behavior
  (including its existing defect) is unchanged for backward compatibility — see Risks for the
  deliberately-deferred question of whether to eventually change that default.
- **`ContextHelper.FindSnippetPosition` itself is never modified** — only added to (a new,
  structured-data-returning sibling method). This keeps every existing caller's behavior byte-for-
  byte identical throughout this entire plan, including through the final hint-strategy evaluation.
- The hint strategy chosen at the end applies retroactively to every tool this plan touches (they
  all funnel through the same `HintStrategy`-dispatching resolution helpers) — there is no separate
  per-tool rollout once the final task picks a winner and collapses the switch.

## Approach
1. Task A — Add a structured, non-throwing ambiguity-data method to `ContextHelper.cs` (independent,
   do first — the resolution helpers in Task B need it).
2. Task B — Add the shared `ResolveMemberByNameOrSnippet`/`ResolveTypeByNameOrSnippet` helpers to
   `RefactoringEngine.cs`, with all 3 hint strategies implemented internally behind a
   `HintStrategy` enum/switch (depends on A).
3. Task C — Migrate the 2 CRITICAL tools (`ReplaceMember`, `RemoveMember`) to the helpers.
4. Task D — Migrate the HIGH tool (`ChangeAccessibility`) and the type-level HIGH-adjacent tools
   (`ModifyBaseType` add/remove).
5. Task E — Migrate the remaining MEDIUM/LOW-MEDIUM tools (`ModifyAttribute` ×3, `ModifyModifier`
   ×2, `AddSummaryComment`, `AddMember`, `AddMemberTyped`, `AddConstructorParameter`, `ModifyEnum`).
6. Task F — Migrate `SafeDeleteUnusedSymbol` to handle-based (`docCommentId`+`projectName`)
   resolution as the preferred path, with `symbolName`+`contextSnippet` as a secondary path and
   `line`/`column` retained as a legacy third path.
7. Task G — Migrate the remaining raw-range tools (`WrapRange`'s 3 actions, `InvertAssignments`) to
   `contextSnippet`-based resolution via the same helpers/switch as C–E.
8. Task H — Sweep tool `[Description(...)]` text for every tool touched by C–G.
9. Task I — Evaluate the 3 hint strategies against the full set of migrated tools and pick one
   (depends on C–G all being done — this is deliberately last, see Background).
10. Task J — Full-suite verification and summary table.

Tasks C/D/E are independent of each other once B lands, but each depends on B. Tasks F and G are
independent of C/D/E and of each other (different tools, different resolution shapes — F uses
`LocateSymbol`'s existing handle machinery, not the Task B helpers at all; G uses the same
`contextSnippet`-for-range pattern as `ExtractMethodSafe`). Task H depends on whichever of C–G
actually landed. Task I depends on C–G all being complete (it needs their tests as evaluation
material) — note Task F's hint story is different from the others since its preferred path
(`docCommentId`) sidesteps ambiguity entirely rather than resolving it; Task I should evaluate hint
quality using C/D/E/G's fixtures, and separately confirm F's secondary/legacy paths still produce a
sensible hint when they fail. Task J is last.

## Key Files
- `RoslynSentinel.Common/ContextHelper.cs` — existing `FindSnippetPosition`/`FindNodeAtSnippet`/
  `FindSymbolAtSnippetAsync` (untouched); gains one new structured-data method (Task A).
- `RoslynSentinel.Basic/RefactoringEngine.cs` — `GetMemberName`, all 15 name-only `FirstOrDefault`
  call sites, and the new shared resolution helpers + `HintStrategy` switch (Task B).
- `RoslynSentinel.Common/PersistentWorkspaceManager.cs` — `ResolveFromWireAsync` (~line 1494),
  `ResolveSymbolAsync`/`ResolveByDocCommentIdAsync` (~line 1467, ~1476) — existing handle-based
  resolution machinery to reuse verbatim for `SafeDeleteUnusedSymbol` (Task F), not reimplement.
- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`'s `RenameSymbol` (~line 210) — the
  precedent to copy for Task F's `docCommentId`+`projectName`+`sessionId` path.
- `RoslynSentinel.Server.Basic/SentinelSymbolTools.cs`'s `PreviewRenameImpact` (~line 347) — the
  precedent to copy for Task F's dual-path design (handle OR filepath+symbolName+contextSnippet).
- `RoslynSentinel.Basic/StructuralRefinementEngine.cs` — `SafeDeleteSymbolAsync` (Task F).
- `RoslynSentinel.Basic/MappingEngine.cs` — `InvertAssignmentsAsync` (Task G).
- `RoslynSentinel.Basic/MsToolAugmentEngine.cs` — `ExtractMethodSafeAsync` (~line 1287), the
  precedent to follow for range-based `contextSnippet` resolution (Task F reference, don't modify).
  Note this is a *different, newer* method than `RefactoringEngine.ExtractMethodAsync` (~line 299,
  `startLine`/`startLineText` shape) — the tool `ExtractMethodSafe` calls the former, not the
  latter; don't confuse the two when looking for "the existing range pattern."
- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs` — tool wrappers for `ReplaceMember`,
  `RemoveMember`, `ChangeAccessibility`, `ModifyAttribute`, `ModifyModifier`, `ModifyBaseType`,
  `AddSummaryComment`, `AddMember`, `AddMemberTyped`, `AddConstructorParameter`, `ModifyEnum`.
- `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` — `SafeDeleteUnusedSymbol` tool wrapper
  (Task F).
- `RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs` — `WrapRange`,
  `InvertAssignments` tool wrappers (Task G).
- `RoslynSentinel.Common/DocumentEditResult.cs` — `EditOutcome` enum; check whether a new value is
  warranted for ambiguous-match failures (Task B).
- `docs/tool-disambiguation-survey-v1.md` — source of truth for which tools are in scope and their
  current defect evidence; re-verify each citation before acting on it (see Assumptions).
- `docs/known-failing-tests.txt` — pre-existing failure baseline; diff against this after every task.

## Risks & Open Questions
- **All 3 hint strategies must stay live and switchable through Task I — don't let an earlier task
  quietly collapse to one.** Because evaluation is last, every migrated tool's tests (Tasks C–E, G)
  only need to assert *that* a hint is present and *shaped* correctly for whichever strategy is
  currently active, not which strategy is "correct" — that judgment is explicitly deferred to Task I.
  Resist writing tests in Tasks C–E/G that bake in an assumption about which strategy wins. (Task F's
  preferred `docCommentId` path has no ambiguity to hint about at all — see its own section — but its
  secondary/legacy paths still go through the same hint machinery and are subject to this same rule.)
- **A "helpful" hint must not become a new silent-wrong-guess vector.** The hint is advisory text for
  the *calling agent* to read and act on in a follow-up call — it must never be used to auto-select a
  match and proceed with the operation. Once `contextSnippet` fails to uniquely resolve, the tool
  must still refuse and return an error (with the hint attached); the hint changes what's *in* the
  error, not whether an error occurs. Getting this backwards would silently reintroduce a version of
  the exact defect this whole effort exists to close, just fuzzier.
- **`GetMemberName`-based lookups mix two different node shapes.** Some of the 15 tools resolve a
  `MemberDeclarationSyntax` (method/property/field/ctor) by `GetMemberName`; others resolve a
  `BaseTypeDeclarationSyntax`/`TypeDeclarationSyntax` by `Identifier.Text` directly (`ModifyBaseType`,
  parts of `AddMember`'s container lookup). Task B's helpers should handle both shapes — don't force
  one generic helper if it makes the type-level callers awkward; two small helpers (member-level,
  type-level) sharing the same hint-building logic underneath is fine, and probably clearer.
- **`RemoveMember`'s existing precheck (from the *prior* plan `plan-symbol-tool-hardening-v1.md`'s
  own Task F — unrelated to this plan's Task F) resolves separately from the removal itself.** The
  precheck calls
  `_symbolNavigationEngine.FindCallersAsync(filePath, memberName, ...)` and
  `FindImplementationsForMemberAsync(filePath, memberName, ...)` — thread the new `contextSnippet`/
  `lineBefore`/`lineAfter` into *those* calls too (both already accept them as optional parameters
  per `SymbolNavigationEngine.cs` ~line 1281 and ~1420 — confirm still true). Otherwise a caller's
  snippet could correctly disambiguate which overload gets *removed*, while the precheck's
  callers/implementations check silently still reports on a different, first-matched overload. This
  is the single most important correctness detail in Task C — verify it with a dedicated test, don't
  assume threading the parameter through is sufficient without checking the result.
- **Deprecation-tracking decision, deliberately deferred.** This plan makes `contextSnippet` optional
  everywhere (and, for Task F, keeps `line`/`column` as a legacy third path rather than removing it).
  Whether to eventually deprecate/require the new paths, or warn when a tool silently first-matches
  without one, is a separate decision — raise it in Task J's summary as a recommendation, don't
  decide it here.
- Explicitly **out of scope** for this plan:
  - The 2 LOWER-severity tools the survey flagged as not truly in the defect shape (`CreateProject`,
    `SplitProjectByFolder`) — these create new targets or operate at project scope, not symbol
    resolution; don't touch them.
  - The "tools with partial escape hatches" the survey listed (`ApplyMethodCodemod`,
    `ApplyClassCodemod`, `ChangeSignature`, `PullUpMember`, `MoveType`, `Inline`,
    `IntroduceParameterObject`) — these already accept optional `contextSnippet`-shaped narrowing;
    confirm in Task J that they still work, but don't re-architect them here.
  - Re-litigating `FindReferences`/`QuerySymbolRelationships` (already fixed by
    `plan-symbol-tool-hardening-v1.md`) — they call `ContextHelper`'s existing, untouched methods, so
    they're unaffected by this plan either way.
  - Making `contextSnippet` required on any tool (additive only, this plan).

## Steps

### Task A — Structured ambiguity data from ContextHelper
**Problem:** `ContextHelper.FindSnippetPosition` throws `InvalidOperationException` with a baked
plain-string message on failure. The new resolution helpers (Task B) need the raw ambiguity data
(how many matches, where they are) to build their own hint text — not a pre-built message string.

**Fix:** Add a new, non-throwing method to `ContextHelper.cs` alongside the existing
`TryFindSnippetPosition` (~line 154-168), e.g.:
```csharp
/// Non-throwing variant that returns every candidate match instead of resolving to one.
/// Empty list = not found. Single-item list = unambiguous (equivalent to FindSnippetPosition's
/// success case). Multi-item list = ambiguous; caller (a resolution helper) decides what to do,
/// including building its own hint from the candidates.
public static List<int> FindAllSnippetMatches(
    SourceText sourceText, string contextSnippet,
    string? lineBefore = null, string? lineAfter = null)
```
Extract the match-finding logic already in `FindSnippetPosition` (exact match ~line 30-36,
line-ending-agnostic retry ~line 38-55, whitespace-collapsed fallback ~line 57-70, and the
`lineBefore`/`lineAfter` filtering ~line 90-125) into this new method, then have
`FindSnippetPosition` call it internally and keep its existing throw-based contract on top —
**do not duplicate the matching logic, refactor `FindSnippetPosition` to call the new method and
throw based on its result.** This keeps the two implementations from drifting apart, while
guaranteeing `FindSnippetPosition`'s existing callers see zero behavior change (same exceptions,
same messages, same everything).

**Test:** unit tests directly against `FindAllSnippetMatches` — 0/1/2+ matches, with and without
`lineBefore`/`lineAfter` narrowing, matching the existing `FindSnippetPosition` test fixtures if any
exist (check first). Also re-run every *existing* `ContextHelper`-adjacent test (anything exercising
`FindReferences`, `InspectSymbol`, `ExtractMethodSafe`, `ExtractLocalVariable`) to confirm the
internal refactor of `FindSnippetPosition` didn't change its behavior — this is the regression check
that matters most for this task. Build, test, diff against `docs/known-failing-tests.txt`, commit.

### Task B — Shared resolution helpers with all 3 hint strategies
**Problem:** 15 call sites in `RefactoringEngine.cs` independently reimplement
`FirstOrDefault(m => GetMemberName(m) == targetName)` with no shared disambiguation path. Adding
`contextSnippet` support to each site individually would duplicate the same ambiguity-handling logic
15 times — and duplicate the not-yet-decided hint strategy 15 times over, making Task I's later
strategy swap need to touch 15 places instead of one.

**Fix:** add to `RefactoringEngine.cs`, near `GetMemberName` (~line 3540, re-locate first):
```csharp
private enum HintStrategy { NearestSnippet, CorrectedCoordinates, NearMissList }

// Flip this constant and recompile to switch strategies — Task I decides the final value.
private const HintStrategy ActiveHintStrategy = HintStrategy.NearestSnippet;

/// Resolves a member by name, optionally disambiguating with a contextSnippet when the name
/// matches more than one declaration. Falls back to first-match-by-name when contextSnippet is
/// null, preserving existing behavior for callers that don't supply one. On an unresolvable or
/// still-ambiguous contextSnippet, throws with a hint built per ActiveHintStrategy.
private static MemberDeclarationSyntax? ResolveMemberByNameOrSnippet(
    SyntaxNode root, SourceText sourceText, string memberName,
    string? contextSnippet, string? lineBefore, string? lineAfter,
    Func<MemberDeclarationSyntax, bool>? extraFilter = null)
{
    var candidates = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
        .Where(m => GetMemberName(m) == memberName && !(m.Parent is InterfaceDeclarationSyntax))
        .Where(m => extraFilter == null || extraFilter(m))
        .ToList();

    if (contextSnippet == null)
    {
        return candidates.FirstOrDefault(); // unchanged default behavior
    }

    var matches = ContextHelper.FindAllSnippetMatches(sourceText, contextSnippet, lineBefore, lineAfter);
    // ... resolve via matches; on 0 or 2+ ambiguous-after-narrowing results, build and throw
    // a hint per ActiveHintStrategy using `candidates` (for symbol-aware hints) and `matches`
    // (for position-aware hints).
}
```
Design the hint-building `switch` (likely a private `BuildHint(HintStrategy, ...)` method shared by
both the member-level and type-level resolution helpers) with access to the resolved
`MemberDeclarationSyntax`/`BaseTypeDeclarationSyntax` candidates, not just raw positions — this is
the whole reason the switch lives here and not in `ContextHelper` (see Background). Implement all
three strategies now, fully, even though none is chosen yet:
1. **`NearestSnippet`** — the closest actual candidate's signature/declaration text and its line
   number, e.g. "closest match: `public int Add(int a, int b)` at line 42."
2. **`CorrectedCoordinates`** — same idea, phrased as corrected line/column, e.g. "nearest candidate
   is at line 42, column 9."
3. **`NearMissList`** — short previews of every candidate that matched by name but wasn't
   disambiguated (or was excluded by `lineBefore`/`lineAfter`), e.g. "3 candidates: line 12
   `public int Add(int a, int b)`, line 40 `public int Add(string a, string b)`, line 58
   `private int Add(int a)`."

Add a second, parallel helper for the type-level lookups (`ModifyBaseType` et al.) that resolves a
`BaseTypeDeclarationSyntax`/`TypeDeclarationSyntax` by `Identifier.Text`, following the same
null-contextSnippet-falls-back-to-`FirstOrDefault` shape and reusing the same `BuildHint` switch.
Name it `ResolveTypeByNameOrSnippet`.

Decide at each call site (in later tasks) whether to let the resolution helper's exception propagate
(likely fine, tool wrappers already wrap calls in try/catch) or catch-and-convert to a
`DocumentEditResult` with a dedicated outcome. Check `DocumentEditResult.cs`'s `EditOutcome` enum
for whether a new value (e.g. `AmbiguousMatch`) is warranted, or whether `CannotEdit` + a clear
`Message` (carrying the hint) is sufficient.

**Test:** unit tests directly against both new helpers, for **each** of the 3 `ActiveHintStrategy`
values (flip the constant, rebuild, re-run — or better, expose the strategy as an internal test seam
so a single test run can exercise all 3 without recompiling; check `InternalsVisibleTo` covers
`RoslynSentinel.Tests` for this assembly, add it if not, this makes Task I far less tedious). Cover:
resolves correctly with contextSnippet omitted (byte-for-byte matches pre-existing `FirstOrDefault`
semantics); resolves the correct member when 2 overloads exist and a disambiguating snippet is
given; on an ambiguous snippet, the resulting error carries a hint of the expected shape for
whichever strategy produced it. Build, test, diff, commit.

### Task C — Migrate ReplaceMember and RemoveMember (CRITICAL)
**Files:** `RefactoringEngine.cs` `ReplaceMemberAsync` (~line 1208) and `RemoveMemberAsync` (~line
1310); tool wrappers in `SentinelRefactoringTools.cs` (`ReplaceMember` ~line 333, `RemoveMember`
~line 382 — re-locate first, these will have shifted since Task B's insert).

Add optional `contextSnippet`/`lineBefore`/`lineAfter` parameters to both engine methods and their
tool wrappers, threading through to Task B's `ResolveMemberByNameOrSnippet`. Update
`[Description(...)]` on both tools minimally (the full sweep is Task H) so the schema doesn't ship a
new parameter with a stale description.

**Critical follow-through for RemoveMember specifically** (see Risks): thread the new
`contextSnippet`/`lineBefore`/`lineAfter` into the tool-level precheck's
`FindCallersAsync`/`FindImplementationsForMemberAsync` calls too (`SentinelRefactoringTools.cs`
~line 394-413), not just into the removal itself. Verify explicitly with a test that the precheck
reports on the *disambiguated* member, not a separately-first-matched one.

**Test:**
- `ReplaceMember` against a file with 2 overloads: without contextSnippet, behavior is unchanged
  (replaces first-in-document-order, matching a pre-migration snapshot of the same test); with a
  contextSnippet identifying the second overload, replaces the correct one; assert the untouched
  overload's body is provably unchanged (not just that *a* replacement happened).
- `RemoveMember` against a file with 2 overloads where only one has a real caller: without
  contextSnippet, current first-match behavior is preserved; with contextSnippet targeting the
  unused overload, removal succeeds; with contextSnippet targeting the used overload, the precheck
  refuses and **the caller list in the error message belongs to the targeted overload**, not the
  other one.
- A contextSnippet matching 2+ locations returns an error carrying a hint (shape-agnostic assertion
  per the Risks note — don't assert on specific hint wording yet).

Build, run full suite, diff against `docs/known-failing-tests.txt`, commit.

### Task D — Migrate ChangeAccessibility and ModifyBaseType (HIGH)
**Files:** `ChangeAccessibilityAsync` (`RefactoringEngine.cs` ~line 2937), `AddBaseTypeAsync`/
`RemoveBaseTypeAsync` (~line 2666, ~2887); tool wrappers `ChangeAccessibility` (~line 524) and
`ModifyBaseType` (~line 828) in `SentinelRefactoringTools.cs`.

`ChangeAccessibility` resolves a `MemberDeclarationSyntax` — use the member-level helper.
`ModifyBaseType`'s add/remove resolve a `TypeDeclarationSyntax` by `Identifier.Text` — use the
type-level helper. Same additive-parameter approach as Task C.

**Test:** `ChangeAccessibility` against 2 overloads, contextSnippet picks the correct one, asserts
the other overload's accessibility is untouched. `ModifyBaseType` add/remove against 2 same-named
nested types in sibling containers (construct a genuinely compilable ambiguity — plain top-level
type name collisions don't compile). Build, test, diff, commit.

### Task E — Migrate remaining MEDIUM/LOW-MEDIUM tools
**Files (re-locate all before starting):**
- `AddAttributeAsync`/`ReplaceAttributeAsync`/`RemoveAttributeAsync` (`RefactoringEngine.cs` ~2586,
  ~2715, ~2824) — tool wrapper `ModifyAttribute` (add/replace/remove actions).
- `AddModifierAsync`/`RemoveModifierAsync` (~3003, ~3068) — tool wrapper `ModifyModifier`.
- `AddSummaryCommentAsync` (~3132) — tool wrapper `AddSummaryComment`.
- `AddMemberAsync` (~1256, container-name resolution, type-level helper) and whatever backs
  `AddMemberTyped` (delegates to `AddPropertyAsync`/`AddFieldAsync` per the survey — confirm exact
  delegation) — tool wrappers `AddMember`, `AddMemberTyped`.
- `AddConstructorParameterAsync` (~3394, class-name resolution + first-constructor selection) — tool
  wrapper `AddConstructorParameter`. This one has a *second* disambiguation dimension (which
  constructor, if a class has 2+) beyond class-name — the contextSnippet here likely needs to
  identify the specific constructor (e.g. its parameter list), not just the class. Design this one
  deliberately rather than mechanically copying Task C/D's shape.
- `ModifyEnumAsync` (~2281, enum-name resolution) — tool wrapper `ModifyEnum`. Enum name collisions
  are rare (survey's own assessment); still apply the same additive pattern for consistency, smaller/
  faster test is fine here.

Apply the same additive `contextSnippet`/`lineBefore`/`lineAfter` pattern, reusing whichever Task B
helper (member-level or type-level) fits each call site.

**Test:** one collision-scenario test per tool (2 candidates, contextSnippet correctly picks the
non-first one, unrelated candidate is provably untouched). Build, test, diff, commit. Consider
splitting into 2 commits (attributes/modifiers vs. add-member/constructor/enum) if the diff gets
unwieldy.

### Task F — Migrate SafeDeleteUnusedSymbol to handle-based resolution
**Problem:** `SafeDeleteUnusedSymbol` (`StructuralRefinementEngine.cs` `SafeDeleteSymbolAsync` ~line
72) takes `line`/`column` directly, then does `root.FindNode(new TextSpan(position, 0))` followed by
`semanticModel.GetDeclaredSymbol(node)` — i.e. its *actual* target is a declared `ISymbol`, not a
raw text position; the line/column is just today's way of pointing at it. Unlike `WrapRange`/
`InvertAssignments` (Task G), which target an arbitrary statement range with no associated symbol,
`SafeDeleteUnusedSymbol` targets exactly the kind of thing `LocateSymbol` already resolves
precisely — so a `contextSnippet` guess is a worse fit here than reusing the handle-based resolution
this codebase already has, proven out by `RenameSymbol` (`SentinelRefactoringTools.cs` ~line 210).

**Fix:** add `docCommentId`+`projectName`+`sessionId` as the **preferred** resolution path, mirroring
`RenameSymbol`'s exact pattern:
```csharp
SymbolResolution resolution = await _workspaceManager.ResolveFromWireAsync(
    sessionId, projectName, docCommentId, cancellationToken);
```
(`PersistentWorkspaceManager.ResolveFromWireAsync`, ~line 1494 — already handles stale-session and
symbol-no-longer-resolves errors cleanly; reuse those error paths verbatim, don't reinvent them.)
This is inherently unambiguous — a `docCommentId` encodes the full signature, so two overloads of
`Add` get two distinct `docCommentId`s; there is no first-match problem to solve here at all, the
mechanism sidesteps the whole class of defect rather than disambiguating within it.

Add a **secondary** path for callers who haven't called `LocateSymbol` first: `filepath` +
`symbolName` + `contextSnippet` (+ optional `lineBefore`/`lineAfter`), resolved via
`ContextHelper.FindSymbolAtSnippetAsync` (already exists, used elsewhere — direct fit). This mirrors
`PreviewRenameImpact`'s existing dual-path design (`docCommentId`+`projectName` OR
`filepath`+`symbolName`+`contextSnippet`, `SentinelSymbolTools.cs` ~line 349-350) — don't invent a
third shape when this tool already has a two-path precedent to copy.

Keep `line`+`column` as a **third, legacy path** for this task (don't remove it — that would be a
breaking change outside this plan's additive scope per Assumptions), but make exactly one of the
three paths required: `docCommentId`+`projectName`, OR `symbolName`+`contextSnippet`, OR
`line`+`column`. Reject any call supplying parameters from more than one path with a clear "supply
exactly one of..." error — same reasoning as Task G's XOR requirement for range tools, applied here
to three alternatives instead of two.

**Test:** `docCommentId` path resolves and deletes the correct overload when 2 overloads exist (this
is the test that actually proves the value of this task — a `line`/`column` or `contextSnippet`
fixture with 2 overloads can only prove "picked *a* correct one," while a `docCommentId` obtained
from a real prior `LocateSymbol` call proves precise, non-guessing resolution). `symbolName`+
`contextSnippet` path succeeds identically to the existing `line`/`column` path on a single-candidate
fixture. `line`/`column` path is unchanged (regression check against a pre-migration snapshot).
Supplying parameters from 2+ paths returns the "supply exactly one" error. A stale `docCommentId`
(from a symbol since renamed/removed) returns `ResolveFromWireAsync`'s existing
`SymbolNotResolved`/`StaleSession` error, not a crash. Build, test, diff, commit.

### Task G — Migrate range tools (WrapRange, InvertAssignments) to contextSnippet
**Problem:** `WrapRange`'s 3 actions (`SentinelAdvancedRefactoringTools.cs` ~line 849) and
`InvertAssignments` (`MappingEngine.cs` `InvertAssignmentsAsync` ~line 98) take `startLine`/
`endLine` directly. Unlike Task F's `SafeDeleteUnusedSymbol`, these target an arbitrary *span of
statements* with no associated declared symbol and no `docCommentId` — `LocateSymbol`'s
handle-based resolution doesn't apply here, `contextSnippet` genuinely is the right replacement.
Coordinates are inherently unambiguous, but the user has asked to replace them regardless —
requiring an agent to compute exact line numbers is its own reliability problem (miscounted lines),
independent of the name-collision defect this plan otherwise addresses.

**Fix:** follow `ExtractMethodSafe`'s actual precedent (`MsToolAugmentEngine.ExtractMethodSafeAsync`
— see Key Files) — a single `contextSnippet` spanning the whole target range (the exact multi-line
text to wrap/invert), optionally + `lineBefore`/`lineAfter` if that text recurs elsewhere. This
replaces `startLine`+`endLine` as an alternative (XOR — exactly one of the two shapes, not both, not
neither), not as an addition alongside them.

**Test:** `WrapRange` (pick 1-2 of its 3 actions to test thoroughly, not all 3 exhaustively) with a
contextSnippet spanning 2 statements wraps exactly those statements, matching the equivalent
startLine/endLine call's output byte-for-byte on the same fixture. Supplying both startLine/endLine
AND contextSnippet returns a clear "supply exactly one" error; neither, same. A stale/typo'd
contextSnippet returns an error carrying a hint. Build, test, diff, commit.

### Task H — Description sweep
**Files:** every tool wrapper touched by Tasks C–G.

Update each `[Description(...)]` to state: the new `contextSnippet` (+ optional `lineBefore`/
`lineAfter`) parameter exists and disambiguates when the name/type/coordinate alone matches more
than one candidate; omitting it preserves today's first-match behavior; for Task F's
`SafeDeleteUnusedSymbol`, document all three paths (handle, snippet+name, legacy line/column) and
that exactly one must be supplied; for Task G's range tools, state plainly that
line/column-or-contextSnippet is XOR, not both.

**Test:** none needed beyond a read-through — description-only change, should be a no-op build.
Commit.

### Task I — Evaluate the 3 hint strategies and pick one
**This is the evaluation task, deliberately last** (see Background) — by this point every tool in
Tasks C–G is migrated and has its own test fixtures already written, giving this task real, varied
material instead of synthetic one-liners.

Using the test fixtures already built in Tasks C–E and G (2-overload collisions, 3+-candidate
ambiguity, type-level collisions, the constructor-selection case from Task E, the
line/column-to-snippet range cases from Task G), run each scenario with each of the 3
`ActiveHintStrategy` values (flip the constant and rebuild, or use the internal test seam from
Task B if one was added) and record the actual hint text produced by each. Separately, exercise Task
F's secondary (`symbolName`+`contextSnippet`) and legacy (`line`/`column`) paths against each
strategy too — its preferred `docCommentId` path has no ambiguity to hint about (see Task F), but
its fallback paths still go through the same hint machinery as everything else.

Judge each strategy's output against the actual goal: **would an agent reading this hint alone (no
re-read of the file) be able to construct a corrected tool call that succeeds on the next try?**
Judge separately per resolution shape — a strategy that's great for member-name collisions might be
worse for the range-snippet cases from Task G; it's fine if the answer differs by shape.

**Deliverable:** a short section added directly to this plan file, under this task, as an addendum —
don't create a separate document for a decision this scoped. Record: which of Tasks C–G's fixtures
were used, each strategy's actual output per fixture, and the chosen strategy (or per-failure-mode
hybrid, e.g. `NearMissList` for ambiguity + `NearestSnippet` for not-found) with a one-paragraph
justification.

Once decided, set `ActiveHintStrategy` to the winner (or replace the enum-switch with direct
per-failure-mode calls if a hybrid was chosen) and **delete the other 1-2 strategies' dead code** —
this is the one point in the whole plan where the switch actually collapses. Update every test
written in Tasks C–G that asserted only "a hint is present" (per the Risks note) to assert the
specific expected hint shape now that there's a real answer.

**Test:** re-run the full suite after the collapse — this should be close to a no-op for
correctness (behavior for `contextSnippet == null` never touched the hint path at all), but confirm
every ambiguity-path test still passes with the now-fixed strategy. Build, test, diff, commit.

#### Addendum (2026-08-19) — Task I actually executed

A prior pass at this task (commit `fa6e5e5`, "Task I: Evaluate and finalize hint strategy
selection") set `ActiveHintStrategy` to `NearestSnippet` and left an inline code comment claiming
an evaluation had been done, but recorded no addendum here as this section requires, and explicitly
left the other 2 strategies' dead code in place "for future evaluation if needed" — directly against
this task's own closing instruction to delete the losing strategies once a winner is picked. This
addendum replaces that prior attempt with an actual evaluation and completes the collapse.

**Fixture-availability check (first finding):** the plan's premise — that Tasks C–G would leave
behind a rich set of real ambiguity fixtures to evaluate against — did not hold. A repo-wide search
found exactly **one** test exercising the 2+-candidate path of `ResolveMemberByNameOrSnippet`/
`ResolveTypeByNameOrSnippet` (`ReplaceMember_OverloadedMembers_StillRequireContextSnippetToDisambiguate`,
`RoslynSentinel.Tests.Advanced/DeepFunctionalVerificationTests.cs`), and it asserted only on
`Outcome`/`UpdatedText`, never on `Message` — so it provided a fixture shape but no evaluation
signal on hint *text*. No fixture anywhere exercised 3+-candidate ambiguity, type-level collisions,
or an ambiguous snippet that matches 2+ real candidates (as opposed to 0). Rather than declare the
evaluation blocked, 4 minimal fixtures were added directly against `RefactoringEngine` (no new test
seam/`InternalsVisibleTo` — flipping the `ActiveHintStrategy` const and rebuilding 3× was cheap
enough not to justify that infrastructure, especially since it becomes dead weight the moment a
winner is chosen) to cover the shapes the plan actually asked Task I to compare:

1. `Probe_TwoOverloads_NotFound` — 2 overloads, `contextSnippet` matches neither (0 matches).
2. `Probe_ThreeOverloads_NotFound` — 3 overloads, `contextSnippet` matches none (0 matches).
3. `Probe_TwoOverloads_AmbiguousSnippetMatchesBoth` — 2 overloads, both carrying a shared marker
   comment, `contextSnippet` set to that marker so it genuinely matches both bodies (2 matches,
   the "ambiguous" branch, not "not found").
4. `Probe_TypeLevel_TwoNestedTypesSameName_NotFound` — 2 nested types named `Nested` in sibling
   outer classes (a genuinely compilable collision, per Task D's own test guidance), exercising
   `ResolveTypeByNameOrSnippet` rather than the member-level helper.

Each was run three times (flip `ActiveHintStrategy`, rebuild `RoslynSentinel.Tests.Advanced`, run
filtered by test name, read `result.Message` via `Console.WriteLine`). Actual recorded output:

| Fixture | `NearestSnippet` | `CorrectedCoordinates` | `NearMissList` |
|---|---|---|---|
| 2 overloads, not found | `contextSnippet not found. Nearest candidate: \`public void Foo(int x) { }\` at line 4. Provide a more specific contextSnippet or use lineBefore/lineAfter.` | `contextSnippet not found. Try correcting the contextSnippet or use line 4, column 5.` | `contextSnippet not found (2 candidates): line 4 \`public void Foo(int x) { }\`, line 5 \`public void Foo(string x) { }\`. Provide a more specific contextSnippet or use lineBefore/lineAfter.` |
| 3 overloads, not found | same shape, still only shows line 4 (`Foo(int x)`) | same shape, still only line 4/column 5 | lists all 3: line 4/5/6 with each signature |
| 2 overloads, snippet matches **both** (genuine ambiguity) | `contextSnippet ambiguous. Nearest candidate: \`public void Foo(int x) { }\` at line 4. ...` — **identical shape to the not-found case**, gives no indication 2 real matches were found | same coordinate-only shape, same blindness to the 2 real matches | `contextSnippet ambiguous (2 candidates): line 4 ..., line 5 .... Provide...` — shows both actual matches |
| Type-level, 2 nested `Nested`, not found | Nearest candidate `public class Nested { public int A; }` at line 4 only | line 4, column 5 only | both: line 4 `... int A ...`, line 8 `... int B ...` |

**Judgment against the plan's actual test** ("would an agent reading this hint alone construct a
corrected call that succeeds next try?"):
- **`CorrectedCoordinates` loses outright.** Its line/column pair isn't even valid input to any of
  these tools' own parameter surface (`ReplaceMember`/`ChangeAccessibility`/etc. take `memberName`
  + `contextSnippet`, never `line`/`column`) — an agent would have to go read the file at that
  position and manually derive a snippet anyway, which is exactly the re-read the plan's evaluation
  question says a good hint should avoid.
- **`NearestSnippet` is actively misleading on genuine ambiguity.** In the 2-overloads/snippet-
  matches-both fixture, its output is byte-for-byte the same *shape* as a plain not-found failure —
  an agent has no way to tell "your snippet hit 2 real targets, pick one" apart from "your snippet
  hit nothing, here's the nearest thing." It also never shows more than 1 candidate regardless of
  how many exist (3-overload and 2-nested-type cases both only ever surface candidate #1), so an
  agent can't even discover that a second/third option exists to disambiguate toward.
- **`NearMissList` wins.** It's the only strategy where every recorded output gives an agent (a)
  confirmation of exactly how many real candidates exist, (b) each one's line number and enough of
  its declaration text to write a next `contextSnippet` that's unique to it (e.g. `"Foo(string x)"`
  from the line-5 preview), and (c) this holds identically for both the member-level and type-level
  helper. On the "matches both" ambiguous case specifically, it's the only strategy that reveals
  the ambiguity is real (2 candidates found) rather than reading like an ordinary not-found miss.

**Decision:** `NearMissList` is the winning strategy, selected outright (no hybrid needed — it
dominated on every fixture tested, including the "ambiguous, not just not-found" case the other two
strategies handle worst). `ActiveHintStrategy` const and the `HintStrategy` enum/switch were removed
entirely; `BuildMemberHint`/`BuildTypeHint` now call the (renamed-in-place, formerly
`BuildNearMissListMemberHint`/`BuildNearMissListTypeHint`) preview-list logic directly. The other 2
strategies' `Build*Hint` methods (6 total: near­est-snippet ×2, corrected-coordinates ×2 — the
NearMissList member/type pair was kept) were deleted, per this task's own closing instruction.

Task F's secondary (`symbolName`+`contextSnippet`) and legacy (`line`/`column`) paths were also
checked: `SafeDeleteUnusedSymbol` resolves via `ContextHelper.FindSymbolAtSnippetAsync` directly
(not through `ResolveMemberByNameOrSnippet`), so it was never wired to `HintStrategy` in the first
place and needed no change here — its failure messages are hand-authored independently of this
switch (see `StructuralRefinementEngine.SafeDeleteSymbolAsync`).

`ReplaceMember_OverloadedMembers_StillRequireContextSnippetToDisambiguate`'s previously
shape-only assertion (`Outcome == EditOutcome.CannotEdit`, no message check) was tightened to assert
the specific `NearMissList` text, and 2 new tests
(`ReplaceMember_ThreeOverloads_AmbiguousSnippetListsUpToThreeCandidates`,
`AddBaseType_TwoNestedTypesSameName_AmbiguousSnippetListsBothCandidates`) were added permanently to
`DeepFunctionalVerificationTests.cs` to keep the 3-candidate and type-level shapes under regression
coverage going forward (the 4 probe fixtures used only for live strategy comparison were temporary
and removed after recording the table above).

### Task J — Full verification and summary
1. Full solution build (`RoslynSentinel.Common`, `RoslynSentinel.Basic`, `RoslynSentinel.Server.Basic`,
   `RoslynSentinel.Server.Advanced`, both `.Http` projects, `RoslynSentinel.Tests`), 0 errors.
2. Full `RoslynSentinel.Tests` run; regenerate the failing-test list and diff against
   `docs/known-failing-tests.txt`. Any new failure is a regression — fix before continuing.
3. Spot-check the "partial escape hatch" tools listed as out of scope (`ApplyMethodCodemod`,
   `ChangeSignature`, `PullUpMember`, etc.) and the already-hardened `FindReferences`/
   `QuerySymbolRelationships` still pass their existing tests unchanged — confirms Task A's addition
   to `ContextHelper` had zero ripple effect on its existing methods.
4. Optional live smoke test: kill the running server process if one is up, rebuild Release, ask the
   user to restart VS Code, exercise 2-3 of the migrated tools live against a file with a real
   overload collision, and deliberately trigger one ambiguous/stale-snippet case to confirm the
   hint text actually appears in the live MCP response, not just in the unit test harness.
5. Output a summary table: task, files touched, commit hash, build/test status — same format as
   `plan-symbol-tool-hardening-v1.md`'s closing summary.
6. In the summary, explicitly flag the deferred deprecation-tracking decision from Risks (whether/
   when `contextSnippet` should become required, or whether first-match-without-it should emit a
   warning) as a recommendation for the user to decide, not something this plan resolved.

#### Addendum (2026-08-19) — Task J executed for the Task I re-do + raw-ContextHelper follow-on

Tasks A–H had already landed in earlier commits (`84fb911` through `d6acc48`/`fa6e5e5` and later).
This pass's scope was: finish Task I properly (see its addendum above) and extend equivalent
candidate-aware error reporting to tools that resolve via raw `ContextHelper` calls rather than
`ResolveMemberByNameOrSnippet`/`ResolveTypeByNameOrSnippet` (outside this plan's original Task
list, but requested alongside this Task I re-do): `RefactoringEngine.ExtractLocalVariableAsync` and
`SymbolNavigationEngine.FindCallersAsync`/`FindImplementationsForMemberAsync`.

1. **Full solution build:** 0 errors (184 pre-existing warnings, unchanged in kind/count from
   before this session's changes — all in test projects/unrelated files).
2. **Full 5-project test run**, diffed against `docs/known-failing-tests.txt` by exact test name
   (not just aggregate counts, since that file predates the `RoslynSentinel.Tests.Basic`/
   `.Advanced` project split and mixes all projects together):

   | Project | Result | Baseline | Regressions |
   |---|---|---|---|
   | `RoslynSentinel.Tests` | 199 passed / 0 failed | 199/0 | none |
   | `RoslynSentinel.Tests.Basic` | 172 passed / 10 failed | 172/10 | none — all 10 confirmed present in known-failing-tests.txt |
   | `RoslynSentinel.Tests.Advanced` | 792 passed / 26 failed / 1 skipped | 790/26/1 | none — the +2 passed are this session's new tests; all 26 failures confirmed present in known-failing-tests.txt |
   | `RoslynSentinel.Tests.Battery` | 777 passed / 47 failed / 91 skipped | 777/47/91 | none, exact match |
   | `RoslynSentinel.Tests.Asyncify` | 95 passed / 2 failed | 95/2 | none, exact match |

3. **Partial-escape-hatch spot-check:** `ChangeSignature`, `PullUpMember`, `MoveTypeToFile`,
   `InlineMethod`/`InlineField`, `IntroduceParameterObject` tests, and `FindReferences`-adjacent
   tests (`ContextHelper_FindSnippetPosition_*`) all pass in the `.Advanced` run above — confirms
   zero ripple from this session's `ContextHelper`-adjacent message changes onto tools this plan
   deliberately left alone.
4. **Live smoke test:** not performed. No RoslynSentinel MCP server process was found running
   (`tasklist //FI "IMAGENAME eq RoslynSentinel*"` returned no matches), so there was nothing to
   kill/restart, and restarting VS Code to exercise the tools live is a user action outside this
   session's scope — flagging as still-optional/deferred, not blocking.
5. **Summary table:**

   | Task | Files touched | Status |
   |---|---|---|
   | Task I (re-done) | `RoslynSentinel.Basic/RefactoringEngine.cs` (collapsed `HintStrategy` enum/switch/const, deleted `BuildNearestSnippet*`/`BuildCorrectedCoordinates*` ×4 methods), `RoslynSentinel.Tests.Advanced/DeepFunctionalVerificationTests.cs` (tightened 1 loose assertion, added 2 new tests) | Done, build clean, tests green |
   | Raw-`ContextHelper` follow-on (not a numbered task in the original plan) | `RoslynSentinel.Basic/RefactoringEngine.cs` (`ExtractLocalVariableAsync`'s 2 failure messages), `RoslynSentinel.Basic/SymbolNavigationEngine.cs` (`FindCallersAsync`/`FindImplementationsForMemberAsync`: fixed `"FindReferences:"` → `"FindCallers:"`/`"FindImplementations:"` prefix bug, added `DescribeNameOnlyCandidates` helper for the contextSnippet-not-resolved case) | Done, build clean, tests green |
   | Docs | `docs/TODO.md`, `Samples/ContosoOrders/SCENARIOS.md`, this plan file | Done |

6. **Deferred deprecation-tracking recommendation (repeating Risks, not resolved here):** whether
   `contextSnippet` should eventually become required on these tools, or whether silently
   first-matching-by-name without one should emit a non-fatal warning, remains an open product
   decision for the user — this plan (and this session) deliberately kept every path additive.
   Separately, worth deciding: should `NearMissList`'s "+N more" cap of 3 candidates be raised for
   pathological cases (5+ same-named overloads)? No fixture with more than 3 real candidates was
   observed in this codebase's test suite, so this was left at the plan's originally-specified cap
   rather than speculatively widened.
