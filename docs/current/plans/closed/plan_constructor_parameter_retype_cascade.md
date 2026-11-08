# Constructor parameter retype with transitive call-site cascade ("option 3 - full build")

## Context

Blocker: `docs/current/blockers/blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md`.
Three options were weighed on 2026-09-29/30:

1. Hand-edit the ~17 call sites - rejected (bypasses atomic tooling).
2. `ConstructorParameter(add, callSiteFixups)` - append a required param and supply its argument at
   every call site in one atomic, compile-gated change. **Implemented 2026-10-01 (uncommitted at time
   of writing):** `MemberRefactoringEngine.AddConstructorParameterWithCallSitesAsync` plus a new
   `RoslynSentinel.Engines.Basic/ConstructorCallSiteFinder.cs`; tests in
   `RoslynSentinel.Tests.Basic/ConstructorParameterCallSiteFixupTests.cs`. Live dryRun of the blocker
   case (`WorkspaceProjectManagementImpl` + `config`) passed the compile gate.
3. This plan: change an *existing* parameter's declared type in place, and optionally walk the
   constructor-injection chain upward, retyping each upstream parameter that merely forwards the
   value, until reaching a site that needs a real expression.

Option 2 unblocks the group-6 engine folds on its own (see "Fold sequencing" below). Option 3 is the
general capability: concrete-to-interface swaps, engine consolidation where the target already has
the members, and any "this dependency is now type B instead of A" refactor - without the agent
hand-tracing the DI graph.

## What already exists (verified by reading source 2026-09-30)

`ChangeSignature` already does most of the call-site work for constructors:

- Tool: `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs:111`. Engine:
  `RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs:99` `ChangeSignatureAsync`.
- Matches `BaseMethodDeclarationSyntax`, so a ctor is targeted by class name.
- Reorder/add/remove via `ExistingParameterSpec(OriginalIndex)` / `NewParameterSpec(Name, Type,
  DefaultValueExpression)`; rewrites `InvocationExpressionSyntax` and
  `BaseObjectCreationExpressionSyntax` call sites solution-wide, handling named args and omitted
  trailing optionals.

Gaps relevant here:

| Gap | Evidence |
| --- | --- |
| No retype: `ExistingParameterSpec` carries only an index | `ChangeSignatureAsync` declaration loop |
| New params get one literal inserted at every site and also kept as the declared default - no per-site expression | `case NewParameterSpec added:` / "Always fill in the literal default value" comment |
| Ctor-only plumbing missing: no backing field / assignment create, retype, or removal | engine edits `ParameterList` only |
| `: base(...)` / `: this(...)` initializers are not call-site shapes it recognizes - they fall into the "not a simple invocation" skip (by reading; untested) | `invocation ??= ...BaseObjectCreationExpressionSyntax` branch |
| **Suspected latent defect:** innermost-node selection checks `InvocationExpressionSyntax` ancestors *before* object creation, so `Foo(new Bar(x))` with `Bar`'s ctor as target appears to bind the outer `Foo(...)` invocation and rewrite the wrong argument list. Hypothesis from source, needs a regression test | `token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault()` evaluated first |
| Target-typed `new(...)` sites are likely missed entirely: option 2's implementation found that `SymbolFinder.FindReferencesAsync` does not report them as ctor references and had to add `ConstructorCallSiteFinder.AddTargetTypedCreationSitesAsync`; `ChangeSignatureAsync` relies on `FindReferencesAsync` alone. Not yet confirmed against `ChangeSignature` with a test | `SymbolFinder.FindReferencesAsync(declaredSymbol, ...)` loop |
| Overloads: first declaration by name wins (`FirstOrDefault`), no disambiguation | method lookup at top of `ChangeSignatureAsync` |
| Zero-param target or empty spec returns `emptyResult` silently (cannot add to a parameterless ctor) | `if (originalParams.Count == 0 \|\| parameters.Count == 0) return emptyResult;` |
| Skipped sites surface either as raw compile-gate dumps or as a post-apply WARNING - not fail-closed with copyable keys like `MoveMember`'s `UnresolvedCallSites` | `summaryNote += " WARNING: ..."` in the tool |
| Not reachable from the Basic server: Basic `Refactor` mode lists only `RefactoringSignatureTools`; `AdvancedRefactoringTools` is Advanced/Claude-mode only | `RoslynSentinel.Server.Basic/ToolClassRegistry.cs:18` vs `:58`/`:64` |

