# RoslynSentinel

An MCP server exposing Roslyn-backed C# analysis and refactoring to AI agents. It bridges what a
language model can reason about and what the compiler actually knows.

## Mission

Enable agents — **especially weak or self-hosted models** — to safely and efficiently navigate and
refactor C# codebases. The explicit goal is to let a small local model punch above its weight: it
should drive meaningful, semantically-correct refactoring by calling structured tools, never by
falling back to grep, regex search-and-replace, file-read loops, or manual text edits.

If an agent reaches for a shell command to inspect or modify C# code, that is a gap in the tool
surface, not a deficiency in the agent.

## Failure doctrine: the environment is responsible

This is the governing frame for interpreting **every** model-eval run, PlanStepRunner step, and
task failure in this repo.

The model under test is a **novice**. The environment — server, tools, schemas, tool descriptions,
error messages, prompts, harness, test assertions — is the **expert**. When the novice fails, that
is first and foremost a failure of the expert to guide, constrain, or protect.

This holds *even when the model is plainly wrong*. A model calling a tool with invalid parameters
is not the end of the analysis; it is the beginning. The question is never "was the model wrong?"
(usually yes, and you cannot change the model). The question is:

> **What change to the environment would have prevented this, made it impossible, or made recovery
> immediate?**

Physical-safety analogues for the kind of answer we want:

- A welder wears a helmet — the hazard is not removed, but the operator is protected by default.
- A stove has a "surface hot" light — invisible danger made visible before contact.
- An electrician uses a non-contact voltage detector — a safe way to check, instead of repeatedly
  grabbing the wire to find out.

Environment fixes look like: a clearer tool `[Description]`; a schema that makes the invalid call
unrepresentable; a required parameter instead of an optional one that relocates the failure; an
error message that names the exact parameter and the correct value; a guardrail that halts before
corruption instead of after; a preview/dry-run affordance so the model can check instead of guess.

Blaming the model, or reporting "the model should have known X", is not an actionable finding.

## Root-cause discipline: never stop at the surface

Skimming a transcript and concluding *"the model called ModifyEnum with invalid parameters, the
value wasn't added, it burned 4 turns and eventually succeeded with a different tool"* is an
accurate **restatement of the log**. It is not a root cause, and it will never expose an
environmental defect.

Taking transcripts and tool results at face value is the single most common analysis failure in
this repo. Tool results can be wrong, misleading, or silently truncated; a "success" result does
not prove the write landed, and an error message does not prove its own stated reason is the real
one.

For any failed or inefficient model action, trace to source and gather evidence:

1. **What did the environment actually tell the model?** Read the tool's real `[Description]`,
   parameter `<summary>` docs, and the **schema actually emitted** (not the C# signature — this
   repo has a history of schema-emission bugs where the emitted JSON schema differed from what the
   source implied). Was the model's call reasonable given only what it could see?
2. **Why did the call actually fail?** Read the tool's implementation and find the specific branch
   that produced that error. The message may be inaccurate, generic, or describing a symptom of a
   different underlying fault.
3. **Did the error message enable recovery?** Did it name the offending parameter and a correct
   value, or merely reject? Count the turns to recovery — a slow recovery is an error-message
   defect, not model slowness.
4. **Is the harness or assertion itself at fault?** A test can fail because the assertion is wrong,
   the fixture drifted, the prompt omitted something, or the server binary is stale. Rule these out
   rather than assuming the model-facing path is where the bug lives.
5. **Cite evidence.** Every claimed cause gets a `file:line`, a quoted error string, or a specific
   turn number. A cause you have not traced to source is a hypothesis — label it as one.

Prefer verification over theorising: check the literal error's named identifier against the actual
source before constructing an explanation for it.

## Working conventions

- Build (0 errors) before committing.
- Never leak raw exceptions, stack traces, or internal paths into a tool's `ResultError` — catch at
  the tool boundary and return a structured, actionable error.
- Any unhandled `CS####` surfacing during automated work gets a writeup in
  `docs/current/blockers/` immediately, not deferred.
- `docs/current/TODO.md` is open-items-only; resolved entries move to `docs/current/CLOSED.md`.
- `*/Worktree/` folders under a PlanStepRunner run are harness clones — exclude from diffs,
  searches, and reviews. Never run git commands inside one and treat the output as authoritative.
