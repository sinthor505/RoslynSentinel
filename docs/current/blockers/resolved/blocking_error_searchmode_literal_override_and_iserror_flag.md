# Blocking errors found while investigating a reported ApplyDiff false-failure

**RESOLVED** — all three real issues fixed in commit `b537249` ("Fix validation scope, searchMode
literal override, and MCP IsError signaling"):
- Issue 0 (validation scope): `ValidationEngine.ValidateChangesAsync` now expands
  `affectedProjectIds` to transitive dependents via `GetProjectDependencyGraph()`
  ([ValidationEngine.cs](../../../../RoslynSentinel.Common/ValidationEngine.cs)), so
  `validateOnApply: true` catches breaks in callers, not just the edited project's own call graph.
- Issue 1 (searchMode literal override): confirmed `searchMode: literal` no longer silently
  coerces to regex — it stays literal and only warns
  ([SentinelWorkspaceTools.cs:1531-1539](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1531-L1539)).
- Issue 3 (missing MCP `IsError`): a `CallToolFilter` now sets `CallToolResult.IsError = true`
  whenever the response body's top-level `success` field is false
  ([ServiceRegistrationExtensionsBasic.cs:197-233](../../../../RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs#L197-L233)).

A separate, related fix (commit `5561d58`) added a whole-file-rewrite size guard to `ApplyDiff`
(files-format applies that would shrink a file >50% are rejected with a confirmation-code escape
hatch), addressing the broader "agent submits truncated content as a full file" risk this doc's
investigation also touched on. The ~25-minute stall documented in the run 2d follow-up below was
independently assessed as a debugger-pause artifact on the dev machine, not a server bug — no fix
applicable. Verified against current code 2026-08-29. Moved here for history; no further action
needed.

**Original status:** the specific symptom the user asked me to investigate (`ApplyDiff` reported
failure but a following `ReadFile` showed the file had changed) turned out to be fully explained by
the model's own actions — not a tool bug. But investigating it surfaced three real, separate
issues: a validation-scope gap that let the model's own destructive first `ApplyDiff` call report
`success:true` with zero diagnostics despite gutting a whole class, a live regression in
`SearchSolutionText`'s new `searchMode` parameter, and one systemic MCP-protocol-level gap
affecting every tool that returns `Success: false` without throwing. Documenting all three per the
dog-fooding policy — no fix attempted. Issue 0 (validation scope) is the most serious of the
three: it means `validateOnApply: true` (the default, and the tool's main safety net) does not
actually protect against a large, common class of real mistakes.

A follow-up review of run 2d (see "Real issue 3" below) found a fourth, unrelated observation: a
~25-minute silent stall on one `ApplyDiff` call, with the entire server process producing zero log
output for the whole window (not just this request — every log source across the process went
quiet). No code path explains a wait that long (traced to a trivial semaphore-guarded field read
with no other logged operation holding the lock), no OS sleep/resume event fired, and the process
never restarted — the shape (silent, then resumes instantly into a deterministic, already-correct
exception) matches a debugger paused at an exception/breakpoint on the dev machine hosting the
server more than any server-side deadlock. Documented for the record per the dog-fooding policy,
but this is most likely a debugging-session artifact, not a RoslynSentinel tool bug.

Source transcript:
`RoslynSentinel-AgentTesting/RoslynSentinel NormalizeWhitespace Test - 9B - run 2c - 2026-08-28 16.36.md`,
cross-referenced against
`RoslynSentinel.Server.Advanced/bin/Debug/net10.0/logs/http-host-20260828-155640.log`.

## The reported symptom, explained (not a tool bug)

What happened, reconstructed from the log (authoritative — the model's own narration in the
transcript misdiagnoses this several times):

1. `16:01:44` — model calls `ApplyDiff(changesetFormat: files, changes: { AdvancedStructuralEngine.cs: "using Microsoft.CodeAnalysis;\r\n...using Microsoft.CodeAnalysis.Formatting;\r\n" })`
   — i.e. it submitted **only the 5 using-directive lines** as the file's entire new content,
   intending this as "step 1 of 3" (add a using directive) rather than a full-file replacement.
   `changesetFormat: files` is documented as whole-file replacement
   ([SentinelWorkspaceTools.cs:400](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L400)),
   so the tool did exactly what it was told: log line 323 confirms
   `Wrote changes to ...AdvancedStructuralEngine.cs (Attempt 1)`, and the call legitimately
   returned `success:true`. **This truncated the 552-line file to 6 lines, correctly, per the
   model's own (mistaken) instruction.** Same `files`-format misunderstanding already documented
   from the earlier 08:42 run (see the `ApplyDiff` partial-file confusion noted in
   [[blocking_error_searchsolutiontext_regex_warning_salience]]) — this model made the identical
   mistake again, independently.
2. `16:02:57–16:03:00` — model's second `ApplyDiff` call submits just the helper-method snippet as
   the "file content," again under `files` format. Pre-apply validation correctly rejects it
   (CS0106 "private not valid here", CS8805 "top-level statements", CS0246 unresolved types —
   because a bare method body with no class/namespace wrapper is not valid as a whole file). The
   log confirms **no** "Wrote changes to..." line for this call — nothing was written; the 6-line
   state the model then reads back is entirely the leftover from call 1, not a new corruption from
   call 2.
3. The model never restores the file. It tries `UndoLastApply` against an unrelated, stale
   changeId (fails correctly), then `git stage` + `git commit -m "Revert: Restore..."` — but since
   the working tree's only content at that point *is* the 6-line truncated file, this "revert"
   commit actually **commits the corruption to git HEAD**, permanently baking it in under a
   misleading message. Every subsequent `git status`/`git diff` correctly reports "clean" —
   clean relative to the now-corrupted HEAD — which the model misreads as evidence the file has
   been magically re-truncated by the tooling ("the git commit didn't restore it... this is
   strange"), rather than recognizing its own commit as the cause.

So: no ApplyDiff bug in the write path. The write that happened was requested and reported
accurately; the write that failed was blocked and reported accurately with no disk effect. The
data loss is the model's own doing, compounded by committing it to git. Flagging the git-commit
step as worth keeping an eye on in future runs (a model that commits a broken intermediate state
under a falsely-reassuring message is a sharper failure mode than just leaving it uncommitted —
worth a future guard, e.g. running a build/diagnostics check before allowing a commit — but that's
a process/prompt concern, not something to fix in the MCP tools themselves).

## Real issue 0: pre-apply validation only recompiles the edited file's own project, not its callers

The user pushed back on the above and asked the sharper question directly: the first `ApplyDiff`
call's response wasn't just "success" — it carried `validationResult: {success:true, diagnostics:[]}`.
`validateOnApply` defaults to `true`
([SentinelWorkspaceTools.cs:401](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L401)),
so this was the tool's actual pre-apply safety check reporting a clean bill of health for a change
that deleted every public method of a class — `ConvertAbstractClassToInterfaceAsync`,
`ReplaceConstructorWithFactoryAsync`, `ExtractSuperclassAsync`, `ExtractClassAsync`,
`InlineClassAsync`, `UpdateTypeReferencesAsync` — while at least seven other files in
`RoslynSentinel.Tests.Advanced` and `RoslynSentinel.Tests.Battery` reference the class and would
fail to compile without it (confirmed via
`grep -l "new AdvancedStructuralEngine("` across the repo: `ComprehensiveToolTests.cs`,
`MassiveRefactoringTests.cs`, `BugFixTests.cs`, `BatteryTwentyFourTests.cs`,
`BatteryThirtyOneTests.cs`, `BatteryThirtyFiveTests.cs`, `BatteryFourteenTests.cs`). This is a real
gap, not something the "the model did this to itself" framing above excuses.

### Root cause

[ValidationEngine.cs:103-206](../../../../RoslynSentinel.Common/ValidationEngine.cs#L103-L206)
(`ValidateChangesAsync`):

1. For each changed file, it resolves that file's own `documentId` and adds only
   `documentId.ProjectId` to `affectedProjectIds` (lines 124-149). It never walks
   `ProjectReference`s (forward or reverse) to find projects that depend on the edited project.
2. It then recompiles **only** the projects in `affectedProjectIds` — baseline vs. candidate — and
   diffs the error sets (lines 160-201).
3. In this case, `AdvancedStructuralEngine.cs` belongs to `RoslynSentinel.Advanced`. Nothing
   *inside* `RoslynSentinel.Advanced` itself calls the methods being deleted, so
   `RoslynSentinel.Advanced` alone compiles cleanly both before and after the change —
   `introducedDiagnostics` stays empty and `DiagnosticReport(true, [])` is returned. The actual
   breakage lives entirely in the `RoslynSentinel.Tests.*` projects, which are never in
   `affectedProjectIds` and are never recompiled by this check at all.

This is not a false report in the sense of the check lying about what it checked — it genuinely
found 0 new errors in the one project it looked at. The gap is scope: a library project's public
surface can be gutted and "pass validation" as long as nothing *in that same project* used it,
regardless of how many other projects in the solution depend on it.

### Why this matters

This defeats the main purpose `validateOnApply` exists for. Any edit that removes or narrows a
public member — a very common shape of "genuine mistake," not just this specific
whole-file-overwrite accident — will validate clean and apply successfully as long as the edited
project is leaf-like relative to its own internal call graph, even when it is *not* leaf-like
relative to the rest of the solution (tests, other libraries, consuming projects). Callers of
`ApplyDiff`/`CreateFile` with `validateOnApply: true` (the default) currently get a false sense of
solution-wide safety from a project-local check.

### Suggested fix direction (not implemented)

- Expand `affectedProjectIds` to include every project that transitively references any project in
  the initial set (via `Solution.GetProjectDependencyGraph()` and its reverse-references lookup),
  not just the directly-edited projects. This is the standard shape of a real
  "will this change break the build" check and is what `dotnet build`/a full solution build would
  have caught (worth confirming against `Build(level: fullBuild)` behavior — if `fullBuild` already
  does this correctly, that's further evidence the gap is specific to `ValidateChangesAsync`'s
  quick/in-memory path, not a fundamental solution-wide blind spot).
- At minimum, document this scope limitation prominently in `ApplyDiff`'s/`CreateFile`'s
  `validateOnApply` description, so a caller (model or human) knows a "success" validation result
  does not mean "safe to apply solution-wide" — only "safe within the edited file's own project."

## Real issue 1: `SearchSolutionText`'s new `searchMode: literal` can't actually force literal search

This codebase has evidently already been updated since the earlier
[[blocking_error_searchsolutiontext_regex_warning_salience]] write-up: `isRegex: bool` has become
`searchMode: TextSearchMode` (`literal` | `regex`) — exactly the "mandatory enum instead of
optional bool" fix direction that was suggested there. Confirmed at
[SentinelWorkspaceTools.cs:1418](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1418).

But the new implementation has a worse problem than the one it replaced:

```csharp
// SentinelWorkspaceTools.cs:1429-1437
if (searchMode == TextSearchMode.regex)
{
    regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeout: TimeSpan.FromSeconds(5));
}
else if (searchMode == TextSearchMode.literal && LikelyRegexPattern.IsMatch(pattern))
{
    warnings.Add($"Pattern '{pattern}' contains regex metacharacters but searchMode is literal - performed search as regex.");
    actualSearchMode = TextSearchMode.regex;
}
```

When the caller explicitly passes `searchMode: literal` and the pattern happens to contain any of
`^$.*+?()[]{}|\`, the tool **silently overrides the caller's explicit choice and searches as
regex anyway** — there is no way to opt out of this, no escape parameter, nothing. `LikelyRegexPattern`
(originally just a warning heuristic) is now load-bearing for actual search execution.

### Reproduction (from the transcript, lines 244-285)

The model explicitly requested literal search for a pattern containing regex metacharacters it did
**not** intend as regex:

```
SearchSolutionText(pattern: "NormalizeWhitespace.*ToFullString", fileGlob: "RoslynSentinel.Advanced/*.cs", searchMode: "literal")
```
→ `{"success":true,"data":[],"totalRecords":0,...,"warning":"Pattern 'NormalizeWhitespace.*ToFullString' contains regex metacharacters but searchMode is literal - performed search as regex. No matches were found for the regex pattern..."}`

There is no literal string `NormalizeWhitespace.*ToFullString` (with a literal `.` and `*`) in the
codebase, so 0 matches for a *regex* interpretation is correct — but the model asked for a
*literal* search, which should have looked for that exact substring including the literal
characters `.` and `*`, and would still have found 0 matches (no file contains that exact
substring either, since real call sites are `NormalizeWhitespace().ToFullString()` with
parentheses in between) — so this particular case is a bad example pattern on the model's part,
but the underlying tool defect is real and independent of this specific query: **there is
currently no way to search literally for any pattern containing a regex metacharacter**, which
for a codebase search tool is a common and reasonable thing to want (e.g. literally finding
`Foo.Bar(`, `x[0]`, `a+b`, or any generic type `List<T>`).

### Why this matters

This is strictly worse than the original optional-bool defaulting problem: previously a caller
could always get a true literal search by being explicit (`isRegex: false` was honored
unconditionally; only the *warning wording* was misleading). Now, explicitness doesn't help —
`searchMode: literal` is silently coerced to regex based on the same heuristic that used to only
generate a warning. The enum-parameter fix direction from the earlier write-up was implemented,
but the override logic that made the old bool version confusing was carried over into the new
enum version and now actively defeats the enum's purpose (a mandatory, explicit choice that the
model reliably respects — except this one is not actually respected).

### Suggested fix direction (not implemented)

- When `searchMode: literal` is explicitly requested, honor it unconditionally — do the literal
  substring search, and if it's likely the caller meant regex (heuristic still useful here), keep
  that as a *warning only*, not a mode override. This restores the enum's mandatory-choice
  guarantee.
- If an auto-detect/auto-correct behavior is wanted, it should be its own explicit mode (e.g.
  `searchMode: auto`) rather than something `literal` silently degrades into.

## Real issue 2: domain-level tool failures aren't surfaced as MCP protocol errors

Confirmed via the log for the second `ApplyDiff` call:

```
16:02:57.950 [INF] method 'tools/call' request handler called.
16:03:00.776 [INF] "ApplyDiff" completed. IsError = false.
```

This call's actual JSON payload was `{"success":false,"error":{"errorCode":"Exception","message":"ApplyDiff pre-apply validate failed: [...]"}}`
(transcript line 391) — a real, well-formed failure. But the server's own log — and, more
importantly, the MCP `CallToolResult.IsError` field sent to the client — says `false`.

### Root cause

`[McpServerTool]`-attributed methods in this codebase return a plain `ToolResult<object>` /
`ToolResult<T>` C# object with a `Success` bool field. The MCP SDK sets the protocol-level
`isError` flag based on whether the .NET method **threw an exception**, not on any field inside
the object it returned. Since every tool in this codebase (by design — see
[[feedback_agent_friendly_error_messages]]) catches its own exceptions and returns
`Success: false` instead of throwing, **no domain-level failure from any tool in this codebase
ever sets `isError: true`** at the protocol level. This isn't specific to `ApplyDiff` — it's true
of every `ToolResult`-returning method in `SentinelWorkspaceTools.cs` and elsewhere.

### Why this matters

Some MCP clients/harnesses treat `isError` as the primary success/failure signal (e.g. rendering
it distinctly, or feeding it into retry/backoff logic) and treat the text content of a
`isError:false` response as just informational payload to parse if convenient. A model — or a
thinner client — that leans on that protocol-level flag rather than parsing the JSON body's own
`success` field will read every one of this server's domain failures as a success. This is the
most likely source of the "reported failure but state looked like success" perception even though,
in this specific transcript, the actual write behavior was correct — the *signal* the client saw
before it decided to look at the response body was `isError: false`, indistinguishable from a
real success at that layer.

### Suggested fix direction (not implemented)

- Set `IsError = true` on the returned `CallToolResult` whenever the tool's own `ToolResult.Success`
  is `false`, in whatever central place wraps/serializes these methods for the MCP SDK (a
  `ToolResult`-aware result mapper, if one doesn't already exist centrally) — so protocol-level and
  payload-level failure signaling agree.
- Audit whether this is a per-tool decorator/attribute question or a single choke point; given how
  many tools return `ToolResult<T>`, this should be fixable in one place rather than per-tool.

## Follow-up: run 2d review (`ReadFile`/`GetFileOutline` improvement confirmed; no new tool bug)

Reviewed at the user's request:
`RoslynSentinel-AgentTesting/RoslynSentinel NormalizeWhitespace Test - 9B - run 2d - 2026-08-29 00.31.md`
(and the raw exported JSON transcript, `1787947349101.conversation.json`, which was more reliable
than the markdown export for exact tool-call payloads), cross-referenced against
`RoslynSentinel.Server.Advanced/bin/Debug/net10.0/logs/http-host-20260828-231820.log`.

### The `ReadFile` → `GetFileOutline` fallback worked as intended

Confirmed directly: `ReadFile` on `RoslynSentinel.Basic/RefactoringEngine.cs` (4769 lines, 231014
bytes, over the 30720-byte threshold) returned a structured symbol outline instead of a bare
"too large" message. The outline listed, among ~90 other symbols,
`{"kind":"method","name":"ReplaceNodeFormattedAsync","container":"RefactoringEngine","startLine":56,"endLine":63}`
and `RemoveNodeFormattedAsync` (lines 69-86) by their real names. The model went straight to
`ReadFile(startLine:50, endLine:90)` and got both helpers verbatim on the first try — no repeated
guessing at wrong names like "FormatNode," which was the failure mode this change was meant to
fix. This is a clear, confirmed improvement.

### The `ApplyDiff` calls in this run: all four explained, none are new tool bugs

Four `ApplyDiff` calls total, all against `AdvancedStructuralEngine.cs`:

1. **`changesetFormat: diff`, two-hunk patch** (add a `using` line + rewrite the buggy method body
   + append two new helper methods) → `DiffApplyFailed`: hunk `@@ -38,10 +39,45 @@` declared line
   39, actual content not found within 60 lines. Root cause, confirmed against the pristine
   original file (`git show d8c6f82:...AdvancedStructuralEngine.cs`): the hunk's context lines
   (`var interfaceNode = ...` / `.WithModifiers(...)` / `.WithMembers(...);`) are real and correctly
   placed, but the model then emitted only `+` insertion lines for its replacement code and never
   marked the old lines it meant to replace — a blank line plus the 8-line
   `var newRoot = ...; return new DocumentEditResult { ... };` block — for **removal** (`-`). A
   correct hunk here needed `-` lines for that old block; instead the diff asks to *insert new code
   after* the three context lines while leaving the old code that follows completely unmentioned.
   Since `ReanchorHunk` requires a hunk's full context/removal sequence to match some contiguous
   span of the real file exactly, and this hunk's sequence (3 context lines immediately followed by
   `}` as if it were the very next line) never actually occurs contiguously anywhere in the file —
   the real next line after the 3 context lines is a blank line, not `}` — no search window, however
   large, could have found a match. This is not a line-number-offset problem the ±60-line search
   should have absorbed; it's a hunk body that doesn't describe any real span of the file. Verified
   the search window itself is not at fault: the file has only ever had one commit in this test
   repo (no intermediate edits between attempts), and the anchor text the error names does appear
   in the file, just not contiguously with what the hunk claims follows it. Correctly rejected,
   nothing written both times this hunk was submitted (confirmed: model re-read the file afterward
   and it was unchanged).
2. **`changesetFormat: diff`, single hunk** (just the `using RoslynSentinel.Basic;` line) →
   succeeded cleanly, file written, `validationResult: {success:true, diagnostics:[]}` genuinely
   correct here (adding a using directive can't break callers).
3. **`changesetFormat: files`, full corrected file content** → pre-apply validation correctly
   caught `CS1525`/`CS1002`/`CS1003` at line 428 (`Invalid expression term '<'`, `'/' `, `; expected`) —
   the model had pasted a malformed XML doc comment (missing `///` or a stray unescaped `<`/`/`)
   when copying the helper methods' doc comments from `RefactoringEngine.cs`. Correctly blocked,
   nothing written.
4. **`changesetFormat: diff`, identical two-hunk patch to call 1** (model retried the exact same
   hunk, still missing the `-` deletion lines for the old code block) → same `DiffApplyFailed`
   exception, but this time the
   request took **1,494,141 ms (~25 minutes)** end-to-end before the client (LM Studio) gave up and
   aborted the connection (log: `"The request was aborted by the client."` — the exception itself
   was thrown by the server at essentially the same instant the connection closed, so the server
   was not stuck retrying after the client left).

None of these four are tool defects — 1 and 4 are the identical genuinely-malformed hunk (missing
its `-` deletion lines, so no window size could have anchored it — correctly rejected both times),
2 is a correct success, 3 is a correct pre-apply validation catch.

### The ~25-minute stall on call 4 (documented, not a confirmed tool bug)

Investigated because a 25-minute wait for what should be a sub-second parse-and-fail is worth
explaining even though the *outcome* (rejection) was correct both times this diff was submitted.

- The exception is thrown from `SentinelWorkspaceTools.ApplyDiff` at
  [SentinelWorkspaceTools.cs:624](../../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L624)
  (`_diffEngine.ApplyDiff(oldText, unifiedDiff)`), caught immediately by the `catch` two lines
  below it — so all the wall-clock time was spent before or during that call, not in some later
  handler.
- `DiffEngine.ReanchorHunk`
  ([DiffEngine.cs:202-236](../../../../RoslynSentinel.Common/DiffEngine.cs#L202-L236)) is bounded — at
  most `HunkReanchorWindow` (60) iterations of a cheap list-slice comparison — so it cannot itself
  account for anywhere near 25 minutes.
- The one blocking call upstream of it is
  `GetCurrentSolutionAsync` ([PersistentWorkspaceManager.cs:924-936](../../../../RoslynSentinel.Common/PersistentWorkspaceManager.cs#L924-L936)),
  which does `await _solutionLock.WaitAsync(cancellationToken)` around a **trivial field read** —
  meaning if this call waited 25 minutes, something else was holding the single global
  `_solutionLock` for that entire window.
- Ruled out the file-watcher-triggered full reload path (`OnDebounceTimerElapsed`, which the
  in-code comment at
  [PersistentWorkspaceManager.cs:487-489](../../../../RoslynSentinel.Common/PersistentWorkspaceManager.cs#L487-L489)
  already documents as capable of holding the lock for "tens of seconds"): that method
  unconditionally logs `"Processing {Count} file system changes..."` when it runs
  ([PersistentWorkspaceManager.cs:579](../../../../RoslynSentinel.Common/PersistentWorkspaceManager.cs#L579)),
  and that line never appears anywhere in this session's log.
- The log shows **zero output from the entire process** (every log source, not just this request)
  for the full window between the request arriving (23:44:42) and the exception firing
  (00:09:37) — every other idle minute in this same log file has dozens of DBG-level connection
  lines. `systeminfo`/`wevtutil` show no reboot and no sleep/resume event in that window either.
- Best-evidence conclusion: this matches a debugger paused at a breakpoint/first-chance exception
  on the machine hosting the server (the user noted this possibility directly) more closely than
  any server-side deadlock — a debugger break would silently freeze the whole process with no log
  output, then resume instantly into the exact same deterministic exception the instant it's
  dismissed, which is exactly the observed pattern. Not treating this as a confirmed RoslynSentinel
  bug; noting it here only because a 25-minute unexplained silence is worth a paper trail, and
  because it's a useful reminder that a debugger left attached to the dev-facing server during a
  live dogfood run can make an unrelated call look like a hang.
