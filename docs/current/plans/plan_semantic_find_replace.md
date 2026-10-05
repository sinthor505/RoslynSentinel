# Plan: SemanticFindReplace - a symbol-anchored replace tool (v1: rename + invert a bool property or field)

**Status:** APPROVED 2026-10-04. Not started; slices are sized for the Haiku-tier implementer, one slice per dispatch.

## Problem
Retiring `SentinelCallToolResult.IsSuccess` in favour of `IsError` (see
`proposals/proposal_shared_result_status_interface.md`, phases 3-5) means about 1264 call sites whose
polarity must flip (`IsSuccess = true` -> `IsError = false`, `!x.IsSuccess` -> `x.IsError`). A text
replace also hits unrelated types with the same member name (`ChildToolResult.IsSuccess`, the bespoke
`Success` records), and no existing tool inverts a boolean member.

Evidence from the existing code:
- `LogicSimplificationEngine.InvertBooleanLogicAsync` (`RoslynSentinel.Engines.Advanced/LogicOptimizationEngine.cs:653`)
  wraps each reference in `!`, but: finds only a local/field/method declarator by name inside one file (no
  properties), turns `x = true` into `!x = true`, never renames or touches the declaration, and (hypothesis,
  read not run) replaces nodes using spans from the original tree after earlier edits in the same file.
  Its tests only check "does not throw" for an unknown name. It is gated behind `--mode Modernize`.
- `BasicRefactoringEngine.InvertBooleanAsync` (`Engines.Basic/BasicRefactoringEngine.cs:2369`) is a stub that
  returns `CannotEdit`.
- `MappingEngine.InvertAssignmentsAsync` swaps the two sides of an assignment; unrelated.

## Decision
Build a new opt-in tool `SemanticFindReplace`. It resolves a symbol, finds its references with Roslyn, classifies
each reference by its syntactic role, and rewrites each role with a fixed typed rule (not free-form text
templates, which a weak model gets wrong). Edits go through the existing compile gate and atomic apply.

v1 has one operation, `invertBoolean`: rename a `bool` property or field to `newName` and flip its polarity at
every site. A generic "role -> replacement" operation is a later plan (see Out of scope).

Tool shape (v1):
`SemanticFindReplace(reason, operation: invertBoolean, symbol: <docCommentId>, newName, mode: preview|apply)`.
- `symbol` is a `docCommentId` (what `Search`/`LocateSymbol` return), resolved with
  `DocumentationCommentId.GetFirstSymbolForDeclarationId`.
- `mode` is mandatory (no default): `preview` lists every site as before/after and writes nothing; `apply`
  writes. A required parameter, per the failure doctrine, instead of an optional dry-run flag.

Role rules for `invertBoolean` (R = the reference expression):

| Role | Example | Rewrite |
| --- | --- | --- |
| WriteLiteral | `IsSuccess = true` (initializer or assignment) | `IsError = false` |
| WriteExpression | `IsSuccess = a && b` | `IsError = !(a && b)` (no parens when `a` is a simple name/member/call) |
| Read | `if (x.IsSuccess)` | `if (!x.IsError)` |
| NegatedRead | `if (!x.IsSuccess)` | `if (x.IsError)` (the `!` is removed, never doubled) |
| NameOf | `nameof(IsSuccess)` | `nameof(IsError)` (rename only) |
| DeclarationInitializer | `bool IsSuccess { get; set; } = true;` or `bool f = true;` | initializer flipped like WriteLiteral/WriteExpression |
| Unsupported | `x.IsSuccess &= y`, `x.IsSuccess++`, `ref x.IsSuccess`, property pattern `{ IsSuccess: true }`, `== true/false` | not rewritten; see refusal rule |

Refusal rules (all return a structured error that names the parameter or site; nothing is written):
- Symbol is not a `bool` property or field -> `InvalidArgument` naming the actual kind/type.
- Property has a custom accessor body (for example `get => !_x`) -> `TargetIneligible` (inverting a computed
  property is not meaningful; this covers the `IsSuccess` compat alias, which must be deleted by hand).
- Symbol is `virtual`, `override` or `abstract` -> `TargetIneligible` (overrides would need coordinated edits).
- Any `Unsupported` site -> the whole call is refused (atomic) and the message lists each file:line so the
  caller can fix those by hand and re-run.
