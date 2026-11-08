# Final step — End-to-end manual verification

## Prior state

All three phases are complete and their gates passed. All of `RoslynSentinel.Tests.Basic`,
`RoslynSentinel.Tests.Battery`, and `RoslynSentinel.Tests.Asyncify` are green, and the full
solution builds clean.

## Task

Manually drive each fix through the live MCP tools (not just unit tests) to confirm the
end-to-end behavior a real model session would see:

1. **Phase 1 — `Build`:** invoke `Build` via MCP with a deliberately-bad `scopeName` (one that
   resolves to zero projects) and confirm the response no longer claims success — it should
   report `Outcome: NotRun` (or the wrapper-level failure), not a fabricated `Succeeded`.
2. **Phase 2 — trivia/EOL:** invoke `ChangeAccessibility` via MCP on a real member with a doc
   comment (use a scratch file, not a real solution file) and diff the before/after text to
   confirm the doc comment survives byte-identical.
3. **Phase 3 — findings routing:** manually drive 3 consecutive zero-match `SearchSolutionText`
   calls via MCP and confirm the **3rd response itself** carries the orientation-breaker finding
   — not a 4th call. Also run a malformed/anomalous diff through `ApplyUnifiedDiff` and confirm
   `Findings` is populated with a `DiffHunkAnalyzer` source and `DirectiveKind == ReviewRequired`.

Per `feedback_dogfood_mcp_blocking_errors.md`: if any MCP tool call during this verification
fails, returns wrong data, or is unreachable, stop, write
`docs/current/blockers/blocking_error_<slug>.md`, and end the turn rather than falling back to
non-MCP verification or routing around it.

## Done

Once all three manual checks pass, the full remediation plan is complete. Report a summary of
what was changed across all 13 steps and the final test/build status.
