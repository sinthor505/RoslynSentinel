# Plan — Symbol Query & Formatting Tool Hardening

## Title
Harden `SearchSolutionText`/`FindUsages`/`FindReferences`/`RemoveMember` and stop whole-file
reformatting on write, based on defects surfaced while grading a weaker agent's run of
`Samples/ContosoOrders/docs/plans/PLAN.md`.

## Background
A separate agent (qwen3.5-9b-coder) ran the ContosoOrders cleanup plan against these MCP tools.
Grading its transcript surfaced two real, reproducible defects in the tool layer itself (not just
agent error):

1. Several write-back paths call `NormalizeWhitespace()` on the **entire file root** after a
   single-node edit, which silently reformats unrelated code (collapses blank lines between
   untouched members) and shifts every line number below the edit point. The agent had cached a
   line number from an earlier tool response; a later edit reformatted the file out from under it,
   and it spent ~8 tool calls chasing the wrong method (`MarkShipped` instead of `ApplyDiscount`)
   before self-correcting. No corruption resulted only because the write-validation pipeline
   happened to catch the bad edit it produced along the way.
2. `FindUsages(searchKind: "objectCreations")` was used to check whether a **method** had zero
   callers. That searchKind only matches `new TypeName(...)` expressions — for a method name it is
   *structurally incapable* of returning anything but `[]`, so the "confirmation" was vacuous. It
   happened to be right (the method really was unused, confirmed independently), but the check
   performed didn't establish that.

This plan fixes both, plus four smaller, related hardening items agreed on while discussing the
grading. See `docs/known-failing-tests.txt` for the pre-existing, unrelated test-suite baseline —
diff against it, don't hand-review all failures.

## Assumptions
- You are working directly in this repo with normal file-editing tools (Read/Edit/Bash), not by
  calling the RoslynSentinel MCP tools on their own source. This plan is about editing
  `RoslynSentinel.Basic`/`RoslynSentinel.Server.Basic` engine/tool code directly.
- No live MCP session needs to stay connected while you make these changes — unlike the
  ContosoOrders scenario, these changes are verified by the existing `RoslynSentinel.Tests` suite,
  not by round-tripping through a live server. You only need to kill/rebuild/ask-for-a-VS-Code-
  restart **once at the end**, and only if you want to smoke-test live — not once per task.
- Line/column references below were correct as of this plan's writing but **will have drifted** —
  this whole plan exists because line numbers are not stable across edits. Re-locate every target
  with Grep before editing; treat every line number here as a starting hint, not ground truth.
- Build and test per task (0 errors before moving on), matching this project's standing convention.
  After each task, diff the failing-test list against `docs/known-failing-tests.txt` rather than
  eyeballing all ~75 pre-existing failures.
- Commit each task separately with a focused message; do not bundle unrelated tasks into one commit.

## Approach
Order matters for tasks 5–8 (renaming and extending the symbol-query tools) but not for 1–4, which
are independent. Suggested order:

1. Task A — stop whole-file reformatting on write (independent, do first — it's the highest-leverage
   fix and has no dependency on anything else).
2. Task B — `SearchSolutionText` enclosing-member field (independent).
3. Task C — expose workspace version on read *and* write tools (independent).
4. Task D — rename `FindUsages` → `QuerySymbolRelationships` (do before E, since E edits the
   renamed tool).
5. Task E — broaden-on-empty fallback + semantic-mismatch guard on `QuerySymbolRelationships`; add
   an `all` kind to `FindReferences` (depends on D).
