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

## Approach
Work through the issues in dependency order: start with the rename (since other steps reference
the renamed member), then independent single-file fixes, then the riskier multi-file/DI change,
then structural (file rename) and documentation cleanup last. After all edits are staged, validate
the whole solution compiles cleanly before applying, then apply and give a final health check.

## Key Files
- `Samples/ContosoOrders/ContosoOrders.Core/OrderProcessor.cs` — contains the `Order` class; most
  edits land here (rename target, accessibility, using directive, dead code, extract method).
- `Samples/ContosoOrders/ContosoOrders.Core/OrderStatus.cs` — enum needing a new value.
- `Samples/ContosoOrders/ContosoOrders.Core/OrderService.cs` — needs a new constructor dependency.
- `Samples/ContosoOrders/ContosoOrders.Tests/OrderProcessorTests.cs` — consumes the renamed member;
  do not modify the test method's own name, only the call inside it.

## Risks & Open Questions
- Renaming `CalcuateTotal` could be mistaken for also needing to rename the test method
  `CalcuateTotal_SumsLineTotals` — it should not be renamed, only the call site inside it.
- Adding the `ILogger<OrderService>` constructor parameter introduces a dependency on
  `Microsoft.Extensions.Logging`, which is not currently referenced in `OrderProcessor.cs`'s using
  list — verify no new compiler errors after this step.
- The accessibility change and the rename both touch `OrderProcessor.cs`; if using a staged-change
  workflow, confirm both edits land together rather than one silently overwriting the other.

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
7. Add an `ILogger<OrderService>` constructor parameter named `logger` to `OrderService`, with a
   backing field, and add any missing using directive this introduces.
8. Rename the file containing the `Order` class so its filename matches the class name.
9. Add an XML `<summary>` doc comment to `OrderService.CreateOrder` describing what it does.
10. Validate the solution has no new compiler errors, then apply all staged changes and confirm
	the workspace is healthy.
