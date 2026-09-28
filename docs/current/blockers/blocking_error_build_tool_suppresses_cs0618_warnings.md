# `Build` reports 0 warnings while `GetDiagnostics` finds 2 real CS0618 warnings for the same file

**Status:** FIXED 2026-09-27. Root cause: `RunFullBuildAsync` in
`RoslynSentinel.Basic/BuildEngine.cs` (around line 165, in the `process.StartInfo.ArgumentList`
block right after `-v quiet`) shelled out to `dotnet build <sln> --nologo -v quiet` with no
`--no-incremental` flag. MSBuild's own incremental "up-to-date" check then applies: if a project's
outputs are already newer than its inputs (e.g. because this same tool, an IDE, or CI built it
moments earlier), MSBuild skips recompiling that project entirely and emits zero diagnostics for
it - not zero *new* diagnostics, zero diagnostics, period. That is a real "the compiler did not run"
condition being reported identically to "the compiler ran and found nothing," with
`DiagnosticsComplete: true` asserted in both cases. Confirmed directly: running the tool's exact
`dotnet build RoslynSentinel.slnx --nologo -v quiet` command by hand reproduced the bug
(`0 Warning(s)` on a second invocation with no source changes), while a clean/forced rebuild
(`--no-incremental`) reproduced the full, expected 10-CS0618-site count every time. This confirms
hypothesis (2) from below (stale/skipped compilation), not hypothesis (1) (verbosity-based
suppression) - `-v quiet` on its own does not hide warnings, confirmed by a separate isolated
single-project repro showing `-v quiet` printing the same warning as `-v minimal`/`-v normal`.

**Fix:** added `process.StartInfo.ArgumentList.Add("--no-incremental")` immediately after the
existing `-v`/`quiet` arguments in `RunFullBuildAsync`, forcing MSBuild to recompile every project
from scratch on every `fullBuild` call regardless of on-disk up-to-date state. `RunQuickBuildAsync`
was never affected - it already goes through `DiagnosticEngine`'s in-memory Roslyn
`Compilation`/`GetDiagnostics` path (the same one `GetDiagnostics` itself uses), which always
recomputes from current syntax trees and has no on-disk incremental cache to go stale.

**Verification:** after rebuilding and restarting the MCP server so it loaded the fix, both
`Build(scope:"solution", level:"fullBuild")` and `Build(scope:"project",
scopeName:"RoslynSentinel.Basic", level:"fullBuild")` now report `errorCount: 0, warningCount: 20`
(10 unique CS0618 sites x 2 referencing projects), including both `SymbolNavigationEngine.cs:1537`
and `:1716`, matching `GetDiagnostics(scope:"file", scopeName:
".../RoslynSentinel.Basic/SymbolNavigationEngine.cs")`'s 2-warning report at the same two lines
exactly. Added a regression test,
`BuildEngineTests.RunFullBuildAsync_CalledTwiceWithObsoleteCallSite_BothCallsReportTheWarningAsync`
in `RoslynSentinel.Tests.Battery/BuildEngineTests.cs`, which calls `RunFullBuildAsync` twice
back-to-back against a real on-disk `TestSolutionFixture` project containing an `[Obsolete]` call
site - the second call is the one that would have silently lost the warning to MSBuild's
incremental skip before this fix. Test passes, along with the existing 36 Build/RunTest-related
tests in `RoslynSentinel.Tests.Battery` (no regressions from adding `--no-incremental`).

## What was being attempted

Ordinary progress-tracking during the universal-symbol-resolver staged migration (see
`docs/current/proposal_universal_symbol_resolver.md`). Five resolver methods in
`RoslynSentinel.Basic/SymbolNavigationEngine.cs` are marked `[Obsolete("...", error: false)]` as
part of that migration; internal self-references to them are being migrated to
`ResolveCandidates` incrementally, caller by caller. Two internal self-references to one of them,
`ResolveSymbolByNameAsync`, remain unmigrated at `SymbolNavigationEngine.cs:1537`
(`FindCallersAsync`) and `:1716` (`FindImplementationsForMemberAsync`) - this is expected, known,
in-scope-for-later work, not itself a bug.

I had an independent prior expectation, from an earlier manual build earlier in the same session,
of 10 distinct `CS0618` sites across 6 files. A subagent's build report after a commit claimed "0
errors, 0 warnings," which conflicted with that expectation. I re-verified twice more myself via
`Build`, got 0 warnings both times, then cross-checked with `GetDiagnostics` at file scope on the
one file I could pin down exactly - which contradicted `Build` outright.

## The exact symptom

`Build(scope: "solution", fullBuild: true)` and `Build(scope: "project", scopeName:
"RoslynSentinel.Basic", fullBuild: true)` both report:

- `errors: 0`, `warnings: 0`
- `diagnosticsComplete: true`
- `stdoutTail` containing MSBuild's own literal text `"0 Warning(s)"`

Reproduced twice, at both scopes, not a one-off flake.

Calling `GetDiagnostics(scope: "file", scopeName:
"c:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Basic\SymbolNavigationEngine.cs")`
against the exact same on-disk file state returns:

- `errors: 0`, `warnings: 2`
- both diagnostics `CS0618`, both naming `SymbolNavigationEngine.ResolveSymbolByNameAsync` as
  obsolete
- at exactly lines 1537 and 1716 - matching the two known, expected unmigrated call sites exactly

`Build` and `GetDiagnostics`, run against identical file state, disagree on whether these two
warnings exist. `Build` also asserts `diagnosticsComplete: true`, which is a false completeness
guarantee given `GetDiagnostics` independently finds real diagnostics `Build` did not report.

