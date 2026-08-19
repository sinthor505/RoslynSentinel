# RoslynSentinel MCP Tool-Use Scenarios — Batch 1 (ContosoOrders sample)

## Purpose
This is a small, self-contained sample solution used to evaluate whether an agent (particularly a
7B–8B local model) can correctly select and drive RoslynSentinel MCP tools to accomplish realistic
refactoring tasks. It is intentionally simple: two projects, a handful of files, and a set of
deliberately planted issues, each mapped to one or more MCP tools exposed by
`RoslynSentinel.Server.Basic`.

## Write model (current tools)
Every edit tool applies its change and writes it to disk immediately, returning
`"status": "applied"` / `"note": "Written to disk. Call UndoLastApply(changeId: ...) to revert if
needed."` in the same response. There is no separate stage → validate → apply lifecycle, and no
bulk "apply all staged changes" tool — each `changeId` names a completed, already-persisted edit,
not a pending one. Earlier versions of this spec (and of the tools themselves) modeled a staged
workflow; that model is gone. Scenarios below assume the current direct-write behavior. The only
implication for multi-edit sequencing: a later step's tool call should resolve its target against
the file's *current* on-disk state (naturally true here, since every tool re-reads the document
before editing) rather than a caller assuming stale pre-edit line numbers/text still apply.

## Sample solution layout
```
Samples/ContosoOrders/
  ContosoOrders.sln
  ContosoOrders.Core/
	ContosoOrders.Core.csproj
	OrderStatus.cs          (enum: Pending, Shipped, Cancelled — missing Delivered)
	OrderLine.cs             (record, no issues — control/reference file)
	OrderProcessor.cs        (class Order — file name intentionally mismatched)
	OrderService.cs          (class OrderService — constructor missing ILogger dep)
	Discounts/
	  DiscountCalculator.cs  (static helper, no issues)
  ContosoOrders.Tests/
	ContosoOrders.Tests.csproj
	OrderProcessorTests.cs   (xUnit tests referencing Order.CalcuateTotal())
```

## Planted issues → tool mapping

| # | Issue | Location | Primary tool(s) |
|---|-------|----------|------------------|
| 1 | Typo'd method name `CalcuateTotal` (missing "l") used across 2 projects | `Order.CalcuateTotal()` in `OrderProcessor.cs`, referenced in `OrderService.cs` and `OrderProcessorTests.cs` | `LocateSymbol` → `PreviewRenameImpact` → `RenameSymbol` |
| 2 | `ApplyDiscount` is `private` but is intended to be called externally by `OrderService` | `OrderProcessor.cs` | `ChangeAccessibility` |
| 3 | Missing `Delivered` enum value | `OrderStatus.cs` | `ModifyEnum` |
| 4 | Fully-qualified `ContosoOrders.Core.Discounts.DiscountCalculator` used instead of a `using` directive | `OrderProcessor.cs` | `AddUsingDirective` |
| 5 | Unused private method `BuildInternalDebugLabel` | `OrderProcessor.cs` | `FindReferences`/`QuerySymbolRelationships` (to confirm zero call sites) → `SafeDeleteUnusedSymbol`, with `RemoveMember(skipPrecheck: true)` as an acceptable fallback (see Scenario 5) |
| 6 | Inline totals-computation block inside `BuildOrderSummary` should become its own method | `OrderProcessor.cs` | `ExtractMethodSafe` |
| 7 | `OrderService` needs a new `ILogger<OrderService>` constructor dependency | `OrderService.cs` | `AddConstructorParameter` |
| 8 | Class `Order` lives in a file named `OrderProcessor.cs` | `OrderProcessor.cs` | `SyncTypeAndFilename` |
| 9 | Missing XML doc summary on public `OrderService.CreateOrder` | `OrderService.cs` | `AddSummaryComment` |
|10 | Verify solution compiles / no orphaned diagnostics after all changes | whole solution | `GetDiagnostics`, `GetWorkspaceHealth` |

