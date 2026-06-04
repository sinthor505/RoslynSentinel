# RoslynSentinel — Batch-First Tool Architecture & Result-Persistence Spec
<!-- v3 -->

**Date:** 2026-05-30
**Purpose:** Define the architectural direction for reducing agent conversation churn and tool-manifest bloat by (a) collapsing single-unit mutation tools into batch-first tools that accept a target list, (b) returning summaries instead of result blobs, (c) persisting full results to a dedicated forensic file tier, and (d) adding a failure-rate circuit breaker enforced as server state. This is a planning spec — hand to an agent to develop per-phase implementation plans.

**Target server:** RoslynSentinel (source available locally)
**Mode:** Design spec / planning input — NOT a turn-by-turn implementation plan

---

## 1. Problem statement

Two distinct token costs, attacked separately:

1. **Manifest tax (fixed, per-turn):** ~48k tokens of tool definitions injected every turn. Addressed by consolidation (see the tools-review doc) and by batch-first collapsing.
2. **Conversation churn (growing, per-session):** each turn re-processes the full prefix — system prompt + manifest + entire history + new I/O. History grows every turn, so cumulative processing over an N-turn sweep is roughly O(N²) in the conversation portion. Thirty single-file tool calls means thirty call/result pairs, each re-read on every subsequent turn.

Churn is the dominant cost on long migration sweeps and is the primary target of this spec. The independent argument that does **not** rely on any prompt-caching assumption: even if re-processing were free, a window stuffed with 30 turns of call/result churn is a window not available for the code the agent must reason about — lost-in-the-middle degradation hits exactly when the session is long enough to matter. Batching keeps the window lean regardless of backend caching behavior.

---

## 2. Core principle — internalize iteration, not orchestration

This extends the project's standing architectural thesis (model owns decisions; IDE/compiler own mechanics) to the tool surface itself.

- **Internalize iteration:** a batch the agent selects the targets for. The agent decides WHICH targets and reads the summary; the server stops making it do that once per item. ✅
- **Do NOT internalize orchestration the agent authors:** the server must not become a workflow interpreter that executes arbitrary agent-authored step sequences (a DSL/VM with data flow between steps, conditionals, and per-step rollback semantics). That is a second product with worse debuggability than the thing it replaces. ❌

The agent still decides what to do and reads the result; the server just stops requiring N round-trips to do one conceptual operation.

### The two gates

Every consolidation/batching decision passes through these:

**Gate A — Independence (what is safe to batch):**
Does correct execution of item N depend on the agent observing item N−1's result?
- **No** → batch freely. (Async migration ops: each file independent; cross-item effects caught by validate/rollback; failures flagged not fatal.)
- **Yes** → do not batch across that point, OR batch only with per-item validate/rollback discipline. (`rename_symbol`: renames can interact — rename A, then B which referenced A. Batch only with strict per-item validation, or leave single.)

**Gate B — Collapse vs. god-tool (how far to consolidate):**
Combine genuinely similar operations. Do NOT build one god-tool with a 40-value discriminated-union `kind` enum and per-kind conditional params — the schema becomes unusable and the agent picks wrong. "A few parameterized tools," not one.

---

## 3. Batch-first tool shape

### 3.1 Unit of batching is a target, not "method vs file"

The input is NOT "the same parameters whether method, list, or file." The unit is a **target = (file path, optional method-name filter)**:

| Intent | Target representation |
|---|---|
| Single method | one target, filter `["MethodName"]` |
| Whole file | one target, filter `null` |
| Many files | many targets |
| "Batch of 1" | one-element target list |

The file-vs-method distinction is a **filter inside the target**, not a separate parameter. Stating it this way keeps the schema one shape and avoids fighting Roslyn's path-vs-symbol duality (see `spec-find-namespace-path-mismatches-v1.md` — semantic vs. path resolution diverging is a known hazard).

This is already proven by the existing `propagate_cancellation_token_batch` input shape (`List<PropagateCtFileTarget>` = file path + optional method filter). The program is to make that the *only* form and retire the `_in_method` / `_in_file` siblings.

### 3.2 Canonical input

