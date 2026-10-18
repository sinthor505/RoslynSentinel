# MoveMember: leave a forwarder behind (`oldMember`), handle same-class calls, and answer a string-for-object argument

**Status:** PROPOSED 2026-10-17; open questions resolved 2026-10-17. Decided by the user: three-value `oldMember`, methods-only first slice (static and instance), callers untouched in keep modes, a dedicated `forwarderReceiver` parameter, XML doc kept on both members, no attributes copied. Remaining "recommended" text is the author's default. One follow-up is awaiting the user: whether to keep the tool schema flat instead of adding a DTO (open question 2). Slices are to be dispatched in a later session.

## Motivation

Moving `Search` out of the `WorkspaceTools` facade (commit 43f20fe1) needed `MoveMember`, and after `fieldMap`
and the ctor-assigned-field guard landed, three TOOL-GAPs remained. They are listed under "Still open" in
`docs/current/TODO.md` (heading at line 61; bullets at lines 66-68):

1. **No way to leave a delegator behind** (TODO.md:68). `MoveMember` always deletes the source member:
   `MoveMemberTextEdits.BuildSourceEdits` (`RoslynSentinel.Engines.Basic/MoveMemberTextEdits.cs:207`) adds a
   `new TextChange(removal, string.Empty)` for every moved member. The `Search` move needed a temporary-name
   staging trick to end up with an attribute-less delegator. This is the same capability the parallel-change
   proposal needs (`MoveMember(oldMember: keepAsDeprecatedWrapper)`), see
   `docs/current/proposals/proposal_parallel_change_deprecated_wrappers.md`.
2. **Unqualified same-class calls are neither rewritten nor reported** (TODO.md:67). A delegator's own bare call to
   a moved member became CS0103 until it was qualified with `this.` by hand.
3. **A string sent where an object is declared surfaces as an opaque failure** (TODO.md:66). `fieldMap` arrived as a
   string from a client whose cached schema predated the parameter, and the server threw a `JsonException`.

Per the CLAUDE.md failure doctrine, none of these is a model mistake: each is a missing capability or a missing
message in the environment. The rest of this document says what to add.

### What the code does today (verified 2026-10-17)

- `MemberRefactoringEngine.MoveMemberAsync` (`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:3403`) has the
  signature `(FilePathWrapper filePath, string className, string[] memberNames, string targetClassName,
  FilePathWrapper? targetFilePath = null, CancellationToken cancellationToken = default, bool autoResolveCallSites =
  true, Dictionary<string,string>? callSiteFixups = null, IReadOnlyDictionary<string,string>? fieldMap = null)`.
  `CancellationToken` is mid-signature and every later parameter is optional, so appending a parameter after
  `fieldMap` breaks none of the 36 callers (7 test files). It is the same placement `callSiteFixups` and `fieldMap`
  used.
- It routes to: base-type pull-up `MoveMembersToBaseTypeAsync` (:3866, no call-site rewriting), static into an existing
  class `MoveMembersToExistingClassAsync` (:4012), static into a new class `MoveMembersToNewClassAsync` (:4077),
  instance into a static class (convert to static, `CollectConversionMoveEditsAsync` :3977), and instance into an
  existing class `MoveInstanceMembersAsync` (:4254) with the receiver preview in
  `PreviewInstanceMoveCallSitesAsync` (:4908).
- The tool is `MoveMemberTool` (`RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs:456-459`). It calls the
  engine at :479, then `ValidateAndApplyAsync(..., expectedEdits: result.AppliedEdits)` (the collateral-change
  tripwire input). A refusal that must not be masked is passed through at :495 (`IsRefusalThatMustNotBeMasked`);
  otherwise unresolved rows from `result.PendingLedgerEntries` become `ToolErrorCode.UnresolvedCallSites`.
- Error codes are string constants in `ToolErrorCode` (`RoslynSentinel.Common/SentinelCallToolResult.cs:42`,
  `UnresolvedCallSites` at :88). `ToolException` subclasses map to codes through `ToolErrorMapper.ToResultError`
  (`RoslynSentinel.Common/ToolException.cs`): `ToolInvalidArgumentException` -> `InvalidArgument`,
  `ToolNotFoundException` -> `NotFound`, `ToolTargetIneligibleException` -> `TargetIneligible`,
  `ToolAmbiguousMatchException` -> `Ambiguous`; anything else becomes `Exception`.

## Proposal

### 1. `oldMember`: what happens to the source member

**Decided (user, 2026-10-17):** one parameter, three values, default = today's behaviour.

| Value | Source member after the move |
| --- | --- |
| `remove` (default) | Deleted. Today's behaviour, unchanged. |
| `keepDelegator` | Replaced by a plain forwarder to the moved member. No attribute. |
| `keepAsDeprecatedWrapper` | Same forwarder plus `[Obsolete("Deprecated wrapper: call <Target>.<M> instead.", DiagnosticId = "RSDEPRECATED")]`. The literal is the one defined in `proposal_parallel_change_deprecated_wrappers.md`; this proposal links it, it does not redefine it. |

**Type.** A new enum `MoveMemberOldMember { remove, keepDelegator, keepAsDeprecatedWrapper }` in
`RoslynSentinel.Common/ToolEnums.cs` with `[JsonConverter(typeof(JsonStringEnumConverter))]`, camelCase members as
every enum there already is (`GitOperation`, `ToolScope`: `ToolEnums.cs:5-60`). Both `Engines.Basic` and
`Tools.Advanced` can see `Common`.