## Scenario spec format
Each scenario below has:
- **Setup**: state assumed before the task (session/solution already loaded unless noted).
- **Task (as given to the agent)**: natural-language instruction, not a tool name.
- **Expected tool sequence**: ordered list of MCP tool calls a competent agent should make.
- **Expected outcome**: the resulting code state / diff description.
- **Grading notes**: what to check automatically (source diff) vs. manually (tool choice, arg shape).

---

### Scenario 0 — Bootstrap (prerequisite for all others)
**Task:** "Load the ContosoOrders solution at `Samples/ContosoOrders/ContosoOrders.sln` and tell me
if it's healthy."

**Expected tool sequence:**
1. `LoadSolution(solutionPath: "Samples/ContosoOrders/ContosoOrders.sln")`
2. `GetWorkspaceHealth()`

**Expected outcome:** Success=true, 2 projects loaded, 0 load errors.

**Grading notes:** Confirms the agent knows to load before doing anything else.

---

### Scenario 1 — Rename a typo'd method (Tier 1 → Tier 2)
**Task:** "The method `CalcuateTotal` on the `Order` class is misspelled. Rename it to
`CalculateTotal` everywhere it's used."

**Expected tool sequence:**
1. `LocateSymbol(symbolName: "CalcuateTotal", symbolKind: method)` → get `SymbolHandle`
   (sessionId/projectName/docCommentId).
2. *(optional but ideal)* `PreviewRenameImpact(...)` to confirm 3 call sites across 2 projects.
3. `RenameSymbol(sessionId, projectName, docCommentId, newName: "CalculateTotal")`

**Expected outcome:** `Order.CalcuateTotal` → `Order.CalculateTotal` in `OrderProcessor.cs`;
call site in `OrderService.GetOrderTotal` updated; call site in
`OrderProcessorTests.CalcuateTotal_SumsLineTotals` test body updated (note: the *test method name*
itself is a separate identifier and should NOT be renamed — only the `Order.CalculateTotal()` call
inside it). `changeId` returned; the rename is already written to disk (see "Write model" above).

**Grading notes:** This is the key disambiguation test — small models often also rename the test
method name because it contains the same substring. Verify only the member reference changed, not
the enclosing test method's own name.

---

### Scenario 2 — Loosen accessibility (Tier 1)
**Task:** "Make `Order.ApplyDiscount` public so other classes can call it."

**Expected tool sequence:**
1. `ChangeAccessibility(filepath: ".../OrderProcessor.cs", targetName: "ApplyDiscount", accessibility: "public")`

**Expected outcome:** `private decimal ApplyDiscount(...)` → `public decimal ApplyDiscount(...)`.

**Grading notes:** Single-tool task; check the agent doesn't confuse this with `ModifyModifier`
(which is for modifiers like `static`/`sealed`/`abstract`, not accessibility).

---

### Scenario 3 — Add a missing enum value (Tier 1)
**Task:** "Add a `Delivered` status to `OrderStatus`, after `Shipped`."

**Expected tool sequence:**
1. `GetTypeInfo(typeName: "OrderStatus", include: "members")` (optional — confirms current members
   and values before rewriting)
2. `ModifyEnum(filepath: ".../OrderStatus.cs", enumName: "OrderStatus", values: "Pending,Shipped,Delivered,Cancelled")`

**Expected outcome:** `OrderStatus` gains `Delivered` positioned between `Shipped` and `Cancelled`.
`ModifyEnum` (the current tool — it superseded an older, narrower `AddEnumValue` tool) takes the
full replacement member list, including any explicit values the caller provides, and otherwise
lets C#'s implicit-numbering rules apply to unpinned members. Two outcomes are both acceptable
depending on exactly what the agent passes:
- If the agent passes `"Pending,Shipped,Delivered,Cancelled"` (no explicit values at all),
  `Cancelled`'s underlying value implicitly shifts from `2` to `3` — same as a hand-edit would
  produce, not a bug.
