---
name: dependencyinjectiontests-missing-admin-mode-fixed
description: "DynamicDiscovery_AllClassesWithToolAttribute_ShouldBeResolvable failed because DependencyInjectionTests.cs's hard-coded allModes set omitted \"Admin\" — fixed by adding it"
metadata: 
  node_type: memory
  type: project
  originSessionId: 94cd7991-f305-447e-9760-d7cf8dd6e13c
  modified: 2026-09-05T10:12:38.865Z
---

`RoslynSentinel.Tests.Advanced/DependencyInjectionTests.cs` builds its test DI container from a
hand-maintained `allModes` HashSet (line ~39) passed to `AddRoslynSentinelToolsAdvanced`, rather than
deriving it from the actual set of mode strings the registration path checks. It was missing
`"Admin"`, so `SentinelAdminTools` (gated behind that mode, added per
[[project_external_drift_hard_blocker_idea]]) was never registered in the test container —
`DynamicDiscovery_AllClassesWithToolAttribute_ShouldBeResolvable`, which enumerates every
`[McpServerToolType]` class in the assembly via reflection and asserts each resolves from DI,
failed with "Dynamically discovered tool SentinelAdminTools is not registered in the DI container."

**Why:** this is the same "hand-copied list drifts from the real registration set" failure mode the
surrounding comment in that file already calls out for engines (see
`AddRoslynSentinelEnginesAdvanced` and the `project_dependency_direction` memory) — it just wasn't
caught for the tool-mode list at the time `SentinelAdminTools`/the `"Admin"` mode was introduced.

**Fix applied:** added `"Admin"` to `allModes` in `DependencyInjectionTests.cs` Setup(), with an
inline comment noting that this set must include every mode string
`AddRoslynSentinelToolsAdvanced` checks, or the corresponding tool class's DynamicDiscovery
assertion silently fails even though nothing is actually broken in production code.

**How to apply:** if `DynamicDiscovery_AllClassesWithToolAttribute_ShouldBeResolvable` (or
`AllMcpTools_ShouldBeResolvable`) fails again naming a tool class not registered, check first
whether `allModes` in this test's Setup() is missing that class's gating mode string — this is a
test-fixture drift bug, not a production DI wiring bug, and is fixed the same way (add the missing
mode string) rather than by touching the production registration code.
