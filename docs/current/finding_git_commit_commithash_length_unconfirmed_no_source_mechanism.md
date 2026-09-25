# Finding: 41-character `commitHash` values reported for 4 `Git(operation: "commit")` calls could
# not be corroborated against any server-side artifact, and the commit implementation has no
# mechanism capable of producing a 41-character string

**Status:** unconfirmed as a tool defect, but re-opened rather than closed as a one-off
transcription error -- the user separately reports having seen 41-character commit hashes mentioned
in other, unrelated sessions, which weakens the "this was just this session's manual miscount"
explanation below. The on-disk commit hashes for the 4 calls this doc originally investigated are
independently verified correct (40 hex characters, standard SHA-1 length), and the commit-hash
construction path in `GitImpl.cs` was traced to source and contains no operation that could append,
duplicate, or otherwise lengthen the hash. No server log or other artifact captures the actual bytes
the tool returned for these 4 calls, so the original 41-character claim in this session rests solely
on a transcription of prior conversation text that could not be independently verified byte-for-byte
at the time. Given the cross-session recurrence, the leading hypothesis shifts from "manual
transcription slip" toward a serialization/escaping issue in a layer this investigation did not
examine (see "Recommended mitigation" below) -- see "What was ruled out" and "What could not be
verified" for what is and isn't settled.

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
`GitImpl.cs`'s own construction path has no mechanism to explain the claimed symptom even if it did
occur.

## Cross-session recurrence (2026-09-24, reported by the user)

After this doc's initial version was written and closed as "very likely a transcription error," the
user reported independently: **"I have seen 41 char commit hashes mentioned in other sessions."**
This is significant because it is not this session's evidence -- it means the symptom (or at least
the report of it) is not a one-off artifact of a single conversation's manual re-transcription. Two
non-exclusive readings:

- The symptom is real and reproducible across sessions, meaning the cause is upstream of anything a
  single session's transcript could show -- e.g. in the MCP framework's own JSON
  serialization/transport layer, or in whatever tool-result rendering path the client applies before
  the text reaches a transcript (neither was examined by this investigation, which only traced
  `GitImpl.cs`/`GitTools.cs`).
- Multiple independent sessions have each separately mis-transcribed a 40-character hash the same
  way, which is a weaker explanation once it is not just one session doing it, but not impossible if
  something about the value's visual presentation (e.g. line-wrapping in a terminal, a copy source
  that inserts characters) makes a specific kind of miscount likely across sessions.

This investigation cannot distinguish between these without a live, byte-verified reproduction (see
"What unblocks it"). The practical takeaway either way: **the tool should not rely on a human or
model eyeballing a hash to catch this class of defect** -- see "Recommended mitigation" below.

## Third occurrence (2026-09-25), and a new variable: a different agent, not a transcription by the reader

A background subagent (dispatched for "Sweep batch 16: migrate AsyncifyTools to IWorkspaceReader")
independently reported the identical symptom in its final report text: it quoted a commit hash as
41 characters, cross-checked it against a fresh `Git(operation: "log")` call on the same commit
(getting the same 41-character string both times, "not a transient tool glitch" by its own
reasoning), and declined to treat it as a blocker per this doc's own precedent.

This occurrence differs from all 4 originals in one respect worth recording: the earlier instances
were this session's own hand-transcription of prior conversation text. This one is a *different*
agent instance transcribing a tool result into its own final-report prose, then handing that prose
to the parent session -- so "the same single agent mis-transcribing repeatedly" is no longer a
candidate explanation; it would have to be "hand-transcription of a 40-char hex string into prose is
unreliable in general, independent of which agent does it," which is actually a *stronger*, more
mundane explanation than a serialization defect, not a weaker one.

The parent session re-verified independently, twice, using methods that do not involve prose
transcription:
1. A fresh `Git(operation: "log", paths: ["RoslynSentinel.Server.Advanced/AsyncifyTools.cs"])` call
   returned `"hash":"75ed7e94a26dcf8ffe6d09562822c38c4e6c51bf"` verbatim in the JSON tool result
   (not retyped by the parent agent -- read directly from the structured response).
