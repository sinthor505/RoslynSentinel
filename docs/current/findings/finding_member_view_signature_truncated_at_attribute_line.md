# Finding: Member view reports an attribute line as the member's signature and ignores memberName

**Status:** OPEN 2026-10-01. Traced to source; fix not yet built. Design direction is in `proposals/proposal_inspectsymbol_source_aspect.md`.

## Context
Found while checking whether any tool returns one property's declaration (the question was whether
`EngineResultBase.UpdatedText` defaults to null). `Member(operation: view, containerName:
EngineResultBase, memberName: UpdatedText)` was called on `RoslynSentinel.Common/EngineResultBase.cs`
on 2026-10-01.

## What is broken
1. **Wrong `signature` for any member that has an attribute on its own line.** Observed result for
   `UpdatedText` (lines 16-20): `"signature": "[JsonIgnore]"`. The other members of the same record
   (no attribute) returned real signatures, e.g. `"public EditOutcome Outcome"`. The same defect
   hits every `[Test]`, `[Fact]`, `[JsonIgnore]`, `[Obsolete]` etc. member, so a `Member(view)` of a
   test class shows `[Test]` for every test method. (Reasoned from the code below; only the
   `UpdatedText` case was run.)
2. **`memberName` is ignored for `view`.** The call passed `memberName: UpdatedText` and got all 7
   members back. The tool's `memberName` description says "Required for remove and replace", so this
   matches the schema, but a caller cannot ask for one member and there is no error saying the
   parameter was not used.
3. **No declaration text.** The result per member is `name`, `kind`, `signature` (header only),
   `startLine`, `endLine`. There is no initializer, accessor or body, so "what is the default value"
   cannot be answered from this tool.
4. **Miscoded failures.** A missing document (`EditOutcome.DocumentNotFound`) and an unresolved or
   ambiguous container (`ResolveBySnippetOrThrow` throws `InvalidOperationException`, caught and
   returned as `EditOutcome.CannotEdit`) both reach the caller as `ToolErrorCode.Exception`
   (`RefactoringStructuralImpl.cs:394-395`). They should be `NotFound` and `Ambiguous`/`NotFound`.
5. **Test gap.** The only `Member` view tests found cover enum containers
   (`BatteryTwentyFourTests.cs:323`, `GetContainerMembers_OnEnum_ReturnsEnumMembers` in
   `BasicRefactoringTests.cs:84` and `MemberRefactoringTests.cs:208`). None cover an attributed
   member, so nothing asserts the signature text.

## Root cause
Traced to source.
- `RefactoringStructuralImpl.cs:388-397`: the `view` branch requires `containerName`, calls
  `_symbolNavigationEngine.GetContainerMembersAsync(filePathResolved, containerName, contextSnippet,
  lineBefore, lineAfter, ...)`, and never reads `memberName`.
- `SymbolNavigationEngine.GetContainerMembersAsync` (`RoslynSentinel.Engines.Basic/
  SymbolNavigationEngine.cs`, about lines 2486-2556, in the non-enum branch): the signature is built as
  `m.WithLeadingTrivia().WithTrailingTrivia().ToFullString().Trim()` and then cut at the first
  `'\n'`, `'{'` or `';'` (`signature.IndexOfAny(['\n', '{', ';'])`). `ToFullString()` includes the
  member's attribute lists, so when an attribute sits on its own line the first `'\n'` falls right
  after it and the "signature" is the attribute text. Nothing strips `AttributeLists`.
- The enum branch of the same method builds its signature from `Identifier` and `EqualsValue`
  directly, which is why enum members are unaffected.

## Why it matters
`Member(view)` exists to feed `replace`/`remove`: its own doc comment says the output "lines up with
what RemoveMember/ReplaceMember need: an exact memberName plus enough signature text to build a
contextSnippet if the name turns out to be overloaded". A weak model that copies the `signature` into
`contextSnippet` for an attributed, overloaded member gets a snippet that cannot disambiguate (it
matches the attribute, not the member). The wrong text looks authoritative, so nothing tells the
model it is wrong. Separately, the missing declaration text pushes agents to `ReadFile` with a guessed
line range, which is the cost that started this investigation.

## Recommendation
Ranked by leverage; none decided yet except where marked.
1. Fix the signature: build it from the declaration without `AttributeLists` (for example take the
   text from the first token after the attribute lists, `m.WithAttributeLists(default)` per member
   type, or use `m.GetFirstToken()` after the last attribute list), then apply the existing cut at
   `{`/`;`/newline. Add tests for an attributed property, an attributed method, and an overloaded
   pair. Small, independent of the larger design.
2. Honour `memberName` on `view`: return that member's declaration text (initializer, accessors,
   body, leading doc comment), reusing one engine method that both `Member(view)` and any
   `InspectSymbol` aspect call. Design and the trade-off against a separate operation are in
   `proposals/proposal_inspectsymbol_source_aspect.md`.
3. Code the failures: `DocumentNotFound` -> `NotFound`; unresolved container -> `NotFound`;
   ambiguous container -> `Ambiguous` (covered by `plan_error_codes_follow_cause.md`, step 4; the
   `InvalidOperationException` -> `CannotEdit` catch in the engine is shape B in that plan).
4. If `memberName` stays unused for some operation, say so in the result (a one-line note) rather than
   silently returning the wider answer.

## Out of scope
- Whether `Member(view)` should include inherited members (it is syntax-scoped to one container by
  design, per the doc comment).
- `GetFileOutline` and `Search(mode: property)` behaviour; the latter ignoring `query` is noted in the
  proposal, not here.
