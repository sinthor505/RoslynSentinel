# `ConstructorParameter(add)` always emits the new parameter as required, with no default-value/optional path, deadlocking against any pre-existing direct-construction call site

**Status:** FIXED 2026-09-28, option 1 from "What unblocks it" below. Added optional
`defaultValue`/`nullDefault` parameters to `ConstructorParameter(add)`, threaded through
`RefactoringSignatureTools.ConstructorParameter` -> `RefactoringSignatureImpl.ConstructorParameter`
-> `RefactoringEngine.AddConstructorParameterAsync`, mirroring the existing
`MethodSignature`/`AddMethodParameterAsync` `defaultValue`/`nullDefault` convention exactly
(including the `nullDefault` sidecar bool for the documented MCP-client null-string-corruption
issue, anthropics/claude-code#81911). The generated parameter now gets `.WithDefault(...)` applied
when either is set, so an added parameter can be backward-compatible with existing
direct-construction call sites instead of unconditionally required.

Regression + fix tests added in `RoslynSentinel.Tests.Battery/BatteryTwentyFourTests.cs`:
`ConstructorParameter_Add_WithDefaultValue_BackwardCompatibleWithExistingCaller`,
`ConstructorParameter_Add_WithNullDefault_BackwardCompatibleWithExistingCaller`,
`ConstructorParameter_Add_NullDefaultAndDefaultValueBothSet_Refused`, and
`ConstructorParameter_Add_NoDefaultValue_ExistingCallerBreaksAndIsRefused` (confirms the original
no-default deadlock is still correctly refused, not silently broken). All 4 pass; full solution
test run afterward showed no new failures (16 pre-existing failures unrelated: 15 LM-Studio-model-
not-loaded live-eval tests, 1 pre-existing `AsyncifyTools` NullReferenceException).

No files were corrupted or left in a bad state during the original incident - the tool's own
pre-write validation refused to apply either change (`AntiPatternEngine` and `DeadCodeEngine`), and
the working tree was reported clean (`isClean: true`, zero staged/unstaged/untracked) after both
attempts. This was a stop-and-report finding, not a corrupted-state incident.

## What was being attempted

Group 3 of an approved engine-reorganization plan: dissolving `AnalysisEngine` into
`ResourceSafetyEngine`, `DeadCodeEngine`, `ArchitecturalEngine`, and `AntiPatternEngine`, executed
entirely via RoslynSentinel MCP tools per CLAUDE.md's dog-fooding policy.

Several `AnalysisEngine` methods being moved into `AntiPatternEngine` and `DeadCodeEngine` (via
`MoveMember`) read a `_config` field of type `RoslynSentinel.Common.SentinelConfiguration`, which
neither target class currently takes as a constructor dependency - both currently take only
`IWorkspaceManager workspaceManager`. `MoveMember`'s own documented limitation is that it does not
detect or add missing constructor/DI dependencies on the target class; the plan's prescribed
workaround is to precheck with `GetMethodSource` + `ConstructorParameter(operation: "view")`, then
run `ConstructorParameter(operation: "add")` on the target class first if a field is missing,
*before* calling `MoveMember`.

Call made:

```
ConstructorParameter(
  operation: "add",
  filePath: "RoslynSentinel.Advanced\AntiPatternEngine.cs",
  className: "AntiPatternEngine",
  paramName: "config",
  paramType: "SentinelConfiguration",
  reason: "..."
)
```

The identical sequence was then attempted for `DeadCodeEngine` with the same shape and outcome.

## The exact symptom

```
errorCode: "Exception"
message: "ConstructorParameter: the change was valid and matched its target(s), but introduces
new compiler errors - change not applied."
```

followed by a list of roughly 49 `CS7036` diagnostics for `AntiPatternEngine`, of the form:

```
There is no argument given that corresponds to the required parameter 'config' of
'AntiPatternEngine.AntiPatternEngine(IWorkspaceManager, SentinelConfiguration)'
```

across roughly 20 test files in `RoslynSentinel.Tests.Advanced`, `RoslynSentinel.Tests.Battery`,
`RoslynSentinel.Tests.Asyncify`, and `RoslynSentinel.Tests.Integration`, every one a direct
`new AntiPatternEngine(_workspaceManager)` construction with a single argument.

The identical failure shape recurred for `DeadCodeEngine`: 11 call sites, same `CS7036` pattern,
same refusal.

As a targeted follow-up, fixing a handful of `AntiPatternEngine` call sites first (via
`ReplaceSnippet`, adding `, new SentinelConfiguration()` as a second constructor argument) was
attempted to see whether the call sites could be pre-repaired ahead of the parameter add. This
failed too:

```
CS1729: 'AntiPatternEngine' does not contain a constructor that takes 2 arguments
```

- expected, since the two-argument overload does not exist until `ConstructorParameter(add)`
creates it, but confirms there is no ordering of these two tool calls that succeeds independently.

## Where it happened

- Tool: `ConstructorParameter`, `operation: "add"`.
- Target 1: `RoslynSentinel.Advanced/AntiPatternEngine.cs`, class `AntiPatternEngine` - ~49 CS7036
  sites across ~20 files in `RoslynSentinel.Tests.Advanced`, `RoslynSentinel.Tests.Battery`,
  `RoslynSentinel.Tests.Asyncify`, `RoslynSentinel.Tests.Integration`.
- Target 2: `RoslynSentinel.Advanced/DeadCodeEngine.cs`, class `DeadCodeEngine` - 11 CS7036 sites,
  same pattern.
- Follow-up probe: `ReplaceSnippet` against a handful of `AntiPatternEngine` test call sites,
  rejected with `CS1729` (confirms no viable call ordering, not a separate defect).

## Root cause - traced to source, not a hypothesis

`ConstructorParameter(add)`'s schema has no `defaultValue`/optional-parameter path. Confirmed live
via `ToolSearch` for `"ConstructorParameter defaultValue optional add"`: the emitted schema for the
`add` operation exposes only `paramName`, `paramType`, and `fieldName` - no `defaultValue` field, no
`optional`/`required` flag, nothing that would let the new parameter be added as e.g.
`SentinelConfiguration config = null!` on first pass. Every parameter this operation adds is
therefore unconditionally required in the generated constructor signature.

Because the parameter is unconditionally required, every existing direct construction of the
target class anywhere in the solution - `new AntiPatternEngine(_workspaceManager)`,
`new DeadCodeEngine(_workspaceManager)` - immediately fails to compile with `CS7036` the moment the
parameter is added, regardless of how many or how few such call sites exist. This is not specific
to these two classes; it is a structural property of the tool's `add` operation whenever the
target class has any non-DI direct-construction call site anywhere in the solution, which turns out
to be the common case for the `Advanced`-flavor engines (mostly test fixtures constructing the
engine directly rather than resolving it through DI), not a rare edge case.

The tool's own pre-write validation (matching the same validate-before-write convention documented
for `ReplaceSnippet`/`MoveMember` elsewhere in this repo's tool family) correctly detects the
resulting broken compile and refuses to apply it - so nothing is corrupted - but this creates a
chicken-and-egg with no way through via this tool alone:

- The required parameter cannot be added without breaking every existing direct-construction call
  site solution-wide.
- Those call sites cannot be fixed up first, because the multi-argument constructor overload they
  would need to compile against does not exist until the parameter add is applied.

This directly contradicts the plan's own prescribed sequencing (add the missing constructor
dependency first, then run `MoveMember`) for any target class that already has non-DI
direct-construction call sites scattered across the test suite.

