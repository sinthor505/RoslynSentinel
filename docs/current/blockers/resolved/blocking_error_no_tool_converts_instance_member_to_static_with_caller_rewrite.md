# No tool converts an instance member to static and rewrites its callers (`ModifyModifier`, `MoveMember`)

**Status:** RESOLVED 2026-10-03 (commit b773820). See "Resolution" at the end. The sections below are the original incident record.

## What was being attempted

Step 1 of `docs/current/proposals/proposal_syntax_target_resolver_extraction.md`: move the stateless
syntax-resolution members out of `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs` into a new
`public static class SyntaxTargetResolver`. The members are:

- `ResolveCandidates`
- `GetMemberName`
- `PreferConstructorOverType`
- `PreferNonInterfaceMember`
- `FilterByContainingType`
- `ResolveBySnippetOrThrow`
- `BuildMemberHint`
- `BuildMemberHintForCandidates`
- `BuildTypeHint`
- `BuildTypeHintForCandidates`
- `BuildContainerNotFoundMessage`

All eleven are instance methods that use no instance state. Callers reach them through an injected
instance, e.g. `_symbolNavigationEngine.ResolveCandidates(...)`, in `MemberRefactoringEngine.cs`,
`BasicRefactoringEngine.cs`, `Engines.Advanced/AdvancedStructuralEngine.cs` and others (about 90
call sites in total, per the `MoveMember` refusal below).

Server build under test: `bin-vscode/d87a019a-273b9eb4`, buildTimeUtc 2026-10-03T08:45:11Z, pid 10524.

Work that did succeed: `NormalizeTypeName` was already `public static`, and `MoveMember` moved it
cleanly to `SyntaxTargetResolver` (a 44-line diff in the source, 12 lines in the caller test file,
all call sites rewritten, cref retargeted to `SyntaxTargetResolver.NormalizeTypeName`). That change
is uncommitted in the working tree (see Current state).

## The exact symptom

### Symptom 1 (reproduced): `ModifyModifier(add static)` is refused with CS0176 x28

Call:

```
ModifyModifier(filePath: "SymbolNavigationEngine.cs", targetName: "ResolveCandidates",
               modifier: "static", action: "add", dryRun: true)
```

Result: `errorCode: ValidationFailed`, message:

```
ModifyModifier: the change was valid and matched its target(s), but introduces new compiler errors - change not applied. Fix the issue(s) below and retry.
```

Detail:

```
CS0176 (x28): Member 'SymbolNavigationEngine.ResolveCandidates(SyntaxNode, SourceText, string, CancellationToken)' cannot be accessed with an instance reference; qualify it with a type name instead
```

First sites listed: `MemberRefactoringEngine.cs` lines 158, 505, 640, 726, 1076, 1199, 1273 and
1371; `BasicRefactoringEngine.cs` lines 1577 and 1754; `Engines.Advanced/AdvancedStructuralEngine.cs:1362`.

The error is accurate. It gives no recovery path: no tool rewrites `instance.M(...)` to `Type.M(...)`.

### Symptom 2 (reported by the implementer agent, not reproduced): `MoveMember` into a static class keeps members as instance members

- `MoveMember` with the 11 members, `targetClassName: SyntaxTargetResolver`,
  `targetFilepath: SyntaxTargetResolver.cs` and no `callSiteFixups` was refused with 90 unresolved
  call sites.
- The same call with `callSiteFixups: {"*": "SyntaxTargetResolver"}` was refused with
  `CS0708`, "cannot declare instance members in a static class".
- The tool's `targetClassName` description says "an existing unrelated class receives them as-is",
  so no static conversion is offered.

The verbatim text of the first refusal was not captured in the brief; only the count (90) and the
CS0708 text above are known.

## Source trace

Not traced to the implementing branches. What is established:

