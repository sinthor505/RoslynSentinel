# Plan: solution-relative paths on the wire via one central response filter (stage 1 of proposal_solution_relative_paths)

**Status:** DRAFT 2026-10-08. Implements only "Part 2" (the JSON-aware backstop filter) of `docs/current/proposal_solution_relative_paths.md`, with measured savings; typed retyping (Part 1) and per-file grouping are deliberately not in this plan.

## Problem

Every path in every tool response is absolute and JSON-escaped (`C:\\Users\\Administrator\\source\\repos\\RoslynSentinel\\`
is 57 bytes on the wire, about 15-18 tokens, repeated on every row). The existing proposal
(`docs/current/proposal_solution_relative_paths.md`, 2026-10-01, "Proposed, not started") already
argues the case and designs a two-part fix. This plan adds the measurement the proposal lacks,
confirms the choke point against source, and cuts out the decidable first slice.

### Measured win (real responses, not estimates of shape)

Sample: the 307 path-bearing responses in `.roslynsentinel/largeresults/` (all over the 15 KB
offload threshold, so this is the worst-case/heavy end), 40.5 MB, 101,389 path rows.

| Variant | Bytes saved | Share of total |
| --- | --- | --- |
| (a) strip the solution root, keep `\` | 5.78 MB | 14.3% |
| (a) plus `/` separators | 5.88 MB | 14.5% |
| (b) group rows by path (dedup the remaining relative path), on top of (a) | +5.51 MB | +13.6% |
| (a) + (b) | 11.39 MB | 28.1% |

- Path bytes are 27.9% of all bytes in that sample; there are 7.1 rows per distinct path.
- By response size: 0-30 KB (161 files) (a) 6.6%, (a)+(b) 11.4%; 30-100 KB (95 files) (a) 10.4%, (a)+(b) 19.0%; over 100 KB (51 files) (a) 15.7%, (a)+(b) 31.3%.
- Token estimate (no tokenizer available here, so this is bytes/3.2-4): (a) is roughly 1.4-1.8 million tokens over those 307 responses, about 4.5-6k tokens per offloaded response. The long `\\` runs tokenize worse than plain text, so the token share is probably at or above the byte share (hypothesis, not measured).
- Small inline responses (not measured byte-exact; counted from a live probe on 2026-10-08): a `FindReferences(callers)` with 12 caller rows carried 16 absolute paths, so about 900 bytes (~230-290 tokens) of pure prefix out of an estimated 4-5 KB response, i.e. roughly 18-20%. `Git(status)` already returns repo-relative `/` paths, so it gains nothing.
- `/` separators add only 0.1-0.2% over stripping the root; they are worth taking only because they need no JSON escaping and the input side already accepts them (probe below).
- Reading (b): grouping adds about as much as (a) again, but only on heavy listings (over 100 KB responses), and it changes the row shape from flat to nested. See "Risks and open decisions".

### Confirmed against source / live probes

- Choke point exists: every response ends as JSON text in the first `TextContentBlock`, and the request-filter chain is shared by both servers. `AddRoslynSentinelToolsBasic` registers the filters (`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs:99-109`: echo, argument validation, error catch-all, not-loaded, drift, `AddLargeResultOffloadFilter` (line 530), orientation breaker, unrecoverable breaker); `ServiceRegistrationExtensionsAdvanced.cs:172` calls the same method, so Advanced is covered with no extra work. The first filter registered is the outermost, so a filter registered immediately after the offload filter runs inside it and its output is what the 15 KB threshold measures and what the raw offload file stores.
- A converter alone cannot cover everything: `FilePathWrapper` writes `Absolute` with no root (`RoslynSentinel.Common/FilePathWrapper.cs`, `FilePathJsonConverter.Write`), and result DTOs mix `FilePathWrapper` (about 496 hits across 102 files for wrapper-typed members, mostly engines and parameters) with plain `string FilePath` (279 hits across 84 files for `string FilePath|Path|File|...` declarations). `WithSentinelTools` passes no serializer options (`RoslynSentinel.Common/McpToolSchemaPatcher.cs:133,155,177,189,263`, call sites in `ServiceRegistrationExtensionsBasic.cs` pass none), so a global converter would also change input-schema generation (the proposal's manifest-diff warning). A post-serialization filter has neither problem.
- `ToolCallEcho.StampText` (`RoslynSentinel.Server.Basic/ToolCallEcho.cs:98`) already parses the final text into a `JsonNode` and re-serializes with `SharedJsonOptions.Compact`, so parse-rewrite-serialize at filter level is an established pattern. The echo filter is outermost and runs after ours, so echoed `arguments` stay exactly what the caller sent.
- Root: `PersistentWorkspaceManager.GetSolutionRoot()` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:969`) = directory of the loaded solution file, no trailing separator, `null` when nothing is loaded. The offload filter already resolves it the same way (`ServiceRegistrationExtensionsBasic.cs`, `workspaceManager?.GetSolutionRoot()`).
- `UseStructuredContent = false` on the tools that declare `OutputSchemaType` (for example `RefactoringSignatureTools.cs:15`, `SymbolNavigationTools.cs:15`), and `ToolCallEcho.Stamp` never touches `StructuredContent`: only the text block needs rewriting.
- Input side, probed live on 2026-10-08 against the stale-but-sufficient running binary: a root-relative path with `\` works in `GetFileOutline`, `GetMethodSource`, `Search(mode: symbol, filePath)` and `FindReferences(filePath)`; a `/` relative path works in `ReadFile`; a lowercased drive/folder absolute path works in `ReadFile` (and the response echoes the caller's casing, so output-side matching of the root must be case-insensitive). `DocumentLookup` (`RoslynSentinel.Common/DocumentLookup.cs`) is the shared resolver and even accepts a bare file name. Not probed (no MCP access in this run): the mutators. Candidates with a `[Consumes(DataTag.SourceFilepath)]` parameter and no `ResolveFromWire` call in their own tool or impl file: `DeclarationTools.cs:41`, `ParameterEditTools.cs:36`, `WorkspaceProjectManagementTools.cs:88` (`SafeDeleteUnusedSymbol`), `WorkspaceBuildTestTools.cs:21` (`GetDiagnostics`). They may resolve through `DocumentLookup` or an engine; Step 5 tests it instead of assuming.
- Consumers of output paths: no hook reads tool output paths. `.claude/hooks/enforce-dogfood.ps1` reads tool *input* (`$toolInput.file_path` line 167, `$toolInput.filePath` line 318); `scripts/Get-JournalDigest.ps1` parses journal text; `Get-UnknownParameterReport.ps1` has no `successData`/`filePath` match; `roslynsentinel-interrogate.ps1` matched a broad pattern and was not read (hypothesis: it only reads transcripts). The live-pipeline test projects are the real consumers: `RoslynSentinel.Tests.Server` calls tools through the filter chain (`live.CallAsync`); tests that call tool classes directly are unaffected. The one assertion found that reads a response path (`ToolCallEchoFilterTests.cs:247`) uses `Does.Contain("ToolCallEchoProbe.cs")` and survives relativization.

## Decision

One new call-tool filter, registered immediately after `AddLargeResultOffloadFilter`, parses the first-level JSON text block, rewrites path strings under the solution root to root-relative `/` form, and stamps `solutionRoot` once. Flat rows stay flat (weak models read flat rows more reliably; see decision 1). Rules, adopted from the proposal's Part 2 and tightened:

1. Root = `GetSolutionRoot()`, trailing separators trimmed. Match is `OrdinalIgnoreCase` and needs a `\` or `/` right after the root (so `...\RoslynSentinelX\` is not under `...\RoslynSentinel`). Not loaded, or path outside the root (csharp-sdk clone, other worktrees): left absolute. Never emit `..\`.
2. Whole-value rule: any string value, and any object key, that is entirely a path under the root and contains no CR/LF becomes `rel/path.cs`.
3. Substring rule: inside string values whose nearest property name is one of `statusMessage`, `message`, `detail`, `warningDetails`, each occurrence of `<root>\` or `<root>/` is removed.
4. Verbatim keys are never rewritten as values (still rewritten as keys): `source`, `preview`, `contextSnippet`, `codeSnippet`, `lineText`, `text`, `content`, `diff`, `oldContent`, `newContent`, `newText`, `changedContent`, `beforeContent`, `afterContent`. A model copies these into `ReplaceSnippet.oldContent`; one changed character is a match failure.
5. Keep-absolute keys/subtrees (the value is later fed back to something that does not resolve against the solution root, or points outside the workspace): `solutionPath`, `solutionRoot`, `serverBinaryPath`, `binaryPath`, `baseRepoDir`, and the whole `largeResult` subtree (the offload file path is also read by harness-side tools whose cwd may not be the root).
6. If at least one rewrite happened, insert `"solutionRoot": "<root>"` as the first property of the root object (the echo filter then puts `toolCall` before it). A response with no rewrite is returned byte-for-byte unchanged. A text block that does not start with `{` is left alone.
7. Toggle: env var `ROSLYNSENTINEL_RELATIVE_PATHS`; default ON; `0`, `false` or `off` disables. Env var only (the four-entry-point `Configure(args)` wiring from the proposal is not worth it for a kill switch; see decision 2).

The rewriter is a pure function in Common so it is unit-testable without a server.

## Execution rules

- Layering: the rewriter and options go in `RoslynSentinel.Common` (no MCP types; `System.Text.Json.Nodes` only). The filter goes in `RoslynSentinel.Server.Basic`. Do not add anything to `Server.Advanced`: it reuses `AddRoslynSentinelToolsBasic`.
- Do not touch `Search` source files or `plan_search_kind_mode_query_filter.md`; another agent is editing them. This plan changes no tool and no DTO.
- A new startup option is not needed (env var only), so none of the four `Configure(args)` entry points change.
- Compare `RunTest` against the known-failure baseline (memory `reference_known_failing_tests`); report only new failures.
- ASCII-only in all code comments and messages.

## Steps

### Step 1 - Add `WirePathOptions` and `WirePathRewriter` (pure, no callers yet)
- Files: `RoslynSentinel.Common/WirePathOptions.cs` (new), `RoslynSentinel.Common/WirePathRewriter.cs` (new).
- Change:
  - `public static class WirePathOptions { public const string EnvVarName = "ROSLYNSENTINEL_RELATIVE_PATHS"; public static bool Enabled { get; } }` reading the env var on each access (so tests can set it); anything other than `0`/`false`/`off` (case-insensitive) is enabled.
  - `public static class WirePathRewriter` with:
    - `public static bool TryRelativize(string value, string root, out string relative)`: whole-value rule 1+2 (root trimmed of trailing `\` and `/`, `OrdinalIgnoreCase`, separator required after the root, no CR/LF, result uses `/`).
    - `public static string RelativizeInText(string text, string root, out int replacements)`: the substring rule (remove each case-insensitive `<root>\` and `<root>/`).
    - `public static string? RewriteJsonText(string text, string root, out int rewrites)`: returns `null` when `text` is not a JSON object (does not start with `{` after trim, or does not parse) or when nothing was rewritten; otherwise parses to `JsonNode`, builds a NEW tree applying rules 2-6 (a recursive private `Rewrite(JsonNode? node, string? parentKey, bool verbatim, bool keepAbsolute, ...)` that returns a new node; object keys are run through `TryRelativize` too; arrays inherit the parent property name), inserts `solutionRoot` at index 0 of the root object, and serializes with `SharedJsonOptions.Compact`.
  - Expose the key sets as `public static readonly IReadOnlySet<string>` named `MessageKeys`, `VerbatimKeys`, `KeepAbsoluteKeys` (OrdinalIgnoreCase), so a later step can extend them in one place.
  - No call sites to update (new types).
- Done when: `Build` reports 0 errors.

### Step 2 - Unit tests for the rewriter
- Files: `RoslynSentinel.Tests/WirePathRewriterTests.cs` (new). (`RoslynSentinel.Tests` already hosts `FilePathFromWireTests.cs` for Common types.)
- Change: NUnit tests, one per behaviour, using root `C:\repo\Sol` and literal JSON strings:
  1. `TryRelativize_UnderRoot_ReturnsForwardSlashRelative` (also lowercase drive letter and mixed `/` input).
  2. `TryRelativize_SiblingPrefix_NotRewritten` (`C:\repo\Sol2\a.cs`).
  3. `TryRelativize_OutsideRoot_NotRewritten`, `TryRelativize_ExactRoot_NotRewritten`, `TryRelativize_MultilineValue_NotRewritten`.
  4. `RewriteJsonText_WholeValueAndKey_Rewritten_AndSolutionRootStampedFirst` (a `successData` array of rows plus a `listSummary.byFile` row; also a `changedContent` object whose KEY is a path and whose VALUE is file text that is a single line starting with the root: key rewritten, value untouched).
  5. `RewriteJsonText_VerbatimKeys_NotRewritten` (`source`, `preview`, `contextSnippet` set to a one-line string that starts with the root).
  6. `RewriteJsonText_KeepAbsoluteKeys_NotRewritten` (`solutionPath`, `largeResult.filePath`).
  7. `RewriteJsonText_StatusMessage_SubstringRewritten`.
  8. `RewriteJsonText_NothingToRewrite_ReturnsNull`; `RewriteJsonText_NotJson_ReturnsNull`; `RewriteJsonText_OutputKeepsAngleBracketsUnescaped` (a `List<int>` string survives unescaped, proving `SharedJsonOptions.Compact` is used).
- Done when: `RunTest` on `WirePathRewriterTests` passes (all tests green).

### Step 3 - Register the filter
- Files: `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`.
- Change: add `private static void AddWirePathRelativizeFilter(IMcpRequestFilterBuilder filters)` next to `AddLargeResultOffloadFilter` (line 530), modelled on it: `var result = await next(...)`; return unchanged when `!WirePathOptions.Enabled`; resolve `context.Server.Services?.GetService<PersistentWorkspaceManager>()?.GetSolutionRoot()`, return unchanged when null; for each `TextContentBlock` whose `Text` starts with `{`, call `WirePathRewriter.RewriteJsonText(block.Text, root, out var n)` and, when non-null, assign `block.Text` and `logger?.LogDebug("Wire path rewrite: tool '{Tool}' rewrote {Count} value(s)", ...)` (this log is the future worklist for retyping). Wrap in `try/catch (Exception ex) { Debug.WriteLine(...) }`: a diagnostic aid must never break a call. Then in `AddRoslynSentinelToolsBasic` (line 99-109) insert `AddWirePathRelativizeFilter(filters);` on the line directly after `AddLargeResultOffloadFilter(filters);` (it must be after, not before: first registered = outermost, and this filter must run inside the offload filter).
- Call sites: one (`AddRoslynSentinelToolsBasic`, line 106 neighbourhood). Definition and call land in the same edit (one `ReplaceSnippet` batch, definition first).
- Done when: `Build` reports 0 errors.

### Step 4 - Live-pipeline regression test
- Files: `RoslynSentinel.Tests.Server/WirePathRelativizeFilterTests.cs` (new).
- Change: model it on `ToolCallEchoFilterTests.cs` (same fixture and `live.CallAsync` helper, `_fixture.SolutionDirectory`; read its `SetUp` at line 52 for the probe-file setup). Tests:
  1. `Search` (mode `symbol`) for the probe class: the response JSON has `solutionRoot` equal to the fixture solution directory, and every `filePath` under it is relative with `/`.
  2. Round trip: pass that relative `filePath` to `ReadFile`; the call succeeds and returns the probe file's text.
  3. `largeResult.filePath` and `solutionPath` (from `LoadSolution`/status if exercised) stay absolute.
  4. With env var `ROSLYNSENTINEL_RELATIVE_PATHS=0` the same call returns the absolute path and no `solutionRoot` (set and restore the env var in a `try/finally`; mark the test `[NonParallelizable]`).
  5. An offloaded large result (reuse the large-list setup in `OffloadedLargeResult_StubRelaysListSummary_AndStatusMessageHasNoFilePaths`, around line 230-247): the raw file written under `.roslynsentinel/largeresults/` contains no occurrence of the escaped root prefix.
- Done when: `RunTest` on `WirePathRelativizeFilterTests` passes.

### Step 5 - Input-side audit test (report, do not fix here)
- Files: `RoslynSentinel.Tests.Server/RelativePathInputAuditTests.cs` (new).
- Change: a table-driven test that calls each of `GetFileOutline`, `GetMethodSource`, `ReadFile`, `Declaration` (read-only operation), `ParameterEdit` (dry run), `SafeDeleteUnusedSymbol` (dry run), `GetDiagnostics` (file scope), `Member` (operation `view`), `ReplaceSnippet` (dry run, single edit AND `batchEdits[].filePath`) with the fixture's probe file given as a root-relative `/` path, and asserts the call is not a `NotFound`/`InvalidArgument` error. Parameter names and required companions must be copied from each tool's real schema (`McpServerStatus(toolListing: all)` or the tool source); if a tool cannot be driven in a few lines, drop it from the table and list it in the commit message. A failing row is a defect finding to write up in `docs/current/findings/`, not something to fix in this step.
- Done when: `RunTest` on `RelativePathInputAuditTests` passes, or fails only on rows whose tool is named in a new finding doc (list the rows in the step report).

### Step 6 - Verification
- Files: none.
- Change: `Build` (0 errors). `RunTest` at solution scope and compare with the known-failure baseline (new failures only; expect any `Tests.Server` test that asserts an absolute response path to surface here, fix by relaxing the assertion to the file name or by `Path.GetFullPath(Path.Combine(solutionDirectory, relative))`). Then `McpServerControl` stop (`operation: StopServer`, `confirmServerStop: ConfirmServerStop`), reconnect, `LoadSolution`, and run live: `Search(mode: symbol, query: "SharedJsonOptions")`, `FindReferences`, one large `Search(mode: text)` that offloads, then `GetLargeResult` for it, and a `ReadFile` using a path copied from the output. Record before/after byte counts of the `FindReferences` call in the step report.
- Done when: Build 0 errors, no new test failures vs baseline, and the live round trip works on the restarted server.

## Out of scope

- Part 1 of the proposal (retyping output `string` path fields to `FilePathWrapper` plus a wire-only converter) and its reflection guard test.
- Row grouping/dedup by path (idea (b)); see decision 1.
- Rewording path parameter descriptions ("solution-relative or absolute") and a `LoadSolution`/`McpServerStatus` sentence that states the root; both are cheap follow-ups once the filter ships, but they touch many descriptions and the schema manifest.
- Changing `FilePathWrapper`, `FilePathJsonConverter`, `SharedJsonOptions` or any persisted artifact (operation blobs, ledger, typed offload files keep absolute paths: the filter only sees the final response text).
- Rewriting responses the outer filters build themselves (argument-validation rejections, exception catch-all): they are produced outside this filter in the chain. Breaker refusals pass through it.
- Any change to `Search` code (cap/filter work belongs to the other plan).
- Edits to `proposal_solution_relative_paths.md`. Proposed follow-up edit for the human to apply: set its Status to "Part 2 planned in plan_wire_relative_paths_backstop_filter.md 2026-10-08" and add the measurement table above.

## Risks and open decisions

1. **Flat rows or grouped rows (user idea (b))?** Measured: grouping adds +13.6% of total bytes on top of (a)'s 14.3%, but nearly all of it comes from responses over 100 KB (31.3% combined there vs 15.7% for (a) alone) and it needs a new nested shape per list tool (references, text hits, outlines), with per-tool schema changes and weak-model parsing risk. Recommendation: ship (a) now; revisit (b) only for the heaviest list tools and only for tools whose rows repeat the same file many times (`Search` text, `FindReferences`). Decision for the user.
2. **Toggle default and plumbing.** This plan defaults ON with an env-var kill switch only. The proposal wants `--relative-paths` through all four entry points and a possible stdio-claude-only rollout. Tradeoff: env-var-only is one file, but not discoverable via `--list-tools` style startup help. Decision for the user.
3. **Which response paths must stay absolute?** The keep-absolute list (`largeResult` subtree, `solutionPath`, `binaryPath`, ...) is my judgement from source; a harness-side consumer that opens `largeResult.filePath` with its own file tool is the reason (hypothesis: the harness cwd equals the solution root in this repo only). Confirm with the user which fields external tools consume.
4. **Message-key allowlist is a first cut.** `Build` and `RunTest` compiler lines (`<abs path>(line,col): error CS...`) live in some result field I did not trace (not read in this run). Until they are added to `MessageKeys` they stay absolute, which is the safe direction. Extend the set after reading the real `Build`/`RunTest` result DTOs; a follow-up one-line edit.
5. **Worktrees and several solutions.** The root is whatever solution is currently loaded and is stamped on each rewritten response, so a response is self-describing; after a `LoadSolution` to another worktree, paths are relative to the new root. A model that keeps a relative path across a reload will resolve it against the new root (same layout in a worktree, so usually the same file). Not tested with two roots; verify during Step 6.
6. **Models that pass a relative path to a non-MCP tool** (built-in `Read`, a shell) resolve it against that tool's cwd, not the solution root. The `solutionRoot` property is the mitigation; no environment lever beyond that. CLAUDE.md already forbids those tools on `.cs` files.
7. **Per-response cost.** Each response gains one extra `JsonNode.Parse` and re-serialize (the echo filter already does one). Only responses that contain a path pay the re-serialize. Not benchmarked.
8. **Untraced:** exact shape of every tool's output field names (I used a measurement over saved responses and a representative sample, not a per-DTO inventory); the proposal's "Phase 0 inventory" would produce that. The mutator tools' relative-path input support is untested until Step 5.
9. **Related defect seen in the same probe, not planned:** a `FindReferences` response carries both `successData.summary` and a top-level `listSummary` with the same `byFile` list (observed live 2026-10-08, source not traced). Removing the duplicate saves a few hundred bytes on every list response. Left as a candidate for the next run.
