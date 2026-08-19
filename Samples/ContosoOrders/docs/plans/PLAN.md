# Plan — ContosoOrders Cleanup Pass

## Title
ContosoOrders: fix naming, accessibility, and dead-code issues found during code review

## Understanding
A code review of `Samples/ContosoOrders` flagged several small issues: a misspelled method name,
an overly restrictive access modifier, a missing enum value, an unnecessary fully-qualified type
reference, a dead private method, an overly long method, a missing constructor dependency, a
misnamed file, and a missing XML doc comment. All issues are isolated and low-risk; none require
new files or structural redesign.

## Assumptions
- The solution at `Samples/ContosoOrders/ContosoOrders.sln` is not yet loaded into the workspace.
- `ContosoOrders.Tests` references `ContosoOrders.Core` and must still compile after all changes.
- Renames must propagate to every reference, including test code.
- Changes should be validated (no new compiler errors) before being treated as done.
- **Every RoslynSentinel edit tool writes directly to disk and returns `"status": "applied"` /
  `"note": "Written to disk."` on success — there is no separate stage/apply lifecycle to manage.**
  Each `changeId` is a completed edit, not a pending one; `UndoLastApply(changeId: ...)` reverts it
  if needed. Do not expect or look for a bulk "apply all staged changes" step — one does not exist,
  and none is required.

## Approach
Work through the issues in dependency order: start with the rename (since other steps reference
the renamed member), then independent single-file fixes, then the riskier multi-file/DI change,
then structural (file rename) and documentation cleanup last. Since every edit writes to disk
immediately, there is no staging/apply phase to sequence — validate as you go (or in one pass at
the end) and give a final health check.

## Key Files
- `Samples/ContosoOrders/ContosoOrders.Core/OrderProcessor.cs` — contains the `Order` class; most
  edits land here (rename target, accessibility, using directive, dead code, extract method).
- `Samples/ContosoOrders/ContosoOrders.Core/OrderStatus.cs` — enum needing a new value.
- `Samples/ContosoOrders/ContosoOrders.Core/OrderService.cs` — needs a new constructor dependency
  (`System.Diagnostics.Stopwatch`).
- `Samples/ContosoOrders/ContosoOrders.Tests/OrderProcessorTests.cs` — consumes the renamed member;
  do not modify the test method's own name, only the call inside it.

## Risks & Open Questions
- Renaming `CalcuateTotal` could be mistaken for also needing to rename the test method
  `CalcuateTotal_SumsLineTotals` — it should not be renamed, only the call site inside it.
- Adding the `System.Diagnostics.Stopwatch` constructor parameter introduces a dependency on the
  `System.Diagnostics` namespace, which is not currently referenced in `OrderService.cs`'s using
  list — verify no new compiler errors after this step. Unlike a package-backed type (e.g.
  `ILogger<T>`), `Stopwatch` is a BCL type with no NuGet package to add, so this step only exercises
  constructor-parameter addition and using-directive addition — not project-reference management.
- The accessibility change and the rename both touch `OrderProcessor.cs`. Since each tool call
  writes to disk and re-reads the current document on its next call, this is no longer a staging
  conflict — just confirm the second edit's tool call resolves its target against the file's
  post-first-edit state (e.g. re-locate the symbol/snippet after the rename lands) rather than
  assuming stale line numbers or pre-rename text still apply.

## Steps
1. Rename the misspelled method `Order.CalcuateTotal` to `Order.CalculateTotal` across the whole
   solution.
2. Change the accessibility of `Order.ApplyDiscount` from `private` to `public`.
3. Add a `Delivered` value to the `OrderStatus` enum, after `Shipped`.
4. Add the missing `using ContosoOrders.Core.Discounts;` directive to `OrderProcessor.cs` and
   simplify the fully-qualified `DiscountCalculator` call site to use it.
5. Confirm `Order.BuildInternalDebugLabel` has zero usages in the solution, then remove it.
6. Extract the running-total/unit-count computation block inside `Order.BuildOrderSummary` into a
   new method named `ComputeTotals`.
7. Add a `System.Diagnostics.Stopwatch` constructor parameter named `stopwatch` to `OrderService`,
   with a backing field, and add the missing `using System.Diagnostics;` directive this introduces.
8. Rename the file containing the `Order` class so its filename matches the class name.
9. Add an XML `<summary>` doc comment to `OrderService.CreateOrder` describing what it does.
10. Validate the solution has no new compiler errors and confirm the workspace is healthy. (No
	separate "apply" action is needed — every prior step already wrote its change to disk; this
	step is verification only.)