```csharp
public class BatchTargetInput
{
    public List<BatchTarget> Targets      { get; set; }
    public bool              DryRun       { get; set; } = false;
    public int               MaxItems     { get; set; } = 100;
    // tool-specific options live here, NOT a generic plan/step list
}

public class BatchTarget
{
    public string    FilePath    { get; set; }
    public string[]? MethodNames { get; set; }   // null = whole file
}
```

No generic "steps" / "plan DAG" field. Tool-specific options are explicit named parameters on the input type. If a tool needs a fixed multi-phase sequence internally (e.g. asyncify), that sequence is **written and tested in the server**, selected by name — never assembled by the agent at runtime.

### 3.3 Scope guard — batch mutations, not queries

The batch principle targets **mutation/transformation tools** that currently operate one-symbol/method/callsite at a time. `scan_*` / `get_*` tools are **already batch** in the sense that matters — they return collections from a single call. Do NOT wrap a batch loop around a query that is inherently a collection query.

---

## 4. Result shape — summary-first, three-tier persistence

Batching moves bloat from many-small-recurring to one-big-recurring unless the return is constrained. A 30-file batch yielding a full per-call-site blob lands on one turn and then sits in history, re-read every subsequent turn — strictly worse than returning it once if left unconstrained.

### 4.1 Three persistence tiers

| Tier | Medium | Lifetime | Consumer |
|---|---|---|---|
| Summary | Tool return → context | Ephemeral (recurs in history) | Agent loop |
| Failure work-queue | `[MigrationCandidate(...)]` source attributes | Durable, queryable via `scan_migration_candidates` | Next session / agent |
| Full result blob | File on disk (forensic tier) | Persistent audit trail | Human, on demand |

Successes are implied by absence (no flag, not enumerated in the return). Failures are the actionable part and appear inline (capped). The blob is the forensic record.

### 4.2 Agent-facing return

```csharp
public class BatchResultSummary
{
    public string  ChangeId       { get; set; }
    public string  BlobName       { get; set; }   // pointer to forensic tier
    public int     Succeeded      { get; set; }
    public int     Skipped        { get; set; }
    public int     RolledBack     { get; set; }
    public int     Failed         { get; set; }
    public int     Attempted      { get; set; }
    public List<FailureDetail> Failures { get; set; } // CAPPED at 10–20
    public string  Severity       { get; set; }   // "ok" | "caution" | "halt"
    public string  Directive      { get; set; }   // human-readable readout
    public bool    BreakerOpen    { get; set; }
}
```

**Severity is a field, not an adjective.** The agent keys decisions off `Severity == "halt"`, never off parsing the tone of `Directive`. Prose-only directives are the weakest link in the current LLM stack — the platform-aware-variants work already documented the model deprioritizing prose directives under competing generation signals. Do not repeat that here: give it a field.

Failure slice is capped (first 10–20) so a 500-failure batch never dumps a blob the agent then tries to fix. Enough to course-correct minor issues; not enough to drown.

### 4.3 Filtered drill-down (NOT file-cat)

```csharp
get_operation_detail(
    string  changeId,
    string? filter = null,    // "failures" | "skipped" | "rolledback" | "file:<path>" | null
    int     maxItems = 50)
```

Returns a **slice**, not the document. A `read_operation_result` that dumps whole JSON back into context just defers the bloat to read-time, where it then sits in history — worse than returning once. The whole-file form is human-only: they open the blob in an editor where size costs nothing; the agent only ever queries slices.

---

## 5. Result-blob persistence — separate from agent doc tools

### 5.1 Do NOT route blobs through the scoped documentation tools

The scoped doc tools (`update_project_documentation` etc.) exist to **constrain the agent** — `DocPathGuard.ResolveSafe`, the 512 KB cap, the write rate limit. Those guards defend against untrusted agent-supplied filenames. The server writing its own machine-generated blob is **trusted code writing a server-controlled filename** — running it through the agent's guard rails is pointless and actively harmful:

- The 512 KB cap could truncate the large batch result you most want a full record of.
- The write rate limit was tuned for deliberate agent writes, not machine artifact emission.
- It inverts the threat model — the guard is the aperture built *for* the agent; the server doesn't defend against itself.

