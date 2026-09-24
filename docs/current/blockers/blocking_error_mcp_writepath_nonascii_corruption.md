# Non-ASCII corruption reproduces after the Git read-side mojibake fix, in a different pipeline layer

## Status

OPEN, UNCONFIRMED - low-to-medium severity, but flag as potentially higher scope than the resolved
bug this is distinct from. Not yet traced to a specific `file:line`. This is a hypothesis backed by
the evidence below, not a confirmed root cause. Do not close or fold into
`docs/current/blockers/resolved/blocking_error_git_diff_mojibake_display.md` - that fix is verified
(build clean, 3 new regression tests passing) and addresses a different hop in the pipeline than the
one implicated here.

## What was being attempted

Continuing routine dog-fooded git work in the same session as the read-side mojibake fix
(`blocking_error_git_diff_mojibake_display.md`), after that fix had already been applied, built, and
confirmed via passing regression tests. A commit was made via the RoslynSentinel MCP `Git` tool:

`Git(operation: "commit", message: "<message text containing a single non-ASCII placeholder
character>")`

The commit succeeded (no error returned). The resulting commit was then read back via:

`Git(operation: "show", target: "<hash of the commit just created>")`

## The exact symptom

The commit message text returned by `Git(operation: "show")` contained a mangled multi-character
sequence at the exact position where the original non-ASCII placeholder character had been written -
structurally the same shape as the pattern documented in the resolved read-side bug (a run of 2-3
non-ASCII characters standing in for what should have been a single multi-byte character), despite
that read-side bug (`RunGitAsync`'s missing `StandardOutputEncoding`/`StandardErrorEncoding`) being
already fixed and verified working by this point in the session.

Separately, in the same `Git(operation: "show")` response, the diff content included lines from
`docs/current/blockers/resolved/blocking_error_git_diff_mojibake_display.md` - a file whose content
is known-correct UTF-8 on disk, because the agent authored it directly via the `Edit` tool in this
same session. Those lines came back through `show`'s diff with the identical mangled-sequence pattern
reproduced verbatim, at positions corresponding to that file's own non-ASCII punctuation.

## What this is NOT

**Not the already-fixed `RunGitAsync` stdout/stderr decoding bug.** That fix
(`StandardOutputEncoding = Encoding.UTF8` / `StandardErrorEncoding = Encoding.UTF8` on
`RunGitAsync`'s `ProcessStartInfo`, plus `-c i18n.logOutputEncoding=utf-8 -c i18n.commitEncoding=utf-8`
on every git invocation) was built and its 3 regression tests were confirmed passing before this
symptom was observed. `show`'s diff content is produced by a second git subprocess invocation that
goes through the same, now-fixed `RunGitAsync` path - if `RunGitAsync` were still the problem, the
regression tests covering it should have caught this shape and did not.

**Not yet confirmed to be file corruption.** Unlike the accidental-truncation incident referenced in
the resolved doc's "Related" section, there is no evidence here that anything on disk changed. The
`docs/current/blockers/resolved/blocking_error_git_diff_mojibake_display.md` content used for
cross-checking is understood to be correct UTF-8 on disk (authored directly via `Edit` this session)
- what's unconfirmed is only whether the new commit's message, as stored in the git object, is itself
corrupted, or whether the git object is fine and the corruption is introduced only when that data is
served back out through the MCP tool response. This doc treats that as an open question - see "Next
steps" below.

**Not confirmed to be in any specific file or function.** No source location has been identified for
this defect. Everything below the "Root cause" heading is a hypothesis, explicitly labeled as such per
CLAUDE.md's root-cause discipline ("a cause you have not traced to source is a hypothesis - label it
as one").

## Root cause - not traced to source; hypothesis only

Unknown. What can be said, and why it points away from `GitImpl.cs`'s own subprocess decoding:

1. **Shape match.** The corruption's shape (a short run of non-ASCII/control-range characters
   replacing a single multi-byte character) matches the exact signature described in the resolved
   doc's "Detection" section - the fingerprint of a UTF-8 multi-byte sequence being decoded one byte
   at a time through a legacy single-byte codepage. That signature is generic to "somewhere a UTF-8
   byte stream got decoded with the wrong codepage" - it does not, by itself, identify which hop did
   it.

