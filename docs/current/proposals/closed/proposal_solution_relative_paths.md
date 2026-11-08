# Solution-relative paths in tool output: typed migration with a wire-level backstop

## Motivation

Every path RoslynSentinel returns is absolute. On this machine the solution-root prefix alone,
`c:\\Users\\Administrator\\source\\repos\\RoslynSentinel\\` as it appears JSON-escaped on the wire,
is about 57 characters, and every further separator inside the path costs 2 characters (`\\`).
A path-heavy response repeats that prefix many times. For example, a `Search(mode: text)` for
`StoreRawJsonAsync` (9 matches, 3 files) carried 15 path occurrences, because each file appears in
the match row, in `statusMessage` and in `listSummary.byFile`. That is about 850 characters out of
roughly 5,000, all of it redundant once the caller knows the root.

This is a direct cost to the repo's mission. Small local models have small context windows, and a
redundant prefix on every path in every response is context that cannot be spent on code.

The **input** side already handles relative paths. `FilePathWrapper.FromWire`
(`RoslynSentinel.Common/FilePathWrapper.cs:59`) resolves a relative path argument against the
loaded solution root, and relative paths work today for `ReadFile`, `GetFileOutline`,
`GetMethodSource` and others. The gap is entirely on the **output** side.

## Current state

### `FilePathWrapper` has a `Relative` property, but nothing emits it

- `Relative` exists (`FilePathWrapper.cs:35`) but is only populated when the constructor receives a
  `solutionRoot` (`:40`). The implicit `string -> FilePathWrapper` conversion and the JSON
  converter's `Read`/`ReadAsPropertyName` (`:181-205`) never pass one, so `Relative` is usually
  empty.
- `FilePathJsonConverter.Write`/`WriteAsPropertyName` always emit `ToString()`, which is
  `Absolute` (`:192-193`, `:207-208`).
- The converter's `Read` builds a wrapper with no root. A relative path inside a nested DTO
  parameter (as opposed to a top-level parameter that goes through `FromWire`) is therefore kept
  unresolved, not resolved against the solution.

### Most output paths are not `FilePathWrapper` at all

A text search over the six source projects (`Common`, `Engines.*`, `Tools.*`) finds about 181
`string`-typed declarations named `FilePath`/`Path`/`SolutionPath`/`ProjectPath`/... across 63
files, including `ItemFailure.FilePath`, the `BatchTypes` records, `LargeResultInfo.FilePath`,
`OperationItemRecord.FilePath`, `GitImpl`'s `Path`, and the search/list result rows. That count
includes parameters and locals; the real number of path-bearing **result properties** is a Phase 0
deliverable. Some types already do it right (`EngineResultBase.FilePath`,
`MigrationEnvelope.filePath` are `FilePathWrapper`).

The per-branch DTO rollout (`proposal_structuredcontent_rollout.md`) has mostly landed: only 8
tool methods still return `SentinelCallToolResult<object>`. Most result shapes are now named types
whose path properties can be retyped directly.

### Paths also live in free text

`statusMessage`, `warningDetails`, `errorData.message`/`detail` (for example the `FileNotFound`
wording "'X.cs' does not exist at 'c:\...\X.cs'"), build and test output (compiler lines are
`<absolute path>(line,col): error CS....`), and Roslyn diagnostic messages. No typed change reaches
these.

### Where serialization happens

- Successful tool results: the MCP SDK serializes the returned `SentinelCallToolResult<T>` itself,
  using `McpServerToolCreateOptions.SerializerOptions`. `WithSentinelTools<T>()` passes `null`
  (`RoslynSentinel.Common/McpToolSchemaPatcher.cs:199-204`), so the SDK defaults apply. Those same
  options also drive argument binding and input-schema generation.
- Hand-built responses: only the exception catch-all, breaker refusal and argument-validation
  paths build a `TextContentBlock` directly (`RoslynSentinel.Server.Basic/
  ServiceRegistrationExtensionsBasic.cs`, around lines 280, 297, 575 and 793).
