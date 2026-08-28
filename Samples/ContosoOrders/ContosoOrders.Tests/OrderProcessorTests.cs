using ContosoOrders.Core;

using Xunit;

namespace ContosoOrders.Tests;

public class OrderProcessorTests
{
    [Fact]
    public void CalcuateTotal_SumsLineTotals()
    {
        var order = new Order("cust-1", new List<OrderLine>
        {
            new("SKU-1", 2, 10.00m),
            new("SKU-2", 1, 5.00m),
        });

        Assert.Equal(25.00m, order.CalcuateTotal());
    }

    [Fact]
    public void MarkShipped_SetsStatusToShipped()
    {
        var order = new Order("cust-2", new List<OrderLine> { new("SKU-3", 1, 1.00m) });

        order.MarkShipped();

        Assert.Equal(OrderStatus.Shipped, order.Status);
    }
}