6. Task F — `RemoveMember` precheck via `FindReferences(kind: all)` (depends on E for the `all` kind).
7. Task G — update `RemoveMember`'s description to reflect both its unconditional nature and the new
   precheck default (do last, once F's actual behavior is final).

## Key Files
- `RoslynSentinel.Basic/RefactoringEngine.cs` — most of the `NormalizeWhitespace()` call sites (Task A)
  and `RemoveMemberAsync` (Task F/G).
- `RoslynSentinel.Server.Basic/SentinelSymbolTools.cs` — `FindUsages`/`FindReferences` tool wrappers
  (Tasks D, E).
- `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs` — `SearchSolutionText`, `RemoveMember`
  tool wrapper (Tasks B, F, G).
- `RoslynSentinel.Basic/DiscoveryEngine.cs` — `FindObjectCreationSitesAsync` and friends, backing
  `QuerySymbolRelationships` (Task E).
- `docs/known-failing-tests.txt` — pre-existing failure baseline; diff against this after every task.

## Risks & Open Questions
- Task A's mechanical fix (format only the changed node, not the whole root) needs a real
  `Formatter.Format(root, annotation, workspace, ...)` pass with a `SyntaxAnnotation` tracking the
  new node — not just moving `.NormalizeWhitespace()` before `ReplaceNode`. Calling
  `NormalizeWhitespace()` on a *detached* node before it's reattached computes indentation as if the
  node were at nesting depth 0, which will often be wrong once it's actually inside a class/method
  body. Get this right or you'll trade one formatting bug for another.
- Task A is scoped to the 6 call sites listed below (the ones actually exercised by the ContosoOrders
  scenario), not all ~94 occurrences of `NormalizeWhitespace()` across the engine layer. Note in the
  commit message that this is a partial fix and the same pattern exists elsewhere, so it isn't
  mistaken for a complete sweep.
- Task F changes `RemoveMember`'s default behavior (adds a refusal path it didn't have before). Any
  existing test that calls `RemoveMember` expecting an unconditional removal of a member that *does*
  have references will need `skipPrecheck: true` added, or it'll start failing for the right reason.
  Check `RoslynSentinel.Tests` for existing `RemoveMember` tests before starting Task F.
- Explicitly **out of scope** for this plan (raised during discussion, deliberately deferred):
  - Sweeping all ~94 `NormalizeWhitespace()` call sites, not just the 6 in Task A.
  - Making `QuerySymbolRelationships` default to running all 6 sub-queries on every call (rejected —
    too expensive and too noisy; broaden-on-empty gets most of the benefit far more cheaply).
  - Consolidating `RemoveMember` and `SafeDeleteUnusedSymbol` into one tool. Task F makes them
    behaviorally converge (both now gate on usages by default), but merging the two tool surfaces is
    a separate decision — don't do it as part of this plan.

## Steps

### Task A — Stop whole-file reformatting on write
**Problem:** `root.ReplaceNode(target, newNode).NormalizeWhitespace()` reformats the *entire* file,
not just the edited node, silently shifting every line number below the edit and rewriting
untouched whitespace (e.g. collapsing blank lines between one-line properties).

**Fix:** add a small shared helper in `RefactoringEngine.cs`, e.g.:
```csharp
private static SyntaxNode ReplaceNodeFormatted(SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode)
{
    var annotation = new SyntaxAnnotation();
    var annotated = newNode.WithAdditionalAnnotations(annotation);
    var newRoot = root.ReplaceNode(oldNode, annotated);
    using var workspace = new AdhocWorkspace();
    var target = newRoot.GetAnnotatedNodes(annotation).Single();
    return Formatter.Format(newRoot, annotation, workspace);
}
```
(Adjust for the `RemoveNode`/`AddUsings` cases below, which don't fit the replace-one-node shape
exactly — `RemoveMemberAsync` removes a node rather than replacing one, and `AddUsingDirectiveAsync`
adds one; each needs the annotation applied to the node that's actually new/changed, or — for
removal — format the *remaining* container node instead of the whole root.)

Apply this to the 6 call sites actually exercised by the ContosoOrders scenario (re-locate each with
Grep first — line numbers below are approximate and will have drifted):
- `ChangeAccessibilityAsync` — `RefactoringEngine.cs` ~line 2955: `root.ReplaceNode(target, target.WithModifiers(newModifiers)).NormalizeWhitespace()`
- `AddUsingDirectiveAsync` — `RefactoringEngine.cs` ~line 2220-2225: `root.AddUsings(newUsing)` then `newRoot.NormalizeWhitespace()`
- `ModifyEnumAsync` — `RefactoringEngine.cs` ~line 2391: `root!.ReplaceNode(enumNode, newEnumNode).NormalizeWhitespace()`
- `ReplaceMemberAsync` (backing `ReplaceMember`) — `RefactoringEngine.cs` ~line 1215: `root!.ReplaceNode(member, newMember).NormalizeWhitespace()`
- `AddMemberAsync` (backing `AddMember`) — `RefactoringEngine.cs` ~line 1269: `root!.ReplaceNode(container, newContainer).NormalizeWhitespace()`
- `RemoveMemberAsync` (backing `RemoveMember`) — `RefactoringEngine.cs` ~line 1327: `root!.RemoveNode(member, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace()`

