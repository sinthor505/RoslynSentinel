namespace ContosoOrders.Core;

public enum OrderStatus
{
    Pending = 0,
    Shipped = 1,
    Cancelled = 2
    // Missing: Delivered = 3 — scenario will add this.
}
