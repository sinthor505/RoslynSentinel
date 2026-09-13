# ConvertOutParamsToValueTuple: replace string-built tuple syntax with SyntaxFactory nodes (design proposal)

## Motivation

`OutParamRefactoringEngine.ConvertOutParamsToValueTupleAsync`
(`RoslynSentinel.Advanced/OutParamRefactoringEngine.cs:30-307`) already does the *composition*
correctly — it uses `DocumentEditor` and `SymbolFinder.FindReferencesAsync` (both public APIs),
not the string-reparse-whole-document anti-pattern `ChangeSignatureAsync` has (see
`docs/current/proposal_changesignature_full_roslyn_service.md`). The defect here is narrower and
purely local: every *node* the method builds — the tuple return type, the rewritten call-site
statement, the rewritten `return` statement — is built by string-interpolating C# source text and
handing it to `SyntaxFactory.ParseTypeName`/`ParseStatement`, instead of constructing the
corresponding syntax node directly. This is the "Duplicate (avoidable)" category from
`docs/current/roslyn-duplication-audit-v1.md`'s legend: a public Roslyn API
(`SyntaxFactory.TupleType`, `TupleExpression`, `ParenthesizedVariableDesignation`) exists and can
directly replace the hand-rolled string-then-parse logic, unlike the `ChangeSignature`/
`ExtractMethodSafe`/inline-family findings in that audit, which are internal-only dead ends.

Reparsing strings for tree construction is fragile in a specific, demonstrable way, not just
stylistically undesirable — see "Concrete bugs" below.

## Where the hand-rolling is (all in `OutParamRefactoringEngine.cs`)

1. **Tuple return type** (lines ~94-114): `outElements` is a sequence of `"{type} {name}"`
   strings, joined and wrapped into `"({...})"` or `"Task<({...})>"` text, then
   `SyntaxFactory.ParseTypeName(tupleType)`. Should be `SyntaxFactory.TupleType(...)` built from
   `TupleElementSyntax` nodes, wrapped in `GenericNameSyntax`("Task") when async.
