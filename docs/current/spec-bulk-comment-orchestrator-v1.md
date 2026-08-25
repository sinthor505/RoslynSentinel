# Plan: Server-orchestrated commenting via `BulkComment`

## Background

A test run had a weak local model (`qwen3.5-9b-coder` via LM Studio) drive a 3-level
nested plan ("for each project → for each file → for each member, comment it") entirely
from the primary agent's own context. It got through 1 of 10 projects (5 of 30 files,
and even the last file left half-done), then — per the transcript — hallucinated a
checklist that didn't exist in the plan, lost track of its own completed work (classic
truncate-middle symptom), redid verification passes on files it had already finished,
and finally declared the entire solution done. The failure was pure completion-tracking
collapse, not comment-quality — the comments it did write were fine.

The fix: move the tree-walk and progress-tracking into the server, where it's
mechanical and can't forget. The primary agent's job shrinks to "call a tool," repeated
a handful of times instead of hundreds. Only the leaf operation — generating comment
prose for one member — touches an LLM, and that LLM call is made *by the server
itself* (a new capability), against a locally-hosted model exposed over HTTP by LM
Studio (OpenAI-compatible endpoint).

Two mechanisms fell out of the design discussion and both generalize beyond commenting:
- A reusable **`ILlmClient`** in `RoslynSentinel.Common`, so any future tool that wants
  server-side LLM calls (not just commenting) can reuse it.
- A reusable **`[ContentHash(ContentHashPurpose, hash)]`** marker attribute, mirroring
  the existing `[MigrationCandidate(...)]` pattern, so any future tool that wants "has
  this member's content changed since I last processed it" can reuse the same
  mechanism under its own enum tag.

## Existing patterns being reused (not reinvented)

All found in `RoslynSentinel.Advanced\AsyncOptimizationEngine.cs`,
`AsyncBatchEngine.cs`, and `RoslynSentinel.Server.Advanced\SentinelAsyncifyTools.cs`,
which already solve "walk the solution scoped by `ToolScope`, tag members with a
custom attribute, batch-apply edits safely":

- **Single-tool scope dispatch** — `ScanAsyncMigrationCandidates`
  (`SentinelAsyncifyTools.cs:101`) takes one `ToolScope scope = ToolScope.solution`
  parameter (`RoslynSentinel.Common\ToolEnums.cs:13`,
  `enum ToolScope { file, project, solution }`) plus `projectName`/`filePath` filters,
  and branches internally (`scope == ToolScope.project ? projectName : null`, etc.) —
  **exactly** the single-tool-with-scope-param shape `BulkComment` will follow, rather
  than three separate tools.
- **Enum-typed tag, not a free string** — `AsyncMigrationPattern`
  (`ToolEnums.cs:72`, `enum AsyncMigrationPattern { AsyncBridgeCandidate,
  HandlerExtractCandidate, HandlerToAsyncCandidate, AsyncCallerUpliftCandidate }`) is
  the existing precedent for constraining an attribute's tag value to a fixed set
  instead of an arbitrary string — `ContentHashPurpose` mirrors this.
- **Attribute source injection** — `BuildMigrationCandidateAttributeSource`
  (`AsyncOptimizationEngine.cs:1683`) returns a hardcoded C# string for a
  global-namespace `internal sealed class ...Attribute : Attribute`; injection-need
  check is a solution-wide filename scan (`d.FilePath == ".../XAttribute.cs"`), target
  path is `Path.Combine(projectDir, "XAttribute.cs")` relying on SDK glob-include.
  Purely syntax-level, no compilation needed. `[ContentHashAttribute]` injection
  mirrors this exactly (own file, own build helper).
- **Flag-if-different logic** — `FlagMigrationCandidateAsync`
  (`AsyncOptimizationEngine.cs:1793`): find the method node, scan `AttributeLists` for
  an existing matching attribute, build a new `AttributeSyntax` via
  `SyntaxFactory.Attribute`, use the shared `ReplaceOrAddAttribute` helper (line 2852)
  to swap it in, apply via `root.ReplaceNode` + `NormalizeWhitespace().ToFullString()`.
  `SetContentHashAsync` mirrors this.
