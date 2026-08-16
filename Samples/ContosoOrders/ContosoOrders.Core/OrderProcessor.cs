namespace ContosoOrders.Core;

/// <summary>
/// Represents a customer order and its processing logic.
/// NOTE: file name intentionally mismatched with class name (see SyncTypeAndFilename scenario).
/// </summary>
public class Order
{
    private readonly string _customerId;
    private readonly List<OrderLine> _lines;

    public Order(string customerId, List<OrderLine> lines)
    {
        _customerId = customerId;
        _lines = lines;
        Status = OrderStatus.Pending;
    }

    public OrderStatus Status { get; private set; }

    public string CustomerId => _customerId;

    public IReadOnlyList<OrderLine> Lines => _lines;

    // Intentional typo target for RenameSymbol scenario ("CalcuateTotal" -> "CalculateTotal").
    public decimal CalcuateTotal()
    {
        decimal subtotal = 0m;
        foreach (var line in _lines)
        {
            subtotal += line.LineTotal;
        }

        return subtotal;
    }

    // Intentionally too-restrictive accessibility (should be public) for ChangeAccessibility scenario.
    private decimal ApplyDiscount(decimal percentage)
    {
        // NOTE: this method uses DiscountCalculator, but the using directive for
        // ContosoOrders.Core.Discounts is intentionally missing from this file (fully qualified below
        // as a workaround) to create a scenario for AddUsingDirective.
        return ContosoOrders.Core.Discounts.DiscountCalculator.ApplyPercentage(CalcuateTotal(), percentage);
    }

    public void MarkShipped()
    {
        Status = OrderStatus.Shipped;
    }

    // Unused private method: nothing in the solution calls this. Target for SafeDeleteUnusedSymbol.
    private string BuildInternalDebugLabel()
    {
        return $"[{_customerId}] {_lines.Count} line(s)";
    }

    // Long method with an inline block that is a good ExtractMethodSafe / ExtractLocalVariable target.
    public string BuildOrderSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Order for {_customerId}");
        sb.AppendLine($"Status: {Status}");

        // --- extract-target block start ---
        decimal runningTotal = 0m;
        int totalUnits = 0;
        foreach (var line in _lines)
        {
            runningTotal += line.Quantity * line.UnitPrice;
            totalUnits += line.Quantity;
        }
        sb.AppendLine($"Total units: {totalUnits}");
        sb.AppendLine($"Running total: {runningTotal:C}");
        // --- extract-target block end ---

        return sb.ToString();
    }
}