2. **Standalone-call rewrite** (lines ~224-227): `SyntaxFactory.ParseStatement($"var {tupleVars} = {newInvocation.ToFullString()};")`.
   Should be a `LocalDeclarationStatementSyntax` with a `VariableDeclaration` whose designation is
   a `ParenthesizedVariableDesignationSyntax` (for `var (x, y) = ...`), and whose initializer is
   `newInvocation` directly (no `ToFullString()` round-trip needed at all — it's already a node).
3. **Local-declaration-assignment rewrite** (lines ~237-240) and **plain-assignment rewrite**
   (lines ~251-254): same pattern, same fix.
4. **Return-statement rewrite**, `ReturnRewriter.VisitReturnStatement` (lines ~343-370):
   `SyntaxFactory.ParseStatement($"return {tupleArgs};\n")` where `tupleArgs` is a string-joined
   tuple. Should be `SyntaxFactory.ReturnStatement(SyntaxFactory.TupleExpression(...))`.
5. **Trailing-return injection** (lines ~256-263 in `RewriteMethodBody`) and **local-variable
   scaffolding** (lines ~245-250, `var {name} = default!;`): same string-then-parse pattern for
   simpler nodes; lower priority than 1-4 but same fix shape (`SyntaxFactory.ReturnStatement`,
   `SyntaxFactory.LocalDeclarationStatement`).

## Concrete bugs caused by the string-building, not just style

- **Nested-generic corruption**: line ~112,
  `originalReturn.Replace("Task<", "").TrimEnd('>')` — if the method's original return type is
  `Task<List<int>>`, this produces `List<int` (strips only the outer `Task<` textually, then trims
  a single trailing `>`), not `List<int>`. A `TypeSyntax`-based unwrap (`GenericNameSyntax.TypeArgumentList.Arguments[0]`
  when the symbol is `Task<T>`) has no such failure mode — it operates on the resolved type
  structure, not text.
- **Fragile `out T x` type-stripping**: line ~95,
  `p.Type?.ToString().Replace("out " , "")` — parameter `Type` never includes the `out` modifier
  text in the first place (that's a separate `Modifiers` token), so this `Replace` is a no-op
  today, but it signals the code was written without confidence in what the node actually
  contains — exactly the kind of thing direct node construction makes moot, since you'd read
  `p.Type` and use it as a `TypeSyntax` rather than reasoning about its printed text.
- **Silent whitespace/formatting drift**: `ParseStatement` on interpolated text produces its own
  default trivia, then the code re-attaches `WithLeadingTrivia(statement.GetLeadingTrivia())` /
  `WithTrailingTrivia(...)` as a patch — this is the same trivia-preservation failure shape already
  seen in `ReplaceSnippet` (`docs/current/...replacesnippet_silent_splice_corruption...`, see memory
  `[[project_replacesnippet_silent_splice_corruption_adjacent_lines]]`) and in `Member`'s
  blank-line-dropping bug (`[[project_member_replace_drops_leading_blank_line_and_verify_gap]]`).
  Direct node construction inherits trivia correctly from its constituent parts without needing a
  manual leading/trailing patch step.
- **No escaping/validity guard**: if a variable name or type ever contains something
  interpolation-unsafe (unlikely for identifiers sourced from existing syntax, but not structurally
  prevented), `ParseStatement`/`ParseTypeName` fails by producing a syntax error node rather than a
  compile-time-checked construction failure. Direct `SyntaxFactory` calls only accept typed
  arguments (`SyntaxToken`, `TypeSyntax`, `ExpressionSyntax`), so a malformed identifier is caught
  as an API misuse, not embedded silently into re-parsed text.

## Proposed approach

Replace each of the four hot spots above with direct node construction. Sketch (not final code):

**Tuple type** (replaces string-building of `tupleType`):
```csharp
var elements = outParams.Select(p => SyntaxFactory.TupleElement(
    UnwrapType(p.Type), SyntaxFactory.Identifier(p.Identifier.Text)));
if (!isVoid) elements = elements.Prepend(SyntaxFactory.TupleElement(returnTypeSyntax, SyntaxFactory.Identifier("result")));
TypeSyntax tupleType = SyntaxFactory.TupleType(SyntaxFactory.SeparatedList(elements));
if (isAsync) tupleType = SyntaxFactory.GenericName("Task").WithTypeArgumentList(
    SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(tupleType)));
```
`UnwrapType`/`returnTypeSyntax` resolve the async case via the *symbol* (`methodSymbol.ReturnType`
as `INamedTypeSymbol`, take `TypeArguments[0]` and re-derive a `TypeSyntax` via
`SyntaxGenerator.TypeExpression(typeSymbol)`) rather than string-stripping `Task<`.

**Call-site tuple-deconstruction assignment** (replaces all three `var {tupleVars} = ...` sites,
which differ only in what the first designation name is):
```csharp
var designations = outVarNames.Select(n => (VariableDesignationSyntax)SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(n)));
if (!isVoid) designations = designations.Prepend(SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(firstName)));
var designation = SyntaxFactory.ParenthesizedVariableDesignation(SyntaxFactory.SeparatedList(designations));
var declaration = SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
    .WithVariables(SyntaxFactory.SingletonSeparatedList(
        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(default)) // deconstruction uses the designation, not a plain declarator name
    ));
// Roslyn's actual shape for `var (a, b) = expr;` is VariableDeclaration with a
// DeclarationExpressionSyntax-style designation on the declarator — verify exact node shape
// against a working sample during implementation (see Open items).
```
This sketch's exact node wiring for `var (a, b) = expr;` needs verifying against Roslyn's actual
tree shape during implementation (tuple deconstruction declarations have a slightly unusual
`VariableDeclaration`/designation relationship) — flagged as an open item rather than guessed
further here. The three current call sites collapse into one shared helper once this is right,
since they only differ in the first tuple-slot name (`_`, existing assigned variable, or the
original `bool`-returning variable) and whether the assignment target already exists vs. is a new
`var` declaration.

**Return statement** (`ReturnRewriter`):
```csharp
var args = outParamNames.Select(n => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(n)));
if (!isVoid) args = args.Prepend(SyntaxFactory.Argument(node.Expression ?? SyntaxFactory.IdentifierName("result")));
var tuple = SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(args));
return SyntaxFactory.ReturnStatement(tuple)
    .WithLeadingTrivia(node.GetLeadingTrivia())
    .WithTrailingTrivia(node.GetTrailingTrivia());
```
This one is straightforward — `TupleExpression` takes `ArgumentSyntax` elements, and
`node.Expression` is already a live `ExpressionSyntax`, so no round-trip through text at all.

## Scope

Fix the four sites above (tuple return type, 3x call-site rewrite, return-statement rewrite). The
lower-priority local-variable-scaffolding (`var {name} = default!;`) and trailing-return-injection
string builds in `RewriteMethodBody` can be folded in at the same time since they're the same
`SyntaxFactory.ReturnStatement`/`LocalDeclarationStatement` shapes, but are not the primary
motivation (no corruption bug identified there, unlike the `Task<` unwrap).

Out of scope: the "complex usage" fallback path (lines ~278-286, TODO-comment + best-effort arg
rewrite) — that path already avoids string-based statement reconstruction and only rewrites the
argument list via node operations; no change needed.

## Test impact

No existing test list was located specific to this engine in this pass — implementation should
locate and confirm current `ConvertOutParamsToValueTuple*` test coverage before changing behavior,
and add a regression case for the `Task<List<int>>` nested-generic scenario described above (should
produce `Task<(List<int> result, ...)>`, not today's silently-wrong `List<int` fragment).

## Open items for implementation

- **Exact node shape for `var (a, b) = expr;`.** Confirm via a scratch sample (or existing Roslyn
  usage elsewhere in the solution, if any) how `VariableDeclaration` + parenthesized designation
  compose for a tuple-deconstruction declaration — the sketch above flags this as unverified.
- **`UnwrapType` for the async-return case.** Decide whether to resolve via `IMethodSymbol.ReturnType`
  (semantic, robust) or via syntactic descent into `GenericNameSyntax.TypeArgumentList` on
  `methodDecl.ReturnType` (avoids re-deriving a `TypeSyntax` from a symbol via `SyntaxGenerator`).
  Semantic is likely more robust (works even if the source used a type alias or `var`-like
  inference is somehow in play) but needs `SyntaxGenerator.TypeExpression(typeSymbol)` to convert
  back to syntax for embedding in the new declaration.
- **Shared helper vs. three separate call sites.** Whether to unify the three near-identical
  call-site rewrite branches into one helper taking "first tuple-slot expression or none" now that
  they're built from nodes instead of format strings — likely yes, since the string-based versions
  only look distinct because of ad-hoc `tupleVars` string assembly, not because the underlying
  transforms differ.

## Status

**Proposed, not implemented.** No code changed as part of writing this proposal;
`OutParamRefactoringEngine.cs` is unchanged on disk.

## Related

- `docs/current/roslyn-duplication-audit-v1.md` — the broader audit this finding extends; see its
  "Duplicate (avoidable)" legend category and cross-reference note added there for this file.
- `docs/current/blockers/blocking_error_changesignature_internal_roslyn_api.md` and
  `docs/current/proposal_changesignature_full_roslyn_service.md` — the audit's template case for
  hand-rolled refactoring logic; unlike that case, this one has no internal-API wall and is
  directly fixable with public `SyntaxFactory`/`SyntaxGenerator` APIs.
- Trivia-preservation precedent: `[[project_replacesnippet_silent_splice_corruption_adjacent_lines]]`,
  `[[project_member_replace_drops_leading_blank_line_and_verify_gap]]` (auto-memory) — same failure
  family (patchwork trivia reattachment after text-based reconstruction) as items 2-4 above.
