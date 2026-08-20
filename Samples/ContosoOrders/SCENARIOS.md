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

**Known model behavior — fabricating `baseRepoDir` for a relative `solutionPath`.** A relative,
forward-slash path like `Samples/ContosoOrders/ContosoOrders.sln` reads as POSIX-flavored to a
model even on a Windows host (forward slashes and bare relative paths dominate the training-data
distribution for coding-agent examples), and a live agent run (attempt 6) responded by inventing a
plausible-looking absolute `baseRepoDir` (a macOS-style `/Users/.../workspaces/...` path) instead of
simply omitting the argument, as `LoadSolution`'s own description already recommends for relative
paths. As of 2026-08-19, a `baseRepoDir` that doesn't exist on the host is now rejected with an
explicit error telling the caller to omit it rather than guess — previously it was silently dropped
and resolution fell through to the server's configured `--base-repo-dir` default, which (in that
run) happened to also contain a same-named `Samples/ContosoOrders` sibling directory, so the load
"succeeded" against the wrong solution entirely, with no error to signal the mismatch. If evaluating
this specific failure mode is a goal, prefer giving the agent an absolute `solutionPath` in the
prompt — it removes the ambiguity that invites the fabrication in the first place.

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
"whole file/member" replacement for a small change, even after `ReplaceMember`'s tool description was
strengthened to explicitly recommend it for small in-member edits (2026-08-18) — that description
change alone did not reliably change this behavior in a live run. Watch for the agent instead reaching
for `ApplyDiff` (`changesetFormat: diff` — the tool formerly named `ProposedChange`) with a hand-built
unified diff before eventually falling back to `ReplaceMember`. As of 2026-08-19, `ApplyDiff`'s diff
path re-anchors a hunk to its actual content within a 60-line search window when the declared line
number doesn't match, so modest staleness no longer produces a parse error the way it used to — but a
whole-member `ReplaceMember` call still can't drift out of sync the way a diff hunk theoretically can,
so it remains the better choice for this scenario. Do not penalize the agent for eventually using
`ReplaceMember` correctly after a diff attempt, successful or not; do note it as an example of the
persistent tool-selection friction (see "Tool gaps observed").

