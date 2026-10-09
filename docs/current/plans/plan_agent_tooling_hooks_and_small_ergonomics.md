# Plan: harden the dog-food hooks, fix stale script signals, and close small tool-ergonomics gaps

**Status:** PARTIALLY IMPLEMENTED 2026-10-09. Steps 1-7 shipped; Steps 8 and 9 are DEFERRED pending human decisions (see Implementation notes at the end); Step 10 verification was run by the orchestrator (results in the notes). Priority P3. Ten steps (eight independent work steps, two conditional on a human decision, then verification); anything feature-sized is listed under Risks as needs-design.

## Problem

Source: 2026-10-08 digest (`.claude/journal/digest_20261008-1509.md`) plus this run's own tracing. Journal lines
are impressions; every claim below cites source read on 2026-10-08, and untraced items are labelled hypotheses.

**A. The dog-food hook has false positives and one real hole (`.claude/hooks/enforce-dogfood.ps1`).**
1. Whole-command regex scanning (header comment lines 22-31; `finding_dogfood_hook_matches_cs_in_scratchpad_commands.md`,
   status OPEN). The reader rule (lines 404-406) fires when `\.cs\b` appears anywhere AND a reader verb
   (`cat|type|more|head|...`) sits at a command position (`(^|[;&|(]\s*|\bxargs\s+)`); the git rule
   (lines 471-480) fires on `git status|log|diff|add|commit|revert` anywhere in the text. A PowerShell here-string or
   bash heredoc appending prose to a `.md` doc therefore trips either rule: prose such as "(more detail" after a
   `(` matches the `more` reader, and "git log" in prose matches the git rule. `51989d5e:L23`: "blocked a Bash
   heredoc appending to a .md blocker doc because the markdown text named C# files".
2. A negated Grep glob is read as targeting C#. Line 212: `$glob -match '\.cs["'']?$'` is true for `!*.cs`, which
   EXCLUDES C#. Hit live during this planning run (journal "known" item).
