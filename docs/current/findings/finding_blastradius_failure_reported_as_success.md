# Finding: InspectSymbol blastRadius reports a failed lookup as a successful "0 call sites"

**Status:** FIXED 2026-10-04 (commit 3b29b07, both `blastRadius` and `info`). Recommendations 2-3 and the `info` error-code question remain open. Matters because blastRadius is the measuring step of the implementer slice contract (CLAUDE.md).

## Context
Checking whether the existing blast-radius capability could gate `implementer` slice sizing. It is not a
standalone tool: it is `InspectSymbol(aspect: blastRadius)` (`Tools.Basic/SymbolNavigationTools.cs`,
`Tools.Basic/SymbolNavigationImpl.cs` `InspectSymbol`), active in claude-lean via `SymbolNavigationTools`.
`McpServerStatus(toolListing: inactive, toolNameFilter: BlastRadius)` returns an empty list because the
name is an aspect value (`Common/ToolEnums.cs` `InspectSymbolAspect.blastRadius`), not a tool.

## What is broken
Live call with a snippet that does not exist in the file:

    InspectSymbol(filePath: ...ImpactAnalyzer.cs, contextSnippet: "ThisTextDoesNotExistAnywhere", aspect: blastRadius)
    -> "isError":false, "successData":{"symbolName":"","symbolKind":"","references":[],
       "totalCallSites":0,"affectedProjectsCount":0,"error":"An exact match could not be located ..."}

The failure is carried only in the `successData.error` field. `isError` is false and the headline
numbers are zero, which reads as "nothing calls this - zero blast radius". For a caller that uses the
number to decide how big a slice is, that is the unsafe direction.

Working call for comparison (`ImpactAnalyzer.AnalyzeImpactAsync`): `totalCallSites: 2`, `affectedProjectsCount: 2`, two references with path/line/column/preview.

## Root cause
`Engines.Basic/ImpactAnalyzer.cs` `AnalyzeImpactAsync` never throws: every failure (document not found,
no semantic model, no symbol, any exception via its outer `catch`) returns
`new ImpactReport("", "", [], 0, 0, Error: ...)`. `SymbolNavigationImpl.InspectSymbol` wraps that report
in `IsSuccess = true` unconditionally, so the request pipeline's domain-failure sync (response
`isError`) never sees it.

The `info` aspect had the same defect, found while fixing this (the first version of this finding
wrongly said `info` was fine). `SymbolNavigationEngine.ErrorHoverInfo` returns a normal
`SymbolHoverInfo(Kind: "Error", FullSignature: "", Error: <message>)`, never null, so
`InspectSymbol`'s `symbolInfo == null` guard never fired for engine-reported failures (file not found,
snippet not found, no symbol, no semantic model) and they came back as `IsSuccess = true`.

## Why it matters
- Slicing decisions use `totalCallSites` / `affectedProjectsCount`; a silent zero produces an under-sized
  estimate and a brief that sends Haiku into a wide change.
- Related gaps for slicing use, not defects: one symbol per call; no distinct-file count; project
  count without project names; no staging suggestion. Callers must count files from `references`.

## Recommendation
1. DONE in 3b29b07: `InspectSymbol` maps a non-empty `ImpactReport.Error` (`NotFound`) and a
   `Kind == "Error"` hover (`Exception`, message from `SymbolHoverInfo.Error`) to `IsSuccess = false`.
   Regression tests in `BatteryTwentyTwoTests`: `GetBlastRadius_UnresolvableSnippet_ReturnsNotFoundError`,
   `GetBlastRadius_ValidMethod_ReportHasNoErrorAndNamesSymbol`,
   `InspectSymbol_UnresolvableSnippet_InfoAndBlastRadiusAgree`. A server binary older than that commit
   still shows the old behavior (`isServerBinaryStale`).
   Open follow-up: `info` reports not-found as `ToolErrorCode.Exception` while `blastRadius` uses
   `NotFound`; align them.
2. Open: add `affectedFileCount` and project names to the report so callers need not post-process
   a potentially large reference list. Cheap, additive.
3. Open: a multi-symbol or staging-suggesting wrapper is only worth building if journal data from the
   slice contract shows single-symbol measurement is the bottleneck.

CLAUDE.md and `implementer.md` keep telling callers to check for a non-empty `error` as a cheap guard
against an older server binary.
