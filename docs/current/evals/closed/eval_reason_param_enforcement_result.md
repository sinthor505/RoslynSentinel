---
name: project_reason_param_enforcement_result
description: "qwen3.5-9b-coder went from never supplying an optional \"reason\" tool param to always supplying it once made a true required C# param + description reworded; enforcement vs wording not isolated"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-04T22:58:21.645Z
---

Three-state smoke test on .113 (qwen3.5-9b-coder, `PlanOnly`) of the experimental `reason`
tool parameter (a free-text "why are you calling this tool" field, added across all 105 MCP
tools for transcript-review purposes):

1. **Optional** (`string? reason = null`, description says "Optional"): model never sent it,
   0/5 tool calls.
2. **Required in JSON schema only** (commit 948f672, `ServerStartupHelpers.RequireReasonParameter`
   post-hoc-mutates the schema; C# signature stays `string? reason = null`, unenforced; description
   still said "Optional..."): model still never sent it, 0/8 tool calls — and no call was ever
   rejected for omitting it, confirming the enforcement was purely declarative.
3. **Required as a true C# parameter** (commit 82ada71, `string reason` with no default — a call
   omitting it now genuinely fails; description's leading word changed "Optional." → "Required."):
   model supplied it in 5/5 tool calls, immediately, with specific and coherent explanations (e.g.
   "Investigate bug in BlockConverter.cs where editing shapes causes unrelated formatting changes").
   No retries needed — it complied from turn 1.

**Confound, not fully isolated:** state 2→3 changed two things at once — schema+signature
enforcement AND the description's leading word ("Optional." → "Required."). The rest of the
description ("Not validated or acted on") was unchanged and present in the winning run too, so the
hedge alone didn't block compliance once something else changed — but whether the real enforcement,
the word "Required.", or both together did the work isn't separated by this test. A clean follow-up
would flip only the description word with schema/signature left at state 2 (or vice versa) to
attribute the effect properly.

**Why:** Some combination of true parameter enforcement and/or explicit "Required." wording moved
this 9B model from never supplying `reason` to always supplying it; a schema-only `required` marker
with an unchanged "Optional" description did nothing on its own.

**How to apply:** Don't rely on marking an MCP tool parameter `required` in schema alone to change
small local-model behavior. If a parameter matters enough to want reliably, enforce it as a true
non-optional argument (real call failure on omission) per commit 82ada71's approach, AND make sure
its description doesn't still say "Optional" — keep both levers aligned rather than assuming one
alone (untested in isolation here) is sufficient.