## Where it happened

- Tool: `Build` (`mcp__root_roslyn_sentinel_advanced_stdio__Build`), `fullBuild: true`, at both
  `scope: "solution"` and `scope: "project"` / `scopeName: "RoslynSentinel.Basic"`.
- Tool: `GetDiagnostics`, `scope: "file"`, `scopeName:
  RoslynSentinel.Basic/SymbolNavigationEngine.cs` (contradicting evidence, not itself the site of
  the defect).
- File under test: `RoslynSentinel.Basic/SymbolNavigationEngine.cs:1537` and `:1716`.

## Root cause - NOT traced to source

Two competing hypotheses, neither confirmed:

1. **`Build`'s warning aggregation or invocation path silently drops `CS0618`/`ObsoleteAttribute`-
   sourced diagnostics** from whatever count/list it surfaces in the JSON result, independent of
   what MSBuild itself detected.
2. **`Build` is compiling from a stale or cached output** (in-memory workspace, incremental/
   restore-cache artifact, or similar) that predates the `[Obsolete]` attributes or the two
   unmigrated call sites landing in the file, so it never sees the warnings in the first place.

The detail that MSBuild's own console text (`stdoutTail`) also says `"0 Warning(s)"` - not just the
tool's JSON summary - suggests the discrepancy may originate upstream of JSON serialization, e.g.
in whatever invocation or msbuild argument set the `Build` tool uses (verbosity level, warning
suppression flags, or a `dotnet build` invocation that behaves differently from the compilation
`GetDiagnostics` inspects) - rather than being purely a reporting/serialization-layer bug. This is
not confirmed; the tool's implementation has not been read to find the specific code path
responsible.

Not ruled out: whether this is specific to `CS0618`/`Obsolete`-sourced diagnostics, or would also
suppress other warning categories - only `CS0618` has been observed missing so far, since it is the
only warning category present in this file at this time.

## Ruled out

- Not a one-off flake: reproduced twice, across both `scope: "solution"` and `scope: "project"`,
  both `fullBuild: true`.
- Not a stale on-disk file: `GetDiagnostics` was called against the identical on-disk state
  immediately after the second `Build` call, no edits in between.
- Not a wrong-file mixup: both tools were pointed at the same absolute path
  (`RoslynSentinel.Basic/SymbolNavigationEngine.cs`), and `GetDiagnostics`'s two reported line
  numbers (1537, 1716) match the two known unmigrated call sites exactly, confirming it inspected
  the right file.

## Why this blocks (per CLAUDE.md failure doctrine)

`Build`'s `warningCount`/`warnings`/`warningSummary` fields, and its `diagnosticsComplete: true`
flag, are the environment's stated completeness signal for "the compiler has told you everything
it knows." Any workflow relying on that signal - including this migration's own plan of tracking
staged-obsolete-migration progress via `CS0618` counts - is unreliable as long as `Build` can
silently disagree with `GetDiagnostics` on the same file with no error, no partial-result flag, and
no indication anything was dropped. A caller has no way to detect this discrepancy from `Build`'s
output alone; it was only caught here because of an independent prior expectation from an earlier
manual build, which will not generally be available.

## What unblocks it

- Read `Build`'s implementation (`RoslynSentinel.Basic/BuildEngine.cs` or equivalent, per the
  `fullBuild`/`quickBuild` split documented in
  `docs/current/finding_build_quickbuild_spurious_error_cascade.md`) to find the exact code path
  `fullBuild: true` uses at `scope: "solution"` and `scope: "project"`, and determine whether it
  invokes `dotnet build` with flags that suppress or filter `CS0618`/obsolete-sourced warnings, or
  whether it reads from a stale/cached compilation instead of a fresh one.
- Confirm or refute hypothesis (2) directly: check whether `fullBuild`'s underlying invocation
  reflects the current on-disk file content (e.g. by comparing a file mtime/hash the invocation
  actually read against the current on-disk file) at the moment `warnings: 0` was reported.
- Once root-caused, either fix `Build` to surface the same warnings `GetDiagnostics` finds, or - at
  minimum - correct `diagnosticsComplete: true` to not claim completeness when it cannot back that
  claim for `CS0618`/obsolete diagnostics.
- Until fixed, treat `GetDiagnostics` at file scope as the accurate source for warning counts on a
  single file. Note this has only been cross-checked at file scope - it has not been verified
  whether `GetDiagnostics` at `scope: "project"` or `scope: "solution"` has its own, separate scope
  limits or aggregation gaps; that is untested, not assumed safe.
- A regression test that intentionally introduces an `[Obsolete]`-sourced `CS0618` call site, then
  asserts `Build(fullBuild: true)`'s `warnings`/`warningCount` reflects it, would catch this
  directly and should be added once the root cause is confirmed.

## Related

- `docs/current/finding_build_quickbuild_spurious_error_cascade.md` - a different confirmed defect
  in the same `Build` tool family, on the `quickBuild` path rather than `fullBuild`; not the same
  root cause (that finding is a false-positive error cascade from a workspace-load timing race,
  this is a false-negative warning suppression), but worth cross-referencing since both concern
  `Build`'s reliability as a completeness signal.
- `docs/current/proposal_universal_symbol_resolver.md` - the migration whose progress tracking
  (via `CS0618` counts on `ResolveSymbolByNameAsync` and 4 sibling obsolete resolvers) surfaced
  this discrepancy.
