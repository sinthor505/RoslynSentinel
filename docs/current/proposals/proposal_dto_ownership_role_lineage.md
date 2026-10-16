# Make DTO ownership, role and lineage visible before restructuring DTOs

**Status:** APPROVED 2026-10-16. The four phases are approved; the namespace work is widened to the whole codebase (see Phase 3). An implementation plan is pending.

## Motivation

DTOs have grown to the point where it is hard to tell whether a type is an item, an input or an output, which
layer owns it, and whether it is a copy of another DTO. Measured on 2026-10-16 (reflection over the built
production assemblies; script and CSVs listed under Related):

**Inventory.** 438 DTO-like types: 330 `record`, 96 `class`, 12 struct/record struct.

| Layer | DTOs |
| --- | --- |
| Common | 206 |
| Engines.Basic | 116 |
| Engines.Advanced | 56 |
| Tools.Basic | 54 |
| Tools.Advanced | 4 |
| PlanStepRunner | 2 |

**Layer is already recorded; domain is not.** Every DTO sits in its project's single flat namespace
(`RoslynSentinel.Common` 186, `RoslynSentinel.Engines.Basic` 116, ...). Only `Common.SnippetMatching` (14) and
`Common.AgentLoop` (6) have sub-namespaces, so the assembly answers "which layer", but nothing machine-readable
answers "which feature". Name prefixes are the only domain signal:

| Domain (by name prefix) | DTOs |
| --- | --- |
| Git | 25 (all Tools.Basic) |
| Test | 18 |
| Project | 11 |
| Snippet | 10 |
| Method | 9 |
| Uplift | 9 |

**Role is mostly derivable, but names are inconsistent.** Role was derived from signature edges:

- Output: returned, never a parameter (162).
- Input: a parameter, never returned (105).
- Mixed: both a parameter and returned (82).
- Item: only a property of another DTO (37).
- Orphan: no signature edge at all (52).

How the main suffixes map to those roles:

- `Result` (102) is an output 66 times, mixed 14 times and an orphan 18 times.
- `Report`, `Outcome`, `Response` and `Summary` are used as synonyms for "output".
- `Info` (29) has no stable meaning: it is an output 8 times, an input 10, an item 4 and mixed 7.
- `Entry`, `Target` and `Summary` each span several roles.

**Duplicates exist and are mostly avoidable.** There are 13 identical-shape groups (two or more properties)
and about 20 near-duplicate pairs (property-name overlap of 0.75 or more). Every cross-layer copy is a pair
where the consuming layer can already see the original. None is forced by the sibling-visibility rule.

- `Engines.Basic.SymbolLocation` = `Tools.Basic.LocatedSymbolInfo`, 12 properties.
- `MemberRefactoringEngine.MethodParameterInfo` = `Tools.Basic.MethodSignatureParameterInfo`.
- `Common.RunUpliftInput` / `UpliftTarget` = `Engines.Advanced.UpliftBatchMultiInput` / `UpliftBatchMultiTarget`.
- Same layer: `MigrationEnvelope` = `SentinelCallToolResult`, 16 properties.
- Same layer: the `Git*Result` family in Tools.Basic.

**Why the copies exist.** The main driver is MCP output-schema tagging:

- `LocatedSymbolInfo` is never constructed. It exists only so that `OutputSchemaType` can advertise
  `[Produces]` tags (`SymbolNavigationTools.cs:66-69`, `:99`).
- `MethodSignatureParameterInfo` exists because the engine type was "outside this POC's file scope"
  (`RefactoringStructuralTools.cs:61-64`).
- `ProducesAttribute` lives in Common (`RoslynSentinel.Common/ProducesAttribute.cs:4`), so engine DTOs could
  carry the tag directly.
- The other schema-only mirrors are `LocateSymbolResult`, `RenameSymbolResultEnvelope`,
  `ModifyModifierResultEnvelope`, `MethodSignatureViewResultEnvelope`, `McpServerStatusResult` and `DocListResult`.

