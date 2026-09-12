# PreviewSymbolTypeChangeImpact + ChangeSymbolType (design proposal)

## Motivation

PlanStepRunner run `20260911-205633-213`, step `02-phase1-types-and-engine-fix`, asked the model
to reshape `BuildResult` — replace `bool BuildSucceeded` with `BuildOutcome Outcome` (a new 3-state
enum) — and fix every call site in the same pass, so the solution only needed to be clean at the
end of the step. The model had no tool for "treat this as one atomic multi-file change." Its
available tools (`Member`, `ReplaceSnippet`) validate and commit per file per call, so a
type-changing edit that necessarily touches several files at once cannot help but be submitted as
several separately-validated calls — and any ordering of those calls leaves some intermediate call
validated against a solution that is genuinely half-migrated (some files already using the new
shape, others still using the old one).

Concretely, the model:
1. Tried to land `BuildResult.cs`'s reshape alone first — rejected, because `BuildEngine.cs` and
   `BatteryTwentyTests.cs` still referenced the old `BuildSucceeded` field (CS1739/CS1061).
2. Pivoted to the purely-additive pieces first (new enum value, new static helper) — succeeded,
   since those can't break anything.
3. Tried `BuildEngine.cs`'s rewrite next, expecting `BuildOutcome`/`Outcome` to already exist —
   rejected twice (CS0103), because the `BuildResult.cs` edit from step 1 had never actually
   landed.
4. Re-read `BuildResult.cs` to confirm it was still in the old shape, reasoned "I need to do all
   changes together," and re-submitted the `BuildResult.cs` change via `ReplaceSnippet`'s
   `action: apply` — a path that skips the normal validate-before-write gate for that one call.
5. Only then could `BuildEngine.cs`'s edit land, since `BuildOutcome`/`Outcome` finally existed.

5 tool-call failures and ~8 extra turns were spent discovering, by trial and error, a mechanism
(the `apply` escape hatch) that exists specifically to let one file land in a transiently-broken
state — rather than being given a way to submit the whole coordinated change as one atomic unit,
which is what the plan step actually called for and what the underlying apply path already
supports (see below).

Full run analysis: PlanStepRunner run `20260911-205633-213`, step `02-phase1-types-and-engine-fix`
(agent.log turns 6-16).

## Why this isn't already solved by `RenameSymbol`

`RenameSymbolAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs`, called from
`SentinelRefactoringTools.RenameSymbol`) produces a `Dictionary<FilePathWrapper, string>` spanning
every referencing file in one Roslyn `Renamer.RenameSymbolAsync` call, and submits that whole
dictionary to `ValidateAndApplyHelper.ValidateAndApplyAsync` in one shot — so a rename is validated
against its *final* state across every file at once, never an intermediate one. This is why rename
never hits the "interim broken solution" problem: the transformation is mechanical and
representation-preserving (every call site keeps compiling with the identifier text swapped, no
matter what the surrounding code does with the value).

A type change is not representation-preserving. `if (x.BuildSucceeded)` becomes
`if (x.Outcome == BuildOutcome.Succeeded)` — a different expression shape, not a token swap — and a
constructor call `new BuildResult(BuildSucceeded: false, ...)` needs a value for the *new* field
that doesn't exist on the old shape at all. Roslyn can find every reference semantically
(`SymbolFinder.FindReferencesAsync`, same machinery `FindReferences`/`PreviewRenameImpact` already
use), but it cannot generate the rewrite content — that requires the same judgment the plan step's
prose already carried ("derive `Outcome` from `ErrorCount`", "`ExitCode` stays null unless a
process was spawned").

**Key finding**: `ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common`) already
accepts a multi-file `Dictionary<FilePathWrapper, string>` and validates/applies it as one atomic,
rollback-on-partial-failure unit. The plumbing for a true multi-file atomic write already exists;
no MCP-facing tool assembles more than one file's worth of freehand content into that dictionary
today. `RenameSymbol` is the only tool currently doing so, and only because its per-file content is
Roslyn-generated rather than model-supplied.

## Proposed tools

### `PreviewSymbolTypeChangeImpact`

Read-only, mirrors `PreviewRenameImpact`'s existing shape and lives beside it in
`SentinelSymbolTools.cs`. Takes the same target-resolution params `PreviewRenameImpact` and
`RenameSymbol` already use (`docCommentId` preferred; `filepath`/`symbolName`/`contextSnippet`
fallback). Finds every reference via the same `SymbolFinder`-backed machinery `FindReferences` uses,
then classifies each reference by **syntactic shape** — a closed set Roslyn can determine
unambiguously from each reference's enclosing syntax, independent of what the rewrite should say:

- `Condition` — reference is (or is the operand of) a boolean condition: `if (x.Field)`,
  `x.Field ? a : b`, `while (x.Field)`
- `NegatedCondition` — same, but wrapped in `!`
- `ConstructorNamedArg` — reference is a named argument at an object/record creation site:
  `new T(Field: v, ...)`
