# RoslynSentinel — Scoped Documentation Tools & Access-Control Hardening Spec
<!-- v1 -->

**Date:** 2026-05-28
**Purpose:** Add a family of path-constrained documentation tools to RoslynSentinel, plus defense-in-depth guards and per-tool rate limiting. Together these let an agent persist legitimate working files (plans, state, handoffs, completed-work logs) while being structurally prevented from using file I/O as an escape hatch to read or write arbitrary source files.
**Target server:** RoslynSentinel (source available locally)
**Mode:** Implementation spec — hand to an agent with workspace access to the RoslynSentinel source

---

## Background and rationale

The architecture goal is to force the agent to work exclusively through RoslynSentinel's MCP interface rather than raw filesystem/shell access. The semantic code tools (`rename_symbol`, `run_bridge_batch`, etc.) are inherently safe because they only accept validated parameters referencing real workspace entities — invalid input is rejected, so there is no arbitrary-data injection surface.

The gap is **legitimate file persistence**. The agent needs to read and write its own working documents — migration plans, current state, handoff notes, completed-work logs. The naive solution (give it generic `read_file` / `write_file`) reopens the exact escape hatch we're trying to close: the agent could read `.cs` files or write anywhere.

The solution is a set of **scoped documentation tools** that:
- Accept a filename parameter but strip all path components
- Confine all reads/writes to a hardcoded `docs/` subtree under the solution root
- Reject non-documentation file extensions via allowlist
- Enforce per-tool rate limits and content-size caps on the freeform-content tools

This makes the documentation tools a constrained aperture rather than a general file API. The boundary is enforced in the tool implementation, not by trusting the agent to honor instructions.

### Threat model (precise)

The agent is **not** modeled as a hostile attacker attempting exfiltration. It is modeled as a **completion-driven system that will use any available aperture to accomplish its task**, including rationalizing its way around instructions. The guards must make the dangerous paths *impossible*, not merely *discouraged*. They do not need to withstand a determined adversary — they need to leave no easy path from the documentation tools to source files.

### Two-tier risk classification

| Tool class | Input | Risk | Guard strategy |
|---|---|---|---|
| Documentation tools | Arbitrary content + filename | Injection, escape, arbitrary write | Path containment + extension allowlist + size cap + tight rate limit |
| Semantic code tools | Validated parameters (symbols, paths, method names) | Thrashing / runaway loops only | Loose rate limit (circuit breaker) — validity is self-limiting |

---

## Part 1 — Scoped documentation tools

### Directory layout (all under solution root)

```
<solutionRoot>/docs/
├── documentation/      # general project documentation
├── plans/              # migration / refactor plans
├── handoffs/           # session handoff documents
├── completed/          # append-only completed-work log
└── migration-state.yaml  # single fixed state file (no subdirectory)
```

The server resolves `<solutionRoot>` from the currently loaded solution's directory. If no solution is loaded, documentation tools return an error directing the caller to `load_solution` first.

### Tool list

| Tool | Directory / file | Mode | Filename param |
|---|---|---|---|
| `list_project_documentation` | `docs/` (recursive) | read | none |
| `read_project_documentation` | `docs/documentation/` | read | yes |
| `update_project_documentation` | `docs/documentation/` | write | yes |
| `read_plan` | `docs/plans/` | read | yes |
| `update_plan` | `docs/plans/` | write | yes |
| `read_handoff` | `docs/handoffs/` | read | yes |
| `write_handoff` | `docs/handoffs/` | write | yes |
| `read_completed_work` | `docs/completed/` | read | yes |
| `append_completed_work` | `docs/completed/` | append-only | yes |
| `read_current_state` | `docs/migration-state.yaml` | read | none (fixed) |
| `update_current_state` | `docs/migration-state.yaml` | write | none (fixed) |

**Design notes:**

- `read_current_state` / `update_current_state` take **no filename** — there is exactly one state file. Fewer free parameters = smaller surface.
- `append_completed_work` is **append-only** — the agent records what it finished but cannot rewrite history. This preserves the audit trail; the agent cannot quietly erase a record of something that broke.
- `list_project_documentation` enumerates only the `docs/` subtree so the agent can discover its own files without being able to enumerate the source tree.
- All write tools create the target subdirectory if it does not exist (within `docs/` only).

### Example signatures

