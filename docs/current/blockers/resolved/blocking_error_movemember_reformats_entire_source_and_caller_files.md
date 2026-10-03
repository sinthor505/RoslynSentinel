# `MoveMember` reformats every document it touches (whole file), turning a one-method move into a 1000+ line diff

**Status:** RESOLVED 2026-10-03 (commits 4dedc78, a23c581, 4aa2dc2, 389ae1b). See "Resolution" at the end. The sections below are the original incident record.

## What was being attempted

Step 1 of `docs/current/proposals/proposal_syntax_target_resolver_extraction.md`: move one method, `NormalizeTypeName` (about 16 lines, already `public static`), out of `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs` into a new, nearly empty `public static class SyntaxTargetResolver` in `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs` (created with `CreateFile`). An implementer subagent used `MoveMember` for this, with call-site rewriting.

- Server build `bin-vscode/d87a019a-20f0d75c`, `buildTimeUtc` 2026-10-03T06:26:31Z, pid 35516.
- Exact `MoveMember` argument JSON was not captured in the report handed to this doc's author. Re-capture it from the implementer's transcript if a repro is needed.

The move itself worked: the method moved, 12 call sites were rewritten (6 in `SymbolNavigationEngine.cs`, 6 in `RoslynSentinel.Tests.Advanced/GenericContainerNameTests.cs`), and `Build` reported 0 errors. The implementer reported "no tool friction, no comment corruption" because a green build was its only check.

## The exact symptom

`Git(diff, stat)` after the move (figures as reported by the launching session):

```
RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs        1178 lines changed (+320/-884)   expected: ~16 removed + 6 call-site lines
RoslynSentinel.Tests.Advanced/GenericContainerNameTests.cs      26 lines changed               expected: 6 call-site lines
```

What the diff shows, applied across the whole of both files and not only the edited regions:

- Every blank line between top-level declarations and between members was removed, including the blank line after the first `using` and after `namespace ...;`.
- Multi-line positional records were collapsed onto one line: `CallerInfo`, `SymbolHoverInfo`, `TypeMemberDetail`, `InterfaceImplementorCoverage`, `ExtensionMethodInfo` and others.
- Multi-line method signatures were collapsed onto one line, e.g. `LocateSymbolAsync`'s 8 parameters.
- Multi-line ternaries and wrapped boolean conditions were joined onto one line.
- `SymbolLocation`'s per-parameter `/// <summary>` doc comments lost their indentation (now at column 0) and gained a trailing `, ` after each parameter.
- The doc comment `<see cref="NormalizeTypeName"/>` in `BuildContainerNotFoundMessage`'s remarks was rewritten with spaces as `<see cref = "NormalizeTypeName"/>`, and still points at the old member, which no longer exists on that class, so the cref dangles.
- In the test file: blank lines removed, a wrapped constructor call collapsed, a wrapped `Assert.That(...)` joined onto one line, and the trailing newline at end of file dropped (`\ No newline at end of file`).

No error text was produced by any tool; every call reported success.

## Source trace (traced, 2026-10-02)

The first-pass author had no MCP read tools and inferred `NormalizeWhitespace` from the damage shape. That inference is CONFIRMED and now has line citations. Line numbers are from the server build `d87a019a-20f0d75c` working tree; those marked "search" come from a literal `Search(mode: text)` hit, the others are counted from a `ReadFile` slice and may be off by one.

### Call path

1. Tool: `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs:468` (`[McpServerTool(Name = "MoveMember")]`) calls `_memberRefactoringEngine.MoveMemberAsync(...)` (line 487, search). There is no `*Impl` hop and no `AdvancedStructuralEngine`: that class no longer exists (`Search(mode: symbol, query: AdvancedStructuralEngine)` returns `NotFound`).
2. Engine: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:3139` `MoveMemberAsync` picks one of four helpers. Here `SyntaxTargetResolver` already existed (created by `CreateFile`) and `NormalizeTypeName` is `static`, so it took the existing-class branch: `MoveMembersToExistingClassAsync` (`MemberRefactoringEngine.cs:3480-3555`), with `sameFile == false`.
3. The tool then validates and writes `result.Changes` unchanged. That matches the "Payload evidence" section below: the flattening is already present in the engine's returned text.

### The reformatting sites (a) source and target documents

All in `MoveMembersToExistingClassAsync`, each a call to `RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(<whole root>).ToFullString()`:

- `MemberRefactoringEngine.cs:3513` (search): source document, `root.ReplaceNode(classNode, updatedSourceClass)` where `root` is the whole `CompilationUnitSyntax`. This is the line that rewrote `SymbolNavigationEngine.cs` (+320/-884).
- `MemberRefactoringEngine.cs:3514` (search): target document, `newTargetRoot` is the whole target `root.ReplaceNode(targetClassNode, newTargetClassNode)`. This is why `SyntaxTargetResolver.cs` has `namespace RoslynSentinel.Engines.Basic;` glued to the next `/// <summary>` with no blank line, and no final newline.
- `MemberRefactoringEngine.cs:3506` (search): same-file variant (`finalRoot`).

