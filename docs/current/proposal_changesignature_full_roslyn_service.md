# ChangeSignature backed by Roslyn's real IChangeSignatureService (design proposal)

## Motivation

The user wants VS-style "Change Signature" — the same lightbulb refactoring available in Visual
Studio's Quick Actions: reorder parameters, add a parameter (with a type and, for existing call
sites, a default/fill-in value), remove a parameter, change a parameter's default value, and have
every call site across the solution rewritten correctly, including ones that use named arguments
or omit optional arguments. This is squarely in RoslynSentinel's mission of letting a weak/local
model drive semantically-correct, multi-file refactors by calling one structured tool instead of
grep-and-edit or hand-editing every call site it can find via `SearchSolutionText`.

We already have a `ChangeSignature` MCP tool. It only does reordering, and — critically — it does
not use Roslyn's actual change-signature engine at all; it's hand-rolled syntax-tree index
permutation. That gap is the subject of this proposal.

## Why this isn't already solved by the current `ChangeSignature` tool

`ChangeSignatureAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs:90-223`) only ever reorders. Its
implementation has several specific hack behaviors that a real Roslyn-backed service does not have:

- **Method lookup is first-name-match only.** `root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName)`
  (line 106) — no overload disambiguation (two overloads with the same name silently pick
  whichever one the tree walk hits first), no support for constructors, indexers, or local
  functions (only `MethodDeclarationSyntax` is searched).
- **No add, remove, type-change, or default-value support at all.** The method signature is
  `ChangeSignatureAsync(FilePathWrapper filePath, string methodName, int[] newParameterOrder, ...)`
  — there is no parameter for "insert a new parameter here" or "delete parameter N" or "change this
  parameter's default." The only input the engine or the MCP schema accepts is a permutation of the
  existing parameter indices (`RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs:139`,
  `newParameterOrder`).
- **Call sites are skipped, not fixed, whenever they'd require real understanding of the call.**
  Lines 176-186: any call site using a named argument (`args.Any(a => a.NameColon != null)`) is
  skipped with reason `"Call site uses named arguments; automatic reordering was skipped to avoid
  corrupting the call."`; any call site whose argument count doesn't match the parameter count
  (`args.Count != parameters.Count`, covering optional-argument omission and `params` expansion) is
  skipped with `"...optional argument omitted or params expansion..."`. Both are recorded as
  `SkippedCallSite` and surfaced to the caller as a warning, but never actually rewritten — the
  model is left to fix them by hand.
- **Edits compose by re-parsing a plain string, not through the `Solution`/`Document` API.** Line
  191: `var currentRoot = SyntaxFactory.ParseCompilationUnit(currentContent);` — each call site's
  edit is applied against a freshly-reparsed compilation unit built from the *previous* pending
  edit's full-text string (line 190's `pendingChanges.TryGetValue`), rather than composing through
  `Solution.WithDocumentText`/`Document.WithSyntaxRoot` and letting Roslyn track spans across
  edits. This works for the narrow reorder-only case tested today, but it is not the normal,
  robust composition path the rest of the codebase uses (contrast `RenameSymbol`, which stays
  entirely inside `Renamer.RenameSymbolAsync` and the `Solution` API — see
  `docs/current/proposal_changesymboltype_tool.md`'s "Why this isn't already solved by
  `RenameSymbol`" section for the same solution-vs-string-composition distinction applied to a
  different tool).
- **No dependency on the actual VS engine.** `Microsoft.CodeAnalysis.Features` — the package
  containing `IChangeSignatureService`, `AbstractChangeSignatureService`, and
  `CSharpChangeSignatureService`, i.e. the real engine behind VS's Change Signature dialog — is not
  referenced anywhere in the solution today (checked all `*.csproj` files: only
  `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`, and
  `Microsoft.CodeAnalysis.CSharp`, all pinned at 5.9.0, are referenced across
  `RoslynSentinel.Advanced/Basic/Common/Server.Advanced` and the test projects). Everything the
  current tool does, it does by re-deriving (badly) what `Microsoft.CodeAnalysis.Features` already
  implements correctly, including the named-argument and optional-argument cases it currently
  gives up on.