- If the agent passes `"Pending, Shipped, Delivered = 3, Cancelled"` (pinning only `Delivered`),
  observed tool behavior keeps every other member's *original* value where it was previously
  explicit or resolvable — `Cancelled` stays `2`, both `Delivered` and `Cancelled` end up with
  distinct explicit-or-preserved values, and the result still compiles. This is arguably the nicer
  outcome (no incidental renumbering of a retained member) and should not be marked down even
  though it differs from the plain-implicit-renumbering case above.

**Grading notes:** Verify the agent does not attempt a manual `ReplaceMember`/full-file rewrite when
a purpose-built tool exists, and passes the full member list (not just `"Delivered"`) since
`ModifyEnum` replaces the whole set. Do not fail the scenario solely because `Cancelled` ended up
`2` instead of `3` (or vice versa) — check that the solution compiles and `Delivered` sits after
`Shipped`, not the exact numeric value of unrelated members.

---

### Scenario 4 — Add missing using directive (Tier 1)
**Task:** "`OrderProcessor.cs` fully-qualifies `ContosoOrders.Core.Discounts.DiscountCalculator`
every time it's used. Add the proper using directive and simplify the call site."

**Expected tool sequence:**
1. `AddUsingDirective(filepath: ".../OrderProcessor.cs", namespaceName: "ContosoOrders.Core.Discounts")`
2. *(Note: `AddUsingDirective` only adds the directive; simplifying the fully-qualified call site is
   a separate edit.)* A capable agent should follow up with `ReplaceMember`, passing the *entire*
   member's new source (not a partial line/diff), to shorten
   `ContosoOrders.Core.Discounts.DiscountCalculator.ApplyPercentage(...)` to
   `DiscountCalculator.ApplyPercentage(...)`, OR explicitly tell the user that only the using
   directive was added and the call site still compiles as-is.

**Expected outcome:** `using ContosoOrders.Core.Discounts;` added to top of file, and (if the agent
follows up correctly) the fully-qualified call site simplified via a whole-member `ReplaceMember`
call.

