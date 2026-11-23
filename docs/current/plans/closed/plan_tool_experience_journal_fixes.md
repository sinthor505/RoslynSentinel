# Plan: tool-experience journal fixes (R1-R25 and owner decisions D1, D2, D4-D7)

**Status:** IMPLEMENTED 2026-11-15. All steps done (6bf9025b..54d071e8, ratchet fixes 5bd7c82e, bedc6f11) except Step 32, moved to TODO.md by owner ruling.

## Problem

`/journal-review all` on 2026-11-14 rolled up 582 tool-experience journals (3778 failed MCP calls, 1256
C#/shell fallbacks). The investigation on 2026-11-15 traced every theme against source and found 25 small
fixes with no owner decision (R1-R25) and 12 owner decisions (D1-D12). Evidence, root causes and the
"already fixed" list live in the source finding, section "Investigation results (2026-11-15)":
[finding_tool_experience_journal_rollup_pain_points.md](../../findings/closed/finding_tool_experience_journal_rollup_pain_points.md).
Per the failure doctrine each item below is an environment defect (a hook, hint, schema, tool behaviour or
doc that let a weak model fail), not a model-competence issue.

Verification at HEAD on 2026-11-15 (every step below was re-read against source; anchors are symbol names
because line numbers drift). Claims in the finding that source did not bear out are recorded here, not
silently adopted:

- **R22 is contradicted.** `ResidualMentionScanner` (Engines.Basic) already skips `bin`, `obj`, `.git`,
  `.roslynsentinel`, `Worktree`, `node_modules` and reads only git-tracked non-source files; tests
  `ScanAsync_SkippedDirectoriesGeneratedFilesAndExtensions_AreNotReturned` and
  `ScanAsync_GitRepo_IgnoredAndUntrackedNonSourceFiles_AreNotReported` pin that. Only the fallback walk
  (not a git repo, or nothing tracked) still includes untracked files. Likely a stale-binary report. Step 23
  is verify-then-close.
- **R15 "names one pid at a time" is contradicted.** `BuildIsolation.DescribeCompetingBuilds` lists up to 3
  pids and "and N more". The probe-counts-a-peer's-isolated-RunTest half holds: `IsCompetingBuild` ignores
  `-p:ArtifactsPath=`, which `TestRunEngine.RunOneProjectAsync` adds through `BuildIsolation.ApplyArtifactsPath`.
- **R16 is half right.** `NotFoundEvidence` already checks term presence (`TermHeld(BeforeSource, term,
  isRegex)`) and already says "in another session". The real gaps are (a) the search scope is not passed to
  the evidence (observed live 2026-11-15: a `Search` limited to `RoslynSentinel.Tests.*/**/*.cs` returned
  `EditAttribution` findings naming `WorkspaceFileEditImpl.cs` and `McpToolSchemaPatcher.cs`, which the
  search never covered) and (b) no session id appears in `EditAttributionFindings.ForPaths` wording
  (`EditAttribution` has `AgentId`/`AgentType` but no session field).
- **Theme 7 "newContent limit absent from the schema" is already fixed**: `McpToolSchemaPatcher.ApplyReplaceSnippetLimits`
  patches `oldContent`/`newContent` limits. Only the 20-edit batch cap (R8) is missing.
- **D5:** `nameOnly` is documented "diff/show" and `GitTool` does not pass it to `GitImpl.LogAsync`, so on `log`
  it is silently ignored today (not rejected).
- **D6:** the server never sends `tools/list_changed` itself; the SDK raises it when `ToolCollection` changes
  (`ToolsetService` doc comment). A "send count" has no send site to count at.
- **D7:** `LaunchTestInstance` is an operation of `McpServerControl` (`AdminTools.ControlTestInstancesAsync`
  -> `TestInstanceLauncher.LaunchAsync`), not a tool. `ToolClassRegistry` lives in `Server.Advanced`, which
  `Tools.Basic` and `Engines.Basic` cannot reference. An unknown `includeTools` name is silently kept by
  `ServerStartupHelpers.NormalizeToolClassNames` and matches nothing.
- **R8:** `MaxSnippetEditsPerBatch` is a `private const` in `WorkspaceFileEditImpl` (Tools.Basic); the patcher
  is in `Common`, so the constant must move to `ReplaceSnippetOptions` (Common).
- **R25:** 13 ratchet-style classes, not "11+" (12 `*RatchetTests` plus `ClaudeLeanSchemaBudgetTests`).
- **R13:** three more sites pass `validated: false` after the gate (`ModifyModifierBatch`,
  `ModifyAttributeBatch`, `ModifyBaseTypeBatch`); they are not in R13 and get a `// TODO` only.
- Line drift: R11 site moved to `CompilerErrorLookupHelper.DescribeCs0103Async` (~line 428, finding said 384);
  R23 `ReadFileWithoutSolutionAsync` is at ~578 (finding said 560); R14 descriptions are at ~178-181.
- **Environment note:** every tool response in this drafting session carried `workspaceStale: true` ("reload
  of 'RoslynSentinel.sln' failed: Solution file not found") although the repo's solution is `RoslynSentinel.slnx`
  and reads were correct. Not traced; if it is real it is a defect of the stale-workspace signal. Out of this plan.

## Decision

Owner approved 2026-11-15:

- R1-R25 all approved as written in the finding's table (with the verified corrections above).
- D1 ChangeSignature supports primary constructors (record positional property policy still to settle:
  open decision O1). D2 Member replace accepts several declarations (proposal option 2,
  [proposal_member_accepts_multiple_declarations.md](../../proposals/proposal_member_accepts_multiple_declarations.md)).
  D4 ReadFile and GetFileOutline auto-resolve a unique bare file name (behaviour only, no new Search mode).
  D5 Git `log` with `nameOnly` returns hash + subject. D6 McpServerStatus adds `clientProtocolVersion` and a
  `tools/list_changed` count. D7 `McpServerControl(LaunchTestInstance)` gets a `toolsets` param and validates
  `includeTools` names against `ToolClassRegistry`; an `environment` param is not approved.
- Not approved, out of scope pending owner: D3, D8, D9, D10, D11, D12. (D12 was later built by
  [plan_tab_scoped_toolset_persistence.md](plan_tab_scoped_toolset_persistence.md), 2026-11-23.)

Owner ruling 2026-11-15 (owner present during the run): do the slices that may need owner input FIRST. So
Batch A holds D1, D2, D4, D5, D6, D7; Batch B-E follow with their own flagged steps early; Batch F (hooks,
scripts, docs, no server restart) goes last. Each flagged step says "May need owner input" with the question
and the recommended answer, and the same question is repeated in "Risks and open decisions".

Owner answers 2026-11-15 (all open decisions settled before the run):
- O1: records allow add-with-default and reorder only; rename and removal of a positional property are refused.
- O10: Member replace with several declarations matches by name and kind; exactly one same-name, same-kind
  declaration replaces the target, the rest are added; zero or two-plus are rejected; an anchor may not name an
  added member.
- O5: Search honours `containingType` in declaration modes and text-mode `projectName` (mapped to a fileGlob);
  `symbolKind` on usage modes is ignored with a note; the other rejections stay.
- O2-O4, O6-O9, O11-O16: accepted as recommended in "Risks and open decisions". Re-ask only if a step's
  verification contradicts one.
- Run mode: Batched (one orchestrator per batch, Batch A first). The owner is present: mid-run decisions are
  asked with a recommendation.

## Execution rules

- **Tiers (read from `.claude/implementer-tiers.json` on 2026-11-15, re-read before dispatching):** depth lane
  `implementer` at D1 = files<=3, edits<=15, acceptance<=1, toolcalls<=35; sweep lane `implementer-sweep` at S0 =
  operations<=1, files<=8, callsites<=25, projects<=2, toolcalls<=30. Brief `Tier: D1` / `Tier: S0`. A step marked
  `implementer-senior` is over the depth limit or is a new algorithm. Counts of `.cs` files include the test file.
- **Acceptance for an implementer is a clean `Build` (0 errors).** Implementers never run tests; the dispatcher
  runs the named test classes detached with `scripts/Invoke-WorktreeTests.ps1` (filter to the touched classes) after
  committing each green slice, and one full run per affected test project at the end of each batch.
- **Coverage is defined here (planner) and confirmed by the reviewer:** each step's "Tests" names the class and the
  behaviour; a test that only shares a helper with the changed path does not count. Passing
  `MemberSingleDeclarationTests` as it stands, for example, proves only the one-member rejection path and says
  nothing about the new replace-plus-add path.
- **Lean-surface rule:** any step that changes a tool description or parameter on the claude-lean surface
  (Member, ReplaceSnippet, RunTest, Search, Git, McpServerControl, CreateFile, ReadFile) adds
  `ClaudeLeanSchemaBudgetTests` (Tests.Server) to the filtered run; if it fails on growth, raise the constant there
  and note why (its `RaiseHint`).
- **Server restart:** Batches A-E change server source: after each batch `Build(level: fullBuild)`, restart your own
  instance, then `LoadSolution`. No step adds a `[McpServerToolType]` class or a tool, so no fresh chat session is needed
  (new params and a result field go on existing tool classes only).
- **Shared files:** peers edited `McpToolSchemaPatcher.cs`, `WorkspaceFileEditImpl.cs`, `CompilerErrorLookupHelper.cs`
  and `CompilerErrorLookupHelperTests.cs` on 2026-11-15 (`EditAttribution` findings during drafting, 25-37 minutes
  before the check). Run `Git(operation: status)` before any step that touches them; commit only this plan's files.
- **Pairs:** steps marked "Pair with N" touch disjoint files and may run as the two concurrent implementers (they
  share `bin/obj`; one `Build` at a time queues on the server semaphore). Within a batch, skeleton and text-neutral
  steps come first; behaviour-changing steps follow.
- **Skeleton rule (plan-writer section 2) is applied per feature**, not once for the whole plan: D1 and D2 each have a
  multi-slice body, so each opens with a compile-green skeleton (Steps 1 and 4); single-slice features use one
  `batchEdits` call with definitions first. Step 48 searches the plan's files for leftover
  `NotImplementedException` stubs.
- **DTOs:** apply the `dto-conventions` skill. Steps that change response shape (Steps 6, 10, 12, 14) say "reuse X" or give
  the NEW-DTO block; no step below needs a new wire DTO.
- **ASCII only** in code comments and docs written by these steps.

## Steps

### Batch A - owner-input and response-shape steps (run first)

### Step 1 - D1 contract skeleton for primary-constructor ChangeSignature
- Files: `RoslynSentinel.Engines.Basic/ChangeSignaturePrimaryConstructor.cs` (new)
- Depends on: none
- Effect: text-neutral (new members, no callers).
- Change: one `CreateFile` with `internal static class ChangeSignaturePrimaryConstructor` (new) and these members, bodies
  `throw new NotImplementedException(); // TODO implement (Step 2)`:
  `static TypeDeclarationSyntax? FindTypeWithPrimaryConstructor(CompilationUnitSyntax root, string typeName)`;
  `static string? RecordPolicyRefusal(TypeDeclarationSyntax type, IReadOnlyList<SignatureParameterSpec> parameters)` (policy hook, null
  means allowed; its body is the open decision O1, so Step 1 returns the refusal-for-every-record behaviour
  `// TODO implement (Step 3)`);
  `static Task<ChangeSignatureResult> ApplyAsync(Document document, TypeDeclarationSyntax type, IReadOnlyList<SignatureParameterSpec> parameters, Solution solution, CancellationToken cancellationToken)`.
  `SignatureParameterSpec` and `ChangeSignatureResult` already exist (`BasicRefactoringEngine.ChangeSignatureAsync` signature).
  The senior implementer in Step 2 may refine these signatures once and must say so in its report; later slices rely on the result.
- DTOs: reuse `TypeDeclarationSyntax`, `SignatureParameterSpec`, `ChangeSignatureResult`; no new type.
- Done when: `Build` 0 errors.
- Lane/tier: `implementer` D1 (1 file, 1 edit).

### Step 2 - D1 ChangeSignature on class/struct primary constructors (no owner input needed; records wait for Step 3)
- Files: `RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs` (`ChangeSignatureAsync`, the `methodDecl == null` branch
  that today refuses with "is a type with a primary constructor"), `RoslynSentinel.Engines.Basic/ChangeSignaturePrimaryConstructor.cs`,
  `RoslynSentinel.Tests.Advanced/ChangeSignaturePrimaryConstructorTests.cs` (new)
- Depends on: 1
- Effect: behaviour-changing: a class or struct with a primary constructor is edited instead of refused; records are still
  refused (their message is unchanged until Step 3).
- Change: replace the refusal for non-record types with `ApplyAsync`: rewrite the type's `ParameterList` per the specs, update every
  `new T(...)` object creation and every `: base(...)`/`: this(...)` primary-constructor call (the existing call-site
  planner already treats `PrimaryConstructorBaseTypeSyntax` as call-like, `BasicRefactoringEngine` ~line 432 and
  `ChangeSignatureWrapper.IsInstanceContext`), honour dry run and the compile gate like the ordinary path. Update the refusal text
  pinned by `ChangeSignatureDescriptionAndRefusalTests` (Tests.Tools.Basic) in the same dispatch only if it asserts the
  non-record message; that file is a fourth file, so it goes to a follow-up `test-writer` dispatch if needed.
- Tests (planner-defined): `ChangeSignaturePrimaryConstructorTests` - add a parameter with default to a class primary
  constructor (declaration and all `new` sites change); remove an unused parameter; reorder two parameters (named and
  positional call sites); `: base(...)` call from a derived class with its own primary constructor; dry run writes nothing;
  a record primary constructor still returns the refusal. These exercise the new path, not just the helper.
- Done when: `Build` 0 errors; dispatcher runs `ChangeSignaturePrimaryConstructorTests`, `ChangeSignatureDescriptionAndRefusalTests`,
  `ChangeSignatureCallBindingTests`.
- Lane/tier: `implementer-senior` (new algorithm in a 3063-line engine file; 3 files). Pair with Step 8 (disjoint projects).

### Step 3 - D1 record positional parameters per the owner's policy (May need owner input)
- Question O1 (record positional property policy): a record's primary-constructor parameter is also a public property,
  the `Deconstruct` position and a `with`-expression member, so changing it is a public API change. Options:
  (a) records stay refused; (b) allow add-with-default and reorder, refuse rename and removal of a positional property
  with a message naming `RenameSymbol` and `Member(remove)`; (c) allow everything and rewrite property references through the
  `SemanticReplaceEngine` record handling (`GetRecordPrimaryParameter`, ~line 951). **Recommendation: (b)**; it covers the
  journal case (8e853f06:L31, a record primary constructor refused) without silently rewriting property reads, which
  (c) does less safely than `RenameSymbol` already does.
- Files: `RoslynSentinel.Engines.Basic/ChangeSignaturePrimaryConstructor.cs` (`RecordPolicyRefusal`, `ApplyAsync` record branch),
  `RoslynSentinel.Tests.Advanced/ChangeSignaturePrimaryConstructorTests.cs`
- Depends on: 2 and the owner's answer to O1
- Effect: behaviour-changing: positional records are edited under the chosen policy.
- Change: implement `RecordPolicyRefusal` per the answer; record branch of `ApplyAsync` including call sites that use
  `new R(...)` and positional deconstruction is not touched (documented limit).
- Tests: add-with-default on a positional record; reorder; (if b) removal and rename each return the refusal naming the other tool;
  a `with` expression and a property read still compile after an add.
- Done when: `Build` 0 errors; dispatcher runs `ChangeSignaturePrimaryConstructorTests`.
- Lane/tier: `implementer-senior` (2 files).

### Step 4 - D2 contract skeleton for replace-plus-add
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`
- Depends on: none
- Effect: text-neutral.
- Change: one `batchEdits` call adding two members beside `ReplaceMemberAsync`/`AddMembersAsync` (both exist: `ReplaceMemberAsync` ~line 2525,
  `AddMembersAsync` ~line 2664), bodies `throw new NotImplementedException(); // TODO implement (Step 5)`:
  `public Task<DocumentEditResult> ReplaceMemberWithAdditionsAsync(` with the same parameter list as `ReplaceMemberAsync` (copy it from source);
  `internal static IReadOnlyList<MemberDeclarationSyntax> SplitMemberDeclarations(string source)` (wraps the probe-class parse that
  `DetectMultipleMemberDeclarations` already does). Neither is called yet.
- DTOs: reuse `DocumentEditResult`.
- Done when: `Build` 0 errors.
- Lane/tier: `implementer` D1 (1 file, 1 edit).

### Step 5 - D2 engine: replace one declaration, add the rest after it (May need owner input)
- Question O10 (D2 target matching): the proposal says the one declaration that "matches the target by name and parameter types" replaces it.
  `ReplaceMemberAsync` already resolves the target (memberName + contextSnippet + overload handling), so what is left is which of the N
  declarations is the replacement. **Recommendation:** match by declared name and member kind only; if exactly one declaration has the
  target's name it replaces the target and the others are added after it in source order (via the `AddMembersAsync` insertion path); zero or two-plus
  same-name declarations (an overload set, a renamed target) are rejected with the existing multi-declaration message. That avoids the
  syntactic-vs-semantic parameter-type question (proposal open question 4) at the cost of rejecting "replace overload X and add overload Y of the same name".
  Also open (proposal question 2, assumed no): the anchor may not name one of the members being added.
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` (`ReplaceMemberWithAdditionsAsync`, `SplitMemberDeclarations`),
  `RoslynSentinel.Tests.Tools.Basic/MemberReplaceMultiDeclarationTests.cs` (new)
- Depends on: 4 and the owner's answer to O10
- Effect: behaviour-changing for engine callers (new method only; `ReplaceMemberAsync` keeps rejecting, so nothing existing changes).
- Change: implement both methods; one `updatedText` so one compile gate and one `changeId`; the silent-drop guard stays per declaration
  (a malformed second declaration, a stray `}` or an `IncompleteMemberSyntax` is rejected, nothing written). Run the existing binding check on the replaced member.
- Tests (planner-defined, from the proposal's list): exactly-one-name-match replaces and the rest are added after it in order; zero matches
  and two same-name matches are rejected with the existing message; `contextSnippet` still selects the target; caller-before-helper in one call
  passes the compile gate (the case the one-per-call form fails with CS0103); a parse error in the second declaration writes nothing; stray `}` and body fragment still rejected.
- Done when: `Build` 0 errors; dispatcher runs `MemberReplaceMultiDeclarationTests`, `MemberSingleDeclarationTests`.
- Lane/tier: `implementer-senior` (5845-line engine file, new algorithm; 2 files). Pair with Step 10 (disjoint projects/files).

### Step 6 - D2 tool layer: Member(replace) uses the new engine path; schema text
- Files: `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs` (`MemberReplaceAsync`), `RoslynSentinel.Tools.Basic/RefactoringStructuralTools.cs` (`newMemberSource` description),
  `RoslynSentinel.Tests.Tools.Basic/MemberSingleDeclarationTests.cs`
- Depends on: 5
- Effect: behaviour-changing and wire text: a multi-declaration `newMemberSource` on replace succeeds; the result message lists the replaced and the added members; the
  description changes from "Exactly one member declaration" to a form that allows several (a handful of tokens, no new parameter).
- Change: in `MemberReplaceAsync`, when the source holds more than one declaration call `ReplaceMemberWithAdditionsAsync`, else the existing path; record all added effects;
  invert `MemberSingleDeclarationTests`' replace-rejection test into an acceptance test for the exactly-one-match case and keep a rejection test for the zero/two-match case.
- Tests: `MemberSingleDeclarationTests` (inverted replace case plus kept rejection case through the tool, not the engine), `MemberReplaceMultiDeclarationTests`.
- DTOs: reuse `AppliedChangeSummary` (no shape change; only `Description`/`StatusMessage` text).
- Done when: `Build` 0 errors; dispatcher runs the two classes plus `ClaudeLeanSchemaBudgetTests`.
- Lane/tier: `implementer` D1 (3 files). Pair with Step 9 (disjoint files).

### Step 7 - D2 oversize advice text
- Files: `RoslynSentinel.Common/WriteToolAdviceHelper.cs` (`AdviseForOversizedEdit`, the `RejectedContentShape.MultipleMembers` and `FileLevel` cases),
  `RoslynSentinel.Tests.Server/WriteToolAdviceHelperTests.cs`
- Depends on: 6
- Effect: wire text: the advice no longer says Member(replace) takes exactly one member per call.
- Change: reword the two cases to "one Member call with all the members (replace plus add), or per-member replace for several existing ones"; keep the rule that advice never names a gated tool.
- Tests: `WriteToolAdviceHelperTests` assertions on the new text and on "never names a gated tool".
- Done when: `Build` 0 errors; dispatcher runs `WriteToolAdviceHelperTests`.
- Lane/tier: `implementer` D1 (2 files).

### Step 8 - D4 helper: resolve a unique bare file name
- Files: `RoslynSentinel.Common/DocumentLookup.cs`, `RoslynSentinel.Tests.Basic/DocumentLookupBareNameTests.cs` (new)
- Depends on: none
- Effect: text-neutral (new member, no callers).
- Change: add `public static FileCandidate? TryResolveUniqueBareName(Solution solution, string requestedPath)` beside `FindFileCandidates` (existing: `FindFileCandidates(Solution, string, int max)` returning
  `FileCandidate(string Path, int Distance, bool SameFileName)`). Returns the candidate only when `requestedPath` has no directory part and exactly one loaded document has that file name
  (ordinal-ignore-case); null for 0 or 2+. Reuse `FileCandidate`; no new type.
- Tests: `DocumentLookupBareNameTests` - unique bare name resolves; two projects with the same name returns null; a path with a directory returns null; a name in no project returns null.
- Done when: `Build` 0 errors; dispatcher runs `DocumentLookupBareNameTests`.
- Lane/tier: `implementer` D1 (2 files). Pair with Step 2.

### Step 9 - D4 wire into ReadFile and GetFileOutline (May need owner input)
- Question O11: auto-resolving means a call with a wrong-looking path succeeds. Should the result say so? **Recommendation:** yes, one `Finding("FileResolved", "'X.cs' resolved to <path> (the only loaded file with that name)")`
  and the real path in the existing path fields, so the model learns the full path; `GetMethodSource` shares `BuildFileNotFoundError` but is not in D4, so it is left for a follow-up (TODO comment only).
- Files: `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs` (`ReadFile`, at the `BuildFileNotFoundError` branch), `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` (`GetFileOutline`, same branch),
  `RoslynSentinel.Tests.Tools.Basic/FileBareNameAutoResolveTests.cs` (new)
- Depends on: 8
- Effect: behaviour-changing: a unique bare name reads/outlines the file instead of returning FileNotFound with candidates.
- Change: before building the not-found error, call `DocumentLookup.TryResolveUniqueBareName`; on a hit continue with the resolved path and add the finding. Two or more matches keep today's error with candidates.
- Tests: `FileBareNameAutoResolveTests` - ReadFile and GetFileOutline each resolve a unique bare name and carry the finding; an ambiguous bare name still returns FileNotFound listing both; `FileNotFoundMessageTests` unchanged and still green.
- Done when: `Build` 0 errors; dispatcher runs `FileBareNameAutoResolveTests`, `FileNotFoundMessageTests`, `ReadFileTests`.
- Lane/tier: `implementer` D1 (3 files). Pair with Step 6.

### Step 10 - D5 Git log with nameOnly (May need owner input)
- Question O4 (wire form): `GitLogResult.Commits` is a list of `GitCommitEntry` (Hash, ShortHash, Author, Date, Message), which is why `log` pages after about 10 records. Options:
  (a) populate only `Hash` and `Message` (first line) in the same entries; (b) add one string property to `GitLogResult` holding one `<shortHash> <subject>` line per commit and leave `Commits` empty;
  (c) a new result type. **Recommendation: (b)**: it mirrors how `diff` returns its `nameOnly` list as text in `Diff`, 100 lines of about 70 characters stay inline (under the offload budget), and it adds one property to an existing DTO (dto-conventions: extend, do not create). (c) is rejected for the reuse rule.
  Need the owner's confirmation only if (a) is preferred.
- Files: `RoslynSentinel.Tools.Basic/GitTools.cs` (pass `nameOnly` to log; `nameOnly` description names log), `RoslynSentinel.Tools.Basic/GitImpl.cs` (`LogAsync`, `GitLogResult`),
  `RoslynSentinel.Tests.Tools.Basic/GitLogToolParityTests.cs`
- Depends on: none
- Effect: behaviour-changing, new tool output field (`GitLogResult` gains the string property under (b)); `nameOnly` on log stops being silently ignored. Wire impact: new tool output field.
- Change: add a trailing `bool nameOnly = false` to `LogAsync` (callers: `GitTool` only; measure with `Search(mode: callers)` before editing); when true, fill the new property from the commits the reader already returns; `stat` with log stays refused/ignored as today.
- Tests: `GitLogToolParityTests` - `nameOnly: true` returns N lines, each hash prefix plus subject, `Commits` empty; `nameOnly: false` output unchanged; count 100 stays inline (no offload).
- Done when: `Build` 0 errors; dispatcher runs `GitLogToolParityTests` plus `ClaudeLeanSchemaBudgetTests` (description grows).
- Lane/tier: `implementer` D1 (3 files). Pair with Step 5.

### Step 11 - D6 spike: where the protocol version and list_changed signal can be read (May need owner input)
- Question O2: (1) "send count": the SDK raises `notifications/tools/list_changed` itself on `ToolCollection.Changed` (`ToolsetService` doc comment), and only on a stateful transport; the server has no send site. Options:
  (a) count `ToolsetService` collection-change scopes (each `DeferChangedEvents` scope is one notification) and report it as the number of changes that made the SDK raise the notification, named `toolListChangedRaised`; (b) subscribe to `ToolCollection.Changed` and count raises (equal in practice); (c) wrap the SDK transport (disproportionate).
  **Recommendation: (a)/(b), named honestly as "raised", plus `clientProtocolVersion`.** It answers "did the server tell the client?"; it cannot prove the client received it.
  (2) the client protocol version: a tool method may take `RequestContext<CallToolRequestParams>` (`SentinelToolFactory.AugmentedServiceTypes` includes it), whose server object exposes the negotiated version in the SDK; the exact member on the pinned SDK is unverified.
- Files: none edited. Read-only: the pinned SDK (check out the matching tag per memory `reference_csharp_sdk_local_clone`), `RoslynSentinel.Common/SentinelToolFactory.cs`, `RoslynSentinel.Common/ToolsetService.cs`.
- Depends on: none
- Effect: text-neutral (no code).
- Change: answer in the dispatcher's report: the exact SDK member for the negotiated protocol version and client info, whether a `RequestContext<CallToolRequestParams>` parameter is bound on `McpServerStatusTool` (a tool method that currently takes none), and the `ToolCollection.Changed` hook point.
- Done when: the report names the SDK type/member and the call-site; Step 12's `Change` is amended with them before dispatch.
- Lane/tier: `explore-tier2` (read-only). Pair with Step 2.

### Step 12 - D6 implement: clientProtocolVersion and toolListChangedRaised on McpServerStatus
- Files: `RoslynSentinel.Common/ToolsetService.cs`, `RoslynSentinel.Tools.Basic/ServerStatusTools.cs` (`McpServerStatusTool`, `McpServerStatusResult`),
  `RoslynSentinel.Tests.Tools.Basic/McpServerStatusToolsetCountTests.cs` (new)
- Depends on: 11
- Effect: behaviour-changing (response shape): two new trailing optional fields on `McpServerStatusResult` (`string? ClientProtocolVersion = null, int? ToolListChangedRaised = null`), the same way `EolMode` was added; `McpServerStatusStructuredContentTests` (Tests.Server) pins the shape and must be updated.
- Change: one `batchEdits` call, definitions first: a counter on `ToolsetService` incremented once per change scope (under its existing lock); result fields; populate them in `McpServerStatusTool`; one sentence in the tool description naming both.
- DTOs: reuse `McpServerStatusResult` (extend by two properties); no new type. Wire impact: new tool output fields.
- Tests: `McpServerStatusToolsetCountTests` - count 0 on a fresh instance, 1 after one toolset on, 1 more after off; protocol version null when none was negotiated (stateless test host). `McpServerStatusStructuredContentTests` updated for the new fields.
- Done when: `Build` 0 errors; dispatcher runs `McpServerStatusToolsetCountTests`, `McpServerStatusStructuredContentTests`, `McpServerStatusToolListingTests`.
- Lane/tier: `implementer` D1 (3 files; Tests.Server file is a fourth, so the structured-content update goes to a `test-writer` follow-up).

### Step 13 - D7 seam: known tool-class names visible below the Server layer (May need owner input)
- Question O3 (layering): `ToolClassRegistry` is in `Server.Advanced`; the validator must run in `Tools.Basic`/`Engines.Basic`. Options: (a) a small interface in `Common` (`IKnownToolClassNames`)
  implemented in `Server.Advanced` from `ToolClassRegistry` and registered in DI, the pattern `ToolsetService` already uses for its `supportedClassNames` set; (b) reflect over `[McpServerToolType]` classes from `Common` (`McpToolSchemaPatcher.DiscoverToolMethods`) and not use `ToolClassRegistry`; (c) validate in the child server's `ParseArgs` so the error appears in the child's stderr.
  **Recommendation: (a)**: honours the owner's wording (validate against `ToolClassRegistry`), keeps the layering one-way, and the error lands in the calling tool's response. (b) would accept classes the registry does not register; (c) reports to the wrong place.
- Files: `RoslynSentinel.Common/IKnownToolClassNames.cs` (new), `RoslynSentinel.Server.Advanced/KnownToolClassNames.cs` (new, wraps the existing `ServerStartupHelpers.KnownToolClassNames`), the DI registration file that registers `TestInstanceLauncher`
  (find it with `Search(mode: references)` on `TestInstanceLauncher`; expected `ServiceRegistrationExtensionsBasic.cs`)
- Depends on: none
- Effect: text-neutral (registered, not yet consumed).
- Change: one `batchEdits` call plus two `CreateFile`s, definitions first: `public interface IKnownToolClassNames { IReadOnlySet<string> Names { get; } }` (new); the Server implementation; DI registration beside the launcher's. Verify the registration is not one of the "tool class DI blocks" rule in CLAUDE.md (it is a service, not a tool class).
- DTOs: none.
- Done when: `Build` 0 errors.
- Lane/tier: `implementer` D1 (3 files). Pair with Step 12 (disjoint files).

### Step 14 - D7 McpServerControl(LaunchTestInstance): toolsets param, includeTools validation (May need owner input)
- Question O12: an `includeTools` name that is not a known class today is silently kept and matches nothing. Should an unknown name fail the launch? **Recommendation:** yes, `InvalidArgument` listing the unknown names, the closest known class names (`DeclarationNotFoundDetail` style) and the fact that `includeTools` takes class names (for example `GitTools`), not tool names (`Git`); facts and options only, no policy words. The `toolsets` param takes `ToolSetName` values (`Common/ToolsetCatalog.cs`), validated the same way against `Enum.GetNames<ToolSetName>()`, and is passed to the child as `--toolsets=a,b` (the flag exists: `ServerStartupHelpers.ParseToolsets`, which today only warns on an unknown name).
  `environment` is not approved and is not added.
- Files: `RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs` (`LaunchAsync`, `BuildServerArguments`), `RoslynSentinel.Tools.Basic/AdminTools.cs` (`McpServerControlTool`, `ControlTestInstancesAsync`),
  `RoslynSentinel.Tests.Tools.Basic/TestInstanceLauncherTests.cs`
- Depends on: 13
- Effect: behaviour-changing: new optional `toolsets` param on `McpServerControl`; an unknown `includeTools` name is an error instead of silent. Wire impact: one new tool parameter, no result field.
- Change: add `string? toolsets = null` after `includeTools` on `McpServerControlTool`/`ControlTestInstancesAsync` (callers measured: one chain, `McpServerControlTool` -> `ControlTestInstancesAsync` -> `LaunchAsync`), inject `IKnownToolClassNames` into the launcher or the admin tool, validate, emit `--toolsets=` in `BuildServerArguments`; description text states "class names, not tool names".
- Tests: `TestInstanceLauncherTests` - `BuildServerArguments` emits `--toolsets=git`; an unknown class name returns InvalidArgument naming the unknown name and a close known name; a tool name (`Git`) is refused with the class-name fact; a valid class name still launches arguments as before.
- Done when: `Build` 0 errors; dispatcher runs `TestInstanceLauncherTests`, `ClaudeLeanSchemaBudgetTests` (new parameter on a lean tool), `McpToolsetControlTests` (Tests.Server).
- Lane/tier: `implementer` D1 (3 files).

Batch A done 2026-11-15: Step 1 6bf9025b, 2 0a940c60, 3 ab34c562, 4 ae6f7f07, 5 80bc1903, 6 fe44261f,
7 8cebd6d9, 8 2eacaac5, 9 c1193c34, 10 b02a72ed + d9f86faa, 11 spike (no commit, O2 unchanged), 12 aaf1c310,
13 224682a3, 14 d97a1f16; lean budget 44100 -> 45000 in f2539067, 9c450e88. Deviations: Step 2 went through
the shared `ChangeSignatureAsync` pipeline (5 files); Step 10 split for `IGitOperations`.

### Batch B - Search, read lookups and paging

### Step 15 - R6a Search: honour or ignore mode-ignored parameters in the tool layer (May need owner input)
- Question O5: `FindMisplacedSearchParameters` rejects any parameter the chosen mode ignores (InvalidArgument is the main Search error source per the finding). Reversing that is a deliberate contract change. **Recommendation, per parameter:**
  `containingType` in the declaration-kind modes -> honour as a filter on `item.Container`; `symbolKind` outside symbol mode -> ignore and say so in the result note; every other misplaced parameter keeps the rejection. The text-mode `projectName` case is Step 16.
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationTools.cs` (`FindMisplacedSearchParameters`, `DispatchSearchCore`), `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` (`ListAll`),
  `RoslynSentinel.Tests.Tools.Basic/SearchModeTests.cs`