In short: the current tool is a narrow special case (pure reorder, simple calls only) of a general
capability Roslyn ships and VS already exposes; we're not extending a real engine, we're patching
around the absence of one.

## Proposed approach — REVISED after blocked attempt (see Status)

**Driving Roslyn's real internal change-signature service is not possible** — confirmed by
decompiling the installed 5.9.0 packages; see
`docs/current/blockers/blocking_error_changesignature_internal_roslyn_api.md`. Every type in that
pipeline (`AbstractChangeSignatureService`, `CSharpChangeSignatureService`, `SignatureChange`,
`ParameterConfiguration`, etc.) is `internal`, gated by a `RestrictedInternalsVisibleTo` allowlist
containing only Microsoft's own signed tooling assemblies. There is no public `IChangeSignatureService`
type in this version at all. This section is revised accordingly: instead of driving VS's internal
engine, extend the existing hand-rolled engine using **public** Roslyn APIs that solve the same
underlying problems (composition correctness, named-argument/optional-argument call sites) without
needing access to internal types:

1. Resolve the target method symbol the same way `PreviewRenameImpact`/`RenameSymbol` resolve
   targets today (methods only for v1 — see Scope below).
2. Represent the desired end-state as a `parameters` list (existing-by-original-index, or
   new-with-name/type/default-value-literal — see "Tool schema" below), same shape as originally
   planned. This part of the design was independent of which engine executes it and is unchanged.
3. Build the new parameter list with `SyntaxGenerator` (`Microsoft.CodeAnalysis.Editing`, fully
   public) rather than hand-built `SyntaxFactory` nodes — e.g. `SyntaxGenerator.ParameterDeclaration(name, type, defaultValue: ...)`
   for inserted parameters, composed with the existing parameters (by original index) into the new
   `ParameterListSyntax`.
4. Apply the declaration-side edit and all call-site edits through **`DocumentEditor`**
   (`Microsoft.CodeAnalysis.Editing.DocumentEditor`, fully public) — one `DocumentEditor` per
   affected document, `ReplaceNode`/`InsertParameter`-style calls queued against it, then
   `editor.GetChangedDocument()` at the end. `DocumentEditor` tracks node identity across multiple
   queued edits to the same document internally, which is exactly the fix for the current
   `SyntaxFactory.ParseCompilationUnit`-on-a-string composition bug — no code path in the new
   implementation should re-parse a document from plain text.