The tree edits feeding those calls are already trivia-lossy even before normalization: `classNode.RemoveNodes(membersToMove, SyntaxRemoveOptions.KeepNoTrivia)` (about line 3491) and `targetClassNode.AddMembers(...)` (about line 3490).

### The reformatting site (b) caller documents

- `MemberRefactoringEngine.cs:3550` (search): `result[doc.FilePath] = NormalizeWholeSubtreeWhitespace(updatedDocRoot).ToFullString()`. `updatedDocRoot` (about line 3549) is `docRoot.ReplaceNodes(memberAccesses, (original, _) => original.WithExpression(IdentifierName(targetClassName)))` on the caller's whole `docRoot`, so a caller-only file such as `GenericContainerNameTests.cs` is normalized in full even though only 6 member-access expressions changed. The loop at about lines 3522-3551 is the call-site rewrite.

### Same pattern in every sibling move path (not just the one hit here)

Search hits for `NormalizeWholeSubtreeWhitespace` in `MemberRefactoringEngine.cs`, all whole-root:

- `MoveMembersToBaseTypeAsync`: 3455, 3472
- `MoveMembersToNewClassAsync`: 3595, 3599, 3635 (caller docs, same loop shape as 3550)
- `MoveInstanceMembersAsync`: 3704, 3710, 3711, 3731, 3732, 3768 (caller docs)
- `PreviewInstanceMoveCallSitesAsync`: 4261, 4267, 4268, 4273 (validation preview only, not written)

So every `MoveMember` mode reformats every document it writes. The fix has to cover all of them, not only 3506-3550.

### Why each symptom follows

- Blank lines removed, records/signatures/ternaries joined, `cref = "..."` spacing: these are what `SyntaxNode.NormalizeWhitespace` does to a whole subtree. `NormalizeWholeSubtreeWhitespace` is a pure pass-through (`RoslynFormattingHelper.cs:328-331`, `node.NormalizeWhitespace(indentation, eol, elasticTrivia)`).
- Final newline dropped: `NormalizeWhitespace` emits no newline after the last token (observed: `\ No newline at end of file`; Roslyn behaviour, not traced into Roslyn source). It is not a write-path effect: the write path receives text that already lacks it.
- Line endings (new observation, partly hypothesis): the helper's default `eol` is `"\n"` (`RoslynFormattingHelper.cs:328`) and no move path calls `EolUtilities.NormalizeEol` (its only uses in `MemberRefactoringEngine.cs` are at 463, 1616, 2052, none in a move path), whereas the scoped helpers do (`RoslynFormattingHelper.cs:66, 151, 203, 294`). Evidence on disk: `GenericContainerNameTests.cs` lines 10-20 read back with bare LF, `SyntaxTargetResolver.cs` is LF everywhere except the moved method's doc comment, which still has CRLF inside it (mixed EOL), while untouched repo files such as `MemberRefactoringEngine.cs` read back CRLF. That suggests the moved files were converted CRLF to LF as well. `git diff --stat` shows only 26 changed lines for a 66-line test file, so Git's diff is evidently not counting the EOL change (hypothesis: `core.autocrlf`; not checked). This also answers the first-pass "Not confirmed" question about the EOL origin: it comes from the engine, not the write path. It does not by itself prove the trailing-newline loss comes from the same place as the EOL change.
- Doc-comment text anomaly (hypothesis, original not diffed): `SyntaxTargetResolver.cs:11` now reads `<c>Foo<T></c>, <c>Foo<TKey , TValue></c>` with raw angle brackets and a space before the comma. If the original used `&lt;`/`&gt;` entities or no space, the normalizer rewrote the doc-comment XML text as well, which would be a CS1570-class corruption. Compare against `SymbolNavigationEngine.cs` at `HEAD` before relying on this.
- Pre-existing precedent: `MemberRefactoringEngine.cs:3791` (search) already contains `<see cref = "NullableAnnotation.Annotated"/>`, so earlier whole-tree normalization has leaked this spacing into committed code before.

