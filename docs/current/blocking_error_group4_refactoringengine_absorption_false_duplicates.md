# Engine-reorg group 4's `RefactoringEngine`-absorption bullet describes duplicates that do not exist

**Status:** OPEN, confirmed. Scoped to group 4's third bullet only (the `RefactoringEngine`/
`InstrumentationEngine`/`MsToolAugmentEngine` absorption). Does not block the rest of group 4 (the
`StructuralRefactoringEngine` merge of `AdvancedStructuralEngine` + `GranularRefactoringEngine` +
selected `AdvancedRefactoringEngine`/`RefinementEngine`/`AdvancedTypeEngine` methods), which is
continuing separately.

## What was being attempted

Executing group 4 ("Structural refactoring merge") of the engine-reorg plan at
`.claude/plans/enumerated-jumping-babbage.md`. The plan's third bullet for group 4 reads (verbatim):

> `RefactoringEngine` (member CRUD, signatures, enums, attributes, etc.) is untouched except
> absorbing `InstrumentationEngine`'s duplicate `WrapInTryCatchAsync` and `MsToolAugmentEngine`'s
> "Safe"-variant duplicates of Extract*/FormatDocument (`GenerateToStringSafeAsync` vs
> `CodeGenerationEngine.GenerateToStringAsync`, etc.) - keep the `RefactoringEngine`/
> `CodeGenerationEngine` originals as canonical, delete the "Safe" duplicates in
> `MsToolAugmentEngine`, redirect callers.

Before executing the delete-and-redirect, per CLAUDE.md's root-cause discipline ("never stop at the
surface" / "verify before theorizing"), the claimed duplicates were read in full via `GetMethodSource`
and their caller sets enumerated via `FindReferences` - all through the RoslynSentinel MCP tools, not
raw file reads.

## Reproduction / evidence

**1. No method named `WrapInTryCatchAsync` exists in `InstrumentationEngine`.**

`RoslynSentinel.Advanced/InstrumentationEngine.cs` (152 lines, read in full) has exactly 3 methods:

- `AddTryCatchToMethodAsync` (lines 20-52): locates a method by name, wraps its entire body.
- `AddTryCatchToClassAsync` (lines 57-93): wraps all public methods of a class.
- `AddStopwatchDiagnosticsAsync` (lines 98-150): unrelated - adds Stopwatch timing
  instrumentation, not try/catch at all.

`RefactoringEngine.WrapInTryCatchAsync` (`RoslynSentinel.Basic/RefactoringEngine.cs`) has two
overloads instead:

- Overload 1 (lines 4372-4460): targets an explicit `startLine`/`endLine` range, finds the
  smallest containing `BlockSyntax`, wraps only the targeted statements.
- Overload 2 (lines 4466-4583): targets via `contextSnippet`/`lineBefore`/`lineAfter` instead of
  line/column.

None of `InstrumentationEngine`'s 3 methods matches either overload's targeting mechanism
(whole-method-by-name / whole-class vs. line-range / context-snippet). `FindReferences` confirms
`AddTryCatchToMethodAsync` has its own dedicated test suite (`InstrumentationEngineTests` in
`BatteryFiveTests.cs`, `BatteryTwentySevenTests.cs`, 4 callers) that depends on whole-method/
whole-class semantics no `RefactoringEngine` overload provides.

**2. The four `MsToolAugmentEngine` "Safe" variants are deliberate bug-fix rewrites with materially
different contracts, not redundant duplicates.** Each has a distinct signature, a distinct result
type or targeting mechanism, and dedicated production callers plus regression tests that assert on
the specific behavior the "canonical" method does not have (all counts from `FindReferences`):

- `GenerateToStringSafeAsync` (`MsToolAugmentEngine.cs:1194-1319`) vs
  `CodeGenerationEngine.GenerateToStringAsync` (lines 294-368): the Safe variant manually
  constructs `InterpolatedStringContentSyntax` to fix a CS8086 brace-escaping bug (inline comment
  references "MS Bug"); has a `members` override parameter (arbitrary prop/field list) the
  canonical version's `excludeProperties`-only parameter does not support the same way; returns a
  different result type (`MsAugmentResult` vs `GenerateToStringResult`). 29 callers across 5 files,
  including production call site `CodemodTools.cs:1560`, and dedicated regression test
  `GenerateToStringSafe_GeneratedCode_NoCS8086` (`AugmentToolsTests.cs:541`) that specifically
  exercises the bug the canonical version does not fix.
