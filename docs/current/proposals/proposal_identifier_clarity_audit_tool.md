# FindIdentifierClarityIssues (design proposal)

## Motivation

The user asked how to audit a repo for unclear/ambiguous symbol identifiers: too short (e.g. under
5 characters), single-word/vague (`Data`, `Manager`, `Info`), or the same name reused for different
purposes in nested scopes. No existing tool answers this.

`FindNamingViolationsAsync` (`RoslynSentinel.Advanced/AntiPatternEngine.cs:1172`) looks adjacent but
solves a different problem: it checks **casing-convention compliance** (`_camelCase` fields, PascalCase
non-private methods, camelCase parameters) via three regex/char checks
(`CheckFieldNamingConventions`, `CheckMethodNamingConventions`, `CheckParameterNamingConventions`,
same file lines 1223-1343). A perfectly convention-compliant name like `int x` or `string data`
passes all three checks today and would continue to. Clarity and convention are orthogonal axes;
this proposal does not touch `FindNamingViolationsAsync` and should not be folded into it — different
`AntiPatternFinding.Category` values, so both can coexist in the same result set (as
`DetectAntiPatternsAsync` already aggregates from multiple category-specific finders — see line 85).

## Proposed tool

### `FindIdentifierClarityIssues`

Lives beside `FindNamingViolationsAsync` in `AntiPatternEngine.cs` (same engine, same DI surface,
same `AntiPatternFinding` return shape — reuses the existing category/severity/file/line/snippet
record rather than inventing a new one).

```
FindIdentifierClarityIssues(
    filePath: string? = null,
    projectName: string? = null,
    minLength: int = 3,           // flag identifiers shorter than this (after exemption list)
    symbolKinds: string[]? = null,  // e.g. ["Method","Property","EnumMember"] - default: all except locals/params
    cancellationToken)
    -> List<AntiPatternFinding>   // Category = "IdentifierClarity"
```

Same `filePath`/`projectName` scoping convention as `FindNamingViolationsAsync` and the rest of
`AntiPatternEngine`, for consistency and so both can be called with identical arguments in a combined
audit pass.

### Checks, and why each needs semantic (not textual) analysis

**1. Short identifiers (length check).**
Flat length threshold, but exempt a small fixed allow-list of conventional short names regardless of
threshold: `id`, `ok`, `db`, `ex` (catch-clause exception), `i`/`j`/`k` *only* inside a `for` loop
header (detected structurally — `ForStatementSyntax` declaration, not by name), plus LINQ lambda
parameters bound within a single chained expression (`x => x.Foo`) since their scope is the
expression itself, not the containing method. This exemption list is why this can't be a plain regex
over source text: "is `i` a loop counter or a field" requires knowing the enclosing
`SyntaxNode` kind, which needs the syntax tree, not grep.

Default `symbolKinds` excludes local variables and parameters — same rationale as the char-length
issue above: a 1-character loop counter spanning 2 lines is idiomatic; a 1-character public method or
enum member is a real problem and lives far longer. Locals/params are opt-in via `symbolKinds` for
users who want the stricter pass.

**2. Single-word / vague identifiers.**
Two signals, both computed from the identifier text directly (no semantic model needed beyond having
already resolved the declaration):
- **Word-count == 1** via a camelCase/PascalCase segment split (e.g. `Regex.Matches(name,
  @"[A-Z]?[a-z0-9]+|[A-Z]+(?![a-z])")` — same style of segmentation .NET analyzers use for
  `IdentifiersShouldBeSpelledCorrectly`-style checks). A name with zero segment boundaries beyond the
  first is single-word.
- **Vague-suffix/whole-word denylist**: `Manager`, `Handler`, `Helper`, `Info`, `Data`, `Base`,
  `Impl`, `Utils`, `Processor`, `Object` (case-insensitive whole-word or suffix match against a
  segment). This catches `OrderManager`, `Data`, `ResultInfo` — flagged regardless of length, since
  these are exactly the vague-but-not-short names the user's own length check would miss.