The server writes the blob via a **direct internal file write** with full access, bypassing the agent-facing constraints. Reusing the underlying path-combine plumbing for a single "write under root" code path is fine; skip the rate limit and the untrusted-filename guard — the filename is server-controlled and safe by construction.

### 5.2 Identifier and naming

Thread the existing `ChangeId` — do not invent a parallel slug. Blob filename is a function of it:

```
<toolName>_<utcTimestamp>_<changeId>.json
e.g. asyncify_20260530T1430Z_<changeId>.json
```

One identifier ties together the staged change, the blob, and the drill-down query. Timestamp prefix is for human scannability in a directory listing only.

### 5.3 Namespace segregation

Do NOT write blobs into `docs/documentation/` or `docs/completed/` — they would pollute `list_project_documentation`, which is the agent's view of its *own* planning files. Use a dedicated subtree:

```
.roslynsentinel/operations/     (preferred — outside docs/ entirely)
  └── <toolName>_<timestamp>_<changeId>.json
```

Optional `list_operation_results` if the agent needs discovery. Keep the agent's planning surface clean of machine exhaust.

### 5.4 Cleanup

Default: leave them, do not auto-expire — disk is cheap and the audit trail is useful. Add a manual `prune_operation_results(olderThanDays)` only if clutter becomes a real problem. Flagged as a deliberate decision, not an accident.

---

## 6. Failure-rate circuit breaker

### 6.1 Threat model — this is a runaway kill-switch, not anti-scheming

The breaker is the same class as the existing rate-limit circuit breaker, keyed on **failure-rate instead of call-rate**. Its real job: a batch hit a systemic problem (build broken, a namespace/path mismatch per `spec-find-namespace-path-mismatches-v1.md`, a bad migration assumption) and is failing the *same way* across many items. Stopping fast prevents hundreds of attribute flags and rolled-back changes that all need cleanup.

It is NOT primarily a defense against a scheming agent. That reframing drives the tuning below.

### 6.2 Enforce as server state, never as a message

**Load-bearing correction.** The breaker must be state in the server, not a sentence in a response. A halt *message* is an instruction, and a completion-driven agent rationalizes around instructions — the exact failure mode the sealed-tank design exists to prevent.

- On trip: set `breaker_open` flag in the workspace-manager singleton (same home as rate-limit counters).
- While open: **every mutating tool returns a structured refusal WITHOUT executing** — the code refuses before doing any work. This is the actual enforcement.
- The scary all-caps user-facing string is just the readout of a state already enforced mechanically.

**Do not go silent.** A server that stops responding looks like a hang/crash and loses the diagnostic. Return a fast, cheap, structured refusal:

```json
{ "severity": "halt", "breakerOpen": true,
  "directive": "Circuit breaker open. All mutating tools disabled until reset_breaker is called by the user. <context>" }
```

Loud and immediate beats silent.

### 6.3 Trip on the right axis — NOT an absolute count

An absolute count (e.g. "20 failures") fires on the wrong axis. 20 of 25 attempts is systemic — halt. 20 of 4,000 methods is a 0.5% skip rate — probably just legitimately un-migratable code (P/Invoke, hand-tuned threading), and halting there false-positives on every large run.

Triggers, in priority order:

1. **Consecutive-failure streak (primary):** N failures in a row with zero successes between them. Cleanest "something is fundamentally wrong" signal; independent of solution size.
2. **Failure-rate-above-floor (backup):** failures/attempts past a threshold (e.g. >25%) AFTER a minimum attempt count (e.g. ≥15) to avoid early-sample noise.
3. **Weight rollbacks heavier than skips.** A skip (un-migratable, flagged, left alone) is benign and expected. A rollback (applied, failed validation, reverted) is more expensive and more suspicious. The trip calculation should weight rollbacks above skips to match actual cost.

Discipline per the existing rate-limit spec: start generous, tighten on observed normal behavior. Use real session data to set the floor at a comfortable multiple above normal.

### 6.4 Reset is manual, by design

Manual re-enable via `reset_breaker` (or a deliberate user action). Auto-reset defeats the purpose. This is the one place in the whole design where friction is *wanted* — keep the human in the loop.

