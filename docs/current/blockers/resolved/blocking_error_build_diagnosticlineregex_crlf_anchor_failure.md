# Blocking error: `Build`'s full-build diagnostic parser matches zero lines on Windows,
# silently reporting `errorCount: 0` / `warningCount: 0` even when real errors/warnings exist

**Status:** FIXED 2026-09-24, commit `b5cc33a1d42dd80f2812fcb1d92f4863066f1c89` --
`RoslynSentinel.Basic/BuildEngine.cs`'s `DiagnosticLineRegex` trailing anchor changed from
`\[.+\]$` to `\[.+\]\r?$`. Fix is correct and confirmed via `ReplaceSnippet`'s own delta-compile
gate (0 new errors) plus a full solution `Build` (0 errors, 61 warnings, exit code 0). **However,
end-to-end confirmation that the live MCP server process now reports nonzero `warningCount` is
still outstanding** -- see "Verification results" below. This is a residual environment gap, not a
defect in the fix itself, and it should be re-checked after the server binary is next rebuilt and
restarted (not attempted in this session, per policy).

Filed per CLAUDE.md's blocking-error policy -- this affects `errorCount`, which is the value this
session (and presumably every prior session) has trusted as the sole gate for "safe to commit."

## What was observed

Two subagents doing mechanical `GetCurrentSolutionAsync` -> `GetSolutionAsync` migration work
(RefactoringEngine.cs, SymbolNavigationEngine.cs) independently flagged the same discrepancy on the
same turn: `Build`'s structured `warningCount` field reported `0`, while `stdoutTail` in the same
response visibly listed dozens of live `warning CS0618: ...` lines. Reproduced directly in this
session:

```
"errorCount":0,"warningCount":0,"errors":[],"warnings":[],"errorSummary":[],"warningSummary":[]
```
...while `stdoutTail` in the same response ends with `"276 Warning(s)\r\n    0 Error(s)"` and lists
32+ individual `warning CS0618` lines above that summary.

## Root cause (traced to source, reproduced in isolation)

`RoslynSentinel.Basic/BuildEngine.cs:129-130` defines:

```csharp
private static readonly Regex DiagnosticLineRegex = new(
    @"^(?<path>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<id>[A-Za-z0-9]+):\s*(?<message>.+?)\s*\[.+\]$",
    RegexOptions.Compiled | RegexOptions.Multiline);
```

`RunFullBuildAsync` (`BuildEngine.cs:217`) runs this regex against the full captured `stdoutText`
from a real `dotnet build` child process, and derives `ErrorCount`/`WarningCount`/`Errors`/
`Warnings`/`ErrorSummary`/`WarningSummary` **entirely from `warnings.Count`/`errors.Count` --
never from `StdoutTail`, which is a separate, independently-computed last-40-lines slice of the
same raw text** (`BuildEngine.cs:246`, `Tail(stdoutText)`). The two are computed from the same
source string but via completely different code paths, so one can silently diverge from the other.

The regex's trailing anchor is `\[.+\]$`. On this Windows environment, `dotnet build` output uses
CRLF (`\r\n`) line endings. `RegexOptions.Multiline` makes `$` match the position immediately
before a `\n` -- but the character immediately before that `\n` is `\r`, and nothing in the pattern
consumes it: the pattern requires the literal `]` (from `\[.+\]`) to be the character sitting right
at the `$` position. With a CRLF terminator, the actual last character before `\n` is `\r`, not `]`,
so the match fails for every single line in real output.

