---
name: failure-analysis
description: Failure doctrine and root-cause discipline for model-eval, PlanStepRunner and task failures. Load before analysing or writing up any such failure.
---

# Failure doctrine and root-cause discipline

This is the governing frame for interpreting **every** model-eval run, PlanStepRunner step, and task
failure in this repo.

The model under test is a **novice**. The environment - server, tools, schemas, tool descriptions,
error messages, prompts, harness, test assertions - is the **expert**. When the novice fails, that is
first a failure of the expert to guide, constrain, or protect, even when the model is plainly wrong.
The question is never "was the model wrong?" but:

> **What change to the environment would have prevented this, made it impossible, or made recovery
> immediate?**

Environment fixes look like: a clearer tool `[Description]`; a schema that makes the invalid call
unrepresentable; a required parameter instead of an optional one that relocates the failure; an error
message that names the exact parameter and the correct value; a guardrail that halts before corruption
instead of after; a preview/dry-run affordance so the model can check instead of guess.

"The model should have known X" is not an actionable finding.

## Root-cause discipline: never stop at the surface

Restating the log ("the model called ModifyEnum with invalid parameters, burned 4 turns, then used a
different tool") is not a root cause. Tool results can be wrong, misleading, or truncated: a "success"
does not prove the write landed, and an error message does not prove its own stated reason is the real
one. For any failed or inefficient model action, trace to source:

1. **What did the environment actually tell the model?** Read the tool's real `[Description]`,
   parameter `<summary>` docs, and the **schema actually emitted** (not the C# signature - emitted
   schemas have differed from what the source implied). Was the call reasonable given only that?
2. **Why did the call actually fail?** Read the implementation and find the specific branch that
   produced the error; the message may be generic or describe a symptom of a different fault.
3. **Did the error message enable recovery?** Did it name the offending parameter and a correct value?
   Slow recovery is an error-message defect, not model slowness.
4. **Is the harness or assertion at fault?** Rule out a wrong assertion, drifted fixture, prompt
   omission, or stale server binary before blaming the model-facing path.
5. **Cite evidence.** Every claimed cause gets a `file:line`, a quoted error string, or a turn number.
   A cause not traced to source is a hypothesis - label it as one.

Check the literal error's named identifier against the actual source before theorising.
