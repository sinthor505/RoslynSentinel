---
name: plan-writer
description: Checks and slicing rules for writing a plan doc (docs/current/plans/plan_<slug>.md) that a dispatcher can run without repairing it - verified names, contract-skeleton first step for new code, slices sized to implementer tiers, runnable Done-when, lane and tool per slice. Load before writing or reworking a plan, or brief a plan-writing subagent with this file's path.
---

# Writing a plan a dispatcher can run as written

The shape (sections, step fields, slicing comment) is `docs/current/templates/plan.md`; follow it.
This skill adds the checks a template cannot express. Every mistake left in a plan is paid once per
slice by a Haiku implementer, so fix it here.

## 1. Verify every name

- Each file path, type, member and signature the plan names: confirm it exists with `Search`
  (`mode: symbol` or a declaration-kind mode) or `GetFileOutline`. A name that will be created by the
  plan is marked `(new)`.
- Quote existing signatures as they are in source, not from memory or another doc.
- For a change to an existing member, measure its blast radius (`InspectSymbol(aspect: blastRadius)` or
  `Search(mode: references)`) and put the count in the step; a large count makes it a sweep or a
  migration-first sequence (new member beside the old, `[Obsolete("Use X")]`, callers in slices).

## 2. Contract skeleton first (new code)

When the plan adds new types or members that reference each other, Step 1 lands them as a skeleton in
one compile-green write: types, fields, properties, and every method with its final signature and a
body of `throw new NotImplementedException(); // TODO implement (Step <n>)`. The server rejects only
errors an edit introduces, so once the skeleton is in, bodies, callers and tests can be written in any
order and in parallel lanes, with no co-dependent edit ordering.

- One write per file (`CreateFile`/`WriteFile` for a new file, one `batchEdits` call for an existing
  one), definitions before users. Name that call in the step.
- Signatures in the skeleton are the contract the later slices rely on. A signature you are unsure of
  goes in "Risks and open decisions" with the slices that depend on it.
- A new `[McpServerTool]` stays out of `ToolClassRegistry.cs` and the DI blocks until its body is
  implemented; registration is its own later step.
- The last step searches for leftover stubs (`Search(mode: text, query: "NotImplementedException")`
  over the plan's files) and fails if any remain. Stubs compile and build clean, so nothing else
  catches them.
- Not for a single-slice change, or for edits to existing code (use migration-first there).

## 3. Size each slice

- Read `.claude/implementer-tiers.json` and size each slice to the current depth (`D`) or sweep (`S`)
  tier: files, edits, call sites, projects. Over the limit: split it, or mark it `implementer-senior`.
- Per slice, state the lane (`implementer`, `implementer-sweep`, `implementer-senior`, `test-writer`)
  and, for a sweep, the exact call-site-updating tool call (`RenameSymbol`, `ChangeSignature`,
  `MethodSignature`) or the repeated `ReplaceSnippet` batch pattern.
- Slices meant to run as a pair touch disjoint files, preferably different projects.

## 4. Done when is a call

Write each "Done when" as something the implementer can run: `Build` 0 errors (name the level when it
matters), or a named diagnostic gone. Tests are named (class and method) for the dispatcher to run;
implementers never run tests.

## 5. Before handing over

- Every step has Files, Depends on (the real prerequisite), Effect, Change, Done when, lane.
- Open questions are in "Risks and open decisions" with options and a recommendation, not routed around.
- DTO additions follow the `dto-conventions` skill (reuse or a NEW-DTO justification in the step).
- A new MCP tool step lists all three registration points (CLAUDE.md, Architecture).
