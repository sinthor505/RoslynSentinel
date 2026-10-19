# Redesign InlineMethod so it substitutes arguments, parenthesizes, and picks the right overload

**Status:** PROPOSED 2026-10-19. Findings and open decisions only; no design chosen, awaiting a design session with the owner.

## Motivation

`Inline(kind: method)` rewrites call sites without reading their arguments and without regard to
operator precedence, and it chooses which method to inline by name alone. Two concrete defects, both
confirmed by reading the code on 2026-10-19 (line numbers drift; re-check before quoting):

- **Defect A (no parentheses, no parameter substitution).** `Calc.Answer( ) * 2` with body `40 + 2`
  becomes `40 + 2 * 2`, a silent change of meaning. The pin test for it asserts the wrong output as
  correct: `RoslynSentinel.Tests.Advanced/AdvancedStructuralNormalizeCharacterizationTests.cs:490`
  (`"        public int Get() => 40 + 2 * 2;"`, run for LF, CRLF and Mixed line endings). A parameterised
  method's body is pasted with its parameter names unbound: `Double(value) + 5` with `Double(int x) => x * 2`
  becomes `x * 2 + 5` (`BugFixTests.cs:3334-3350` asserts only that the text `x * 2` appears, so it pins
  the unbound name).
- **Defect B (overload chosen by name).** The engine picks the first `MethodDeclarationSyntax` in the file
  whose identifier text equals `methodName`
  (`RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs:1571`), then, after rewriting call sites,
  removes the first same-named method from the rewritten root (`:1639`). Line 1639 re-finds by name; it
  does not reuse `methodSymbol`. With several same-named methods the first one wins, for both the template
  and the removal. The tool receives only `filePath` and `targetName`
  (`RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs:1121-1131`), so no call site or signature
  reaches the engine and "the overload this call resolves to" cannot be implemented without a new
  discriminator.

This is an environment defect, not a caller error: the tool description says only "expression-body/single-return
only" (`AdvancedRefactoringTools.cs:1121`) and gives a caller no way to know that arguments are ignored or
that overloads are collapsed. Origin: logged as a noticed-not-fixed defect in the Whitespace initiative
follow-ups in `docs/current/TODO.md`.

### Current algorithm (verified)

`StructuralRefactoringEngine.InlineMethodAsync` (`AdvancedStructuralEngine.cs:1555-1653`, the only implementation):

1. `:1571` first same-named method in the given file.
2. `:1578-1586` template is the expression body or the single `return` expression; anything else is refused
   with `ToolNotFoundException` (`:1589`).
3. `:1599` `SymbolFinder.FindReferencesAsync(methodSymbol)`, grouped per document.
4. `:1621` each location maps to `FindNode(span).AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault()`.
5. `:1634` `docRoot.ReplaceNodes(callSites, ...)` swaps each call for the same template node,
   `WithTriviaFrom(original)` plus `Formatter.Annotation`. Arguments are never read.