**Fixed regression — `ReplaceMember` failing on a defensive/mismatched `contextSnippet` for a
non-overloaded member (attempt 7):** `ApplyDiscount` is a single, non-overloaded method — `memberName`
alone already resolves it unambiguously. A live agent passed `contextSnippet` anyway (habitually, not
because anything needed disambiguating), and a whitespace/formatting mismatch in that snippet (missing
indentation from copy-pasting without preserving the file's leading whitespace) made the call fail
twice with "contextSnippet not found," even though nothing about *which* member was targeted was ever
in question. The agent then abandoned `ReplaceMember` entirely for `ApplyDiff` rather than retrying
without the unnecessary snippet. Root cause: the resolution helper required a supplied `contextSnippet`
to match whenever it was non-null, with no check for whether there was more than one candidate to
disambiguate between in the first place. Fixed: when `memberName` (or `typeName`/`symbolName`, for the
analogous helpers used by other tools) resolves to zero or one candidates, a supplied `contextSnippet`
is now ignored rather than required to match — it's only ever consulted when there's real ambiguity
(2+ same-named candidates) to resolve. A genuinely ambiguous name with a non-matching `contextSnippet`
still correctly fails. Separately, a distinct bug in the whitespace-tolerant multi-line matching
fallback was found and fixed while investigating this: a `contextSnippet` ending in a trailing newline
(e.g. copying a statement plus its closing brace with a trailing `\n`, as one such agent snippet did)
produced a phantom empty line via `Split('\n')`, inflating the sliding window's size by one and forcing
it to compare against an extra, unrelated real source line — so a snippet that should have matched via
the whitespace-tolerant fallback silently didn't. Trailing/leading blank lines in a `contextSnippet` no
longer affect the window size. If a `contextSnippet` that is either (a) unnecessary for a
non-overloaded member, or (b) has a trailing/leading blank line, still fails to resolve, that is a
regression.

---

### Scenario 5 — Safe-delete dead code (Tier 2, multi-step verification)
**Task:** "Is `Order.BuildInternalDebugLabel` used anywhere? If not, remove it."

**Expected tool sequence:**
1. `LocateSymbol(symbolName: "BuildInternalDebugLabel")` or `FindReferences`/`QuerySymbolRelationships` to confirm
   zero call sites.
2. `SafeDeleteUnusedSymbol(filepath: ".../OrderProcessor.cs", symbolName: "BuildInternalDebugLabel")`
   — as of 2026-08-19 this is the recommended path: `symbolName` alone resolves the target when
   there's only one declaration with that name, with `contextSnippet`/`lineBefore`/`lineAfter`
   available to disambiguate an overload. The `docCommentId` + `projectName` handle-based path (from
   an earlier `LocateSymbol`/`FindReferences` call) and the legacy `line`/`column` path both still
   work too. Falling back to `RemoveMember(filepath: ..., memberName: "BuildInternalDebugLabel",
   skipPrecheck: true)` if `SafeDeleteUnusedSymbol` still fails for any reason is an acceptable
   recovery — `skipPrecheck: true` is appropriate specifically because the zero-usage check already
   happened in step 1.

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

**Fixed regression — `FindReferences` silently returning a false zero-callers result:** as of
2026-08-19, a `FindReferences(kind: callers)` call whose target fails to resolve (omitted `filepath`
previously fell through to a dead by-name path; a stale/typo'd `contextSnippet` also failed silently)
now throws a descriptive error explicitly stating the lookup never ran, rather than returning
`Success: true, Data: []` — which used to be indistinguishable from a real zero-usages confirmation.
If step 1 ever again reports zero callers/references for a symbol that is actually still referenced
elsewhere, that is a regression in this fix, not evidence the tool's underlying safety check is
merely advisory.

**Fixed regression — `SafeDeleteUnusedSymbol`'s documented `contextSnippet` fallback didn't exist
(attempt 5):** a live agent run had only a line number (from `SearchSolutionText`) and no column —
no tool surfaces a column, so the legacy `line`/`column` path was effectively unobtainable — and
tried the tool's own documented `contextSnippet` fallback, which the implementation silently ignored
entirely (the parameters were accepted but never wired to any resolution logic). The agent hit a
generic "requires either (sessionId, projectName, docCommentId) or (line, column)" error and had to
fall back to `RemoveMember(skipPrecheck: true)` instead — a correct recovery, but one it shouldn't
have needed. Separately, `sessionId` — part of that same error message — was never actually the
blocking factor (an empty `sessionId` already passes the internal staleness check) and is not
obtainable from any tool's output, so it has been removed from this tool's exposed parameters
entirely. Added a `symbolName` parameter (paired with the existing `contextSnippet`/`lineBefore`/
`lineAfter`) that now does what the description always claimed. If `SafeDeleteUnusedSymbol` rejects a
`symbolName`+`contextSnippet` call that correctly names and disambiguates a real, zero-usage
declaration, that is a regression.

---

### Scenario 6 — Extract a method (Tier 2)
**Task:** "In `Order.BuildOrderSummary`, extract the block that computes running total and total
units into its own method called `ComputeTotals`."

**Expected tool sequence:**
1. `ExtractMethodSafe(filepath: ".../OrderProcessor.cs", newMethodName: "ComputeTotals", exactSourceBlock: <the block>, lineBefore/lineAfter as anchors)`
   — the parameter was renamed from `contextSnippet` to `exactSourceBlock` on 2026-08-19 (see
   "Fixed" list) specifically because it does NOT behave like `contextSnippet` on every other tool: it
   is not a search fragment, the whole extraction range must appear verbatim.

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
right. As of 2026-08-19, generated members from `ExtractMethodSafe` carry a leading
`// Added by ExtractMethodSafe` comment — use this to quickly locate the extracted method in a diff
without cross-referencing the tool-call log.

**Fixed regression — nullable parameter over-annotation:** a live agent run (attempt 3) selected a
block that also called a method on a variable declared earlier in the same method (an `sb` of type
`System.Text.StringBuilder`, used via `sb.AppendLine(...)` both before and inside the selection).
The tool generated a parameter typed `StringBuilder? sb` — nullable — even though `sb` is
unconditionally constructed and never reassigned, and the generated body dereferenced it with no
null check, producing a live `CS8602` warning that did not exist before extraction. Root cause:
Roslyn's flow-analysis-derived `NullableAnnotation` on the incoming symbol's type is conservative at
region boundaries and was being copied verbatim into the synthesized signature. Fixed by stripping a
flow-state-only nullable annotation before rendering the type string. If a similarly-shaped selection
(a block that uses a reference-typed local/parameter declared before it) still
produces a nullable-annotated parameter for an obviously-non-null variable, that is a regression —
verify with a real build (`dotnet build`), not just `GetDiagnostics`' summary count, since a single
new warning is easy to miss if only checking for `errors: 0`.

**Fixed regression — single-statement-before-a-loop ambiguity (attempt 4):** a live agent run passed
`contextSnippet: "decimal runningTotal = 0m;"` — matching only the accumulator's *initialization*
statement, not the `foreach` two statements later that actually accumulates into it. The tool
extracted just that one statement into `ComputeTotals`, which then unconditionally `return
runningTotal;` (always `0m`), silently stranding the `foreach`/`totalUnits` logic in the caller —
while still reporting success. This is the same ambiguity class as the inside-the-loop accumulator
case below, but the pre-existing guard for that case only checked whether the matched statement's
*enclosing block* was itself a loop body; it never fired here because the statement sits in the
*method* body, one level up from the loop. Generalized the guard to also scan forward within the
same block for a later loop that reads or mutates a variable the matched statement declares/assigns,
and refuse extraction in that case too. If a selection naming only an accumulator's initializer (with
its consuming loop appearing later in the same block, possibly with other statements in between)
still gets silently extracted alone, that is a regression.

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
can influence via its arguments — do not grade the agent down for the resulting placement. As of
2026-08-19, both the new backing field and (when synthesized) the new constructor carry a leading
`// Added by AddConstructorParameter` comment specifically so this placement is easy to spot in a
diff/review rather than needing to scroll to the bottom of the class to notice it landed there.

**Fixed regression — `fieldName` colliding with `paramName` (attempt 4):** a live agent run called
`AddConstructorParameter(..., paramName: "stopwatch", fieldName: "stopwatch")` — passing the same
name for both. The tool generated `private readonly Stopwatch stopwatch;` and, in the constructor,
the assignment statement `stopwatch = stopwatch;` — a no-op self-assignment of the parameter to
itself, since nothing distinguished the field from the parameter. The field was left permanently
uninitialized (default `null`/`0`), which `GetDiagnostics` correctly flagged (CS8618/CS1717/CS0169)
but the agent dismissed as expected/benign. Fixed: a caller-supplied `fieldName` equal to `paramName`
(with or without a leading underscore, e.g. `foo` or `_foo`) is now treated the same as omitting
`fieldName` — it resolves to the default `_camelCase(paramName)` derivation, which always differs
from `paramName`, so a self-assignment can no longer be generated. The tool's response description
now also reports both the resolved `paramName` and `fieldName` explicitly, so a caller (or grader)
never has to infer the actual field name from the diff. If a `fieldName`/`paramName` collision still
produces a self-assignment, that is a regression.

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

## Tool gaps observed (from live agent runs)

### Still open
- **`ReplaceMember` requires the full member body, and strengthening its description alone did not
  reliably fix the underlying reluctance.** Agents consistently hesitate to call it for what looks
  like a small, localized edit, because most models are trained heavily on diff/patch-style edits.
  The tool description was rewritten on 2026-08-18 to explicitly recommend `ReplaceMember` for small
  in-member edits and warn against a hand-built diff — but a live run afterward (attempt 3, Step 4)
  still went straight to `ApplyDiff`'s diff path (twice, both failing) before falling back to a
  whole-file `ApplyDiff(changesetFormat: files)` overwrite, never trying `ReplaceMember` at all. This
  suggests the friction may be a broader json-building weakness (composing a large multi-line string
  tool-call argument is harder for smaller models than composing a compact diff) rather than purely a
  tool-selection/description problem — worth a dedicated investigation rather than assuming either
  cause in isolation, and worth re-testing again now that `ApplyDiff` is also more line-tolerant (see
  "Fixed" below), since that may shift which path agents settle on even further away from
  `ReplaceMember`.
- **Fabricated `contextSnippet`/reconstructed source text on tool-call retries** is a recurring
  small-model behavior across multiple tools (`SafeDeleteUnusedSymbol`, `ExtractMethodSafe` in
  earlier attempts) — the agent reconstructs what it believes the target code looks like instead of
  copying the verbatim text it already received from an earlier `ReadFile`/`GetMethodSource` call.
  Snippet matching was hardened against indentation-only differences, but a snippet with genuinely
  different *content* (not just whitespace) will still and should still fail to match. This is a
  model-behavior risk to keep watching for across scenarios, not something a single tool fix
  resolves.
- **Duplicate, unreachable `SafeDeleteSymbolAsync` implementation on `RefactoringEngine`.** A second,
  more thorough implementation (includes reflection/string-literal usage detection that the actually-
  wired `StructuralRefinementEngine` path lacks) exists but is never called by the
  `SafeDeleteUnusedSymbol` MCP tool — dead code, only exercised by one test. See `docs/TODO.md` for
  the full writeup and remediation options; not fixed as part of the 2026-08-19
  `symbolName`/`contextSnippet` fallback work below, which stayed scoped to the reachable engine.
- **`ConvertExpressionBodyAsync` (and possibly other, not-yet-audited tools) has the same
  single-candidate `contextSnippet`-required bug class as `ReplaceMember` did, but with a different
  code shape** (branches to position-based resolution entirely when `contextSnippet` is supplied,
  rather than computing name-based candidates first and only consulting the snippet when there's
  real ambiguity) — not fixed as part of the `ReplaceMember` fix below, since it requires restructuring
  that method's resolution logic rather than the same one-line guard. See `docs/TODO.md`.

### Fixed
- **(2026-08-19) CRITICAL — `FindReferences`/`FindCallersAsync`/`FindImplementationsForMemberAsync`
  silently returned an empty (`Success: true, Data: []`) result, indistinguishable from "confirmed
  zero references," whenever resolution of the target itself failed.** Two distinct bugs, found
  together: (1) the MCP tool layer resolves an omitted `filepath` argument to `FilePath`'s
  empty-string default via `SetFilePath` (a `string`, not a C# `null`), but the engine methods
  checked `filePath != null` — always true for an empty string — so the by-`symbolName`
  whole-solution fallback was structurally unreachable through the actual tool surface; every
  no-`filepath` call silently took the file-scoped branch, found no document with an empty path, and
  returned `[]`. (2) Even when a real `filepath` was supplied, a `contextSnippet` that failed to
  resolve to a symbol (typo, stale text, wrong file) produced the same silent `[]` — indistinguishable
  from a genuine zero-callers/zero-implementations answer. This directly undermines
  `SafeDeleteUnusedSymbol`'s and `RemoveMember`'s "confirm zero usages, then delete" safety contract:
  an agent could delete an actively-used symbol believing `FindReferences` had confirmed it unused,
  when the lookup never actually ran. Fixed: both methods now treat a blank `filePath` the same as
  `null` (reaching the by-name fallback as intended), and throw a descriptive `InvalidOperationException`
  — explicitly stating "this is NOT a confirmed zero-references/implementations result" — at every
  resolution-failure point, instead of returning an empty list. A symbol that resolves successfully but
  genuinely has zero callers/implementations still correctly returns `[]` — only resolution failure now
  errors, not a legitimate empty answer. See Scenario 5.
- **(2026-08-19) `ContextHelper`'s single-line whitespace-tolerant fallback resolved to a line-START
  position, not the actual match position within the line.** Fine for member/type disambiguation
  (`ResolveMemberByNameOrSnippet`/`ResolveTypeByNameOrSnippet` only need the position to fall inside
  the right member's span), but wrong for `ExtractLocalVariableAsync`, which needs the position to
  land exactly at an `ExpressionSyntax`'s `SpanStart`. E.g. source `return a  +  b;` (extra spacing
  around `+`) matched against a caller's `contextSnippet: "a + b"` used to resolve to the position of
  `r` in `return`, not `a`, so the exact-match check never found a candidate and silently fell through
  to the ambiguous nearest-enclosing-expression guess. Fixed by mapping the match offset found within
  the whitespace-collapsed line back to the corresponding offset in the real, pre-collapse line text
  (walking raw-vs-normalized in lockstep, consuming an entire raw whitespace run per normalized
  space), so the resolved position now lands on the actual matched text, not just "somewhere in the
  right line." See Scenario 6.
- **(2026-08-19) `contextSnippet` wording and naming audit across all tools.** Surveyed every
  MCP tool accepting `contextSnippet` and classified each by verified engine behavior (not just its
  description) into two categories: `FRAGMENT_ANCHOR` (≈18 of 20 tools — a short, unique substring is
  sufficient and preferred; the code searches for it as a position anchor and walks outward to the
  enclosing member/type) vs. `EXACT_BLOCK` (`ExtractMethodSafe` fully, `ExtractLocalVariable` partially
  — the snippet's matched span directly becomes the operation's boundary, so it must verbatim-cover
  the whole intended range). The shared description text ("Verbatim substring... **Must match
  exactly**...") was ambiguous between these two meanings and, being reused by ~18 of 20 tools, trained
  agents toward shrinking `contextSnippet` to a unique fragment — the opposite of what `ExtractMethodSafe`
  and `ExtractLocalVariable` need. Fixed: rewrote the shared `ToolParams.ContextSnippet` text to
  explicitly state "short, unique fragment... do NOT paste the whole member" for the 18
  `FRAGMENT_ANCHOR` tools, and renamed the parameter itself on the 2 `EXACT_BLOCK` tools —
  `ExtractMethodSafe`'s to `exactSourceBlock`, `ExtractLocalVariable`'s to `exactExpressionText` — each
  with its own tool-specific description making clear it is NOT a search fragment. Also hardened
  `ExtractLocalVariableAsync`'s exact-match check to tolerate internal whitespace differences (not just
  raw-trim) so more real callers hit the precise exact-match path instead of the ambiguous
  nearest-enclosing-expression fallback. See Scenario 6.
- **(2026-08-19) `ReplaceMember` (and the shared member/type resolution helpers behind
  `RemoveMember`, `ChangeAccessibility`, `ModifyModifier`, `ModifyAttribute`, `ModifyBaseType`,
  `ModifyEnum`, `AddSummaryComment`, `SafeDeleteUnusedSymbol`, and others) requiring a supplied
  `contextSnippet` to match even when the name alone already resolved unambiguously.** See Scenario 4
  for the full writeup — fixed by skipping `contextSnippet` matching entirely when there are zero or
  one name-matched candidates, since a snippet only has a job when there's real ambiguity to resolve.
  Also fixed a related, independently-discovered bug in the whitespace-tolerant multi-line matching
  fallback: a `contextSnippet` ending in a trailing newline inflated the sliding window's size by one
  via a phantom empty line from `Split('\n')`, causing snippets that should have matched via that
  fallback to silently fail instead.
- **(2026-08-19) `LoadSolution` silently dropping a nonexistent `baseRepoDir` instead of failing
  fast.** A caller-supplied `baseRepoDir` that doesn't exist on the host used to be silently
  discarded, falling through to other resolution candidates (server-wide `--base-repo-dir` default,
  then the app base directory) with no signal that the supplied value was ignored — meaning a
  fabricated/guessed `baseRepoDir` combined with a relative `solutionPath` could silently resolve to
  an unintended sibling directory that happens to share the same relative path, rather than erroring.
  Now throws immediately, naming the specific nonexistent directory and steering the caller toward
  omitting `baseRepoDir` rather than guessing another value. See Scenario 0.
- **(2026-08-19) `ExtractMethodSafe` synthesizing a spuriously nullable parameter.** A selection
  that used a reference-typed local/parameter declared earlier in the same method (e.g. an
  unconditionally-constructed `StringBuilder`, never reassigned) got a generated parameter typed
  `Foo?` instead of `Foo`, because a flow-analysis-conservative `NullableAnnotation` was copied
  verbatim into the synthesized signature — producing a live `CS8602` warning that didn't exist
  before extraction. Fixed by stripping a flow-state-only nullable annotation before rendering the
  parameter/return type. See Scenario 6.
- **(2026-08-19) `ExtractMethodSafe` silently stranding a loop when the matched statement sits
  before it, not inside it.** A single-statement `contextSnippet` naming only an accumulator's
  initializer (e.g. `decimal runningTotal = 0m;`), with the loop that actually accumulates into it
  appearing later in the same block, was extracted alone — producing a method that always returns
  the initializer's value and silently stranding the loop in the caller, while still reporting
  success. The existing inside-the-loop accumulator guard didn't cover this shape because it only
  checked whether the matched statement's enclosing block was itself a loop body. Generalized to
  also scan forward for a later loop in the same block that consumes a variable the statement
  declares/assigns, and refuse in that case too. See Scenario 6.
- **(2026-08-19) `SafeDeleteUnusedSymbol`'s documented `contextSnippet` fallback didn't exist, and
  `sessionId` was a dead, unobtainable parameter.** The tool's own description promised a
  `contextSnippet`/`lineBefore`/`lineAfter` resolution path, but the implementation never wired those
  parameters to any lookup logic — only the handle-based (`docCommentId`+`projectName`) and legacy
  `line`/`column` paths actually worked, and no tool surfaces a column, only a line, making the latter
  effectively unusable for a caller who only has `SearchSolutionText`/`FindReferences` output.
  `sessionId` — named first in the resulting error message — was never actually the blocking factor
  (an empty `sessionId` already passes the internal staleness check) and has been removed from the
  tool's exposed parameters entirely. Added a `symbolName` parameter (paired with the existing
  `contextSnippet`/`lineBefore`/`lineAfter`) that now resolves a target by name, optionally
  disambiguated by snippet — reusing the same name-then-snippet pattern every other RoslynSentinel
  tool already follows. See Scenario 5.
- **(2026-08-19) `AddConstructorParameter` self-assignment when `fieldName` collides with
  `paramName`.** Passing `fieldName` equal to `paramName` (or to its underscore-prefixed form)
  produced a no-op self-assignment (`stopwatch = stopwatch;`) instead of assigning the parameter into
  a distinct field, leaving the field permanently uninitialized. A colliding `fieldName` is now
  treated the same as omitting it, falling back to the default `_camelCase(paramName)` derivation,
  which always differs from `paramName`. The tool's response now also states the resolved
  `paramName`/`fieldName` pair explicitly. See Scenario 7.
- **(2026-08-19) `ProposedChange` renamed to `ApplyDiff`, and its diff path made line-tolerant.**
  The tool no longer trusts a hunk's declared line number unconditionally — if the hunk's content
  isn't found there, it searches a window of nearby lines and re-anchors to the real match, so
  modest line-number drift from an earlier edit no longer produces a parse error. See Scenario 4.
- **(2026-08-19) "Added by \<Tool\>" comments** on newly-synthesized members from `ExtractMethodSafe`,
  `ExtractConstantSafe`, `Generate(kind: generate_to_string_safe)`, `AddMember`
  (`AddMemberTyped`/`InsertMemberAfter`/`InsertMemberBefore` included via delegation), and
  `AddConstructorParameter` — directly mitigates the constructor-placement surprise in Scenario 7 and
  should make it faster to attribute any newly-added member in a diff to the tool call that produced
  it during grading, without cross-referencing the tool-call log.
- **(2026-08-19) `RemoveMember(skipPrecheck: true)` fallback for `SafeDeleteUnusedSymbol` failures**
  — already reflected in Scenario 5.

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
