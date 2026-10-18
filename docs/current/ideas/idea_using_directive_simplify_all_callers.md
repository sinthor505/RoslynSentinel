# UsingDirective(operation: add, simplifyAllCallers: true) - solution-wide simplification

**Status:** NOT DESIGNED 2026-10-17. Future feature recorded from the old TODO.md; no design or implementation exists.

## What happened
Moved verbatim from the restructured TODO.md.

## Moved from TODO.md (2026-10-17)

### Future feature: `UsingDirective(operation: add, simplifyAllCallers: true)` — solution-wide simplification

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