- **Query/read-back** — `FindMigrationCandidatesAsync`
  (`AsyncOptimizationEngine.cs:2120`): syntax-level only (no semantic model),
  `Parallel.ForEachAsync` (MaxDegreeOfParallelism=2) over projects/documents, walks
  declaration nodes, matches attributes by short/full name, extracts constructor/named
  args. `FindStaleContentAsync` mirrors this shape.
- **Batch-apply safety** — every batch-mutating core method starts with
  `_workspaceManager.CheckBreaker()` (returns early if tripped) and ends with
  `RecordBatchOutcome(succeeded, failed, rolledBack, skipped)`
  (`ICircuitBreaker`, implemented by `PersistentWorkspaceManager`). Per-item failures
  don't abort the batch — they get parked and the loop continues. `BulkComment` reuses
  `ICircuitBreaker` the same way, and reuses `ApplyProposedChangesAsync` for writes
  (retry/validate/rollback already built in).
- **Comment application** — `AddSummaryCommentAsync` / `GetSummaryCommentAsync`
  (`RoslynSentinel.Basic\RefactoringEngine.cs:3056`/`3251`) already do the actual
  "insert or update an `/// <summary>` doc comment on this member" work, including
  staleness detection. The orchestrator calls these directly (in-process, same as any
  engine-to-engine call) rather than reimplementing comment insertion.
- **MCP tool wrapper shape** — `[McpServerToolType]` class,
  `[McpServerTool(Name = "PascalName")]` + triple-quoted `[Description]`, params end
  with `RequestContext<CallToolRequestParams>? requestParams, CancellationToken
  cancellationToken`, return `Task<ToolResult<T>>`
  (`RoslynSentinel.Common\ToolResult.cs`), errors via
  `ToolErrorMapper.ToResultError(ex, _workspaceManager, "ToolName")`. `dryRun` is a
  plain input field that, when true, skips `ApplyProposedChangesAsync` and marks items
  `Outcome = ItemRecordOutcome.Skipped, Reason = "dry_run"`.

## New pieces

### 1. `ILlmClient` (new, `RoslynSentinel.Common`)

```csharp
public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt,
        int maxTokens, CancellationToken cancellationToken = default);
}
```

