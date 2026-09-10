# Step 0 — Baseline

## Prior state

Nothing has changed yet. This is the first step.

## Task

Run the full test suite once, before touching any code, to record a clean pass count. Every
later step's gate compares against this baseline.

Use the `RunTest` MCP tool (or run each test project individually if that's more reliable) to
run:

- `RoslynSentinel.Tests.Basic`
- `RoslynSentinel.Tests.Battery`
- `RoslynSentinel.Tests.Asyncify`

Record the pass/fail/skip counts for each project. If any tests are already failing before you
make any changes, note which ones — do not treat pre-existing failures as something you caused,
and do not attempt to fix them as part of this work.

## Gate

No gate — this step only records a number. Proceed to
[02-phase1-types.md](02-phase1-types.md) once you have the baseline counts.
