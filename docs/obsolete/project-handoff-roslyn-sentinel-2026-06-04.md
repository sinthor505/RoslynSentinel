<!-- project-handoff-roslyn-sentinel-2026-06-04-v1.md -->
# Project Handoff — Roslyn Sentinel
**Generated:** 2026-06-04
**Scope:** Roslyn Sentinel project
**Chats covered:** 12 (spanning 2026-05-31 → 2026-06-04)
**Status summary:** 4 Active | 0 Blocked | 1 Stale | 7 Resolved/Background

**Package contents:**
- `project-handoff-roslyn-sentinel-2026-06-04.md` — this document
- `roslyn-sentinel-files-summary-2026-06-04.md` — summarized project files
- `roslyn-sentinel-originals-2026-06-04.zip` — archived originals

---

## You (Andrew)
- **Role:** Tier 3 escalation engineer @ a Canadian MSP; 20 yrs, senior infra/RE/dev.
- **This project:** Roslyn Sentinel — an agentic C# coding tool (MCP server) built on Roslyn. Personal/hobby development effort.
- **Architectural thesis:** CEO/production-line — the model owns *decisions*, the deterministic substrate (disk reads, Roslyn parses) owns *operations* and should do maximum work for free. Every place the model is forced to reconstruct information the substrate could have supplied is a misallocation.
- **Key preferences:** systematic spec-driven work over ad-hoc; specs in the established `spec-*-v{n}.md` / `roslyn-sentinel-*-impl-spec-v1.md` format; C# with explicit types, braces always, no expression-bodied members; build/verify before claiming success.

---

## Active Work

