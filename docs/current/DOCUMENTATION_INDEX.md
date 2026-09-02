# RoslynSentinel Documentation Index

**Last Updated:** 2026-08-24

> **Docs were split into `docs/current/` (accurate/relevant today) and `docs/obsolete/`
> (superseded, completed-and-historical, or describing something no longer true) on
> 2026-08-24.** All links below have been updated to point at each file's new location.
> When adding a new doc, file it into the correct subdirectory from the start.

---

## 📚 Documentation Files

### Current (`docs/current/`)

| File | Purpose |
|------|---------|
| **[TODO.md](./TODO.md)** | Living task list — open items and closed/fixed history |
| **[UNFINISHED.md](./UNFINISHED.md)** | Backlog: planned features and known limitations |
| **[UNFINISHED_FEATURES.md](./UNFINISHED_FEATURES.md)** | Deferred bugs with regression tests and edge-case limitations |
| **[reference-code-file-write-paths-v1.md](./reference-code-file-write-paths-v1.md)** | Living reference: the single write-to-disk chokepoint and its guarantees |
| **[roslyn-duplication-audit-v1.md](./roslyn-duplication-audit-v1.md)** | Ongoing audit of hand-rolled logic vs. native Roslyn APIs |
| **[tool-terminology-refinement-reference-v1.md](./tool-terminology-refinement-reference-v1.md)** | Open naming/terminology backlog for MCP tool surface |
| **[spec-read-tool-metadata-envelope-v1.md](./spec-read-tool-metadata-envelope-v1.md)** | Unimplemented spec: truncation/scope metadata envelope for read tools |
| **[spec-replace-throws-in-mcp-tools-v1.md](./spec-replace-throws-in-mcp-tools-v1.md)** | Partially-implemented plan: convert throws to string returns in `[McpServerTool]` methods |
| **[project_readfile_createfile_path_inconsistency_bug.md](./project_readfile_createfile_path_inconsistency_bug.md)** | Open: wrong-path CreateFile/ApplyDiff collides with an existing project file (CS0101/CS0111) while ReadFile reports FileNotFound for the same path, no pointer to the real file |
| **[project_readfile_createfile_disk_fallback_fixed.md](./project_readfile_createfile_disk_fallback_fixed.md)** | Fixed: `ReadFile` couldn't see files `CreateFile` wrote outside the `.cs`-only Document sync; `ReadFile` now falls back to a disk read |

### Obsolete (`docs/obsolete/`)

Historical plans, specs, and snapshots — implemented, superseded, or describing a state of
the codebase that no longer exists (e.g. the old singular `RoslynSentinel.Server` project,
pre-consolidation tool counts). Kept for historical record. See individual files for what
each one covered; not re-indexed in detail here since they are no longer live references.

---

## 🎯 Quick Reference

### For Users: "Is this tool complete?"
1. Check **UNFINISHED_FEATURES.md** → Search tool name
2. If listed under "Known Limitations" → Read the workaround
3. If listed under "Deferred Bugs" → Has regression test marked `[Ignore]`, awaiting fix

### For Developers: "What still needs to be done?"
1. Read **TODO.md** → Current open/closed task log (primary source of truth)
2. Read **UNFINISHED.md** → Lists planned enhancements by difficulty
3. Check **UNFINISHED_FEATURES.md** → Lists deferred bugs and limitations

---

**This index links all RoslynSentinel documentation.**