A typed enum is safe here because the pre-bind filter already handles a bad value: `ToolArgumentValidator.Validate`
step 3b (`RoslynSentinel.Server.Basic/ToolArgumentValidator.cs:751-907`) rejects a string that is not a schema
`enum` member with the list of valid values, and `NormalizeEnumCase` (:402) repairs a case-only mismatch
(`KeepDelegator` -> `keepDelegator`) with a note.

**Placement.**
- Engine: `MoveMemberAsync(..., IReadOnlyDictionary<string,string>? fieldMap = null, MoveMemberOldMember oldMember =
  MoveMemberOldMember.remove, string? forwarderReceiver = null)`. Both after `fieldMap`, mid-signature `CancellationToken` untouched, following the existing
  precedent (CLAUDE.md says new signatures put `CancellationToken` last; this method already does not, and
  reordering would break 36 callers for no gain).
- Tool: `MoveMemberTool` gets `[Description(...)] MoveMemberOldMember oldMember = MoveMemberOldMember.remove`,
  then `string? forwarderReceiver = null`, after `fieldMap` and before `CancellationToken`, and passes both as the last engine arguments (:479).
- Migration-first note: no `[Obsolete]` overload is needed because the new parameter is optional and last of the
  optional set.

**Forwarder shape (recommended).**
- Expression-bodied, one call: `<modifiers> <return type> M(<params>) => <receiver>.M(<args>);`.
- Parameters are copied verbatim (names, types, default values, `ref`/`out`/`in`/`params` modifiers, which are
  mirrored at the call: `ref x`, `out x`, `params` array passed through). Parameter attributes (for example
  `[Description]`) are not copied.
- Modifiers kept: accessibility, `static`, `unsafe`, `new`. Dropped: `async` (the forwarder returns the target's
  `Task` directly), `partial`, `extern`.
- The member's own attributes are **not** copied. `[McpServerTool]`, `[Description]`, `[Produces]` stay on the
  moved member. This is exactly the "attribute-less delegator" the `Search` move needed (TODO.md:63, :68), and it
  prevents two tools registering under one name.
- The body is **always fully qualified**: static -> `global::<Namespace>.<Target>.M(...)` (symbol display with
  `FullyQualifiedFormat`); instance -> `this.<field>.M(...)`. Fully qualified so the forwarder compiles regardless of
  the source file's usings and cannot be captured by a same-named local (the CS0103 in TODO.md:67 is the bare-name
  version of this problem). Whether to drop `global::` when unambiguous is open (see Open questions).
- Source splice point: in `BuildSourceEdits` a kept member gets `new TextChange(member.Span, forwarderText)` instead
  of the deletion. Using `Span` (not `FullSpan`) leaves the leading trivia, including the XML doc comment, in place.
  Reference edits inside a moved member's `FullSpan` already travel with the member
  (`insideMovedMemberEdits`, :211-212), so they cannot overlap the replacement.

**Callers are left untouched in both keep modes (decided by the user, 2026-10-17).** The point of a delegator and of
a deprecated wrapper is that existing callers keep compiling and migrate later (the `InlineDeprecatedCalls` step in
the parallel-change proposal). So in keep modes the engine skips the outside-the-moved-members caller rewrite
(`CollectStaticMoveReferenceEditsAsync` for static moves, the receiver rewrite loop in `MoveInstanceMembersAsync`
for instance moves), and keeps only the edits inside the moved bodies. Consequences:
- `autoResolveCallSites` and `callSiteFixups` have nothing to resolve for caller sites, so an instance move in keep
  mode never produces `UnresolvedCallSites` and the scoped operation ledger is not opened (the tool opens it only
  from `PendingLedgerEntries` after a successful apply, never on `dryRun`).
- Same-class bare calls (section 2) stay valid, because the forwarder keeps the old name in scope.
- For `keepAsDeprecatedWrapper` every remaining caller now carries an `RSDEPRECATED` warning. That is the intended
  worklist for the later inline step.

**Receiver for an instance forwarder (decided: dedicated `forwarderReceiver` parameter).** An instance move into an
existing class today resolves a receiver only at *callers* (`PreviewInstanceMoveCallSitesAsync`); nothing in the engine
chooses a receiver for code inside the source class. The optional `string? forwarderReceiver` is a receiver expression
valid inside the source class (for example `_impl`); the forwarder body is `<forwarderReceiver>.M(...)`. When omitted, the
engine uses the source class's single field or property of the target type; with zero or several candidates it
refuses with `InvalidArgument` listing them and naming `forwarderReceiver`. See the refusal table for misuse.

**Refusals in both keep modes.** Nothing is written; each uses an existing code, no new code is added:

| Condition | errorCode | Why |
| --- | --- | --- |
| `oldMember` value not in the enum | `InvalidArgument` (from the pre-bind filter 3b) | already handled |
| Member is not a method (property, field, event, nested type, indexer, operator, ctor) | `TargetIneligible` | out of scope for the first slices; section "Out of scope" |
| Member is `virtual`, `abstract`, `override`, or implements an interface member | `TargetIneligible` | a forwarder changes dispatch; also named in the parallel-change proposal |
| Member is generic (has type parameters) | `NotImplemented` ("not yet supported") | forwarder would need type parameters, constraints and explicit type arguments; deferred, not rejected on principle |
| Member is an extension method (`this` first parameter) | `NotImplemented` | the moved member's receiver changes meaning; deferred |
| Route is base-type pull-up (`MoveMembersToBaseTypeAsync`) | `TargetIneligible` | the source already inherits the member; a forwarder would hide the base member |
| Instance move with zero or several receiver candidates and no `forwarderReceiver` | `InvalidArgument` | see above |
| Source class has more than one declaration part (partial) | `TargetIneligible` | receiver lookup scans one part only (open question 10) |
| `forwarderReceiver` given with `remove`, or with a static move | `InvalidArgument` | never a silent no-op (honoured in `remove` only after slice 7) |
| Moved member is `ref`/`out`/`in`/`params` | **allowed** | mirrored in the call; covered by a unit test |

The refusal check is one new private method in `MemberRefactoringEngine` called before any route runs, so a refused
call has no partial effects and one place to extend when generics are added.

**Interaction with the existing guards.**
- *Collateral-change tripwire.* The forwarder replacement is part of `MoveMemberResult.AppliedEdits`, so the
  tripwire (`CollateralChangeGuard`, `expectedEdits` at AdvancedRefactoringTools.cs:~490) sees the exact edit list; a
  later whole-file reformat would still be caught. A new target file still has no entry, as today.
- *Write chokepoint and EOL.* The forwarder text is built with the file's detected EOL (the same helper the insert
  builders in `MoveMemberTextEdits` use, :195) so `EolChangeRefused` cannot be triggered by it. Unverified: that every
  keep route reaches the chokepoint with the original EOL; the unit tests in slice 4 assert it.
- *`dryRun` / `returnDiff`.* No change: the forwarder is ordinary text edits, so it appears in the diff.
- *`fieldMap`.* Unchanged. It rewrites field references inside the *moved* body; the forwarder body does not
  reference source fields except the receiver.
- *RequireNoUnmovedDependencies, ctor-assigned-field guard.* Unchanged; they run before the keep logic.

### 2. Unqualified same-class calls to moved members

**Evidence (code reading; not yet reproduced by a test, so a hypothesis until slice 3 pins it).**
- The instance-route receiver rewrite only considers `MemberAccessExpressionSyntax`:
  `MemberRefactoringEngine.cs:4333` selects `OfType<MemberAccessExpressionSyntax>()` and edits `memberAccess.Expression`.
  A bare `M(x)` inside the source class is an `IdentifierNameSyntax` with no member access, so it produces no edit.
  `PreviewInstanceMoveCallSitesAsync` finds such a reference through `SymbolFinder` (:5042-5055) but
  `memberAccess` is null for it, so the row is classified without a receiver to replace.
- The static route already inserts a type qualifier on bare references outside the moved members
  (`CollectStaticMoveReferenceEditsAsync`, :3925), so the gap is expected to be **instance-route specific**.

**Proposal (remove mode).**
1. Treat a bare reference to a moved member, outside the moved set, as a call-site row like any other. The receiver
   is the same expression the forwarder would use (section 1): the unique target-typed field, else a
   `callSiteFixups` value for that site; otherwise a pending ledger entry and `UnresolvedCallSites`, exactly as for
   other unresolved rows. The rewrite is `M(x)` -> `this.<field>.M(x)`.
2. **Report every rewrite as file / line / old text / new text**, not only the unresolved ones, so a reviewer can see
   what the engine decided. The exact carrier is open: a new `RewrittenCallSites` list on `MoveMemberResult`
   (`MemberRefactoringEngine.cs:20`) surfaced in the tool's `AppliedChangeSummary`; the shape of that record was not
   read for this proposal.
3. In keep modes nothing is rewritten (section 1), so the gap does not arise.

**Relation to `docs/current/proposal_splitfacademember_tool.md`.** That legacy root doc proposes a dedicated
tool whose Steps 4-5 redirect internal same-class callers and report them as file, line, old call, new call. The
report format here deliberately matches it. The two are complementary: `SplitFacadeMember` is the facade-specific
workflow (extract a member, keep the facade shape); this proposal makes the general `MoveMember` honest about the
same-class callers it already has to handle. Building the same-class rewrite here first removes most of that
document's Step 4-5 work from any future tool.

### 3. String-for-object arguments

**Where the failure actually surfaces (traced).**
- Tools are registered through `McpToolSchemaPatcher.WithSentinelTools` / `CreateSentinelTool` / `CreateOptions`
  (`RoslynSentinel.Common/McpToolSchemaPatcher.cs:128-195`, :260). `serializerOptions` is `null` at all 39 call sites
  (counted earlier in this investigation, not re-counted), so the SDK default options apply.
- Binding happens inside the SDK's `AIFunctionMcpServerTool` via `AIFunctionFactory`. In the local clone
  (`Microsoft.Extensions.AI.Abstractions/Functions/AIFunctionFactory.cs:964-977`) a `JsonElement` argument goes straight
  to `JsonSerializer.Deserialize(element, typeInfo)`. A JSON string for a `Dictionary<string,string>?` therefore throws
  `JsonException` (TODO.md:66 and the doc comment at `ToolArgumentValidator.cs` step 3 record the same exception
  type; I did not reproduce it). The string-is-JSON fallback at :979-1005 only runs for non-`JsonElement` values.
