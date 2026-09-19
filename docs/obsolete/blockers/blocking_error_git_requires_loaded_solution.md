# `Git` refuses to run without a loaded solution, and `LoadSolution` leaks a raw `ArgumentException`

**Status:** PARTIALLY FIXED 2026-09-12 (`07b0164`). Defect 2 is fixed at the chokepoint; defect 1
remains OPEN. See the amendment below before acting on this document.

## Amendment 2026-09-12 — what is actually fixed

- **Defect 2 (raw `ArgumentException` on a missing required parameter): FIXED.** It was generic
  dispatch-layer behaviour, not `LoadSolution`-specific, so `ToolArgumentValidator` now handles it
  once at the `AddCallToolFilter` chokepoint for every tool. It also closes a defect found later
  the same day and not listed below: **unknown parameter names were silently ignored**, producing
  `success:true` with default behaviour instead of an error.
- **snake_case identifiers in error text: FIXED** — 104 occurrences corrected server-wide.
- **`Git`'s `stageAll` footgun: FIXED, and was already on master before this session.** The
  parameter is now a `scope` enum (`tracked`/`all`/`listed`); naming files alongside a non-`listed`
  scope is rejected rather than silently overridden. Verified live: `scope:"listed"` with 11 named
  paths staged exactly those.
- **Defect 1 (`Git` requires a loaded solution) is still OPEN** and is now the only item here.

Note the original write-up below claims `Git` has no unstage operation. That was correct at the
time; check the current `operation` enum before relying on it.

---

**Original status:** OPEN — found 2026-09-12, reported per the dog-fooding policy in `CLAUDE.md`.

Two separate defects, hit back-to-back on the *first* live `Git` call after the dog-fooding
enforcement hook (`.claude/hooks/enforce-dogfood.ps1`) went in. Both are affordance defects rather
than logic bugs: the operations are implemented correctly, but the environment fails to guide the
caller to a working call.

## Defect 1 — `Git(operation: "status")` requires a loaded solution

Call:

```
Git(operation: "status", reason: "Verifying working tree state ...")
```

Result:

```json
{"success":false,"error":"No solution path configured. Call load_solution first."}
```

**Why this is wrong:** git status has no dependency on the Roslyn workspace. It reads the working
tree, not the compilation. Requiring a loaded solution couples an operation to state it does not
need, and the coupling is invisible until the call fails.

Worth checking which of the seven operations genuinely need the workspace. `status`, `log`, `diff`
and `commit` appear not to; if any do, it is presumably only to resolve the repo root — which could
fall back to discovering the `.git` directory upward from the server's base path.

**Impact on the target audience:** a weak model that has just been told "use `Git` instead of the
shell" gets a hard failure on its first attempt, with a fix (`load_solution`) that has no obvious
connection to the thing it asked for. That is exactly the environment-fails-the-novice pattern the
failure doctrine targets. Likely outcomes are giving up and reaching for the shell, or burning
turns.

**Also note the error names `load_solution` in snake_case**, while the tool is exposed as
`LoadSolution`. A model copying the identifier verbatim will call a tool that does not exist.

## Defect 2 — omitting `solutionPath` throws a raw `ArgumentException`

Call:

```
LoadSolution(reason: "...")     // solutionPath omitted
```

Result:

```
Tool call failed unexpectedly (ArgumentException): The arguments dictionary is missing a value
for the required parameter 'solutionPath'. (Parameter 'arguments')
```

This violates the working convention in `CLAUDE.md`:

> Never leak raw exceptions, stack traces, or internal paths into a tool's `ResultError` — catch at
> the tool boundary and return a structured, actionable error.

"arguments dictionary" is MCP dispatch-layer vocabulary the model never sees and cannot act on. The
message also gives no recovery path — it does not say what a valid `solutionPath` looks like, and
notably does not mention that `ListWorkspaceSolutions` exists to find one.

Compare the *useful* version of this error:

```
solutionPath is required. Pass the absolute path to a .slnx/.sln file, e.g.
C:\...\RoslynSentinel.slnx. Call ListWorkspaceSolutions to discover candidates.
```

## Suggested fixes

1. Drop the loaded-solution precondition from the `Git` operations that don't need it; if a repo
   root is required, discover it from the `.git` directory instead.
2. If the precondition must stay, make the error self-servicing: name `LoadSolution` in the correct
   casing and point at `ListWorkspaceSolutions`.
3. Catch missing-required-parameter at the tool boundary and return a structured error naming the
   parameter, an example value, and the discovery tool.
4. Worth auditing whether defect 2 is generic across all tools with required params, since it looks
   like dispatch-layer behavior rather than anything specific to `LoadSolution`. If so, one fix at
   the `AddCallToolFilter` chokepoint covers the whole surface.

## Related

- `blockers/blocking_error_git_stage_ignores_untracked_files.md` — separate open defect in `Git(stage)`.
- `docs/current/TODO.md` — `Git` missing branch/push/checkout/worktree/stash.
