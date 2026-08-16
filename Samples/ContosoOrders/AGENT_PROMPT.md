# Agent Prompt — Execute ContosoOrders Plan via RoslynSentinel MCP tools

This is the prompt to give the test agent (backed by the 7B–8B model) to simulate a real
"here is the plan, implement it" handoff. Paste everything below the `---` line as the user/system
message to the agent. Do not paraphrase or summarize it before sending — the wording is part of the
test (it deliberately does not name specific MCP tools).

---

You are an autonomous coding agent with access to the RoslynSentinel MCP tools for working with
.NET solutions (loading a solution, locating/inspecting symbols, renaming, refactoring, staging and
applying changes, and checking diagnostics/workspace health).

A teammate already reviewed the `ContosoOrders` sample project and wrote up a plan of the fixes
needed. The plan file is at:

```
Samples/ContosoOrders/PLAN.md
```

Your job:
1. Read the plan file first.
2. Load the solution referenced by the plan (`Samples/ContosoOrders/ContosoOrders.sln`) before
   doing anything else.
3. Execute each numbered step in the plan's "Steps" section, in order, using whichever tools are
   appropriate — the plan intentionally does not tell you which tool to call for each step; that is
   your decision to make based on each tool's description.
4. For each step, briefly state what you're about to do and why, then make the tool call(s).
5. Pay attention to the "Risks & Open Questions" section of the plan — it calls out specific
   pitfalls (e.g., not renaming the wrong symbol, watching for a missing using directive, handling
   two edits landing in the same file). Do not ignore these.
6. After completing all numbered steps, perform a final validation that the solution compiles with
   no new errors, and report a summary of what changed, file by file.
7. If any step fails or a tool returns an error, do not silently skip it — report the failure,
   explain what you tried, and either retry with corrected arguments or ask for guidance before
   continuing to the next step.
8. Do not modify `PLAN.md` or `SCENARIOS.md` themselves. Only modify files under
   `Samples/ContosoOrders/ContosoOrders.Core` and `Samples/ContosoOrders/ContosoOrders.Tests` as
   required by the plan.

Begin by loading the solution and reading the plan's first step.

---

## Notes for the evaluator (not part of the agent prompt)
- This prompt deliberately withholds tool names so the agent must map plan language ("rename",
  "change the accessibility of", "add a value to the enum", "confirm zero usages then remove",
  "extract ... into a new method", "add a constructor parameter", "rename the file", "add a doc
  comment", "validate ... apply") to the correct MCP tool itself — this is the real skill under
  test, not plan-following per se.
- Step 1 (rename) and step 8 (file rename via `SyncTypeAndFilename`) are the two most likely to be
  confused with each other or with a manual filesystem operation — watch for the agent trying to
  simulate a file rename by deleting/recreating the file instead of using the dedicated tool.
- Step 7's missing-using-directive trap is the single best signal of whether the agent actually
  reads compiler diagnostics after making a change, rather than assuming success once a tool call
  returns without an exception.
- Grade using `SCENARIOS.md`'s per-scenario "Expected tool sequence" / "Expected outcome" /
  "Grading notes" — each PLAN.md step maps 1:1 to a SCENARIOS.md scenario (step 1 → Scenario 1,
  step 2 → Scenario 2, ... step 10 → the compile-and-apply portion of Scenario 10).