2. **Timing.** This reproduced *after* the `RunGitAsync` fix was applied, built, and verified via
   passing tests, in the same session. That rules out the specific defect already fixed there as the
   sole explanation, though it does not rule out a second, structurally-similar defect elsewhere in
   the same file.

3. **Two different data paths, same symptom.** The corruption appeared both on data that had just
   been written (the commit message, going in via `Git(operation: "commit", message: ...)`) and on
   data that was known-good before being re-served (the resolved doc's own file content, going out via
   `Git(operation: "show")`'s diff). If the defect were solely in how `RunGitAsync` decodes git's
   redirected stdout, only the read/output path would be affected - the write-path symptom (the commit
   message itself coming back wrong) suggests either a second, independent decoding hop, or a hop that
   sits outside `GitImpl.cs` entirely and affects both directions.

Candidate locations, none yet checked against source, in rough order of how the evidence points:

- **MCP JSON-RPC transport / stdio pipe** between the RoslynSentinel server process and the MCP client
  (Claude Code / the VS Code extension). If this pipe or its (de)serialization layer has an unset or
  incorrect text encoding somewhere, it would affect every tool's response uniformly, not just `Git`'s
  - consistent with the corruption appearing in both a fresh write's echo and a re-served file's diff
  content, both of which leave `GitImpl.cs` as correctly-decoded strings (per the already-fixed
  `RunGitAsync`) but then cross this additional hop before reaching the agent.
- **How the server process's own stdout is captured by whatever launches it** - see project memory on
  the per-session stdio launcher (`roslynsentinel-mcp-launch.ps1`, `project_per_session_mcp_server_isolation`).
  If the launcher or the hosting process itself has an unset `Console.OutputEncoding` (the same class
  of bug `RunGitAsync` had, just at a different layer), that would reproduce the identical signature
  independently of anything `GitImpl.cs` does correctly internally.
- **Write-side argv marshaling.** `Git(operation: "commit", message: ...)` has to get the message
  string into the git child process somehow - if that construction uses `ArgumentList.Add` (or
  equivalent) for the `-m` argument, .NET's marshaling of a Unicode string into a Windows child
  process's argv could have its own, independent encoding hazard that has nothing to do with
  `RunGitAsync`'s *stdout* decoding fix. This is untested here.
- **JSON serialization of the tool result**, between `GitImpl.cs` returning a correctly-decoded
  .NET string and that string reaching the wire. Unlikely on priors (`System.Text.Json` defaults to
  UTF-8 correctly) but not ruled out.
- **Claude Code's / the VS Code extension's own tool-result display/re-decode path.** Also unlikely
  on priors (other non-ASCII content displays correctly elsewhere in this same session) but not ruled
  out without a controlled test.

## Impact

Unconfirmed severity, provisionally low-to-medium, same "workaround known" caveat as the resolved
bug: cross-checking suspicious output against `ReadFile`/`Grep` remains the fallback the resolved
doc's "Detection" section built for exactly this scenario, and `DetectDecodeCorruption`'s two checks
(U+FFFD, runs of C1 control characters U+0080-U+009F) should still catch this shape if it recurs
through `Git(operation: "show"/"diff")`, since that helper runs on the returned text regardless of
which hop introduced the corruption.

However, this is flagged as **potentially higher scope** than the resolved bug for two reasons:

1. It appears to sit in a **different, currently-unfixed layer** of the pipeline (see candidates
   above) - the fix already shipped for `RunGitAsync` does not address it, and no equivalent
   guardrail exists yet at whichever layer is actually responsible.
2. It touches **write correctness**, not just diagnostic display. The resolved bug was scoped to
   `diff`/`show`/`log` output being unreadable while the underlying data was confirmed fine. Here, the
   corruption was observed on a commit message that had just been *written* - if the git object itself
   turns out to be corrupted (not yet confirmed either way, see "Next steps" #1), that would mean an
   agent's commit messages containing non-ASCII characters are being permanently mis-recorded in
   history, not merely mis-displayed.

## Workaround

Same as the resolved bug, until this is traced further: cross-check any tool output containing
suspicious non-ASCII byte patterns against an independent source before trusting it, and prefer
ASCII-only punctuation in commit messages and prompt/doc text per CLAUDE.md's existing convention,
which sidesteps this class of defect entirely for content the agent itself authors. This does not
help for content that legitimately must contain non-ASCII characters (e.g. test fixtures, or a commit
message quoting user-supplied non-ASCII text) - there is currently no known-safe way to round-trip
such content through `Git(operation: "commit")` and confirm it landed correctly.

## Next steps (investigation, not yet implemented)

1. **Isolate the hop with a clean before/after byte comparison.** Call
   `Git(operation: "commit", message: "<a single known non-ASCII character>")` and then
   `Git(operation: "show")` on that exact commit, in isolation from any other content, to get an
   unambiguous before/after comparison. Cross-check the same commit independently of the MCP tool -
   either `git cat-file -p <hash>` run directly outside RoslynSentinel, or a debugger attached to the
   server process inspecting the string at the point `GitImpl.cs` returns it - to determine whether
   the git object itself is corrupted or only the MCP response is.
2. **Check the MCP server's own stdout/response-channel encoding setup.** Verify whether
   `Console.OutputEncoding` (or equivalent stream setup) is explicitly set to UTF-8 at each server
   flavor's hosting entrypoint (`Program.cs` or equivalent) - the same class of unset-encoding bug
   `RunGitAsync` had, just potentially recurring at the JSON-RPC transport layer instead of the git
   subprocess layer. See project memory `project_server_flavors_and_build_configs` (4 flavors x
   Debug/Release) and `project_per_session_mcp_server_isolation` (per-window stdio launcher) for where
   to look across all entrypoints, not just one flavor.
3. **Check write-side argv marshaling independently of `RunGitAsync`'s stdout decoding.** Confirm
   whether the commit `-m` argument is passed via `ArgumentList.Add` or equivalent, and whether .NET's
   marshaling of a Unicode string into a Windows child process's argv has its own encoding hazard
   distinct from (and unaffected by) the `StandardOutputEncoding`/`StandardErrorEncoding` fix already
   applied.
4. **Consider promoting `DetectDecodeCorruption` (currently private static in `GitImpl.cs`) to a
   shared location** (e.g. `RoslynSentinel.Common`) so it can guard every tool's response boundary,
   not just `Git`'s `diff`/`show`. Additionally consider an **inbound** guard that flags a tool
   parameter already containing these corruption signatures when it arrives from the client - if a
   parameter is already corrupted on arrival, that would itself be strong evidence the defect is
   upstream of RoslynSentinel entirely (client-side or transport-side), independent of anything in
   `GitImpl.cs`.

## Related

- `docs/current/blockers/resolved/blocking_error_git_diff_mojibake_display.md` - the read-side
  `RunGitAsync` stdout/stderr decoding bug, fixed and verified in the same session this symptom was
  first observed in. That fix's `DetectDecodeCorruption` helper and its two detection signals (U+FFFD,
  C1 control-character runs) are reused above as the working definition of "this shape" for this
  separate incident. Confirmed distinct - see "What this is NOT" above.
- `project_per_session_mcp_server_isolation` (project memory) - the per-VS-Code-window stdio launcher
  (`roslynsentinel-mcp-launch.ps1`), a candidate location for an encoding defect on the transport side.
- `project_server_flavors_and_build_configs` (project memory) - 4 server flavors x Debug/Release;
  next-step #2 needs checking across all of them, not just the flavor active this session.
- CLAUDE.md's ASCII-punctuation convention - already the practical mitigation for any content the
  agent itself authors; does not help for non-ASCII content the task genuinely requires.
