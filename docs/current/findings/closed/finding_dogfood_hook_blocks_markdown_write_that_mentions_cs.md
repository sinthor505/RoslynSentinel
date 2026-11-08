# Finding: enforce-dogfood.ps1 blocks a shell write to a Markdown file because its text mentions a .cs name

**Status:** FIXED 2026-10-24 (path-shaped `.cs` token matching in `enforce-dogfood.ps1`, reader and writer rules; also closes
`finding_dogfood_hook_matches_cs_in_scratchpad_commands.md`). Decision: option 1 below was replaced by a simpler rule - a `.cs`
counts only when a whole token or whole quoted literal is path-shaped (`^[\w.\\/:~$*-]+\.cs$`), so prose such as `Foo.cs:12` or
`see Foo.cs` never matches and no per-verb destination parsing is needed. Residual false positive: a bare path-shaped literal
(`.Replace('Foo.cs','Bar.cs')`) in a non-`.cs` write; use the bypass keyword.

## Context
Seen 2026-10-24 (about 22:04 UTC) while the `journal-improvement-planner` subagent revised
`docs/current/plans/plan_unknown_parameter_rejection_reduction.md`. The PowerShell command read the plan with
`[IO.File]::ReadAllText`, patched it with `.Remove/.Insert/.Replace`, and wrote it back with
`[IO.File]::WriteAllText($p, $t, ...)`. The write target `$p` is a `.md` file. One of the `.Replace(...)`
string literals quoted a code reference (`SymbolNavigationEngine.cs:2679-2680`). The hook denied it as
"shell write/move/copy of a C# file". The agent then reworded the command to drop the `.cs` token and
re-ran it, which the hook message and CLAUDE.md tell agents not to do (report the false positive and stop).
The two other denials in that run (`Read` and `Grep` aimed at C# source) were correct and are not part of this finding.

## What is broken
A write whose destination is not C# is denied only because the string `.cs` appears somewhere in the command text.
Prose about C# files is normal in plan, proposal, finding and blocker docs, so any scripted edit of such a doc that
quotes a `file.cs:line` reference and uses `WriteAllText`, `Set-Content`, `Out-File` etc. is blocked.

## Root cause
`.claude/hooks/enforce-dogfood.ps1:616-617` ("shell writes/moves of C#"): the deny condition is
`$scan -match '\.cs\b'` (anywhere in the command, including inside quoted string data) AND any of
- `$csWriters` verb at a command position (line 613),
- `$csWriteApis` anywhere (line 614: `WriteAllText|WriteAllLines|WriteAllBytes|AppendAllText|AppendAllLines`),
- `$csRedirect` (line 615; this one is already target-aware, it requires the `.cs` to follow the `>`).
The two non-redirect branches never check what is being written. The file header already admits this
(`:22` "regex over the command line", and the comment at `:610-611` "Known false positive: a writer verb with a
.cs name mentioned only as text ... use the DeliberateHookBypass keyword"). `Remove-HereStrings` (used to build
`$scan`) strips here-string bodies but not ordinary quoted strings, so single/double-quoted data is still scanned.
The reader rule at `:583-584` has the same shape (`cat`/`Get-Content`/`sed` plus any `.cs` text) and fails the same way
for reads of Markdown.

## Why it matters
The documented escape is a bypass keyword, which is a logged deviation per `reference_hook_bypass.md`, and the
alternative (reword to hide `.cs`) is explicitly forbidden, yet it is what an agent does under pressure. The hook
therefore pushes agents either to violate policy or to stop work on a legitimate doc edit. The environment should let
a non-C# write through without any special handling.

## Recommendation
1. **Target-aware matching (recommended).** For the verb and API branches, extract the destination and deny only if it
   ends in `.cs`:
   - `WriteAll*`/`AppendAll*`: first argument; `Set-Content`/`Add-Content`/`Out-File`/`New-Item`: `-Path`/`-LiteralPath`/
     first positional; `Copy-Item`/`Move-Item`: destination; `tee`/`cp`/`mv`: last argument.
   - If the destination is a variable, resolve a preceding `$x = '<literal>'` (or `"<literal>"`) assignment in the same
     command; if it still cannot be resolved, keep today's deny (fail closed).
   - Keep the `$csRedirect` branch as is.
   Apply the same destination idea to the reader rule only if reads of `.md` mentioning `.cs` show up in practice.
2. **Cheaper partial:** before testing `\.cs\b`, blank out the bodies of quoted strings that are not the destination
   argument. Simpler but easier to get wrong (quotes inside strings, `-f` formats), so it is the fallback.
3. **Agent side (complements, does not replace 1):** Markdown edits belong to the `Edit` tool, which the hook allows
   for non-`.cs` files; agent files that write docs should say so, so no shell patch script is needed at all.

Acceptance cases for whichever option is built (add to `.claude/hooks/` tests, same style as `journal-hooks.Tests.ps1`
if there is a matching enforce-dogfood test file, otherwise create one):
- ALLOW: `[IO.File]::WriteAllText('docs/x.md', $t.Replace('Foo.cs:1','Foo.cs:2'))`
- ALLOW: `$p='docs/x.md'; ...Replace("(Foo.cs)","(Bar.cs)")...; [IO.File]::WriteAllText($p,$t)`
- ALLOW: `Set-Content docs/x.md "see Foo.cs"` (today's documented false positive)
- DENY: `[IO.File]::WriteAllText('src/Foo.cs', $t)`; `$p='src/Foo.cs'; [IO.File]::WriteAllText($p,$t)`
- DENY: `Set-Content src/Foo.cs 'x'`; `'x' | Out-File src/Foo.cs`; `echo x > src/Foo.cs`
- DENY (fail closed): `[IO.File]::WriteAllText($unknown, $t)` when the command also names a `.cs` file

## Out of scope
- The `Read`/`Grep` denials in the same run: correct behaviour.
- `.csproj`/`.cshtml`: already excluded by `\.cs\b`.