### Leads from existing docs

The first-pass author listed the leads below. Two corrections from the trace: `AdvancedStructuralEngine` no longer exists, so the Advanced-side "Risky" inventory entries are not the route; and the path from the resolved instance-call-site blocker is stale for the same reason. These paths and line numbers are stale, because projects were renamed to `Engines.*`, so re-locate them before relying on them:

- `docs/current/blockers/resolved/blocking_error_movemember_instance_callsite_not_rewritten.md` places the tool in `AdvancedStructuralEngine.MoveMemberAsync` (then `RoslynSentinel.Advanced/AdvancedStructuralEngine.cs:173`), with a static-member path `MoveMembersToExistingClassAsync` (then line 301) and an instance path `MoveInstanceMembersAsync` (then lines 1013-1176). It also quotes `RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedSourceRoot).ToFullString()` in `PreviewInstanceMoveCallSitesAsync`. That quoted site only builds a validation preview, but it shows this engine routes whole roots through the whole-tree normalizer.
- `docs/current/finding_normalizewhitespace_container_reformat_risk_inventory.md` lists `AdvancedStructuralEngine.cs` as "Risky" (about 15-18 unscoped whole-tree sites) and says the Advanced-side sweep was never done (`docs/current/TODO.md` entry "`RoslynSentinel.Advanced`'s NormalizeWhitespace occurrences never got a follow-up sweep", Advanced still open). `MoveMember` is the most likely route to those sites.
- The shared helper is `RoslynSentinel.Common/RoslynFormattingHelper.cs`. It has `NormalizeWholeSubtreeWhitespace` (the whole-tree wrapper) and, per the resolved ModifyAttribute blocker, `ReplaceNodesFormattedAsync` (annotation-scoped formatting).

What to confirm when tracing (all three now answered by the sections above and below):

1. Static path, document text: answered in "The reformatting sites (a)" (3506, 3513, 3514). Yes, whole root.
2. Call-site rewriter: answered in "The reformatting site (b)" (3550). Yes, whole root of each caller document.
3. The dangling cref: answered in "Why the cref was missed" below.

## Relationship to `RoslynFormattingHelper` and the risk inventory (traced, 2026-10-02)

- `MoveMember` does not bypass the helper class. It uses it, but only through the one member the helper's own doc comment warns about. `NormalizeWholeSubtreeWhitespace` (`RoslynFormattingHelper.cs:328`) is documented as "Risky: an existing tree root or container ... normalizing it reformats every sibling's whitespace as a side effect", and that is exactly the shape passed at 3506, 3513, 3514, 3550 and the sibling sites. None of the safe, annotation-scoped helpers is used by any move path: `ReplaceNodeFormattedAsync` (`RoslynFormattingHelper.cs:52`), `ReplaceNodesFormattedAsync` (122), `RemoveNodeFormattedAsync` (165), `InsertMemberFormattedAsync` (235). So the helper's guard-rail is advisory only: nothing stops a caller from passing a whole root.
- The risk inventory (`docs/current/finding_normalizewhitespace_container_reformat_risk_inventory.md`) does NOT list this site. Its "Safe" list names `RoslynSentinel.Basic/RefactoringEngine.cs` at lines about 476, 5257, 5288, 818, 540, 1624 (fresh syntax or display-only), and its "Risky" list names no `RefactoringEngine.cs` or `MemberRefactoringEngine.cs` at all. Neither the inventory nor `docs/current/TODO.md`'s sweep entry (starts at line 360) mentions the `MoveMember` move paths; `TODO.md` matches for `MoveMember` (lines 67 and 89) are unrelated entries. The first-pass doc suggested `AdvancedStructuralEngine.cs` as the likely listed route; that class no longer exists, so the inventory's Advanced entries cannot be it.
- The TODO entry says the Basic side was "fully closed 2026-08-27": 55 of 89 hits were bugs, 52 fixed, and 3 deliberately left (`RunMicroRefactoringAsync`, `ExtractConstantSafeAsync`, `GenerateToStringSafeAsync`). The `MoveMember` sites are not among those three, yet 20-plus whole-root sites remain in the move code today. Either they were classified as "legitimate" in that sweep or they were lost when `RefactoringEngine` became `MemberRefactoringEngine`. This is unverified; `git log` on `MemberRefactoringEngine.cs` and the sweep's per-file classification would settle it. Either way the claim "Basic side fully closed" is wrong, and the inventory is incomplete.