- `FormatDocumentSafeAsync` (lines 678-727) vs `RefactoringEngine.FormatDocumentAsync` (lines
  214-243): the Safe variant has an explicit `preview: bool` parameter (default true) and a
  disk-fallback read; the canonical version has no preview parameter at all and reads only from the
  loaded workspace (documented "READCHOKEPOINT-CAST" pattern, see
  `docs/current/design_read_chokepoint.md`). 13 callers, including production call site
  `CodemodTools.cs:233`, and dedicated tests `FormatDocumentSafe_Preview_DoesNotModifyDisk`
  (`FiveStarToolTests.cs:761`, `RegressionTests.cs:956`) and
  `FormatDocumentSafe_Apply_WritesDiskFile` (`FiveStarToolTests.cs:783`) that assert on the
  preview/apply distinction the canonical version cannot represent.
- `ExtractConstantSafeAsync` (lines 1043-1147) vs `RefactoringEngine.ExtractConstantAsync` (lines
  1768-1866, confirmed to exist at that range via outline, not yet read in full): the Safe variant
  locates the target literal via `contextSnippet`/`lineBefore`/`lineAfter`
  (`ContextHelper.FindSnippetPosition`) instead of line/column, explicitly fixing a claimed "Column
  99 is beyond end of line" UX bug per its inline comments, and replaces all identical literals in
  the file. 18 callers, including production call site `AdvancedRefactoringTools.cs:809`, and
  dedicated test `ExtractConstantSafe_ReplacesAllIdenticalLiterals` (`RegressionTests.cs:1222`).
- `ExtractMethodSafeAsync` (lines 1411-1819, ~408 lines) vs `RefactoringEngine.ExtractMethodAsync`
  (lines 559-778, not yet read in full): 27 callers, including two production call sites
  (`RefactoringExtractionDocsImpl.cs:311` and `AsyncifyTools.cs:2455` and `:3641` inside
  `RunHandlerExtractPhaseAsync`/`HandlerExtractCore`), plus behavior-specific tests (e.g.
  `ExtractMethodSafe_WholeBlockSnippetSpanningForeach_ExtractsEntireBlockAsOneCall`,
  `ExtractMethodSafe_SingleStatementSnippetInsideAccumulatorLoop_RefusesAmbiguousExtraction`) whose
  snippet-based extraction semantics were not verified to exist in the canonical overload.

## Root cause

Not fully traced to source - labeled as a hypothesis. The plan document
(`.claude/plans/enumerated-jumping-babbage.md`, group 4, third bullet) appears to have been
authored by pattern-matching on method **names** ("Safe" suffix, "duplicate of") without reading
either implementation's body, parameters, or caller set. This is the same category of defect
already documented for an earlier group of the same plan - see
`project_engine_reorg_group2_modernization_family_complete.md`'s note that the plan's
`SyntaxUpgradeEngine`/`SyntaxModernizationEngine` claim was also false on verification. The plan
document itself, not any RoslynSentinel tool, is the source of the false premise here; no MCP tool
returned incorrect data in the course of gathering this evidence.

## Why this blocks

Deleting any of these "Safe" methods and redirecting their approximately 29 + 13 + 18 + 27 + 4 = 91
call sites to the claimed "canonical" equivalent would either:

- fail to compile, because the canonical method's signature does not accept the same parameters
  (`contextSnippet`, `preview`, `members` list), or
- compile but silently regress real bug fixes and break the dedicated regression tests that exist
  specifically to guard those fixes (CS8086 escaping, preview-mode disk safety, ambiguous-snippet
  extraction refusal).

This is load-bearing for group 4's third bullet as literally written: it cannot be executed as
stated without either data loss (loss of already-fixed behavior) or build breakage.

## What unblocks it

- The plan's third bullet for group 4 needs to be revised, not executed as written, to reflect that
  these are not duplicates.
- Likely correct resolution for whoever picks this up: leave `MsToolAugmentEngine`'s 4 "Safe"
  methods and `InstrumentationEngine`'s 3 methods in place, untouched - do not delete or redirect
  them.
- If a true behavioral overlap is later found (e.g. after reading
  `RefactoringEngine.ExtractMethodAsync` and `ExtractConstantAsync` bodies in full, not yet done),
  re-evaluate it as a feature-parity question (does the canonical version need to gain the Safe
  version's capability) rather than a delete-the-duplicate operation.
- Open question for whoever revises the plan: was the "duplicate" claim based on anything beyond
  method-name similarity? If there is a specific reason (e.g. a design decision that these should
  converge despite the contract differences found here), that reasoning is not currently recorded
  anywhere this session found it.

## Related

- `.claude/plans/enumerated-jumping-babbage.md` - group 4 text, third bullet, quoted above verbatim.
- `project_engine_reorg_group2_modernization_family_complete.md` (memory) - prior instance of a
  false plan premise about `SyntaxUpgradeEngine`/`SyntaxModernizationEngine` in the same plan
  document.
- `docs/current/design_read_chokepoint.md` - documents the "READCHOKEPOINT-CAST" workspace-read
  pattern referenced above as one of the contract differences between `FormatDocumentAsync` and
  `FormatDocumentSafeAsync`.
