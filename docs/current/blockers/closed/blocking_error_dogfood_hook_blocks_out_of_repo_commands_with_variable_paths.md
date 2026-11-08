# `enforce-dogfood.ps1` blocks read-only commands on another repo when the path is in a variable, and names a tool that cannot reach that repo

**Status:** RESOLVED 2026-11-05. See Resolution.

## Resolution

**Root cause:** a path in a variable was captured as the literal text `$r`, which counts as "cannot tell", so the out-of-repo exemption
was skipped. Separately, the reader rule matched a reader verb and a `.cs` token anywhere in the command, across unrelated statements.

**Fix** (the commit that moved this file to `resolved/`), in `.claude/hooks/enforce-dogfood.ps1`:

- `Expand-SimpleAssignments` substitutes `$name`, `${name}` and Bash `"$name"` in the scanned copy only, when the command assigns that
  name exactly once, as its own statement, to a literal. Anything else stays "cannot tell". Block messages keep the original command.
- `Get-CsReaderHit` evaluates the reader rule per statement, splitting on `;`, `&&`, `||` and newlines outside quotes and outside
  `(...)` and `{...}`. A pipeline is one statement.
- A statement with a `.cs` token marks every variable it names, including `-OutVariable` and `-PipelineVariable` values, so a later
  read of one is still denied.
- The git block message now names the literal `git -C` route, and CLAUDE.md says that route is not rewording.

**Tests:** `.claude/hooks/enforce-dogfood.Tests.ps1` passes 211 of 211, up from 190. Beyond the cases suggested below, it adds deny
cases for `$f = Get-ChildItem *.cs; Get-Content $f`, `-OutVariable`, `-ov`, a `+=` inside `ForEach-Object`, and a `foreach` over `*.cs`.

**Verified live:** the variable-path `git -C` command and the licence read plus `.cs` count both ran against the ILSpy clone.

**Known over-block, kept on purpose:** any variable named in a statement with a `.cs` token counts as holding C#, even one for another
repo, so `$f = gci <other-repo> -Filter *.cs; Get-Content $f` is denied.

The sections below are the original report. The source lines they cite are from before the fix.

## What was being attempted

Reviewing the ILSpy clone at `C:\Users\Administrator\source\repos\ILSpy`, outside this repo, for
`docs/current/proposals/proposal_ilspy_derived_capabilities.md`. Two read-only commands were blocked.

Checking whether an API exists at the `v11.2` tag (abridged to the first of several `git` calls on one line):

```powershell
$r='C:\Users\Administrator\source\repos\ILSpy'; git -C $r log -1 --format='HEAD %h %ad' --date=short; ...
```

Listing the clone, reading its licence text file and counting C# files (abridged):

```powershell
$r='C:\Users\Administrator\source\repos\ILSpy'; ... Get-Content "$r\doc\license.txt" ...; ... Get-ChildItem ... -Filter *.cs ...
```

A review subagent separately hit a block it described the same way: `Get-Content` alongside a `*.cs` mention.

## The exact symptom

```
PreToolUse:PowerShell hook error: [command hook 1]: BLOCKED by dog-fooding policy: git via shell.
...
status, log, diff, stage/add, commit (incl. amend) and revert are covered by the MCP Git tool; tag, stash and worktree by the MCP GitRemote tool:
...
If the Git tool fails or returns wrong data, that is a BLOCKING finding: stop,
write docs/current/blockers/blocking_error_<slug>.md, and end the turn.
```

```
PreToolUse:PowerShell hook error: [command hook 1]: BLOCKED by dog-fooding policy: 'Get-Content' via shell on C# source.
```

The first message sends the caller to a tool that cannot do the job: the MCP Git tool "only ever operates on whichever
solution is currently loaded" (`.claude/hooks/enforce-dogfood.ps1:867`, by the hook's own comment). The second says the
command reads C# source, which it does not.

Running the hook directly on saved payloads, with exit 2 meaning blocked:

| # | Command shape | Exit | Correct? |
| --- | --- | --- | --- |
| 1 | `$r='C:\...\ILSpy'; git -C $r log -1` | 2 | No |
| 2 | `git -C C:\...\ILSpy log -1` | 0 | Yes |
| 3 | `$r='C:\...\ILSpy'; Get-Content "$r\doc\license.txt"; Get-ChildItem $r -Recurse -Filter *.cs` | 2 | No |
| 4 | `Get-Content C:\...\ILSpy\doc\license.txt; Get-ChildItem C:\...\ILSpy -Recurse -Filter *.cs` | 2 | No |