- Every response is then re-serialized once more by the tool-call echo filter
  (`ToolCallEcho.StampText`), which is the outermost filter.

### The same converter writes to disk

`SharedJsonOptions.Default` is used for persisted state (`PersistentWorkspaceManager.cs:143`,
`LargeResultHelper.cs:13`), and `FilePathWrapper` is serialized into operation blobs, the
migration ledger/envelope and typed large-result files. Changing `FilePathJsonConverter` globally
would write relative paths into persisted artifacts that are later read back with no guarantee of
the same root. **Relativization must be wire-only.**

## Proposal

Two parts, both shipped behind one toggle:

- **Part 1 (primary):** retype output path fields to `FilePathWrapper`, emitted solution-relative
  by a wire-only converter.
- **Part 2 (backstop):** a JSON-aware call-tool filter that relativizes whatever Part 1 has not
  reached yet, plus free text that no typed change can reach.

Why both: Part 1 is type-checked, needs no string heuristics, and fixes nested-parameter input
resolution as a side effect. But it never reaches free text, and every new tool is a chance to
reintroduce a `string FilePath`. Part 2 covers both of those, but on its own it is a heuristic
string rewrite. With both in place, the backstop's hit log becomes Part 1's worklist.

### Shared rules

- **Root** is `PersistentWorkspaceManager.GetSolutionRoot()`, the directory of the loaded solution
  file.
- **Under root**: emit the path relative to the root, with `/` separators. Forward slashes need no
  JSON escaping, and `FromWire` and `CanonicalizeSeparators` already accept them on input.
