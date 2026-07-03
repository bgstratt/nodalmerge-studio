using MiniLedger.Contracts.Customers;
using MiniLedger.Contracts.Discounts;
using MiniLedger.Core.Discounts;
using Xunit;

namespace MiniLedger.Core.Tests;

public sealed class DiscountStackingFixTests
{
    [Fact]
    public void MultipleApplicableRules_TakesBestSingleDiscount_NotStacked()
    {
        var service = new DiscountService();
        service.RegisterRule(new DiscountRule("SAVE20", 20m));
        service.RegisterRule(new DiscountRule("VIP30", 30m));

        var result = service.ApplyDiscounts(100m, ["SAVE20", "VIP30"], CustomerTier.Standard);

        // Best single discount (30%) wins — not 20% + 30% = 50% stacked.
        Assert.Equal(70m, result);
    }
}