6. `:1639-1642` removes the first same-named method from the defining root with `RemoveMemberDeclaration`.
7. `FormatAnnotatedTextAsync`, then the tool calls `ValidateAndApplyAsync`
   (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:18`), the compile gate.

The compile gate runs only in the tool path (`AdvancedRefactoringTools.cs:1132`). Engine-level tests call
`InlineMethodAsync` directly and never reach it.

### Further hazards (code-reading hypotheses, no repro yet)

- **Non-invocation references.** Step 4 takes the nearest enclosing `InvocationExpressionSyntax` whatever
  the reference is. A method-group reference such as `Foo(Answer)` would match the OUTER `Foo(...)` call
  and replace it with the template. Hypothesis: not reproduced.
- **Removal despite surviving references.** `:1642` removes the method even if some references were not
  replaced (the same non-invocation case, or a reference with no enclosing invocation).
- **Unguarded shapes today** (nothing in the engine checks any of these): `ref`/`out`/`in` parameters;
  `params`, optional and named arguments; side-effecting arguments when the parameter is used zero times or
  several times; parameters captured by lambdas; `this`, instance or private members in the template;
  unqualified names and `using` directives when the call site is in another file or namespace;
  self-recursion; async and generic methods; `virtual`/`override`/interface implementations.

### Why the compile gate is not enough

The gate would likely catch an unbound parameter name (an undefined identifier is a compile error) in the
tool path. It cannot catch a precedence change (`40 + 2 * 2` compiles), an argument evaluated zero or two
times, or a method group swallowed into an outer call that still compiles. Treat the gate as a partial
net for Defect A's second half only.

## Proposal

Nothing here is decided. This section lists what a design must settle and the building blocks available.

### Available Roslyn building blocks

- `semanticModel.GetOperation(invocation) as IInvocationOperation`: `Arguments` resolve named, optional
  and `params` arguments to their `IParameterSymbol`, so mapping argument to parameter is not syntactic guesswork.
- A `CSharpSyntaxRewriter` over the template's identifiers, resolved via `GetSymbolInfo` to parameter
  symbols, to substitute argument expressions.
- `SyntaxFactory.ParenthesizedExpression` for wrapping.
- The public `Simplifier` probably does not strip needless parentheses (unverified; check before relying on it
  either way), so the engine likely needs its own parent-aware "needs parentheses" test.
- The template must be bound with the DEFINING document's semantic model, not the call site's.

### Decision 1 - parameterised methods

- **1a. Refuse parameterised methods outright.** Cheapest. Leaves only parent-aware parenthesization.
  Removes the whole ref/out/params/optional/named/side-effect rule set from scope. Cost: `BugFixTests.cs:3334-3350`
  and `BUG_69_InlineMethod_SingleStatementMethod_NoServerCrash` (`BugFixTests.cs:2704`, `Double(int x)`) change
  from success to refusal.
- **1b. Full substitution.** Needs rules for: `ref`/`out`/`in`; `params` expansion; optional defaults
  (the default value must be materialised); named arguments in any order; an argument with side effects
  used zero or several times (spill to a local, or refuse); parameter captured by a lambda;
  parameter reassigned in the body (not possible for a single expression, but possible for an expression
  containing an assignment). Much larger test matrix.
- **1c. Middle path (open).** Substitute only when each parameter is used exactly once or the argument is a
  side-effect-free simple name or literal, refuse otherwise. The boundary of "simple" is itself undecided.

### Decision 2 - overload selection

- **2a. New discriminator parameter** on the `Inline` tool (a parameter signature, or a line number). This
  is a public API shape change. CLAUDE.md prefers a mandatory parameter when it closes a known footgun;
  a mandatory parameter would break every existing caller that inlines a non-overloaded method, so a design
  would likely need it mandatory only when the name is ambiguous. Whether the schema can express that is open
  (see the `CONDITIONAL-PARAM-REVIEW-REQUIRED` comment already on the tool at `AdvancedRefactoringTools.cs:1120`).
- **2b. Inline every inlinable overload.** Each call site uses its own overload's template. Larger blast radius;
  surprising if the caller meant one.
- **2c. Refuse when more than one method matches and list the signatures** in the error so the caller can
  retry with a discriminator (pairs with 2a) or give up. No signature change by itself.
- Regardless of the choice, the engine should carry the resolved `IMethodSymbol` through to removal instead
  of re-finding by name at `:1639`.

### Decision 3 - which shapes to refuse, and the supported-body scope

Candidates to refuse with a structured, actionable error (per CLAUDE.md, no raw exceptions): self-recursion;
templates using `this`, instance or private members from a call site outside the type; virtual, override and
interface-implementing methods; async and generic methods; non-invocation references (method group,
`nameof`, delegate creation); cross-file private or internal members; unqualified names that do not resolve
at the call site. Scope of supported bodies (expression body and single `return` today) is also open:
widen, or keep.

### Decision 4 - existing pins that encode the defect

- `AdvancedStructuralNormalizeCharacterizationTests.cs:490` expects `40 + 2 * 2` for LF, CRLF and Mixed. It
  must change to the parenthesized form (`(40 + 2) * 2`), or the test input must change if the owner prefers
  to keep the byte-exact pin for a different case.
- `AdvancedStructuralNormalizeCharacterizationTests.cs:432-495`, same test, `Calc.cs` expectation
  `return 40 + 2 + 1;` is arithmetically fine (left-associative `+`), but a blanket "always parenthesize"
  rule would change it to `(40 + 2) + 1`. The rule must be parent-aware.
- `BugFixTests.cs:3334-3350` pins unsubstituted parameters (asserts `x * 2`).
- `BugFixTests.cs:2724` and `:3354` pin refusal of non-expression bodies; keep.
- `RefactoringTests.cs:165` (`GetTen` block-bodied single return, `var x = 10;`) and
  `BatteryTwentyFourTests.cs:1315` (tool-level, asserts only non-null for `GetLabel` in `SimpleSource`; I did
  not read `SimpleSource`, so it is unknown whether `GetLabel` is parameterised) should be checked against
  whichever Decision 1 outcome is chosen.

### Candidate minimal slice (option, not a decision)

If the owner chooses 1a + 2c + parent-aware parenthesize + guard non-invocation references: engine change
plus two or three test files, no tool signature change. The reviewer's judgement was that this is probably
simple. It does not fix overload SELECTION (it only refuses ambiguity), and it makes the tool refuse
cases it silently half-handles today.

## Alternatives considered, not pursued

None built or tried. Options 1a-1c and 2a-2c above are the alternatives under consideration; none has been
rejected yet. Not-yet-considered: replacing the engine with Roslyn's internal inline-method refactoring
(not in the public Roslyn packages as far as this investigation checked; unverified) and routing the tool
through a different refactoring provider.

## Open questions

- Everything under Decisions 1-4 is open.
- Does the public `Simplifier` strip redundant parentheses added by the engine? Unverified.
- Is the method-group hazard (`Foo(Answer)`) real? Code-reading hypothesis; write the failing test first.
- Does the tool schema allow a discriminator that is mandatory only on ambiguity?
- Should `dryRun` output name any call sites that were skipped, so the caller sees a partial inline
  rather than a silent one?
- Is the refusal of partially-replaceable methods (some references not inlinable) all-or-nothing? The
  current behaviour (remove the method regardless) is unsafe; the safe default is refuse, but that is not decided.

## Cost / risk

- **Touches:** `AdvancedStructuralEngine.cs` `InlineMethodAsync`; possibly `AdvancedRefactoringTools.cs`
  `InlineSymbolTool` (description and, under 2a, a parameter); tests in
  `AdvancedStructuralNormalizeCharacterizationTests.cs`, `BugFixTests.cs`, `RefactoringTests.cs`,
  `BatteryTwentyFourTests.cs`. `ValidateAndApplyHelper.cs` is unchanged.
- **Breaks:** the pins listed under Decision 4 change deliberately; any caller that relied on the unbound or
  unparenthesized output (none known) changes behaviour. Under 1a, parameterised-method inlining that
  "worked" (and produced unbound names) becomes a refusal.
- **Makes harder:** under 2a, a public schema change must be registered in the tool-schema token budget
  (see `proposal_reduce_tool_schema_token_cost.md`); under 1b, a large rule matrix to maintain.
- **Coverage risk:** engine-level tests bypass the compile gate, and the one tool-level test asserts only
  non-null. Per the role split: the planner of the redesign must specify the cases a test has to cover
  (precedence cases per parent node kind, overload cases, non-invocation references, removal-with-survivors);
  the reviewer confirms each named case exercises the changed path and not just a shared helper; an
  implementer reaching green on the tests it is given proves nothing about uncovered cases. No existing test
  covers overloads, method groups, or more than one parameter use.
- **Process:** per CLAUDE.md, implementation goes to a dispatched implementer; this doc is for design only.
  A pin change must land with the fix, not before it.

## Related

- `docs/current/TODO.md`, Whitespace initiative follow-ups entry ("InlineMethod neither substitutes
  parameters nor adds parentheses").
- `docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md` (the initiative
  that surfaced this and that owns the byte-exact pins).
- `docs/current/proposals/proposal_reduce_tool_schema_token_cost.md` (schema cost of any new parameter).