- There is no `try/catch` around binding in the SDK tool, and `McpServerImpl.CreateToolCallErrorResult`
  (`csharp-sdk/src/ModelContextProtocol.Core/Server/McpServerImpl.cs:1743-1755`) returns the generic text
  `An error occurred invoking '<tool>'.` for any exception that is not an `McpException`. So the exception never reaches
  `MoveMemberTool`'s catch or `ToolErrorMapper`, and the caller sees no cause and no hint. This is an environment
  defect, not a caller one.

**This path already has an owner.** `ToolArgumentValidator`
(`RoslynSentinel.Server.Basic/ToolArgumentValidator.cs`), run by `AddArgumentValidationFilter`
(`ServiceRegistrationExtensionsBasic.cs:1091`) before the SDK binder, exists for exactly this class of defect: its
doc comment names "a type-mismatched or invalid-enum argument throws a raw `JsonException`" as defect 3. Step 3a
rejects an array or object where the schema declares a *scalar*; step 3b rejects a bad enum. There is no check for
the reverse: a *string* where the schema declares `object` (or `array`). That missing check is the gap.

**Proposal (recommended).**
1. **Coerce, then reject.** Add a mutating normaliser beside `WrapFlatBatchParameters` (:630), run before `Validate`
   in the filter. For each argument whose emitted property schema declares `type` `object` (alone or in a
   `["object","null"]` union) or `array`, and whose value is a JSON string that parses to that kind, replace the
   value with the parsed element and return a one-line note (`Note: 'fieldMap' was sent as a JSON string; it was
   parsed as an object. Send it as a JSON object next time.`), appended to the result by the existing
   `AppendAliasNotes`. This makes the stale-client case work outright instead of failing.
2. A string that does not parse, or parses to the wrong kind, fails in `Validate` as new step 3c with
   `InvalidArgument`-style text naming the parameter and the expected shape, for example `Parameter 'fieldMap' of
   tool 'MoveMember' takes a JSON object such as {"_readNav":"_impl"}, not a string. Nothing was executed.` No
   `JsonException` text, stack trace or path appears in it.
3. **Client-side mitigation** (not a server fix): a client that cached the tool list before `fieldMap` existed should
   refresh schemas on server restart. Worth stating in the tool description, but the server must not depend on it.

Both parts are schema-driven, no per-tool table, matching the file's stated design. Unit tests in
`RoslynSentinel.Tests.Server` cannot pass `server: null` (schema lookup returns null and the method no-ops,
`ToolArgumentValidator.cs:24-30`), so they need a real tool collection like the end-to-end cases in
`McpToolsetControlTests.cs`; that test also shares `ToolArgumentValidator.SchemaCache` and is `[NonParallelizable]`.

### Out of scope / later

- `InlineDeprecatedCalls` (the step that consumes `RSDEPRECATED`); `ChangeSignature(oldMember)`.
- Property, field, event, indexer and nested-type forwarders.
- Interface, virtual, abstract and override members.
- Generic and extension methods in keep modes (refused, not rejected on principle).
- Moving to a target outside the solution.
- Making `oldMember` mandatory (the parallel-change proposal leaves "mandatory vs default" open; this proposal keeps
  the default so no existing caller or test changes).

## Alternatives considered, not pursued

- **Two booleans (`keepDelegator`, `deprecate`).** Rejected by the user's 2026-10-17 decision for the three-value
  `oldMember`; a boolean pair also admits the meaningless combination `deprecate` without `keepDelegator`.
- **A separate tool (`MoveMemberKeepWrapper`).** Duplicates the whole route and guard logic. The parallel-change
  proposal already frames this as a mode of `MoveMember`.
- **Rewrite callers in keep modes too.** Then the forwarder has no callers and the mode is just `remove` with extra
  text. It would also reintroduce `UnresolvedCallSites` for instance moves. Kept as an open question because it is
  the one design choice here the user did not make.
- **Make the same-class gap a refusal ("bare call found, use `this.` first").** Cheapest, but it sends the caller to
  a manual edit, which the Mission calls a tool-surface gap. Rewrite-and-report is the intended behaviour; refusal is
  only the fallback when no receiver can be chosen.
- **A tolerant `JsonConverter` for object-typed parameters, passed as `SerializerOptions`.** Rejected: a custom
  converter is opaque to the JSON schema exporter, so the emitted schema for that parameter would degrade to a bare
  `true` node, which is the known "schema-true" defect that breaks LM Studio (`RewriteBrokenSchemaNode` in
  `McpToolSchemaPatcher.cs` exists to patch it; memory `project_filepath_schema_true_bug_and_detection_method`). It
  would also need all 39 `WithSentinelTools` call sites to change, or a global default, for one parameter. Hypothesis:
  whether `SerializerOptions` converters are honoured for parameter binding (`AIFunctionFactory.cs:961` calls
  `serializerOptions.GetTypeInfo`, so likely yes); not tested because the filter approach does not depend on it.
- **Declare `fieldMap`/`callSiteFixups` as `JsonElement?` or `object?` and parse in the tool.** Rejected for the same
  schema reason: the model would see no shape for the parameter.
- **Catch `JsonException` in an outer call-tool filter.** Possible, but it reports after the fact and cannot repair the
  call; the pre-bind validator can do both. Not verified whether the exception reaches an outer filter unwrapped.
