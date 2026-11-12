# Reference: tool error and hint text carries facts and options, never policy

**Status:** CURRENT 2026-11-12. Owner decision 2026-11-12 (plan_not_found_error_orientation, Decision 10).

## Purpose

Rules for writing any text a tool returns to a model: errors, not-found messages, zero-result notes, hints,
`findings`, descriptions of what to try next. Consult it before adding or rewording such text, and when an
eval failure tempts you to "fix" it by telling the model what to do in a tool message.

The tool surface serves every caller: Claude, weak self-hosted models, model-eval agents, and any task,
including one that really does want what a policy sentence would forbid. Text that is true for every caller
stays; text that is right only for some callers' instructions belongs in the prompt.

## How it works

Every sentence in tool text is one of three kinds:

| Kind | Belongs in tool text? | Examples |
| --- | --- | --- |
| **Fact**: what is true about the request, the workspace or the search | Yes | "File 'X.cs' was not found. Searched: exact path, bare file name and similar names in 12 projects." / "No file in the solution has a similar name." / "Casing is not the cause." / "It does not exist in this solution." |
| **Option**: what the caller can do, as a possibility with arguments prefilled | Yes | "Did you mean OrderPricingCalculator.cs? -> ReadFile(filePath: ...)" / "ListSolutionItems(kind: all) lists every file." / "GetFileOutline(filePath: ...) shows what the file declares." |
| **Policy**: what the caller ought to do or must not do, which depends on its task and instructions | No (put it in the prompt) | "Stop and tell the user." / "Do not create it." / "Tell the user so instead of editing a different file." / "You MUST call ListSolutionItems." / "If your task is to create this file, call CreateFile." |

Rules:

1. **State facts first, completely.** The CLAUDE.md not-found rule applies: rule out scope, name-shape,
   container and kind mistakes, and say what was searched, before claiming absence. A precise fact ("nothing
   similar exists in the 12 projects searched") does more for a weak model than an order.
2. **Name next calls as options, not orders.** Prefill arguments from the request. Name a tool only when it
   is callable on the active surface (`isCallable`), and build a `ToolCallHint` only when it will be rendered.
3. **No policy.** No imperative about what the caller should do with the result: no "stop", "report",
   "do not edit/create", "MUST", and no conditionals on the caller's task ("if your task is to ..."). The
   server cannot see the task. Such a sentence is policy that asks the model to check its own instructions.
4. **No eval vocabulary.** No BLOCKED, scenario, grader or system-prompt phrasing in tool text.
5. **Behaviour policy lives in prompts.** "Stop and report when the target is missing" is the eval agent's
   system prompt's job. In normal use it comes from the user, CLAUDE.md or the model's own judgment.

Rewrite examples:

- Policy: "If the file is not in that list it does not exist in this solution: tell the user so instead of
  editing a different file." -> Fact and option: "ListSolutionItems(kind: all) lists every file. A file not in
  that list does not exist in this solution."
- Policy: "If your task is to create this file, call CreateFile(...)." -> Option: "CreateFile(filePath: ...)
  creates a new file." (only if the option is worth naming at all for this error).
- Policy: "You MUST call ListSolutionItems(reason, kind: all)." -> Option: "ListSolutionItems(kind: all)
  lists every file in the solution."

## Usage

- When a model-eval or PlanStepRunner failure points at tool text, first ask whether a missing or wrong
  **fact** or **option** caused it (failure-analysis skill). If the fix you want is a policy sentence, the
  environment change belongs in the prompt or the scenario, not the tool.
- Do not tune tool text toward one model's pass rate at small sample sizes; measure, do not target.
- A server mode or flag that changes tool text only for evals is ruled out: evals would then measure a
  surface that real callers never see. A guidance-level profile is acceptable only if production weak-model
  use gets the same text.

## Gotchas

- Older text in the tree predates this rule and contains policy, for example the CreateFile sentence and the
  "tell the user so ..." endings in `RoslynSentinel.Common/FileNotFoundMessage.cs` and
  `RoslynSentinel.Common/SymbolNotFoundDetail.cs`, and the "You MUST call" text in `OrientationCircuitBreaker`.
  Strip policy when you touch such text; do not copy it as a pattern.
- A warning that is true for every caller is a fact, not policy: "the near match is in a different file than
  the one you named" is fine.
- Tests pin wording. When you strip policy, change the assertion to check the fact or option that remains;
  never just delete it.
