# Plan: Member view/replace/remove can target a local function

**Status:** APPROVED 2026-11-24 (owner; to be run in a fresh session with the implement-plan skill). Owner decisions A (ReadFile inherits view), B (containerName also names the enclosing method) and C (plan doc first) settled in session 30a40d85.

## Problem

`Member(operation: view|replace|remove, memberName: X)` cannot target a local function. All three resolve
through `SyntaxTargetResolver.ResolveCandidates` (`RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs:508`),
which enumerates only `MemberDeclarationSyntax` and `EnumMemberDeclarationSyntax`; there is a
`// TODO local functions are never candidates` at its top (INBOX line 2026-11-24 18:00). A caller with a
local-function name gets a not-found error. `GetMethodSource` already resolves a unique local function
itself (commit 5abddf52, finding source `LocalFunctionResolved`).

`ResolveCandidates` is meant as the one shared lookup that callers filter by `CandidateKind`, but its 49
callers (measured: `Search(mode: callers, query: ResolveCandidates, containingType: SyntaxTargetResolver)`,
7 files) filter in three ways: allow-lists (`Kind is Method or Constructor`) that would ignore a new kind;
about 12 deny-lists in `MemberRefactoringEngine` (`Kind is not (Class or Interface or Struct or Record or
Enum or EnumMember)`: modifiers, attributes, accessibility, replace, remove) that would let it through; and
unfiltered callers (`GetMemberSourceAsync`, `BuildContainerMissDetailCore`, `ResolveCandidatesWithSemanticAsync`,
several `SymbolNavigationEngine` lookups) whose counts and ambiguity decisions would change. So a new kind
must be opt-in.

Further blockers found by investigation:
- Replace: `ReplaceMemberAsync` (`MemberRefactoringEngine.cs:2525`) parses `newSource` with
  `SyntaxFactory.ParseMemberDeclaration`; `ReplaceMemberWithAdditionsAsync` (`:2640`) rejects a target that
  is not a `MemberDeclarationSyntax` inside a `TypeDeclarationSyntax`.
- Remove: `IsDeclarationStillPresentAsync` (`:3087`) repeats the lookup after removal; without the opt-in it
  would report "Verified" for a local function that survived. The tool-level precheck in
  `RefactoringStructuralImpl.MemberRemoveAsync` (`RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs:744`)
  calls the name-based `FindCallersAsync`/`FindImplementationsForMemberAsync`, which can match a
  same-named member elsewhere.

## Decision

- Add `CandidateKind.LocalFunction` and an opt-in `includeLocalFunctions` parameter (default `false`) to
  `ResolveCandidates`. All 49 existing callers stay byte-for-byte unchanged in behaviour; only Member
  view (`GetMemberSourceAsync`, so `ReadFile(memberName:)` too), replace and remove pass `true`.
- A real member always wins: after the container filter, `PreferMembersOverLocalFunctions` (new) drops
  local-function candidates when any non-local-function candidate remains.
- `containerName` for a local-function candidate matches its containing type (existing rule) or its
  enclosing member: `VerdictAsync` or `ImplementationScenarioChecks.VerdictAsync`.
- Several same-named local functions: the existing ambiguity path, with each candidate described as
  "local function inside Type.Member (lines S-E)" and a retry with `containerName` = the enclosing member.
- Replace parses `newSource` as one local function statement; replace-with-additions is refused for a
  local function. Remove checks usages semantically, and the post-remove check opts in.
- Edit-effect facts record a removed local function with a display name and no docCommentId (local
  functions have none); accepted.

## Execution rules

- Lanes and limits per `.claude/implementer-tiers.json` (depth tier D1: files<=3, edits<=15,
  acceptance<=1, toolcalls<=35). Implementers stop at a clean `Build`; the dispatcher commits each slice
  (`Plan: plan_member_local_function_targets` trailer) and runs the named test classes with
  `scripts/Invoke-WorktreeTests.ps1 ... -WithRatchets`.
- Existing test text must not change in Steps 1-4: the fallback applies only when no member matched, so
  existing not-found and ambiguity pins (`SymbolNavigationMissTests`, `MemberViewAmbiguityToolTests`,
  `GenericContainerNameTests`) stay green. A slice that needs one changed replies `RESCOPE:`.
- New tests go in the per-slice test files named below.

## Steps