- `newName` is not a valid identifier, or already exists on the containing type -> `InvalidArgument`.

## Execution rules
These apply to every slice. The implementer reads this section once, then only its own slice.

1. **One slice per dispatch.** Do exactly the named slice, then stop and report. Do not start the next slice,
   do not refactor nearby code, do not "improve" files outside the slice's Files list.
2. **All C# reads/writes and git go through the RoslynSentinel MCP tools** (CLAUDE.md dog-fooding rules).
   Create files with `CreateFile`; edit with `Member`/`ReplaceSnippet`. If a tool fails or lacks the operation,
   stop and report `ESCALATE:` with the exact error; do not work around it with shell edits.
3. **Build gate:** after the slice, `Build(level: fullBuild)` must show 0 errors. Run only the tests named in the
   slice's "Done when", plus the new tests you wrote.
4. **Do not commit.** The supervisor commits (the worktree holds other sessions' uncommitted edits, and some
   target files already contain unrelated hunks). Report the list of files you created or changed.
5. **Report format** (keep it short): files changed; build result; tests run with pass/fail counts; anything that
   surprised you. If blocked, start the report with `NEED-ADVICE:` (a question for the advisor) or `ESCALATE:`.
6. **Conventions:** ASCII-only punctuation in comments and strings; `CancellationToken` is the last parameter of
   new methods; new engine code lives in `RoslynSentinel.Engines.Basic` and must not reference MCP types
   (`RequestContext<>`, tool attributes); never put raw exceptions or file-system paths in a `ResultError`.
7. **Tests:** engine tests go in `RoslynSentinel.Tests.Basic`, tool tests in `RoslynSentinel.Tests.Tools.Basic`.
   Prefer in-memory fixtures (a `CSharpSyntaxTree`/`AdhocWorkspace` built from a string) over files on disk.
   Copy the style of the nearest existing test in the same project; do not invent a new fixture helper if one
   exists (search the test project first).
8. **Known baseline:** the full suite has pre-existing failures/skips; only report NEW failures (see
   `reference_known_failing_tests`). Do not run the whole suite per slice.

## Steps

### Step 1 - Types only (no behaviour)
- Files: new `RoslynSentinel.Engines.Basic/SemanticReplaceTypes.cs`.
- Change: add `enum SemanticReplaceRole { WriteLiteral, WriteExpression, Read, NegatedRead, NameOf, DeclarationInitializer, Unsupported }`;
  `record SemanticReplaceSite(string FilePath, int Line, SemanticReplaceRole Role, string Before, string After, string? UnsupportedReason)`;
  `enum SemanticReplaceMode { preview, apply }` is NOT here (it belongs to the tool layer, Step 9).
- Done when: Build 0 errors. No tests (data types only).

### Step 2 - Symbol resolution
- Files: new `RoslynSentinel.Engines.Basic/SemanticReplaceEngine.cs` (class + one method); new test file
  `RoslynSentinel.Tests.Basic/SemanticReplaceEngineTests.cs`.
- Change: `SemanticReplaceEngine` takes the workspace dependency the same way `CloneDetectionEngine` does (copy its
  constructor shape; find it with `Search`). Add
  `Task<(ISymbol? Symbol, ResultError? Error)> ResolveBoolMemberAsync(string docCommentId, CancellationToken ct = default)`:
  resolve the id across the solution's compilations; return `NotFound` if none; return `InvalidArgument` (message
  names the actual symbol kind and type) unless it is a `bool` property or field. Apply the custom-accessor,
  `virtual/override/abstract` refusals from the Decision section here too.
- Done when: Build 0 errors; new tests pass for: auto-property ok; field ok; unknown id -> NotFound; `int` property ->
  InvalidArgument; property with `get => !_x` -> TargetIneligible; `virtual` property -> TargetIneligible.
- Reference: `ToolErrorCode` constants and `ResultError(ErrorCode, Message, ...)` in `RoslynSentinel.Common`.

### Step 3 - Role classifier, write sites
- Files: new `RoslynSentinel.Engines.Basic/ReferenceRoleClassifier.cs` (static class); tests in
  `RoslynSentinel.Tests.Basic/ReferenceRoleClassifierTests.cs`.
