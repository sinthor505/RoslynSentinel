# RoslynSentinel MCP Tool-Use Scenarios — Batch 1 (ContosoOrders sample)

## Purpose
This is a small, self-contained sample solution used to evaluate whether an agent (particularly a
7B–8B local model) can correctly select and drive RoslynSentinel MCP tools to accomplish realistic
refactoring tasks. It is intentionally simple: two projects, a handful of files, and a set of
deliberately planted issues, each mapped to one or more MCP tools exposed by
`RoslynSentinel.Server.Basic`.

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
| 3 | Missing `Delivered` enum value | `OrderStatus.cs` | `AddEnumValue` |
| 4 | Fully-qualified `ContosoOrders.Core.Discounts.DiscountCalculator` used instead of a `using` directive | `OrderProcessor.cs` | `AddUsingDirective` |
| 5 | Unused private method `BuildInternalDebugLabel` | `OrderProcessor.cs` | `FindReferences`/`FindUsages` (to confirm zero call sites) → `SafeDeleteUnusedSymbol` |
| 6 | Inline totals-computation block inside `BuildOrderSummary` should become its own method | `OrderProcessor.cs` | `ExtractMethodSafe` |
| 7 | `OrderService` needs a new `ILogger<OrderService>` constructor dependency | `OrderService.cs` | `AddConstructorParameter` |
| 8 | Class `Order` lives in a file named `OrderProcessor.cs` | `OrderProcessor.cs` | `SyncTypeAndFilename` |
| 9 | Missing XML doc summary on public `OrderService.CreateOrder` | `OrderService.cs` | `AddSummaryComment` |
|10 | Verify solution compiles / no orphaned diagnostics after all changes | whole solution | `GetDiagnostics`, `Diagnose` or `GetWorkspaceHealth` |

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
2. `GetWorkspaceHealth()` (preferred) or `Diagnose()`

**Expected outcome:** Success=true, 2 projects loaded, 0 load errors.

**Grading notes:** Confirms the agent knows to load before doing anything else, and prefers
`GetWorkspaceHealth` over the documented-buggy `Diagnose` when both are available.

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
inside it). `changeId` returned; change is staged, not yet applied.

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
1. `AddEnumValue(filepath: ".../OrderStatus.cs", enumName: "OrderStatus", valueName: "Delivered")`

**Expected outcome:** `OrderStatus` enum gains `Delivered = 3` (or next implicit value) after
`Shipped`.

**Grading notes:** Verify the agent does not attempt a manual `ReplaceMember`/full-file rewrite when
a purpose-built tool exists.

---

### Scenario 4 — Add missing using directive (Tier 1)
**Task:** "`OrderProcessor.cs` fully-qualifies `ContosoOrders.Core.Discounts.DiscountCalculator`
every time it's used. Add the proper using directive and simplify the call site."

**Expected tool sequence:**
1. `AddUsingDirective(filepath: ".../OrderProcessor.cs", namespaceName: "ContosoOrders.Core.Discounts")`
2. *(Note: `AddUsingDirective` only adds the directive; simplifying the fully-qualified call site is
   a separate edit.)* A capable agent should follow up with `ReplaceMember` (or equivalent) to
   shorten `ContosoOrders.Core.Discounts.DiscountCalculator.ApplyPercentage(...)` to
   `DiscountCalculator.ApplyPercentage(...)`, OR explicitly tell the user that only the using
   directive was added and the call site still compiles as-is.

**Expected outcome:** `using ContosoOrders.Core.Discounts;` added to top of file.

**Grading notes:** This scenario deliberately tests whether the agent over-claims completion ("done!")
when only half the described task was actually accomplished by the tool it called — a common small-model
failure mode.

---

### Scenario 5 — Safe-delete dead code (Tier 2, multi-step verification)
**Task:** "Is `Order.BuildInternalDebugLabel` used anywhere? If not, remove it."

**Expected tool sequence:**
1. `LocateSymbol(symbolName: "BuildInternalDebugLabel")` or `FindReferences`/`FindUsages` to confirm
   zero call sites.
2. `SafeDeleteUnusedSymbol(filepath: ".../OrderProcessor.cs", line: <decl line>, column: <decl column>)`

**Expected outcome:** Success with a report confirming zero usages found and the method removed;
if the agent skips step 1 and calls `SafeDeleteUnusedSymbol` directly, that's acceptable too since
the tool itself enforces the zero-usage guarantee — but it should still report the "why" to the user.

**Grading notes:** Tests whether the agent trusts the tool's built-in safety check vs. redundantly
verifying first (both are correct; only skipping the removal or deleting a *used* method is wrong).

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
`Delivered` enum value to `OrderStatus`. Then verify the solution still compiles, and apply all the
changes."

**Expected tool sequence:**
1. `LocateSymbol` + `RenameSymbol` (stages change A)
2. `ChangeAccessibility` (stages change B)
3. `AddEnumValue` (stages change C)
4. `GetDiagnostics` or `StagedChange(action: validate, changeId: ...)` per staged change
5. `StagedChange(action: apply, changeId: ...)` for each, in any order that doesn't conflict
   (A and B touch the same file — the agent should notice this and either apply sequentially or
   re-stage after the first apply if the tool requires non-overlapping change sets)

**Expected outcome:** All three edits present in `OrderProcessor.cs`/`OrderStatus.cs` on disk after
apply; `GetDiagnostics` reports 0 errors.

**Grading notes:** This is the hardest scenario — it tests whether the agent understands the
propose/stage/validate/apply lifecycle and handles the same-file conflict between changes A and B
correctly rather than silently losing one edit. This is where 7B–8B models are most likely to fail;
expect this scenario to have the lowest pass rate and use it to differentiate model capability, not
as a baseline expectation.

---

## Grading methodology
- **Automated**: after each scenario, diff the actual file(s) against a hand-authored "golden"
  version. A pass requires the code to compile and match the golden semantics (not necessarily
  byte-for-byte, since summary text/variable naming may vary in scenarios 6 and 9).
- **Semi-automated**: log the exact tool name + arguments the agent called for each scenario and
  compare against the "Expected tool sequence" column. Flag any call to a plausible-but-wrong tool
  (e.g., `ModifyModifier` instead of `ChangeAccessibility`, `ReplaceMember` instead of
  `ExtractMethodSafe`) as a tool-selection failure even if the end result happens to compile.
- **Manual**: scenario 10's conflict-handling behavior, and scenario 9's doc-comment wording quality.

## Suggested next batches
- Batch 2: `Git` tool scenarios (stage/commit a staged change), `GenerateMapping` between two DTOs,
  `ModifyBaseType`/`ModifyModifier`/`AddMemberTyped` disambiguation drills.
- Batch 3: adversarial/error-recovery — intentionally wrong symbol names, stale `changeId`s,
  `RetryFailedChanges`, `UndoLastApply` after a bad apply.
- Batch 4: `SplitProjectByFolder` and `ProjectConsistencyEngine`-backed tools on a slightly larger
  multi-project sample.
