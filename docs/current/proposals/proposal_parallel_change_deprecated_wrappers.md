# Parallel change (expand / migrate / contract): deprecated forwarding wrappers instead of interim-broken states

**Status:** PROPOSED 2026-10-09. Awaiting review; nothing built. Generalizes the Asyncify bridge/uplift workflow to signature changes and member moves.

## Terminology

This doc uses the established names for the pattern, not the Asyncify-specific ones, because models
already know them:

| Term here | Meaning | Asyncify equivalent (unchanged) |
| --- | --- | --- |
| Parallel change / expand-migrate-contract | The whole workflow | Bridge workflow |
| Expand | Add the new member beside the old one | `BridgeAsyncMethods` |
| Deprecated forwarding wrapper | The old member, reduced to one `[Obsolete]` forwarding call | Asyncify-bridge sync wrapper |
| Migrate / inline | Rewrite a call site to call the new member directly | `UpliftCallers` |
| Contract | Delete the wrapper once nothing calls it | (none) |

Names deliberately avoided in new tools and parameters: "bridge" (the GoF Bridge pattern means
something unrelated), "uplift" (niche jargon; elsewhere it means backporting), "cascade" (suggests one
automatic sweep, and already names `ChangeSignature`'s interface-implementer gap), "propagate" (taken by
`PropagateCancellationToken`), and "delegate" (a C# keyword and type kind). Asyncify's existing tool
names stay as they are; see Open questions.

## Motivation

Multi-file signature changes and member moves today have two ways through, and both are poor:

1. **Accept interim-broken state.** `finding_multifile_signature_change_sequencing_friction.md` records
   the `validateOnApply: false` + terminal `Build` workaround: about a dozen unvalidated edits in one
   session, because interface, implementers and call sites cannot be moved in any single-file order
   that compiles. The workaround is not discoverable from any tool description or error message.
2. **Track breakage in a gated worklist.** `ScopedOperationLedger` (`proposal_scoped_operation_ledger.md`)
   applies the change, records every unresolved call site, and refuses writes to unrelated files until
   all entries are fixed. Its only production caller is `MoveMember`
   (`RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs:533`). Its state lives in the session,
   so the caller must resolve every entry before doing anything else.

The repo already has a third pattern that avoids both, running at scale: Asyncify.

- `ConvertToAsyncBridgeAsync` (`RoslynSentinel.Engines.Advanced/AsyncOptimizationEngine.cs:358`) turns
  `Foo()` into a new `FooAsync()` plus a sync wrapper marked
  `[Obsolete("Asyncify-bridge: call FooAsync instead.", false)]` (literal at `:561`). The old member
  never disappears, and the solution compiles after the step.
- `RunUpliftBatchAsync` (`RoslynSentinel.Engines.Advanced/AsyncBatchEngine.cs:873`) finds callers of the
  obsolete wrapper with the generic `AntiPatternEngine.FindObsoleteCallersAsync(messagePattern: ...)`. It
  rewrites them one at a time, validates each in memory, writes through, and marks failures
  `[MigrationCandidate("NeedsManualReview")]` instead of breaking the build. A caller that cannot be
  rewritten stays on the wrapper and is reported as pending (`GetAsyncMigrationProgress`).

CLAUDE.md already prescribes this shape by hand for implementer slices: "Big migrations are staged so
every step compiles: add the new type/overload beside the old, move callers a few files at a time,
remove the old one in a final slice." Agents carry that out today with `ReplaceSnippet`, one call site
at a time. This proposal makes it mechanical.

## Proposal

### Three phases, each one compiles

1. **Expand.** Add the new member, under a temporary name if needed (see "Naming the new member"
   below), and reduce the old member to a deprecated forwarding wrapper:

   ```csharp
   [Obsolete("Deprecated wrapper: call FooV2 instead.", DiagnosticId = "RSDEPRECATED")]
   public Task<X> Foo(string a, string b) => FooV2(b, a, flags: Flags.None);
   ```

   The wrapper body is the old-to-new argument mapping, written in C# and checked by the compiler.
   Nobody has to describe the mapping in a parameter DSL.
2. **Migrate.** At each call site, inline the wrapper: replace the call with the wrapper's body after
   substituting arguments for parameters. This step is identical for every operation that produced the
   wrapper. Do it in scoped batches (by file or project, capped by `maxSites`); after any batch, the
   solution compiles.
3. **Contract.** Delete the wrapper once it has zero references. If a temporary name was used,
   `RenameSymbol` `FooV2` -> `Foo`.

### The source is the ledger

Pending work is "references to a deprecated wrapper". That state lives in the files themselves, so it
survives server restarts and sessions, needs no gate, and never blocks unrelated work. Progress is
measured by counting references to the wrapper (`SymbolFinder`). Obsolete warnings with the dedicated
`DiagnosticId` are a secondary, human-visible signal; they are not authoritative because of the
warning-reporting history in `blockers/resolved/blocking_error_build_tool_suppresses_cs0618_warnings.md`.

### New and changed tools

| Piece | What | Decided? |
| --- | --- | --- |
| `InlineDeprecatedCalls` (new) | Inline any source-defined deprecated forwarding wrapper at its call sites. Inputs: wrapper symbol (`docCommentId` or locator), scope (files/project), `maxSites`, `dryRun`. Output: per-site Succeeded/Skipped/Failed with reasons, using the `OperationItemRecord`/operation blob/`FailureRouter` scaffolding from `AsyncifyTools.UpliftCallersCore` | Proposed as the first slice |
| `ChangeSignature(oldMember: keepAsDeprecatedWrapper)` | New enum parameter, values `keepAsDeprecatedWrapper` / `remove`. With `keepAsDeprecatedWrapper`, emit the new signature beside the old and reduce the old one to a forwarding wrapper. Never touches call sites | Proposed |
| `MoveMember(oldMember: keepAsDeprecatedWrapper)` | Same parameter. Move the body and leave `[Obsolete] Foo(...) => _target.Foo(...)` at the source. This is the delegating-stub strategy the ledger proposal already prefers, made the default | Proposed |
| `Member(remove)` on a deprecated wrapper | Refuse while references remain, and name the count and `InlineDeprecatedCalls` | Proposed |
| `UpliftCallers` | Unchanged. It stays the async-specific variant (it also creates `callerAsync` overloads and threads `CancellationToken`, which is more than inlining) | Decided: leave as is |

The `oldMember` parameter values say what happens, rather than naming a mode, because a weak model can
read `keepAsDeprecatedWrapper` without knowing the pattern. Whether `oldMember` is mandatory or defaults
to today's behaviour is open (see Open questions).

The inliner works **per call site, not per caller method** (Uplift works per caller method). For each
site it re-reads the current document, validates, and writes through, following the loop in
`RunUpliftBatchAsync`. A site that fails validation is skipped with its diagnostics. It is not
annotated, because the remaining wrapper reference already marks it as pending.

### Inline rules (v1)

The inliner refuses the whole wrapper (no sites touched) unless the wrapper body is one invocation or
member-access expression whose parameters each appear at most once. Per site, it skips and reports when:

- an argument with possible side effects would be evaluated in a different order than at the original
  call site;
- the call is a method group or delegate conversion (`list.Select(Foo)`), sits inside an expression
  tree, or is the operand of `nameof`;
- a `ref`/`out`/`in` argument cannot be passed through unchanged;
- a member the inlined body uses is not accessible from the call site (for example, a private `_target`
  of a moved member);
- the result does not compile.

A skipped site stays on the wrapper. That is graceful degradation: still correct code, still counted
as pending.

### Naming the new member

Adding the new member under the old name as an overload can silently change which overload existing
calls bind to, or cause CS0121, without any compile error that would flag the change. The expand phase
therefore defaults to a distinct temporary name (`FooV2`, or a caller-chosen name) and the contract
phase finishes with a rename. Asyncify gets this for free from its `Async` suffix.

## Where this does not apply (the other paths stay)

- **Interface members.** Adding the new member breaks every implementer (CS0535). Default interface
  methods could do the expand phase in several steps, but the atomic multi-file batch
  (`proposal_batch_replacesnippet.md`, or `ChangeSignature` with an implementer cascade) fits better.
  `keepAsDeprecatedWrapper` refuses interface members in v1.
- **Virtual, abstract and override members.** Existing overrides keep overriding the old member, so
  behaviour would split silently. Refuse in v1.
- **Moves against the project layering.** The wrapper in the old location must be able to reference the
  new one. `Engines.Basic` -> `Common` can forward; `Common` -> `Engines.Basic` cannot. Most
  cross-project type relocation falls here.
- **Targets outside the solution.** You cannot mark a Roslyn method obsolete, so
  `proposal_redirect_calls_tool.md` is still the route for redirecting calls to an external method
  (its motivating case, `NormalizeWhitespace`).
- **Sites where no wrapper compiles.** That is the case `ScopedOperationLedger` was built for, and it
  keeps that role. Its scope should shrink to it.

## Alternatives considered, not pursued

- **Make `validateOnApply: false` discoverable** (fix 1 in the sequencing finding). It is cheap and still
  worth doing for the interface case, but it leaves interim-broken state and gives no per-site
  accounting.
- **A general call-rewrite DSL in `RedirectCalls`.** It would duplicate what a compiled wrapper body
  already expresses, and every new shape needs new mapping code. The inliner handles any shape a wrapper
  can express.
- **Grow `ScopedOperationLedger` to cover signature changes.** It keeps the session-scoped gate, which
  blocks unrelated work and is lost on restart, for cases that never need to be broken in the first place.
- **Reuse `UpliftCallers` directly.** Its rewrite (`TryTransformCallerAsync`) is async-specific:
  it creates a `callerAsync` overload and adds `await`. Only its scaffolding is reusable.
- **Reuse Asyncify's vocabulary ("bridge", "uplift") for the new tools.** Rejected for the reasons in
  Terminology: both terms are niche or collide with an unrelated meaning, so a model cannot infer what
  the tool does from its name.

## Open questions

- Can Roslyn's built-in inline-method refactoring (`Microsoft.CodeAnalysis.CSharp.Features`) be invoked
  as a `CodeRefactoringProvider` to do the per-site rewrite, instead of writing a new inliner?
  Hypothesis, not checked. It would cover argument evaluation order and named arguments for free.
- Should `oldMember` be mandatory on `ChangeSignature`/`MoveMember`, so every caller chooses
  explicitly (memory `feedback_prefer_mandatory_params_to_close_footgun_roundtrips`), or default to
  `remove` to keep today's behaviour for existing callers?
- `ObsoleteAttribute.DiagnosticId` needs .NET 5+. For older target frameworks, should the attribute fall
  back to a message-only marker (as Asyncify does), with the wrapper found by message pattern?
- Should `InlineDeprecatedCalls` also run in the same call as the expand phase when the site count is
  small, as the `Asyncify` macro chains bridge and uplift? Leaning no for v1: keep the phases separate
  and explicit.
- Should deprecated wrappers be recorded in `MigrationLedger` (`RoslynSentinel.Common/MigrationLedger.cs`)
  for cross-session observability, or is a solution-wide search for the `RSDEPRECATED` marker enough?
- Should Asyncify's `BridgeAsyncMethods`/`UpliftCallers` later gain aliases in the new vocabulary?
  Not now: renaming costs churn and test breakage for little gain. Revisit if journals show models
  confusing the two toolsets.
- Tool name: `InlineDeprecatedCalls` is the recommendation; `InlineWrapperCalls` is the alternative.

## Cost / risk

- **New mutation kind.** The inliner synthesizes call expressions from a wrapper body: the same risk
  class as the proposed `ChangeSymbolType`. Per-site validation and skip-on-failure limit the damage to
  "left on wrapper", never "left broken".
- **Code churn.** The expand phase doubles a member temporarily, and an abandoned migration leaves
  deprecated wrappers behind. They are visible and counted, but nothing forces cleanup.
- **Touches:** `ChangeSignature` and `MoveMember` (new parameter; behaviour unchanged unless
  `keepAsDeprecatedWrapper` is chosen), one new tool class (three-place registration per the
  architecture map), and a regeneration of `docs/generated/architecture_tools.md`. No change to
  `ValidateAndApplyHelper` or the write chokepoint.
- **Makes easier:** implementer slices. "Inline deprecated wrapper X in these files" is a narrow,
  single-check task that fits the Haiku slice contract without a hand-measured call-site list.

## Related

- `docs/current/finding_multifile_signature_change_sequencing_friction.md` - the friction this removes for
  non-interface members.
- `docs/current/proposal_scoped_operation_ledger.md` - section "Relationship to the existing Asyncify
  bridge/ledger machinery" already notes that Asyncify avoids the ledger because its rewrite is
  mechanical; this proposal makes more rewrites mechanical.
- `docs/current/proposal_movemember_instance_callsite_resolution.md` - delegating stub as the resolution
  strategy.
- `docs/current/proposal_redirect_calls_tool.md` - narrows to external targets.
- `docs/current/proposal_changesymboltype_tool.md`, `docs/current/proposal_batch_replacesnippet.md`.
- Memory: `project_caller_fixup_tool_audit_idea` (shared caller-fixup logic across mutating tools).