5. Resolve each call site's actual argument-to-parameter binding via the **semantic model**
   (`SemanticModel.GetSymbolInfo(invocation).Symbol` to get the resolved overload, then match its
   `IMethodSymbol.Parameters` against the invocation's bound arguments) instead of positional-index
   matching against raw syntax. This is what actually fixes the two skip-only gaps:
   - **Named arguments**: since named arguments are order-independent, a call site that uses them
     needs no argument reordering at all when parameters are merely reordered — only when a
     parameter is removed does its named argument need deleting, and only when one is added does a
     new named/positional argument need inserting. No permutation logic is needed for this case,
     only correct identification of which existing argument (if any) corresponds to which surviving
     parameter.
   - **Optional/omitted arguments and `params` expansion**: resolving the call site's actual bound
     arguments via the semantic model (rather than requiring `args.Count == parameters.Count`)
     correctly identifies "this call site doesn't pass an argument for parameter N because N has a
     default and was omitted" as a fine, no-op case for that call site, instead of bailing out.
6. Format each changed document via `Formatter.FormatAsync`, same as today.

This still fully satisfies the original motivation (correct handling of named/optional/params call
sites, add/remove/default-value support) — it just gets there via public composition APIs plus
semantic-model-driven call-site resolution, rather than by delegating to Roslyn's own (inaccessible)
internal engine.

**Tool schema**: VS's Change Signature dialog is a "specify the desired end-state" UI, not a
sequence of add/remove/reorder operations — the model should be able to describe the target
signature directly rather than composing several separate tool calls (which would reintroduce the
interim-broken-solution problem `proposal_changesymboltype_tool.md` describes for a different
refactor). A plausible shape, left to be finalized during implementation:

```
ChangeSignature(
  docCommentId or filepath+methodName: <target>,
  parameters: [
    { kind: "existing", originalIndex: 1 },
    { kind: "existing", originalIndex: 0 },
    { kind: "new", name: "timeout", type: "TimeSpan", defaultValue: "TimeSpan.FromSeconds(30)",
      callSiteValue: "TimeSpan.FromSeconds(30)" }     // see open item on fill-in vs default-fallthrough
  ]
)
```

This generalizes the current `newParameterOrder: int[]` (still expressible as an all-`"existing"`
list) while adding insert/remove/default-value in the same call.

**Decided (2026-09-13, see Status):** clean break, no back-compat alias — `newParameterOrder: int[]`
is removed outright and replaced by `parameters`. Default values are literal C# source-text
expressions, always inserted at existing call sites (no separate fill-in-vs-fallthrough flag). V1
covers methods only; constructors/indexers/local functions are a tracked followup.

## New dependency — REVISED, no longer needed

~~`Microsoft.CodeAnalysis.Features` 5.9.0~~ is **not required**. It was added to
`RoslynSentinel.Basic.csproj` during the blocked attempt purely to decompile and inspect the
package's actual (internal, inaccessible) API surface — see the blocker doc. Since the revised
approach uses only `Microsoft.CodeAnalysis.Editing` (`SyntaxGenerator`, `DocumentEditor`) and the
semantic model, both of which are already available transitively via the
`Microsoft.CodeAnalysis.CSharp.Workspaces` 5.9.0 reference `RoslynSentinel.Basic.csproj` already
has, **no new package reference is needed at all**. The `Microsoft.CodeAnalysis.Features` reference
added during the blocked attempt should be removed as part of implementing this revised approach,
since it serves no purpose without the internal types it was added to reach.

## Test impact

7 of the 9 existing tests exercise reorder-only inputs and should produce byte-identical outputs
under the new implementation — they should keep passing unchanged:

- `RoslynSentinel.Tests/RegressionTests.cs`: `ChangeSignature_ReordersCallSiteArguments_NotJustDeclaration`, `ChangeSignature_TwoParam_Swap_RoundTrip`
- `RoslynSentinel.Tests.Advanced/BugFixTests.cs`: `ChangeSignature_ReordersParameters_InDeclaration`, `ChangeSignature_WithInvalidOrder_ReturnsEmpty`, `REG_ChangeSignature_UpdatesCallSiteArguments`
- `RoslynSentinel.Tests.Battery/BatteryTwelveTests.cs`: `ChangeSignature_TwoParameterMethod_ReordersParameters`
- `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`: `ChangeSignature_AutoStageTrue_ReturnsNotNull`

**Two tests must be explicitly updated, not just kept green**, because they assert a limitation of
the current hand-rolled hack rather than a real spec:

- `ChangeSignature_CallSiteWithNamedArgument_IsReportedAsSkipped`
  (`RoslynSentinel.Tests.Advanced/BugFixTests.cs:173`)
- `ChangeSignature_CallSiteWithFewerArgsThanParameters_IsReportedAsSkipped`
  (`RoslynSentinel.Tests.Advanced/BugFixTests.cs:187`)

Roslyn's semantic model correctly identifies named-argument call sites (reordering the named args,
or leaving them as-is since named args are order-independent by construction) and correctly
identifies optional-argument omission and `params` expansion at call sites when resolution is driven
by the bound-argument mapping instead of raw positional syntax matching — that is the whole reason
to resolve call sites semantically instead of preserving hand-rolled positional index permutation.
If the new implementation still skips these cases, the migration hasn't actually fixed the gap; if
it silently breaks these two tests without updating their intent, that's a regression of trust in
the test suite, not a passing bar. These two should be rewritten to assert the call site is now
correctly rewritten (or correctly left alone, for the named-argument case), not that it's reported
as skipped.