- Depends on: none
- Effect: behaviour-changing: fewer InvalidArgument results; `ListAll` gains a trailing optional `string? containingType = null` (callers: `WorkspaceReadNavigationTools` and `WorkspaceTools.ListAllTool`; adding a trailing optional parameter leaves them compiling).
- Change: drop `containingType` (declaration kinds) and `symbolKind` from the `Check(...)` list, thread `containingType` into `ListAll`, add a `StatusMessage` note for ignored `symbolKind`. Keep the existing `SearchCallersMode_ContainingType_IsNotRejectedAsMisplaced` behaviour.
- Tests: `SearchModeTests` - `Search(mode: class, containingType: X)` returns only declarations whose container is X; `symbolKind` in text mode returns results plus a note; `hasAttribute` outside attribute mode is still rejected; BatteryTwentyTests' `SearchCallersMode_*` unchanged.
- Done when: `Build` 0 errors; dispatcher runs `SearchModeTests`, `BatteryTwentyTests`, `ClaudeLeanSchemaBudgetTests` (the `Search` description mentions `containingType` scope).
- Lane/tier: `implementer` D1 (3 files). Pair with Step 16.

### Step 16 - R6b Search: text-mode projectName becomes a fileGlob
- Files: `RoslynSentinel.Server.Advanced/ToolArgumentRewrites.cs` (new `RewriteSearchProjectName`, called from `Apply` beside `RewriteSearchFilePath`), `RoslynSentinel.Tests.Server/ToolArgumentRewritesTests.cs`
- Depends on: O5 answered (same question as Step 15)
- Effect: behaviour-changing: `Search(mode: text, projectName: P)` restricts to `P/**/*.cs` with a note instead of being rejected as misplaced.
- Change: text mode only, `fileGlob` declared and absent, `projectName` a non-empty string: set `fileGlob` to `"<projectName>/**/*.cs"` and remove nothing else (projectName is a declared parameter, so it stays for `FindMisplacedSearchParameters` unless the rule also removes it; the rule removes it, as `RewriteSearchFilePath` removes `filePath`), return a note. Bail out unchanged when `projectName` contains glob characters. Precedent: `RewriteSearchFilePath`, `PathToFileGlob`.
- Tests: `ToolArgumentRewritesTests` - text mode with projectName gets the glob and the note; explicit fileGlob wins and projectName stays; non-text mode untouched; glob characters bail out.
- Done when: `Build` 0 errors; dispatcher runs `ToolArgumentRewritesTests`.
- Lane/tier: `implementer` D1 (2 files).

