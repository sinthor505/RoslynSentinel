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

## Dog-fooding is mandatory — this is an instruction, not background

**All C# reads and writes, and all git operations, go through the RoslynSentinel MCP tools.** This
applies to work on RoslynSentinel's own source, which is nearly every task here — "I'm editing the
server itself" is *not* an exemption, and treating it as one has silently voided this policy in past
sessions.

| Instead of | Use |
| --- | --- |
| `Read` a `.cs` | `ReadFile`, `GetFileOutline`, `GetMethodSource` |
| `Grep` / `Glob` for C# symbols | `SearchSolutionText`, `LocateSymbol`, `FindReferences` |
| `Edit` / `Write` a `.cs` | `Member`, `MethodSignature`, `ModifyModifier`, `ReplaceSnippet`, `ApplyDiff`, `RenameSymbol` |
| `Bash(git status/log/diff/add/commit/revert)` | `Git(operation: ...)` |
| `Bash(dotnet build/test)` | `Build`, `RunTest` |

**Scope boundary:** non-C# files — `.md`, `.ps1`, `.json`, `.csproj` — are outside the Roslyn
workspace and have no tool coverage. Use the normal file tools for those directly; that is the edge
of where the tools apply, not a bypass. Likewise git operations the `Git` tool doesn't implement
(branch, push, checkout, worktree, stash) legitimately use the shell — see `docs/current/TODO.md`.

**Why it outranks convenience:** chokepointing every operation is the only way to surface
in-memory-vs-on-disk drift, multi-call sequencing bugs, and edge cases that never appear in an
isolated test. Falling back whenever a tool is awkward hides precisely the failures this exists to
find — and the awkwardness *is itself the finding*, per the failure doctrine below.

**A tool failure is a blocking finding.** If a needed MCP tool fails, returns wrong data, is
unreachable, or has no equivalent operation: finish any in-flight edit, stop advancing the task,
write `docs/current/blockers/blocking_error_<slug>.md` (slug from the real symptom — never a generic
name, since several can be open at once), and end the turn. Do not retry speculatively, do not route
around it with shell tools, and do not resume until told the issue is fixed.

A `PreToolUse` hook (`.claude/hooks/enforce-dogfood.ps1`) blocks the common violations mechanically,
because a rule enforced only by remembering decays across hundreds of calls. The hook is a backstop,
not the policy — it cannot see intent, and reading a `.cs` with `Read` still violates this section
even though nothing stops it. If the hook ever blocks something genuinely necessary, say so and stop;
do not reword the command to slip past it.

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