- **Reorder `MoveMemberAsync` so `CancellationToken` is last.** Breaks 36 callers across 7 files for no behavioural
  gain.

## Open questions (all resolved 2026-10-17)

"Decided" means the user chose it; "Resolved" means a fact was looked up (2026-10-17, by reading, not by running code); "Set by author" is a recommendation taken as the default.

1. **Do keep modes leave callers untouched?** Decided: yes. The forwarder keeps old callers compiling and they migrate later.
2. **Receiver for an instance forwarder.** Decided: a dedicated optional `string? forwarderReceiver` parameter (engine and tool, after `oldMember`), not a reserved `callSiteFixups` key. Semantics: it is a receiver expression valid inside the source class, for example `_impl`; the forwarder body is `<forwarderReceiver>.M(...)`. When omitted, the engine uses the source class's single field or property of the target type; zero or several candidates -> `InvalidArgument` listing them and naming `forwarderReceiver`. Given with `remove` (until slice 7) or with a static move -> `InvalidArgument`, so it is never a silent no-op. After slice 7 it is also honoured in `remove` mode as the receiver for rewritten same-class bare calls.
   - *Author's recommendation on the user's follow-up question (a DTO to cut `MoveMember` parameters), to be confirmed:* keep the tool schema flat; no DTO. `oldMember` and `forwarderReceiver` are two scalars, and a nested object is exactly the failure class of section 3 (a client sends it as a string) and of the schema-true defect that breaks LM Studio. Weak models also fill flat scalars more reliably than nested objects. A DTO would help only the C# engine signature (11 parameters after this change); an internal `MoveMemberOptions` record is worth doing as its own refactor when the next parameter arrives, since it touches the 36 existing callers. Not part of these slices.
3. **XML docs.** Decided: copy to both. The forwarder replaces only the member `Span`, so the original doc comment stays in its leading trivia, and the moved member carries the same comment with it.
4. **Attributes other than `McpServerTool`.** Decided: copy none to the forwarder for now. `[MethodImpl]` and `[EditorBrowsable]` can be reconsidered later.
5. **`global::` prefix.** Set by author: always emit it. It is the safest form and can be relaxed later without changing behaviour.
6. **Does `CS0618` fire for a call inside the declaring type?** Still unverified. No existing test covers it: the only CS0618 assertion is `BuildEngineTests.cs:57-96`, and its caller is a different class. Slice 5 adds the assertion. The design does not depend on the answer; it only decides whether the source class itself shows warnings.
7. **Warnings-as-errors.** Resolved: no project in this solution sets `TreatWarningsAsErrors`, `WarningsAsErrors` or `NoWarn` (`Directory.Build.props:1-7` sets only `NuGetAudit`; the `.editorconfig` files set only CA/IDE severities), so `CS0618` is an ordinary warning here and the compile gate is not affected. A user's own solution could differ, so slice 5 keeps one fixture with `TreatWarningsAsErrors` that records the outcome, but it is no longer a blocker. Parent directories above the repo root were not checked.
8. **Which exception carries "not yet supported".** Resolved: `ToolNotImplementedException` -> `ToolErrorCode.NotImplemented` exists (`RoslynSentinel.Common/ToolException.cs:140-149`; its doc describes a deliberate stub). Set by author: generic and extension methods, and the slice-1 placeholder for unwired routes, use `NotImplemented`; "this member can never be forwarded" cases (virtual, abstract, override, interface implementation, non-method, base-type pull-up) keep `TargetIneligible`. The refusal table is updated to match.
9. **Response shape for the rewrite report.** Resolved: `AppliedChangeSummary` is a record shared by every write tool (`RoslynSentinel.Common/AppliedChangeSummary.cs:10-20`), so it gets no new field. Set by author: a new record `RewrittenCallSite(FilePath, Line, OldText, NewText)` and a `RewrittenCallSites` list on `MoveMemberResult` (same pattern as `AppliedFixups` and `Notes`, `MemberRefactoringEngine.cs:20-27`), surfaced as one `Finding` per row on the tool result (as `Notes` are, `AdvancedRefactoringTools.cs:~542`), capped at 25 rows plus a count. None of the existing row types (`PreviewCallSite`, `CallSiteLedgerEntry`, `AppliedCallSiteFixup`, `SkippedCallSite`) carries old and new text.
10. **Partial classes.** Resolved: `MoveMemberAsync` locates the source class from `filePath`'s document only (first `ClassDeclarationSyntax` with that name, `MemberRefactoringEngine.cs:~3407-3424`) and never looks at other parts. Set by author: in keep modes a source class with more than one declaration part is refused with `TargetIneligible`. Separate, pre-existing observation, not fixed here: when some requested names are in other parts and some are not, the missing ones appear to be skipped silently; worth a TODO line once confirmed.
11. **Installed package versions.** Resolved: ModelContextProtocol 2.2.0 and Microsoft.Extensions.AI.Abstractions 10.8.3 (transitive; read from `project.assets.json`). The binding code traced in section 3 is from a clone at v10.9.0-37, so the first test of slice 8 must reproduce the failure against the real pipeline before any fix.

## Cost / risk

