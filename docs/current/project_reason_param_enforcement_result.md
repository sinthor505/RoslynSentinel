---
name: project_reason_param_enforcement_result
description: "qwen3.5-9b-coder only supplies an optional \"reason\" tool param once it's truly enforced (real C# required param), not when merely marked required in the JSON schema"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-04T22:57:01.924Z
---

Three-state smoke test on .113 (qwen3.5-9b-coder, `PlanOnly`) of the experimental `reason`
tool parameter (a free-text "why are you calling this tool" field, added across all 105 MCP
tools for transcript-review purposes):

1. **Optional** (`string? reason = null`, description says "Optional"): model never sent it,
   0/5 tool calls.
2. **Required in JSON schema only** (commit 948f672, `ServerStartupHelpers.RequireReasonParameter`
   post-hoc-mutates the schema; C# signature stays `string? reason = null`, unenforced): model
   still never sent it, 0/8 tool calls — and no call was ever rejected for omitting it, confirming
   the enforcement was purely declarative.
3. **Required as a true C# parameter** (commit 82ada71, `string reason` with no default — a call
   omitting it now genuinely fails): model supplied it in 5/5 tool calls, immediately, with
   specific and coherent explanations (e.g. "Investigate bug in BlockConverter.cs where editing
   shapes causes unrelated formatting changes"). No retries needed — it complied from turn 1.

**Why:** This 9B model does not appear to read/respect a JSON schema's `required` array as a
behavioral signal on its own — it only responds to parameters whose absence actually breaks the
call. Schema-level hints without real enforcement are inert for this model class.

**How to apply:** Don't rely on marking an MCP tool parameter `required` in schema alone to change
small local-model behavior — if a parameter matters enough to want reliably, enforce it as a true
non-optional argument (real call failure on omission), per commit 82ada71's approach. This
generalizes beyond `reason`: any future "please always include X" tool-design idea for this model
tier should assume schema hints are ignored unless backed by an actual rejection path.
