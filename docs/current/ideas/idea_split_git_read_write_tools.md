# Idea: Split the `Git` tool into a read tool and a write tool

**Status:** NOT DESIGNED 2026-11-10. Raised during the claude-lean schema trim, when `Git` (4261 chars, untrimmed) would have been the largest tool after the trim.

## What happened

- In the implement run of 2026-11-10 for [proposal_reduce_tool_schema_token_cost.md](../proposals/closed/proposal_reduce_tool_schema_token_cost.md) (step "Amended (implement run 2026-11-10)"), `Git` at 4261 chars would have failed a 4500 per-tool budget with the 300-char headroom check.
- A GitRead/GitWrite split was raised there and recorded as an open TODO.md item; the proposal itself was moved to `proposals/closed/` with status PARTLY IMPLEMENTED, so this doc carries the open part.

## The actual gap

- One `Git` tool carries every operation in one enum and one description, so each call pays about 4.3k chars of schema and a weak model picks from a long operation enum.
- Surfaces that never commit (eval, PlanStepRunner) still see the write operations.

## Possible fixes (not decided)

1. **GitRead + GitWrite.** Each tool about 2.3k chars instead of 4.3k, with a shorter operation enum (helps weak models).
   - **Trade-off:** the claude-lean total grows about 300-600 chars (shared `reason`/`paths`/`ref` params, two descriptions).
2. **Split and gate the write tool off surfaces that never commit** (eval, PlanStepRunner). Only this variant shrinks the total on those surfaces.
3. **Leave as is.** The 2026-11-10 trim (468fb932) set budgets 43500 / 5000, so `Git` fits today.

Design in a fresh chat.

## Related

- [proposal_reduce_tool_schema_token_cost.md](../proposals/closed/proposal_reduce_tool_schema_token_cost.md) - origin, lines "Amended (implement run 2026-11-10)" and "Done 2026-11-10".
- TODO.md P3 "Split Git into read and write tools".
