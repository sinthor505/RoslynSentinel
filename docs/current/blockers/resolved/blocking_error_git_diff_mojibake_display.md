# Git tool diff output mis-decodes non-ASCII characters

## Status

FIXED (2026-09-24) - display/transport defect, not data corruption. See "Root cause - confirmed"
and "Fix" below.

## Symptom

`Git(operation: "diff", paths: ["docs/current/TODO.md"])` returned a JSON `diff` string containing
byte sequences like `ΓÇö` at every position where the actual file (confirmed via
direct `Grep`/`Read`) contains a UTF-8 em dash (`—`, `-`). Example, from the tool's own
response:

```
-## `Member`'s three divergent declaration-kind dispatch tables ΓÇö proposal only, not implemented
```

versus the file on disk at that line, read directly:

```
## Symbol/member lookup fragmentation across five resolvers plus three dispatch tables -- proposal only, not implemented
```

## What this is NOT

Initially treated as a real content-corruption incident - a subagent's `Write` had truncated and
then reconstructed `docs/current/TODO.md` via `git cat-file -p HEAD:...`, and this diff output was
the first evidence reviewed afterward. The mangled bytes looked exactly like the mojibake pattern
CLAUDE.md's ASCII-punctuation rule warns about ("non-ASCII punctuation... turns into mojibake e.g.
`Γçö` when it crosses an encoding mismatch").

Ruled out by direct verification: `Grep "[^\x00-\x7F]"` and `Grep "-"` (literal em dash) against
the live file both confirm the on-disk bytes are correctly-encoded UTF-8 em dashes, consistent with
the file's own pre-existing convention (84 occurrences file-wide, unchanged from before this
session). The file is fine. Only the `Git` tool's `diff` response is wrong.

## Root cause - confirmed

Confirmed at `RoslynSentinel.Basic/GitImpl.cs`, `RunGitAsync`'s `ProcessStartInfo` construction. The
git child process's `RedirectStandardOutput`/`RedirectStandardError` streams were read via
`OutputDataReceived`/`ErrorDataReceived` with no `StandardOutputEncoding`/`StandardErrorEncoding`
set. `System.Diagnostics.Process` decodes redirected pipes using `Console.OutputEncoding` when left
unset, which on this Windows host is the legacy OS codepage, not UTF-8. Git itself always writes
UTF-8 to stdout/stderr regardless of that codepage, so any UTF-8 multi-byte character (an em dash's
3-byte sequence `E2 80 94` here) got decoded as three separate legacy-codepage characters and
re-encoded into exactly the reported mojibake (`ΓÇö`). This matched the hypothesis
exactly and affects every non-ASCII character in any diffed/shown/logged content, not just em dashes.

## Fix

`RunGitAsync` now sets `StandardOutputEncoding = Encoding.UTF8` and `StandardErrorEncoding =
Encoding.UTF8` on the `ProcessStartInfo`, so the redirected pipes are decoded the way git actually
wrote them. Also added `-c i18n.logOutputEncoding=utf-8 -c i18n.commitEncoding=utf-8` to every git
invocation as a belt-and-braces measure against a repo-level `i18n.*` config changing how git itself
encodes commit/log text before it reaches the pipe.

Additionally, `GitDiffResult`/`GitShowResult` gained a `Warning` field populated by a new
`DetectDecodeCorruption` helper, so a future decode failure (a different root cause, a different
host codepage, etc.) surfaces as an explicit warning in the tool response instead of silently
returning corrupted text - see "Detection" below.

## Detection

`DetectDecodeCorruption` (`GitImpl.cs`) checks diff/show text for two independent signals that
decoding went wrong, without needing to know what the correct text should have been:

1. **U+FFFD** (Unicode replacement character) - the .NET UTF-8 decoder's own marker for "this byte
   sequence was invalid and was discarded." Any occurrence means real data was lost.
2. **A run of 2+ C1 control characters (U+0080-U+009F)** - the specific shape produced when a UTF-8
   multi-byte sequence is decoded one byte at a time through a Windows-125x-family codepage (the
   `ΓÇö`-style pattern this incident is named for). Well-formed text essentially never contains this
   run on its own, so it is a reliable tell even without ground truth to compare against.

Both checks are cheap (a single pass over the string) and run on every `diff`/`show` call. This is
the general environment-level guardrail the failure doctrine asks for: instead of an agent having to
notice a suspicious byte pattern and cross-check it by hand (the workaround this doc previously
documented), the tool now names the hazard itself in the response.

## Impact

Cosmetic/diagnostic only, as currently observed: the diff response is unreadable at the corrupted
positions but does not appear to reflect or cause actual data loss - `status`, `Read`/`ReadFile`,
and `Grep` against the same file all show correct content. Risk: an agent trusting the `diff` output
at face value (per CLAUDE.md's root-cause discipline - tool results can be wrong or misleading)
could misdiagnose a non-problem as file corruption, as nearly happened in this session, or could
misjudge the size/nature of a real change if a diff hunk's non-ASCII content is what's being
reviewed for correctness.

## Workaround (historical, no longer needed after the fix above)

Cross-check any diff containing suspicious byte sequences (especially ones matching the
`\uXXXX\uXXXX\uXXXX` mojibake shape) against a direct `Read`/`Grep` of the file before concluding
content is corrupted. Kept for reference in case a differently-caused decode issue ever recurs and
`DetectDecodeCorruption` doesn't happen to catch its particular shape.

## Related

Surfaced while verifying a subagent's TODO.md recovery (`design-doc-scribe`, writing
`proposal_universal_symbol_resolver.md` / `proposal_compilation_cache.md`) after a `Write` accident
truncated `TODO.md` and the subagent reconstructed it via `git cat-file -p HEAD:docs/current/TODO.md`
(a read-only plumbing command outside the MCP `Git` tool's covered operations, used because no
covered operation restores a single file's content). That reconstruction turned out fine for this
file, but the recovery method itself carries a real risk - `HEAD` is not necessarily the same as the
working tree's pre-edit state if a file was already modified-but-uncommitted, and that gap would be
silently unrecoverable. Worth its own note for subagents: prefer catching a bad `Write` before more
edits stack on top of it, over recovering after the fact from a git ref that may not match the true
prior state.
