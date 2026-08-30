---
name: project_changeaccessibility_enum_and_listall
description: "ChangeAccessibility converted to AccessibilityLevel enum; new ListAll tool for solution-wide symbol orientation, both committed de39a8d"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-30T09:49:21.498Z
---

`ChangeAccessibility`'s `accessibility` parameter changed from `string` to a new
`AccessibilityLevel` enum (`RoslynSentinel.Common/ToolEnums.cs`), using
`[JsonStringEnumMemberName]` to alias `protectedInternal`/`privateProtected` to their
space-separated wire values. This makes an invalid value rejected by JSON schema/binding
before `ChangeAccessibilityAsync` runs, closing the old silent-fallback-to-public bug
structurally (that fallback was already partially fixed as a runtime check in [[project_system_prompt_v1_result_and_modifymodifier_gap]]'s
`f3aeee1`; this segment replaced the runtime check with a type-level guarantee and
removed the now-unreachable `ChangeAccessibility_UnrecognizedValue_ReturnsError` test).

Added `ListAll` tool (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`): lists every
namespace/class/interface/struct/record/enum/enum member/constructor/field/method/property
in the loaded solution, one row per symbol (file, kind, name, container, line range),
filterable by `ListAllKind` enum and/or `projectName`. Implementation reuses
`GetFileOutline`'s extraction logic, factored out into a shared `ExtractOutlineItems(SyntaxNode root)`
static method — `ListAll` just runs it across every document in the solution. Added
`ResultWrapperType.SolutionSymbolEntryList` for the existing offload-to-disk pattern (see
[[project_offload_helper_partial_wiring]]).

**Why:** User's diagnosis, confirmed via LM Studio dev-console `reasoning_content` logs
across three temperature/repeat-penalty settings (0.1/1.1 default, 0.2/1.2, 0.5/1.2): the
model's search/discovery *reasoning* was correct but it kept guessing plausible-sounding
nonexistent identifiers and searching for each one individually rather than orienting
itself first, and didn't recover after repeated empty search results. User's framing
(verbatim, worth preserving): models "don't inherently think 'oh, I should just look
through every file until I find something that looks relevant'" — they're trained toward
efficiency/task completion in a few steps, so under sparse information they fabricate
plausible names instead. The fix direction is giving the model cheap orientation anchors
up front, not dumping full file contents (traditional shell-tool style) and not expecting
it to brute-force search.

The `.112` sweep's `TwoStep` prompt variant (`SizeThresholdAgentTests.cs`) succeeding 9/9
through file-size-60 does NOT contradict this — `TwoStep` names the target file/method
explicitly, sidestepping the discovery problem by prompt design. It tests a different,
easier sub-problem than the symptom-only `MinimalGuidance` prompt that motivated `ListAll`.

**How to apply:** `ListAll`'s tool description explicitly tells the model to call it FIRST
when it doesn't already know an exact symbol name, before guessing/searching. When
diagnosing future model-eval failures, check whether the failure is a *discovery* problem
(model doesn't know what exists) vs an *execution* problem (model knows the target but
botches the edit, e.g. ApplyDiff line-anchor drift, still unaddressed) — they need
different fixes.

Next natural step (not yet done, not yet requested by user): rerun
`Model_FixesWholeFileRewriteBug_MinimalGuidance` now that `ListAll` exists, to see if it
actually resolves the search-strategy-gap failure observed across all three settings.