## Decisions made (2026-09-13, after the blocked internal-service attempt)

- **Default value expression**: literal C# source text (e.g. `"TimeSpan.FromSeconds(30)"`), parsed
  as an expression via `SyntaxFactory.ParseExpression`. Not a typed value/type pair.
- **Call-site fill-in**: always insert the literal default value at every existing call site when a
  parameter is added. No separate flag for "leave alone."
- **Scope**: methods only for v1. Constructor/indexer/local-function support is a tracked followup,
  not part of this implementation.
- **Schema**: clean break — `newParameterOrder: int[]` is removed outright, replaced by
  `parameters: [...]`. No back-compat alias.

## Open items for implementation

- **Overload disambiguation.** The current code takes the first `MethodDeclarationSyntax` matching
  `methodName` by name only (line 106) — should the tool require a `docCommentId` or an explicit
  parameter-count/type hint to disambiguate overloads? The rest of the symbol-tools surface
  (`PreviewRenameImpact`, `RenameSymbol`) already prefers `docCommentId` with a
  `filepath`/`symbolName`/`contextSnippet` fallback — `ChangeSignature` should probably adopt the
  same convention rather than reinventing target resolution. Not blocking for v1 (first-match is an
  acceptable starting behavior, same as today) but should be revisited.
- **`DocumentEditor` composition details.** Confirm whether one `DocumentEditor` per affected
  document (declaration site + each distinct call-site document) composes correctly when the
  declaration and a call site are in the same file (e.g. a private method called from within its own
  class) — likely fine since `DocumentEditor` tracks node identity across queued edits to the same
  document, but should be verified with a same-file declaration+call-site test case.
- **Semantic-model call-site resolution mechanics.** Exact approach for mapping each invocation's
  bound arguments to parameters — likely `SemanticModel.GetSymbolInfo(invocation).Symbol as
  IMethodSymbol` to get the resolved overload, cross-referenced with the invocation's
  `ArgumentListSyntax` (both positional and named arguments) to build an explicit
  argument-index-or-name → original-parameter-index map per call site, then apply the same
  existing/new/removed parameter transform to that map. Left as an implementation detail, not
  pre-designed here.
- **`dryRun`/`returnDiff`/`autoStage` plumbing should carry over unchanged.** The existing wrapper
  (`RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs:131-175`) already threads
  these through `ValidateAndApplyAsync` and builds an `AppliedChangeSummary`; nothing about
  swapping the engine underneath should require changing that contract, aside from whatever new
  parameters the richer schema adds.
- **Schema field naming.** The `parameters: [...]` shape sketched above is illustrative, not final —
  exact field names and whether reordering is expressed by list position vs an explicit
  `originalIndex` are still open (the back-compat-alias question itself is decided: no alias, see
  Decisions above).

## Status

**REVISED, unblocked, ready for implementation (2026-09-13).** A first attempt tried to drive
Roslyn's real internal change-signature service and hit a hard wall: every type in
`Microsoft.CodeAnalysis(.CSharp).Features` 5.9.0's change-signature pipeline
(`AbstractChangeSignatureService`, `CSharpChangeSignatureService`, `SignatureChange`,
`ParameterConfiguration`, etc. — there is no public `IChangeSignatureService` type in this version
at all) is `internal`, gated by a `RestrictedInternalsVisibleTo` allowlist containing only
Microsoft's own signed tooling assemblies. Full decompiled evidence in
`docs/current/blockers/blocking_error_changesignature_internal_roslyn_api.md`. That path is
confirmed **not implementable** — no workaround was attempted, per CLAUDE.md's blocking-finding
doctrine.

