# Rename-Locator Plan — Step Index (PlanStepRunner variant)

**This directory is the single source of truth for the executable plan.** The original combined
design lives at `docs/current/plans/plan_rename_symbol_snippet_locator.md` (background/rationale
only, not executable as-is), and its three per-concern sub-plans live at
`docs/current/plans/plan_rename_locator_step1_encode_decode.md`,
`plan_rename_locator_step2_locate_symbol_snippet_mode.md`, and
`plan_rename_locator_step3_rename_symbol_decode.md`. This directory adapts those three sub-plans
into the step-file format `RoslynSentinel.Tools.PlanStepRunner` consumes (via
`roslynsentinel-planstep.ps1`), so they can be run against a real model end to end.

Each step file is a fully self-contained task, run by the runner as its own fresh, isolated model
conversation with no memory of any other step. Each step restates whatever prior-step context it
depends on, and explicitly names the file(s) it implements — the model must not read, open, or
act on any other step file (via `ProjectDoc` or otherwise), and must stop after its own gate
rather than continuing on to the next step itself.

This directory lives under `docs/testing/` (not `docs/current/plans/`) so the runner's
`--testing` flag can scope `ProjectDoc` to it without colliding with any same-named production
doc, per the existing convention established in
`docs/testing/plan-eval-defect-remediation-v2-steps-runner/00-index.md`.

Run steps **strictly in order**. Each step ends with its own local build/test gate; do not start
the next step until the current one's gate passes.

Per `feedback_dogfood_mcp_blocking_errors.md`: every read/write goes through the RoslynSentinel
MCP tools (`ReadFile`, `ApplyDiff`/`ApplyUnifiedDiff`, `Member`, `ModifyModifier`, `CreateFile`,
etc.) — never plain filesystem edits. Any MCP tool failure or gap encountered while executing a
step is itself a blocking finding: stop immediately, write
`docs/current/blockers/blocking_error_<slug>.md` describing it, and end the turn rather than
routing around it.

**There is no single tool that creates a `.cs` file with its full content in one call.** Creating
a brand-new `.cs` file always means a two-step sequence:

1. `CreateFile` — stubs the file. For a `.cs` file this requires `namespaceName`, `typeKind`
   (class/record/interface/enum/struct/**staticClass**), and `typeName`, and produces exactly
   `namespace {namespaceName};\n\npublic {modifiers} {kind} {typeName}\n{\n}\n` (`typeKind:
   staticClass` gives `public static class`; the others give a plain `public {kind}`). It cannot
   take free-form content — there is no content parameter. It fails if the file already exists.
2. `Member(add)` — populate the stub, one call per method/property/field/nested-type. Each call
   takes a full `newMemberSource` (including its own XML doc comment) and a `position`
   (`"end"`/`"after:X"`/`"before:X"`).

Additional small tools for the same workflow: `UsingDirective(add)` for imports the stub doesn't
carry; `ModifyModifier` for any other modifier `CreateFile`'s `typeKind` options don't cover.

Any step below written as if a new file's full content lands in one call is describing the
*intent*, not a literal tool call — decompose it into `CreateFile` + `Member(add)` (+
`UsingDirective`/`ModifyModifier` as needed) when executing.

## Steps

| # | File | What it does |
|---|---|---|
| 0 | [01-baseline.md](01-baseline.md) | Run full test suite once, record clean pass count |
| 1 | [02-locator-encode-decode.md](02-locator-encode-decode.md) | Add `SnippetSymbolLocator` type with `Encode`/`TryDecode` + tests |
| 2 | [03-locate-symbol-snippet-mode.md](03-locate-symbol-snippet-mode.md) | Add `contextSnippet` search mode to `LocateSymbol` + tests |
| 3 | [04-rename-symbol-decode.md](04-rename-symbol-decode.md) | Make `RenameSymbol` decode the locator token as an alternate input shape + tests |

## Why this split

Each of the three sub-plans under `docs/current/plans/` was already scoped to one coherent unit
of work with its own build-and-test gate, so each becomes exactly one step file here, in order,
with a leading baseline step. No technical content, decision, or file/line reference changes from
the source sub-plans — this only:

- Converts each sub-plan's "Prerequisite" section into a "Prior state" section restating what the
  previous step must have already produced.
- Adds an explicit "Files this step touches" list and a "Gate" section ending in an explicit stop
  instruction, matching this directory format's convention.
- Drops the design-discussion content (rejected alternatives, cross-plan rationale) that isn't
  needed by a model executing the step — that content stays in `docs/current/plans/` for human
  reference.

If a step proves too large for a single turn in practice, split it further along its own internal
numbered sub-tasks rather than re-deriving new boundaries.
