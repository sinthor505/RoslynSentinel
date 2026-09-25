# Finding: 41-character `commitHash` values reported for 4 `Git(operation: "commit")` calls could
# not be corroborated against any server-side artifact, and the commit implementation has no
# mechanism capable of producing a 41-character string

**Status:** unconfirmed as a tool defect. The on-disk commit hashes are independently verified
correct (40 hex characters, standard SHA-1 length). The commit-hash construction path was traced to
source and contains no operation that could append, duplicate, or otherwise lengthen the hash. No
server log or other artifact captures the actual bytes the tool returned for these 4 calls, so the
41-character claim rests solely on a transcription of prior conversation text that could not be
independently verified byte-for-byte. This is filed as a finding about an evidence gap, not a
confirmed response-integrity bug -- see "What was ruled out" and "What would confirm or refute this"
below.

## What was being attempted

Investigate a claim (raised at the start of this task) that `Git(operation: "commit")` had returned
a `commitHash` field of 41 hex characters -- one longer than a real SHA-1 -- on 4 consecutive real
commits made earlier in this session (2026-09-24), each backing one step of the
`design_read_chokepoint.md` sweep:

1. `fb936909c842de2e37f5620ff7a7b560fece53c7` region -- "Sweep batch 1: migrate AdvancedLogicEngine
   to IWorkspaceReader"
2. `243f75d45ea1aec5aede60daf979c526e932c701` -- "Sweep batch 2: migrate SymbolResolver to
   IWorkspaceReader"
3. `7b8dc4aa58d50fe4e07c5cfa8a34484265515b4e` -- "Sweep batch 3: migrate
   ValidateAndApplyHelper.BuildDiffAsync to IWorkspaceReader"
4. `b44b744ba9801db56d4b46836389873fe6439533` -- "Sweep batch 4: migrate ProjectConsistencyEngine to
   IWorkspaceReader"

## What was verified independently

### On-disk hashes are correct (40 characters each)

Read `.git/logs/HEAD` directly (a plain file read, not a `git` shell invocation -- justified here
specifically because the object under investigation is the Git MCP tool's own reported output, so
using the tool itself to check the tool would not be independent corroboration; this is a
investigation-scoped exception to the dog-fooding policy, not a routing-around of it for normal
work) and confirmed the "new hash" field of the 4 relevant reflog lines:

```
f81f20ce96b78566c1537eb95b0879e187cd7634 fb936909c842de2e37f5620ff7a7b560fece53c7 ... Sweep batch 1
fb936909c842de2e37f5620ff7a7b560fece53c7 243f75d45ea1aec5aede60daf979c526e932c701 ... Sweep batch 2
243f75d45ea1aec5aede60daf979c526e932c701 7b8dc4aa58d50fe4e07c5cfa8a34484265515b4e ... Sweep batch 3
7b8dc4aa58d50fe4e07c5cfa8a34484265515b4e b44b744ba9801db56d4b46836389873fe6439533 ... Sweep batch 4
```

Measured programmatically (not by eye): all 4 "new hash" values are exactly 40 characters, the
correct SHA-1 length. Source: `.git/logs/HEAD` lines 546-549 (via `Grep` on that file).

### The commit-hash construction path has no mechanism to lengthen the hash

Traced the full path from `git commit` to the returned `CommitHash` field:

- `RoslynSentinel.Basic/GitImpl.cs:948-949` -- after a successful commit, the hash is obtained via
  a fresh, separate `git rev-parse HEAD` call:
  ```csharp
  var hashRaw = await RunGitAsync(gitRoot, ["rev-parse", "HEAD"], cancellationToken);
  var hash = hashRaw.ExitCode == 0 ? hashRaw.Stdout.Trim() : "";
  ```
  `rev-parse HEAD` emits exactly one line containing the 40-character hash. `.Trim()` strips the
  trailing newline. There is no concatenation, substring, padding, or string-building of any kind
  applied to `hash` between this line and its use.
- `RoslynSentinel.Basic/GitImpl.cs:956` -- `hash` is placed into the result verbatim:
  `return new GitCommitResult { Success = true, CommitHash = hash, Message = finalMessage };`
- `RoslynSentinel.Basic/GitImpl.cs:323-401` (`RunGitAsync`) -- stdout is assembled via
  `stdout.AppendLine(e.Data)` per output line (line 358) and returned as `Stdout = stdout.ToString()`
  (line 398). For a single-line command like `rev-parse HEAD`, this appends exactly one line plus one
  `Environment.NewLine`, which `.Trim()` at the call site removes. `StandardOutputEncoding` is
  explicitly forced to `Encoding.UTF8` (line 343, with a code comment at 336-341 documenting a past
  mojibake bug this guards against) -- ruled out as a contributing factor here because a SHA-1 hash
  is pure ASCII hex, unaffected by any codepage/UTF-8 decoding mismatch regardless of which encoding
  path is taken.
- `RoslynSentinel.Basic/GitImpl.cs:124-128` -- `GitCommitResult` record: `CommitHash` is a plain
  `string` property with no custom getter/setter logic, `[JsonConverter]`, or formatting attribute
  that could alter the value during serialization.