- Change: `static SemanticReplaceRole Classify(SyntaxNode referenceNode)` where `referenceNode` is the identifier
  expression (`IdentifierNameSyntax`) of the reference. In this slice handle only: the node is the left side of a
  simple `=` assignment or the name in an object-initializer assignment (`new T { IsSuccess = ... }`) -> look at
  the right-hand side: a `true`/`false` literal -> `WriteLiteral`, anything else -> `WriteExpression`. Every other
  shape returns `Unsupported` for now (Step 4 fills in the rest). Pure syntax; no semantic model needed.
- Done when: Build 0 errors; table-driven tests parse small snippets and assert the role for: `x.IsSuccess = true;`,
  `x.IsSuccess = a && b;`, `new T { IsSuccess = false }`, and one unrelated shape that must be `Unsupported`.

### Step 4 - Role classifier, reads and the rest
- Files: `RoslynSentinel.Engines.Basic/ReferenceRoleClassifier.cs` (extend); extend its test file.
- Change: add `Read` (plain use as a value), `NegatedRead` (the node's parent, after skipping parentheses, is a
  `!` prefix-unary), `NameOf` (inside `nameof(...)`). Mark `Unsupported` with a reason string (add an overload
  `Classify(node, out string? unsupportedReason)`): compound assignment (`&=`, `|=`, `^=`), `++/--`, `ref`/`out`
  argument, property pattern subpattern, and a comparison against a `true`/`false` literal (`== true`, `!= false`).
- Done when: Build 0 errors; tests cover one snippet per role plus each Unsupported reason.

### Step 5 - Rewriter (pure text rule)
- Files: new `RoslynSentinel.Engines.Basic/BooleanInversionRewriter.cs` (static); tests in
  `RoslynSentinel.Tests.Basic/BooleanInversionRewriterTests.cs`.
- Change: `static string Rewrite(SemanticReplaceRole role, string originalText, string newName)` returning the
  replacement text for the reference expression's enclosing unit, per the role table: literal flip, `!(expr)` with
  parentheses only when the expression is not a simple name/member-access/invocation, `Read` -> `!` + name,
  `NegatedRead` -> name without the `!`, `NameOf` -> name only. Work on small text fragments; do not touch syntax
  trees here. State in a code comment which text span each role replaces (the identifier alone for NameOf; the
  identifier plus the `!` for NegatedRead; the whole right-hand side for WriteLiteral/WriteExpression).
- Done when: Build 0 errors; table-driven tests for every row of the role table, including: `true`->`false`,
  `a && b` -> `!(a && b)`, `Foo()` -> `!Foo()`, and that `!x` never becomes `!!x`.

### Step 6 - Declaration handling
- Files: `RoslynSentinel.Engines.Basic/SemanticReplaceEngine.cs` (add one method); extend
  `SemanticReplaceEngineTests.cs`.
- Change: add `static (TextSpan IdentifierSpan, TextSpan? InitializerValueSpan, string? InitializerText) DescribeDeclaration(ISymbol symbol, CancellationToken ct = default)`
  (or an equivalent small record) that locates the declaring syntax of the property/field: the identifier token
  span (to rename) and, if there is an initializer (`= true`), the value span and text (to flip via Step 5).
  Pure lookup; applies nothing.
- Done when: Build 0 errors; tests for: auto-property with initializer, auto-property without, field with
  initializer, field without.

