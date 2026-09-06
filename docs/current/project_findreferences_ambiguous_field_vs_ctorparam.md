---
name: project_findreferences_ambiguous_field_vs_ctorparam
description: "FindReferences with contextSnippet resolved to ctor parameter instead of same-named private field — FIXED"
metadata: 
  node_type: memory
  type: project
  originSessionId: d912bb80-d14d-41b4-b453-e7a77fd07e20
  modified: 2026-09-06T03:09:59.912Z
---

**FIXED (2026-09-05):** root cause was `ContextHelper.FindSymbolAtSnippetAsync` walking
`node.AncestorsAndSelf()` for the first declared symbol instead of checking the node itself —
for a reference-site snippet (an assignment line), that walk climbed past the reference and
returned the enclosing constructor's declared symbol. A second bug in `FindCallersAsync`'s
no-contextSnippet branch called `GetDeclaredSymbol` directly on a `FieldDeclarationSyntax` (always
null; must use the `VariableDeclaratorSyntax` child), causing Call 2's false "not found declared"
error. Both fixed in `ContextHelper.cs`/`SymbolNavigationEngine.cs`; regression tests added to
`RegressionTests.cs`. See `docs/obsolete/blockers/blocking_error_findreferences_field_contextsnippet_resolves_to_ctorparam.md`
for full root-cause writeup. FindReferences/FindCallers with `filepath`+`contextSnippet` on a
ctor-shadowed field name can now be trusted again.

While auditing engine field call-sites for [[project_di_engine_audit_open_question]], tried
`FindReferences(symbolName: "_dependencyEngine", kind: callers, contextSnippet: "_dependencyEngine
= dependencyEngine;", filepath: SentinelWorkspaceTools.cs)` to disambiguate the field from
same-named fields in other classes (SentinelIntelligenceTools also has `_dependencyEngine`).

**Bug:** the contextSnippet — being the assignment line itself — caused resolution to land on the
constructor **parameter** `dependencyEngine`, not the **field** `_dependencyEngine`. Result was the
~13 test call sites that pass a positional `new DependencyEngine(...)` argument, not the actual
in-method field usages (ListSolutionItems/ListWorkspaceSolutions) that grep found correctly.
Same pattern repro'd for `_structuralRefinementEngine` and `_projectConsistencyEngine`. Without
`filepath`+`contextSnippet`, plain `symbolName` lookup instead silently resolved to a *different*
class's same-named field (e.g. `SentinelIntelligenceTools`'s copy) with no ambiguity warning.

**Why it matters:** for any private field whose name collides with its own constructor parameter
(the near-universal convention in this codebase) or with a same-named field in another class,
`FindReferences` can silently return the wrong symbol's reference list — not an error, just
plausible-looking wrong data. Grep remained the reliable fallback here.

**How to apply:** when auditing field usage across this codebase, verify FindReferences results
look like real method-body call sites, not constructor-assignment lines, before trusting them. For
ambiguous private-field names, prefer grep or pin disambiguation with a snippet drawn from an actual
usage site (not the declaration/assignment line).