- **Files touched overall** (across all slices): `RoslynSentinel.Common/ToolEnums.cs`,
  `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` (largest, 5.6k lines),
  `RoslynSentinel.Engines.Basic/MoveMemberTextEdits.cs`, `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs`,
  `RoslynSentinel.Server.Basic/ToolArgumentValidator.cs`, `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`
  (only if the new normaliser needs a filter call), plus tests.
- **The default path must not change.** Every edit to a route is guarded by `oldMember != remove`, and the 36 existing
  `MoveMemberAsync` callers and the `PreviewInstanceMoveCallSitesTests` / `BatteryTwentyFourTests` MoveMember cases are the
  regression net for it.
- **Test-coverage caveat.** A green run of the existing MoveMember tests proves only the `remove` paths; it says nothing
  about a keep route, because no existing test passes `oldMember`. The planner must specify, per route, a test that
  calls it with each keep value (slices 4-6 list them); the reviewer must check each named test really passes
  `oldMember` and asserts the forwarder text, not just a green build. An implementer reaching green with only the
  old tests would be legitimately correct from its vantage point and the change would still be unverified.
- **Schema growth.** One more parameter on `MoveMember` (an enum, listed values) costs prompt tokens every session
  the tool is active. The enum's three short values are the cheapest honest shape.
- **Obsolete warnings spread.** `keepAsDeprecatedWrapper` deliberately creates warnings at every remaining caller.
  Open question 7 is the unknown here.
- **Instance forwarder receiver** is the one place the engine invents code from context rather than from a
  caller's own text; it can pick wrongly when a class holds two fields of the target type. Hence the refusal on
  ambiguity.
- **Makes harder:** `MoveMemberTextEdits.BuildSourceEdits` stops being "delete only"; its callers
  (`MemberRefactoringEngine` routes) all need a keep-set argument, which is why wiring is split by route.

## Related

- `docs/current/TODO.md` (lines 61-77): the source TOOL-GAPs. The dispatcher updates it; this doc does not.
- `docs/current/proposals/proposal_parallel_change_deprecated_wrappers.md`: defines the wrapper attribute literal and
  `InlineDeprecatedCalls`; this proposal is its `MoveMember` half.
- `docs/current/proposal_splitfacademember_tool.md` (legacy root doc): facade-specific variant; report format shared.
- `docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md`: the work that hit these gaps.
- Commit 43f20fe1: `fieldMap`, ctor-assigned-field guard, `Search` move.
- Memory: `project_filepath_schema_true_bug_and_detection_method`, `project_tool_error_code_taxonomy_proposal`,
  `project_member_replace_drops_leading_blank_line_and_verify_gap`.

## Slices

Each slice is compile-green on its own (`Build` 0 errors), touches at most 3 files, and is about 10 edits or fewer.
Dispatch sequentially, never in parallel, for C# edits. Tier and `Limits:` come from
`.claude/implementer-tiers.json` at dispatch time. Implementation slices go to `implementer`; slices that only add
tests go to `test-writer`.

Slice 8 (validator) is independent of 1-7 and may run at any point. Slices 1-7 are ordered.

**Slice 1 - plumbing and refusals, no behaviour change (3 files, about 8 edits).**
- `RoslynSentinel.Common/ToolEnums.cs`: add `MoveMemberOldMember { remove, keepDelegator, keepAsDeprecatedWrapper }` with
  `[JsonConverter(typeof(JsonStringEnumConverter))]`.
- `MemberRefactoringEngine.MoveMemberAsync`: append `MoveMemberOldMember oldMember = MoveMemberOldMember.remove` and `string? forwarderReceiver = null` after
  `fieldMap`; add a private `RequireKeepModeEligible(...)` called before routing that throws the refusals in the table
  for non-method / virtual / abstract / override / interface-implementing / generic / extension members and for the
  base-type pull-up route. For an eligible member in a keep mode, throw `NotImplemented` with "keep modes are not
  wired for this route yet" (to be removed in slices 5-6).
- `MoveMemberTool`: add the `oldMember` and `forwarderReceiver` parameters with `[Description]`s (the first lists the three values) and pass them to the
  engine.
- Acceptance: `Build` 0 errors; the full existing MoveMember test set is unchanged (default path); `McpServerStatus`
  or the generated tool table shows `oldMember` with an enum of the three values after a `fullBuild` and server restart.

**Slice 2 - tests for slice 1 (1 file: `RoslynSentinel.Tests.Tools.Advanced/BatteryTwentyFourTests.cs`, about 8 new tests, `test-writer`).**
- Named after the existing `MoveMember_FieldMap...` cases (:454-483): `MoveMember_KeepDelegator_VirtualMethod_ReturnsTargetIneligible`,
  `..._AbstractMethod_...`, `..._OverrideMethod_...`, `..._Property_...`, `..._GenericMethod_...` (NotImplemented), `..._PartialSourceClass_...`, `MoveMember_ForwarderReceiver_WithRemove_ReturnsInvalidArgument`,
  `MoveMember_KeepAsDeprecatedWrapper_ToBaseClass_ReturnsTargetIneligible`, and `MoveMember_DefaultOldMember_Unchanged`
  (an existing happy-path move with `oldMember` omitted gives the same changed content as before).
- Acceptance: new tests pass; each refusal test asserts `ErrorData.ErrorCode == ToolErrorCode.TargetIneligible` (`NotImplemented` for generic) and that
  `dryRun`-independent disk content is unchanged.