3. **Hole: shell writes to `.cs` are not blocked.** `$csReaders` (line 404) lists only readers. Nothing matches
   `Set-Content`, `Add-Content`, `Out-File`, `[IO.File]::WriteAllText`, `>`/`>>`, `Copy-Item`, `Move-Item`, `tee`.
   The digest shows the consequence: "x25 PowerShell by implementer" using `$content = @' namespace ...` to write C#
   (digest line 40), `a08be84f:L34` (SessionHalted after the Haiku implementer rewrote a test file by a non-MCP
   write), `47b2c93d:L16` (a BOM-only file left behind tripped the drift latch), `96b0b939:L17` ("the hook let it
   through"). The Edit/Write rule (lines 166-197) covers only the built-in tools. The environment let the
   weakest agent bypass the policy by switching tool; the brief wording ("no Write/Edit") cannot close that.
4. The implementer dispatch check (lines 264-314) enforces five labels and the 3-file limit but not the "Out of
   scope" content CLAUDE.md requires ("edit nothing outside the named symbols/branches ... reply RESCOPE:").
   `db2b9846:L8`: the implementer edited an out-of-brief branch to make a test pass.
5. `Read` on a `.cs` file is allowed: PreToolUse matcher (`.claude/settings.json:27`) does not include `Read`, and the
   hook has no `Read` branch. `0017ac93:L8`. CLAUDE.md states "nothing stops it".

**B. Stale script signals.**
6. `.claude/hooks/check-build-staleness.ps1:46` reads `$response.isServerBinaryStale` as a property of the
   `tool_response` object. The sibling hook `journal-log-call.ps1:69-76` documents the real shape: "MCP results arrive
   as {type:"text", text:"<json>"} (or an array of those)", and extracts `[string]$_.text`. If the real payload is a
   content-block array, the property is `$null` and the hook is a silent no-op. The test `friction-cases.Tests.ps1`
   FC7 (line 125-130) feeds a synthetic `@{ isServerBinaryStale = $true }` object, so it cannot catch this.
   `f768a4e8:L9` ("now a silent no-op"). Hypothesis on the live shape: derived from `journal-log-call.ps1`, not from a
   captured Build payload; Step 4 handles both shapes so it does not matter which is true.
7. `scripts/build.ps1:404`: the call `# Invoke-VSCodeServerRestart` is commented out, so the default run (no
   `-SkipVSCodeRestart`) silently leaves `bin-vscode\Advanced.Http` stale, and prints nothing. Only the
   `-SkipVSCodeRestart` path prints a warning (line 399). `a08be84f:L44`, `a08be84f:L56`.

**C. Hand-synced toolset checklist.** Adding a toolset needs five synchronized edits and no single place lists them
   (`50e0e6ac:L7`): `ToolSetName` and `ToolsetCatalog.ToolsBySet` / `Summaries` in
   `RoslynSentinel.Common/ToolsetCatalog.cs` (enum lines 8-14, maps lines 25-63), the hand-written description string
   in `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs:26`, and the golden `[TestCase]` list at
   `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs:53-56`. The golden tests caught each omission, but only
   after the fact and with messages that do not name the missing place.

**D. Small ergonomics.**
8. `ChangeAccessibility`: the model guessed `symbolName`/`newAccessibility` (`f768a4e8:L12`). The declared parameters
   are `targetName` and `accessibility` (`RefactoringSignatureTools.cs:57-68`). `newAccessibility` is already aliased
   (`ToolArgumentValidator.cs:382-385`); `symbolName` is not. The journal says the error "named the unknown params
   but not the correct ones"; current source does list them ("This tool accepts only: ...", `Validate`, unknown branch),
   so that part is already fixed or the binary was old.
9. Enum values are case-sensitive: `framework: "nunit"` is rejected with a "Did you mean 'NUnit'?" hint
   (`50e0e6ac:L12`). This is pinned deliberately: `TestCategoryToolSchemaTests.cs:136-147`
   (`Call_WithWrongCaseName_IsRejectedWithClosestSuggestion`, description "A wrong-case name is not silently
   repaired"). Parameter names, by contrast, ARE case-repaired (`NormalizeParameterCase`), and aliases are applied
   with a visible note. The asymmetry costs a round trip.
10. `CreateFile` cannot carry a body (`ebd9923b:L8`, `6406d612:L7`, `50e0e6ac:L6`). Its description
    (`WorkspaceFileEditTools.cs:80`) says "this tool never overwrites or writes free-form whole-file content" but names
    no alternative; the facade copy (`WorkspaceTools.cs:171`) says only "Creates a new file; fails if it already
    exists." `WriteFile(operation: CreateFile, content: ...)` already does exactly this
    (`WholeFileWriteTools.cs:30-39`), and is in the claude-lean allow-list (`ToolClassRegistry.cs:36`).

## Decision

- Fix the hook by removing the cause, not by adding more exemptions: scan a copy of the command with here-string and
  heredoc bodies removed; treat a negated glob as not targeting C#; deny shell writes to `.cs` (the same policy the
  built-in Edit/Write rule already states).
- Fix `check-build-staleness.ps1` to read both payload shapes, and add the shapes to its test.
- Make the dead `build.ps1` branch say what it is doing instead of staying silent; the re-enable-or-delete choice is
  the human's (decision 3).
- Turn the toolset checklist into code: a comment on `ToolSetName` plus a guard test that fails naming the place.
- Close the two cheapest tool-description/alias gaps (ChangeAccessibility alias, CreateFile pointer).
- Two steps are conditional on decisions 1 and 2 and are marked as such.

## Execution rules

- Hook and script steps edit non-C# files; use the normal file tools for `.ps1` and `.json`. C# edits (Steps 6-8)
  go through the MCP tools.
- Every hook change adds cases to the named test script and runs it:
  `pwsh -NoProfile -File .claude/hooks/enforce-dogfood.Tests.ps1` (59/59 passing at commit `41298ed`; the count has
  grown since) and `pwsh -NoProfile -File .claude/hooks/friction-cases.Tests.ps1`. Use a file-form test script, never
  an inline command, because test data containing `git ...` trips the hook itself (header comment lines 22-25).
- Hooks apply to subagents' tool calls too, which is what makes Step 2 effective against the implementer.
- Do not edit `CLAUDE.md` or `docs/current/TODO.md` in implementation; proposed text is under Risks.

## Steps

### Step 1 - Hook: ignore here-string/heredoc bodies; negated Grep glob
- Files: `.claude/hooks/enforce-dogfood.ps1`, `.claude/hooks/enforce-dogfood.Tests.ps1`
- Change:
  - Add near `Test-PathOutsideRepo` a function `Remove-HereStrings([string]$cmd)` returning `$cmd` with these removed
    (regex, single-line mode): PowerShell `@'...'@` -> `''`; PowerShell `@"..."@` -> `""`; bash heredoc
    `<<-?\s*['"]?(\w+)['"]?.*?\r?\n\s*\1\b` -> empty. Quoted single-line literals are NOT stripped (so
    `bash -c "git add ."` is still seen).
  - In the `Bash`/`PowerShell` branch, after the journal exemption (line 395) add `$scan = Remove-HereStrings $command`
    and use `$scan` instead of `$command` in: the reader test at lines 405-406, the `$uncovered` test at line 478,
    and the `$covered` test at line 480. Keep `$command` for the block messages, the out-of-repo `git -C` checks, and
    `Exit-IfBypassed` (they must see the original text).
  - Line 212: change to `if ($glob -and $glob -notmatch '^\s*!' -and $glob -match '\.cs["'']?$') { $targetsCs = $true }`.
  - Tests (add cases; `want` values in parentheses):
    1. PowerShell `Add-Content docs/x.md @'` + prose mentioning `Foo.cs`, `(more detail)` and `git log` + `'@` (allow).
    2. Same command plus a real `Get-Content C:\...\Foo.cs` outside the here-string (DENY).
    3. Bash `cat >> docs/x.md <<'EOF'` heredoc whose body names `Bar.cs` and `git commit` (allow).
    4. Grep `glob: '!*.cs'`, `path` a docs folder, `pattern: 'foo bar'` (allow); the existing `*.cs` cases stay DENY.
- Done when: `enforce-dogfood.Tests.ps1` exits 0 with all cases passing, including the four new ones.

### Step 2 - Hook: deny shell writes and moves of `.cs` files
- Files: `.claude/hooks/enforce-dogfood.ps1`, `.claude/hooks/enforce-dogfood.Tests.ps1`
- Change: in the `Bash`/`PowerShell` branch, directly after the reader rule (and using `$scan`), add a rule that denies
  when `$scan -match '\.cs\b'`, the command is not under `Worktree`, and any of:
  - a writer verb at a command position: `(^|[;&|(]\s*)(Set-Content|Add-Content|Out-File|Copy-Item|Move-Item|Rename-Item|New-Item|tee|cp|mv|copy|move|ren|sc|ac)(\s|$)`;
  - `\b(WriteAllText|WriteAllLines|WriteAllBytes|AppendAllText|AppendAllLines)\b`;
  - a redirection to a `.cs` target: `>{1,2}\s*["']?[^\s"'|;&]*\.cs\b`.
  Use `Exit-IfBypassed "$command`n$([string]$toolInput.description)" $null` then `DenyBypassable` with a message that
  names the MCP routes: `Member`, `ReplaceSnippet`, `ApplyDiff`, `WriteFile (operation: CreateFile or ReplaceFile)`,
  `CreateFile`, `MoveMember`, `DeleteFile`; and says that a missing capability (for example repairing line endings)
  is a finding to report, not something to do by shell.
  Tests (file form): `Set-Content -Path <repo>\Foo.cs -Value x` (DENY); `[IO.File]::WriteAllText('<repo>\Foo.cs', $c)`
  (DENY); `x | Out-File <repo>\Foo.cs` (DENY); `echo x > Foo.cs` (DENY); the same under `...\Worktree\...` (allow);
  `Set-Content notes.md 'Foo.cs'` is a known false positive of this rule and is asserted DENY with a comment pointing
  to the bypass keyword; `Set-Content x.csproj` (allow, `\.cs\b` does not match `.csproj`); a command carrying
  `# DeliberateHookBypass: raw bytes repro` (allow); a journal write under `.claude/journal/` (allow, existing
  exemption at line 395).
- Done when: `enforce-dogfood.Tests.ps1` exits 0 including the new cases, and the existing reader cases are unchanged.

### Step 3 - Hook: implementer brief must carry RESCOPE
- Files: `.claude/hooks/enforce-dogfood.ps1`, `.claude/hooks/enforce-dogfood.Tests.ps1`
- Change: in the dispatch branch (lines 264-314), extract the text from the `Out of scope` label match to the end
  of the brief. Add a problem line when it does not contain `RESCOPE`: "Out of scope must tell the implementer to
  reply RESCOPE: instead of editing anything outside the named symbols (CLAUDE.md slice contract)." Separately, when
  it names none of `Write`, `Edit` or `shell`, write a non-blocking stderr warning (same style as the ReplaceSnippet
  warning at lines 373-380) suggesting "no Write/Edit/shell writes on .cs files". Update the nine dispatch cases added
  by `ae03f13` so their fixture briefs include `RESCOPE`, and add one case with it missing (DENY).
- Done when: `enforce-dogfood.Tests.ps1` exits 0, including the new missing-RESCOPE case.

### Step 4 - check-build-staleness: read content blocks
- Files: `.claude/hooks/check-build-staleness.ps1`, `.claude/hooks/friction-cases.Tests.ps1`
- Change: replace lines 40-46 with the extraction used at `journal-log-call.ps1:74-76`: obtain `$text` as
  `$resp` when it is a string; else the joined `[string]$_.text` of `@($resp)`; and when `$resp` has a `content`
  property use that array. If `$resp` is a plain object with `isServerBinaryStale` keep honouring it. Then
  `if ($text -notmatch '"isServerBinaryStale"\s*:\s*true' -and $resp.isServerBinaryStale -ne $true) { exit 0 }`.
  Tests: keep FC7/FC7b; add FC7c (array of one `@{ type='text'; text='{"isError":false,"isServerBinaryStale":true}' }`,
  stderr must contain "older than the") and FC7d (the same wrapped in `@{ content = @(...) }`, same expectation),
  FC7e (text block without the flag stays silent).
- Done when: `friction-cases.Tests.ps1` exits 0 with FC7, FC7b-FC7e passing.

### Step 5 - build.ps1: say when the HTTP copy is not refreshed
- Files: `scripts/build.ps1`
- Change: in the `else` branch at lines 400-412, add before the commented block:
  `Write-Host "VS Code Advanced.Http copy was NOT refreshed (auto-refresh is disabled in this script); rebuild by hand if you use it: dotnet build -c Release -o bin-vscode\Advanced.Http" -ForegroundColor Yellow`.
  Do not uncomment or delete the existing block (decision 3).
- Done when: `pwsh -NoProfile -Command "[void][scriptblock]::Create((Get-Content -Raw scripts/build.ps1))"` exits 0 (the
  script still parses) and a `scripts/build.ps1 -Mode Build` run prints the new line.

### Step 6 - ToolsetCatalog: checklist comment and guard test
- Files: `RoslynSentinel.Common/ToolsetCatalog.cs`, `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs`
- Change (definition first, then test):
  - On `ToolSetName` (line 8) extend the XML summary with "Adding a toolset: (1) add the enum member here;
    (2) add it to `ToolsetCatalog.ToolsBySet`; (3) add it to `ToolsetCatalog.Summaries`; (4) add its name and tools to
    the `[Description]` of `McpToolsetControl` in `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs`; (5) add a
    `[TestCase]` to `Enable_AddsTheWholeSet_AndAccountsForEveryName` in
    `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs`; if the tools live in a new class, also list the class in
    `ToolClassRegistry.ClaudeLeanOnDemandToolClasses`."
  - Add test `Catalog_EverySetIsRegisteredAndDescribed` in `McpToolsetControlTests`: for each
    `Enum.GetValues<ToolSetName>()` assert `ToolsetCatalog.ToolsBySet` has the key, `ToolsetCatalog.Summaries` has the
    key, and the `DescriptionAttribute` text of `typeof(ToolsetControlTools).GetMethod("McpToolsetControl")` contains
    `set.ToString()`. Each assertion message names the missing place ("add <set> to ToolsetCatalog.Summaries").
    First confirm with `Search` that the class is `ToolsetControlTools` and the method `McpToolsetControl`
    (`ToolsetControlTools.cs:26` shows the description; the declaring type name is read from that file).
- Done when: `Build` 0 errors and `RunTest` filter `FullyQualifiedName~McpToolsetControlTests` passes.

### Step 7 - ChangeAccessibility alias and CreateFile pointer
- Files: `RoslynSentinel.Server.Basic/ToolArgumentValidator.cs`, `RoslynSentinel.Tools.Basic/WorkspaceFileEditTools.cs`,
  `RoslynSentinel.Tools.Basic/WorkspaceTools.cs`
- Change (data/description only, no signatures):
  - `ParameterAliases["ChangeAccessibility"]` (line 382): add `["symbolName"] = "targetName"` next to `newAccessibility`.
    The table-driven test `ToolParameterAliasTests.EveryAliasEntry_ForAnActiveTool_PointsAtADeclaredParameter_AndIsNotItselfDeclared`
    (line 185) validates every entry against the live schema.
  - `CreateFile` `[Description]` at `WorkspaceFileEditTools.cs:80` and `WorkspaceTools.cs:171`: append
    " For a new file with its full content in one call, use WriteFile(operation: CreateFile, content: ...)."
- Done when: `Build` 0 errors; `RunTest` filter `FullyQualifiedName~ToolParameterAliasTests` passes; and
  `ArchitectureDocFreshnessTests` passes (if the tool table embeds descriptions it will fail; then regenerate with
  `scripts/Generate-ArchitectureMap.ps1` and include `docs/generated/architecture_tools.md` in the change).

### Step 8 - (CONDITIONAL on decision 2) Enum value case repair with a visible note
- Files: `RoslynSentinel.Server.Basic/ToolArgumentValidator.cs`, `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`,
  `RoslynSentinel.Tests.Server/TestCategoryToolSchemaTests.cs`
- Change (one `ReplaceSnippet` batch, definition first):
  - Add `public static IReadOnlyList<string>? NormalizeEnumCase(McpServer? server, string? toolName, IDictionary<string, JsonElement>? arguments)`
    in `ToolArgumentValidator`: for each string argument whose property schema has an `enum` array (use
    `TryGetSchemaParameters`' `propertySchemas`), when the value is not an exact member but equals exactly ONE member
    ignoring case, replace it with that member and add a note `'nunit' -> 'NUnit' for parameter 'framework'`.
    Two case-insensitive matches, or no match, leave the value for `Validate`.
  - In `AddArgumentValidationFilter` (`ServiceRegistrationExtensionsBasic.cs:927-936`) call it after
    `ApplyParameterAliases` and merge its notes into `aliasNotes` the way `wrapNotes` is merged.
  - Test: replace `Call_WithWrongCaseName_IsRejectedWithClosestSuggestion` (line 136) with
    `Call_WithWrongCaseName_IsRepairedAndNoted` asserting no "not a valid value" text and a trailing note naming
    `NUnit`; keep `Call_WithUnknownName_IsRejectedListingValidValues` unchanged.
- Done when: `Build` 0 errors and `RunTest` filter `FullyQualifiedName~TestCategoryToolSchemaTests` passes.

### Step 9 - (CONDITIONAL on decision 1) Block `Read` of in-repo `.cs`
- Files: `.claude/settings.json`, `.claude/hooks/enforce-dogfood.ps1`, `.claude/hooks/enforce-dogfood.Tests.ps1`
- Change: add `Read` to the PreToolUse matcher string at `settings.json:27`. In the hook add a branch before the Grep
  branch: `if ($toolName -eq 'Read')`; take `file_path`; if it ends in `.cs`, is not under `Worktree`, and
  `-not (Test-PathOutsideRepo $filePath)`, then `Exit-IfBypassed '' $filePath` and `DenyBypassable` naming
  `ReadFile`, `GetFileOutline`, `GetMethodSource`. Tests: in-repo `.cs` Read (DENY), out-of-repo `.cs` (allow), a `.md`
  (allow), `.cs` under `Worktree` (allow).
- Done when: `enforce-dogfood.Tests.ps1` exits 0 with the new cases.

### Step 10 - Verify
- Files: none.
- Change: `Build` (0 errors); `RunTest` at solution scope compared with the known-failure baseline, new failures only;
  run both hook test scripts once more; then `McpServerControl(operation: StopServer, confirmServerStop:
  ConfirmServerStop)` and `LoadSolution` so tools run the new binary.
- Done when: no new failures versus baseline, both hook scripts exit 0.

## Out of scope

- The needs-design items in Risks (items 4-9).
- Any change to `CLAUDE.md` text (proposed wording is in Risks).
- Re-enabling or deleting the HTTP-copy restart machinery in `scripts/build.ps1` (decision 3).
- The duplicate-exemption refactor of the hook into a table-driven rule engine.

## Risks and open decisions

1. **Decision (Step 9): block `Read` on in-repo `.cs`?** Pro: closes the last non-MCP read route that CLAUDE.md calls
   a violation but does not stop (`0017ac93:L8`). Con: any agent without ReadFile available (a restricted subagent
   toolset) loses its only C# read; mitigated by the bypass keyword/token. Recommended yes.
2. **Decision (Step 8): repair enum case silently-with-a-note?** The existing test deliberately pins rejection
   ("not silently repaired"). Repair-with-note is consistent with parameter-name case repair and alias notes, and
   removes a round trip for weak models; the cost is one more rewriting layer in the validation filter and a
   rewritten test. Recommended yes, because the rewrite is announced rather than silent.
3. **Decision (Step 5): the HTTP copy.** `build.ps1` still carries a full restart function
   (lines 306-381) that is never called. The agent memory says "stdio is primary, Http is fallback-only", and
   `a08be84f:L54` drove moveExtract tools through an HTTP script. Either re-enable `Invoke-VSCodeServerRestart`
   and the status check (restores auto-refresh, costs build time and a port-5150 process) or delete the dead block and
   the `-SkipVSCodeRestart`/`-VSCodePort` parameters. Step 5 only makes the current state visible.
4. *Needs design* - **MethodSignature/ParameterEdit `remove` with a call site in the edited file**
   (`47b2c93d:L39`). Traced: `MemberRefactoringEngine.cs:1603` (`RemoveMethodParameterAsync`) refuses with "call site
   at ... could not be re-located after an earlier edit to the same file"; `ParameterEditToolTests.cs:90` records it
   as "the original tool's behavior". The fix is to apply same-file call-site edits in one pass against one syntax
   tree (or re-resolve by symbol after each edit); it changes the engine's edit loop, so it needs a design and a
   regression matrix.
5. *Needs design* - **MoveMember of instance members into a static class** (`51989d5e:L18`). No handling found
   (`Search` for `CS0708`/`makeStatic` in the MoveMember engine files returned only the class name
   `MoveMemberTextEdits`); hypothesis: instance members are moved verbatim and the compile gate reports CS0708. Needs
   a decision on a `makeStatic` option versus a refusal that names it, and on how `this` references are handled.
6. *Needs design* - **RenameSymbol merge into an existing same-signature name** (`51989d5e:L28`): the refusal
   (CS0111/CS0121) is correct; a merge operation is a new feature (conflict policy, body reconciliation).
7. *Needs design* - **RunTest does not surface `TestContext.Out`** (`47b2c93d:L31`, `L36`). Traced: `TestRunEngine`
   (`RoslynSentinel.Engines.Basic/TestRunEngine.cs:268-290`) runs `dotnet test` with a TRX logger and keeps only a
   `StdoutTail`, which `WithoutTailsWhenClean` (`WorkspaceBuildTestImpl.cs:125-139`) drops on a clean run; `Search`
   for `StdOut` in the engine projects finds no per-test output parsing, so TRX `<StdOut>` text is never read.
   An `includeOutput` parameter changes the public schema (token cost, schema-diet policy); needs a design.
8. *Needs design* - **raw bytes / line-ending inspection** (`50e0e6ac:L18`, `96b0b939:L7`, `51989d5e:L12`): no tool in
   `RoslynSentinel.Tools.*` reports CR/LF counts or BOM (`Search` for `crlf|BareLf|HasBom|LineEnding` returned no
   match). Candidate: additive `lineEndings` and `hasBom` fields on the ReadFile envelope, or the
   `FileNormalizationEngine` dry-run in the existing `plan_file_normalization_tool.md`. Decide which owns it before
   building a new tool; Step 2 makes the shell route harder, so this gap gets more visible.
9. Hypothesis, untraced: `51989d5e:L12`/`96b0b939:L7` suggest agents read bytes to settle EOL arguments; the
   write-chokepoint now reports per-file changed-line counts (commit `389ae1b`), which may remove most of the need.
10. Proposed `CLAUDE.md` edit for the human (not made here): in the slice-contract "Out of scope" line add "no
    Write/Edit/shell writes on .cs files". Step 3 only warns on its absence.
11. Dropped as already handled or stale: McpServerControl confirm parameter (`ebd9923b:L10`; restored as a one-value
    enum in `45f3170`, described at `AdminTools.cs` "only value: ConfirmServerStop", and the refusal names parameter
    and value); ChangeAccessibility "error did not name correct parameters" (current `Validate` lists them);
    UndoLastApply EOL refusal (fixed in `30bc781`); WriteFile create-missing (`a506102`).
12. Noise or one-off, not planned: `47b2c93d:L25` (SDK list_changed), `630f0332:L7` (Connection closed after stop is
    client-side), `ebd9923b:L14` (client caches schema across a restart; client behaviour), `RunTest` project-scope
    probing time (`a08be84f:L15`, `L25`; a perf item, deferred).

## Implementation notes (2026-10-09)

- Steps 1-5 (commit 347e0da5153e18daaacd816958c0d46c892fadf5): hook and script changes. `enforce-dogfood.Tests.ps1` 116/116,
  `friction-cases.Tests.ps1` 17/17. Deviations: the reader/writer rules treat a newline as a command position (the plan's
  "real read after a here-string" case needed it); the hook header comment was updated; extra cases added (FC7f JSON string
  payload, here-string plus real git, RESCOPE present but no Write/Edit/shell warning). Which Build payload shape the host
  really sends is still unconfirmed; all four shapes are handled and tested.
- Step 6 (commit ec31e84f9264b4c5b2060c7f140a175dba2379c0): checklist comment on `ToolSetName`. The guard test
  `Catalog_EverySetIsRegisteredAndDescribed` reads the private `ToolsBySet` and `Summaries` fields by reflection and names
  the missing place. The description check in the plan was already covered by the existing
  `Catalog_EveryToolIsDeclared_AndTheDescriptionNamesEverySetAndTool`, so it was not duplicated.
- Step 7: `ChangeAccessibility` alias `symbolName -> targetName`; `CreateFile` descriptions (both copies) point at
  `WriteFile(operation: CreateFile, content: ...)`. `ArchitectureDocFreshnessTests` passes, so no docs/generated change.
- DEFERRED (judgement calls left to the human): Step 9 (block built-in `Read` of in-repo `.cs`; a restricted subagent loses
  its only C# read); Step 8 (enum case repair; conflicts with the pinned test `Call_WithWrongCaseName_IsRejectedWithClosestSuggestion`);
  the CLAUDE.md "Out of scope" wording edit (Risks item 10; Step 3 warns on its absence in the meantime).
- Decision 3 (HTTP copy in `scripts/build.ps1`) unchanged: Step 5 only made the state visible.
