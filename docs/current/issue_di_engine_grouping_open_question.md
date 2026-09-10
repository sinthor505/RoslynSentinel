# DI tool-split plan: engine-usage groupings may not match planned class boundaries — OPEN

## Summary

The [Workspace/Refactoring DI split plan](./plans/plan_split_workspace_refactoring_tools_for_di.md)
groups the new `*Tools`/`*Impl` classes by naming-intuition (e.g. "project management," "structural
refactoring") rather than by which engines their methods actually share. A first-pass grep of the
planned `WorkspaceProjectManagementTools` class (7 methods) showed each of its 4 non-
`IWorkspaceManager` engine dependencies is used by only 1-2 methods, never shared within the class:

- `DependencyEngine` → only `ListSolutionItems`/`ListWorkspaceSolutions`
- `StructuralRefinementEngine` → only `SafeDeleteUnusedSymbol`
- `SolutionManagementEngine` → only `CreateProject`/`SplitProjectByFolder`
- `ProjectConsistencyEngine` → only `ListProjectFrameworkTargets`

## Why it matters

This class's constructor ends up wide not because its methods share dependencies, but because the
class bundles several single-engine methods that each happen to drag in one unique engine — the
same shape that motivated splitting the original 13/14-argument god-classes in the first place, just
recurring at smaller scale one level down. Left unaddressed, it carries straight into the plan's
`*Impl` classes (see the plan's "Decision 1-Amendment" section).

## Status

Not resolved. Needs the same fact-finding rigor already applied to the original two god-classes —
grep every engine call site per method — applied to all 8 planned classes, not just
`WorkspaceProjectManagementTools`, before finalizing the plan's constructor table (Decision 2).

Three ways this could go, undecided:
1. Accept narrow multi-engine constructors as fine, since `*Impl` classes are no longer
   LLM-schema-visible and this only affects testability.
2. Regroup methods across the planned classes by engine-sharing instead of by naming-intuition.
3. Split further (e.g. one `*Impl` per engine) — more files/classes for arguably little
   readability gain over option 1.

## How to apply

Run the audit as its own step, ideally via RoslynSentinel's own MCP tools
(`QuerySymbolRelationships`/`GetCallGraph`/`FindReferences` against each engine field) rather than
manual grep, before or alongside the `*Impl` extraction step — not after, to avoid re-shuffling the
groupings twice. See the plan's "Decision 1-Amendment-2" section for the full detail this issue
tracks.
