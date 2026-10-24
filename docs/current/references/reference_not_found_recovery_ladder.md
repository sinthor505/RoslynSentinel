# Reference: Not-found / zero-result errors must run a recovery ladder

**Status:** CURRENT 2026-10-24. Policy written from the `Search` text-mode revision; the other tools have not been audited against it yet.

## Purpose
Rule for every tool that can report "nothing found" (text, symbol, member, method, file, snippet,
reference, type). Consult when writing or reviewing a not-found, zero-match, ambiguous or
invalid-argument path. Goal (CLAUDE.md mission): a weak model must be able to recover on the next
turn from the error text alone.

## How it works
A claim of absence is only allowed after the tool has ruled out the model's likely input mistakes.
Do this in order, cheapest first, and only on the failure path (hit-bearing calls stay single-pass):

1. **Scope.** Was the target filtered out before the search ran (test projects, `includeTests`,
   `projectName`, `fileGlob`, gated or excluded folders)? If matches exist in the hidden scope, say
   so with counts and the exact rerun arguments.
2. **Name shape.** Retry with the simple name when given a qualified one (`Foo.Bar.Baz` -> `Foo.Bar`
   -> `Baz`), the reverse, different case, generic arity stripped, `|` alternatives split.
3. **Container / path.** Wrong or stale file path, wrong containing type or namespace: look the
   member up solution-wide and name where it actually lives.
4. **Mode / kind.** The thing exists as another kind (method vs property vs field, type vs
   namespace): say which mode or operation finds it.
5. **Nearest matches.** Up to ~5 closest candidates (edit distance, same-token) with their paths.
6. **Only then** report absence, stating exactly what was searched (scope, filters, name forms
   tried) so the model can tell "does not exist" from "was not looked at".

Every error ends with the next call to make, with ready-to-use arguments. Relaxed matches are
suggestions, never returned as hits. A zero-result lookup is not an error when "none" is a valid
answer; it is an error only when the caller needed a target (edit, rename, move).

Reference implementation: `Search(mode: text)`, see
[plan_search_text_scan_all_projects_and_recovery_hints.md](../plans/plan_search_text_scan_all_projects_and_recovery_hints.md).

## Usage
Wording to avoid: "There are 0 matches for X in the solution" when scope, container or name shape
was not exhausted; any sentence that blames the model's argument ("glob problem") without having
verified it. Preferred shape:

`0 matches for 'Foo.Bar.Baz' in 38 projects (tests excluded). 8 for 'Foo.Bar'; 23 for 'Foo'; 4 more in test projects. Rerun with query: 'Foo.Bar' or includeTests: true.`

## Where to look for offenders (cheap, before any code review)
- `.claude/journal/<date>_<sid8>.calls.jsonl` has `tool`, `ok`, `errorCode`, but no error text.
  Not-found style failures: group `ok:false` rows by `tool, errorCode` for `NotFound`, `NoMatches`,
  `FileNotFound`, `MethodNotFound`, `Ambiguous`. `scripts/Get-JournalDigest.ps1` already summarises
  failed calls by tool and errorCode.
- Error text lives in the Claude transcripts (`~\.claude\projects\<repo>\*.jsonl`, `tool_result`
  blocks). `scripts/Get-NotFoundRecoveryReport.ps1` (about 6 s for 1,347 files) lists not-found style
  failures by tool and code, whether the message carries a next-step marker, the top message
  templates with a verbatim example, and what the model did next (recovered, retried, other MCP tool,
  fell back to shell). Options: `-Since`, `-Tool`, `-Top`, `-CsvPath`, `-Detail`. Heuristic: "next
  call" ignores parallel calls, and the next-step marker is a keyword match, so read the examples.
- Zero-result successes (`isError: false` with a `statusMessage` such as "No matches", "0 callers.")
  are reported with `Code = ZeroResult` (skip them with `-ErrorsOnly`). The match is a keyword
  regex on `statusMessage`, so tools that signal emptiness only through `totalRecords: 0` or an empty
  `findings` array are not seen. Not covered: model-eval / PlanStepRunner logs (separate formats).
- Measured 2026-10-24 over 116 journals (33,361 calls, 1,903 failed): not-found style codes were
  `Search` NotFound 85 / NoMatches 76 / Ambiguous 39, `ReplaceSnippet` NotFound 71 / Ambiguous 23,
  `GetMethodSource` MethodNotFound 23 / FileNotFound 12, `ReadFile` FileNotFound 21,
  `FindReferences` NotFound 15, `GetFileOutline` FileNotFound 15, `InspectSymbol` NotFound 14,
  `Member` NotFound 7.

## Gotchas
- Do not leak raw exceptions, stack traces or internal paths while building the hint (CLAUDE.md).
- `ToolErrorMapper.ToResultError` (`RoslynSentinel.Common/ToolException.cs`) currently repeats the
  message in `Detail`; put ladder results in `Message` once, not in both.
- Ladder passes must stay bounded (cap candidates, no regex relaxation) so a failure path never
  costs more than one extra scan.