**Test:** write or extend a test that asserts an edit via one of the above (e.g. `ChangeAccessibility`
on a file with blank lines between unrelated one-line properties elsewhere) leaves those unrelated
blank lines and line numbers untouched. Build, run the full suite, diff against
`docs/known-failing-tests.txt`, commit.

### Task B — Add enclosing member to `SearchSolutionText`
**File:** `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs` (tool wrapper) and whatever engine
method backs it (Grep for `SearchSolutionText`/its implementation — likely in `DiscoveryEngine.cs` or
similar).

Each match result currently returns file path, line, column, and a text preview. Add an
`enclosingMember` (or similarly named) field: walk up from the matched position's `SyntaxNode` via
`AncestorsAndSelf()` to the nearest `MethodDeclarationSyntax`/`PropertyDeclarationSyntax`/
`ConstructorDeclarationSyntax`/etc., and report its name (null if the match isn't inside any member,
e.g. a using directive or namespace-level comment).

**Test:** a search hit inside a known method returns that method's name in `enclosingMember`; a hit
outside any member (e.g. in a `using` line) returns null without erroring. Build, test, diff, commit.

### Task C — Expose workspace version on read and write tools
**Why both:** a version number is only useful as a diff. Emitting it only on mutating tools gives an
agent nothing to compare a previously-fetched line number against. `PersistentWorkspaceManager`
already tracks `_workspaceVersion` (incremented on every successful in-memory update) — it just isn't
surfaced anywhere per-call.

Add a `workspaceVersion` (or similar) field, sourced from `PersistentWorkspaceManager`'s existing
version counter, to the responses of:
- **Read/location-bearing tools:** `GetFileOutline`, `SearchSolutionText`, `GetMethodSource`,
  `LocateSymbol`.
- **Mutating tools:** the ones touched in Task A at minimum (`ChangeAccessibility`, `AddUsingDirective`,
  `ModifyEnum`, `ReplaceMember`, `AddMember`, `RemoveMember`) — ideally all `autoStage`-capable tools,
  but scope to at least these if time-boxing.

You likely need a public accessor on `PersistentWorkspaceManager` for the current version if one
doesn't already exist (check for a `_workspaceVersion` field and whether it's already exposed via a
property before adding a duplicate).

**Test:** a read tool's response version matches the value after a prior mutation increments it, and
differs before/after a mutation in a sequential test. Build, test, diff, commit.

### Task D — Rename `FindUsages` → `QuerySymbolRelationships`
**File:** `RoslynSentinel.Server.Basic/SentinelSymbolTools.cs`, ~line 187.

Rename the C# method and the `[McpServerTool(Name = "...")]` override (if the name differs from the
method name — check current convention; per `docs/plan-tool-rename-v1.md`'s established pattern, tool
name should equal the method name, no explicit `Name=` override needed unless there's a reason).
Update the `[Description(...)]` to reflect the new name. Grep the whole repo for `"FindUsages"` /
`find_usages` afterward (tests, docs, other prompts) and update any that reference it by name —
this is a breaking rename for anything hardcoding the old tool name.

**Test:** existing `FindUsages`-named tests still pass under the new method name (rename in test code
too). Build, test, diff, commit.