Cases 1 and 2 differ only in the variable. Case 4 shows the second defect does not depend on the variable.

## Source trace

Line numbers are for `.claude/hooks/enforce-dogfood.ps1` and include the two `# TODO` comments added with this writeup.

**Defect 1: a path in a variable is not resolved.**

- `Get-CommandLocations` (`:200`) captures the text after `git -C`, so case 1 yields the literal string `$r`.
- `Test-AllLocationsOutside` returns false when a location is not rooted (`:256`), per the rule that "cannot tell" keeps
  enforcing (`:184`).
- The out-of-repo exemption is then skipped, and the command reaches the normal git rule.

This matches the stated design, so the fail-closed choice is deliberate. The defects are that a simple same-line assignment
is treated as unknowable, and that the message does not reveal the out-of-repo exemption exists.

**Defect 2: the reader rule matches across unrelated statements.**

- The rule needs a `.cs` token anywhere in the command, plus a reader verb at any command position (`:801-802`).
- In cases 3 and 4, `Get-Content` reads a `.txt` file and `*.cs` is a `Get-ChildItem -Filter` value, in separate statements.
- The comment at `:794` says "Listing alone (Get-ChildItem *.cs for mtimes) is left alone", but putting a listing on the same
  line as an unrelated read defeats that.
- `Test-ShellCsTargetsOutsideRepo` cannot rescue case 4, because the `*.cs` token is relative and the command has no explicit
  location of the kinds it collects (`:206-209`). An absolute path argument to `Get-ChildItem` is not one of them.

**Test coverage.** `.claude/hooks/enforce-dogfood.Tests.ps1:336-341` covers `git -C` outside the repo, but builds each command
in a double-quoted string, so `$env:TEMP` is already expanded and the hook sees a literal path. No case covers a variable
held in the command. No case seen covers a read and a listing as separate statements, though that file was only searched.

## What is and is not confirmed

Confirmed:

- All four exit codes above.
- The source path for each defect.
- A literal-path form is exempt by design, which also explains why
  `git -C C:\Users\Administrator\source\repos\ILSpy describe --tags --abbrev=0` passed earlier in the same session.

Not confirmed:

- Other variable forms (`$env:X`, `${r}`, double-quoted assignment, or a Bash `r=...`) were not run. They are expected to
  behave the same way.
- The exact command the subagent ran was not captured.
- Running the payloads through the hook may have added three test rows to its block log (cases 1, 3 and 4). The log was not
  inspected.

## Why this matters

The hook's instructions and CLAUDE.md together leave no compliant route: the message names a tool that cannot reach the repo,
and CLAUDE.md says to stop and not reword. So a harmless read of another repo costs the task. Here it left a question open in
the proposal: whether `FindEntity(string, IReadOnlyList<MetadataFile>)` exists at `v11.2`.

An agent that follows the guidance gives up on the read. An agent that happens to retry with a literal path succeeds, but cannot
tell that this is a designed exemption rather than slipping past the hook, which is what CLAUDE.md forbids.

## Suggested direction (not implemented)

1. **Resolve simple assignments.** Before `Get-CommandLocations`, substitute `$name` and `${name}` where the same command
   assigns that name once, to a quoted or bare literal. Anything else stays "cannot tell". This also means a variable holding
   an in-repo path is still enforced.
2. **Say what is exempt.** Add to the git block message: "The Git tool only sees this repo. For a read-only command on another
   repo, pass its absolute path literally after `git -C`."
3. **Evaluate per statement.** Split on `;`, `&&`, `||` and newlines outside quotes, then require the reader verb and the `.cs`
   token in the same statement. A pipeline stays one statement, so `Get-ChildItem *.cs | Get-Content` is still caught.
4. **Add test cases** to `enforce-dogfood.Tests.ps1` in file form, with the four commands above plus these two: a variable
   holding an in-repo path (DENY), and `Get-Content Foo.cs; Get-ChildItem` (DENY).
5. **Update CLAUDE.md** to say a literal absolute path to another repo is an allowed route, not rewording.

`# TODO` comments mark both sites in `enforce-dogfood.ps1`.
