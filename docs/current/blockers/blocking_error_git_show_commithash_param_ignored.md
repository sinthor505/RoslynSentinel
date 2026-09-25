# `Git` tool's `show` operation ignores `commitHash` entirely, silently falls back to `target`'s
# default "working", surfacing as `fatal: ambiguous argument 'working'`

**Status:** OPEN. Root cause traced to source (see below) - this is a parameter-routing defect in
`RoslynSentinel.Server.Basic/GitTools.cs`'s `Git` method, not a git short-hash resolution bug in
`GitImpl.ShowAsync`.

## What was being attempted

Inspecting a specific prior commit via the dogfooded `Git` tool, per CLAUDE.md's mandated
`Bash(git status/log/diff/add/commit/revert)` -> `Git(operation: ...)` chokepoint:

```
Git(operation: "show", commitHash: "f4d2d24", reason: "<reason text>")
```

The target commit was confirmed to exist independently via plain `git log --all --oneline | grep
f4d2d24` (run outside any MCP session, for cross-check purposes only, consistent with CLAUDE.md's
carve-out for verifying tool behavior against real git): `f4d2d24 Fix Member(replace/remove)
NotFound on interface-declared members`.

## The exact error text

```json
{"isSuccess":false,"errorData":{"errorCode":"GitError","message":"fatal: ambiguous argument 'working': unknown revision or path not in the working tree.\r\nUse '--' to separate paths from revisions, like this:\r\n'git <command> [<revision>...] -- [<file>...]'"}}
```

## Where this happens

`RoslynSentinel.Server.Basic/GitTools.cs`, the `Git` method's parameter list and operation dispatch:

- Line 33-34: `target`'s `[Description]` reads `"diff: \"working\", \"staged\", a commit hash, or a
  range. show: a single commit hash/ref."` and its default is `string target = "working"`.