**Confirmed empirically** (Python's `re` module has the same `$`-before-`\n` Multiline semantics as
.NET's `Regex`, so this isolates the mechanism cleanly):

```python
pattern = r"^(?P<path>.+?)\((?P<line>\d+),(?P<col>\d+)\):\s*(?P<severity>error|warning)\s+(?P<id>[A-Za-z0-9]+):\s*(?P<message>.+?)\s*\[.+\]$"
line = r"C:\proj\File.cs(10,5): warning CS0618: 'X' is obsolete: 'Y' [C:\proj\proj.csproj]"

re.search(pattern, line, re.MULTILINE)              # matches (no trailing CRLF)
re.search(pattern, line + "\r\n", re.MULTILINE)      # NO MATCH (real-world shape)
re.search(pattern.replace(r"\]$", r"\]\r?$"), line + "\r\n", re.MULTILINE)  # matches again
```

Every real line dotnet-build emits is CRLF-terminated on Windows, so `DiagnosticLineRegex` matched
**zero** lines against real output on this platform -- for errors as well as warnings, since both
severities go through the identical trailing-anchor pattern. `errors.Count` and `warnings.Count`
were therefore always 0 in `RunFullBuildAsync`'s structured result on Windows, regardless of what
the actual build produced, as long as the process's own exit code / stdout otherwise looked normal.

## Why this is a blocking-severity finding, not just a cosmetic warning-count nit

This session used `Build`'s `errorCount: 0` as the sole automated gate before every commit across
15+ sweep batches. If a `ReplaceSnippet` edit had introduced a genuine compiler error, this same
regex failure would have reported `errorCount: 0` for that error too (the pattern is severity-
agnostic; `error` and `warning` share the identical CRLF-broken trailing anchor). The `outcome`
field (`"Succeeded"`/`"Failed"`) is driven by `process.ExitCode` (`BuildEngine.cs:247`,
`Outcome: process.ExitCode == 0 ? BuildOutcome.Succeeded : BuildOutcome.Failed`), which is NOT
affected by this bug -- exit code comes directly from the `dotnet build` child process, not from the
regex. So `outcome: "Succeeded"` / `exitCode: 0` remained a trustworthy signal throughout this
session's use of `Build`. But `errorCount`/`warningCount`/`errors`/`warnings`/`errorSummary`/
`warningSummary` were not, and any workflow (this session's included) that inspects those structured
fields instead of `outcome`/`exitCode` to decide pass/fail is silently blind to real diagnostics.

`RunQuickBuildAsync` (`BuildEngine.cs:117`, `WarningCount: summary.Warnings`) is unaffected -- it
uses in-memory Roslyn diagnostics from the loaded workspace, not a regex over spawned-process stdout,
per its own `[Description]` distinguishing `level=quickBuild` from `level=fullBuild`. Confirmed
still true when re-reading the current source in the fix session (2026-09-24): `RunQuickBuildAsync`
builds its `errors`/`warnings` lists from `summary!.Details.Where(d => d.Severity == "Error"/...)`,
with no reference to `DiagnosticLineRegex` anywhere in that method.

## Fix applied (2026-09-24)

`RoslynSentinel.Basic/BuildEngine.cs`'s `DiagnosticLineRegex`, trailing anchor changed:

```diff
- @"^(?<path>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<id>[A-Za-z0-9]+):\s*(?<message>.+?)\s*\[.+\]$",
+ @"^(?<path>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<id>[A-Za-z0-9]+):\s*(?<message>.+?)\s*\[.+\]\r?$",
```

Applied via `ReplaceSnippet` (validated against the delta-compile gate, 0 new errors). Confirmed by
`SearchSolutionText` that this was the only copy of `DiagnosticLineRegex` / this pattern anywhere in
the solution (2 literal hits in `BuildEngine.cs` only: the field definition and its one usage site
in `RunFullBuildAsync`), so no duplicated copy elsewhere needed the same fix.

No regression test added. The regex-matching logic is inlined directly inside `RunFullBuildAsync`
(BuildEngine.cs, lines ~131-268), coupled to a real spawned `dotnet build` `Process`, with no
extracted parse helper (e.g. a `ParseDiagnostics(string stdoutText)` method) to unit test in
isolation. `RoslynSentinel.Tests.Battery/BuildEngineTests.cs` exists and is the right home for such
a test, but its two current tests only exercise the zero-project short-circuit path, not real stdout
parsing. Adding a proper regression test would require either extracting a parse helper (a real
refactor) or mocking the `Process` spawn (substantial new test infrastructure) -- both judged out of
scope for this fix.

## Verification results

Full solution `Build(level: fullBuild, scope: solution)` run immediately after applying the fix:

- Outcome: `Succeeded`, exit code `0`, 0 errors (as expected -- the fix is source-only and compiles
  cleanly).
- `stdoutTail` clearly showed `"61 Warning(s)\r\n    0 Error(s)"` and dozens of individual
  `warning CS0618` lines (the in-progress `ISolutionProvider` migration sweep's expected residue).
- **`warningCount` was still `0`** in the structured result -- the fix did NOT visibly take effect.

Investigated via `McpServerStatus`: the live server process (`pid 18828`,
`RoslynSentinel.Server.Advanced.dll`) reports `buildTimeUtc: 2026-09-25T00:51:56Z`, which predates
this session's `ReplaceSnippet` edit. `ReplaceSnippet` writes the corrected source to disk and
delta-compiles it in-memory via the Roslyn workspace for its own validation gate, but that is
independent of the server's own compiled IL: the `dotnet build` child process spawned by
`RunFullBuildAsync` is spawned by the *already-running, already-compiled* server binary, which still
contains the pre-fix regex. This matches the previously-recorded pattern that a source edit does not
rebind the running server binary (see memory: "LoadSolution doesn't rebind server binary").

**This session did not attempt to rebuild or restart the server**, per the task's explicit
instruction to not attempt that and instead document the finding. The fix on disk (and in git
history as of commit `b5cc33a1d42dd80f2812fcb1d92f4863066f1c89`) is correct and verified by:
1. `ReplaceSnippet`'s own delta-compile validation gate (0 new errors introduced).
2. A full solution `Build` after the edit (0 compile errors, exit code 0) -- i.e. the change itself
   does not break the build.
3. Direct inspection of the regex change against the documented root cause (the `\r?` now sits
   exactly where the CRLF's `\r` was previously unconsumed).

What remains unverified: that `warningCount`/`errorCount` become correctly nonzero **once the
server process running this code is rebuilt and restarted**. Re-run `Build(level: fullBuild)` after
the next server rebuild/restart and confirm `warningCount` lands in the same ballpark as the warning
lines visible in `stdoutTail` (not necessarily exact, since `stdoutTail` is only the last ~40 lines).

## Related

- `RoslynSentinel.Basic/BuildEngine.cs:128-130` -- the (now fixed) `DiagnosticLineRegex` definition.
- `RoslynSentinel.Basic/BuildEngine.cs:217-231` -- `RunFullBuildAsync`'s parse loop, where
  `errors`/`warnings` are populated.
- `RoslynSentinel.Basic/BuildEngine.cs:246-247` -- `StdoutTail: Tail(stdoutText)`, the separate,
  unaffected raw-text path that is why the discrepancy was visible at all (the raw warnings are
  right there in the same response, just not reflected in the structured counts).
- `RoslynSentinel.Tests.Battery/BuildEngineTests.cs` -- existing test home; no new test added, see
  "Fix applied" above for why.
- This session's sweep batches 6-15 (`docs/current/design_read_chokepoint.md` migration) all relied
  on `Build`'s `errorCount`/`warningCount` fields as the per-batch safety gate; those specific builds
  are not retroactively suspect (each batch's target file was independently verified via
  `SearchSolutionText` to have zero remaining live `GetCurrentSolutionAsync` references after each
  edit, and `outcome`/`exitCode` correctly reflected build success throughout).
- Commit: `b5cc33a1d42dd80f2812fcb1d92f4863066f1c89` (2026-09-24), "Fix DiagnosticLineRegex CRLF
  anchor bug in RunFullBuildAsync".