**Grading notes:** This scenario deliberately tests whether the agent over-claims completion ("done!")
when only half the described task was actually accomplished by the tool it called — a common small-model
failure mode. It also tests a known tool-selection friction point: agents are frequently reluctant to
call `ReplaceMember` for what looks like a one-line change, because it requires the full member body
rather than a diff/patch — most models are trained heavily on diff-style edits and hesitate to emit a
"whole file/member" replacement for a small change. Watch for the agent instead reaching for
`ProposedChange` with a hand-built unified diff (fragile — line numbers drift after any prior edit in
the same file and this consistently produces `CS1519`/parse errors when the diff's line offsets don't
match the file's current state) before eventually falling back to `ReplaceMember`. Do not penalize the
agent for eventually using `ReplaceMember` correctly after a failed diff attempt — that recovery is the
correct behavior; the tool-selection friction itself is a known gap in `ReplaceMember`'s description,
not an agent failure (see "Tool gaps observed" at the end of this document).

---

### Scenario 5 — Safe-delete dead code (Tier 2, multi-step verification)
**Task:** "Is `Order.BuildInternalDebugLabel` used anywhere? If not, remove it."

**Expected tool sequence:**
1. `LocateSymbol(symbolName: "BuildInternalDebugLabel")` or `FindReferences`/`QuerySymbolRelationships` to confirm
   zero call sites.
2. `SafeDeleteUnusedSymbol(filepath: ".../OrderProcessor.cs", line: <decl line>, column: <decl column>)`
   — or, if that call fails to resolve the symbol (observed in practice: it can reject a correct
   target with `CannotEdit`/"Symbol not found" depending on how the caller identifies the method),
   falling back to `RemoveMember(filepath: ..., memberName: "BuildInternalDebugLabel", skipPrecheck: true)`
   is an acceptable recovery — `skipPrecheck: true` is appropriate specifically because the zero-usage
   check already happened in step 1.

**Expected outcome:** Success with a report confirming zero usages found and the method removed;
if the agent skips step 1 and calls `SafeDeleteUnusedSymbol` directly, that's acceptable too since
the tool itself enforces the zero-usage guarantee — but it should still report the "why" to the user.

**Grading notes:** Tests whether the agent trusts the tool's built-in safety check vs. redundantly
verifying first (both are correct; only skipping the removal or deleting a *used* method is wrong).
Separately, watch the `contextSnippet`/anchor text the agent supplies on any failed attempt here —
a recurring small-model failure mode is passing a *plausible-looking but fabricated* reconstruction
of the method body (e.g. rewriting a single interpolated string as a string-concatenation expression)
instead of the verbatim text it already saw via an earlier `ReadFile`/`GetMethodSource` call. This can
masquerade as a tool bug (the error may report `CannotEdit` or "Symbol not found" rather than
"content doesn't match") when the actual defect is the caller inventing text instead of copying it.
Flag this even when the agent successfully recovers via a fallback tool afterward.

---

### Scenario 6 — Extract a method (Tier 2)
**Task:** "In `Order.BuildOrderSummary`, extract the block that computes running total and total
units into its own method called `ComputeTotals`."

**Expected tool sequence:**
1. `ExtractMethodSafe(filepath: ".../OrderProcessor.cs", newMethodName: "ComputeTotals", contextSnippet: <the block>, lineBefore/lineAfter as anchors)`

**Expected outcome:** New private method `ComputeTotals` returning the computed values (likely a
tuple `(decimal runningTotal, int totalUnits)` given Roslyn's inferred-return-type behavior),
called from `BuildOrderSummary`.

**Grading notes:** Validates the agent picks `ExtractMethodSafe` over manually hand-writing a
`ReplaceMember` rewrite — the whole point of this tool is correct return-type inference.
`ExtractMethodSafe`'s `contextSnippet` matching now tolerates indentation differences on multi-line
snippets (a sliding-window whitespace-collapse fallback), so an agent that retypes the target block
from memory with different indentation than the file — a common LLM behavior, since models
reliably reproduce tokens but not incidental whitespace — should still succeed; do not count a
differently-indented-but-otherwise-verbatim `contextSnippet` against the agent. Do flag it if the
agent never re-reads the file/method afterward to confirm the generated signature and call site
actually look correct — declaring the step done purely because the tool call returned success,
without checking the result, is a real (if lower-severity) gap even when the outcome happens to be
right.

---

### Scenario 7 — Add a DI constructor parameter (Tier 2)
**Task:** "`OrderService` needs a logger. Add an `ILogger<OrderService>` constructor parameter named
`logger` with a backing field."

**Expected tool sequence:**
1. `AddConstructorParameter(filepath: ".../OrderService.cs", className: "OrderService", paramName: "logger", paramType: "ILogger<OrderService>")`
2. Likely follow-up: `AddUsingDirective(namespaceName: "Microsoft.Extensions.Logging")` if not already
   present (it is not, in this sample — a good agent should notice the resulting diagnostic).
3. `GetDiagnostics(scope: file, scopeName: ".../OrderService.cs")` to confirm no new errors.

**Expected outcome:** `OrderService` gains `private readonly ILogger<OrderService> _logger;` field,
constructor parameter, and body assignment `_logger = logger;`.

**Grading notes:** Tests error-recovery — does the agent notice/fix the missing using directive for
`ILogger<>`, or does it leave a broken build? This is the most realistic multi-step failure case for
weaker models.

**Known tool behavior — constructor placement:** when the target class has no pre-existing
constructor (true for `OrderService` in this sample), `AddConstructorParameterAsync` appends the new
constructor after every existing member rather than placing it in a more idiomatic position (e.g.
first member, or immediately after fields). This is tool-controlled, not something the calling agent
can influence via its arguments — do not grade the agent down for the resulting placement.

**Out of scope — stale doc comments:** `OrderService`'s class-level XML doc in this sample says
"Target for AddConstructorParameter scenario (add an ILogger dependency)" regardless of which
dependency (`ILogger`, `Stopwatch`, etc.) a given task actually asks for, since the same file is
reused across scenario variants. An agent will typically see this text verbatim (it's returned by
any `ReadFile`/`GetMethodSource` call over the file) but is not asked to reconcile or correct it —
general housekeeping was never part of the agent's prompt or PLAN.md's steps. Do not fail an agent
for leaving this comment as-is; if evaluating an agent's judgment on unprompted cleanup is desired,
that needs to be a stated, separate scenario (see "Suggested next batches"), not an implicit
expectation here.

---

### Scenario 8 — Rename file to match class (Tier 1)
**Task:** "The `Order` class lives in a file called `OrderProcessor.cs`. Fix the mismatch."

**Expected tool sequence:**
1. `SyncTypeAndFilename(filepath: ".../OrderProcessor.cs")` (exact parameter shape depends on tool
   signature — verify against current source).

**Expected outcome:** File renamed to `Order.cs` (or tool reports the corrected target name).

**Grading notes:** Simple single-tool call; tests whether the agent recognizes "rename this file"
language maps to `SyncTypeAndFilename` rather than attempting a manual file-system rename (which it
has no tool for) or misusing `RenameSymbol` (which renames the *symbol*, not the file).

---

### Scenario 9 — Add missing XML doc comment (Tier 1)
**Task:** "Add an XML summary comment to `OrderService.CreateOrder` explaining what it does."

**Expected tool sequence:**
1. `AddSummaryComment(filepath: ".../OrderService.cs", targetName: "CreateOrder", summaryText: "<agent-authored summary>")`

**Expected outcome:** `/// <summary>...</summary>` added above `CreateOrder`.

**Grading notes:** Checks whether the agent writes a reasonably accurate summary (semantic quality,
not just tool-call correctness).

---

### Scenario 10 — End-to-end multi-step workflow (Tier 3)
**Task:** "Rename `CalcuateTotal` to `CalculateTotal`, make `ApplyDiscount` public, and add a
`Delivered` enum value to `OrderStatus`. Then verify the solution still compiles."

**Expected tool sequence:**
1. `LocateSymbol` + `RenameSymbol` (change A — written to disk immediately on success)
2. `ChangeAccessibility` (change B — written to disk immediately on success)
3. `ModifyEnum` (change C — written to disk immediately on success)
4. `GetDiagnostics(scope: solution)` and/or `GetWorkspaceHealth()` to confirm 0 errors and an
   operational workspace after all three edits.

**Expected outcome:** All three edits present in `OrderProcessor.cs`/`OrderStatus.cs` on disk;
`GetDiagnostics` reports 0 errors; no separate "apply" action taken or expected, since each of the
three edit tools already wrote its change to disk in the same call that produced it.

**Grading notes:** This scenario previously tested a propose/stage/validate/apply lifecycle and
same-file-conflict handling between changes A and B (both land in `OrderProcessor.cs`). Current
tools removed that lifecycle entirely — every edit tool applies and persists its change in one call,
and each subsequent tool call re-reads the document's current on-disk state before editing, so
change B's tool call automatically sees change A's already-applied result with no staging conflict
possible. As a result this is no longer the hardest scenario in the set; grade it primarily on:
- Whether the agent still narrates or looks for a stage/validate/apply lifecycle that doesn't exist
  (e.g. calling a nonexistent bulk-apply tool, or treating a successful edit response as merely
  "staged" and hedging on whether it actually landed) — that's a stale mental model to flag, not a
  correctness failure, since it doesn't change the resulting file state.
- Whether the agent's own step count matches what it actually did. A frequent (harmless) point of
  confusion: an agent may report "N steps completed" counting only *edit* steps and treating a final
  validate-only step (this one) as not itself a countable "step," producing an off-by-one style
  mismatch against a step count that included the validation step in its numbering. This is a
  bookkeeping quirk in the final summary, not a functional defect — check whether every edit in the
  plan actually landed on disk (that's what matters), not whether the agent's final tally arithmetic
  is self-consistent.
- Whether `GetDiagnostics`/`GetWorkspaceHealth` (or both) were actually called at the end, and
  reported 0 errors — this is still the meaningful pass/fail signal for this scenario.

---

## Grading methodology
- **Automated**: after each scenario, diff the actual file(s) against a hand-authored "golden"
  version. A pass requires the code to compile and match the golden semantics (not necessarily
  byte-for-byte, since summary text/variable naming may vary in scenarios 6 and 9).
- **Semi-automated**: log the exact tool name + arguments the agent called for each scenario and
  compare against the "Expected tool sequence" column. Flag any call to a plausible-but-wrong tool
  (e.g., `ModifyModifier` instead of `ChangeAccessibility`, `ReplaceMember` instead of
  `ExtractMethodSafe`) as a tool-selection failure even if the end result happens to compile.
- **Manual**: scenario 10's final-summary accuracy (step count, validation results reported), and
  scenario 9's doc-comment wording quality.

## Tool gaps observed (from live agent runs, not yet fixed)
- **`ReplaceMember` requires the full member body, and its tool description doesn't make this loud
  enough.** Agents consistently hesitate to call it for what looks like a small, localized edit
  (e.g. simplifying one fully-qualified call site inside an otherwise-unchanged method), because
  most models are trained heavily on diff/patch-style edits and are reluctant to emit a "replace
  the whole member" call when only one line actually changed. Observed failure mode: an agent
  reaches for `ProposedChange` with a hand-built unified diff instead, which is fragile in practice
  (its line numbers must match the file's *current* state exactly, and drift after any earlier edit
  in the same file, producing parse errors like `CS1519`/`CS1061` that look like content errors but
  are actually stale-offset errors) before eventually falling back to `ReplaceMember` successfully.
  Action item: strengthen `ReplaceMember`'s tool description to explicitly say it's the right choice
  for small in-member edits too, not just full-member rewrites, and that the caller should pass the
  member's complete new source (copied/adjusted from what it already read) rather than avoiding the
  tool because the edit is small. Possibly also worth asking whether this reflects a broader
  json-building weakness (large multi-line string tool-call arguments are harder for smaller models
  to construct reliably) independent of the "diff vs. full-replace" framing — worth a dedicated
  investigation rather than assuming either cause in isolation.
- **`AddConstructorParameterAsync` appends the new constructor after all existing members when the
  target class has no pre-existing constructor**, rather than a more idiomatic position (e.g. first
  member or immediately after fields). Not agent-controllable; see Scenario 7.
- **Fabricated `contextSnippet`/reconstructed source text on tool-call retries** is a recurring
  small-model behavior across multiple tools (`SafeDeleteUnusedSymbol`, `ExtractMethodSafe` in
  earlier attempts) — the agent reconstructs what it believes the target code looks like instead of
  copying the verbatim text it already received from an earlier `ReadFile`/`GetMethodSource` call.
  `ExtractMethodSafe`'s snippet matching was hardened against indentation-only differences, but a
  snippet with genuinely different *content* (not just whitespace) will still and should still fail
  to match. This is a model-behavior risk to keep watching for across scenarios, not something a
  single tool fix resolves.

## Suggested next batches
- Batch 2: `Git` tool scenarios (commit a written-to-disk change), `GenerateMapping` between two
  DTOs, `ModifyBaseType`/`ModifyModifier`/`AddMemberTyped` disambiguation drills.
- Batch 3: adversarial/error-recovery — intentionally wrong symbol names, stale `changeId`s,
  `RetryFailedChanges`, `UndoLastApply` after a bad edit.
- Batch 4: `SplitProjectByFolder` and `ProjectConsistencyEngine`-backed tools on a slightly larger
  multi-project sample.
- Batch 5: unprompted-housekeeping judgment — does the agent notice and (only when asked to use its
  own judgment, not as an implicit expectation) fix stale doc comments / dead scaffolding text it
  encounters incidentally while completing an unrelated task, vs. correctly staying in scope when
  the prompt is narrow. Requires an explicit prompt variant that either invites or doesn't invite
  this judgment call, so results are comparable.
