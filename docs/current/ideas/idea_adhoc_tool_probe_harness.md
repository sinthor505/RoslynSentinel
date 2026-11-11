# Idea: ad hoc tool probe - a session gives a prompt, a live model runs a few turns, the trace comes back

**Status:** NOT DESIGNED 2026-11-11. Owner's follow-up to the targeted scenario eval plan: quick model feedback for a session that is changing a tool or an error hint.

## What happened

While planning short targeted model-eval scenarios
([plan_targeted_tool_scenario_evals.md](../plans/plan_targeted_tool_scenario_evals.md)), the owner
described the next step: a generic harness a working Claude session can call during implementation.

The owner's example: a session has just changed a `ChangeSignature` error hint. It runs a script with
the prompt "Use the ChangeSignature tool to add a string parameter foo with no default to method Bar"
and perhaps a turn cap. The script runs the harness and returns the result. The session then sees
whether the tool returned the intended hint (a required parameter cannot follow optional parameters)
and how the model responded to it.

## The actual gap

A session changing a tool description, schema or error message has no fast way to see how a weak model
reacts to it. The choices today are a full eval (10-chain is 5-15 minutes per run and measures the
change only indirectly) or shipping the wording untested. The scenarios in the plan are fixed, named
and written in C#; they do not serve a one-off question asked mid-implementation.

## Possible fixes (not decided)

A script front door over the scenario harness, taking a prompt and returning a compact trace. Points a
design would have to settle:

- **Inputs.** A prompt and a turn cap, as the owner described. The example also needs code to act on
  (a method `Bar` with an optional parameter). Owner confirmed 2026-11-11: the probe takes a named
  fixture or a path to a test solution or project (ContosoOrders and the like), plus the usual knobs
  (model, samples, max tokens and so on). Whether it also accepts staged file content is open.
- **Build under test.** The value is testing an uncommitted change, so the probe must run the working
  tree's build, not the session's running server (which is stale until restarted).
- **Output.** A short per-call trace for the calling session: tool, arguments, result or error text,
  and the model's next call, plus the final message. Small enough to read in context; the full
  transcript stays on disk.
- **Samples.** One run is an anecdote. A sample count, and a one-line tally across samples, would make
  the answer usable.
- **Host contention.** Host 113 runs one model at a time. A probe from one session can collide with
  another session's eval run, so it needs a lock, a queue or a clear "host busy" result.
- **Reuse.** The plan's base fixture (step 4) and per-call scorer (step 3) are the building blocks.
  A probe is an ad hoc cold-start scenario; a kept probe could be promoted to a named scenario.
- **Tool surface.** Whether a probe uses the 10-chain surface for comparability or lets the session
  enable the toolset that holds the tool it is changing.

## Related

- [plan_targeted_tool_scenario_evals.md](../plans/plan_targeted_tool_scenario_evals.md) - the harness this would sit on.
- [reference_model_eval_procedure.md](../references/reference_model_eval_procedure.md) - hosts, env vars and the eval script.