### Step 17 - R7 Search declaration modes: `|` alternation and a better zero-match glob hint
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` (`ListAll` name filter; `SearchSolutionText` zero-match branch that computes `globExampleFolder`),
  `RoslynSentinel.Tests.Tools.Basic/SearchModeTests.cs`
- Depends on: 15 (same method and file)
- Effect: behaviour-changing: `Search(mode: method, query: "A|B")` matches either; the zero-match hint names the project folder the glob's first segment resembles.
- Change: split `nameFilter` on `|` in `ListAll` (exact and substring modes both); in the hint, pick the `RoslynSentinel.*` folder with the longest common prefix with the glob's first segment, falling back to today's first-sample folder.
- Tests: `SearchModeTests` - alternation in exact and substring modes; a query without `|` unchanged; hint names the matching folder for a glob rooted at a similar project name (`BatteryTwentyTests.SearchSolutionText_GlobMatchesNoFiles_ReturnsNonErrorWithGlobHint` stays green).
- Done when: `Build` 0 errors; dispatcher runs `SearchModeTests`, `BatteryTwentyTests`.
- Lane/tier: `implementer` D1 (2 files).

### Step 18 - R10 GetMethodSource Ambiguous: a ReadFile range for every candidate
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` (`BuildAmbiguousMethodError`), `RoslynSentinel.Tests.Tools.Basic/GetMethodSourceTests.cs`
- Depends on: 17 (same file)
- Effect: wire text: the error stops offering the range of candidate 1 only.
- Change: the last part of the message says each candidate's `lines S-E` (already listed) can be read with `ReadFile(filePath, startLine: S, endLine: E)`, showing the call for candidate 1 as the example. No new field.
- Tests: `GetMethodSourceTests` - an overloaded method's Ambiguous error mentions that every listed range is readable and still contains the first candidate's call.
- Done when: `Build` 0 errors; dispatcher runs `GetMethodSourceTests`.
- Lane/tier: `implementer` D1 (2 files).