### Step 1 - Resolver: opt-in LocalFunction kind, container match, precedence
- Files: `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs`, `RoslynSentinel.Tests.Basic/LocalFunctionCandidateTests.cs` (new)
- Depends on: none
- Effect: text-neutral (no caller opts in yet)
- Lane: `implementer` (D1)
- Change:
  - `CandidateKind`: add `LocalFunction` (the one switch over it, `SymbolNavigationEngine.cs:~3280`, has a `_ =>` arm).
  - `SyntaxNodeCandidate`: add optional trailing `string? EnclosingMemberName = null` (7 existing
    constructions, 6 in `GenericContainerNameTests.cs`, compile unchanged).
  - `ResolveCandidates(SyntaxNode root, SourceText sourceText, string name, CancellationToken cancellationToken = default)`
    gains `bool includeLocalFunctions = false` as the LAST parameter (after `cancellationToken`) so
    positional callers compile; callers pass it by name. When true, also add
    every `LocalFunctionStatementSyntax` whose `Identifier.Text == name` with kind `LocalFunction`,
    `ContainingTypeName` as today, and `EnclosingMemberName` = nearest ancestor `MemberDeclarationSyntax`
    name (via `GetMemberName`). Replace the `// TODO local functions are never candidates` line with a
    doc-comment sentence on the parameter.
  - `GetMemberName` (`:457`): add a `LocalFunctionStatementSyntax` case returning its identifier.
  - `CandidateMatchesContainer(SyntaxNodeCandidate candidate, string containerName)` (new, public static):
    for `LocalFunction` candidates true when `ContainingTypeMatches(candidate.ContainingTypeName, containerName)`
    or the last dotted segment equals `EnclosingMemberName` (and any prefix matches the type via
    `ContainingTypeMatches`); for other kinds it returns exactly what the current filters compute.
  - `FilterByContainingType` (`:108`) and `FilterByContainingTypeStrict` (`:279`): use
    `CandidateMatchesContainer` for `LocalFunction` candidates only; member candidates keep the current
    expressions unchanged.
  - `PreferMembersOverLocalFunctions(List<SyntaxNodeCandidate> candidates)` (new, public static): drop
    `LocalFunction` candidates when any other kind is present.
  - `BuildHintForCandidates` (`:390`) and `BuildOverloadHintForCandidates` (`:413`): for a `LocalFunction`
    candidate, describe it as `local function inside <Type>.<EnclosingMember> (lines S-E)` and offer a
    retry with `containerName` = `EnclosingMemberName`.
  - Tests (`LocalFunctionCandidateTests`, follow `GenericContainerNameTests` style): default call returns no
    local functions; opt-in returns one with `EnclosingMemberName`; `CandidateMatchesContainer` accepts
    `Outer`, `Type.Outer` and the type name; `PreferMembersOverLocalFunctions` drops the local function when a
    same-named method exists.
- Done when: `Build` 0 errors.

### Step 2 - View (and ReadFile memberName) opt in
- Files: `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs`, `RoslynSentinel.Tests.Tools.Basic/MemberLocalFunctionViewTests.cs` (new)
- Depends on: Step 1
- Pairs with: Step 3 (disjoint files)
- Effect: behaviour-changing: `Member(view, memberName: X)` and `ReadFile(memberName: X)` return a local
  function's source when no member is named X, with a `MemberSource` finding
- Lane: `implementer` (D1)
- Change: in `GetMemberSourceAsync` (`SymbolNavigationEngine.cs:3145`) pass `includeLocalFunctions: true`
  to the resolution that picks the target (not to the qualified-name probes at `:3165-3166`), apply
  `PreferMembersOverLocalFunctions` after the container filter, and when the chosen node is a local
  function set the returned message to `'X' is a local function inside <Type>.<EnclosingMember> (lines S-E).`
  (`MemberViewAsync` already turns the message into a `Finding("MemberSource", ...)`).
  Tests: unique local function returns its source and the note; a same-named method wins over a local
  function; two local functions without `containerName` -> ambiguity naming both enclosing members;
  `containerName: "<enclosing method>"` picks one; `ReadFile(memberName:)` returns the local function.
- Done when: `Build` 0 errors.

