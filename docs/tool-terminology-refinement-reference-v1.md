# Tool Terminology Refinement — Reference

**Date:** 2026-08-21
**Purpose:** Reference for evaluating and revising MCP tool names, parameter names, and
descriptions against terminology strength — i.e., how well each term matches vocabulary a
coding agent is likely to have strong, unambiguous priors for from training, versus
idiosyncratic or overloaded terms that force the agent to read (or re-read) the full
description every time.

**Relationship to prior work:** This is a second pass, informed by the outcome of
[plan-tool-rename-v1.md](plan-tool-rename-v1.md), which already executed a batch rename
(e.g. `acknowledge_sync` → `clear_external_drift`, `get_external_changes` →
`list_external_disk_changes`, `scan` → `run_scan_detector`, `list` → `list_solution_items`)
and rewrote most tool descriptions. Some terms flagged below (`ClearExternalDrift`,
`DescribeAdvancedToolOptions`'s lookup-table pattern) are the deliberate *result* of that
pass, not unnoticed defects — they're included here because the terminology question is
still open, not because the prior work missed them. See also
[tool-disambiguation-survey-v1.md](tool-disambiguation-survey-v1.md) for the separate,
unrelated defect-shape audit (name-only + silent-first-match + mutating tools) — that
survey is about resolution *safety*, this document is about naming *clarity*. Do not conflate
the two when scoping remediation.

**Method:** Extracted `[McpServerTool]` / `[Description]` declarations from
`RoslynSentinel.Server.Advanced\*Tools.cs` and `RoslynSentinel.Server.Basic\*Tools.cs`,
cross-checked confusable groups against source (`SentinelAdvancedRefactoringTools.cs`,
`SentinelCodemodTools.cs`, `SentinelWorkspaceTools.cs`).

---

## Strong terms — keep as-is

These map directly onto vocabulary an agent has almost certainly seen heavily and
consistently in training: established IDE/refactoring vocabulary, LSP/Roslyn conventions,
or generic REST/CLI verbs.

`RenameSymbol`, `ExtractMethodSafe`, `ExtractLocalVariable`, `PullUpMember`,
`ChangeSignature`, `MoveType`, `FindReferences`, `InspectSymbol`,
`QuerySymbolRelationships`, `GetTypeInfo`, `GetDiagnostics`, `Build`, `LoadSolution`,
`CreateProject`, `SearchSolutionText`, `UndoLastApply`, `AddCancellationToken`,
`InvertBooleanLogic`, `GetCallGraph`, `ReadFile`.

No action needed on these.

---

## Weak/ambiguous terms

| Name | Why weak | Suggested alternative |
|---|---|---|
| `GetBreakerStatus` / `ResetBreaker` | "Circuit breaker" is trained primarily as a distributed-systems/resilience term (Polly, Hystrix). Here it gates the server's own mutating-tool safety lock after repeated failures — an agent may not connect "breaker" to "why did my edit get refused." | `GetToolSafetyStatus` / `ResetToolSafetyLock` |
| `GetMigrationLedger` / `ResetMigrationLedger` | "Ledger" evokes accounting/bookkeeping. Actual meaning: a persisted per-run history of touched methods during async migration. | `GetMigrationHistory` / `ClearMigrationHistory` |
| `ClearExternalDrift` | "Drift" is an infrastructure-as-code term (Terraform state drift). Its sibling `ListExternalDiskChanges` uses a plain, concrete phrase for the same underlying concept (files changed on disk outside the tool's own writes) — the pair uses two different metaphors for one concept, which is more inconsistent than either term alone being weak. Already renamed once (see plan-tool-rename-v1.md); flagging again because the inconsistency, not the term itself, is the issue. | `AcknowledgeExternalDiskChanges` — matches its sibling's vocabulary |
| `BridgeAsyncMethods` / `UpliftCallers` | Invented jargon specific to this codebase's async-migration workflow ("bridge" = sync wrapper delegating to new async method; "uplift" = rewrite callers to call the async method directly). Not guessable without reading the whole Asyncify workflow description. | `CreateAsyncBridgeWrapper` / `RewriteCallersToAsync` |
| `Apply*Codemod` family (`ApplyFileCodemod`, `ApplyMethodCodemod`, `ApplyClassCodemod`) | "Codemod" is a real term but rooted in the JS/jscodeshift ecosystem — weaker prior for a C#-trained agent than "transform" or "refactor." Compounding issue: the `transform` parameter is an untyped string whose valid values live only in prose / behind `DescribeAdvancedToolOptions`, not in the schema. | Naming is secondary here; the higher-value fix is making `transform` a real enum (see Systemic Issue below) |
| `WrapRange` param `wrapper` | Untyped string; valid values (`tryCatch`, `using`, `region`) only documented in prose. | Convert to enum `WrapperKind` |
| `Member` (Basic tools' `AddMember`) param `position` | Free-text mini-DSL (`"end"`, `"after:Foo"`, `"before:Foo"`) with no schema-level grammar description. | Split into `insertMode` (enum: `End`/`After`/`Before`) + `anchorMemberName` (string) |
| `ModifyEnum` param `values` | Comma-separated mini-DSL (`"Pending,Shipped=99"`) packed into one string, no inline parameter description explaining the grammar. | Rename param to `memberListDsl` with a Description stating the `Name=Value` grammar explicitly |
| `SafeDeleteUnusedSymbol` | "Safe" is vague, shared across several tool names (`ExtractMethodSafe`, `ConvertPropertySafe`, etc.) and doesn't say *what* is checked. Lower priority — the description does state the zero-usages precondition. | Leave as-is unless doing a broader `*Safe` suffix pass |

---

## Confusable near-duplicate groups

Tool names that overlap in apparent scope, where an agent may not know which sibling to
pick without reading both descriptions in full.

- **`Inline` vs `InlineClass`** — `Inline` inlines a *symbol* (method/variable/field/parameter,
  selected via a `kind` string); `InlineClass` merges a *class* into another. Same verb,
  disjoint targets, no shared prefix to signal the split.
  → Rename `Inline` → `InlineSymbol` to mirror `InlineClass`'s specificity.

- **`Introduce` vs `IntroduceParameterObject`** — bare `Introduce` creates a
  local/field/parameter/constant from an expression (the classic "Introduce Variable"
  refactor); `IntroduceParameterObject` is the distinct, well-named Fowler refactor. An
  agent asked to "introduce a parameter object" could plausibly reach for either.
  → Rename bare `Introduce` → `IntroduceVariable` (its dominant use case) or
  `IntroduceFromExpression`.

- **`Member` vs `ExtractMembers` vs `PullUpMember`** — three different "member" verbs:
  CRUD a single member (`Member`/`AddMember` family), extract a group of members into a new
  type (`ExtractMembers`), move one member to a base class (`PullUpMember`). The shared noun
  gives no signal about scope (single vs. group, same-file vs. cross-hierarchy).
  → Rename bare `Member`/`AddMember` → `EditMember` or `AddMember` (keep, since it's already
  specific); keep `ExtractMembers`/`PullUpMember` as-is — they're already well-differentiated.

- **`Generate` vs `GenerateMapping` vs `GenerateHttpClient` vs `GenerateClassesFromJson` vs
  `GenerateDefaultConfigJson`** — bare `Generate` is a wide multi-kind dispatcher (test
  scaffolds, builders, decorators, equality overrides, etc., selected via `kind`) sitting
  alongside four purpose-built `Generate*` siblings that look like they should be `kind`
  values of the bare tool but structurally aren't.
  → Rename bare `Generate` → `GenerateCodeArtifact`, or fold its sub-kinds into named
  siblings to match the specific ones.

- **`Features` / `Git` / `ProjectDoc`** — all three are single "unified dispatcher" tools
  named after their *domain* (feature flags; git status/log/diff/stage/commit/revert;
  project docs read/write) rather than their *verb*. This is a defensible, consistent
  pattern once an agent has seen one of them — but on first encounter, `Git` in particular
  reads as read-only (given its `status`/`log`/`diff` sub-actions) and an agent may not
  expect it also stages/commits/reverts. The mutating sub-actions (`stageAll`, commit,
  revert) deserve explicit emphasis in the description, not just enumeration.

---

## Systemic issue — the highest-leverage fix

`Generate`, `Introduce`, `Inline`, `Member`, `Features`, `Git`, and the `Apply*Codemod`
family all disambiguate scope through an **untyped string** discriminator parameter
(`kind`, `action`, `transform`, `operation`). For several of these (`Generate`, the codemod
`transform` values, `async_migrate`'s `operation`, `scan`'s `detector`), the valid values
are **not enumerable from the schema at all** — the agent must make a separate call to
`DescribeAdvancedToolOptions` to learn what's valid before it can call the tool correctly.
This "look it up in a second tool" pattern is a deliberate, documented design choice (see
`DescribeAdvancedToolOptions`'s own description in plan-tool-rename-v1.md — "Only covers
tools whose valid values cannot be inferred from the schema alone"), not an oversight. It's
flagged here because it's the single biggest remaining terminology-adjacent gap.

Other tools in the same codebase already do this correctly with real C# enums exposed
directly in the schema — `DetectorId`-style scan detectors (post-rename, exposed via
`describe_scan_detectors` rather than the schema, actually — worth double-checking which
enums *are* schema-native), `BuildVerifyLevel`, `SymbolKindFilter`. Where a discriminator's
value set is small and stable (e.g. `WrapRange.wrapper`, `ModifyModifier.modifier`), converting
it to a real enum lets the MCP schema self-describe and removes the extra round-trip. Where
the value set is large (94 scan detectors, 60+ codemod transforms) a lookup tool remains the
right call — but the tool name and description should say so explicitly, and consistently
across all dispatcher tools, rather than leaving the agent to discover the pattern per-tool.

**Recommendation:** Before renaming individual tools, decide the scope of this pass:
1. Convert small, stable discriminator params to real enums (low risk, high payoff per tool).
2. For the remaining wide dispatchers (`Generate`, codemod `transform`, `scan` detector,
   `async_migrate` operation), standardize the "call `DescribeAdvancedToolOptions` first"
   pattern's wording so every such tool's description states it the same way.
3. Only then revisit the naming table above — several of those renames (`Inline` →
   `InlineSymbol`, `Introduce` → `IntroduceVariable`) are independent of the enum work and
   can proceed on their own.

---

## Out of scope for this document

- Resolution-safety defects (name-only + silent-first-match + mutating tools) — tracked
  separately in [tool-disambiguation-survey-v1.md](tool-disambiguation-survey-v1.md) and
  [plan-tool-disambiguation-remediation-v1.md](plan-tool-disambiguation-remediation-v1.md).
  Do not fold naming changes and resolution-safety changes into the same change set; they
  have different risk profiles (naming is low-risk/cosmetic, resolution-safety changes
  affect mutation correctness).
- Any code changes. This document is descriptive only — no tool has been renamed or had its
  description changed as part of producing this reference.
