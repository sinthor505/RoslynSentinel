# Finding: `Git` tool's array-typed parameters and invalid `operation` values crash with a raw `JsonException`

**Status:** confirmed tool defect, not yet fixed. Found during self-run
`manual-selfrun-20260914-004605`, step 06. Escalates a prior "silently mis-parsed"
characterization (`project_git_tool_defects_2026_09_12` memory) to a confirmed unhandled crash.

## Context

Three reproductions, same step:

1. `Git(operation: "log", paths: ["...cs"])` (array) — raw `JsonException`: "The JSON value
   could not be converted to System.String." The tool's own prior error message had suggested
   `paths` (plural) as the correct name, implying array usage — but array usage crashes.
2. `Git(operation: "stage", files: ["...", "...", "...", "..."])` (array, 4 elements) — same raw
   `JsonException`, same shape.
3. `Git(operation: "show", ...)` — crashes the same way ("The JSON value could not be converted
   to RoslynSentinel.Common.GitOperation") — `show` is not a valid `GitOperation` enum member,
   but the tool crashes instead of listing valid operations.

## Workaround (confirmed working)

Pass a single string, one call per file/path, instead of an array — e.g.
`Git(operation: "stage", files: "path/to/file.cs", scope: "listed")` repeated per file. For
inspecting a single commit's changes, use `Git(operation: "diff", commitHash: ...)` instead of
the invalid `show`.

## Why this matters

This is worse than the prior "silently mis-parsed" characterization from
`project_git_tool_defects_2026_09_12` — it's an unhandled crash, not silent misbehavior, and
another direct violation of CLAUDE.md's "never leak raw exceptions" rule. The plural parameter
names (`paths`, `files`) actively invite array usage, and the tool's own prior error text
reinforced that expectation.

## Recommendation

- Either truly support arrays for `paths`/`files` (most natural given the plural names and the
  multi-file `stage`/`commit` use case), or reject an array cleanly with an explicit message
  ("paths/files takes a single string per call; call once per file") instead of crashing.
- `operation` should reject unknown values (e.g. `show`) with the list of valid `GitOperation`
  members, not a raw deserialization exception.

## Reference

- Session log: `C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md`,
  Step 06 (~line 624).
- Related memory: `project_git_tool_defects_2026_09_12` (prior, less severe characterization of
  the array-param issue).