### 🟡 In Progress
| Topic | Chat | Current State | Next Action |
|---|---|---|---|
| Tool result pagination | [Roslyn Sentinel Setup](https://claude.ai/chat/this-chat) | Only 6/93 tools paginate. `list_solution_items(kind=files)` confirmed dumping 59k chars inline (no `hasMore`) during local-model test. | Add `limit`/`offset`+`hasMore` to high-priority tools: `list_solution_items`, `find_references`, `get_di_registrations`, `get_type_info`, `run_scan_detector`. Mirror `scan_migration_candidates` pattern. |
| Engine consolidation spec | [Engine consolidation and tool organization](https://claude.ai/chat/6fdeffc2-b2bb-47a3-adf1-9f5439a84d25) | Analysis complete (62 engines, 336 methods; 9 duplicate names, ~16 mis-homed methods, fragment engines identified). Spec **offered twice but not yet produced**. | Produce the formal engine-consolidation spec. Gates both the async-cluster merge and the Core/Server split. |
| MCP bug-fix campaign (B1–B7b) | [Agent feedback on MCP tool design](https://claude.ai/chat/fff68f7e-9572-4b17-a0c1-7c0a85dbf61d) | Specced in `spec-migration-scan-summary-patch` (v8). B1 (summarize ordering), B2 (`.Designer.cs` SyntaxTree crash), B7/B7b (`minScore` ignored in both paths) are critical-path. | Implement B1 first, then B2 + B7/B7b in parallel per the agent instructions block in that chat. |
| Tool-cap profiles (<128) | [Organizing 430 tools under 128 tool cap](https://claude.ai/chat/1b74e9fa-6c33-4e70-a2af-9bdd2aa24070) | Tools grouped by routine-dev importance; startup args to expose task-specific toolsets. `DescribeScanDetectors` null-detector bug fixed. | Finalize startup-arg toolset profiles; wire the grouping into server startup. |

### 🔵 Awaiting Review / Validation
| Topic | Chat | What Needs Review | Notes |
|---|---|---|---|
| Local-model viability | [Roslyn Sentinel Setup](https://claude.ai/chat/this-chat) | Confirm the May-31 "local LLM is a dead end" conclusion is formally retired. | gemma-4-e4b on GTX 1080 ran load→scan→rename→commit successfully. TTFT degrades (28s→60s) as context fills past ~33k, but functional. The lever was tool-surface reduction, not hardware. |

---

## Chat Inventory

### Tool surface & organization

#### Organizing 430 tools under 128 tool cap
- **Link:** https://claude.ai/chat/1b74e9fa-6c33-4e70-a2af-9bdd2aa24070
- **Status:** Active
- **Updated:** 2026-06-03
- **Summary:** Tool list had grown to 430 visible tools (~130k payload chars), over the 128-cap most agents/IDEs enforce. Grouped tools by importance to routine dev work; redundant/unnecessary tools split out. Startup args to control exposed toolsets. Also fixed `DescribeScanDetectors` — `Enum.TryParse` failed the null case so "return all detectors" fell into the failure branch; fix only parses/filters when `detector` is non-null.
- **Open items:**
  - [ ] Finalize startup-arg toolset profiles and wire into server startup.

#### Reorganizing scan tool domains
- **Link:** https://claude.ai/chat/05889b18-7433-48c1-8c97-85f098171801
- **Status:** Resolved
- **Updated:** 2026-06-02
- **Summary:** Async/`Task` detectors (e.g. `task_void_usage`, `async_in_constructor`) were mis-filed under `concurrency`. Reclassified: true concurrency (locks/races/shared-state) stays; async API-contract/correctness detectors moved out. `SentinelScanTools.cs` `s_descriptors` made `internal`; `SentinelQualityTools.ScanOptions()` rewritten to derive descriptions/options from `s_descriptors` at call time so adding a detector is a one-line change picked up by both `describe_scan_detectors` and `describe_advanced_tool_options("scan")`.

### MCP tool design & bug fixes

#### Agent feedback on MCP tool design and usability
- **Link:** https://claude.ai/chat/fff68f7e-9572-4b17-a0c1-7c0a85dbf61d
- **Status:** Active
- **Updated:** 2026-06-03
- **Summary:** Synthesized 4 agent-test feedback sessions into `spec-migration-scan-summary-patch` (now v8). Key finding: two distinct offload mechanisms were being conflated — (1) server-side offload to `.roslynsentinel/operations/` recoverable via `get_scan_result`, vs (2) VS Code's chat layer independently intercepting large inline responses and writing them to a VS Code-controlled path the agent can't reach. Mitigation for (2) is B1 — keep `summarize=true` responses small enough VS Code never offloads.
- **Open items:**
  - [ ] B1 — `summarize=true` early-return ordering guard (`SentinelQualityTools.cs`).
  - [ ] B2 — guard every `GetSemanticModel` with `compilation.ContainsSyntaxTree(tree)` (`AntiPatternEngine.cs` ~line 2723).
  - [ ] B7 — filter `minScore` before aggregation in `BuildScanSummaryAsync`.
  - [ ] B7b — apply `minScore` in `FindMigrationCandidatesAsync` before pagination (paged path).

#### MCP tool implementation debugging and fixes
- **Link:** https://claude.ai/chat/e0925a59-bc46-46cb-b07e-cbcd978d61c5
- **Status:** Background
- **Updated:** 2026-06-01
- **Summary:** A Haiku agent applied edits then failed to revert — `undo_last_apply{changeId:"17"}` returned "No operation blob found … only available for batch-first tools" (rename/staged_change aren't batch-first). Compilation errors resulted from a corrupted `[Description("""…""")]` raw-string literal hand-authored by the model. Implemented Phases 1/2/3/5 of the feedback fix plan (slim summary types, `DocReadResult.ResolvedPath`, `OperationId` on `MigrationResult<T>`). Added `ReplaceAttributeAsync` to `RefactoringEngine.cs` (line 2149) which parses `newAttributeSource` via Roslyn so malformed delimiters are rejected at parse time — eliminates the corruption failure mode.
- **Open items:**
  - [ ] Wire up the `modify_attribute` wrapper for `ReplaceAttributeAsync` (per `remaining-task-modify-attribute-wrapper-v1.md`).

#### Catching tool invocation errors and error handling
- **Link:** https://claude.ai/chat/fa33b165-b41c-4f6b-afe2-2c76f65b9927
- **Status:** Resolved
- **Updated:** 2026-06-01
- **Summary:** "An error occurred invoking 'my_tool'" is the MCP transport fallback fired when schema validation rejects the call *before* the handler's try/catch runs. Fix: replace `throw`/`throw ex` with `return $"Error: …"` throughout, and add an early "No solution loaded" guard. Gives agents a readable, detectable error pattern via the normal response path.

### Architecture & design

#### Monolithic project decomposition strategy
- **Link:** https://claude.ai/chat/fad1e108-00af-4429-b8ab-71965a17f1c4
- **Status:** Active
- **Updated:** 2026-06-03
- **Summary:** Recommended Option A (layer split: Core → Server) over Option B (by domain), because domain boundaries are leaky — tools like `async_migrate` span multiple engines, so domain projects force shared-interface plumbing that negates the split. Practical target: `RoslynSentinel.Core` (shared types, workspace manager, base engine infra, circuit breaker) + `RoslynSentinel` (engines, tools, entry point) + optional `.Tests`. **Timing: do it AFTER engine consolidation** — splitting first turns a rename/move into a rename/move + project-reference plumbing for no benefit.
- **Open items:**
  - [ ] Execute Core/Server split — only after engine consolidation lands.

#### Engine consolidation and tool organization
- **Link:** https://claude.ai/chat/6fdeffc2-b2bb-47a3-adf1-9f5439a84d25
- **Status:** Active
- **Updated:** 2026-05-31
- **Summary:** Analyzed `engine_methods_all.json` — 62 engine classes, 336 non-boilerplate methods. Found 9 duplicate method names across engines (notably perf-analysis methods duplicated between `AnalysisEngine` and `PerformanceEngine`), ~16 mis-homed methods (strays in catch-all `AnalysisEngine`/`AntiPatternEngine`), and fragment engines to collapse (async cluster `AsyncSafetyEngine`+`AsyncOptimizationEngine`+`AsyncBatchEngine`; style engines; `RefinementEngine`→`StructuralRefinementEngine`). Recommended sequence: duplicates → relocate methods → collapse fragments → async-cluster merge last (largest surface).
- **Open items:**
  - [ ] Produce the formal engine-consolidation spec (offered, not yet delivered).

#### Truncated file reads in coding agents
- **Link:** https://claude.ai/chat/f94036aa-2a9c-4a41-ab38-d75ea52d4198
- **Status:** Background
- **Updated:** 2026-05-31
- **Summary:** Design discussion on the agent failure pattern where silently truncated file reads desync the model from search line numbers. Produced `spec-read-tool-metadata-envelope-v1.md`: a `ReadEnvelope` type with leading scope signal (total lines/bytes) + trailing truncation marker (range, continuation offset), positive completeness stamp, conditional `OutlineAvailable` flag. Return shapes for `get_method_source` and `get_file_outline`; shared `BuildEnvelope` helper; 16 tests.
- **Open items:**
  - [ ] Implement `spec-read-tool-metadata-envelope-v1.md` (no evidence of implementation yet).

### Testing & process

#### Testing mcp server with dead-end prevention
- **Link:** https://claude.ai/chat/ee84c4f9-1604-40ad-b2cb-9bef258b3351
- **Status:** Resolved
- **Updated:** 2026-06-03
- **Summary:** Hardened the agent test prompt against spiraling/token-burn at dead-ends. Added: no-retry-loop rule, no-inference-substitution, an 8k output-token budget, a "BLOCKED: [reason] → move on" protocol, and a preflight `load_solution` hard-dependency check with "Output nothing else" to enforce a true hard stop in a stateless agent.

#### Roslyn Sentinel Setup (this chat)
- **Link:** https://claude.ai/chat/this-chat
- **Status:** Active
- **Updated:** 2026-06-04
- **Summary:** First successful local-model test. gemma-4-e4b on a GTX 1080 ran load_solution → list projects → get_public_api_surface (5,659 records, offloaded to scan file) → get_scan_result paging → inspect_symbol → rename_symbol → staged_change apply. `find_by_name(kind=method)` rejected (valid kinds are `implementorsOf`/`attributeUsages`/etc — no name→file lookup). `list_solution_items(kind=files)` dumped 59k chars inline. Identified high-priority pagination targets.
- **Open items:**
  - [ ] Add pagination to high-priority tools (see Active Work).
  - [ ] Address symbol-name → file discovery gap.

### General reference

#### Generating and updating source code files
- **Link:** https://claude.ai/chat/bf6a4a3f-4f06-4633-9ccc-dab31fcaca12
- **Status:** Background
- **Updated:** 2026-05-31
- **Summary:** The `all_methods.csv` / `engine_methods.json` dumps are reflection/syntax dumps of ~315 public methods across ~52 engine/tool classes. Recommended replacing both with a single `dump_engine_methods` MCP tool returning the JSON shape (drop the lossy truncated-signature CSV); use a Roslyn syntax-walk over reflection for exact-signature fidelity since signatures are part of the tool contract.

#### Visual Studio intellisense and code completion mechanisms
- **Link:** https://claude.ai/chat/9224d67b-1552-47f4-bf83-d3dc009d3703
- **Status:** Background
- **Updated:** 2026-05-31
- **Summary:** Reference Q&A. Brace/indent/snippet completion = VS editor host; member completion, squiggles, refactors = Roslyn (same semantic model Roslyn Sentinel taps); ghost-text body suggestions = Copilot. No project action.

---

## Project Files

**Total files:** 1 | **Token cost per session (before):** ~24,300
**After summarization:** ~600 tokens (~97% reduction)

| File | Class | Est. Tokens | Purpose | Disposition |
|---|---|---|---|---|
| tool_list_all.json | Reference | ~24,300 | Server-generated 93-tool registry dump | Summarized; regenerate from server when needed |

### Loading Instructions for New Project
1. Add `roslyn-sentinel-files-summary-2026-06-04.md` to the new project.
2. Add this handoff document to the new project.
3. Carry forward custom skills unchanged (see Skills and Tooling).
4. Do NOT add `tool_list_all.json` — it's regenerable and 24k tokens; re-dump from the server only when a fresh analysis needs it.
5. Remove any superseded prior handoff/index files.

### Hygiene Notes
- `tool_list_all.json` is the only project file and it's a regenerable server dump. Keeping it resident costs ~24k tokens every session in this project for data that's only needed during active tool-surface analysis. Recommend removing it from the project and re-uploading on demand.
- Several referenced artifacts (`engine_methods_all.json`, `spec-*.md` files, `project-index-v1.md`) are mentioned in chats but are NOT currently in `/mnt/project/` — they live in chat outputs only. If they're load-bearing for the engine-consolidation spec, collect them before starting fresh.

---

## Cross-Chat Notes

- **Sequencing dependency:** Engine consolidation → Core/Server split. The decomposition chat explicitly defers the project split until after the engine layer is clean. Don't reorder.
- **Engine-consolidation spec is the keystone:** It's been offered twice (May 31, June 3) and gates the async-cluster merge AND the Core/Server decomposition. It is the highest-leverage unblocking deliverable.
- **Two offload mechanisms, easy to conflate:** server-side (`.roslynsentinel/operations/`, recoverable) vs VS Code chat-layer interception (unreachable). The pagination work in this chat and the B1 fix both depend on keeping responses small enough that VS Code never triggers (2). Don't let the pagination work reintroduce large inline responses.
- **Stale conclusion retired:** The May-31 "local LLM is a dead end — constraint is hardware" framing is contradicted by the June-4 successful gemma-4-e4b run. The real lever was the 430→93 tool-surface reduction (~67k→~8k schema tokens). Any new instance should treat local-model support as viable, not dead.

---

## Resolved Work (Reference)

| Topic | Resolution | Chat |
|---|---|---|
| Scan-tool domain misclassification | Async detectors moved out of `concurrency`; descriptors derived from single `s_descriptors` source | https://claude.ai/chat/05889b18-7433-48c1-8c97-85f098171801 |
| Generic transport error masking | `throw` → `return "Error: …"` + no-solution guard throughout | https://claude.ai/chat/fa33b165-b41c-4f6b-afe2-2c76f65b9927 |
| `DescribeScanDetectors` null-detector bug | Only parse/filter `detector` when non-null | https://claude.ai/chat/1b74e9fa-6c33-4e70-a2af-9bdd2aa24070 |
| Agent test prompt spiraling | Anti-spiral guardrails + preflight `load_solution` hard stop | https://claude.ai/chat/ee84c4f9-1604-40ad-b2cb-9bef258b3351 |

---

## Skills and Tooling

| Name / Type | Purpose | Status | Notes |
|---|---|---|---|
| Roslyn Sentinel MCP server | The project itself — Roslyn-based agentic C# coding tool | In active development | 93 tools, monolithic project, pre-decomposition |
| Custom Claude skills (this workspace) | `auto-token-monitor`, `file-summarizer`, `handoff`, `project-handoff`, `ticket-notes`, `troubleshooting`, etc. | Installed | Carry forward unchanged to any new project |

---

## Starting Fresh

### New single chat
Paste this document at the start of a new conversation, then state your immediate goal. No re-explanation needed.

> "Project handoff attached. Next: produce the engine-consolidation spec (analysis is in the Engine consolidation chat) — it gates the async-cluster merge and the Core/Server split."

For a specific thread, link to its section above and paste key artifacts from the relevant single-chat `/handoff` document if you need code/commands.

### New project
1. Create the new project.
2. Add `roslyn-sentinel-files-summary-2026-06-04.md`.
3. Add `project-handoff-roslyn-sentinel-2026-06-04.md`.
4. Add custom skill files carried forward.
5. Do NOT add `tool_list_all.json` — regenerate on demand.
6. First chat: paste this document, state your next goal.
