# Roadmap

## Phases

- [x] **Phase 1: Foundation & Reliability** - Establish a robust, persistent host with incremental workspace loading.
- [x] **Phase 2: Atomic Multi-File Validation** - Implement in-memory branching for batch diagnostic feedback.
- [x] **Phase 3: Semantic Impact Analysis** - Enable deep cross-project tracing and "blast radius" reporting.
- [x] **Phase 4: Atomic Refactoring Macros** - Deliver AI-native tools for multi-file code transformations.
- [ ] **Phase 5: Advanced Protocol: Unified Diffs** - Support token-efficient updates via Diff application.
- [ ] **Phase 6: Distribution & DevExp** - Streamline installation and provide comprehensive integration guides.

## Progress

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1: Foundation & Reliability | 1/1 | COMPLETED | 2026-04-27 |
| 2: Atomic Multi-File Validation | 1/1 | COMPLETED | 2026-04-27 |
| 3: Semantic Impact Analysis | 1/1 | COMPLETED | 2026-04-27 |
| 4: Atomic Refactoring Macros | 1/1 | COMPLETED | 2026-04-27 |
| 5: Advanced Protocol | 0/0 | Not started | - |
| 6: Distribution & DevExp | 0/0 | Not started | - |

## Phase Details

### Phase 1: Foundation & Reliability
**Goal**: The MCP server runs persistently, monitors workspace health, and responds to physical file changes without full reloads.
**Status**: **COMPLETED**
**Success Criteria**:
1. Server starts, loads a target .sln, and stays running in the background. (MET)
2. Server detects file changes via FileSystemWatcher and updates its internal MSBuildWorkspace incrementally. (MET)
3. Server exposes a `get_health` tool showing memory usage and Roslyn workspace status. (MET)

### Phase 2: Atomic Multi-File Validation
**Goal**: AI agents can test complex, cross-cutting changes across multiple files and projects in memory.
**Status**: **COMPLETED**
**Success Criteria**:
1. Agent can call `validate_proposed_changes` with a map of file paths to new contents. (MET)
2. Server creates an in-memory solution fork, applies all changes, and returns global compiler diagnostics. (MET)
3. Performance: Batch validation of 5 files completes in < 3 seconds on warm cache. (MET)

### Phase 3: Semantic Impact Analysis
**Goal**: AI agents can query the true impact of a proposed change before attempting it.
**Status**: **COMPLETED**
**Success Criteria**:
1. Agent can call `get_blast_radius` on a symbol and get usages across all projects in the solution. (MET)
2. Report includes symbol kind, file locations, and source code previews. (MET)

### Phase 4: Atomic Refactoring Macros
**Goal**: AI agents can execute complex, multi-file code transformations with a single tool call.
**Status**: **COMPLETED**
**Success Criteria**:
1. Agent can call `move_type_to_file` and the server extracts a class to a new file, updating original file. (MET)
2. Agent can call `extract_interface` to create an interface from a class and update inheritance. (MET)
3. Agent can call `add_parameter_to_method` and update all call sites across the solution. (MET)
4. Agent can call `format_document` and `add_braces` for style maintenance. (MET)

### Phase 5: Advanced Protocol: Unified Diffs
**Goal**: Support token-efficient updates via Diff application.
**Depends on**: Phase 4
**Success Criteria**:
1. Server can parse and apply standard Unified Diffs to the in-memory or physical workspace.
2. AI agents can validate Diffs before they are applied.

### Phase 6: Distribution & DevExp
**Goal**: Developers can easily install, configure, and understand how to use the Roslyn Sentinel MCP.
**Depends on**: Phase 4
**Success Criteria**:
1. Developer can run an install script (PowerShell/Bash) to globally register the MCP server.
2. Claude Desktop or other MCP clients can automatically discover and launch the server.
3. README provides clear examples of agent instructions for using custom refactoring tools.