- **Outside root, or no solution loaded**: emit the absolute path unchanged. Never emit
  `..\..\` chains; they are longer and harder to read than the absolute path.
- **Comparison is case-insensitive**: the loaded root on this machine is `c:\...`, while
  `Assembly.Location` reports `C:\...`.
- **Prefix match needs a trailing separator**, so that a sibling directory such as
  `...\repos\RoslynSentinel2\` is not treated as being under `...\repos\RoslynSentinel`.

### Part 1: typed migration plus a wire-only converter

1. **Wire-only serializer options.** Add a factory in Common (alongside `SharedJsonOptions`) that
   builds the wire options from the SDK's default options, plus:
   - a `WireFilePathJsonConverter` in `options.Converters`. Converters registered on the options
     take precedence over the type-level `[JsonConverter(typeof(FilePathJsonConverter))]`, so the
     disk path (which never sees these options) keeps writing absolute paths unchanged;
   - the compact relaxed encoder (see "Related" below).

   Pass the options through `McpToolSchemaPatcher.CreateOptions`. The `services` argument is
   already available inside the `AddSingleton` factory, so the converter can resolve
   `PersistentWorkspaceManager` for a `Func<string?>` root provider.
2. **Converter behaviour.**
   - `Write`/`WriteAsPropertyName`: apply the shared rules to `Absolute` at write time. Do not
     rely on `Relative` having been populated. Dictionary keys (e.g. changed-file maps keyed by
     `FilePathWrapper`) get the same treatment.
   - `Read`/`ReadAsPropertyName`: `FilePathWrapper.FromWire(value, root)`. This closes the
     nested-DTO-parameter gap described above, so a relative path a model copies from output into
     a batch edit item resolves the same way a top-level parameter does.
3. **Retype output path properties from `string` to `FilePathWrapper`.** Order:
   1. shared envelope and list types (`ItemFailure`, `BatchTypes`, `LargeResultInfo.FilePath`,
      `listSummary.byFile` rows, the text-search match row, symbol search rows);
   2. per-tool result DTOs, ordered by Part 2's hit log (most frequent first);
   3. the remaining 8 `SentinelCallToolResult<object>` payloads, together with their DTO rollout.

   Types that are also persisted (e.g. `OperationItemRecord`) are safe to retype: disk
   serialization does not use the wire options, so their on-disk form stays absolute.
4. **Regression guard.** Add a reflection test over every type reachable from a tool's return
   type that fails on any `string` property whose name ends in `Path` or `File`, with an explicit
   allowlist for genuine non-path strings. This is the "next tool added" problem that
   `proposal_centralized_large_result_filter.md` raises for offloading; a guard test closes it
   mechanically instead of by memory.

### Part 2: JSON-aware backstop filter

A call-tool filter in `ServiceRegistrationExtensionsBasic.cs`, registered immediately **after**
the large-result offload filter. The first filter registered is the outermost, so this one runs
inside the offload filter, and its post-processing happens before the offload size check. The
size check then measures the already-shrunk text, and raw offload files contain relative paths
too.

For a JSON text block: parse once into a `JsonNode`. Working on parsed values means matching the
unescaped root, not its `\\`-escaped form. Then walk it with three rules:

1. **Whole-value rule (anywhere):** a string value that is entirely an absolute path under the
   root is relativized.
2. **Substring rule (allowlist only):** inside an allowlisted set of message fields -
   `statusMessage`, `warningDetails`, `errorData.message`, `errorData.detail`, and the build and
   test output fields (exact names are a Phase 0 deliverable) - replace each occurrence of the
   root prefix.
3. **Nothing else is touched.** In particular, verbatim content (`ReadFile`/`GetMethodSource`
   `source`, Search `preview`, `contextSnippet`, diffs) is never substring-rewritten, even when the
   file content contains the root path as a literal. A model copies those fields into
   `ReplaceSnippet` `oldContent`; one silently altered character turns into a match failure, which
   is exactly the transcription hazard this repo exists to remove.

Then re-serialize with the compact relaxed options. A non-JSON text block (the hand-built
exception, breaker and validation messages) is a message, not verbatim content, so the substring
rule applies to the whole text.

**Telemetry.** Log every whole-value rewrite with the tool name and JSON path, like the existing
"Large tool result" log line. Each hit is a typed field Part 1 has not reached yet. Over time the
whole-value hits should fall toward zero. The substring rewrites in messages are the backstop's
permanent job.

### Toggle

`--relative-paths` / `ROSLYNSENTINEL_RELATIVE_PATHS`, wired through the standard startup-option
pattern (static options class in Common, `Configure(args)` at all four server entry points; see
`LlmOptions.cs`). Both parts consult it. It lets tests and harnesses that assert absolute paths opt
out while they are updated, and it lets model evals A/B the change.

### Telling the model

- The `LoadSolution` success message and `McpServerStatus` state the root once, along the lines
  of: "Paths in results are relative to `<root>` unless absolute."
- Path parameter descriptions that say "absolute" get reworded to "solution-relative or absolute".
- **Phase 0 audit:** any `[McpServerTool]` method whose path parameter skips `FromWire` would fail
  when handed a relative path copied from output. Each one found gets fixed before the toggle
  defaults to on.

## Interactions

- **Tool-call echo:** the echo filter is outermost, so it stamps `toolCall` after Part 2 runs.
  Echoed arguments therefore stay exactly what the caller sent, which is the echo's stated purpose.
- **Typed large-result offload:** `StoreLargeResultAsync<T>` writes with the disk options
  (absolute). `GetLargeResult` deserializes, then returns the value through the SDK, where Part 1
  relativizes typed fields and Part 2 catches string fields. No change is needed in the offload
  path.
- **`serverInfo.binaryPath`:** handled separately by the cheap-wins change (see Status).
- **SubAgent child servers:** a child server relativizes against its own root (a worktree). Because
  a worktree mirrors the repo layout, a relative path should mean the same file in both parent and
  child, which arguably makes child output more portable than today's absolute worktree paths.
  This needs a verification test, not an assumption.
- **Transcript tooling:** `Parse-AgentLog.ps1`, analyst agents and any eval assertion that matches
  absolute paths need checking. That belongs in the Phase 0 inventory.

## Phasing

- **Phase 0 - inventory.** Path-bearing result properties by type; tool path parameters that
  bypass `FromWire`; message field names for the substring allowlist; tests, scripts and harness
  code that assert or parse absolute paths; a baseline tool manifest (`ConsoleMode`'s
  `ExtractToolManifest`) and a baseline character count for a fixed call set.
- **Phase 1 - infrastructure.** Toggle, wire options and converter (Part 1 steps 1-2), the
  backstop filter with telemetry (Part 2), the root statement in `LoadSolution`/`McpServerStatus`,
  and `FromWire` fixes from the Phase 0 audit.
- **Phase 2 - shared types.** Retype the envelope and list types (Part 1 step 3.1).
- **Phase 3 - per-tool DTOs,** ordered by backstop hit frequency, plus the remaining `object`
  payloads.
- **Phase 4 - guard.** The reflection test (Part 1 step 4), and the toggle defaults to on.

## Verification

- **Manifest diff:** passing non-null serializer options changes what drives input-schema
  generation. The emitted `tools/list` manifest must be byte-identical to the Phase 0 baseline.
  This repo has a history of schema-emission bugs that the C# signature did not reveal.
- **Round trip:** a relative path taken from one tool's output, passed to another tool's input,
  resolves to the same file. Cover a top-level parameter (`ReadFile`), a pinning parameter
  (`Search` `filePath`) and a nested DTO parameter (`ReplaceSnippet` `batchEdits[].filePath`).
- **Disk stays absolute:** operation blobs, the migration envelope/ledger and typed large-result
  files still contain absolute paths.
- **Edge cases:** a path outside the root, no solution loaded, a drive-letter case mismatch, and a
  sibling directory sharing the root as a string prefix.
- **Verbatim safety:** a fixture `.cs` file containing the root path as a string literal comes
  back from `ReadFile`, `GetMethodSource` and `Search` `preview` unchanged.
- **Savings:** before/after character counts for the Phase 0 call set (`Search` text,
  `FindReferences`, `Build` with errors, `ReadFile`), then a toggle-on/toggle-off model-eval A/B.

## Open questions

1. **Toggle default:** on for every flavour and mode, or only stdio `--mode=claude` at first?
2. **Root:** the solution directory or the git repository root? They differ when the solution file
   sits in a subfolder. This proposal assumes the solution directory, since that is what `FromWire`
   already resolves against.
3. **`FilePathWrapper.Relative` and the constructor's `solutionRoot` parameter** become mostly
   redundant once the converter computes relative paths at write time. Keep them or remove them?
4. **Messages that interpolate `FilePathWrapper.ToString()`** (for example the `FileNotFound`
   wording): fix at the source, or leave them to the backstop's substring rule? This proposal
   recommends leaving them to the backstop; there are many such sites and the rule handles them
   uniformly.

## Related: cheap wins implemented separately

Three smaller token reductions found during the same investigation are being implemented as an
independent change:

1. `ToolCallEcho.StampText` re-serialized every response with a bare `ToJsonString()`, so the
   default HTML-safe encoder turned `<`, `'`, `"` and `&` into `\u003C`-style escapes. That undid
   `SharedJsonOptions`' relaxed-escaping intent; escaped generics were the trigger for the
   dropped-parenthesis transcription failure that motivated it. Fixed by compact relaxed options.
2. `serverInfo.binaryPath` (on every response) is relativized under the loaded solution root.
   `McpServerStatus` keeps the absolute path.
3. List `statusMessage` text no longer repeats the per-file path list already in
   `listSummary.byFile`.

## Status

**Proposed** 2026-10-01. Not started. The three related cheap wins above are being implemented
separately (uncommitted at the time of writing).