### Step 7 - Site collection and per-document edits
Split 2026-10-04 into 7a/7b/7c for the Haiku implementer (one new algorithm is too much for one slice):
- 7a: `ValidateNewName(ISymbol symbol, string newName)` -> `ResultError?` (valid identifier, no collision with a member of the containing type). Tests.
- 7b: `CollectSitesAsync(ISymbol symbol, string newName, ct)` -> `(List<SemanticReplaceSite> Sites, List<ReferenceEdit> Edits, ResultError? Error)`: FindReferences -> identifier node -> `Classify` -> per-site replace span + `Rewrite` text (Read gets `parenthesize` when the reference is the receiver of a member/element access; NegatedRead span includes the `!`; Write roles span the whole right-hand side), plus the declaration edits (Step 6). Any `Unsupported` site -> error listing every `file:line` + reason. No text is applied here.
- 7c: `PlanInvertBooleanAsync` = 7a + 7b + apply the edits per document in DESCENDING span order into new file text, returning `Changes` (`Dictionary<FilePathWrapper,string>`, full new text per file, the shape `ValidateAndApplyHelper` takes). Tests per the "Done when" below.
Original single-slice description (kept for the contract):
- Files: `RoslynSentinel.Engines.Basic/SemanticReplaceEngine.cs` (add one method); extend its tests.
- Change: `Task<(List<SemanticReplaceSite> Sites, Dictionary<FilePathWrapper,string> Changes, ResultError? Error)> PlanInvertBooleanAsync(ISymbol symbol, string newName, CancellationToken ct = default)`.
  Use `SymbolFinder.FindReferencesAsync(symbol, solution, ct)`; for every location map to the identifier node,
  call `Classify` (Step 3/4) and `Rewrite` (Step 5); add the declaration edits (Step 6). Group edits by document
  and apply them as text changes sorted by span start DESCENDING in one pass per document, so earlier edits never
  shift later spans. This is the exact defect to avoid from `InvertBooleanLogicAsync` (see Problem). If any site is
  `Unsupported`, return an error that lists every such `file:line` + reason and an empty `Changes` (atomic refusal).
  Reject `newName` that is not a valid identifier (`SyntaxFacts.IsValidIdentifier`) or collides with a member of
  the containing type.
- Done when: Build 0 errors; tests with a two-class fixture where ONLY class A's `IsSuccess` is targeted and class
  B's same-named property is left untouched; a file with 3+ sites in one document (proves descending application);
  an Unsupported site refuses everything; a colliding `newName` refuses.

### Step 8 - Engine entry point
- Files: `RoslynSentinel.Engines.Basic/SemanticReplaceEngine.cs` (add one method); extend its tests.
- Change: `Task<SemanticReplaceOutcome> InvertBooleanAndRenameAsync(string docCommentId, string newName, CancellationToken ct = default)`
  composing Step 2 then Step 7, returning one record (`Sites`, `Changes`, `Error`). Add the `SemanticReplaceOutcome`
  record to `SemanticReplaceTypes.cs`. No new logic; this is the single call the tool will make.
- Done when: Build 0 errors; one end-to-end engine test over an in-memory solution that asserts the changed text of
  every document for a fixture mirroring the real case: property `IsSuccess` with `init`, an object initializer
  `{ IsSuccess = true }`, `if (r.IsSuccess)`, `if (!r.IsSuccess)`, `r.IsSuccess = a && b;`, plus an unrelated class
  with its own `IsSuccess`.

### Step 9 - Tool class (thin) and enums
- Files: new `RoslynSentinel.Tools.Basic/SemanticFindReplaceTools.cs`; `RoslynSentinel.Common/ToolEnums.cs`
  (add `SemanticReplaceOperation { invertBoolean }` and `SemanticReplaceMode { preview, apply }`, with the same
  `[JsonConverter(typeof(JsonStringEnumConverter))]` attribute the neighbouring enums use);
  new `RoslynSentinel.Tests.Tools.Basic/SemanticFindReplaceToolTests.cs`.
- Change: copy the structure of `RoslynSentinel.Tools.Basic/DeclarationTools.cs` (constructor, `[McpServerToolType]`,
  `[McpServerTool(Name = "SemanticFindReplace")]`, `[Produces(...)]`, `ToolCallReason reason`, the
  `InvalidArgument` helper and `RejectForeignParams` pattern). Parameters: `reason`, `operation`, `symbol`,
  `newName`, `mode` (all required, no defaults except `CancellationToken` last). `preview` returns the `Sites`
  list and writes nothing. `apply` passes `Changes` to `ValidateAndApplyHelper.ValidateAndApplyAsync(...)`
  (signature in `RoslynSentinel.Common/ValidateAndApplyHelper.cs:17`; see how `RefactoringSignatureImpl.RenameSymbol`
  at `Tools.Basic/RefactoringSignatureImpl.cs:61` calls it and how it builds its result) with
  `operationName: "SemanticFindReplace"`. Every error names the parameter and a correct value. Write a short,
  concrete `[Description]` (what it does, that `symbol` is a docCommentId from `Search`/`LocateSymbol`, that
  `preview` should come first).
- Done when: Build 0 errors; tool tests (calling the tool class directly, as `DeclarationToolTests.cs` does):
  preview returns sites and leaves the file text unchanged; apply changes the file; unknown `symbol` returns
  `NotFound`; missing/invalid `mode` is rejected with a message naming `mode`.