## Ruled out

- Not a `MoveMember` defect - `MoveMember` was never reached; the blocker occurs entirely inside
  the prerequisite `ConstructorParameter(add)` step the plan itself prescribes to run first.
- Not a corrupted or partially-applied write - the tool's validate-before-write gate refused both
  attempts (`AntiPatternEngine`, `DeadCodeEngine`) before anything was written to disk; the working
  tree was confirmed clean afterward.
- Not resolvable by reordering the two tool calls - explicitly tested by attempting to pre-repair a
  handful of `AntiPatternEngine` call sites via `ReplaceSnippet` before the parameter add; this
  fails with `CS1729` because the target overload does not exist yet, confirming there is no
  sequencing of `ConstructorParameter`/`ReplaceSnippet`/`MoveMember` that avoids the deadlock as
  the tool is currently shaped.
- Not scoped to `AntiPatternEngine` specifically - the identical `CS7036` pattern reproduced for
  `DeadCodeEngine` with a different, smaller call-site count (11 vs. ~49), confirming this is a
  general property of the `add` operation rather than something particular to one class's call-site
  shape.

## Why this blocks (per CLAUDE.md failure doctrine)

Per CLAUDE.md, "a required parameter instead of an optional one that relocates the failure" is
named explicitly as an environment defect pattern, and that is exactly what is happening here:
`ConstructorParameter(add)` has no optional/default-value path, so the failure is relocated from
"add the dependency safely" to "add the dependency and simultaneously break every existing
direct-construction call site solution-wide in one unrecoverable step." The tool's refusal is
correct and safe, but the schema gives the caller no way to reach a state where the write can
succeed at all for a target class with pre-existing direct-construction call sites - there is no
smaller, independently-completable step available. This is a tool-surface gap, not a case of the
model calling the tool incorrectly: the call matches the plan's own prescribed workaround exactly.