- Line 46-48: `commitHash`'s only documentation is `// CONDITIONAL-PARAM-REVIEW-REQUIRED: commitHash
  is required when operation=revert, unused otherwise.` with `[Description("Required for
  operation=revert: commit hash to revert.")]`.
- Line 99 (the dispatch switch): `GitOperation.show => await _gitImpl.ShowAsync(gitRoot, target,
  resolvedPaths, maxBytes, cancellationToken)` - **`show` is wired to `target`, not `commitHash`.**
- Line 103, for contrast: `GitOperation.revert => await _gitImpl.RevertAsync(gitRoot, commitHash,
  noCommit, cancellationToken)` - `commitHash` is used exactly once in this file, here, for `revert`
  only.

Confirmed by grep across `GitTools.cs`: `commitHash` appears only at its own declaration (line 48)
and at this single `revert` call site (line 103). There is no code path in `GitTools.cs` that ever
reads `commitHash` for `operation: "show"`.

So the call in question, `Git(operation: "show", commitHash: "f4d2d24", ...)`, silently drops
`f4d2d24` on the floor. `target` was never set by the caller, so it kept its default value
`"working"`, which is what actually reached `ShowAsync`.

`GitImpl.cs`'s `ShowAsync` (`RoslynSentinel.Basic/GitImpl.cs:703-713`) then ran, with `target ==
"working"`:

```csharp
var metaRaw = await RunGitAsync(
    gitRoot, ["show", $"--format={format}", "--no-patch", target], cancellationToken);
```

i.e. it executed `git show --format=... --no-patch working`. `"working"` is a sentinel value this
tool surface uses elsewhere (`diff`'s `target: "working"` means "working tree vs. index/HEAD" - see
`GitImpl.cs:657`) but it is not a git revision and does not exist as a ref, branch, or path in this
repo, so real `git show` rejected it exactly as it would for any other nonexistent revision:
`fatal: ambiguous argument 'working': unknown revision or path not in the working tree.` This is the
literal error text observed - `ShowAsync` passed the string through to `RunGitAsync` unchanged and
returned git's own stderr verbatim as `Error`.

## Root cause - confirmed, not a short-hash resolution bug

The initiating hypothesis (that this shares a code family with the already-fixed `79b7ad0` "Fix: Git
diff target:HEAD returned commit's own diff instead of working-tree changes", and that `ShowAsync`
has a broken or inverted target-resolution path specific to short hashes) does **not** hold up
against source. `ShowAsync` (`GitImpl.cs:703-761`) never receives `commitHash` at all - it only ever
sees whatever `GitTools.cs` line 99 passes as `target`. The defect is entirely upstream of
`GitImpl.ShowAsync`: it is a parameter-routing/schema-surface bug in `GitTools.cs`, not a bug in how
`ShowAsync` resolves a short hash to a full revision once it receives one.

This was verified directly against source per CLAUDE.md's root-cause discipline (never stop at the
surface, verify the named identifier against source before theorizing): grepped `GitTools.cs` for
every `commitHash` occurrence (2 total: declaration + the `revert` dispatch line) and read
`ShowAsync`'s full body in `GitImpl.cs` end to end. There is no length check, no short-hash-specific
branch, and no code that would behave differently for a 7-char hash vs. a 40-char hash anywhere in
this path - the string never reaches `ShowAsync` in the first place, so hash length is irrelevant.
**Ruled out:** short-hash resolution logic in `ShowAsync`, and any shared code path with `DiffAsync`
or the `79b7ad0` fix - `ShowAsync`'s only shared logic with `DiffAsync` is the `EmptyTreeHash`
parent-fallback (`GitImpl.cs:721-722`), which is unrelated to this symptom and is not reached, since
the call fails earlier at the `git show --no-patch working` metadata step (line 712-715).

Whether `Git(operation: "show", target: "f4d2d24", ...)` (using `target` instead of `commitHash`)
succeeds was not tested as part of this writeup but is expected to work based on reading the
dispatch line - `target`'s default and description already describe `show`'s "single commit
hash/ref" usage correctly; only the parameter name a caller reaches for is wrong. This is the
detail a follow-up session should confirm before considering this closed.

## Why this blocks (per CLAUDE.md failure doctrine)

`show` is a read-only operation in this tool's own dispatch (`GitTools.cs:75`,
`isReadOnlyOperation` includes `GitOperation.show`), and CLAUDE.md requires all git operations to go
through the `Git` tool rather than shell git. With no working `show`-by-hash path via the
documented, discoverable `commitHash` parameter, a caller following the tool's own schema (which
tells `commitHash` is "Required for operation=revert" and says nothing about `show` needing it) has
no way to inspect an individual prior commit without either falling back to shell `git show` (a
CLAUDE.md violation) or guessing that `target`, not `commitHash`, is the parameter `show` actually
reads - a guess the schema does not support, since `commitHash`'s own description names only
`revert` and never mentions being irrelevant to `show`, while `target`'s description does mention
`show` but a caller asking to "show a commit" has no strong reason to prefer `target` over the
identically-plausible, more literally-named `commitHash`.

## What unblocks it

One of the following, either is sufficient:

1. **Preferred:** In `GitTools.cs`'s dispatch switch, make `show` accept `commitHash` (falling back
   to `target` if `commitHash` is unset) so both spellings work, since a caller reasonably expects a
   parameter named `commitHash` to be honored by an operation whose entire job is showing a commit.
   Pair this with tightening `commitHash`'s `[Description]` (currently "Required for operation=revert:
   commit hash to revert.") to also mention `show`, so the schema stops implying it is revert-only.
2. **Minimum viable fix:** Leave the routing as-is (i.e. `show` continues to only read `target`), but
   fix `commitHash`'s `[Description]` so it explicitly says it is NOT read by `show` and that `show`
   must use `target` instead - closing the schema gap that made this call look reasonable in the
   first place. This is a strictly worse UX than option 1 (two parameters that mean almost the same
   thing depending on operation) but is a smaller change.
3. Regardless of which of the above is chosen, add a guard: when `operation == show` (or `diff`) and
   `target` is left at its default `"working"` while `commitHash` was explicitly supplied, either
   honor `commitHash` (if doing fix 1) or fail fast with an actionable `InvalidArguments` error
   naming both parameters and which one `show` actually reads - never let `"working"` reach
   `RunGitAsync` for `show` and surface as a raw git stderr string that never mentions
   `commitHash`, `target`, or the tool's own parameter names at all.

## Related

- `docs/current/blockers/resolved/blocking_error_git_diff_target_head_vs_working_inconsistency.md` -
  the `79b7ad0` fix this doc's initiating hypothesis suspected sharing a root cause with; ruled out
  above, kept here only because the symptom text ("working") coincidentally overlaps.
- `RoslynSentinel.Server.Basic/GitTools.cs:33-48, 99, 103` - the exact lines cited above.
- `RoslynSentinel.Basic/GitImpl.cs:703-761` - `ShowAsync`'s full body, read end to end to rule out a
  short-hash-specific branch.
- CLAUDE.md, "Root-cause discipline: never stop at the surface" - the discipline this doc follows in
  ruling out the initiating hypothesis rather than assuming it.