### 6.5 Graduated severity ladder

| Condition | `severity` | Behavior |
|---|---|---|
| Within normal failure tolerance | `ok` | proceed |
| Elevated but below trip | `caution` | proceed; directive notes elevated failures |
| Streak/rate/rollback trip | `halt` | breaker opens; mutating tools refuse until reset |

---

## 7. Tools to promote to batch-first

Candidates pass **Gate A (independence)**. Tools failing Gate A are listed separately with the required discipline.

### 7.1 Clean batch candidates (independent items, validate/rollback safety)

| Current tool(s) | Collapse to | Notes |
|---|---|---|
| `propagate_cancellation_token_in_method` / `_in_file` / `_batch` | `propagate_cancellation_token` (target-list) | Already has the batch shape; retire the two siblings |
| `convert_to_async_bridge` (single) + `run_bridge_batch` | `convert_to_async_bridge` (target-list) | Single = batch of 1; absorb batch variant |
| `add_cancellation_token_to_method` / `apply_cancellation_token_to_file` | `add_cancellation_token` (target-list) | Method = filtered target; file = unfiltered |
| `run_uplift_batch` / `run_uplift_batch_multi` | `run_uplift` (target-list) | Multi-target is the general case |
| `flag_migration_candidate` / `flag_migration_candidates_in_project` | `flag_migration_candidates` (scope: targets or project) | Scope param, not separate tools |

### 7.2 The asyncify macro-workflow (named, fixed-sequence, NOT agent-authored)

The proven end-to-end migration sequence (scout → flag attributes → build async bridges → uplift callers → propagate cancellation tokens) currently spread across multiple tools and orchestrated by the agent. Internalize the **fixed, tested sequence** into one named tool:

```
asyncify(targets, exclusions, dryRun, propagateCancellationTokens = true)
```

- The agent SELECTS and PARAMETERIZES it (which project, which exclusions). The agent does NOT author the phase order.
- Internal sequence is the server's tested repertoire — pure mechanical orchestration the agent currently does for no judgment value.
- Summary-return + blob persistence + breaker all apply.
- This is "internalize the workflow, not the workflow engine" — the capstone of the batching program, valid precisely because the sequence is fixed and tested to death.

### 7.3 Query/graph consolidation (Gate B — parameterize, don't god-tool)

Not "batch" (these already return collections) but consolidation that trims the manifest. Renamed to `scan_` per the naming convention (§12):

| Current tool(s) | Collapse to | Parameter |
|---|---|---|
| `find_task_yield_usage`, `find_task_delay_usage`, `find_task_delay_zero_usage`, `find_task_when_all_usage`, `find_task_void_usage`, `find_task_run_in` | `scan_task_api_usage` | `api: "Yield\|Delay\|Delay_Zero\|WhenAll\|Void\|RunSync"` |
| `find_unsafe_lazy_init` / `find_unsafe_lazy_init_thread` | `scan_unsafe_lazy_init` | `checkThreadSafety: bool` |
| `find_mutable_public_properties` / `find_mutable_public_collection_properties` | `scan_mutable_public_properties` | `collectionsOnly: bool` |
| `get_call_graph` / `get_reverse_call_graph` / `get_blast_radius` | `get_relationship_graph` | `direction: "forward\|reverse"`, `depth`, `edgeKinds` (blast radius = reverse traversal with wider edge set: field/inheritance refs, not just call edges — model as relationship tool, not strict call-graph) |
| `add_braces`, `use_index_from_end`, `upgrade_unbound_nameof`, `cleanup_implicit_spans`, `use_field_backed_properties`, `wrap_in_region`, and other single-rule cleanups | `apply_modernization` | `ruleId: string` (the analyzer/diagnostic ID), plus standard `BatchTargetInput` for scope. Applies one registered Roslyn code fix across the target set. Subsumes ~10 single-rule tools. This is a **mutation** consolidation, so it follows the batch-first shape and result/breaker rules — listed here because it's a parameterize-the-family move, but it is NOT a query. |

### 7.3 boundary — stop before the god-tool