**Slice 3 - characterise the same-class gap (1 file: `RoslynSentinel.Tests.Battery.Basic/PreviewInstanceMoveCallSitesTests.cs`, about 4 tests, `test-writer`).**
- `KnownLimitation_*` style (the pattern named at TODO.md:59): (a) instance move, source class has a bare call `M(x)` to a
  moved member -> record the actual outcome (expected: CS0103 or `ValidationFailed`); (b) the same on a static move ->
  expected to pass with a qualifier inserted. These tests pin current behaviour.
- Acceptance: tests pass as written; a short comment states which of (a)/(b) matched the hypothesis. If (a) does not fail,
  report it to the dispatcher so TODO.md:67 can be closed instead of building slice 7.

**Slice 4 - forwarder builder, not wired (2 files, about 6 edits).**
- `RoslynSentinel.Engines.Basic/MoveMemberTextEdits.cs`: add `BuildForwarder(MethodDeclarationSyntax method, string
  qualifiedReceiver, bool deprecated, string targetDisplayName, string eol)` returning the replacement text, and an
  overload of `BuildSourceEdits` taking a set of kept members that emits `new TextChange(member.Span, forwarderText)`
  for them and the existing deletion for the rest.
- New test file `RoslynSentinel.Tests.Battery.Basic/MoveMemberForwarderTests.cs` (pure syntax tests): void and value
  return; `ref`/`out`/`in`/`params`; optional parameters with defaults; expression-bodied and block-bodied sources;
  `async` dropped; `static` kept; member attributes dropped; the exact `[Obsolete(..., DiagnosticId = "RSDEPRECATED")]`
  literal for the deprecated mode; LF and CRLF.
- Acceptance: `Build` 0 errors; new tests pass; the only non-test change is additive (no existing caller of
  `BuildSourceEdits` changes).

**Slice 5 - wire the static routes (3 files, about 8 edits).**
- `MemberRefactoringEngine.cs`: in `MoveMembersToExistingClassAsync` (:4012) and `MoveMembersToNewClassAsync` (:4077), when
  `oldMember != remove`, skip the outside-moved-members caller edits from `CollectStaticMoveReferenceEditsAsync`, use the
  slice-4 `BuildSourceEdits` overload, and make sure `AppliedEdits` contains the replacement; remove the slice-1
  placeholder refusal for these two routes only.
- Tests: static-route cases in `BatteryTwentyFourTests.cs` and `PreviewInstanceMoveCallSitesTests.cs`, each passing
  `oldMember`: `keepDelegator` and `keepAsDeprecatedWrapper` into an existing class and into a new class; assert the
  forwarder text, that callers are unchanged, that the build is green, that `dryRun` writes nothing, that no
  `EolChangeRefused`/collateral refusal occurs, and (hypothesis check, open question 6/7) the `RSDEPRECATED` diagnostic
  appears at a caller and a `TreatWarningsAsErrors` fixture's outcome is recorded.
- Acceptance: all new tests pass; all pre-existing MoveMember tests unchanged.

**Slice 6 - wire the instance route (3 files, about 10 edits).**
- `MemberRefactoringEngine.cs`: in `MoveInstanceMembersAsync` (:4254) for `oldMember != remove`, resolve the forwarder
  receiver (`forwarderReceiver` if given, else the unique target-typed field/property of the source class, else
  `InvalidArgument` listing candidates), build `this.<receiver>.M(...)` forwarders, skip the caller receiver rewrite
  (:4333 loop), skip opening pending ledger entries for caller sites, keep the `fieldMap` rewrite inside moved bodies;
  remove the placeholder refusal for this route. Finalise the `forwarderReceiver` `[Description]` (example `_impl`); the
  `callSiteFixups` description is unchanged.
- Tests: instance move with one candidate field; with two candidates and no `forwarderReceiver` (refusal naming both); with `forwarderReceiver`;
  `fieldMap` combined with `keepDelegator`; a caller in another file left untouched; same-class bare call left valid.
- Acceptance: new tests pass; `autoResolveCallSites: false` with a keep mode still succeeds (nothing to resolve).

**Slice 7 - rewrite and report same-class bare calls in `remove` mode (3 files, about 10 edits). Only if slice 3 confirmed the gap.**
- `MemberRefactoringEngine.cs`: in `PreviewInstanceMoveCallSitesAsync` (:4908) and the rewrite loop (:4333), include a bare
  `IdentifierNameSyntax` reference to a moved member outside the moved set as a call-site row; the edit is
  `M` -> `this.<receiver>.M`; add the file / line / old / new list to `MoveMemberResult` and surface it in the tool's
  result. Unresolvable rows go through the existing `UnresolvedCallSites` path.
- Flip the slice-3 `KnownLimitation_*` instance test into a passing behavioural test.
- Acceptance: the flipped test passes; the same scenario with two receiver candidates returns `UnresolvedCallSites` naming
  the site; the rewrite list matches the edits exactly (assert old and new text, not only counts).

**Slice 8 - string-for-object coercion in the pre-bind validator (2 files, about 6 edits; independent).**
- `RoslynSentinel.Server.Basic/ToolArgumentValidator.cs`: add the coercion normaliser (pattern: `WrapFlatBatchParameters`, :630)
  and Validate step 3c for a string where `object`/`array` is declared; wire the normaliser into
  `AddArgumentValidationFilter` (`ServiceRegistrationExtensionsBasic.cs:1091`) before `Validate` (this is a second file if
  the filter call list is edited, so total 2 source files plus the tests).