### Step 3 - Remove opts in, semantic usage check, post-check
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`, `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs`, `RoslynSentinel.Tests.Tools.Basic/MemberLocalFunctionRemoveTests.cs` (new)
- Depends on: Step 1
- Pairs with: Step 2
- Effect: behaviour-changing: `Member(remove, memberName: X)` removes an unused local function and refuses
  a used one
- Lane: `implementer` (D1)
- Change:
  - `RemoveMemberAsync` (`MemberRefactoringEngine.cs:2921`): opt in, `PreferMembersOverLocalFunctions`
    after the container filter. For a local-function target the usage check binds it
    (`semanticModel.GetDeclaredSymbol`) and refuses when it has references inside its enclosing member
    (`SymbolFinder.FindReferencesAsync` if it reports local-function references, else a bound
    identifier scan of the enclosing member; see Risks). Removal keeps `RemoveNodePreservingTriviaAsync`.
  - `IsDeclarationStillPresentAsync` (`:3087`): opt in and apply the same precedence.
  - `IsLocalFunctionRemovalTargetAsync(FilePathWrapper filePath, string memberName, string? contextSnippet, string? lineBefore, string? lineAfter, string? containerName, CancellationToken cancellationToken)` (new, beside `IsTypeRemovalTargetAsync` at `:3053`).
  - `MemberRemoveAsync` (`RefactoringStructuralImpl.cs:744`): skip the name-based caller/implementation
    precheck when `IsLocalFunctionRemovalTargetAsync` is true (comment: the engine's semantic check covers it).
  - Tests: unused local function removed and "Verified" message; used one refused naming the usage;
    same-named method elsewhere does not block removing the local function; a method named X wins over a
    local function X.
- Done when: `Build` 0 errors.

### Step 4 - Replace opts in, local-function parse branch
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`, `RoslynSentinel.Tests.Tools.Basic/MemberLocalFunctionReplaceTests.cs` (new)
- Depends on: Step 1, Step 3 (same engine file)
- Effect: behaviour-changing: `Member(replace, memberName: X)` replaces a local function
- Lane: `implementer` (D1)
- Change:
  - `ResolveReplaceTargetAsync` (`:2597`): opt in; `PreferMembersOverLocalFunctions` after `FilterByContainingType`.
  - `ReplaceMemberAsync` (`:2525`): when the target is a `LocalFunctionStatementSyntax`, parse `newSource`
    with `SyntaxFactory.ParseStatement`; it must be one `LocalFunctionStatementSyntax` without
    diagnostics, else `ToolInvalidArgumentException` stating that the target is a local function and the
    source must be one local function statement. Replace through
    `RoslynFormattingHelper.ReplaceNodeAndFormatReplacementAsync` (takes `SyntaxNode`), keeping the
    existing AlreadyInTargetState and message shape.
  - `ReplaceMemberWithAdditionsAsync` (`:2640`): for a local-function target, state that extra
    declarations cannot follow a local function (one local function statement only).
  - Tests: body change applied and re-indented; rename inside newSource that breaks a call site is
    rejected by the compile gate; a method-shaped source with `public` modifier is rejected with the
    local-function message; multi-declaration source refused.
- Done when: `Build` 0 errors.

### Step 5 - Tool parameter descriptions
- Files: `RoslynSentinel.Tools.Basic/RefactoringStructuralTools.cs`
- Depends on: Steps 2-4
- Effect: behaviour-changing (schema text)
- Lane: main session (one small edit) or `implementer` (D0)
- Change: `memberName` description `"The member to target. For overloads, combine with contextSnippet/lineBefore/lineAfter."`
  gains `" A local function matches when no member has that name."`; `containerName`
  `"The type that holds the member."` becomes `"The type that holds the member (for a local function, its type or enclosing method)."`.
  Regenerate `docs/generated/` tool tables if a generator covers descriptions.
- Done when: `Build` 0 errors and the schema-size ratchet/baseline tests pass (see Risks).

### Step 6 - Verify and close
- Files: none (plan doc status, INBOX line removal)
- Depends on: Steps 1-5
- Effect: none
- Change: dispatcher runs `LocalFunctionCandidateTests`, `MemberLocalFunctionViewTests`,
  `MemberLocalFunctionRemoveTests`, `MemberLocalFunctionReplaceTests`, `GenericContainerNameTests`,
  `SymbolNavigationMissTests`, `MemberViewAmbiguityToolTests`, `GetMethodSourceTests`, `MemberRefactoringTests`
  with `-WithRatchets`, then one `-Suite fast` run. Restart the server, then live: `Member(view, memberName: "Verdict")`
  on `RoslynSentinel.Tests.ModelEval/ImplementationScenarios/Graders/ImplementationScenarioChecks.cs`
  (expect source + note) and `Member(remove, memberName: "Verdict", dryRun: true)` (expect a refusal naming
  the usage). Delete the 2026-11-24 18:00 INBOX line; status IMPLEMENTED with hashes; move to `plans/closed/`.
- Done when: all named classes pass, both live calls give the expected outcome.

## Out of scope

- The other ~45 `ResolveCandidates` callers (modifiers, attributes, accessibility, insertAfter/Before,
  parameters, ChangeSignature, navigation). Each can opt in later after its own audit; flipping the deny-list
  filters to allow-lists is a separate sweep.
- Folding `GetMethodSource`'s own local-function helpers (`WorkspaceReadNavigationImpl.cs`) onto the new
  resolver option.
- Lambdas and anonymous methods.

## Risks and open decisions

- **SymbolFinder and local functions (Step 3).** Unverified whether `SymbolFinder.FindReferencesAsync`
  reports references to a local-function symbol. The step names the fallback (bound identifier scan of the
  enclosing member); the implementer checks with one test before choosing.
- **Schema budget (Step 5).** INBOX notes the claude-lean schema budget has no headroom (stage D R3). If the
  description additions fail a schema-size test, options: (a) shorten to the `containerName` change only,
  (b) put the local-function fact in the not-found error text instead of the description, (c) raise the
  budget (owner decision). Recommendation: (a), then (b).
- **Ambiguity hint shape (Step 1).** The hint builders are shared by every Member ambiguity; the change must
  only add text for `LocalFunction` candidates. Pins in `MemberViewAmbiguityToolTests` guard the rest.
- **Edit-effect facts.** `MemberEditEffectFacts.DescribeExistingAsync` records a null docCommentId for a
  removed local function; accepted, not changed here.