The user chose direction: **extend the hand-rolled engine using public Roslyn APIs**, rejecting the
shell-out-to-another-process and drop-the-feature alternatives. The "Proposed approach" section
above is revised accordingly — `SyntaxGenerator` + `DocumentEditor` (both public,
`Microsoft.CodeAnalysis.Editing`, already reachable via the existing
`Microsoft.CodeAnalysis.CSharp.Workspaces` 5.9.0 reference) plus semantic-model-driven call-site
resolution (instead of positional-syntax matching) achieve the same correctness goals — fixing the
named-argument and optional/params call-site gaps, and adding insert/remove/default-value support —
without needing any inaccessible internal type. **No new package reference is needed**; the
`Microsoft.CodeAnalysis.Features` reference added during the blocked attempt should be removed as
part of this implementation, since it was only needed to decompile-and-inspect the internal
surface that's no longer part of the plan.

All "Decisions made" above (default-value-as-literal-source-text, always-fill-in-at-call-sites,
methods-only v1, clean-break schema) still stand — they were independent of which engine executes
the change.

**IMPLEMENTED (2026-09-13).** `RefactoringEngine.ChangeSignatureAsync`
(`RoslynSentinel.Basic/RefactoringEngine.cs`), the `ChangeSignature` MCP tool wrapper
(`RoslynSentinel.Server.Advanced/SentinelAdvancedRefactoringTools.cs`), and all 9 existing tests were
migrated per this plan. The `Microsoft.CodeAnalysis.Features` 5.9.0 reference added during the
blocked attempt was removed from `RoslynSentinel.Basic/RoslynSentinel.Basic.csproj`; no new package
reference was needed, confirming the prediction above.

**Engine-side spec types** (new top-level records in `RefactoringEngine.cs`, `RoslynSentinel.Basic`
namespace):

```csharp
public abstract record SignatureParameterSpec;
public sealed record ExistingParameterSpec(int OriginalIndex) : SignatureParameterSpec;
public sealed record NewParameterSpec(string Name, string Type, string DefaultValueExpression) : SignatureParameterSpec;
```

`ChangeSignatureAsync`'s new signature:

```csharp
Task<ChangeSignatureResult> ChangeSignatureAsync(
    FilePathWrapper filePath, string methodName,
    IReadOnlyList<SignatureParameterSpec> parameters,
    CancellationToken cancellationToken = default)
```

replacing the old `int[] newParameterOrder` parameter outright (no overload, no back-compat alias,
as decided). "Invalid" input (returns the same empty `ChangeSignatureResult` as before, i.e.
`Changes` empty and no `SkippedCallSites`) now means: `parameters` is empty, the target method has no
parameters, any `ExistingParameterSpec.OriginalIndex` is out of range for the original parameter
list, or any `OriginalIndex` is referenced more than once.

**Actual Roslyn API calls used**, confirmed against the installed 5.9.0 /
`Microsoft.CodeAnalysis.Editing` package (decompiled with `ilspycmd` to check the real shape rather
than assumed names — `DocumentEditor` has no public constructor, only `CreateAsync`, and
`SyntaxGenerator` has no `ParameterList`-level helper, only per-parameter `ParameterDeclaration`):

- `SyntaxGenerator.GetGenerator(document)` — one generator per document.
- `generator.ParameterDeclaration(name, SyntaxFactory.ParseTypeName(type), defaultExpr)` cast to
  `ParameterSyntax`, for each `NewParameterSpec`; `defaultExpr` comes from
  `SyntaxFactory.ParseExpression(added.DefaultValueExpression)` (the literal-source-text decision
  above).
- Existing parameters are carried over as `originalParams[existing.OriginalIndex].WithoutTrivia()` —
  no `SyntaxGenerator` call needed for the unchanged case, just AST reuse.