### Task E — Broaden-on-empty + semantic guard (`QuerySymbolRelationships`), `all` kind (`FindReferences`)
**`QuerySymbolRelationships`** (`SentinelSymbolTools.cs`, the renamed method from Task D):
1. **Semantic-mismatch guard.** Before dispatching to `objectCreations` specifically (the case that
   caused the original defect — its backing `FindObjectCreationSitesAsync(string typeName, ...)` in
   `DiscoveryEngine.cs` ~line 207 can only ever match `new TypeName(...)` sites), resolve `name` via
   the existing symbol-lookup machinery (`LocateSymbol`'s underlying resolution, or equivalent) and
   check its `SymbolKind` first. If it resolves to a method/property/field (not a type), return a
   clear error suggesting `FindReferences(kind: callers)` instead of running a query that is
   structurally guaranteed to return `[]`. This is the fix that would have prevented the original
   defect outright — it's higher priority than the broaden-on-empty fallback below.
2. **Broaden-on-empty fallback.** If the requested `searchKind` genuinely runs and returns `[]` (and
   the semantic guard above didn't already reject it), automatically run the other 5 `searchKind`
   values and return them, clearly labeled by kind, with a message like: `"0 results for
   'objectCreations'. Broadened search across all relationship kinds — found 2 result(s) under
   'attributeUsages': [...]"`. If all 6 are empty, say so plainly (this is a real, trustworthy
   "nothing found under any relationship kind" signal — do not treat it as an error). **Do not**
   make this the default/always-run-all path — only trigger it when the targeted query returns empty,
   to avoid the cost of 6 solution-wide scans on every call.
3. **Cross-reference hint.** Update the `[Description(...)]` to mention `FindReferences` for
   call-site/override queries, so a model reasoning about which tool to use sees the pointer before
   ever making a call.

**`FindReferences`** (`SentinelSymbolTools.cs`, ~line 346):
1. Add an `all` value to `FindReferencesKind` (alongside `callers`, `implementations`) that runs both
   `FindCallersAsync` and `FindImplementationsForMemberAsync` and returns both, clearly labeled.
   Consider making `all` the default when `kind` is omitted (currently required — check whether making
   it optional-with-default is a bigger change than intended; if `kind` must stay required, ensure
   `all` is at least a valid, well-tested option since Task F depends on it).
2. Update the `[Description(...)]` with a cross-reference to `QuerySymbolRelationships` for
   type-relationship queries (implementors, object creations, attribute usage), mirroring the hint
   added on the other tool.

**Test:** a targeted `objectCreations` query against a method name returns the semantic-guard error,
not `[]`. A targeted query against a real type/kind that happens to have zero matches for that
specific kind but a match under another kind returns the broadened result, clearly labeled. `all` on
`FindReferences` returns both callers and implementations for a symbol that has both. Build, test,
diff, commit.

### Task F — `RemoveMember` precheck
**File:** `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs` (tool wrapper), backing
`RemoveMemberAsync` in `RefactoringEngine.cs` (~line 1327, see Task A).

Add a `skipPrecheck` parameter (default `false`) to `RemoveMember`. When `false` (default), before
removing, call the same logic backing `FindReferences(kind: all)` (from Task E) for the target
member. If it returns any callers or implementations, refuse with a clear message listing what was
found (mirroring `SafeDeleteUnusedSymbol`'s existing "refuse and explain" contract — don't just
return an empty success). When `skipPrecheck: true`, remove unconditionally as today.

Note: this checks *both* callers and implementations deliberately — for an interface/virtual member,
a callers-only check misses "something implements this," which the general compile-validation
safety net would catch anyway but only after the fact.

**Test:** removing a member with zero references succeeds as before. Removing a member with a known
caller is refused by default and lists the caller. The same call with `skipPrecheck: true` succeeds
regardless. Check existing `RemoveMember` tests in `RoslynSentinel.Tests` first — any that remove a
member with real references will need `skipPrecheck: true` added or they'll start failing (correctly)
under the new default. Build, test, diff, commit.

### Task G — Update `RemoveMember`'s description
**File:** `RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`, `[Description(...)]` on
`RemoveMember`.

Do this last, once Task F's behavior is final. The description should state plainly: default behavior
now checks for callers/implementations first and refuses if any are found (pointing to
`skipPrecheck: true` for the unconditional path), and cross-reference `SafeDeleteUnusedSymbol` as the
narrower zero-usage-gated alternative if a caller still wants that specific contract.

**Test:** none needed beyond a read-through — description-only change. Build (should be a no-op),
commit.

## Verification
After all seven tasks:
1. Full solution build, 0 errors.
2. Full `RoslynSentinel.Tests` run; diff the failing-test list against `docs/known-failing-tests.txt`.
   Any new failure not in that file is a regression — fix before continuing. Any of the pre-existing
   75 that now *pass* is fine (don't need to update the baseline file unless you want to).
3. Optional live smoke test: kill the running `RoslynSentinel.Server.Basic.exe` process (check
   `tasklist` for its PID first), rebuild Release, ask the user to restart VS Code, then re-run the
   ContosoOrders plan (`Samples/ContosoOrders/docs/plans/PLAN.md`) steps 4 and 5 specifically — those
   are the two steps that surfaced the original defects — and confirm the agent (or you, driving the
   tools directly) no longer hits the `MarkShipped` confusion or the vacuous `objectCreations` check.
4. Output a short summary table: task, files touched, commit hash, build/test status.
