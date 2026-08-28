namespace ContosoOrders.Core;

/// <summary>
/// Coordinates order lifecycle operations. Target for AddConstructorParameter scenario
/// (add an ILogger dependency).
/// </summary>
public class OrderService
{
    private readonly List<Order> _orders = new();

    public Order CreateOrder(string customerId, List<OrderLine> lines)
    {
        var order = new Order(customerId, lines);
        _orders.Add(order);
        return order;
    }

    public decimal GetOrderTotal(Order order)
    {
        return order.CalcuateTotal();
    }

    public void ShipOrder(Order order)
    {
        order.MarkShipped();
    }
}