### Step 19 - R23 ReadFile without a solution honours showLineEndings
- Files: `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs` (`ReadFile`, the `// TODO showLineEndings is ignored on the no-solution ReadFile path` branch; `ReadFileWithoutSolutionAsync`),
  `RoslynSentinel.Tests.Tools.Basic/ReadFileTests.cs`
- Depends on: 9 (same file)
- Effect: behaviour-changing: `showLineEndings` is applied when no solution is loaded (reuse `EolUtilities.DescribeLineEndings`/`AnnotateLineEndings`, as the solution path does).
- Change: pass `showLineEndings` into `ReadFileWithoutSolutionAsync` (private; one caller) and mirror the solution path's `lineEndings` field and `All` annotation.
- Tests: `ReadFileTests` - no-solution read of a CRLF file with `showLineEndings: summary` reports CRLF; `all` annotates; `none` unchanged.
- Done when: `Build` 0 errors; dispatcher runs `ReadFileTests`.
- Lane/tier: `implementer` D1 (2 files). Pair with Step 20.

### Step 20 - R9 GetLargeResult last page must not say COMPLETE when the tool capped the list (May need owner input)
- Question O6: where does the tool's own truncation signal live? The TODO in `SequenceMessages.ForPage` says a capped `Search(mode: text)` with 19356 matches pages to "195-200 of 200" with `hasMoreData` false and no warning. If the stored envelope's top-level `totalRecords` still says 19356, `ForPage` can compare it with `page.Total`; if the cap is applied before `TotalRecords` is set there is no signal and the fix belongs in Search (set the true count). **Recommendation:** read `full["totalRecords"]` in `ForPage`; if it is greater than `page.Total` append "The tool capped this list at {page.Total} of {totalRecords}; narrow the query." to the last page. Confirm the field with one real capped Search before editing.
- Files: `RoslynSentinel.Common/SequenceMessages.cs` (`ForPage`), `RoslynSentinel.Tests.Tools.Basic/SequenceLargeResultPagingTests.cs`
- Depends on: none
- Effect: wire text on last pages only.
- Change: add the capped clause when the envelope reports more records than the sequence holds; do not change the first-page text.
- Tests: `SequenceLargeResultPagingTests` - a stored sequence of 200 whose envelope says `totalRecords: 19356` ends with the capped clause; the existing `TwoLists_ChainedToTheEnd_...` assertion (`COMPLETE:` and "nothing more to fetch") stays true when the totals agree.
- Done when: `Build` 0 errors; dispatcher runs `SequenceLargeResultPagingTests`.
- Lane/tier: `implementer` D1 (2 files). Pair with Step 19.