```csharp
[McpServerTool]
[Description("Reads a project documentation file from the solution's docs/documentation/ " +
    "directory. The filename parameter is treated as a bare filename — any path components " +
    "are stripped and rejected. Only documentation file types (.md, .yaml, .yml, .json, .txt) " +
    "are permitted. Cannot read source files or files outside the docs directory.")]
public async Task<DocReadResult> ReadProjectDocumentation(string filename)

[McpServerTool]
[Description("Writes (creates or overwrites) a project documentation file in the solution's " +
    "docs/documentation/ directory. Filename is treated as bare — path components stripped and " +
    "rejected. Only documentation file types permitted. Content size capped. Cannot write source " +
    "files or files outside the docs directory.")]
public async Task<DocWriteResult> UpdateProjectDocumentation(string filename, string content)

[McpServerTool]
[Description("Appends an entry to the completed-work log in docs/completed/. Append-only — " +
    "existing content cannot be overwritten or deleted. Use to record finished work as an " +
    "immutable audit trail.")]
public async Task<DocWriteResult> AppendCompletedWork(string filename, string entry)

[McpServerTool]
[Description("Reads the migration state file (docs/migration-state.yaml). Takes no parameters — " +
    "there is exactly one state file.")]
public async Task<DocReadResult> ReadCurrentState()

[McpServerTool]
[Description("Lists all files in the solution's docs/ directory tree. Returns filenames and " +
    "relative paths within docs/ only. Cannot enumerate source files or directories outside docs/.")]
public async Task<DocListResult> ListProjectDocumentation()
```

### Return types

```csharp
public class DocReadResult
{
    public bool    Found     { get; set; }
    public string  Filename  { get; set; }   // bare filename actually read
    public string? Content   { get; set; }   // null if not found
    public string? Error     { get; set; }
}

public class DocWriteResult
{
    public bool    Success     { get; set; }
    public string  Filename    { get; set; }
    public string  FullPath    { get; set; }   // resolved path (for confirmation)
    public int     BytesWritten { get; set; }
    public string? Error       { get; set; }
}

public class DocListResult
{
    public List<string> Files { get; set; }   // relative paths within docs/
    public int          Count { get; set; }
}
```

---

## Part 2 — Defense-in-depth path/extension guards

Every documentation tool that accepts a `filename` MUST run the following validation chain **before** any file access. Implement once as a shared helper (e.g. `DocPathGuard.ResolveSafe(docsSubdir, filename)`) and call it from every documentation tool.

### Validation chain (in order)

```csharp
public static (bool Ok, string FullPath, string Error) ResolveSafe(
    string docsSubdirRoot,   // e.g. <solutionRoot>/docs/documentation
    string filename)
{
    // 1. Strip to bare filename — discard all directory components
    string bareName = Path.GetFileName(filename);
    if (bareName != filename)
        return (false, "", "Filename only — path components are not allowed.");

    if (string.IsNullOrWhiteSpace(bareName))
        return (false, "", "Empty filename.");

    // 2. Reject alternate data streams and invalid characters (Windows-specific)
    if (bareName.Contains(':'))
        return (false, "", "Invalid character ':' in filename (alternate data stream).");
    if (bareName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        return (false, "", "Invalid characters in filename.");

    // 3. Reject Windows reserved device names
    string nameNoExt = Path.GetFileNameWithoutExtension(bareName).ToUpperInvariant();
    string[] reserved = { "CON","PRN","AUX","NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9" };
    if (reserved.Contains(nameNoExt))
        return (false, "", $"'{nameNoExt}' is a reserved filename.");

    // 4. Extension ALLOWLIST (not blocklist) — exact match via GetExtension
    string ext = Path.GetExtension(bareName).ToLowerInvariant();
    string[] allowed = { ".md", ".yaml", ".yml", ".json", ".txt" };
    if (!allowed.Contains(ext))
        return (false, "", $"Extension '{ext}' not permitted. Allowed: {string.Join(", ", allowed)}");

    // 5. Resolve and confirm containment (defense-in-depth backstop)
    string fullPath = Path.GetFullPath(Path.Combine(docsSubdirRoot, bareName));
    string rootFull = Path.GetFullPath(docsSubdirRoot);
    if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString()))
        rootFull += Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        return (false, "", "Resolved path escapes the documentation root.");

    return (true, fullPath, "");
}
```

### Why each layer exists

| Layer | Catches |
|---|---|
| Strip to bare filename | `../`, absolute paths, subdirectory traversal — the primary containment |
| Reject `:` and invalid chars | NTFS alternate data streams (`notes.md:payload.cs`), malformed names |
| Reserved device names | `CON`, `NUL`, etc. — Windows special files |
| Extension allowlist | Any source/project file type — `.cs`, `.csproj`, `.razor`, `.xaml`, anything not explicitly documentation. **Allowlist is closed and complete; a blocklist would be perpetually incomplete.** |
| GetFullPath + StartsWith | Backstop for anything the above missed; confirms the resolved absolute path is genuinely inside the docs root |