2. That exact string, copied byte-for-byte (not retyped) into `printf '%s' "<hash>" | wc -c` via the
   Bash tool, counted **40** characters.

This is consistent with the doc's existing lean: **when a hash is checked by counting bytes
programmatically instead of visually reading/repeating hex digits, it is always 40.** Every
"41-character" report to date, across 3 separate agent contexts now (this session twice, one
subagent), has been a claim made by an agent reading/transcribing the digits itself, never a
byte-counted measurement. No occurrence has yet survived a `wc -c`-style check. This raises the
prior for "human/agent hex-transcription is the actual failure mode, not the Git tool" without fully
closing the file -- the recommended mitigation below (an assertion at construction time) would
settle this permanently regardless, and remains worth doing since it is cheap either way.

## Fourth, fifth, and sixth occurrences (2026-09-24/25), all resolved to 40 on independent check

Three more background subagents, dispatched for the `design_read_chokepoint.md` sweep (batches 35
"LogicOptimizationEngine", 37 "SecurityAndSafetyEngine", 38 "TestingEngine"), each independently
reported their own commit hash as 41 characters in their final-report prose. In every one of the 3
cases, the parent session re-verified via the same non-prose method as the third occurrence above --
a fresh `Git(operation: "log", paths: "<file>")` call read directly from the structured JSON
response (not retyped), followed by `printf '%s' "<hash>" | wc -c` on that exact byte-for-byte
string -- and every one measured **40** characters, matching the JSON verbatim:

- Batch 35: reported as 41; `4163960a26ccad048e144e632d4577e756c896c6` measured 40.
- Batch 37: reported as 41; `9e576430b9297c0dbad71bbdc343d790ee89de5a` measured 40.
- Batch 38: reported as 41; `d82f44f6b34d45f9683159e966ef8840f87c87ea` measured 40.

This makes it 6 for 6: every "41-character" report to date, across at least 4 separate agent
contexts (this session's own hand-transcription twice, plus 4 different background subagents), has
failed to survive a byte-counted check -- and every byte-counted check has landed on exactly 40. No
occurrence has ever produced a hash that measured non-40 when counted programmatically rather than
read/repeated by an agent. Given this density of consistent negative results, the practical
conclusion (short of the assertion-at-construction-time mitigation below actually shipping and
firing) is that this is an agent-transcription artifact of reading/quoting a 40-character hex string
in prose, not a tool-side defect -- the status line above is now stale in this respect and could
reasonably be tightened, but is left as-is pending that mitigation actually landing, per this doc's
own point 3 under "What unblocks it": the assertion is what would settle this permanently, and
until it ships and is observed never firing, "confirmed transcription artifact" is a hair stronger
a claim than the evidence technically requires.

## What unblocks it

Any of the following would resolve the open question:

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
3. **Add a length assertion at the DTO/serialization boundary (recommended, cheap, ships regardless
   of root cause).** The user's suggestion: add a sanity check on `GitCommitResult.CommitHash` (and
   plausibly `GitRevertResult`'s hash-typed fields, if any) that asserts/validates the value is
   exactly 40 hex characters before it leaves `GitImpl.cs`, logging or flagging a structured warning
   if not. This does not require knowing the root cause to be worth doing: a SHA-1 commit hash has a
   fixed, checkable shape, so validating it at the point it is constructed (`GitImpl.cs:948-956`) is
   a cheap, permanent guardrail against this exact symptom recurring silently -- consistent with this
   repo's failure doctrine ("a guardrail that halts before corruption instead of after"). It would
   also immediately answer the open question: if the assertion never fires across many future
   commits, that is strong evidence the original reports were transcription artifacts; if it does
   fire, it pinpoints that the corruption (if any) happens before this point rather than in a later
   serialization/transport layer.

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