## Why the cref was missed (traced, 2026-10-02)

There is no cref handling anywhere in the move code. A text search of `MemberRefactoringEngine.cs` for `Cref`/`descendIntoTrivia` finds only doc-comment text and an unrelated `DescendantTrivia(descendIntoTrivia: true)` at line 1025 (`DetectTrailingUnparsedDeclaration`). Mechanism, from `MoveMembersToExistingClassAsync`:

- Same-class references (the case here, where the cref sits in `BuildContainerNotFoundMessage`'s remarks): `bareIdentifiers = updatedSourceClass.DescendantNodes().OfType<IdentifierNameSyntax>()...` (about line 3494). `DescendantNodes()` without `descendIntoTrivia: true` never enters structured trivia, and a cref lives in structured trivia, so it is invisible. The same shape is in `MoveMembersToNewClassAsync` (about line 3585).
- Cross-document references: the loop at about 3540-3544 selects `SimpleNameSyntax` nodes by `SymbolFinder` location span, then keeps only those whose `Parent` is a `MemberAccessExpressionSyntax` (`id.Parent as MemberAccessExpressionSyntax`), and skips the document when none qualify (`if (memberAccesses.Count == 0) continue;`). A cref name's parent is a `NameMemberCrefSyntax`, never a member access, so a cref reference would be dropped even when `SymbolFinder` reports it (hypothesis: `SymbolFinder` does report cref locations; not verified here).
- Why the build stayed green (hypothesis): an unresolved cref is a documentation warning (CS1574), not an error, so the compile gate in `ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:16`) has nothing to reject.

## Why the existing tests did not catch it (traced, 2026-10-02)

- All engine-level `MoveMemberAsync` tests (`engine.MoveMemberAsync(` at `RoslynSentinel.Tests.Battery.Basic/PreviewInstanceMoveCallSitesTests.cs` lines 425, 465, 507, 560, 613, 702, 757, 821, 901) exercise the INSTANCE path. A solution-wide search for `.MoveMemberAsync(` finds no other caller in tests. Nothing calls the static existing-class path (`MoveMembersToExistingClassAsync`) or the new-class path directly.
- The only tool-level move test touching `MoveMember` is `MoveMember_ToBaseClass_AutoStageTrue_ReturnsNotNull` (`RoslynSentinel.Tests.Tools.Advanced/BatteryTwentyFourTests.cs:407`), which pulls up to a base class and asserts only `Is.Not.Null`.
- Assertions are containment-only and fixtures tiny, for example `Assert.That(callerChange.Value, Does.Contain("_classB.Foo"))` and `Does.Contain("public void Foo()")` (`PreviewInstanceMoveCallSitesTests.cs`, test starting at line 389). No test asserts that regions outside the edit are byte-identical, and the fixtures are a few lines with at most one blank line, so even an identity assertion would need richer content (multi-line record, wrapped signature, per-parameter doc comments, final newline, CRLF) to expose this.
- A direct precedent exists to copy: `RemoveMember_DoesNotReformatUnrelatedSiblingSpacingOrBlankLines` (`RoslynSentinel.Tests.Basic/MemberRefactoringTests.cs:1342`) guards the same bug class for `RemoveMember`. No equivalent was written for `MoveMember`.

## Payload evidence (2026-10-02, traced from the tool result)

The `MoveMember` call returned 162,222 bytes (offload threshold 15,360), written to `.roslynsentinel/largeresults/largeresult_20261003T063825Z_acd3a8fcd41b402b808d49931827d43f.json` as type `Raw`. Its `changedContent` field holds the full new text of each touched file:

- `SymbolNavigationEngine.cs`: 143,142 chars
- `GenericContainerNameTests.cs`: 3,233 chars
- `SyntaxTargetResolver.cs`: 1,843 chars
- every other field (`changeId`, `affectedFiles`, `description`, `dryRun`, `validated`, `status`, `note`): under 400 chars each

What this establishes:

1. **The damage is in the engine's proposed text, before any write.** The returned `SymbolNavigationEngine.cs` text already has `namespace RoslynSentinel.Engines.Basic;` followed directly by `public record CallerInfo(...)` (no blank line) and the multi-line records on one line. `MoveMember` returns `result.Changes` from `_memberRefactoringEngine.MoveMemberAsync` unchanged (`RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs:557`), so the flattening happens inside `MoveMemberAsync` (or a helper it calls), not in `ValidateAndApplyAsync` or the disk write. This narrows the "Not confirmed" item below about a shared write helper: the write path is not the origin. The exact normalizing call was untraced when this was written; the 2026-10-02 trace in "Source trace (traced)" has since identified it (`MemberRefactoringEngine.cs:3506/3513/3514/3550`), which confirms this item's conclusion.
2. **The result also leaks the whole files.** On a written success the tool passes `ChangedContent: result.Changes` (the full text of every touched file) in `AppliedChangeSummary` (`AppliedChangeSummary.cs`, `ChangedContent` parameter). The comment above that return (`AdvancedRefactoringTools.cs:551`) says the member's text "is already visible in the diff", but `returnDiff` defaults to false, so `Diff` is null and the full text is the only content returned. Moving one 16-line method therefore costs a 162 KB response, which a small model must page through with `GetLargeResult`. Only the generic request-filter backstop (`LargeResultHelper.StoreRawJsonAsync`, type `Raw`) caught it; the typed `AppliedChangeSummaryResult` path was not used, and `itemCount: 3` is just the length of `affectedFiles`.
3. **The leak is what made the damage visible.** Because the full text came back, the flattening could be seen directly in the response. A tool that returned only per-file changed-line counts would have shown 1178 and 26 immediately, which supports guardrail 3 in "Suggested direction".

Suggested additions to "Suggested direction":

- On a written (non-dry-run, autoStage true) success, drop `ChangedContent` and return per-file changed-line counts instead. Keep full text only for `autoStage: false` and `dryRun`, where nothing is on disk.
- Correct the misleading comment at `AdvancedRefactoringTools.cs:551`.

## What is and is not confirmed

