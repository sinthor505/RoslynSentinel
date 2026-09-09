---
name: feedback_dont_name_gated_tools_in_agent_docs
description: "When a tool is gated off from an agent, don't reference it by name in docs the agent reads — state the allowed workflow positively instead"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: ed9672c6-7cb1-472d-afcf-6da9436d8838
  modified: 2026-09-09T16:12:53.567Z
---

When writing plan/step docs for an agent, never explain a workaround by naming a tool that's
been disabled/gated for that agent (e.g. "`WriteFile` is gated off, so use X instead"). The agent
still sees the tool name in the doc, and since it can't find that tool in its actual tool list,
the mismatch confuses it — even though the doc's intent was to preempt exactly that confusion.

**Why:** surfaced 2026-09-09 while patching `plan-eval-defect-remediation-v2-steps/*.md` after
`WriteFile` was gated off for qwen3.6-35b-a3b runs (high rate of model misuse/failure) and
`CreateFile` was added as the scoped replacement (stub-then-populate via `Member(add)`). The
first patch pass explained the change by name ("`WriteFile` is gated off — use `CreateFile`
instead"), which the user flagged as reintroducing the same confusion it was meant to prevent.

**How to apply:** state the required tool/workflow positively and skip mentioning what's absent
— "Build this new file via `CreateFile` + `Member(add)`, not in one call" rather than "`WriteFile`
is gated off, so use `CreateFile` + `Member(add)`." Applies to any doc an agent will load as
working instructions, not just this plan. See [[project_createfile_tool_added_for_gated_writefile]]
for the underlying tool-capability context this rule was extracted from.
