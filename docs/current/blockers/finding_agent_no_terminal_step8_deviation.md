# Finding: Step-following test plans must not assume the agent has terminal access

**Status:** informational finding, not a blocking error — no RoslynSentinel bug. Filed under
`blockers/` per the dog-fooding process anyway, since it changes how future step-by-step
test plans must be written.

## What happened

During the first "9B model literal step-following" test
(`RoslynSentinel-AgentTesting/RoslynSentinel NormalizeWhitespace Test - 9B - run 1 - 2026-08-27 23.09.md`),
Step 8 of the plan (`docs/current/plan-9b-model-test-step1.md`) instructed the agent to verify
its fix compiled by running:

```
dotnet build "...\RoslynSentinel.Advanced\RoslynSentinel.Advanced.csproj" -c Debug
```

via "your terminal/bash tool (not an MCP tool)". The model (qwen3.5-9b-coder, via LM Studio)
instead called the MCP `Build` tool with `level: fullBuild, scope: project,
scopeName: "RoslynSentinel.Advanced"` — a tool call the plan never mentioned — and reported
success from that instead.

## Root cause

Not a reasoning failure. The agent genuinely has no terminal/bash tool available in this setup:
LM Studio (running on a separate machine from the one hosting this repo and its RoslynSentinel
MCP server) does not provide one. RoslynSentinel's own MCP `Build` tool was the only build
capability actually exposed to the model, so it used the closest available substitute rather
than stopping and reporting a blocking error — which, per the plan's own instructions ("if a
step's tool call fails, stop and report... do not guess a workaround"), would arguably have been
the more literal-compliant response, but reasonably was not what happened since the step didn't
technically fail — the specified tool just wasn't present at all.

## Why this matters for the test's validity

The stated goal of this test series is to first measure literal instruction-following, then
progressively reduce guidance to measure reasoning. A step that assumes a capability the agent
doesn't have contaminates that measurement — the model can't fail *or* succeed at "run dotnet
build in a terminal" if no terminal exists; whatever it does instead is reasoning/substitution
behavior leaking into what was meant to be a literal-following step. It happened to pick the
right substitute here, but that's a data point about its judgment, not about its ability to
follow literal steps.

## Recommendation

Future plans for this agent/environment must only reference tools actually available to it —
i.e., RoslynSentinel MCP tools exclusively, no "use your terminal" steps — unless a specific test
is intentionally designed to see whether the model notices a missing capability and reports it
instead of substituting. If a build-verification step is needed, use the MCP `Build` tool
explicitly (with an appropriate `scope`/`scopeName`) rather than assuming shell access.

Side note, not this finding's main point: the MCP `Build` tool's project-scoped mode
(`scope: project`) is also the correct way to work around the AgentTesting copy's now-fixed
missing-test-projects gap (see prior session) — worth preferring it over a full-solution build in
this environment going forward regardless of which agent is running the plan.
