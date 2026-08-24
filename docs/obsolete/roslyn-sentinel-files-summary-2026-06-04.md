<!-- roslyn-sentinel-files-summary-2026-06-04-v1.md -->
# Project Files Summary — Roslyn Sentinel
**Generated:** 2026-06-04
**Files summarized:** 1
**Original total tokens:** ~24,300
**Summary total tokens:** ~600
**Net saving:** ~23,700 tokens per session (~97% reduction)

---

## tool_list_all.json
**Class:** Reference (regenerable server dump)
**Original est. tokens:** ~24,300 | **Summary tokens:** ~600 | **Saving:** ~97%

### Purpose
Machine-generated inventory of the Roslyn Sentinel MCP server's currently exposed tool surface. Produced by the server (`generatedUtc` field present), used as the source-of-truth dataset for tool-surface analysis (pagination audits, consolidation, domain grouping).

### Key Facts
- 93 tools, all unique names. Metadata payload 31,075 chars; full file 97,178 chars.
- Generated 2026-06-04T18:01:11Z (this matches the current working state).
- Reduced from a peak of 430 visible tools via `roslyn-sentinel-consolidation-impl-spec-v1.md`.
- 17 tools are mutating (carry `autoStage`); the rest are read/analysis.
- Pagination currently on 6 tools only: `get_comprehensive_health_report`, `get_diagnostics`, `get_operation_detail`, `get_scan_result`, `scan_migration_candidates`, `search_solution_text`.
- Verb distribution skews to `get_*` (18), then `add_*` (6), `generate_*` (5), `list_*` (4).
- Several unified dispatchers exist (`async_migrate`, `apply_*_codemod`, `generate`, `scan`) documented via `describe_advanced_tool_options`.

### State / Configuration
| Field | Value |
|---|---|
| Tool count | 93 |
| Payload chars (schema) | 31,075 (~7.8k tokens) |
| Mutating tools | 17 |
| Paginated tools | 6 |
| Source | Server-generated; regenerable |

### Key Decisions / Conclusions
- File is **regenerable** — do not treat as precious. Re-dump from the server when a fresh tool list is needed rather than carrying the 24k-token raw file into a new project.
- Schema reduction (430→93, ~67k→~8k schema tokens) is what made local small models viable (confirmed gemma-4-e4b run, 2026-06-04).

### Open Action Items
- [ ] Pagination coverage is the active work item — only 6/93 tools paginate; high-priority gaps identified (see handoff Active Work).