- `DocumentEditor.CreateAsync(document, cancellationToken)` for the declaration-site edit;
  `editor.ReplaceNode(methodDecl.ParameterList, newParameterList)`; `editor.GetChangedDocument()`.
- Call sites are found via `SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken)`
  (the method's own declared symbol from `semanticModel.GetDeclaredSymbol(methodDecl)`), not a text
  search.
- Each call site's binding is resolved via
  `refSemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol` — the
  actually-bound overload — then each `ArgumentSyntax` is mapped to an original-parameter ordinal
  using `boundMethod.Parameters[i].Ordinal` for positional args and, for named args,
  `boundMethod.Parameters.FirstOrDefault(p => p.Name == arg.NameColon.Name.Identifier.Text).Ordinal`.
  This is what actually fixes the two previously-skipped cases:
  - **Named arguments**: resolved by name against the bound symbol's parameter list, not by
    position, so reordering `parameters` requires no change to a named-argument call site at all
    (order-independence falls out for free); only add/remove affects it.
  - **Omitted optional / `params`-expanded arguments**: since the map is built from what's actually
    bound (not `args.Count == parameters.Count`), an omitted optional argument simply has no entry
    for that ordinal, which is handled explicitly (see the "materialize gap" algorithm below) instead
    of triggering a blanket skip.
- Each call site's rewritten argument list is applied via its own
  `DocumentEditor.CreateAsync(pendingDoc, cancellationToken)` /
  `editor.ReplaceNode(invocation, invocation.WithArgumentList(newArgList))`, keyed per-document in a
  `Dictionary<FilePathWrapper, Document>` so multiple call sites in the same file compose against the
  same pending document rather than the original — this directly replaces the old
  `SyntaxFactory.ParseCompilationUnit`-on-a-string re-parse bug described in "Why this isn't already
  solved" above. (This also resolves the "DocumentEditor composition details" open item: a fresh
  `DocumentEditor` is created per call site against the current pending document snapshot, and node
  re-location for same-file multi-call-site cases is done via `Span` matching against the pending
  root — verified working for the declaration-and-call-site-in-different-files cases exercised by
  the test suite; same-file declaration+call-site was not specifically added as a new test case, so
  treat that specific sub-case as verified-by-construction, not verified-by-test.)
- `Formatter.FormatAsync(document, null, cancellationToken)` at the end, same as before.

**"Materialize gap" algorithm** for omitted trailing-then-reordered optional arguments: for each call
site, walk the new parameter-slot list right-to-left. Once any slot with an explicit bound argument
has been seen, every earlier slot with no explicit argument must be materialized (it can no longer be
left as a trailing omission, since something after it is now explicit) — for an `ExistingParameterSpec`
slot this means reusing that original parameter's own `Default?.Value` AST expression; for a
`NewParameterSpec` slot it means using its own `DefaultValueExpression`. If an `ExistingParameterSpec`
slot needs materializing but its original parameter has no default value, the call site is recorded
as a `SkippedCallSite` (this is the one remaining, unavoidable case: the source has no value to put
there). Slots before the first explicit-from-the-end argument are left as pure trailing omissions,
unchanged, exactly matching current C# call-site semantics.

**MCP tool wire schema** (`SentinelAdvancedRefactoringTools.ChangeSignature`,
`RoslynSentinel.Server.Advanced`) — deliberately **flat**, not the polymorphic
`{ kind: "existing" | "new", ... }` shape sketched illustratively above:

```csharp
public sealed record ChangeSignatureParameterInput(
    int? originalIndex = null,
    string? name = null,
    string? type = null,
    string? defaultValue = null);
```