- `ConstructorPositionalArg` — same but positional (name not recoverable from the call site;
  flagged separately since a template keyed only on shape can't safely target it)
- `ValueAccess` — reference is read as a plain value with no special syntactic role:
  `return x.Field;`, `var y = x.Field;`
- `DeclarationSite` — the member declaration itself (e.g. the record parameter/property), always
  reported separately since it's not a "call site" and needs the new declaration source directly,
  not a shape-templated rewrite
- `Unclassified` — anything not cleanly matching the above (e.g. inside an expression tree, a
  reflection call, or other unusual context) — always surfaced, never silently bucketed into a
  best-guess shape

Returns, per shape: count, and a few example locations (file, line, surrounding snippet) — same
depth as `PreviewRenameImpact`'s existing "affected files and location count... for the full
per-location list, use FindReferences" convention. Purpose is letting the model see the full blast
radius and its shape distribution in one call, rather than `SearchSolutionText` plus several
individual `ReadFile`s (as happened in the observed run's turns 2-5).

### `ChangeSymbolType`

Takes `docCommentId`, the new declaration source (replacing `DeclarationSite`, same idea as
`newName` on `RenameSymbol` but a full source fragment instead of an identifier), and a
`replacements` list of `{shape, template}` pairs — one rule **per shape**, not per instance:

```
ChangeSymbolType(
  docCommentId: "...",
  newDeclarationSource: "BuildOutcome Outcome",
  replacements: [
    { shape: "Condition",         template: "{expr}.Outcome == BuildOutcome.Succeeded" },
    { shape: "NegatedCondition",  template: "{expr}.Outcome != BuildOutcome.Succeeded" },
  ])
```

Internally: re-run the same reference search and classification `PreviewSymbolTypeChangeImpact`
uses, apply each site's matching template (substituting `{expr}` with that site's actual receiver
expression, Roslyn-printed — not a text substitution against source), assemble every resulting edit
plus the declaration-site edit into one `Dictionary<FilePathWrapper, string>`, and submit it to
`ValidateAndApplyHelper.ValidateAndApplyAsync` exactly as `RenameSymbol` does — one validation pass
against the fully-migrated solution, atomic apply, rollback on partial failure.

**Fail-closed coverage requirement**: if any found, non-`Unclassified` reference's shape has no
matching rule in `replacements`, or any reference is `Unclassified` or `ConstructorPositionalArg`,
reject the whole call before generating any edits and report exactly which sites are uncovered
(reusing the same location detail `PreviewSymbolTypeChangeImpact` returns). Partial application
would silently reintroduce the interim-broken-solution problem this tool exists to remove, just
relocated inside one call instead of across several.

**Explicitly not a regex/text-pattern mechanism.** "Shape" means a Roslyn-determined syntactic
classification of each reference (condition / negated-condition / named-arg / plain value /
declaration), not a text or regex pattern matched against source. A template is applied only at
locations the semantic reference search already found and classified — never matched independently
against file text — so there is no risk of a template silently matching zero sites, or matching an
unintended site (inside a comment, a string, or an unrelated symbol with a coincidentally identical
expression). This is the same guarantee `RenameSymbol` gets from operating on `ISymbol` references
rather than text, extended one step further to a small set of known rewrite shapes.

## Relationship to the general multi-file-apply gap

`ChangeSymbolType` is best understood as **one convenient way to assemble a multi-file
`ValidateAndApplyAsync` call**, pre-populated by a symbol reference search, rather than a
freestanding mechanism. The same gap it plugs — no MCP-facing tool lets a model submit several
files' worth of freehand content as one atomically-validated unit — likely has other legitimate
uses beyond type changes (e.g. any plan step that says "reshape X and fix every call site in the
same pass," which is a common shape for this kind of phased migration work). If a general
multi-file-apply primitive gets built first (accepting an explicit list of
`{filepath, newContent}` pairs, threaded into the existing `ValidateAndApplyAsync` contract),
`ChangeSymbolType` becomes a thin layer generating that input from a symbol search, rather than a
second implementation of the atomicity/rollback logic `ValidateAndApplyHelper` already has.

Recommendation: consider whether the general primitive is worth building first — same effort either
way, but avoids `ChangeSymbolType` duplicating the underlying commit logic.

## Open items for implementation

- Confirm which existing engine (`DiscoveryEngine`, `RefactoringEngine`, or a new one) is the right
  home — `PreviewRenameImpact` lives on `DiscoveryEngine`, `RenameSymbol` on `RefactoringEngine`;
  this pair likely splits the same way (preview read-only on Discovery, apply on Refactoring).
- Nail down the exact shape-classification rule set against a handful of real call sites beyond
  this run's `BuildSucceeded` example (e.g. a reference inside a LINQ lambda, a ternary, a
  switch-expression arm) before committing to the closed shape list above — it may need to grow.
- Decide `{expr}` substitution semantics precisely: is it the receiver only (`x` in `x.Field`), or
  does it need to handle chained/nested receivers (`a.b.Field`)? Roslyn's printed expression syntax
  should handle this correctly if substitution operates on the actual receiver `ExpressionSyntax`
  node rather than a string.
- Decide how `ConstructorPositionalArg` sites should be handled long-term — always reject, or offer
  a way to promote them to named args first (a separate, existing mechanical transform) so they
  reclassify as `ConstructorNamedArg` on retry.
- Regression test mirroring this run's fixture: a `bool` field turned into an enum-valued property
  across a record + 2-3 call sites of different shapes (condition, negated, constructor arg).

## Status

Design proposal only — not yet implemented. Motivated by a live PlanStepRunner run's observed
friction, not a hypothetical.