The last row matters for the mission: the small-model target is the Basic server, so the capability
must be reachable from a Basic-hosted tool.

## Design

### Operation shape

Expose one new operation, retype exactly **one root parameter** per call:

```
ConstructorParameter(operation: retype, className, paramName, paramType: <new type>,
                     newParamName?: <optional rename, also renames backing field>,
                     cascade: none | injectionChain,      // REQUIRED for retype, no default
                     callSiteFixups?: { "<FilePath:Line>|<FilePath:*>|*": "<expr of new type>" },
                     dryRun, returnDiff, contextSnippet/lineBefore/lineAfter for overloads)
```

- One root param keeps the fixup value a plain `string` (one argument per site), the same shape as
  `MoveMember` and option 2 - no nested map, which small models handle poorly.
- `cascade` is mandatory (per the "prefer mandatory params" rule): an optional default either
  silently over-reaches across many files or silently under-reaches into a compile-gate wall.
- `ChangeSignature` (Advanced) gains `newType`/`newName` on existing-param entries and the same
  `callSiteFixups`, driven by the same engine, so the two surfaces never diverge.

### Per-site resolution

For each call site binding to the target ctor (object creation, target-typed `new(...)`,
`: base(...)`, `: this(...)`), the argument for the retyped param is classified:

1. **Converts implicitly** (`Compilation.ClassifyConversion(argType, newType).IsImplicit`) - keep
   as-is. Covers concrete-to-interface/base swaps with zero fixups.
2. **Forwarder** - identifier bound to a parameter of the enclosing ctor, or to a field assigned
   from one in that ctor's body. With `cascade: injectionChain`, retype that upstream parameter (and
   its field) too and recurse. With `cascade: none`, falls through to 3.
3. **Needs a value** - matched `callSiteFixups` entry wins; otherwise unresolved.

Any unresolved site fails the whole call **before** the compile gate with `UnresolvedCallSites`,
one row per site: full-path `FilePath:Line` key (copyable verbatim), the call text, the old/new
type, and identifiers of the new type in scope. Exact-line keys matching no site are rejected
(`InvalidArgument`), as in option 2.

### Cascade terminals

- **DI-container roots** - a class whose ctor has no syntactic call sites (resolved by the
  container). Check that the new type is registered in `ServiceRegistrationExtensions*.cs`-style
  registrations (`Add{Singleton,Scoped,Transient}<T>` / `typeof(T)` / factory lambdas). Not
  registered -> unresolved row "DI root: register <newType>". Never auto-edit registrations.
- **Test construction sites** - need a fixup. The `"new"` shorthand means `new NewType()` only when a
  zero-arg ctor exists, refused up front otherwise (mirror `MoveMember`).
- Cycle guard via a visited set of (ctor symbol, param ordinal); depth cap with an explicit error.

### Inside each retyped class

- Retype the parameter and its inferred backing field (reuse `GetConstructorParametersAsync`'s
  field inference, `MemberRefactoringEngine.cs` ~line 434). Rename both via `Renamer` if
  `newParamName` is set.
- **Member-usage pre-check:** every member access on the retyped field/param inside the class must
  bind on the new type (speculative binding against the candidate solution). Failures return an
  actionable "`_dependencyEngine.GetProjectDependenciesAsync` at File:Line does not exist on
  `SolutionStructureEngine`" list instead of a raw CS1061 dump.

### dryRun preview

`dryRun: true` returns the cascade tree - each node (class, param, old -> new type, file:line) and
each leaf site with its classification (`Converts` / `Forwarded` / `FixupApplied` / `Unresolved` /
`DiRootOk` / `DiRootMissing`) - modelled on `MoveMember`'s `PreviewCallSite` rows. This is the
"non-contact voltage detector" for a multi-file change: the agent sees the blast radius before
committing to it.

### Explicit refusals (clear message, no partial behaviour)

Primary-constructor classes and records, partial classes whose ctor/field span files (until tested),
generic type parameters as the retyped type, and an ambiguous overload without a snippet locator.

