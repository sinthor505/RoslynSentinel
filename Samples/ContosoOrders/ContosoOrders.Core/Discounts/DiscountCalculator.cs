namespace ContosoOrders.Core.Discounts;

public static class DiscountCalculator
{
    public static decimal ApplyPercentage(decimal amount, decimal percentage)
    {
        return amount - amount * percentage / 100m;
    }
}
