# Blocking error — `FindReferences` on a private field, disambiguated with a `contextSnippet` taken from its own assignment line, silently resolves to the constructor parameter instead of the field

**Status:** FIXED — root causes identified and corrected in `ContextHelper.cs` and
`SymbolNavigationEngine.cs` (see Root cause / Fix below). Ready to move to
`docs/obsolete/blockers/`.

## Root cause

Two independent bugs in symbol resolution, both triggered by this repro:

1. **Call 3 (wrong symbol via contextSnippet)** — `ContextHelper.FindSymbolAtSnippetAsync`
   resolved the node at the snippet position, then walked `node.AncestorsAndSelf()` looking for
   the first ancestor with a declared symbol, falling back to `GetSymbolInfo` only if that whole
   walk found nothing. For a snippet landing on a plain reference (`_dependencyEngine =
   dependencyEngine;`), the node itself is an `IdentifierNameSyntax` with no declared symbol
   (correct — it's a reference, not a declaration), but the walk didn't stop there: it kept
   climbing through `AssignmentExpressionSyntax` → `ExpressionStatementSyntax` → `BlockSyntax` →
   the enclosing `ConstructorDeclarationSyntax`, which *does* have a declared symbol (the
   constructor itself) — so the walk returned the constructor symbol instead of ever trying
   `GetSymbolInfo` on the original node, which would have correctly returned the field. Confirmed
   with an isolated Roslyn repro: `GetSymbolInfo(node)` alone gives the correct field symbol;
   `AncestorsAndSelf().Select(GetDeclaredSymbol).FirstOrDefault(...)` gives the constructor.
   Fixed by checking only `GetDeclaredSymbol(node)` directly (no ancestor walk), then falling back
   to `GetSymbolInfo(node)`, then (for the field/property-declaration case, which can declare
   multiple variables and so has no declared symbol on the `FieldDeclarationSyntax` node itself) a
   descend into `VariableDeclaratorSyntax` children.

2. **Call 2 (false "not found declared" error)** — `FindCallersAsync`'s no-contextSnippet,
   filePath-pinned branch called `model.GetDeclaredSymbol(decl, ...)` directly on a
   `FieldDeclarationSyntax` found via name search. `GetDeclaredSymbol` always returns `null` for a
   `FieldDeclarationSyntax` node itself (a field declaration can list multiple variables; the
   declared symbol lives on the matching `VariableDeclaratorSyntax` child) — so a field that
   `decls` genuinely found was reported as "not found declared" anyway. Fixed with the same
   `VariableDeclaratorSyntax` fallback.

Call 1 (no filePath, no contextSnippet, wrong class) is not an independent bug: `FindCallersAsync`
resolves by name across the whole solution when no `filePath` is given, and multiple classes
declaring a same-named field is inherently ambiguous without a `filePath` — that's expected,
documented behavior, not silent misresolution. Once `filePath` is supplied (Call 3), resolution is
now correct.

## Fix

- `RoslynSentinel.Common/ContextHelper.cs` — `FindSymbolAtSnippetAsync` rewritten to check the
  node itself (`GetDeclaredSymbol` → `GetSymbolInfo` → `VariableDeclaratorSyntax` descent) instead
  of walking ancestors.
- `RoslynSentinel.Basic/SymbolNavigationEngine.cs` — `FindCallersAsync`'s no-contextSnippet branch
  now falls back to the field's `VariableDeclaratorSyntax` when `GetDeclaredSymbol` on the
  `FieldDeclarationSyntax` itself returns null.
- Regression tests added in `RoslynSentinel.Tests/RegressionTests.cs`:
  `FindCallersSafe_FieldNameShadowedByCtorParam_ContextSnippetOnAssignmentLineResolvesToField` and
  `FindCallersSafe_FieldNameOnly_NoContextSnippet_ResolvesDeclaredField`. Both pass; full
  `RegressionTests` suite (52 tests) passes in isolation; `Advanced` flavor build/test baseline
  shows 0 new warnings/errors and 10 pre-existing known test failures (no new ones).

## Symptom

`FindReferences(symbolName: "<fieldName>", kind: "callers", filepath: ..., contextSnippet: "<the
field's own assignment line, e.g. `_dependencyEngine = dependencyEngine;`>")` returns `success:true`
with a plausible-looking, non-empty reference list — but the list is for the **constructor
parameter** with the same name, not the **field**. No error, no ambiguity warning, no indication the
wrong symbol was resolved.

Separately, `QuerySymbolRelationships` cannot help either: `searchKind: "objectCreations"` on a field
name correctly refuses with `InvalidArgument` ("resolves to a Field ... not a type"), but its error
message directs the caller straight back to the same broken `FindReferences` path.

## Repro

Task: audit which methods of `SentinelWorkspaceTools` actually use the `_dependencyEngine` field
(this class and `SentinelIntelligenceTools` in `RoslynSentinel.Server.Advanced` both declare a field
of this same name, so disambiguation is required).

**Call 1** — no `filepath`, no `contextSnippet`:
```
FindReferences(symbolName: "_dependencyEngine", kind: "callers")
```
Result: `success:true`, single reference — `SentinelIntelligenceTools`'s constructor assignment line
only. Silently picked the *other* class's same-named field; `SentinelWorkspaceTools`'s own
`_dependencyEngine` usages do not appear at all.

**Call 2** — pinned to the correct file, no `contextSnippet`:
```
FindReferences(symbolName: "_dependencyEngine", kind: "callers",
  filepath: "RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs")
```
Result: `error:"Exception"` — `"symbolName '_dependencyEngine' was not found declared in
'...SentinelWorkspaceTools.cs'"` — even though the field genuinely is declared in that file (grep
confirms it, and it compiles). The tool suggests verifying via `GetFileOutline` or dropping
`filePath`, but dropping `filePath` reproduces Call 1's wrong-class result.

**Call 3** — pinned file + `contextSnippet` taken from the field's own assignment line (the obvious
disambiguation to try, and the pattern this tool's own docs recommend for ambiguous names):
```
FindReferences(symbolName: "_dependencyEngine", kind: "callers",
  filepath: "RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs",
  contextSnippet: "_dependencyEngine = dependencyEngine;")
```
Result: `success:true`, 13 references — but every one is a **test file's constructor call**
(`new SentinelWorkspaceTools(..., new DependencyEngine(_workspaceManager), ...)`), i.e. matches to
the constructor **parameter** `dependencyEngine`, not the field `_dependencyEngine`. The two actual
in-method field usages (`ListSolutionItems`, `ListWorkspaceSolutions` — confirmed present via grep
and manual `Read`) are entirely absent from the result.

Reproduced identically for `_structuralRefinementEngine` (resolved to `SentinelRefactoringTools`'s
field/param instead of `SentinelWorkspaceTools`'s 3 genuine usages in `SafeDeleteUnusedSymbol`) and
`_projectConsistencyEngine` (resolved to `SentinelIntelligenceTools`'s field/param, missing
`SentinelWorkspaceTools`'s 1 genuine usage in `ListProjectFrameworkTargets`).

## Impact

There is currently no reliable MCP-tool-only way to answer "which methods of class X use private
field Y" when Y's name collides with its own constructor parameter (near-universal convention in
this codebase) or with a same-named field in another class (also common — `_dependencyEngine`,
`_structuralRefinementEngine`, `_projectConsistencyEngine` all collide across
`SentinelWorkspaceTools`/`SentinelRefactoringTools`/`SentinelIntelligenceTools`). Every call shape
tried either silently returns a different symbol's references (wrong class, or ctor-param instead of
field) with `success:true` and no warning, or errors on a declaration that demonstrably exists. An
agent trusting the `success:true` result would draw incorrect conclusions about dependency usage —
directly relevant to the DI-engine-audit work in
`docs/current/project_di_engine_audit_open_question.md`, where this was first hit.

## Confirmed NOT the cause

- Not a stale-server issue — solution was freshly loaded this session before any of these calls.
- Not specific to one field/class pair — reproduced for 3 distinct fields across 3 distinct classes.
- Not fixable by supplying `filepath` alone — Call 2 shows `filepath` alone causes a false
  "not declared" error against a file where the field is confirmed (by direct `Read`) to exist.
- Grep against the same file, for the same field name, correctly finds only true in-method usage
  sites (verified by reading each matched line) — so this is specific to this tool's symbol
  resolution, not a general ambiguity inherent to the question being asked.

## What I did NOT do

Per policy, did not fall back to guessing further undocumented parameter combinations to work around
the resolution bug, and did not silently substitute grep results into the audit without flagging the
tool gap — grep was used as the verified fallback for the immediate task
([[project_di_engine_audit_open_question]] / `project_findreferences_ambiguous_field_vs_ctorparam.md`),
but the underlying tool defect is reported here rather than worked around indefinitely.

## Next step

Waiting for confirmation/fix before relying on `FindReferences` for field-usage audits involving
ctor-param-shadowed or cross-class-collided field names. Once resolved, move this file to
`docs/obsolete/blockers/`.