**Mapping sprawl is narrower than it looks.**

- After removing the ubiquitous `ToolCallReason` -> `SentinelCallToolResult` edges, 92 members take one DTO
  and return another. Most are operations, not conversions.
- Only about 10 are mapper-shaped, for example `BuildResultProjector.Project`, `GitImpl.ToHunkEntry`,
  `*Report.ToOutcome` and `DiagnosticExtensions.ToInfo`. They live in no common place.
- **The largest real repetition is in method bodies, not signatures.** There are 83 inline
  `new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), note, apply.DryRun, apply.Diff, ...)` sites:
  `AdvancedRefactoringTools` 41, `RefactoringStructuralImpl` 25, `RefactoringExtractionDocsImpl` 8,
  `RefactoringSignatureImpl` 6, other 3.
- There are also field-by-field copies of `RunUpliftInput` into `UpliftBatchMultiInput` at
  `AsyncifyTools.cs:1819-1832`, `:1335` and `:2865`.

**Common's 47% share is structural, not just misplacement.** Common holds logic that consumes and returns these
DTOs: `DiffEngine`, `ValidateAndApplyHelper`, `OperationBlobWriter`, `CollateralChangeGuard`,
`BuildResultProjector` and `MigrationLedger`. Of the 206 DTOs:

- About 35-45 are used by exactly one higher layer and by no Common logic, so they could move.
- 16 are used by two or more layers and must stay.
- Most of the rest are children of a Common DTO or used inside Common helpers.
- Two look dead: `AsyncMigrateInput` (`BatchTypes.cs:467`) and `ClassCandidateSummary` (`MigrationEnvelope.cs:29`).
- These counts are estimates from 6 sampled DTOs plus the CSV, not measured per DTO.

**Wire risk of moves and renames.** All serialization is System.Text.Json:

- There is no `$type`, `JsonDerivedType`, `JsonPolymorphic`, `JsonSerializerContext` or `TypeNameHandling`.
- Enums use `JsonStringEnumConverter` (`SharedJsonOptions.cs:26`).
- Moving a type to another namespace or assembly is wire-safe.
- Renaming a type changes `LargeResultInfo(resultType: typeof(X).Name)` output, at
  `ScanTools.cs:1092,1126`, `AsyncifyTools.cs:288`, `WorkspaceProjectManagementImpl.cs:54,76,187` and
  `WorkspaceReadNavigationImpl.cs:419,627,649`.
- Renaming a property changes the JSON contract.

**Nothing guards any of this.** There are no architecture tests: no NetArchTest, ArchUnitNET or layering
reflection tests, and no banned-API files. Layering is enforced only by `<ProjectReference>`.

**Agents have no way to find a reusable DTO.** No MCP tool lists DTOs with their roles or consumers.
`Search(mode: references)` cannot even resolve a type by name without `filePath` (TODO.md entry dated
2026-10-16). A "reuse before you create" rule has nothing to look DTOs up with, which is a tool-surface gap
under the Mission.

## Assessment of the ideas raised

The useful framing is that DTOs need three dimensions recorded: **owner** (layer), **role** and **lineage**. Each
idea is judged against that and against the measurements above.