## Fold sequencing - why option 3 alone does not unblock group 6

Retype and `MoveMember` form a cycle for an engine fold: retyping `DependencyEngine` ->
`SolutionStructureEngine` first fails the member-usage check (the methods are not on the target
yet); `MoveMember` first fails because no receiver of the target type is in scope.

The working sequence uses option 2: `ConstructorParameter(add, callSiteFixups)` the target engine
-> `MoveMember` (receiver now in scope) -> remove the old param. The remove step needs Phase 4 below,
since `ConstructorParameter(remove)` today is last-param-only and `DependencyEngine` is not last.
Breaking the cycle in one call would need a `MoveMember` composite (Phase 7, optional).

## Phases

| # | Work | Size |
| --- | --- | --- |
| 0 | Regression tests pinning the existing `ChangeSignature` gaps above, especially the suspected nested-creation-inside-invocation defect; fix that defect if confirmed (one-line ordering fix: pick the innermost of the two candidates). | S |
| 1 | Shared infrastructure in `Engines.Basic`: move `CallSiteFixupMap` out of `MemberRefactoringEngine` into its own internal type; make option 2's `ConstructorCallSiteFinder.cs` the single call-site locator (all four call-site shapes, target-typed `new(...)` compensation, innermost-node selection, overload binding) and switch `ChangeSignatureAsync` and the new retype onto it. Behaviour-preserving apart from the Phase 0 defect fixes; existing tests green. | M |
| 2 | Retype, `cascade: none`: declaration + backing field + optional rename, conversion classification, fixups, fail-closed `UnresolvedCallSites`, member-usage pre-check, dryRun preview. Expose as `ConstructorParameter(retype)` (Basic) and `ChangeSignature` existing-param `newType`/`newName` (Advanced). | L |
| 3 | `cascade: injectionChain`: forwarder detection, recursion, cycle/depth guards, DI-root registration check, cascade tree in the preview. | L |
| 4 | `ConstructorParameter(remove)` at any position: reuse the locator to drop the argument at every site; keep the existing "delete field only if unused" check. | M |
| 5 | Descriptions/schemas: verify the **emitted** JSON schema for the new params (repo history of schema-emission drift), mandatory `cascade` shows as required, `ToolParams` wording for small models. Model-eval run on a Basic-server fixture exercising a concrete-to-interface retype. | M |
| 6 | Dog-food on the real case: finish the group-6 folds (`DependencyEngine`, `ProjectConsistencyEngine`, `SolutionManagementEngine`) with these tools only; close the blocker per the blocker workflow. | M |
| 7 | *(Optional, decide after 6)* `MoveMember(retypeInjectedSource: true)`: when a fold empties the source class, retype every injected source-typed param to the target along the chain in the same atomic change. Only worth it if folds recur. | L |

Phases 0-1 can land independently and de-risk everything after. 2 and 3 are the core; 4 is small
but on the critical path for Phase 6.

## Risks

- **Blast radius:** the motivating chain touches 16+ test files. `ValidateAndApplyHelper` already
  applies multi-file changes atomically with rollback; the risk is compile-gate latency on large
  cascades. Measure in Phase 3 before optimizing.
- **Forwarder heuristics:** a field assigned in the ctor but reassigned elsewhere is not a pure
  forwarder - treat any non-ctor write as "needs a value", never guess.
- **Same-file multi-edits:** the class edit and a `this(...)`/test site in the same document must
  be merged into one document edit (`ChangeSignatureAsync` already re-locates by span after earlier
  edits; the shared locator must keep that).
- **Option 2 overlap:** option 2 introduced `ConstructorCallSiteFinder`; `ChangeSignatureAsync` has
  its own inline locator. Phase 1 consolidates onto the finder - do not add a third.

## Open decisions

1. **Surface:** both `ConstructorParameter(retype)` (Basic, small-model reachable) and
   `ChangeSignature` extension (Advanced) on one engine - recommended - vs Basic-only.
2. **`cascade` mandatory vs defaulted:** recommended mandatory.
3. **Phase 7:** build the `MoveMember` composite, or accept the three-step option 2 sequence for
   folds.
