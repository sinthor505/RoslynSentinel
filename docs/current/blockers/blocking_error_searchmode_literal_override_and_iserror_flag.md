# Blocking errors found while investigating a reported ApplyDiff false-failure

**Status:** the specific symptom the user asked me to investigate (`ApplyDiff` reported failure
but a following `ReadFile` showed the file had changed) turned out to be fully explained by the
model's own actions — not a tool bug. But investigating it surfaced three real, separate issues:
a validation-scope gap that let the model's own destructive first `ApplyDiff` call report
`success:true` with zero diagnostics despite gutting a whole class, a live regression in
`SearchSolutionText`'s new `searchMode` parameter, and one systemic MCP-protocol-level gap
affecting every tool that returns `Success: false` without throwing. Documenting all three per the
dog-fooding policy — no fix attempted. Issue 0 (validation scope) is the most serious of the
three: it means `validateOnApply: true` (the default, and the tool's main safety net) does not
actually protect against a large, common class of real mistakes.

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
   ([SentinelWorkspaceTools.cs:400](../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L400)),
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
([SentinelWorkspaceTools.cs:401](../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L401)),
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

[ValidationEngine.cs:103-206](../../../RoslynSentinel.Common/ValidationEngine.cs#L103-L206)
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
[SentinelWorkspaceTools.cs:1418](../../../RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs#L1418).

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