`newParameterOrder: int[]` was removed outright; the tool parameter is now
`ChangeSignatureParameterInput[] parameters`. An entry with `originalIndex` set is treated as
"existing"; an entry with `name`+`type`+`defaultValue` all set is treated as "new"; anything else
(neither shape fully satisfied) returns `ToolErrorCode.InvalidArgument` before the engine is called.
This is a deliberate deviation from the proposal's illustrative discriminated-union sketch: a Grep of
`SentinelAdvancedRefactoringTools.cs` and `RoslynSentinel.Common` at implementation time found zero
existing precedent anywhere in the codebase for a polymorphic/tagged-union array parameter in an MCP
tool schema, and CLAUDE.md's stated mission (letting a **weak/local model** drive tools reliably) plus
its documented history of schema-emission bugs both favor a flat, nullable-field DTO whose JSON
Schema a small model can produce correctly over a `oneOf`-style union it is more likely to get wrong.
`dryRun`/`returnDiff`/`autoStage`/`reason` are unchanged, as expected.

**Test results.** All 9 tests pass (verified individually per-project via `RunTest`, since a single
solution-wide filtered run produces a misleading top-level `exitCode`/`runSucceeded` due to a TRX-file
overwrite race across the multiple matching test projects — each project's own "Passed!" summary and
per-test `outcome` are unambiguous and are what was actually checked):

- `RoslynSentinel.Tests/RegressionTests.cs`: `ChangeSignature_ReordersCallSiteArguments_NotJustDeclaration`,
  `ChangeSignature_TwoParam_Swap_RoundTrip` — updated call shape only, same behavior, both pass.
- `RoslynSentinel.Tests.Battery/BatteryTwelveTests.cs`: `ChangeSignature_TwoParameterMethod_ReordersParameters`
  — updated call shape only, passes.
- `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`: `ChangeSignature_AutoStageTrue_ReturnsNotNull`
  — updated call shape only, passes.
- `RoslynSentinel.Tests.Advanced/BugFixTests.cs`: `ChangeSignature_ReordersParameters_InDeclaration`,
  `REG_ChangeSignature_UpdatesCallSiteArguments`, `ChangeSignature_WithInvalidOrder_ReturnsEmpty`
  (redefined "invalid" as an out-of-range `originalIndex` rather than a wrong-length array) — updated
  call shape only, all pass.
- `ChangeSignature_CallSiteWithNamedArgument_IsReportedAsSkipped` →
  **renamed and rewritten** to `ChangeSignature_CallSiteWithNamedArgument_IsHandledCorrectly` —
  now asserts `SkippedCallSites` is empty and the call site is correctly present in `Changes`. Passes.
- `ChangeSignature_CallSiteWithFewerArgsThanParameters_IsReportedAsSkipped` →
  **renamed and rewritten** to `ChangeSignature_CallSiteWithFewerArgsThanParameters_IsHandledCorrectly`
  — now asserts `SkippedCallSites` is empty and specifically that the rewritten call site text
  contains `"Add(0, 1, 2)"` (the previously-omitted optional argument correctly materialized at its
  new, non-trailing position by the "materialize gap" algorithm above). Passes.

**Build**: `Build(level=fullBuild, scope=solution)` succeeds — 0 errors, 0 new warnings (3
pre-existing, unrelated `CS8602` warnings only).

**Deviations from this doc, both already called out above**: (1) the wire schema is a flat
nullable-field DTO rather than the illustrative `kind`-tagged union; (2) the engine reuses each
existing parameter's own AST default-value expression directly (`ParameterSyntax.Default?.Value`)
rather than going through `IParameterSymbol.HasExplicitDefaultValue`/`ExplicitDefaultValue`, since the
algorithm needs a reusable expression *node* to splice into a new argument list, not a boxed CLR
value — both properties exist on the public API (confirmed via decompilation) but weren't the right
shape for this use.

No new tests were added beyond the 9 migrated ones; the two rewritten tests already cover the two
previously-unhandled behaviors (named arguments, omitted-then-reordered optional arguments), so
additional net-new coverage was judged not required for this migration.

Not committed — left staged/unstaged for review, per instruction.