### Critical implementation rules

- **NEVER use `filename.Contains(".cs")` or any substring match.** It over-matches (`notes.css`, `release.csv`) and is gameable. Always extract the real extension with `Path.GetExtension` and compare exactly.
- **Use an allowlist, never a blocklist.** Documentation is a short, closed set of types. Anything else is rejected by default.
- The containment check (layer 5) must run on the `GetFullPath`-resolved path, not the raw combined path, so symlink/traversal resolution is accounted for.

### Content-size cap (write tools only)

```csharp
const int MaxDocBytes = 512 * 1024;   // 512 KB — generous for markdown/yaml/json
if (Encoding.UTF8.GetByteCount(content) > MaxDocBytes)
    return new DocWriteResult { Success = false,
        Error = $"Content exceeds {MaxDocBytes} bytes. Documentation files should be concise." };
```

Rate limiting bounds frequency; size cap bounds payload. Both matter for the arbitrary-content channel; neither applies to the semantic tools.

---

## Part 3 — Per-tool rate limiting

### Policy: asymmetric by tool class

- **Documentation tools** (arbitrary content = real attack surface): tight limits.
- **Semantic code tools** (validated params = self-limiting): loose limits — circuit breaker against thrashing only.

### Suggested initial thresholds (calls per 60s sliding window)

| Tool | Limit/min | Rationale |
|---|---|---|
| `read_*` documentation tools | 30 | Reading is cheap and safe |
| `update_project_documentation`, `update_plan`, `write_handoff` | 10 | Writes should be deliberate |
| `append_completed_work` | 15 | Logging, slightly higher |
| `update_current_state` | 5 | State updates are once-per-phase events |
| `list_project_documentation` | 20 | Discovery, moderate |
| `run_bridge_batch`, `run_uplift_batch`, `propagate_cancellation_token_batch` | 5 | Large operations; high frequency = a loop |
| Other semantic tools (`rename_symbol`, `get_call_graph`, etc.) | 120 | Circuit breaker only — never trips in normal batch work |

**Start generous, tighten based on observed normal behavior.** Setting limits too tight initially causes false trips on legitimate work. Use real session data (e.g. CodeBurn per-tool call rates) to set limits at a comfortable multiple above observed normal.

### Implementation

- Maintain a per-tool sliding-window counter in the workspace-manager singleton (in-memory, reset on server restart).
- The rate limiter operates at the **MCP tool boundary** — count the agent's tool invocations, NOT internal engine calls. `run_uplift_batch` counts as ONE invocation even if it triggers hundreds of internal operations. Do not throttle internal engine work.
- On breach, **return a failure with a diagnostic message**, not a silent cooldown:

```csharp
return Error(
    $"Rate limit: '{toolName}' called {count} times in {windowSeconds}s (limit {limit}). " +
    $"This usually indicates a retry loop or thrashing. Stop, assess what is failing, " +
    $"and either fix the root cause or — if this is legitimate high-volume work — " +
    $"propose a batch tool that accomplishes it in fewer calls.");
```

The diagnostic message turns the rate limit into a gap-surfacing signal: a tool that frequently trips its limit is telling you it needs a batch variant (the same signal that motivated `run_bridge_batch`).

### Configuration

Make thresholds configurable (e.g. via a `rate-limits.json` in the server directory or environment variables) so they can be tuned without recompiling. Provide the table above as defaults.

---

## Part 4 — Integration with the sealed-tank configuration

These tools are the **only** file-access aperture the agent should have once the VS Code generic file/shell tools are disabled. The intended end-state capability model:

| Need | Allowed via |
|---|---|
| Read/modify source code | Semantic Roslyn tools only (`rename_symbol`, `run_bridge_batch`, etc.) |
| Inspect code structure | Semantic query tools (`get_code_inventory`, `get_call_graph`, etc.) |
| Persist plans / state / handoffs / logs | Scoped documentation tools (this spec) |
| Anything else | Does not exist — no general file/shell access |

The agent's entire world becomes: semantic operations for code, scoped documentation tools for working files. There is no general-purpose escape hatch because there is no general-purpose tool.

### Recommended agent-instruction addition

```markdown
## File access
You have NO general file system access. To work with files:
- Source code: use the Roslyn semantic tools (rename_symbol, run_bridge_batch, etc.)
- Your working documents (plans, state, handoffs, completed-work): use the 
  scoped documentation tools (read_plan, update_current_state, etc.)
If you need file access that no tool provides, STOP and propose a new MCP tool. 
Do not attempt shell, Python, or any workaround — they are unavailable by design.
```