Do NOT merge all `scan_*` / `get_*` tools into one discriminated-union monster. Combine genuinely-similar families only.

### 7.4 Failing Gate A — batch only with discipline, or leave single

| Tool | Why it fails Gate A | Disposition |
|---|---|---|
| `rename_symbol` | Renames interact (rename A, then B that referenced A); agent often wants per-result visibility | Leave single, OR batch only with strict per-item validate/rollback and per-item result reporting |

### 7.5 Leave off-profile (do not batch, do not load by default)

Niche scaffolders (`generate_http_client`, `generate_decorator_class`, `add_benchmark_stub`, etc.) and migration-only tools when not in a migration: opt-in profile, ranked low or behind a discovery affordance. Per the tools-review doc.

---

## 8. Low-level fallback set — capable, not minimal

The more workflow is internalized, the rarer low-level access is needed — but the times it IS needed are the weird residue where macros didn't fit. That set is the escape valve preventing the agent from rationalizing back to a shell. It must be genuinely functional, not a token-budget afterthought.

Must include (some are gaps flagged in the tools-review doc):

- `get_method_source` / `get_file_outline` — **BLOCKER per review doc.** contextSnippet-based tools are dead weight without a source-reading aperture.
- `search_solution_text(pattern, isRegex, fileGlob)` — grep replacement, solution-scoped.
- `rename_symbol`, `extract_method`, `extract_constant` (renamed from `_safe`).
- Staged-change lifecycle: `apply_staged_changes`, `validate_staged_changes`, `get_staged_changes`, `discard_staged_changes`.
- `undo_last_apply(changeId)` — symmetric reversal of a *committed* apply, keyed on the same `ChangeId` threaded everywhere else. Closes a real asymmetry: the breaker rolls back *uncommitted* changes, but a committed batch currently has no first-class reversal. (Flagged by external tool-list review, 2026-05-30.)

### 8.1 Sealed-tank completeness — build/test/git read access