Concrete implementation `LmStudioClient` (also `RoslynSentinel.Common`) talks to a
locally-hosted LM Studio server over HTTP using its OpenAI-compatible
`/v1/chat/completions` endpoint (simplest, best-supported surface — no need for the
Anthropic-compatible endpoint since this is a single-turn, no-tool-use completion).
Registered via `IHttpClientFactory` (`services.AddHttpClient<LmStudioClient>(...)`) in
`ServiceRegistrationExtensionsAdvanced.cs`, base URL / model name / timeout sourced
from environment variables (no existing config-file plumbing in the server to build
on — env vars match how the rest of the server is configured today):
- `ROSLYNSENTINEL_LLM_BASE_URL` (default `http://localhost:1234/v1`)
- `ROSLYNSENTINEL_LLM_MODEL` (required — LM Studio needs the loaded model's name)
- `ROSLYNSENTINEL_LLM_TIMEOUT_SECONDS` (default 30)

Living in `.Common` (not `.Advanced`) answers the "should this be reusable" question:
any future engine can take `ILlmClient` as a constructor dependency the same way
engines already take `ISolutionProvider`.

### 2. `ContentHashAttribute` + `ContentHashPurpose` enum (new, mirrors `MigrationCandidateAttribute`)

`RoslynSentinel.Common\ToolEnums.cs` gains:

```csharp
public enum ContentHashPurpose { Comment }
```

(single member today; a future feature adds its own value here rather than inventing
a parallel mechanism — this is the whole point of generalizing the attribute now).

`RoslynSentinel.Common\ContentHashAttribute.cs` (new file):

```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor
    | AttributeTargets.Property | AttributeTargets.Enum,
    AllowMultiple = true, Inherited = false)]
internal sealed class ContentHashAttribute : Attribute
{
    public ContentHashAttribute(string purpose, string hash)
    { Purpose = purpose; Hash = hash; }
    public string Purpose { get; }   // stored as string in the generated attribute
                                      // source (attributes can't take enum args from a
                                      // separate assembly cleanly across the injected-
                                      // file boundary); engine code parses/formats via
                                      // ContentHashPurpose.ToString() /  Enum.Parse, so
                                      // the enum is still the single source of truth
                                      // for valid values — free-text only at the
                                      // syntax-text layer, same as MigrationCandidate's
                                      // Pattern.
    public string Hash { get; }
}
```

- `AllowMultiple = true` + the `Purpose` enum-backed tag is what makes this reusable
  beyond commenting — a future consumer adds `[ContentHash("SomethingElse", "...")]`
  under its own `ContentHashPurpose` value alongside without collision.
- Sentinel value `"00000000"` means "seeded, never processed" — written by a bulk seed
  pass (see below), never a real SHA output, so `hash != ComputeHash(member)` is always
  true for it and no separate magic value is needed for "went stale after edit."
- Hash = truncated SHA-256 (matching the codebase's existing `[..8]`/`[..12]` short-hex
  convention seen elsewhere, e.g. `loopRunId`) of the member's `NormalizeWhitespace()`
  text — cheap, deterministic, no compilation needed. Lives as a static helper
  `ContentHasher.ComputeHash(SyntaxNode member)` in `RoslynSentinel.Common` so any
  future consumer computes it identically rather than reimplementing.
- No member name stored in the attribute (discussed and deliberately rejected —
  overloads collide, renames desync, copy-paste carries a stale name+hash together).
  Linkage to "which member" comes from syntactic attachment, which is always correct.

Source-injection, flag-if-different, and query mirror the `MigrationCandidate`
mechanics 1:1 (see "Existing patterns" above) — new file
`RoslynSentinel.Common\ContentHashAttribute.cs` for the type, and engine methods
`SetContentHashAsync` / `FindContentHashAsync` (placed in
`RoslynSentinel.Advanced\CommentingEngine.cs`, new file — see below) built the same
way `FlagMigrationCandidateAsync` / `FindMigrationCandidatesAsync` are built, taking
`ContentHashPurpose purpose` typed parameters (converted to/from string only at the
syntax-text boundary).

### 3. `CommentingEngine` (new, `RoslynSentinel.Advanced`)

One engine, two phases, driven by a single core method taking `ToolScope scope` —
mirrors how `ScanAsyncMigrationCandidates` branches internally on scope rather than
having three call paths.

**Phase 1 — seed pass** (`SeedContentHashesAsync(scope, projectName, filePath)`):
purely mechanical, no LLM. Walk every member in scope (method, constructor, property,
enum); if it has no `[ContentHash("Comment", ...)]` at all, stamp
`[ContentHash("Comment", "00000000")]`. Batched per-file like
`FlagMultipleMigrationCandidatesAsync` does (one evolving `root` per file, one
`ApplyProposedChangesAsync` per file) to avoid line-drift and keep this safe to run
solution-wide in one shot. This is also what makes progress a queryable fact rather
than a claim: after seeding, `FindContentHashAsync(purpose: ContentHashPurpose.Comment,
staleOnly: true)` returns an exact count of remaining work, independent of any
transcript.

**Phase 2 — work pass** (`CommentCore(scope, projectName, filePath, options)`): for
each member where `FindContentHashAsync` says the hash doesn't match current content:
1. Get member body text (reuse existing outline/source retrieval — same calls
   `GetFileOutline`/method-source tools already make).
2. Call `ILlmClient.CompleteAsync` with a fixed system prompt ("write a single-line
   XML summary comment describing what this code does") and the member's source as the
   user prompt.
3. Apply via `RefactoringEngine.AddSummaryCommentAsync` (existing, reused as-is).
4. Update `[ContentHash("Comment", newHash)]` via `SetContentHashAsync` in the same
   edit pass as the comment insertion (single `ApplyProposedChangesAsync` per member,
   so comment and hash never desync).
5. `CheckBreaker()` / `RecordBatchOutcome` around the batch, same as Asyncify — a
   member whose LLM call or apply fails gets recorded as skipped with a reason and the
   loop continues, it doesn't abort the run.

Guardrails mirroring `AsyncifyLoop`: `maxMembers` (default e.g. 200 per call, so one
solution-scoped call over 1800 members doesn't run unbounded — caller can re-invoke,
and thanks to the hash check it resumes for free), `maxRuntimeSeconds`, `dryRun` (skips
LLM+apply, returns the planned work count only).

**Result type** `RoslynSentinel.Common\CommentingResult.cs` (new record): total
members in scope, already-current count, seeded count, commented count this call,
skipped (with reasons), per-file breakdown — same shape philosophy as
`BatchResultSummary`/`FlagMigrationCandidateResult`. This return value is the
completion signal — not agent prose.

### 4. MCP tool — single `BulkComment` (new, `RoslynSentinel.Server.Advanced\SentinelCommentingTools.cs`)

One tool, `ToolScope`-dispatched, mirroring `ScanAsyncMigrationCandidates`'s shape
exactly rather than three separate tools:

```csharp
[McpServerTool(Name = "BulkComment")]
[Description("""
    Adds or refreshes /// <summary> doc comments across a solution, project, or file,
    tracking per-member completion via [ContentHash] so repeated calls resume for free
    and never re-process unchanged members.

    scope: solution (default) | project | file.
      project — restrict to one project; projectName required.
      file    — restrict to a single file; filePath required.
    dryRun: report planned work without calling the LLM or writing any changes.
    maxMembers: cap on members processed in this call (default 200) — re-invoke to
      continue; already-commented members are skipped automatically on the next call.

    Returns CommentingResult: counts of already-current / seeded / commented-this-call /
    skipped members, per-file breakdown. This return value is the authoritative
    completion signal for the run.
    """)]
public async Task<ToolResult<CommentingResult>> BulkComment(
    ToolScope scope = ToolScope.solution,
    string? projectName = null,
    string? filePath = null,
    bool dryRun = false,
    int maxMembers = 200,
    int maxRuntimeSeconds = 0,
    RequestContext<CallToolRequestParams>? requestParams = null,
    CancellationToken cancellationToken = default)
```

Body: validate solution loaded → validate scope's required filter present
(`project` needs `projectName`, `file` needs `filePath`, same style as
`ScanAsyncMigrationCandidates`'s `scopedProjectName`/`scopedFilePath` derivation) →
call `CommentingEngine`'s seed phase then work phase for that scope → wrap result in
`ToolResult<CommentingResult>` → `ToolErrorMapper.ToResultError` on exception.

## Files touched

- New: `RoslynSentinel.Common\ILlmClient.cs`, `LmStudioClient.cs`,
  `ContentHashAttribute.cs`, `ContentHasher.cs`, `CommentingResult.cs`
- Modified: `RoslynSentinel.Common\ToolEnums.cs` (add `ContentHashPurpose` enum)
- New: `RoslynSentinel.Advanced\CommentingEngine.cs`
- New: `RoslynSentinel.Server.Advanced\SentinelCommentingTools.cs` (single
  `BulkComment` tool)
- Modified: `RoslynSentinel.Server.Advanced\ServiceRegistrationExtensionsAdvanced.cs`
  (register `IHttpClientFactory`-backed `LmStudioClient` as `ILlmClient`, register
  `CommentingEngine`)
- Reused as-is (no changes needed): `RefactoringEngine.AddSummaryCommentAsync` /
  `GetSummaryCommentAsync`, `ICircuitBreaker`, `ApplyProposedChangesAsync`,
  `ReplaceOrAddAttribute`-style helper (copy the pattern, or extract it to a shared
  helper if reuse across two attribute kinds makes that worthwhile at implementation
  time)

## Verification

1. Build (`Build` MCP tool, `fullBuild`) — 0 errors.
2. Point `ROSLYNSENTINEL_LLM_BASE_URL`/`ROSLYNSENTINEL_LLM_MODEL` at a running LM
   Studio instance; call `BulkComment(scope: file, filePath: ..., dryRun: true)`
   against a small test file — confirm it reports the right member count with zero
   edits applied.
3. Call `BulkComment(scope: file, ...)` for real on a small engine file in the
   `RoslynSentinel-AgentCommentingTest` copy — confirm every member gets both a
   `/// <summary>` and a `[ContentHash("Comment", ...)]`, build stays green.
4. Re-call with no changes — confirm it reports 0 newly-commented (idempotency: hash
   matches, everything skipped).
5. Hand-edit one member's body, re-call — confirm only that member gets
   re-commented and its hash updated, others untouched.
6. Call `BulkComment(scope: project, projectName: "RoslynSentinel.Advanced")` (the
   directory the original failed test targeted) — confirm the returned
   `CommentingResult` count matches reality by spot-checking file diffs, and that it
   completes without needing the primary agent to track any per-file state itself.