**3. Same identifier bound to different things across nested scopes.**
This is the check that actually requires Roslyn's semantic model, not just syntax:
- **Shadowing** — walk each method body's scopes (block, lambda, local function) via
  `SemanticModel.LookupSymbols` at each nested scope boundary and detect when a name introduced in an
  inner scope matches a name already visible from an outer scope. Report as `Category =
  "IdentifierShadowing"`, `Severity = "Medium"` (higher than plain reuse — this is the dangerous
  variant, since the compiler silently allows a nested redeclaration that changes meaning
  mid-method).
- **Cross-scope reuse with a type change** — same identifier text used for locals/fields of
  *different* `ITypeSymbol` in sibling (non-nested) scopes within the same containing type. Computed
  by collecting `(name, ITypeSymbol)` pairs per containing type via `SemanticModel.GetDeclaredSymbol`
  over all `VariableDeclaratorSyntax`/parameter nodes, then grouping by name and flagging groups with
  more than one distinct type. This is a stronger signal than raw reuse count: `result` used as
  `string` in one method and `bool` in another is a real ambiguity risk; `result` used as `string` in
  both is just a common, harmless name and should **not** be flagged — plain same-type reuse across
  unrelated methods is deliberately out of scope (see below), since flagging it produces mostly noise
  (`temp`, `item`, `result` used naturally in dozens of unrelated small methods) and would drown the
  two signals that actually matter.

Explicitly **not implemented**: raw "identifier X appears in N different methods" counting with no
type-difference filter. This was in the user's original proposal but isn't included because it has
low precision — it flags idiomatic reuse (`i`, `result`, `item`) as often as real problems, and
without the type-difference signal there's no way to rank findings by actual risk.

### Not included, but worth a one-line mention (near-duplicate names)

Sequential/near-identical names in the same scope (`data`/`data2`, `item`/`items`, `temp`/`temp2`)
are a real ambiguity smell the checks above don't catch — no length, casing, or shadowing rule fires
on `data2`. An edit-distance or numeric-suffix-stripped-collision check within a single scope's
declared symbols could catch this, but it's a distinct algorithm (pairwise comparison within a scope,
not a per-identifier classification) and is left as a possible **follow-up finding type** rather than
folded into v1, to keep the first cut's rule set auditable.

## Severity weighting by symbol kind

Same `Severity` field `AntiPatternFinding` already carries. Weight by blast radius, not just which
rule fired:
- Public/internal API surface (method, property, enum member) failing any check -> `Medium`
- Private member failing any check -> `Low`
- Shadowing (check 3) -> `Medium` regardless of accessibility, since it's a correctness hazard, not
  a readability one
- Type-differing cross-scope reuse (check 3) -> `Low`/`Medium` depending on whether either binding is
  public API surface

## Open items for implementation

- Confirm final vague-suffix denylist against a sample pass over this repo's own `*Engine.cs` files
  before committing to the list (the repo's own naming — `AnalysisEngine`, `DiscoveryEngine` — uses
  `*Engine` as a suffix pervasively and intentionally; the denylist above deliberately excludes
  `Engine` for that reason, and should be re-checked against other repo-wide intentional suffixes).
- Decide whether `symbolKinds` filtering happens pre- or post-collection; pre-collection is cheaper
  but post-collection makes the cross-scope type-collision check (which needs to see all kinds
  together to compare) simpler to implement correctly.
- Decide the loop-counter/LINQ-lambda exemption list's exact `SyntaxNode` detection rules with a few
  real examples (`foreach`, `for`, nested lambdas capturing an outer short name) before implementation
  — the exemption logic is the part most likely to need iteration once tested against real code.
- No regression fixture exists yet; would need a small fixture type with a shadowed variable, a
  type-differing cross-scope reuse pair, and a few vague/short names of different `SymbolKind`s.

## Status

Design proposal only - not yet implemented. Written in response to a user question about how to
audit identifier clarity across the codebase; no PlanStepRunner run or existing bug motivates it.
