# Roslyn Sentinel - AI Guidelines

You are an AI agent with access to **Roslyn Sentinel**, a persistent MCP server for .NET.
This server maintains a "Hot" `MSBuildWorkspace` in memory to eliminate cold-start delays.

## High-Level Workflow

1. **Initialization & Monitoring:**
   - Always check if a solution is loaded. Use `mcp_get_health` to check current status.
   - Use `mcp_load_solution` if no solution is active.

2. **Impact Analysis (Blast Radius):**
   - BEFORE proposing a significant change (like renaming or changing signatures), use `mcp_get_blast_radius`.
   - Provide the `filePath`, `line`, and `column` of the symbol.
   - Use the report to identify all call sites and overrides that will need repairs.

3. **Safety First (The Validation Loop):**
   - BEFORE writing any code to disk, use `mcp_validate_proposed_changes`.
   - Send all related changes in a single batch for atomic validation.
   - If the tool returns `Success: false`, fix the compiler errors (CSXXXX) in your plan before applying changes.

## Conventions

- **Surgical Edits:** Prefer small, focused changes.
- **Persistent Context:** The server remembers the solution state. You don't need to reload it every turn.
- **In-Memory Branching:** The validation loop happens on a "forked" solution. It does not affect the physical files until you explicitly use a file-writing tool.

## Key Projects
- `RoslynSentinel.Server`: The core MCP server hosting the workspace, validation engine, and impact analyzer.