## What unblocks it

Most likely fixes, any one of which would break the deadlock (schema/tool-side, not a model
workaround):

1. **Add an optional `defaultValue` parameter to `ConstructorParameter(add)`'s schema.** Adding the
   dependency with a default (e.g. `SentinelConfiguration config = null!`, or a project-appropriate
   sentinel) would decouple "add the dependency" from "backfill every call site" into two
   independently-completable, always-safe steps - the field could be added first without breaking
   any existing call site, and call sites could then be migrated to pass a real value
   incrementally, with the default removed as a final follow-up once every call site is confirmed
   fixed.
2. **Give `ConstructorParameter(add)` the same auto-resolve-call-sites behavior `MoveMember`
   already has** - rewrite each `new AntiPatternEngine(x)` / `new DeadCodeEngine(x)` call site
   found solution-wide to append a suitable default/new-instance argument automatically, the same
   tradeoff `MoveMember` already makes for method-move call sites.
3. **A preview/dry-run mode that surfaces the exact call-site impact list before refusing**, so the
   model can act on it proactively rather than receive it only as the terminal failure message.
   This is partially already achieved today - the `CS7036` dump the tool returns on refusal already
   names every affected file/line - but it arrives only *after* the tool has decided to refuse, not
   as a `dryRun: true` preview the model could inspect and act on ahead of time.

Whichever fix lands, re-verify against this exact repro: `ConstructorParameter(add)` for `config:
SentinelConfiguration` on both `AntiPatternEngine` and `DeadCodeEngine` should succeed (or, for
option 3, should surface a complete, actionable call-site list under `dryRun: true` before any
write is attempted), and a follow-up `MoveMember` for the originally-blocked `AnalysisEngine`
methods should then succeed without a constructor-shape mismatch.

## Related

- Plan: the engine-reorganization plan's Group 3 step, which prescribes the
  `ConstructorParameter(add)`-before-`MoveMember` sequencing this doc shows is unworkable for any
  target class with pre-existing direct-construction call sites.
- CLAUDE.md, "Failure doctrine: the environment is responsible" - the "required parameter instead
  of an optional one that relocates the failure" pattern this doc is a direct instance of.
- `docs/current/blockers/resolved/blocking_error_movemember_instance_callsite_not_rewritten.md` -
  a different `MoveMember`-family defect in the same reorg-tooling area; not the same root cause,
  but worth cross-referencing since both concern call-site rewriting safety around structural
  moves.