### Step 10 - Registration (opt-in, claude-lean on demand)
- Files: `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`, `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`,
  `RoslynSentinel.Common/ToolsetCatalog.cs`, `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs`; the engine's DI
  registration in `AddRoslynSentinelEnginesBasic` (find it with `Search`); possibly
  `RoslynSentinel.Tests.Server/ClaudeLeanModeTests.cs` and `McpToolsetControlTests.cs` (counts/lists change).
- Change: mirror commit 97964b2 (`git show 97964b2 --stat`; the registration hunks are the template): add
  `"SemanticFindReplaceTools"` to `ClaudeLeanToolClasses` and `ClaudeLeanOnDemandToolClasses`; add the DI block next
  to the `declarationActive` block in `AddRoslynSentinelToolsBasic` (singleton tool class + `WithSentinelTools`);
  add `"SemanticFindReplace"` to the `moveExtract` set in `ToolsetCatalog.ToolsBySet` (decision recorded under Risks);
  add the name to the `moveExtract = ...` text in the `McpToolsetControl` `[Description]`; register the engine
  singleton. The new tool is a mutating tool, so do NOT add `[UnrecoverableBreaker(Allowed)]` (absent = refused during
  a halt, which is correct).
  Then regenerate the generated docs: `pwsh scripts/Generate-ArchitectureMap.ps1` (PowerShell tool), and include the
  changed `docs/generated/*.md` in the report.
- NOTE for the supervisor: `ServiceRegistrationExtensionsBasic.cs` may carry unrelated uncommitted hunks from other
  work; check `Git diff` for that file before committing it.
- Done when: Build 0 errors; `ClaudeLeanModeTests`, `McpToolsetControlTests`, `ArchitectureDocFreshnessTests`
  (Tests.Server) pass, with any count/list assertions updated to include the new tool (and only that).

### Step 11 - End-to-end tool test on a real-shaped fixture
- Files: extend `RoslynSentinel.Tests.Tools.Basic/SemanticFindReplaceToolTests.cs`.
- Change: one test that builds a fixture of three source files (the property declaration with `init` and an
  initializer, an object-initializer call site, a read, a negated read, a write of a non-literal expression, an
  unrelated type with its own `IsSuccess`, and one file with a `Unsupported` site). Assert: preview lists the
  expected sites; apply on the supported subset writes the expected text for all files and the project still
  compiles (the compile gate); the Unsupported fixture is refused atomically with `file:line` in the message and
  nothing on disk changed.
- Done when: the new test passes and the earlier tests in the file still pass.

## Out of scope
- A generic `replace` operation with caller-supplied role -> replacement rules (follow-up plan once v1 is used).
- Parameters, locals, methods; call-site argument flipping (`Foo(isSuccess: true)`); implicit interface
  implementations (hypothesis: the compile gate rejects the broken result; confirm in Step 11 if cheap).
- Non-C# text (`"isSuccess"` in JSON fixtures, `.ps1` hooks).
- Migrating the real `IsSuccess` call sites (a separate pilot after v1 ships; start with one small project).
- Exposing the tool in modes other than claude-lean on demand.

## Risks and open decisions
- **Toolset placement:** `moveExtract` is chosen because `InvertAssignments` already lives there; `declarations` is
  the alternative. A single word in `ToolsetCatalog` changes it. Confirm before Step 10 is dispatched.
- **Live verification needs a fresh server.** A new tool class is not callable in the running session (see
  CLAUDE.md "new tool needs a fresh session"); the unit/integration tests are the in-session check, and a live
  `McpToolsetControl` + `SemanticFindReplace(mode: preview)` call is a supervisor step after the server restarts.
- **Span handling (hypothesis):** the descending-order single-pass rule in Step 7 is the answer to the stale-span
  problem seen in `InvertBooleanLogicAsync`; Step 7's three-sites-in-one-document test is what proves it.
- **`Read` ambiguity:** a reference inside an object initializer on the left of `=` is a write, but the same
  identifier on the right is a read; Step 3/4 must classify by position, and the tests above cover both.
- **Haiku context:** if a slice proves too big (the implementer reports `NEED-ADVICE` twice on it), split it
  rather than escalating; Steps 3-5 are already split for that reason.