### Batch C - Member and refactoring

### Step 21 - R13 Member results report validated: true after the compile gate (May need owner input)
- Question O7: six `Member` results (replace, remove, addTopLevelType, enum add; enum replace/remove) pass `validated: false` although `ValidateAndApplyAsync` ran the gate, so the wire flag says "not validated". Flipping it changes a wire value. **Recommendation:** flip all six to the default (`true`), because `AppliedChangeSummary.From(ApplyOutcome ...)` defaults `validated: true` for "went through the compile gate" (its own test `FromApplyOutcome_CopiesChangeId...` says so). Check `DeclarationToolTests.AssertSameOutcome` (compares `Validated` between two paths) first.
- Files: `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs` (`MemberReplaceAsync` x2, `MemberRemoveAsync` x2, `MemberAddTopLevelTypeAsync`, `MemberAddEnumMemberAsync`), tests per below
- Depends on: 6 (same file)
- Effect: behaviour-changing (wire value `validated`).
- Change: delete the `validated: false` argument at the six sites (one `batchEdits` call); leave `// TODO validated: false after the compile gate (ModifyModifierBatch/ModifyAttributeBatch/ModifyBaseTypeBatch)` at the three batch sites, unchanged.
- Tests: add `MemberValidatedFlagTests` (new, Tests.Tools.Basic): each of the six paths returns `validated: true` on a real apply and `false` on a dry run; run `DeclarationToolTests`, `MemberSingleDeclarationTests`, `MemberRemoveTests` (Tests.Tools.Advanced) for pinned expectations.
- Done when: `Build` 0 errors; dispatcher runs the four classes above.
- Lane/tier: `implementer` D1 (2 files: the impl and the new test).

### Step 22 - R12 Member remove refusal says what skipPrecheck skips and what the counts include
- Files: `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs` (`MemberRemoveAsync` refusal), `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` (`RemoveMemberAsync` "usages in the solution" refusal), `RoslynSentinel.Tests.Tools.Advanced/MemberRemoveTests.cs`
- Depends on: 21 and 5 (same files)
- Effect: wire text only.
- Change: tool precheck text adds "skipPrecheck skips only this caller/implementation precheck; the engine still refuses a member with usages in the solution" and that the counts are semantic references, including doc-comment crefs; the engine text names that a cref counts and that the count is by `SymbolFinder` references. D3 (ignore crefs) is not approved, so no behaviour change; `MemberRemoveTests` line ~163 expectation (crefs block removal) stays.
- Tests: `MemberRemoveTests` - the refusal text mentions both the skipPrecheck scope and the cref inclusion (assert on the two facts, not on exact wording).
- Done when: `Build` 0 errors; dispatcher runs `MemberRemoveTests`.
- Lane/tier: `implementer` D1 (3 files).

### Step 23 - R22 RenameSymbol residualMentions: verify, then close (May need owner input)
- Question O9: source and tests already exclude bin/obj and untracked non-source files (see Problem). **Recommendation:** close R22 as "fixed before the report / stale binary" unless the dispatcher reproduces `deps.json` or untracked files in `residualMentions` on a fresh server against a git repo; the remaining non-git fallback walk is documented behaviour. No code change planned.
- Files: none edited (read-only); `RoslynSentinel.Tests.Basic/ResidualMentionScannerTests.cs` is run, not changed.
- Depends on: none
- Effect: text-neutral.
- Change: dispatcher runs `ResidualMentionScannerTests`; if green and no reproduction, note R22 as not reproduced in the finding's R22 row and in `TODO.md` if tracked.
- Done when: `ResidualMentionScannerTests` green and the note written; otherwise open a fix slice naming the reproduced file.
- Lane/tier: dispatcher (no implementer). Pair with any step.

Batches B-C done 2026-11-15: Step 15 720d640a, 16 66183759, 17 71bbeff2, 18 b6d80216, 19 23efb288,
20 9b7ad049, 21 0fbb2348, 22 2e5a3601, 23 closed not reproduced (9cbfdc86). Deviations: Step 15 keeps
`symbolKind` rejected in text/attribute/declaration modes per O5; Step 21 flipped four sites (the
addTopLevelType and enum-add sites are `!autoStage` branches that skip the gate, so `false` is correct).

Correction 2026-11-15 for Step 24 (R15): ModelEval runs use the CLI form `--artifacts-path <dir>`
(`roslynsentinel-modeleval.ps1`), not `-p:ArtifactsPath=`; the probe skip must match both spellings, and
only when the directory is outside the repo's own `bin/obj` (do not skip `-o/--output`, which leaves `obj`
in the repo). Observed live: fullBuild refused by two `dotnet test ...Tests.ModelEval.csproj --artifacts-path
...\_scratchbuild_112/113` processes.

### Batch D - build, test, git, attribution, drift

### Step 24 - R15 Build probe ignores a peer's isolated RunTest (May need owner input)
- Question O13: skipping any process whose command line has `-p:ArtifactsPath=` assumes isolated runs never write the repo's `obj`/`bin`. `ApplyArtifactsPath` redirects both, but a restore started by that process may still touch the repo's `obj/project.assets.json`. **Recommendation:** skip it, and add a test that pins the command-line shape; verify once with a live `RunTest` plus `Build(fullBuild)` in parallel that the repo `obj` timestamps do not move.
- Files: `RoslynSentinel.Engines.Basic/BuildIsolation.cs` (`IsCompetingBuild`), `RoslynSentinel.Tests.Tools.Basic/BuildIsolationTests.cs`
- Depends on: none
- Effect: behaviour-changing: fewer "another build is still running" refusals on `fullBuild`.
- Change: in `IsCompetingBuild`, return false for a dotnet/msbuild process whose command line carries `ArtifactsPath=` (normalised separators, case-insensitive). The probe text (`DescribeCompetingBuilds`, up to 3 pids) is unchanged.
- Tests: `BuildIsolationTests` - a `dotnet test ... -p:ArtifactsPath=...` command line is not competing; the same line without it is; a throwaway `fullBuildThrowaway` command is not competing.
- Done when: `Build` 0 errors; dispatcher runs `BuildIsolationTests`.
- Lane/tier: `implementer` D1 (2 files). Pair with Step 18 (disjoint files).

### Step 25 - R19a RunTest zero-match hint keeps filter and scope (engine side, migration-first)
- Files: `RoslynSentinel.Engines.Basic/TestSuiteFilter.cs` (`ZeroMatchHint`), `RoslynSentinel.Engines.Basic/TestRunEngine.cs` (`RunAsync` call), `RoslynSentinel.Tests.Basic/TestSuiteFilterTests.cs`
- Depends on: none
- Effect: text-neutral until Step 26 supplies the new arguments; then wire text.
- Change: add trailing optional `ToolScope scope = ToolScope.solution, string? scopeName = null` to `ZeroMatchHint(TestSuite suite, string? filter, int totalCount)` and render the `suite: full` call with `filter`, `scope`, `scopeName` (resolving the `// TODO the hint drops the caller's filter/scope/scopeName`); update the `TestRunEngine.RunAsync` call. Production callers: 2 (`TestRunEngine.RunAsync`, `WorkspaceBuildTestImpl.RunTest`); test callers: 4 in `TestSuiteFilterTests`, which keep compiling.
- Tests: `TestSuiteFilterTests` - hint with filter and `scope: project, scopeName: P` contains all three arguments; with no scope it renders as today.
- Done when: `Build` 0 errors; dispatcher runs `TestSuiteFilterTests`.
- Lane/tier: `implementer` D1 (3 files).

### Step 26 - R19b RunTest zero-match hint: pass scope from the tool layer
- Files: `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs` (`RunTest`, the `ZeroMatchHint` call)
- Depends on: 25
- Effect: wire text: the hint copied verbatim runs the same scope.
- Change: pass `scope` and `scopeName` at the one call.
- Done when: `Build` 0 errors; dispatcher runs `TestSuiteFilterTests`.
- Lane/tier: `implementer` D1 (1 file, 1 edit).

### Step 27 - R20a RunTest timeoutSeconds is documented (per project)
- Files: `RoslynSentinel.Tools.Basic/WorkspaceTools.cs` (`RunTestTool`: `int timeoutSeconds = 600` has no `[Description]`)
- Depends on: none
- Effect: wire text (schema description).
- Change: add `[Description("Seconds allowed per test project (default 600); a project that exceeds it is terminated and the whole result is marked incomplete.")]`. Verify the sentence against `TestRunEngine.RunOneProjectAsync` (timeout is passed to `ChildProcessCapture.RunAsync` per project and a timed-out project returns `RunCompleted: false`).
- Done when: `Build` 0 errors; dispatcher runs `ClaudeLeanSchemaBudgetTests`.
- Lane/tier: `implementer` D1 (1 file, 1 edit). Pair with Step 24.

### Step 28 - R20b RunTest names a hung test (May need owner input)
- Question O14: `dotnet test --blame-hang --blame-hang-timeout <t>` names the hung test but writes a blame sequence file and a dump, and the TRX may be partial. **Recommendation:** add `--blame-hang --blame-hang-timeout` set to the per-project timeout minus 5 seconds (minimum 30), parse nothing new, and quote the hung test name from stdout in the timeout `Detail` when present. Verify on a deliberately hung test whether a partial TRX exists before relying on it. The third item in R20 (detached rows capped by `maxDetails`) is unverified: `RunTest` detaches `testRunResult.Results` whole, so check where `maxDetails` truncates (`TestRunEngine.RunAsync`) before changing anything; no edit planned for it here.
- Files: `RoslynSentinel.Engines.Basic/TestRunEngine.cs` (`RunOneProjectAsync`), `RoslynSentinel.Tests.Tools.Basic/RunTestBlameHangTests.cs` (new)
- Depends on: 25 (same file)
- Effect: behaviour-changing: a timed-out run's `Detail` can name the hung test; extra files under the test results folder.
- Change: add the two arguments in `RunOneProjectAsync`; extend `timeoutDetail` with the hung test name parsed from stdout when the blame line is present.
- Tests: `RunTestBlameHangTests` - argument list contains `--blame-hang-timeout`; a canned stdout with the blame-hang line produces a `Detail` containing the test name; an ordinary run is unchanged.
- Done when: `Build` 0 errors; dispatcher runs `RunTestBlameHangTests`, `TestInstanceLauncherTests` is not needed.
- Lane/tier: `implementer` D1 (2 files).