A genuinely sealed tank (generic shell/file tools removed) needs a way to validate and to see what changed, beyond compiler diagnostics. This intersects this spec because **the circuit breaker trips on validation failure** — and "what does validation mean" is underspecified without a build/test signal. Flagged here, not fully specced (it's sealed-tank scope, not batch-architecture scope), but the implementing agent must resolve it before the tank is sealed:

- `run_build(projectName?)` / `run_tests(filter?)` — validation beyond Roslyn diagnostics.
- `git_status` / `git_diff` — **read-only.** Agents repeatedly need to know what they've changed; without this they re-derive it expensively or rationalize back to a shell.

Decision needed: does breaker validation rely on Roslyn compilation only, or also on `run_build`/`run_tests`? If the latter, these move from "sealed-tank nicety" to "breaker dependency" and must land before the breaker.

---

## 9. Target end-state shape

Roughly four tiers, ~40 tools total:

1. **Macro-workflows (5–10):** `asyncify`, `full_quality_pass`, etc. Named, fixed-sequence, parameterized, summary-return. Agent selects and parameterizes; never authors the sequence.
2. **Batch-first mutations (8–15):** the target-list tools from §7.1.
3. **Parameterized query/graph (8–12):** the collapsed families from §7.3.
4. **Low-level fallback + lifecycle (~15):** §8 — the capable escape valve.
5. **Scoped docs:** the existing set, unchanged.

Niche scaffolders and migration-only tools stay off-profile until needed.

---

## 10. Open items for per-phase planning

- [ ] Per-tool migration: define exact input/return types for each §7.1 collapse, preserving existing behavior at "batch of 1".
- [ ] Deprecation strategy: keep `_in_method`/`_in_file`/`_batch` as thin aliases calling the unified tool during a transition window? (Tools-review doc suggests alias-during-deprecation for client-side script compatibility.)
- [ ] Define `BreakerState` storage and the exact trip thresholds (consecutive-streak N, rate floor %, min-attempts, rollback weight). Start generous; tune on real session data.
- [ ] Implement `reset_breaker` and decide the user-action surface for it.
- [ ] Define the forensic blob JSON schema (per-item records: target, outcome, reason, before/after pointers).
- [ ] Implement `get_operation_detail` filtered drill-down; confirm it never returns the whole blob.
- [ ] Decide `.roslynsentinel/operations/` vs. a `docs/operations/` subtree; implement the direct internal writer (bypassing `DocPathGuard`).
- [ ] Sequence the work: source-reader + search (§8 blockers) FIRST, then batch collapses, then asyncify macro, then breaker, then blob persistence.
- [ ] **Verify the MCP schema generator emits nested array-of-object input schemas correctly — the entire batch shape depends on it.** The external tool-list review (2026-05-30) found several existing tools with empty `"properties": {}` inputSchema blocks. `BatchTargetInput` is `List<BatchTarget>` with a nested `string[]? MethodNames` — if the `[McpServerTool]` schema generator drops nested object/array properties, the batch tools serialize as uncallable. **Confirm a round-trip of the canonical input shape through the MCP boundary BEFORE building any batch tool on it.** This is load-bearing, not hygiene — find out before writing five tools against a shape that doesn't serialize.
- [ ] Re-measure minified `name + description + schema` payload after each consolidation — savings are sublinear in tool count (fat input schemas on parameterized tools), so measure, don't assume.

### 10.1 Rejected — `describe_tool` / on-demand description lookup

The external review (2026-05-30) proposed capping descriptions at ~180 chars and moving detail behind a `describe_tool(name)` lookup, calling it "the biggest single token win." **Rejected**, recorded here so it is not re-litigated:

- It does not reduce the **manifest** — input schemas still ship eagerly to every turn; only descriptions shrink.
- It trades manifest tokens for **conversation churn** — each lookup is an extra round-trip, and churn is this spec's primary cost target (§1). Trading the cheaper axis for the more expensive one is backwards given the priority ordering.
- The MCP client fetches the tool list once at init and does not re-query mid-session, so a lookup tool can only ever surface detail as tool *output*, which then sits in history and is re-read every subsequent turn — the exact bloat §4 exists to prevent.

**What IS adopted from the same review:** trim verbose descriptions (strip "FIXES MS BUG" preambles, return-field dumps, repeated boilerplate) down to the selection-signal essentials. Trimming descriptions is good; adding a lookup indirection is not. These are different actions — do the first, not the second.

### 10.2 Startup tool-list dump (internal, not an exposed tool)

On server startup, after tool registration completes, write the full registered tool list to `tool_list.json` in the server root. This is an **internal method wired into the post-initialization path — NOT an `[McpServerTool]`.** It must not appear in the manifest (it would be self-referential overhead and pointless to an agent).

Purpose: debugging and inspection. Lets a human open `tool_list.json` and see exactly what an agent sees — names, descriptions, and input schemas — without standing up a client to query `tools/list`. Particularly useful for verifying the schema-serialization concern above: if `BatchTargetInput` renders with empty properties in `tool_list.json`, the generator bug is confirmed at a glance.

- Write on every startup, overwriting the previous file (it reflects current state, not history).
- Serialize the **exact** payload the MCP layer would emit (name + description + minified inputSchema) — not a hand-rolled summary, or it won't reveal serialization bugs.
- Include a top-of-file metadata block: tool count, total payload char/token estimate, generation timestamp. Turns the file into a one-glance manifest-size gauge for the consolidation work (§1 manifest tax, and the re-measure item above).
- Root location, not under `docs/` — it is server diagnostic output, not agent-facing documentation (same segregation principle as the operation blobs in §5.3).

---

## 11. Standing constraints (carried from prior specs)

- **C# style:** explicit types, always braces, no expression-bodied members.
- **Build/deploy:** RoslynSentinel process holds a file lock on the running binary while VS Code is open. Build Debug for dev (different output path); to deploy Release, stop VS Code / the MCP process first, then `dotnet build -c Release`, confirm `.mcp.json` points at the Release path.
- **The line:** internalize the workflow, not the workflow engine. Every macro/batch tool is a sequence or iteration the server owns and tested — never a step sequence the agent assembles at runtime.

---

## 12. Tool naming conventions

All tool names use `snake_case`. The prefix encodes **epistemic status** — what the tool returns and how much the caller should trust it. This is the primary selection signal; the agent should be able to route to the right tool family from the prefix alone, before reading the description.

### 12.1 Prefix definitions

| Prefix | Role | Returns | Epistemic status |
|---|---|---|---|
| `get_` | Retrieve a known entity or derived structure by identity | The entity, graph, or structure — deterministic | Fact you asked for; trust completely |
| `scan_` | Sweep the codebase by a rule or pattern; locate all matching sites | Candidate list + reason string per site; may be empty | Rule-based locate; empty result is success, not failure |
| `analyze_` | Deep assessment of a specific named target | Opinion / risk judgment / verdict | Heuristic; false positives expected; surface for human review |
| *(mutations)* | Transform code | `BatchResultSummary` + `changeId` | — |

### 12.2 The test for each prefix

Apply in order. First match wins.

1. **`get_` test:** Does the caller supply an identity (symbol name, file path, method name) and expect that specific thing back? → `get_`. Examples: `get_symbol`, `get_type_hierarchy`, `get_method_source`, `get_relationship_graph`.

2. **`scan_` test:** Does the tool sweep the codebase (or a scoped subset) applying a deterministic or near-deterministic rule, returning all matching locations? Is an empty result a valid, expected outcome ("scanned, all clean")? → `scan_`. Examples: `scan_namespace_path_mismatches`, `scan_large_methods`, `scan_unawaited_fire_and_forget`, `scan_task_api_usage`, `scan_migration_candidates`.

3. **`analyze_` test:** Does the tool return a verdict, risk assessment, or heuristic judgment about a *specific named target* (method, type, file)? Are false positives expected? → `analyze_`. Examples: `analyze_stack_overflow_risks`, `analyze_exception_handling`, `analyze_structural_smells`.

**Structure vs. verdict fork:** Some tools return a derived structure (a control-flow graph, a call graph) rather than a judgment. Those are `get_`, not `analyze_`, even if the structure is complex. Test: does it return data the caller will reason over, or a conclusion the caller is meant to act on? Data → `get_`. Conclusion → `analyze_`.

### 12.3 Retired prefixes

| Retired prefix | Absorbed into | Rationale |
|---|---|---|
| `find_` | `scan_` (rule-based sweep) or `get_` (retrieve by identity) | `find_` implies the target exists and will be found; wrong for diagnostic sweeps that may return nothing |
| `detect_` | `scan_` (rule-based) or `analyze_` (heuristic) | `detect_` implies certainty ("detected!") for tools that are often heuristic; misleads the agent on trustworthiness |
| `analyze_` scoped to whole-codebase sweeps | `scan_` | `analyze_` is reserved for per-target deep review; codebase-wide sweeps belong to `scan_` |

### 12.4 The scan → analyze pipeline

`scan_` and `analyze_` describe two stages of one workflow, not competing synonyms:

```
scan_*      →  sweep codebase, emit candidate list  (cheap, wide, possibly empty)
    ↓ (agent triages candidates)
analyze_*   →  deep review of one specific target   (expensive, narrow, returns verdict)
```

A `scan_` result is input to agent triage, not a final answer. An `analyze_` result is a verdict on a target the agent (or human) already decided was worth investigating. Keeping this pipeline clean is why the two prefixes must not be used interchangeably.

### 12.5 Rename pass — scope and sequencing

All existing `find_*` and `detect_*` tools must be renamed in a single coordinated pass. Do this in the same release as the `_safe` → clean rename (§7, tools-review doc) to take the compatibility hit once.

During the transition window, keep old names as **thin aliases** that delegate to the renamed tool and emit a deprecation warning in the response. Remove aliases after one full migration cycle.

Checklist for each renamed tool:
- [ ] Apply the §12.2 prefix test; confirm the correct new prefix
- [ ] Update `[McpServerTool]` registration name
- [ ] Update `[Description(...)]` to lead with the new prefix semantics ("Sweeps the solution for..." vs. "Returns a risk assessment of...")
- [ ] Add old name as alias with deprecation warning
- [ ] Update any internal cross-references (health report integrations, batch tool calls)
- [ ] Update agent instruction files that reference the old name

<!-- v3 -->