- Symptom 1: the `ModifyModifier` compile gate refused the change because the post-edit workspace
  had CS0176 diagnostics. This is the gate working as designed (the "valid and matched, but
  introduces new compiler errors" message). The gap is that `ModifyModifier` has no caller-rewrite
  step to run before the gate.
- Symptom 2: the implementer's account is consistent with `MoveMember` moving members "as-is" to an
  existing unrelated class. Per `docs/current/TODO.md:11`, the caller/cref rewrite for moves lives in
  `MoveMemberTextEdits.BuildReferenceEdit`, and the brief states this rewrite exists only for static
  members (the static existing-class path in `MemberRefactoringEngine.MoveMembersToExistingClassAsync`).
  I did not open those methods in this writeup, so the exact branch that emits CS0708 is a
  hypothesis: the instance path appears to copy the member declaration unchanged into the static
  target.
- Related prior design note: `docs/current/proposal_codemodtools_decomposition.md:157` and open
  question 5 (line 372) discuss `makeStatic`. They cover `MakeMethodStaticAsync` rewriting instance
  access inside the body, not rewriting external callers. That question is still open and is the same
  design decision as option (a) below.

## What is and is not confirmed

Confirmed:
- `ModifyModifier(add static, dryRun: true)` on `ResolveCandidates` fails with CS0176 x28 and applies nothing.
- `NormalizeTypeName` (already static) moves cleanly with all callers rewritten.
- No existing blocker covers this: this is not a duplicate of
  `blockers/resolved/blocking_error_movemember_reformats_entire_source_and_caller_files.md`, and
  `docs/current/blockers/` and `docs/current/TODO.md` contain no make-static entry. The nearest items
  are `proposal_codemodtools_decomposition.md` (open question 5) and the TODO.md:11 entry about
  instance moves leaving crefs behind, both cross-referenced here.

Reported, not reproduced:
- Symptom 2 (the 90-site refusal and the CS0708 refusal).

Not confirmed:
- The code branch producing CS0708.
- Whether all 11 members truly use no instance state (the brief says so; the instance-state check
  proposed below would verify it mechanically).

## Why this matters

- "Make static" plus "move to a static helper class" is a common decoupling refactoring, and this
  proposal needs it for 11 members.
- The two routes left are both bad. One is a hand-written `ReplaceSnippet` batch over about 90 call
  sites, which is error-prone and exactly the text-editing fallback the tool surface exists to
  prevent. The other is the proposal's `[Obsolete]` forwarders, which only defer the same rewrite.
- A weak model hitting CS0176 x28 has no tool to reach for. The message says "qualify it with a type
  name instead", which is a correct description of a rewrite the environment will not perform.

## Suggested direction (not implemented)

Options, with tradeoffs:

- **(a) `ModifyModifier(add static)` rewrites each CS0176 site.** Replace the receiver span with the
  containing type name, as one atomic change set. Refuse if the member uses instance state, and name
  that state in the error. Pro: smallest tool surface, fixes in-place makeStatic (open question 5 in
  the decomposition proposal). Con: does not by itself move the member, so extraction still takes two
  calls; rewriting inside a type that is the receiver of its own instance calls needs care.
- **(b) `MoveMember` with a static target class makes each moved member static.** Reuse the
  instance-state check and rewrite `instance.M` to `Target.M`. Today that rewrite only exists for
  static members (`MoveMemberTextEdits.BuildReferenceEdit`, the static existing-class path in
  `MemberRefactoringEngine.MoveMembersToExistingClassAsync`). Pro: one call does the whole
  extraction. Con: `MoveMember` already has a large parameter surface; must be explicit when it adds
  `static` so the "received as-is" description stays truthful.
- **(c) Both.** (a) as the primitive, (b) composing it. Likely the best end state, at the cost of the
  most work.

Notes for whichever option is chosen:
- The receiver-only text-edit machinery added in commits 4dedc78 and 4aa2dc2 already does most of the
  rewrite work.
- An injected field (e.g. `_symbolNavigationEngine`) may become unused after the rewrite. The tool
  should report that field rather than silently delete it.
- The `MoveMember` `targetClassName` description should say what happens for a static target class
  and an instance member, so the refusal is predictable before the call.

## Current state

- The resolver-extraction proposal is paused at step 1.
- The uncommitted `NormalizeTypeName` move is left in the working tree: `SymbolNavigationEngine.cs`,
  `RoslynSentinel.Tests.Advanced/GenericContainerNameTests.cs` and the new
  `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs`.
- Unblocks when a tool can make an instance member static and rewrite its callers (option a, b or c),
  after which the remaining 11 members can be moved.

## Resolution (2026-10-03)

Fixed in b773820 with option (c): both tools now convert.

- `ModifyModifier(add static)`, both the singular and the batch form, goes through `MemberRefactoringEngine.ConvertMembersToStaticAsync`.
  - It refuses a member that uses instance state, and names each use by symbol and line. A member that only calls other members converted in the same batch is allowed.
  - It rewrites `receiver.M(...)` and `receiver.P` to `ContainingType.M` as receiver-span text edits, in `RoslynSentinel.Engines.Basic/StaticConversionEdits.cs`.
  - `recv?.M()` is rewritten only when the receiver is a simple field, local or parameter, and a finding notes that the null check is gone. Any other receiver is refused.
  - A receiver field that is no longer read anywhere is reported as a finding and never deleted.
- `MoveMember` into an existing static class converts instance methods and properties.
  - It applies the same check (`RequireInstanceMembersConvertibleToStatic`) and routes them through the existing-class text-edit path.
  - The conversion notes are returned as `conversionFindings`.
  - An instance field moved into a static class is refused.
- These cases are refused with a named reason:
  - auto-properties and init accessors;
  - virtual, override, abstract, extern, partial, readonly, sealed and required members;
  - interface implementations;
  - members of generic types;
  - captured primary-constructor parameters.
- Extension-method conversion is not implemented.
- In one `ModifyModifier` `edits` batch, a file touched both by an add-static conversion and by another edit is refused, with the message "Split them into separate calls.".

Regression tests: `RoslynSentinel.Tests.Battery.Basic/MakeStaticRewritesCallersTests.cs`, 13 cases, byte-exact on LF and CRLF. The full suite passes: 2964 total, 2855 passed, 0 failed, 109 skipped.