### Step 29 - R16a not-found evidence respects the search scope (May need owner input)
- Question O15: pass the search scope (the effective `fileGlob`, or the `projectName`) into `NotFoundEvidence.ForZeroHitSearch` and drop evidence for files outside it, or keep it and label it "outside the searched scope (<glob>)"? **Recommendation:** keep the evidence but label it (a wrong-scope search is exactly when "that text is in another file" helps), instead of dropping it.
- Files: `RoslynSentinel.Common/NotFoundEvidence.cs` (`ForZeroHitSearch`, `SearchBlobsAcrossFiles`, `BlobNote`), `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` (the one `NotFoundEvidence.ForZeroHitSearch` call in the zero-match branch), `RoslynSentinel.Tests.Tools.Basic/NotFoundEvidenceTests.cs`
- Depends on: 18 (same impl file); and O15
- Effect: wire text on findings.
- Change: add a trailing optional `string? searchedScope = null` to `ForZeroHitSearch` (callers measured before editing: `Search(mode: references)` on it) and append " (outside the searched scope '<scope>')" to a note whose file is not under it.
- Tests: `NotFoundEvidenceTests` - a blob note for a file outside the glob carries the label; inside it does not; no scope given behaves as today; `NotFoundEvidenceWiringTests` (Tests.Server) stays green.
- Done when: `Build` 0 errors; dispatcher runs `NotFoundEvidenceTests`, `NotFoundEvidenceWiringTests`.
- Lane/tier: `implementer` D1 (3 files).

### Step 30 - R16b session id in the EditAttribution finding wording
- Files: measure first: `InspectSymbol(aspect: blastRadius)` on the `EditAttribution` record constructor (`RoslynSentinel.Common/EditAttributionLedger.cs`). Expected edits: `EditAttributionLedger.cs` (record and its `Append`), `EditAttributionFindings.cs` (`ForPaths` wording), `EditAttributionFindingsTests.cs` (Tests.Basic).
- Depends on: none
- Effect: wire text: the finding says "(session abc12345)" so two main sessions are distinguishable.
- Change: extend the existing `EditAttribution` record with a trailing `string? SessionId = null` (`ToolCallContext.SessionId` exists, init-only) populated where an entry is appended; `ForPaths` renders the first 8 characters when it differs from the caller's session. Reuse `ToolCallContext.SessionId`; no new type. If the construction sites exceed 3 files, make it a sweep (`implementer-sweep`, operation: add the trailing argument at each construction site) before the wording slice.
- Tests: `EditAttributionFindingsTests` - a foreign entry with a session id renders it; a same-session entry is not reported; a null session id renders as today.
- Done when: `Build` 0 errors; dispatcher runs `EditAttributionFindingsTests`, `EditAttributionFindingsSurfaceTests`.
- Lane/tier: `implementer` D1 if the measurement is within 3 files, else `implementer-sweep` S0 then D1.

### Step 31 - R18a ExternalFileDrift: filePath/filePaths alias and partial Acknowledge (May need owner input)
- Question O16: today `Acknowledge` with one unmatched name refuses the whole call ("Nothing cleared: ..."), pinned by `AdminToolsTests.Acknowledge_*`. **Recommendation:** clear the matched names and report the unmatched ones in the message (partial success), because the all-or-nothing refusal is what costs a retry. The `filePath`/`filePaths` alias is a rename to `files` in `ToolArgumentRewrites` (precedent: its `Rename` helper), not a new parameter.
- Files: `RoslynSentinel.Tools.Basic/AdminTools.cs` (`AcknowledgeDriftResult`, `UnmatchedDriftNamesText`), `RoslynSentinel.Server.Advanced/ToolArgumentRewrites.cs` (new `ExternalFileDrift` rename rule), `RoslynSentinel.Tests.Tools.Basic/AdminToolsTests.cs`
- Depends on: 16 (same rewrites file)
- Effect: behaviour-changing: an Acknowledge naming one flagged and one unflagged file clears the flagged one.
- Change: as above; keep the `ConfirmAll` + `files` refusal (`Acknowledge_ConfirmAllWithFiles_IsRefusedAndClearsNothing`).
- Tests: `AdminToolsTests` - partial match clears the matched file and names the unmatched; all-unmatched still returns the NotFound refusal; alias `filePath` reaches `files`. Rewrite rule test goes to `ToolArgumentRewritesTests` in a `test-writer` follow-up (fourth file).
- Done when: `Build` 0 errors; dispatcher runs `AdminToolsTests`, `ToolArgumentRewritesTests`.
- Lane/tier: `implementer` D1 (3 files).

