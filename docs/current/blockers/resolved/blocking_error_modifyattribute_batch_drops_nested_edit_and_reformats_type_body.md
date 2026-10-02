# `ModifyAttribute` batch silently drops a member edit when the same file also gets a type edit, and reformats the whole type body

**Status:** FIXED 2026-10-02 (see "Resolution" at the end). Root cause verified in source, including `RoslynFormattingHelper.ReplaceNodesFormattedAsync`.

## What was being attempted

Test-speed work (parallelizing slow NUnit fixtures). One `ModifyAttribute` batch (`edits[]`, `action: add`) with 7 edits across 5 files:

- 5 type-level adds: `[Parallelizable(ParallelScope.All)]` on `GitToolsSmokeTests`, `PreviewInstanceMoveCallSitesTests`, `RunTestTests`, `PathCaseLookupRegressionTests`, `CreateFileDeleteFileTests`.
- 2 method-level adds: `[NonParallelizable]` on `GitToolsSmokeTests.Git_Pull_DivergentBranchesWithNoPullConfig_MergesInsteadOfFailingAsync` and `RunTestTests.RunTest_TrxTempFile_DeletedAfterCallAsync`.

(A first call using `newAttribute` instead of `existingAttribute` was rejected with a clear message; see "Why this matters".)

## The exact symptom

Tool result (success):

```
"description":"Applied 7 attribute edit(s) across 5 file(s).", "status":"applied"
```

`Git(operation: diff)` afterwards showed only the 5 type-level attributes. Neither `[NonParallelizable]` was written. Both dropped edits were in a file that also received a type-level edit in the same batch (`GitToolsSmokeTests.cs`, `RunTestTests.cs`).

Second symptom, same call: `PathCaseLookupRegressionTests.cs` received only a type-level add, yet the diff also re-indented the unrelated `Spell` method (the two `case ... { }` blocks moved four spaces right). Excerpt:

```
-            {
-                var directory = Path.GetDirectoryName(realPath)!;
+                {
+                    var directory = Path.GetDirectoryName(realPath)!;
```

The returned `diff` field (returnDiff: true) also showed the `Spell` hunks but no hunk for the dropped method edits, so the tool's own diff disagreed with its own "Applied 7" message.

## Source trace

- `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs:261` - `ModifyAttributeBatch` builds the success text from `edits.Count` (the number requested), not from what the engine applied. `ApplyAttributeBatchAsync` returns only `UpdatedText` per file; there is no per-edit outcome, so a dropped edit cannot be reported.
- `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` `ApplyAttributeBatchAsync` (starts at line 1529 in the 4779-line file):
  - The `add` branch builds `replacements[targetNode] = <whole target node with attribute list added>`, i.e. for a type edit the replacement is the entire class built from the ORIGINAL class node.
  - The collision check (`seen` dictionary) only rejects two edits resolving to the identical node. It does not detect an ancestor/descendant pair (class + one of its methods).
  - Everything is then passed to `RoslynFormattingHelper.ReplaceNodesFormattedAsync(document, root, replacements, ...)`.
- Verified root cause (read `RoslynSentinel.Common/RoslynFormattingHelper.cs` `ReplaceNodesFormattedAsync`, ~line 115):
  1. Dropped edit: the helper tracks every key with `TrackNodes`, then folds `ReplaceNode(currentOldNode, newNode)` in dictionary order. The class replacement is a node built from the ORIGINAL class, so it carries none of the tracking annotations of the rewritten method inside it. If the method is replaced first, the later class replacement overwrites it with the original method. If the class is replaced first, `GetCurrentNode(methodKey)` returns null and the loop hits `if (currentOldNode == null) { continue; }`, silently skipping the method edit. Either order loses the member edit, with no error and no signal back to the caller.
  2. Reformat: the helper puts one formatting annotation on every replacement node and calls `Formatter.FormatAsync(doc, annotation)`. For a type-level add the annotated node is the whole class, so every member inside it is re-formatted (this is the `Spell` re-indent).
  3. False success: `ModifyAttributeBatch` built "Applied {edits.Count}" from the number requested; the engine returned only `UpdatedText`.
- The single (non-batch) path has the SAME whole-type reformat: `AddAttributeAsync` called `ReplaceNodeFormattedAsync(document, root, targetNode, newNode)` with the whole type as `targetNode`. `ReplaceAttributeAsync` annotates only the replaced attribute and `RemoveAttributeAsync` only the member, so they do not reformat siblings (they do re-format the member itself, same family as the known Member-reformats-content issue).

## What is and is not confirmed

Confirmed: the 2 method edits were not written, the success message claimed 7 applied, and `Spell` was re-indented by a type-level-only edit (all via `git diff`).

Originally unconfirmed, now confirmed by reading the helper (see "Verified root cause" above). A method-only batch does work (no ancestor), but still re-formatted the edited member before the fix.