- Tests in `RoslynSentinel.Tests.Server` using a real tool collection (not `server: null`): `fieldMap` sent as the string
  `"{\"_a\":\"_b\"}"` is parsed and the call proceeds with a note; `fieldMap` sent as `"hello"` is rejected with the
  step-3c message and no `JsonException`, stack trace or path in it; an array/object sent correctly is untouched.
- Acceptance: new tests pass (the first, a repro of the failure against the real pipeline, must fail before the fix); `ToolArgumentValidator` unit tests in `ApplyLegacySearchReferencesKindTests.cs` and the
  e2e cases in `McpToolsetControlTests.cs` unchanged. After a `fullBuild` and server restart, a live
  `MoveMember(fieldMap: "{...}")` call with a JSON string no longer returns the generic
  `An error occurred invoking 'MoveMember'.` text.

**After slice 8 / 7 (dispatcher, not an implementer slice):** update `docs/current/TODO.md` lines 66-68 (move the closed
entries to `CLOSED.md`), flip this doc's status to PARTLY BUILT or IMPLEMENTED with the commit hashes, and record the
parallel-change proposal's `MoveMember(oldMember: keepAsDeprecatedWrapper)` item as built.

## Moved from TODO.md (2026-10-17)

### TOOL-GAP: `MoveMember` cannot move members that use private state or constructor-initialised fields (2026-10-17) - mostly done 2026-10-17

**Done:** `fieldMap` (source private field -> target field, instance moves into an existing class) with validation, body rewrite, collision guard for a moved local named like the target field, tool parameter and tool-level tests (valid move, dry run, invalid value); a guard refusing to move a field that a source constructor assigns; the `Search` move itself (`WorkspaceReadNavigationTools` now takes `SymbolNavigationImpl`/`SymbolRelationshipImpl` by DI; `WorkspaceTools.SearchSolutionTool` stays as an attribute-less delegator; five duplicate WorkspaceTools tool methods became delegators). Option (a) (moving a ctor assignment with its field) is dropped: `ConstructorParameter` plus `fieldMap` covers it. A static-only move with `fieldMap` is refused on purpose (a static member cannot use an instance field).

**Still open (TOOL-GAPs from the Search move).** The first three are designed in `proposals/proposal_movemember_keep_delegator_and_same_class_calls.md` (PROPOSED 2026-10-17; slices 1-8, not built):
- The MoveMember client schema cached by a running client can lack `fieldMap` (the value was sent as a string and the server threw a `JsonException`); refresh schemas on server restart, or answer a string-for-object with a hint.
- MoveMember neither rewrites nor reports unqualified same-class calls to moved members (a delegator's own call became CS0103 until qualified with `this.`).
- MoveMember has no "leave a delegator behind" option (`keepDelegator`); see `proposals/proposal_parallel_change_deprecated_wrappers.md`. The Search move needed a temporary-name staging trick to keep a delegator.
- A toolset enabled by a subagent through `McpToolsetControl` is not visible to that subagent's client; enable it in the main session before spawning.
- Class-level XML docs on `WorkspaceTools` and `WorkspaceReadNavigationTools` are stale (still say the read-navigation tools are unreachable, mention `_readNav`).
- `SearchSolutionText` lost its `[McpServerTool]` attribute, so it is gone from the `WorkspaceReadNav` and `WorkspaceFileContent` sub-modes; `Search(mode: text)` covers it. Restore if wanted.

Original report follows.

Task: move `Search` (`SearchSolutionTool` plus `KindListingMaxItems`, `SearchModeToListAllKind`, `DispatchSearchCore`, `DispatchSearch`, `FindIgnoredSearchParameters`, `WorkspaceTools.cs:617-771`) from the `WorkspaceTools` facade into `WorkspaceReadNavigationTools`, the first step of removing the facade (`plans/plan_split_workspace_refactoring_tools_for_di.md`). Tool tried: `MoveMember` (`dryRun: true`, two attempts). Both were refused with `InvalidArgument`: the bodies reference private members `_readNav`, `_symbolNavigation`, `_symbolRelationship` of `WorkspaceTools` (`:33`, `:841`, `:843`) that are not in the move. Adding the two symbol fields to `memberNames` narrowed the refusal to `_readNav` only. Limits: (1) `_readNav` is also used by other facade methods, so it cannot move, and the target's equivalent is `_impl`, so the bodies would need rewriting; (2) the fields are assigned in the `WorkspaceTools` constructor (`:64-73`) and moving the declarations does not move the initialisation; (3) the target has no constructor parameters for the two symbol engines. Measured: 6 members (about 155 lines), 1 file, 1 project (`Tools.Basic`), 2 private fields, 1 shared field.

Suggested change: extend `MoveMember` so that when the moved bodies use private fields it can (a) move a field together with its constructor initialisation, or (b) add the needed dependencies to the target's constructor as new parameters and rewrite the target's construction sites, optionally mapping a source field to a target field (`_readNav` -> `_impl`). Until then, the migration-first route is to add the two engine dependencies to the target (and its `Impl`) first, then `MoveMember` the six members. Dry-run refusal text was clear and actionable but offered no way to resolve a shared field.