### Step 32 - R18b remove the dead drift-halt branch
- Files: `RoslynSentinel.Common/HaltInfo.cs` (`From`, the `KindExternalDrift` branch), `RoslynSentinel.Tools.Basic/AdminTools.cs` (the `// TODO ExplicitFileDrift Status prints SessionHalted=IsSessionHalted(), always False now` line)
- Depends on: 31 (same `AdminTools.cs`)
- Effect: text-neutral if the premise holds.
- Change: first run `Search(mode: references)` on `IsSessionHalted` and `KindExternalDrift`; delete only what has no remaining writer. If anything still sets the latch, replace the deletion with a `// TODO` and stop (the finding's "dead" claim is then wrong).
- Done when: `Build` 0 errors; dispatcher runs `AdminToolsTests` and the `HaltInfo` tests found by that search.
- Lane/tier: `implementer` D1 (2 files).

### Batch E - schema and hint text

### Step 33 - R8a move the batch cap to ReplaceSnippetOptions (migration-first)
- Files: `RoslynSentinel.Common/ReplaceSnippetOptions.cs`, `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`
- Depends on: 19 (same file); run `Git(status)` first (peer edits seen in this file)
- Effect: text-neutral.
- Change: one `batchEdits` call: add `public const int MaxEditsPerBatch = 20;` to `ReplaceSnippetOptions` (Common; static class today, read by the patcher), point `WorkspaceFileEditImpl.MaxSnippetEditsPerBatch` at it (`private const int MaxSnippetEditsPerBatch = ReplaceSnippetOptions.MaxEditsPerBatch;`) so the two call sites (`ReplaceSnippetBatch` count check and message) are untouched; `RefactoringStructuralImpl.MaxModifierFamilyEditsPerBatch` keeps citing it in a comment.
- Done when: `Build` 0 errors.
- Lane/tier: `implementer` D1 (2 files, 2 edits).

### Step 34 - R8b the batch cap in the ReplaceSnippet schema
- Files: `RoslynSentinel.Common/McpToolSchemaPatcher.cs` (`ApplyReplaceSnippetLimits`, `PatchBatchEditItems`), `RoslynSentinel.Tests.Server/ReplaceSnippetBatchCapSchemaTests.cs` (new)
- Depends on: 33; `Git(status)` first (peer edits seen in the patcher)
- Effect: wire text (schema description of `batchEdits`/`edits`).
- Change: patch the batch parameter's description with "Limit: {ReplaceSnippetOptions.MaxEditsPerBatch} edits per call; split larger batches." beside the existing `oldContent`/`newContent` limit patches.
- Tests: `ReplaceSnippetBatchCapSchemaTests` - build the tool schema the way `DocCommentIdWordingTests.BuildSchema` does and assert the batch parameter's description contains the number from `ReplaceSnippetOptions.MaxEditsPerBatch`.
- Done when: `Build` 0 errors; dispatcher runs `ReplaceSnippetBatchCapSchemaTests`, `BatchItemSchemaTests`, `ClaudeLeanSchemaBudgetTests`.
- Lane/tier: `implementer` D1 (2 files).

### Step 35 - R14 CreateFile typeKind/typeName descriptions no longer contradict content
- Files: `RoslynSentinel.Tools.Basic/WorkspaceTools.cs` (`CreateFileTool`: `typeKind` and `typeName` say "Alternative to content. Required for .cs files")
- Depends on: none
- Effect: wire text.
- Change: reword both to "Alternative to content: seeds an empty top-level type in a .cs file. Omit when you pass content." (keep the `staticClass` hint on `typeKind`).
- Done when: `Build` 0 errors; dispatcher runs `ClaudeLeanSchemaBudgetTests`.
- Lane/tier: `implementer` D1 (1 file, 2 edits). Pair with Step 36.

### Step 36 - R17 Git maxBytes says output over about 12K characters is paged anyway
- Files: `RoslynSentinel.Tools.Basic/GitTools.cs` (`maxBytes` description)
- Depends on: 10 (same file)
- Effect: wire text.
- Change: append "Output over the inline budget (about 12K characters) is paged through GetLargeResult regardless of maxBytes." Verify the budget constant (`LargeResultHelper.InlineBudgetChars`, used in `ReadFile`) before writing the number.
- Done when: `Build` 0 errors; dispatcher runs `ClaudeLeanSchemaBudgetTests`.
- Lane/tier: `implementer` D1 (1 file, 1 edit). Pair with Step 35.

### Step 37 - R21 launcher recovery hint covers a diverged clone
- Files: `RoslynSentinel.Common/LaunchBuildMarker.cs` (`Parse`: the hint that only describes `index.lock`), `RoslynSentinel.Tests.Tools.Basic/LaunchBuildMarkerTests.cs`
- Depends on: none
- Effect: wire text in `lastLaunch.hint`.
- Change: when `pullError` contains "Diverging branches" or "cannot be fast-forwarded", the hint states the fact (the source clone has commits the remote lacks, so the launcher keeps serving `servedSha`) and the option (fetch and reset the clone at `<instances root>\source`, or delete the clone so the launcher re-clones); otherwise the `index.lock` text is unchanged. Facts and options only, no policy words.
- Tests: `LaunchBuildMarkerTests` - a marker with that pull error gets the diverged wording and not the `index.lock` wording; an unrelated pull error keeps today's text.
- Done when: `Build` 0 errors; dispatcher runs `LaunchBuildMarkerTests`.
- Lane/tier: `implementer` D1 (2 files). Pair with Step 33.

### Step 38 - R11 CS0103 hint stops naming ReplaceSnippet's `newContent` for Member callers (last in Batch E)
- Files: `RoslynSentinel.Engines.Basic/CompilerErrorLookupHelper.cs` (`DescribeCs0103Async`, the sentence "Declare '{name}' in the same newContent as the code that calls it"), `RoslynSentinel.Tests.Basic/CompilerErrorLookupHelperTests.cs` (project to confirm with `Search(mode: class)`; peer edited this class)
- Depends on: none, but run `Git(operation: status)` first: another session had uncommitted edits in both files on 2026-11-15. If either is dirty, wait or coordinate with that session (`ListAgents`, `SendMessage`); do not commit its changes.
- Effect: wire text.
- Change: the hint cannot know which tool produced the diagnostic, so name both: "Declare '{name}' in the same edit as the code that calls it (the same newContent for ReplaceSnippet, the same newMemberSource for Member), or add it first with <addCall>". The existing `addCall` hint is already a `Member(addMember)` call.
- Tests: `CompilerErrorLookupHelperTests` - the CS0103 no-candidate invocation case contains both parameter names and the `addMember` call; the no-invocation case is unchanged.
- Done when: `Build` 0 errors; dispatcher runs `CompilerErrorLookupHelperTests`.
- Lane/tier: `implementer` D1 (2 files).

Batches D-E done 2026-11-15: Step 24 14200ef1, 25 651ae8d3 + cdc5d4ad, 26 7a8825d7, 27 714f30c3 + 83b0e19f,
29 45e34a2c, 30 c49123a4, 31 82aab4e0 + 8dbae9c4, 33 d50b5150, 34 d8b88556, 35 192716e9, 36 03c383b0 (inline
budget is ~15K chars, not 12K), 37 bb21a5a2, 38 0682c103; lean budget 45000 -> 45200 in e197ca35.
Not done: Step 28 (O14 check failed: a `--blame-hang` timeout leaves a TRX with zero results and a ~323 MB
dump; stdout does name the hung test) - owner question. Step 32 deferred: the dead drift latch has 54 hits in
19 files, far over the planned 2 files; needs a staged removal.
Ratchet regressions found by the batch-end full runs (filtered runs missed them, the R25 problem):
FirstMatchRatchetTests and PathComparisonRatchetTests (Steps 5 and 9) and ToolHintRatchetTests (Step 18);
fix slice dispatched 2026-11-15.

### Batch F - hooks, scripts and docs (no server restart; text-neutral first)

### Step 39 - R1 journal logger stops flagging prose
- Files: `.claude/hooks/journal-log-call.ps1` (`Test-CsFallback`: Glob `\.cs\b`, Grep glob match, the journal-path exemption, `dotnet\s+(build|test)`), `.claude/hooks/journal-hooks.Tests.ps1`
- Depends on: none
- Effect: text-neutral for the server; fewer false journal lines.
- Change: (1) Glob and Grep: ignore a negated glob (`!*.cs`) and require the match to be the path's extension, not a mention; (2) shell: exempt a command whose only `.cs`/`dotnet test` mention sits inside a quoted string or here-string (the journal exemption also accepts a variable-held journal path: resolve a `$var = '...journal...'` assignment like `Expand-SimpleAssignments` in `enforce-dogfood.ps1`). About 25 lines.
- Tests: 4 new cases in `journal-hooks.Tests.ps1`: prose `.cs` in an `echo`, a variable-held journal path, Grep glob `!*.cs`, and a real `grep ... --include=*.cs` still logged.
- Done when: `pwsh -NoProfile -File .claude/hooks/journal-hooks.Tests.ps1` exits 0.
- Lane/tier: `implementer` D1 (2 non-C# files; hook counts only `.cs`). Pair with Step 40.

### Step 40 - R2 dogfood git rule ignores quoted prose; journal exemption accepts a variable path
- Files: `.claude/hooks/enforce-dogfood.ps1` (journal exemption at the line `if ($command -match '\.claude[\\/]+journal[\\/]' -and $command -notmatch '(^|[;&|(]\s*)git\s') { exit 0 }`; covered-operation match `"(^|[;&|]|\s)git\s+(-C\s+\S+\s+)?($covered)\b"`), `.claude/hooks/enforce-dogfood.Tests.ps1`
- Depends on: none
- Effect: text-neutral for the server; fewer false blocks of `; git` inside quotes.
- Change: strip quoted strings (single, double) from the scan copy before the covered-operation test (as here-strings already are via `Remove-HereStrings`); let the journal exemption see a path assigned to a variable earlier in the command (reuse `Expand-SimpleAssignments`). About 30 lines. Never loosen an unquoted `git status`.
- Tests: new cases: `echo "blocked a git diff" >> $j` is allowed; `git status` unquoted is still blocked; `; git add` after a quoted prose string is still blocked.
- Done when: `pwsh -NoProfile -File .claude/hooks/enforce-dogfood.Tests.ps1` exits 0.
- Lane/tier: `implementer` D1 (2 files). Pair with Step 39.

### Step 41 - R3 dispatch-brief hook: label rule stated, prose `.cs` not counted, RenameSymbol file count
- Files: `.claude/hooks/enforce-dogfood.ps1` (`$labels` table at "Field labels at line start"; `$csFiles` regex in the Files-section count), `.claude/skills/dispatch-implementer/SKILL.md`, `.claude/hooks/enforce-dogfood.Tests.ps1`
- Depends on: 40 (same hook file)
- Effect: text-neutral for the server; the hook counts fewer files and the skill states the label shape.
- Change: (1) skill brief template: "each label starts a line and the colon must follow within 20 characters (`Files:`, not `Files (full paths):`)"; (2) `$csFiles`: count only tokens that look like paths (contain a directory separator or begin with `RoslynSentinel.`), not a bare `Foo.cs` in prose; (3) sweep briefs: count only the files the brief lists under `Files:` as the hook already does, and say in the skill that a `RenameSymbol` sweep lists the declaration file plus the call-site files it will touch (the hook cannot know the real fan-out; the agent preflight measures it).
- Tests: new cases in `enforce-dogfood.Tests.ps1`: prose `Foo.cs` in `Symbols:` is not a file; two real paths are two; a label with a 21-character gap is reported missing.
- Done when: `pwsh -NoProfile -File .claude/hooks/enforce-dogfood.Tests.ps1` exits 0.
- Lane/tier: `implementer` D1 (3 non-C# files).

### Step 42 - R4 DTO hook exempts service-like suffixes (May need owner input)
- Question O8: 30 `DTO-EXEMPT` overrides came from the suffix-only decision (`$exemptSuffixes`). Options: (a) add suffixes (2 lines); (b) also inspect the type body for data members (about 25 lines, riskier). **Recommendation: (a)**, using the suffixes the log shows being overridden: `Collector`, `Memory`, `Ledger`, `Gate`, `Launcher`, `Marshaller`, `Store`, `Flusher`, `Summarizer`, `Pumps`, `Lease`, `Handle`, `Source`. Re-count from `.claude/journal/dto-hook.jsonl` (local file, not in git) before settling the list. Names that are genuine data carriers must not match (check `Source`: a data record could end in it).
- Files: `.claude/hooks/enforce-dto-justification.ps1` (`$exemptSuffixes`; `Test-DtoTrigger`), `.claude/hooks/enforce-dto-justification.Tests.ps1`
- Depends on: none
- Effect: text-neutral for the server.
- Change: extend the suffix array; one test per added suffix.
- Done when: `pwsh -NoProfile -File .claude/hooks/enforce-dto-justification.Tests.ps1` exits 0.
- Lane/tier: `implementer` D1 (2 files). Pair with Step 43.

### Step 43 - R5 Invoke-WorktreeTests reports the dotnet exit code and teardown-only failures
- Files: `scripts/Invoke-WorktreeTests.ps1` (the `Wait-Process` call and the `& dotnet @(Get-TestArgs $p) *> $log` call; the final `all passed` line)
- Depends on: none
- Effect: text-neutral for the server; the script now exits non-zero and prints FAILURES when a project's dotnet exit code is non-zero.
- Change: capture each project's exit code (`$p.ExitCode` for `Start-Process -PassThru`; `$LASTEXITCODE` after the sequential call), record it in the project row (`exitCode`), set `$anyFailed` when non-zero even if the TRX shows zero failures (a failing `OneTimeTearDown` produces exit non-zero with all tests passed), and print the stderr tail line that names the teardown failure.
- Done when: `pwsh -NoProfile -Command "$null = [scriptblock]::Create((Get-Content -Raw scripts/Invoke-WorktreeTests.ps1))"` parses with 0 errors, and a dispatcher run with a deliberately non-zero exit (for example `-Filter` matching nothing) prints a non-clean summary row.
- Lane/tier: `implementer` D1 (1 file; no script test harness exists, so the Done-when is a parse plus a live run). Pair with Step 42.

### Step 44 - R24 docs: toolsets before spawning, EOL verification, schema loads outside the cap
- Files: `.claude/skills/dispatch-implementer/SKILL.md`, `.claude/agents/implementer.md`, `.claude/agents/test-writer.md`
- Depends on: 41 (same skill file)
- Effect: text-neutral for the server.
- Change: (1) skill: "before spawning a subagent, enable the toolsets its slice needs in the dispatcher's own session; a subagent inherits no toggles" (state the workflow positively; the toolset-enabling tool is `McpToolsetControl`, named in CLAUDE.md); (2) skill brief template and the two agent docs: a slice that creates or rewrites a file verifies line endings with `ReadFile(showLineEndings: summary)`, not by counting bytes in a shell; (3) `implementer.md` and the skill's `Limits:` description: loading a tool schema (ToolSearch-style) does not count toward `toolcalls`. Do not edit the generated `<!-- call-shapes -->` block.
- Done when: `pwsh -NoProfile -File scripts/Test-GuidanceHygiene.ps1` reports no new findings (size budget and style).
- Lane/tier: `implementer` D1 (3 files).

### Step 45 - R25a mark ratchet tests with a shared category (sweep, Tests.Basic)
- Files: the 8 of 9 `RoslynSentinel.Tests.Basic` classes: `AppliedChangeSummaryConstructionRatchetTests.cs`, `BareFileWriteRatchetTests.cs`, `EolAuditRatchetTests.cs`, `ErrorPolicyKindRatchetTests.cs`, `FirstMatchRatchetTests.cs`, `NewlineLiteralRatchetTests.cs`, `PathComparisonRatchetTests.cs`, `StaleReadPolicyRatchetTests.cs`
- Depends on: none
- Effect: text-neutral (adds `[Category("Ratchet")]`); makes `TestCategory=Ratchet` selectable.
- Change: add `[Category("Ratchet")]` to each class declaration; measure first that none already has a `[Category]` (then merge). Operation: one `ModifyAttribute` batch call with one add-attribute edit per class (call-site count 8, projects 1).
- Done when: `Build` 0 errors; `Search(mode: attribute, query: "Category")` over the 8 files lists the new attribute.
- Lane/tier: `implementer-sweep` S0 (operations 1, files 8, call sites 8, projects 1).

### Step 46 - R25b mark the remaining ratchet tests
- Files: `RoslynSentinel.Tests.Basic/WhitespaceRatchetTests.cs`, `RoslynSentinel.Tests.Server/ClaudeLeanSchemaBudgetTests.cs`, `RoslynSentinel.Tests.Server/ToolHintRatchetTests.cs`, `RoslynSentinel.Tests.Server/ToolReturnShapeRatchetTests.cs`, `RoslynSentinel.Tests.Tools.Basic/GitCommitRoutePinRatchetTests.cs`
- Depends on: 45 (same convention)
- Effect: text-neutral.
- Change: the same `[Category("Ratchet")]` on 5 classes (3 projects, over the S0 project limit of 2, so it runs on the depth lane: 5 files exceeds D1's 3-file limit, so split into two dispatches: (a) the two Tests.Basic and Tests.Tools.Basic files, (b) the three Tests.Server files).
- Done when: `Build` 0 errors for each dispatch.
- Lane/tier: `implementer` D1, two dispatches (2 files, then 3 files).

### Step 47 - R25c ratchet tests join filtered runs
- Files: `.claude/skills/dispatch-implementer/SKILL.md` ("Tests and test runs" filter example), `scripts/Invoke-WorktreeTests.ps1` (`-Filter` composition)
- Depends on: 45, 46, 43 (same script)
- Effect: text-neutral for the server; filtered runs also execute the ratchets.
- Change: the skill's example filter becomes `FullyQualifiedName~<ClassA>|FullyQualifiedName~<ClassB>|TestCategory=Ratchet`; add a `-WithRatchets` switch to the script that appends `|TestCategory=Ratchet` to a non-empty `-Filter`. Measure the ratchet classes' total runtime first with one `RunTest(filter: TestCategory=Ratchet)`; if it is more than a few minutes, keep it opt-in (the switch) and document it instead of making it the default.
- Done when: script parses (as Step 43); `pwsh -NoProfile -File scripts/Test-GuidanceHygiene.ps1` clean; one dispatcher run with `-WithRatchets` lists the ratchet classes in `summary.json`.
- Lane/tier: `implementer` D1 (2 non-C# files).

### Step 48 - closing check: no stubs left, trackers updated
- Files: none edited by this step; `docs/current/TODO.md`, `docs/current/CLOSED.md`, the source finding's Status line, this plan's Status line
- Depends on: all other steps
- Effect: text-neutral.
- Change: `Search(mode: text, query: "NotImplementedException")` over `RoslynSentinel.Engines.Basic/ChangeSignaturePrimaryConstructor.cs` and `MemberRefactoringEngine.cs`; it must find none of this plan's `// TODO implement (Step n)` stubs. Move TODO.md entries this plan closes (the Member option-2 item in `proposal_member_accepts_multiple_declarations.md`, the launcher-hint TODO, the `WorkspaceFileEditImpl.cs` no-solution ReadFile TODO, the `SequenceMessages` TODO, the `TestSuiteFilter` TODO) to CLOSED.md; update the finding's Status line with the commit hashes and set this plan IMPLEMENTED with `Closes:` on the last commit (Commit trailers rule).
- Done when: the stub search returns 0 hits for the plan's files and the trailer check (`ProjectDoc action: trailers`) lists no unknown ID for `plan_tool_experience_journal_fixes`.
- Lane/tier: dispatcher.

Batch F and Step 28 done 2026-11-15: Step 28 5b0815de (owner re-scope: hung-test name from stdout,
`--blame-hang-dump-type none`, no dump; names a single hung test, not a slow project), 39 aa95d4db,
40 3e84f791, 41 56d06c87, 42 b7c3323c (Source and Handle left out: real data records end in them), 43 d032b303,
44 5b46de54 (agent-doc edits are git-ignored, local only), 45 93328454, 46 6ca76c2a + 44643687 (second
`[Category("Ratchet")]` attribute), 47 54d071e8 (`-WithRatchets` is the per-slice default), 48 stub search clean.
Ratchet fixes for Steps 5, 9, 18 and 38: 5bd7c82e, bedc6f11. Step 32 moved to TODO.md by owner ruling.
Last full run (bedc6f11): 0 failures from this plan; one ratchet failure from another session's tab-toolset
work (`BareFileWrites_AreOnlyInTheAllowlist`, `TabToolsetState.cs`), reported to that session.

## Out of scope

- D3 (Member remove ignores doc-comment crefs; reverses `MemberRemoveTests` ~line 163), D8 (RunTest `includeOutput` keeps passing tests' output; RunTest reports HEAD sha and dirty count), D9 (Member opt-out of the changedFiles detach pointer), D10 (ExternalFileDrift `List` reports committed/dirty state), D11 (write results carry the resolved EOL), D12 (persist toolset state across a restart): not approved; pending owner. D12 was later built by [plan_tab_scoped_toolset_persistence.md](plan_tab_scoped_toolset_persistence.md).
- D7's `environment` param on `McpServerControl(LaunchTestInstance)`: not approved.
- `GetMethodSource` auto-resolving a unique bare name (shares `BuildFileNotFoundError` with GetFileOutline; follow-up).
- The severe one-offs S1-S4 and the line-ending loop: already fixed (finding); the dead `!autoStage` branches in `RefactoringStructuralImpl` need their own decision.
- The `workspaceStale` signal reporting a failed reload of a nonexistent `RoslynSentinel.sln` while the solution is `.slnx` (see Problem, "Environment note"): untraced, not fixed here.
- Re-verification of themes the finding already lists as fixed.

## Risks and open decisions

Open decisions, each repeated at its step with options and a recommendation (the owner is present; ask in this order):

| # | Step | Question | Recommendation |
| --- | --- | --- | --- |
| O1 | 3 | D1 record positional property policy | (b) allow add-with-default and reorder; refuse rename and removal of a positional property |
| O10 | 5 | D2 which declaration replaces the target; anchor naming | match by name and kind only, exactly one same-name declaration; reject zero or two-plus; anchor may not name an added member |
| O11 | 9 | D4 say that a bare name was resolved | yes, one `FileResolved` finding with the real path |
| O4 | 10 | D5 wire form for `log` + `nameOnly` | (b) one string property on `GitLogResult` |
| O2 | 11 | D6 meaning of "send count" and source of the protocol version | count change scopes as `toolListChangedRaised`; read the version from the SDK per the spike |
| O3 | 13 | D7 how `Tools`/`Engines` see `ToolClassRegistry` | (a) `IKnownToolClassNames` seam in Common, implemented in Server.Advanced |
| O12 | 14 | D7 unknown `includeTools` name fails the launch | yes, InvalidArgument with unknown names, close names, and the class-vs-tool-name fact |
| O5 | 15, 16 | R6 reverse the misplaced-parameter rejection for `containingType` (declaration modes), `symbolKind`, text-mode `projectName` | honour `containingType` and `projectName`; ignore `symbolKind` with a note; keep other rejections |
| O6 | 20 | R9 where the tool's cap signal lives | read envelope `totalRecords`; confirm with one real capped Search |
| O7 | 21 | R13 flip six `validated: false` to true | yes |
| O9 | 23 | R22 close without a code change | yes unless reproduced |
| O13 | 24 | R15 skip `ArtifactsPath=` processes in the probe | yes, after one live check that isolated runs leave the repo `obj` alone |
| O14 | 28 | R20 add `--blame-hang` | yes, with the timeout 5 s under the per-project timeout; verify partial TRX first |
| O15 | 29 | R16 label vs drop out-of-scope evidence | label it |
| O16 | 31 | R18 partial Acknowledge | clear matched, report unmatched |
| O8 | 42 | R4 suffixes vs body inspection | suffixes |

Other risks:

- **Peer baselines and shared files.** Four files had peer edits on 2026-11-15 (Execution rules). A peer's A/B baseline is not known to depend on any step here; `BasicRefactoringEngine.cs`, `MemberRefactoringEngine.cs`, `WorkspaceFileEditImpl.cs` and `RefactoringStructuralImpl.cs` are large shared files, so steps touching them are sequential and each commit lists only its files.
- **Unverified at drafting time:** the SDK member for the negotiated protocol version (Step 11 verifies); whether `RunTest` timeouts leave a partial TRX (Step 28); where `maxDetails` truncates RunTest `Results` before detaching (R20 third item, no edit planned); that nothing still sets the drift latch (Step 32 searches first); the test-project of `CompilerErrorLookupHelperTests` (Step 38 confirms); the exact `ModifyAttribute` batch call shape for Steps 45-46 (confirm with its schema at dispatch).
- **Skeleton signatures (Steps 1 and 4)** are the contract for Steps 2, 3 and 5; a slice that needs one changed replies `RESCOPE:`. `ReplaceMemberWithAdditionsAsync` deliberately reuses `ReplaceMemberAsync`'s parameter list, copied at Step 4.
- **Lean schema budget.** Steps 6, 10, 14, 15, 27, 34, 35 and 36 grow descriptions or parameters on lean tools; the budget test may require raising its constant (a deliberate, noted change).
- **Wire-value changes:** `validated` (Step 21), `McpServerStatus` fields (Step 12), `GitLogResult` property (Step 10), `McpServerControl.toolsets` (Step 14). Baselines in `docs/known-failing-tests.*.txt` are stale-zero (memory `feedback_known_baselines_stale_zero_failures`): any red test is new.
- **Test-coverage gap to watch (planner/reviewer, not implementer):** `MemberSingleDeclarationTests` and `AdminToolsTests` stay green through an implementation that never reaches the new path; the named new tests per step are the coverage definition, and the reviewer confirms each exercises the changed path rather than a shared helper.
- **Time and size:** 48 steps, about 14 implementer-senior or multi-file D1 slices with owner questions in front of them; Batches A-E each need a build, a restart and a batch-end full run of the affected test projects.