---

## Implementation order

1. **`DocPathGuard.ResolveSafe` shared helper** (Part 2) — the security foundation everything else depends on. Build and unit-test this first and thoroughly.
2. **Read-only documentation tools** — `read_project_documentation`, `read_plan`, `read_handoff`, `read_completed_work`, `read_current_state`, `list_project_documentation`. Lower risk (no writes), validates the path guard in practice.
3. **Write documentation tools** — `update_*`, `write_handoff`, `append_completed_work`, `update_current_state`. Add content-size cap.
4. **Rate limiting** (Part 3) — applied to documentation tools first (tight), then loose circuit-breaker limits on semantic tools.
5. **Verify sealed-tank integration** — disable VS Code generic file/shell tools and confirm the agent can still maintain its working documents through the scoped tools alone.

Each step builds on the previous. The path guard is the keystone — get its tests right before building tools on top of it.

---

## Tests required

### `DocPathGuard.ResolveSafe`

1. Bare valid filename (`notes.md`) → resolves inside docs subdir
2. Relative traversal (`../../Main/Avaal.Forms/SomeForm.cs`) → rejected (path components)
3. Absolute path (`C:\Windows\System32\...`) → rejected (path components)
4. Source extension (`SomeForm.cs`) → rejected (extension not allowed)
5. Project extension (`Avaal.csproj`) → rejected
6. Substring trap (`release.csv`) → rejected (`.csv` not in allowlist) but for the right reason — NOT because it contains `.cs`
7. Allowed extension variants (`.md`, `.yaml`, `.yml`, `.json`, `.txt`) → all accepted
8. Alternate data stream (`notes.md:hidden.cs`) → rejected (contains `:`)
9. Reserved name (`CON.md`, `NUL.txt`) → rejected
10. Empty / whitespace filename → rejected
11. Resolved path containment backstop — a crafted name that survives stripping but resolves outside root → rejected
12. Case-insensitivity of extension (`SomeForm.CS`, `notes.MD`) → handled correctly

### Documentation tools

13. Read existing file → `Found = true`, content returned
14. Read non-existent file → `Found = false`, no error thrown
15. Write valid file → created at correct path inside docs subdir
16. Write oversized content (> 512 KB) → rejected with size error
17. `append_completed_work` → appends, does not overwrite existing content
18. `read_current_state` / `update_current_state` → operate on the fixed `migration-state.yaml`, no filename accepted
19. `list_project_documentation` → returns only docs/ subtree files, never source files
20. No solution loaded → documentation tools return clear "load solution first" error

### Rate limiting

21. Documentation write under limit → succeeds
22. Documentation write over limit → fails with diagnostic message
23. Semantic tool under (loose) limit → succeeds normally during batch work
24. Batch tool counts as one invocation despite many internal operations
25. Sliding window — calls age out of the window correctly over time

---

## Build and deployment note

The RoslynSentinel MCP server process holds a file lock on the running binary while VS Code is open. Build the Debug configuration (`dotnet build -c Debug`) for development — its output path differs from the running Release binary, so it will not conflict. To deploy: stop VS Code (or the MCP server process), `dotnet build -c Release`, then restart. Confirm `.mcp.json` points at the Release binary path.

---

## Key files in RoslynSentinel

- `RoslynSentinel.Server/SentinelQualityTools.cs` — `[McpServerTool]` registrations
- `RoslynSentinel.Server/SentinelWorkspaceTools.cs` — workspace/solution-root access
- `RoslynSentinel.Server/PersistentWorkspaceManager.cs` — singleton home for rate-limit counters and solution-root resolution
- New file suggested: `RoslynSentinel.Server/DocumentationTools.cs` — the scoped documentation tool registrations
- New file suggested: `RoslynSentinel.Server/DocPathGuard.cs` — the shared path-validation helper

---

## Acceptance criteria

1. All documentation tools registered and callable via MCP.
2. `DocPathGuard.ResolveSafe` passes all listed tests, especially the traversal, extension, and ADS rejection cases.
3. No documentation tool can read or write any file outside `<solutionRoot>/docs/`, confirmed by attempting traversal and source-extension writes.
4. `append_completed_work` cannot overwrite existing content.
5. Rate limits enforced per-tool with diagnostic failure messages; batch tools count as single invocations.
6. Content-size cap enforced on write tools.
7. With VS Code generic file/shell tools disabled, the agent can still read and write its plans, state, handoffs, and completed-work logs entirely through the scoped tools.
8. No regression in existing semantic tools.

<!-- v1 -->