Confirmed (from the diff stat and diff content reported by the launching session, not independently re-run by this doc's author):

- A one-method move produced 1178 changed lines in the source file and 26 in a caller-only file.
- The reformatting covers regions far from the edit (records, signatures, blank lines, EOF newline).
- The build stays green, so the compile/verify gate cannot see this class of damage.
- The `cref` in a doc comment is left pointing at a member no longer on that class.

Resolved by the 2026-10-02 trace (see "Source trace (traced)"):

- The mechanism is confirmed, not inferred: whole-root `NormalizeWholeSubtreeWhitespace` at `MemberRefactoringEngine.cs:3506/3513/3514/3550` and the sibling move paths.
- The damage originates in `MoveMember`'s engine code (`MemberRefactoringEngine`), not in a shared write helper. This agrees with Payload evidence item 1 below, which reached the same conclusion from the returned `changedContent`. The write path (`ValidateAndApplyHelper`, `PersistentWorkspaceManager.ApplyProposedChangesAsync`) only passes the text through; it has no check that compares written text to the pre-image beyond no-op skipping.
- The trailing newline: it is lost in the engine's proposed text (it is absent from `changedContent`), so it is not a write-path effect. The EOL change is likewise engine-side (see the EOL bullet above), with the caveats stated there.

Still not confirmed:

- The exact `MoveMember` parameters used.
- Whether the shared write path also has independent BOM/CRLF side effects (the related open blockers below); not investigated here.
- Whether the original doc comment on `NormalizeTypeName` was altered beyond whitespace (needs a diff against `HEAD`).
- Whether `git diff` hides an EOL change via `core.autocrlf` (inferred from the 26-versus-66 line count, not checked).
- Why the Basic-side sweep did not catch these sites (needs `git log` and the sweep's per-file classification).

Duplicate check: no existing blocker covers this exact symptom for `MoveMember`. Related docs, none of which is the same defect:

- `docs/current/member_replace_drops_leading_blank_line_and_verify_gap.md` (memory entry "Member reformats unrelated content"): same symptom family on `Member(replace/remove)`, small scale (one blank line, one signature's spacing). Its blank-line bug is recorded there as still unfixed. The verify-phase gap it describes (verify never diffs raw whitespace) applies here as well.
- `docs/current/finding_normalizewhitespace_container_reformat_risk_inventory.md`: the inventory of unscoped whole-tree normalization sites. Likely contains the offending call site.
- `docs/current/TODO.md`: the Advanced-side `NormalizeWhitespace` sweep entry.
- `docs/current/blockers/resolved/blocking_error_modifyattribute_batch_drops_nested_edit_and_reformats_type_body.md`: same class of bug in `ModifyAttribute`, fixed by annotation-scoped formatting. A fix template.
- `docs/current/blockers/blocking_error_member_strips_utf8_bom_on_write.md` and `docs/current/blockers/blocking_error_no_tool_can_write_or_preserve_crlf_line_endings.md`: other whole-file side effects of the shared write path (BOM, line endings).
- `docs/current/blockers/blocking_error_movemember_callsitefixups_wildcard_corrupts_source.md`: a different `MoveMember` defect (wildcard fixups), which was caught by validation.

## Why this matters

- A "pure move" commit becomes an unreviewable 1000+ line diff that hides any real change, and it destroys deliberate formatting (multi-line records and signatures, blank-line grouping, doc-comment indentation).
- The verify/compile gate cannot catch it, because the code still compiles. The tool result gave no signal that unrelated content was reformatted, and the implementer, trusting a green build, reported "no tool friction". This is an environment failure to communicate, not an agent error.
- The planned work is blocked. About 13 more `MoveMember` calls are planned, touching `MemberRefactoringEngine.cs` (about 4.7k lines) and `BasicRefactoringEngine.cs`, and each would reformat those files wholesale too. Work on `docs/current/proposals/proposal_syntax_target_resolver_extraction.md` is halted until this is fixed.
- It also undermines the project's premise for weak models: a model that reads a diff to check its own work sees thousands of changed lines and cannot tell the intended change from the noise.

## Suggested direction (not implemented)

Step 1 of the original list (trace first) is done; see "Source trace (traced)". The remaining steps, with the 2026-10-02 specifics:

1. Trace: done. Sites are `MemberRefactoringEngine.cs` 3455, 3472, 3506, 3513, 3514, 3550, 3595, 3599, 3635, 3704, 3710, 3711, 3731, 3732, 3768 (written) and 4261-4273 (preview only).
2. Fix by scoping, not by removing. Preferred design: compute each document's new text as text edits against the ORIGINAL `SourceText`, never by re-serializing a tree:
   - Source document: delete the moved member's span (its full leading trivia including the doc comment, plus one adjoining line break) with a `TextChange`; do not use `RemoveNodes(..., KeepNoTrivia)` plus a normalizer.
   - Call sites, same class and other documents: one `TextChange` per reference. For `ClassA.Foo` replace the receiver span; for a bare `Foo` insert `Target.` at the identifier span. No formatting pass is needed because the replacement is a single qualified name. This is the approach the resolved ModifyAttribute fix already used (`AttributeTextEditBuilder`, mentioned in the `ReplaceNodesFormattedAsync` doc comment at `RoslynFormattingHelper.cs` around line 118).
   - Target document: insert the member with `RoslynFormattingHelper.InsertMemberFormattedAsync` (`RoslynFormattingHelper.cs:235`), which scopes formatting to the inserted member and preserves the document's dominant EOL. Where a member needs re-indenting, use an annotation-scoped `Formatter.FormatAsync(document, annotation)`, never a whole-root pass.
   - A newly created target file (`MoveMembersToNewClassAsync`) is a freshly synthesized tree, which the helper's doc comment classes as safe for normalization; keep that, but apply `EolUtilities.NormalizeEol` to its text.
   - Cover every move mode (base type 3455/3472, existing class, new class, instance 3704-3768), not only the one hit here.
3. Environment guardrails, so this is visible and refused next time:
   - Report per-file changed-line counts in `AppliedChangeSummary` and drop `ChangedContent` on a written success (the other agent's suggestion in "Payload evidence" above; I agree).
   - Add a collateral-change check before returning from `MoveMemberAsync` (or, more generally, in `ValidateAndApplyHelper.ValidateAndApplyAsync`, `RoslynSentinel.Common/ValidateAndApplyHelper.cs:16`, which already holds the pre-image text and the new text): compare each file's old and new text, and fail with a distinct error code when the number of changed lines exceeds the engine's own declared edit count by more than a small allowance (for example the moved member's line count plus the number of rewritten expressions, plus a margin). The error should name the file and both counts. A one-line `bool allowReformat` opt-in would relocate the failure, so prefer no opt-in until a legitimate caller needs one.
   - Make the helper's risk visible at the call site: a `NormalizeWholeSubtreeWhitespace` overload that accepts only a node known to be synthesized (for example by taking a `SyntaxNode` produced by a factory method) or an analyzer or test that fails when it is passed a `CompilationUnitSyntax` produced from a `Document` root. Without that, the "Risky" warning in its doc comment is not enforced.
4. Update crefs and other doc-comment symbol references. With the text-edit design this falls out of enumerating `SymbolFinder` locations: take each location span as is, including those inside crefs, and insert or replace the qualifier there. If a cref location cannot be edited safely, report it in the result instead of leaving it silent.
5. Correct the inventory: add the `MemberRefactoringEngine.cs` move sites to `docs/current/finding_normalizewhitespace_container_reformat_risk_inventory.md` as Risky and fix its stale `AdvancedStructuralEngine` entries; amend the "Basic side fully closed" text in `docs/current/TODO.md`.

Size estimate (rough): about 150 to 250 lines of production code in `MemberRefactoringEngine.cs` plus one small shared text-edit helper in `Engines.Basic` for the four static and base-type paths, plus the guardrail in `ValidateAndApplyHelper.cs` and `AppliedChangeSummary` (about 40 to 60 lines); the instance path (3704-3768) is a second slice because it also rewrites receivers. Tests about 200 to 300 lines. Files to touch: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`, `RoslynSentinel.Common/RoslynFormattingHelper.cs` (optional overload), `RoslynSentinel.Common/ValidateAndApplyHelper.cs`, `AppliedChangeSummary.cs`, `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs` (drop `ChangedContent`), new tests in `RoslynSentinel.Tests.Battery.Basic` (next to `PreviewInstanceMoveCallSitesTests.cs`), and the two docs above.

### Regression test outline

Place engine-level tests next to `PreviewInstanceMoveCallSitesTests.cs` using its `CreateInMemoryTestFixture`, and one tool-level test through `AdvancedRefactoringTools.MoveMember` for the summary fields.

- Fixture, source file: a `public static class Source` with (a) a blank line after `using` and after `namespace X;`, (b) a multi-line positional record, (c) a method with a wrapped multi-line signature, (d) a wrapped ternary and a wrapped boolean condition, (e) a record whose parameters each carry a `///` summary at 4-space indentation, (f) a `<see cref="Moved"/>` in a doc comment, (g) the member to move (`public static`, with its own doc comment), (h) a final newline. Caller file: a wrapped constructor call and a wrapped `Assert.That(...)` around `Source.Moved(...)` calls, with a final newline. Target file: a nearly empty `public static class Target`.
- Assertion helper `AssertOnlySpansChanged(before, after, allowedEdits)`: strips each expected edit (moved member's full span with its leading trivia in the source, inserted member in the target, each qualified-name replacement in callers) and asserts the remainder of `before` and `after` are byte-equal. This is stricter than `Does.Contain` and covers blank lines, multi-line records and signatures, doc-comment indentation and the final newline.
- Run each fixture with LF, with CRLF, and with no final newline; assert the output EOL equals the input's, and that no mixed EOL appears.
- cref: assert `<see cref="Moved"/>` becomes `<see cref="Target.Moved"/>` (or the result lists it as unresolved) and keeps its original attribute spacing (`cref="..."`, not `cref = "..."`).
- Guardrail: assert the result reports changed-line counts per file that equal the expected small numbers, and a deliberately re-introduced whole-root normalization makes `MoveMember` fail with the collateral-change error rather than succeeding.
- Cover each mode: existing static class, new class, same file, base-type pull-up, and (as a second slice) the instance path.

## Current state and evidence

The reformatted changes are intentionally left uncommitted as evidence: unstaged changes to `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs` and `RoslynSentinel.Tests.Advanced/GenericContainerNameTests.cs`, plus the untracked `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs`. Do not commit or discard them until the trace is done. The proposal step is paused.

## Resolution (2026-10-03)

Fixed by the text-edit design in "Suggested direction" item 2, in four commits. Every move path now builds each file's new text as minimal `TextChange`s against the original `SourceText`. No move path calls `NormalizeWholeSubtreeWhitespace` any more; the two calls left in `MemberRefactoringEngine.cs` are in `ExtractMethodAsync`.

- **4dedc78**, static member to an existing class (`MoveMembersToExistingClassAsync`).
  - The member is cut out with its doc comment, and inserted re-indented in the target's dominant EOL.
  - Every `SymbolFinder` reference gets a qualifier-only edit. This includes crefs (item 4) and references inside the source and target documents.
  - New helper: `RoslynSentinel.Engines.Basic/MoveMemberTextEdits.cs`.
- **a23c581**, `MoveMembersToBaseTypeAsync` and `MoveMembersToNewClassAsync`.
  - A new-class file is written as text, using the source's usings, namespace style and EOL.
  - A static member pulled up to a base type no longer gets an invalid `virtual`.
- **4aa2dc2**, `MoveInstanceMembersAsync` and `PreviewInstanceMoveCallSitesAsync`.
  - Each receiver rewrite replaces only the receiver span.
  - The preview builds its trial text with the same helpers as apply.
- **389ae1b**, guardrails (item 3, partly).
  - `EolChangeGuard` (`RoslynSentinel.Common/EolChangeGuard.cs`) refuses a write that changes an existing `.cs` file's dominant line ending or introduces mixed endings, with `ToolErrorCode.EolChangeRefused`. It runs in `ValidateAndApplyHelper.ValidateAndApplyAsync` and again as a backstop in `PersistentWorkspaceManager.ApplyProposedChangesAsync`.
  - Per-file `FileLineChange` counts were added to `AppliedChangeSummary.LineChanges`. Only `MoveMember` populates them so far.
  - The guard exposed three hard-coded EOLs, now fixed: `CommentingEngine.AddAttribute`, `BasicRefactoringEngine.BuildDocCommentText` and `AddUsingDirectiveAsync`.

Regression tests:
- `RoslynSentinel.Tests.Battery.Basic/MoveMemberPreservesUntouchedTextTests.cs`, 9 tests. They cover existing class, base type, new class, instance and the instance preview, each with CRLF and LF, and use byte-exact `AssertOnlySpansChanged` checks.
- `RoslynSentinel.Tests.Basic/WriteChokepointGuardrailTests.cs`, 23 tests.
- Full suite after 389ae1b: 2951 tests, 2842 passed, 0 failed, 109 skipped.

Not done, tracked in `docs/current/TODO.md`:
- **Collateral-change check, not built.** The changed-line-count check in item 3 was deliberately left out. Only the EOL refusal, which is exact, was added.
- **Full text still returned.** `ChangedContent` is still returned on a written success. The comment at `AdvancedRefactoringTools.cs:551` is also not corrected.
- **Guard still advisory.** No overload of `NormalizeWholeSubtreeWhitespace` enforces the "synthesized node only" rule.
- **Inventory not updated.** Item 5's changes to the inventory and TODO-sweep text are not done.
- **Line counts not wired up.** The other `AppliedChangeSummary` construction sites do not pass `LineChanges` yet.
- **MethodSignature still re-serializes callers.** Its call-site rewrite writes whole caller files as CRLF. This is the same defect class, outside `MoveMember`.
- **Instance moves leave crefs behind.** A cref to the moved member still names the old class. Static moves retarget it.
- **Wrapped call sites can be misclassified.** The instance preview matches diagnostics to references by start line. A wrapped call whose error lands on a later line, such as on a named argument, is classified `Valid` and left unrewritten. The compile gate is the only thing that stops the result. Line shifts in the source and target after the removal skew the same comparison.
- **New files are LF.** `MoveMemberTextEdits.cs`, `EolChangeGuard.cs`, `FileLineChange.cs` and both new test files are LF, because no MCP tool can write CRLF. See `blocking_error_no_tool_can_write_or_preserve_crlf_line_endings.md`.