| Idea | Verdict | Why |
| --- | --- | --- |
| 1a. Per-layer marker interfaces | Reject | The assembly already records the layer for 100% of DTOs, so a marker adds no information. Empty markers trip CA1040. A layer x role matrix multiplies interfaces. |
| 1b. `[Dto(layer, role)]` attribute | Defer | The layer part is redundant with the assembly. Role can be derived for the 304 types with clean edges. An attribute would only add value for the 82 mixed and 52 orphan types, and a hand-written role can drift from actual use. Revisit only if the generated catalog leaves those ambiguous. |
| 2. CLAUDE.md reuse and justification rule | Adopt now, with a lookup aid | Cheap and matches the Mission. On its own it is unenforceable: an agent cannot cite "existing DTOs evaluated" without a catalog. Needs Phase 1. |
| 3. Move DTOs out of Common | Defer, then partial | Only about 35-45 of 206 can move. The rest are tied to Common logic (layer inversion), and moving the logic is a separate, larger refactor. Cross-project type relocation is an open tool bug (engine-reorg Bug 2), so moves now would be brute force. The moves are wire-safe; the payoff is clarity, not correctness. |
| Pasted: naming suffix convention | Adopt | Free, and the role-by-suffix table shows exactly where it is broken. |
| Pasted: generic `IRequest<TResult>` | Reject | Engine entry points take primitives plus `CancellationToken` (`LocateSymbolAsync` takes 9 primitives; `RenameSymbolAsync(handle, symbol, newName, ct)`). Adopting it means inventing about 100 request records first. |
| Pasted: centralized mappers / Mapperly | Partial: local factories, no new dependency | Classic A->B mapping is about 10 members. The real target is the 83 `AppliedChangeSummary` constructions plus the Uplift copies: one factory each. Mapperly cannot express the real reshaping (`BuildResultProjector`, `RenameSymbol`'s projection, the Uplift classification). AutoMapper licensing is moot. |
| Pasted: architecture tests | Adopt, custom | No layering tests exist today. The repo already does reflection over assemblies in `Tests.Server/ArchitectureDocGenerator.cs:127-285`, so a custom test adds no dependency. |
| Pasted: Roslyn or reflection graph | Adopt | The survey script already produces the edges. Make it a maintained output, not a one-off. |
| Pasted: type-name persistence risk | Mostly n/a | No CLR type names on the wire, except the short name in `LargeResultInfo.resultType`. |

## Two further approaches

### A. Generated DTO catalog with derived role and lineage (observability first, no annotations)

Promote the survey script to a test-time generator next to `ArchitectureDocGenerator`, writing
`docs/generated/dto_catalog.md`. For each DTO it lists:

- layer, domain (name prefix or sub-namespace) and derived role;
- who constructs, accepts and returns it;
- what it contains;
- near-duplicate warnings.

It also writes a Mermaid graph per domain.

Add reflection guard tests on the same data:

- layering (no Engines DTO in Common signatures, and similar);
- a ratchet baseline: no new identical-shape duplicate, and no new DTO whose suffix contradicts its derived role.

Optionally expose the same data as an MCP read mode, so agents can query reusable DTOs by role and domain. That
fits the Mission: the reuse rule then has a tool behind it.

- **Merits:**
  - Zero annotation churn, so nothing can drift; the catalog is recomputed every run.
  - Answers all three dimensions today.
  - Gives the CLAUDE.md rule the evidence it requires.
  - The ratchet stops new sprawl without a big-bang cleanup.
- **Tradeoffs:**
  - Body-only usage is invisible to reflection (the 52 orphans). Full lineage needs a Roslyn pass (the
    `AppliedChangeSummary` sites) or the MCP references tool once fixed.
  - Derived roles are heuristics.
  - It is another generated doc to keep fresh.

### B. Feature sub-namespaces as the domain axis (vertical slices inside each layer)

Give each layer feature sub-namespaces that match the name-prefix domains, for example
`RoslynSentinel.Tools.Basic.Git` and `RoslynSentinel.Engines.Advanced.Uplift`, moving DTOs and their logic
together one domain at a time. Domain then becomes compiler-visible and greppable without attributes, and reuse
pressure across features drops because each slice owns its contracts.

- **Merits:**
  - Records the one dimension nothing records today.
  - Wire-safe (namespace moves only).
  - Idiomatic .NET.
  - Makes later Common extraction obvious, because whole slices move.
- **Tradeoffs:**
  - Broad `using` churn across many files and projects.
  - Needs reliable namespace and cross-project move tooling, and cross-project relocation is an open bug.
  - Folder and namespace mismatches can arise.
  - It fixes the domain dimension only, not role or duplicates.

## Proposal (recommended sequence)

1. **Phase 0 (now, docs only).** Add the CLAUDE.md "DTOs" section: reuse rule, justification rule, the suffix
   convention below, and "reuse the upstream type, never mirror it". Drafted 2026-10-16.
2. **Phase 1 (approach A).** Build the catalog generator and the guard tests with a ratchet baseline. Fix the
   `Search(mode: references)` type-resolution gap so per-DTO usage can be measured by tool.
3. **Phase 2 (targeted consolidation, migration-first).** Each item is one slice:
   - an `AppliedChangeSummary` factory from `ApplyOutcome`, covering the 83 sites;
   - tag engine DTOs with `[Produces]` and remove the schema-only mirrors;
   - drop the Uplift input copies;
   - delete the two dead DTOs after verifying them;
   - fold the identical same-layer pairs;
   - decide the open `finding_engine_result_shapes_primitives_and_wrapper_usage.md` (result envelope
     standard), because it sets the canonical output shape.
4. **Phase 3 (deferred, evidence-gated).**
   - Feature sub-namespaces (approach B) one domain at a time. Decided 2026-10-16: this covers every type
     (engines, tools, `*Impl`, helpers), not only DTOs. The goal is finer-grained answers to "what does this belong
     to" and "what does it depend on" than the current one-namespace-per-project layout gives.
   - Move the about 35-45 single-consumer Common DTOs only after cross-project relocation works.
   - Add a `[Dto]` role attribute only if the catalog leaves the mixed and orphan types ambiguous.

Suffix convention (decided for new DTOs; existing names change only when a slice touches them, because a type
rename can change `LargeResultInfo.resultType`):

| Role | Suffix | Notes |
| --- | --- | --- |
| Input to an engine or tool operation | `*Request`, `*Options` | `Options` for optional knobs only. |
| Output of an operation | `*Result` | Retire `Report`, `Outcome`, `Response` and `Summary` as synonyms for new types. |
| Item inside a result or request | plain noun (`SymbolLocation`, `GitHunk`) | No `Info`, `Data` or `Entry` suffix on new types. |
| Wire-only projection | `*View` | Only when a Tools-side shape genuinely differs (token trimming, renamed fields); cite why. |

## Alternatives considered, not pursued

- Per-layer marker interfaces: redundant with the assembly; CA1040.
- `IRequest<TResult>` everywhere: engine APIs are primitive-parameter methods.
- Mapperly or AutoMapper: too little classic mapping to justify a dependency, and the real reshaping is not
  expressible in them.
- A big-bang move of DTOs out of Common: blocked by layer inversion and the cross-project relocation bug.

## Open questions

- Should the catalog also be an MCP tool mode, or is the generated doc enough for agents?
- Domain naming: adopt the name-prefix domains as-is, or define a fixed list first?
- Are the 52 orphans all schema mirrors or wire types? This was not verified per type.
- Should existing `Info`, `Entry` and `Report` names ever be renamed, given the `resultType` wire effect?

## Cost / risk

- Phase 0 is docs only.
- Phase 1 adds one generator and a few tests, plus a fix in `SymbolNavigationEngine.FindCallersAsync`. The
  ratchet baseline must start green.
- Phase 2 touches about 90 call sites in Tools.Basic and Tools.Advanced, plus 4 schema tools. Removing a mirror
  changes `OutputSchemaType`, so check the emitted schema is unchanged.
- Phase 3 is broad churn and stays gated on tool readiness.

## Related

- `docs/current/findings/finding_engine_result_shapes_primitives_and_wrapper_usage.md` (OPEN, result envelope standard)
- `docs/current/proposals/proposal_parallel_change_deprecated_wrappers.md` (migration-first)
- `docs/current/blockers/blocking_error_search_references_by_name_excludes_types.md` (prerequisite for Phase 1)
- Memory `project_engine_reorg_final_closeout_bugs_1_2_3` (Bug 2: cross-project type relocation)
- Survey script: `DtoInventory.cs` (file-based `dotnet run` over `Server.Advanced/bin`); not committed. Phase 1
  replaces it.