- `RoslynSentinel.Server.Basic/GitTools.cs:103` -- the `[McpServerTool]`-attributed `Git` method
  calls into `GitImpl` and returns its result object directly; it does not touch, reformat, or
  re-derive `CommitHash` anywhere in this file (confirmed via `Grep` for `CommitHash`/`commitHash` --
  the only two hits in this file are the `commitHash` *input* parameter used for `operation=revert`,
  unrelated to the commit path's output).

No branch, helper, or serialization step anywhere in this path appends, duplicates, or pads a
character onto the hash. Given `git rev-parse HEAD` cannot itself emit an incorrect-length hash for
a real commit, this path -- as written -- cannot produce a 41-character `CommitHash` under any
input this investigation could identify.

## What was ruled out

- **Encoding/mojibake corruption** (the repo's known failure class, e.g.
  `docs/current/finding_git_diff_output_mojibake_encoding_defect.md`) -- ruled out because mojibake
  corrupts non-ASCII multi-byte sequences into extra/garbled characters; a SHA-1 hash is pure
  ASCII/hex and is not subject to that class of defect regardless of codepage.
- **On-disk corruption of the commit object itself** (the "much bigger deal" scenario flagged in the
  task) -- ruled out. `.git/logs/HEAD` shows unambiguous 40-character hashes for all 4 commits, and
  `git rev-parse HEAD` (the exact command `GitImpl.cs:948` runs) can only emit the canonical 40-char
  object ID for a valid commit; there is no supported git internal state that would make
  `rev-parse` itself return 41 characters.
- **A substring/truncation-then-pad bug** -- ruled out by reading; no substring or padding operation
  exists between `hashRaw.Stdout.Trim()` and the value landing in `CommitHash`.

## What could not be verified

No artifact independent of the task's own prior-turn text was found containing the actual bytes the
`Git(operation: "commit")` tool call returned for these 4 calls:

- The per-window stdio server logs under `bin-vscode/*/Advanced/logs/*.log` and the HTTP-host logs
  under `RoslynSentinel.Server.*/bin/**/logs/*.log` were searched (via `Grep`) for both the 4 hash
  prefixes and the 4 commit messages ("Sweep batch 1" through "Sweep batch 4"); none of the existing
  log files contain a match, meaning tool-call response payloads are not captured in any log this
  investigation could locate.
- The only record of the claimed 41-character values is the quoted text carried over from the prior
  turn of this conversation. Manually re-transcribing that same 41-character hex string multiple
  times during this investigation (to test a "duplicated leading character" hypothesis vs. a
  "duplicated trailing character" hypothesis) produced inconsistent results between attempts --
  demonstrating, empirically, that hand-transcription of a 41-character hex string is itself
  error-prone. This means the 41-character claim cannot currently be distinguished from a
  transcription artifact introduced somewhere between the original tool response and the text
  quoted into this investigation's task description.

Per CLAUDE.md's root-cause discipline ("a cause you have not traced to source is a hypothesis --
label it as one"): a genuine tool-side defect producing 41-character hashes remains a hypothesis,
not a confirmed finding. The confirmed findings are narrower: the on-disk hashes are correct, and
the implementation path has no mechanism to explain the claimed symptom even if it did occur.

## What unblocks it

Either of the following would resolve the open question:

1. **Reproduce live.** Run a fresh `Git(operation: "commit")` call in an active session and inspect
   the raw MCP tool-call response JSON directly (not a re-typed/re-quoted copy of it) -- e.g. via
   whatever transcript/log mechanism captures the actual wire bytes of a tool result in the harness
   in use. If a live reproduction shows 40 characters, the original report was very likely a
   transcription artifact and this finding can be closed as such. If a live reproduction shows 41
   (or any non-40) characters, capture the raw response bytes verbatim (not retyped) and re-open
   this finding with that artifact attached, which would justify a deeper look at whether the MCP
   framework's own JSON serialization or transport layer (outside `GitImpl.cs`/`GitTools.cs`, which
   are now ruled out) is responsible.
2. **Add response logging for mutating Git operations.** There is currently no log capturing
   tool-call response payloads (confirmed by the log search above), which is why this investigation
   could not corroborate or refute the original claim independently. If `Git` (or all mutating MCP
   tools) logged their outgoing result object at `LogLevel.Debug` or similar, a future occurrence of
   this same claim could be checked against a real artifact within the same session instead of
   relying on conversation-text transcription.

## Related

- `RoslynSentinel.Basic/GitImpl.cs:948-949, 956` -- the traced commit-hash construction, found to
  contain no lengthening mechanism.
- `RoslynSentinel.Basic/GitImpl.cs:323-401` -- `RunGitAsync`, including the UTF-8 encoding fix
  (lines 336-344) whose surrounding comment documents the unrelated prior mojibake bug.
- `docs/current/finding_git_diff_output_mojibake_encoding_defect.md` -- the repo's actual confirmed
  git-output encoding defect; cited here only to explain why it was checked and ruled out as a cause
  of this symptom.
- `.git/logs/HEAD` (lines 546-549 at time of writing) -- source of the verified 40-character on-disk
  hashes for the 4 commits in question.