State left behind: the tree was repaired in the same session with `ReplaceSnippet` (missing attributes added, `Spell` formatting restored). `git diff` now shows exactly 13 added lines across 5 files.

## Why this matters

- False success: a caller (especially a small model) trusts "Applied 7" and moves on with 2 edits missing. Here the missing attributes were safety-critical: `Parallelizable(All)` without `FixtureLifeCycle`/`NonParallelizable` would have introduced racy tests.
- Unrequested reformatting inflates diffs and violates the "tool edits only what was asked" expectation (same family as the known Member-reformats-unrelated-content memory entry).
- Minor: the parameter that carries the attribute to add is named `existingAttribute` for `action: add`, and `newAttribute` is replace-only. A first-try mistake costs a round trip (the error message was clear and cheap, so low priority).

## Suggested direction (not implemented)

1. Report per-edit outcomes: have `ApplyAttributeBatchAsync` return the applied edit indexes and make `ModifyAttributeBatch` assert `applied.Count == edits.Count` (or list the dropped ones) before reporting success.
2. Extend the collision check to ancestor/descendant pairs: either reject ("edits[i] targets a type and edits[j] targets a member inside it; split into separate calls") or, better, apply edits so nested ones compose (insert attribute lists via text spans or a bottom-up `ReplaceNode` fold) instead of replacing whole ancestors.
3. For a type-level add, insert only the attribute list (do not replace and re-format the whole type), or restrict formatting to the changed node's attribute lists, so unrelated members are untouched. Add a regression test that asserts byte-identical output outside the inserted line(s).
4. Same audit for `ApplyModifierBatchAsync` (its doc comment says it shares this execution model).
5. Optionally accept `attribute` as an alias for `existingAttribute` on `action: add` (or rename the parameter in the schema description).

## Resolution (FIXED 2026-10-02, uncommitted at time of writing)

Approach: attribute edits are now minimal text-span edits against the ORIGINAL source text, not "rebuild the target node, then format it".

- New `RoslynSentinel.Engines.Basic/AttributeTextEditBuilder.cs`: builds an insertion (add: new attribute line at the declaration's indentation, after existing lists and any doc comment; inline when the keyword shares a line), an in-place replacement (replace), and a list/line removal or in-place list rewrite (remove). `TryApply` applies all edits in one pass and rejects overlapping spans.
- `MemberRefactoringEngine.ApplyAttributeBatchAsync`: uses the builder. Ancestor/descendant targets (a type plus a member in it) now compose because no ancestor node is rebuilt; the same-node collision rejection is kept. Returns the new `DocumentEditResult.AppliedEditIndexes` (edits that actually changed text; a `remove` of an absent attribute is not listed).
- `MemberRefactoringEngine.AddAttributeAsync` (single edit): inserts only the attribute list via the builder, so a type-level add no longer re-formats the type body.
- `RefactoringStructuralImpl.ModifyAttributeBatch`: the success text is built from the engine-reported applied indexes ("Applied N attribute edit(s) ..." or "Applied N of M ...; no effect: edits[i] (...)"), refuses success when the engine reports no applied-index list, and fails (nothing written) when no edit had any effect.
- `RoslynSentinel.Common/DocumentEditResult.cs`: new `AppliedEditIndexes` property.
- Regression tests in `RoslynSentinel.Tests.Battery.Basic/ModifyAttributeBatchTests.cs` (9 new cases): type+method add in both edit orders; remove-on-method + add-on-type exact output; type-level add (batch and single) and method-level add leave non-canonically formatted members byte-identical; applied-count description for all-applied, partial, and zero-effect batches.

Behavior notes: a batch with zero effective edits (e.g. only a `remove` of an attribute that is absent) now fails instead of reporting "applied"; a partial no-op is reported in the description. Added attribute text is normalized with `NormalizeWhitespace` (e.g. `[Obsolete("x")]`), not passed through the Formatter.

### Audit of `ApplyModifierBatchAsync`

Same execution model, but the ancestor/descendant bug is not reachable there: it resolves only non-type members (type kinds are filtered out of the candidates), and a member never contains another resolvable member, so no key is an ancestor of another. Its formatting scope is the edited member only. Left unchanged.

### Still open (follow-ups, not done here)

- `ApplyBaseTypeBatchAsync` (MemberRefactoringEngine) uses the same `ReplaceNodesFormattedAsync` fold over TYPE nodes; a batch naming a nested type and its container would hit the same silent drop. Needs an ancestor/descendant guard or the same text-span treatment.
- `RoslynFormattingHelper.ReplaceNodesFormattedAsync` still silently `continue`s when a tracked old node cannot be located; it should report the skipped replacement (or throw an actionable error) so no caller can silently lose an edit.
- Suggested direction item 5 (accept `attribute` as an alias for `existingAttribute` on `action: add`) is not done.
- Single-edit `RemoveAttributeAsync` / `ReplaceAttributeAsync` were not moved to the text-edit builder (they do not re-format siblings; only the edited member / attribute).
