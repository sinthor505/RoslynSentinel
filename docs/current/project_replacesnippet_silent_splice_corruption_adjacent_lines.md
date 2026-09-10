---
name: project_replacesnippet_silent_splice_corruption_adjacent_lines
description: "FIXED 2026-09-10 — ReplaceSnippet reported success but spliced leftover characters onto adjacent short lines sharing a trailing substring (e.g. two lines both ending in \"Count,\")"
metadata: 
  node_type: memory
  type: project
  modified: 2026-09-10T01:05:23.226Z
  originSessionId: d5420069-fa31-4691-9972-17cc57de8126
---

In PlanStepRunner run `20260910-002952-695/01-baseline` (eval agent reshaping `BuildResult.cs` per plan step 1.1), `ReplaceSnippet` corrupted the file **twice while reporting `success:true`** both times:

1. Turn 14: model replaced a 4-line block ending `int ErrorCount,` with a 5-line block (inserting `ProjectsCompiled`/`DiagnosticsComplete` above it) also ending `int ErrorCount,`. Result landed as `int ErrorCount,nt,` — a leftover `nt,` fragment fused onto the new line, confirmed by immediate `ReadFile`.
2. Turn 18: model's own corrective `ReplaceSnippet` call triggered the *same bug* one line down, turning `int WarningCount,` into `int WarningCount,,` and silently dropping the already-inserted `int? ExitCode = null` field entirely.

Both calls returned `"success":true` — no error surfaced. The model caught the mangling itself via a follow-up `ReadFile` + reasoning ("I see the tool mangled the line") and self-corrected both times; final file was clean.

This is a distinct bug from [[project_diffengine_anchor_collision_duplicate_trivia]]: that case is `ApplyUnifiedDiff` correctly *rejecting* (`DiffApplyFailed`) when a hunk anchor is ambiguous across two sibling methods. This case is `ReplaceSnippet` **silently succeeding with corrupted output** when two adjacent short lines share a trailing substring (both end in `Count,`) — worse, because there's no error to catch it; only an explicit re-read caught it here.

**Root cause found**: `ContextHelper.FindSnippetPosition`/`FindAllSnippetMatches` only ever returned a start offset (`int`), never the real matched length. `ReplaceSnippet` (`SentinelWorkspaceTools.cs`) did `oldText.ToString().Remove(pos, oldContent.Length).Insert(pos, newContent)` — assuming the matched span was exactly `oldContent.Length` chars. True for the literal/CRLF-normalized match paths, but FALSE for `ContextHelper`'s two whitespace-collapsing fallback paths (single-line and multi-line window), where the raw source span can be a different length than the literal snippet that matched it (different indentation/spacing). When a fallback fired, `Remove` cut the wrong number of characters, leaving fragments (the `nt,`/doubled-comma symptoms).

**Fixed 2026-09-10** (`RoslynSentinel.Common/ContextHelper.cs`, `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`, `RoslynSentinel.Tests/ContextHelperTests.cs`):
- Added `ContextHelper.SnippetMatch(int Start, int Length)` plus `FindAllSnippetMatchesWithLength`/`FindSnippetPositionWithLength` — same matching behavior as before (all 4 fallback tiers), but each match now carries its true source-span length. Old `int`-returning methods are now thin wrappers (`.Start`) — zero behavior change, all pre-existing tests pass unmodified.
- Added a **strict** pair, `FindAllExactSnippetMatches`/`FindExactSnippetPosition` — only literal-ordinal + CRLF-normalized matching, never the whitespace-collapse fallback. `ReplaceSnippet` now calls this strict variant and uses `match.Length` (not `oldContent.Length`) for the `Remove`/`Insert` splice. A snippet that only matches approximately now fails loudly ("not found verbatim... re-read the file") instead of silently corrupting.
- `ApplyDiff`/`ApplyUnifiedDiff` (gated tools) untouched — they still use the original loose `ContextHelper` API, per user's explicit direction to keep two separate anchoring strictness levels rather than tightening shared behavior.
- 9 new tests added to `ContextHelperTests.cs` covering length-correctness of both fallback paths, an end-to-end repro of this exact corruption pattern (now proven fixed), and the new strict API's rejection of approximate/differently-indented snippets.
- Left as out-of-scope follow-up: `MsToolAugmentEngine.cs`'s extract-method selection-span logic (~line 1467) also derives a `TextSpan` length from `contextSnippet.Length` post-`FindSnippetPosition` — flagged as a secondary, logic-only risk (could mis-include/exclude a statement at the selection boundary) by the pre-fix audit, but not a text-corruption bug and not fixed here.
